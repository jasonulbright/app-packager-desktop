using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services.ConfigMgrSession;

/// <summary>
/// The session host. Protocol: each request is one JSON line on standard input with an
/// "id" and an "op" ("connect" or "run"); each response is one JSON line on standard
/// output with the same "id" and a "t" of "out", "warn" or "error", closed by one "end"
/// line. Lines without the running command's id (host text, stray output) are ignored.
///
/// One worker takes commands from a FIFO queue. A command whose token is cancelled while
/// it waits is dropped without touching the process; a running command that is cancelled
/// or overruns its timeout ends the process, because PowerShell offers no way to stop a
/// pipeline from outside. The next command starts and connects a new process, as it does
/// after the process dies or the site or provider changes.
/// </summary>
public sealed partial class ConfigMgrSessionHost : IConfigMgrSession, IDisposable
{
    public const string LogArea = "ConfigMgr";
    internal const string SettingsLocation = "Settings, ConfigMgr Site Settings";
    public const int MaxRequestBytes = 1024 * 1024;
    public const string UnexpectedMessage = "The ConfigMgr session failed unexpectedly. The log has the details.";

    private sealed class Work(
        ConfigMgrScript script,
        ConfigMgrParameters parameters,
        TimeSpan timeout,
        CancellationToken token)
    {
        public ConfigMgrScript Script { get; } = script;
        public ConfigMgrParameters Parameters { get; } = parameters;
        public TimeSpan Timeout { get; } = timeout;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<ConfigMgrCommandResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Started;
    }

    private readonly Func<ConfigMgrConnection> _connection;
    private readonly IConfigMgrSessionProcessFactory _factory;
    private readonly IAppLog? _log;
    private readonly ConfigMgrSessionOptions _options;
    private readonly Func<string?> _modulePath;
    private readonly Channel<Work> _queue = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _disposing = new();
    private readonly Task _worker;
    private IConfigMgrSessionProcess? _process;
    private ConfigMgrConnection? _connected;
    private int _disposed;

    public ConfigMgrSessionHost(Func<ConfigMgrConnection> connection, IAppLog? log = null, ConfigMgrSessionOptions? options = null)
        : this(connection, new PowerShellSessionProcessFactory(), log, options ?? new ConfigMgrSessionOptions(), () => ConfigMgrConsole.ModulePath())
    {
    }

    internal ConfigMgrSessionHost(
        Func<ConfigMgrConnection> connection,
        IConfigMgrSessionProcessFactory factory,
        IAppLog? log,
        ConfigMgrSessionOptions options,
        Func<string?> modulePath)
    {
        _connection = connection;
        _factory = factory;
        _log = log;
        _options = options;
        _modulePath = modulePath;
        _worker = Task.Run(RunWorkerAsync);
    }

    [GeneratedRegex("^[A-Za-z0-9]{3}$")]
    private static partial Regex SiteCodeRegex();

    public Task<ConfigMgrCommandResult> InvokeAsync(
        ConfigMgrScript script, ConfigMgrParameters? parameters = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (ct.IsCancellationRequested) return Task.FromCanceled<ConfigMgrCommandResult>(ct);

        var work = new Work(script, parameters ?? new ConfigMgrParameters(), timeout ?? _options.CommandTimeout, ct);
        if (!_queue.Writer.TryWrite(work)) throw new ObjectDisposedException(nameof(ConfigMgrSessionHost));
        if (ct.CanBeCanceled)
        {
            // A queued command leaves at once; a running one completes only after the
            // worker has ended the process, so the caller never races the restart.
            var registration = ct.Register(() =>
            {
                if (Volatile.Read(ref work.Started) == 0) work.Completion.TrySetCanceled(ct);
            });
            work.Completion.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        }
        return work.Completion.Task;
    }

    // A service container disposes synchronously and throws for a service that is only
    // IAsyncDisposable. Every await below runs without the caller's context, so the
    // blocking wait cannot deadlock a UI thread.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        _disposing.Cancel();
        try { await _worker.ConfigureAwait(false); }
        catch (Exception) { }
        while (_queue.Reader.TryRead(out var left))
            left.Completion.TrySetException(new ObjectDisposedException(nameof(ConfigMgrSessionHost)));
        await EndProcessAsync("the session was closed").ConfigureAwait(false);
        _disposing.Dispose();
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(_disposing.Token).ConfigureAwait(false))
            {
                Volatile.Write(ref work.Started, 1);
                if (work.Completion.Task.IsCompleted) continue;
                try
                {
                    var result = await RunAsync(work).ConfigureAwait(false);
                    work.Completion.TrySetResult(result);
                }
                catch (OperationCanceledException) when (work.Token.IsCancellationRequested)
                {
                    work.Completion.TrySetCanceled(work.Token);
                }
                catch (OperationCanceledException) when (_disposing.IsCancellationRequested)
                {
                    work.Completion.TrySetException(new ObjectDisposedException(nameof(ConfigMgrSessionHost)));
                }
                catch (ConfigMgrSessionException ex)
                {
                    work.Completion.TrySetException(ex);
                }
                catch (Exception ex)
                {
                    _log?.Error(LogArea, $"{work.Script.Name} failed unexpectedly: {SecretMask.Apply(ex.ToString(), work.Parameters.SecretValues)}");
                    work.Completion.TrySetException(new ConfigMgrSessionException(ConfigMgrFailure.Unexpected, UnexpectedMessage));
                }
            }
        }
        catch (OperationCanceledException) when (_disposing.IsCancellationRequested) { }
    }

    private async Task<ConfigMgrCommandResult> RunAsync(Work work)
    {
        work.Token.ThrowIfCancellationRequested();
        var connection = _connection();
        if (!SiteCodeRegex().IsMatch(connection.NormalizedSiteCode))
        {
            var shown = connection.NormalizedSiteCode.Length == 0 ? "is empty" : $"{connection.NormalizedSiteCode} is not three letters or digits";
            throw Fail(ConfigMgrFailure.InvalidSiteCode, $"The site code {shown}. Correct it in {SettingsLocation}.");
        }

        var id = Guid.NewGuid().ToString("N");
        var requestLine = new JsonObject
        {
            ["id"] = id,
            ["op"] = "run",
            ["name"] = work.Script.Name,
            ["script"] = work.Script.Text,
            ["parameters"] = ParametersNode(work.Parameters)
        }.ToJsonString();
        // The child reads a whole request line before it can answer; a line that is too
        // large to read or parse would leave the command waiting for its timeout.
        if (Encoding.UTF8.GetByteCount(requestLine) > MaxRequestBytes)
            throw Fail(ConfigMgrFailure.RequestTooLarge,
                $"The ConfigMgr command {work.Script.Name} was not sent: its script and values are larger than the 1 MB limit for one command. Send the values in smaller parts.");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(work.Token, _disposing.Token);
        var process = await EnsureConnectedAsync(connection, stop.Token).ConfigureAwait(false);
        _log?.Info(LogArea, $"Run {work.Script.Name}" + (work.Parameters.Count > 0 ? $" ({work.Parameters.ToLogText()})" : ""));

        var secrets = work.Parameters.SecretValues.ToList();
        Exchange exchange;
        try
        {
            exchange = await ExchangeAsync(process, id, requestLine, work.Timeout, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await EndProcessAsync(work.Token.IsCancellationRequested ? $"{work.Script.Name} was cancelled" : "the session was closed").ConfigureAwait(false);
            throw;
        }

        switch (exchange.Status)
        {
            case ExchangeStatus.TimedOut:
                await EndProcessAsync($"{work.Script.Name} timed out").ConfigureAwait(false);
                throw Fail(ConfigMgrFailure.Timeout,
                    $"The ConfigMgr command {work.Script.Name} did not finish within {Seconds(work.Timeout)} seconds. The session was restarted.");
            case ExchangeStatus.Ended:
                await EndProcessAsync($"the process ended during {work.Script.Name}").ConfigureAwait(false);
                throw Fail(ConfigMgrFailure.ProcessDied,
                    $"The Windows PowerShell session for ConfigMgr ended while the command {work.Script.Name} ran. The next command starts a new session.");
        }

        if (exchange.Error is { } error)
        {
            if (error.Kind == "access") throw Fail(ConfigMgrFailure.AccessDenied, AccessDeniedMessage(connection));
            var detail = SecretMask.Apply(error.Message, secrets).Trim();
            throw Fail(ConfigMgrFailure.ScriptFailed,
                $"The ConfigMgr command {work.Script.Name} failed" + (detail.Length > 0 ? ": " + detail : "."));
        }

        _log?.Debug(LogArea, $"{work.Script.Name} returned {exchange.Output.Count} objects.");
        var warnings = exchange.Warnings.Select(w => SecretMask.Apply(w, secrets)).ToList();
        return new ConfigMgrCommandResult(exchange.Output, warnings);
    }

    private async Task<IConfigMgrSessionProcess> EnsureConnectedAsync(ConfigMgrConnection connection, CancellationToken ct)
    {
        if (_process is not null)
        {
            if (_process.HasExited) await EndProcessAsync("the process had ended").ConfigureAwait(false);
            else if (_connected?.Key != connection.Key) await EndProcessAsync("the site or provider changed").ConfigureAwait(false);
            else return _process;
        }

        try
        {
            _process = _factory.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw Fail(ConfigMgrFailure.PowerShellNotStarted, "Windows PowerShell 5.1 (powershell.exe) could not start. ConfigMgr commands need it.");
        }

        var provider = connection.NormalizedProvider;
        _log?.Info(LogArea, $"Connecting to site {connection.NormalizedSiteCode} on {ProviderText(connection)}.");
        var id = Guid.NewGuid().ToString("N");
        var request = new JsonObject
        {
            ["id"] = id,
            ["op"] = "connect",
            ["siteCode"] = connection.NormalizedSiteCode,
            ["provider"] = provider,
            ["modulePath"] = _modulePath() ?? ""
        };

        Exchange exchange;
        try
        {
            exchange = await ExchangeAsync(_process, id, request.ToJsonString(), _options.ConnectTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await EndProcessAsync("the connect was cancelled").ConfigureAwait(false);
            throw;
        }

        ConfigMgrSessionException? failure = exchange.Status switch
        {
            ExchangeStatus.TimedOut => Fail(ConfigMgrFailure.Timeout,
                $"The session did not connect to site {connection.NormalizedSiteCode} on {ProviderText(connection)} within {Seconds(_options.ConnectTimeout)} seconds."),
            ExchangeStatus.Ended => Fail(ConfigMgrFailure.ProcessDied,
                "The Windows PowerShell session for ConfigMgr ended while it connected. The next command starts a new session."),
            _ => exchange.Error?.Kind switch
            {
                null => null,
                "module" => Fail(ConfigMgrFailure.ModuleNotFound,
                    "The ConfigurationManager PowerShell module was not found. Install the Configuration Manager console on this computer."),
                "access" => Fail(ConfigMgrFailure.AccessDenied, AccessDeniedMessage(connection)),
                _ => Fail(ConfigMgrFailure.DriveNotConnected,
                    $"The CMSite drive for site {connection.NormalizedSiteCode} on {ProviderText(connection)} could not connect. Check the site code and the SMS Provider in {SettingsLocation}.")
            }
        };
        if (failure is not null)
        {
            await EndProcessAsync("the connect failed").ConfigureAwait(false);
            throw failure;
        }

        _connected = connection;
        _log?.Info(LogArea, $"Connected to site {connection.NormalizedSiteCode} on {ProviderText(connection)}.");
        return _process;
    }

    private enum ExchangeStatus { Completed, TimedOut, Ended }

    private sealed record ErrorLine(string Kind, string Message);

    private sealed record Exchange(ExchangeStatus Status, List<JsonElement> Output, List<string> Warnings, ErrorLine? Error);

    /// <summary>Writes one request and reads response lines until its end line.</summary>
    private static async Task<Exchange> ExchangeAsync(
        IConfigMgrSessionProcess process, string id, string requestLine, TimeSpan timeout, CancellationToken ct)
    {
        var output = new List<JsonElement>();
        var warnings = new List<string>();
        ErrorLine? error = null;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            try
            {
                await process.WriteLineAsync(requestLine).WaitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return new Exchange(ExchangeStatus.Ended, output, warnings, null);
            }

            while (true)
            {
                // The read is abandoned on cancellation; ending the process completes it.
                var line = await process.ReadLineAsync().WaitAsync(limit.Token).ConfigureAwait(false);
                if (line is null) return new Exchange(ExchangeStatus.Ended, output, warnings, error);
                if (!TryParse(line, id, out var message)) continue;
                switch (Text(message, "t"))
                {
                    case "out":
                        output.Add(message.TryGetProperty("v", out var value) ? value.Clone() : default);
                        break;
                    case "warn":
                        warnings.Add(Text(message, "m"));
                        break;
                    case "error":
                        error ??= new ErrorLine(Text(message, "kind"), Text(message, "m"));
                        break;
                    case "end":
                        return new Exchange(ExchangeStatus.Completed, output, warnings, error);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Exchange(ExchangeStatus.TimedOut, output, warnings, error);
        }
    }

    private static bool TryParse(string line, string id, out JsonElement message)
    {
        message = default;
        // Host text written without a line end (Write-Host -NoNewline, [Console]::Write)
        // can prefix a response line.
        var start = line.IndexOf("{\"id\":\"" + id + "\"", StringComparison.Ordinal);
        if (start < 0) return false;
        var text = line[start..].Trim();
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "id") != id) return false;
            message = root.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static JsonObject ParametersNode(ConfigMgrParameters parameters)
    {
        var node = new JsonObject();
        foreach (var (name, value) in parameters)
            node[name] = JsonSerializer.SerializeToNode(value);
        return node;
    }

    private async Task EndProcessAsync(string reason)
    {
        var process = _process;
        _process = null;
        _connected = null;
        if (process is null) return;
        _log?.Info(LogArea, $"Session ended: {reason}.");
        try { process.Kill(); } catch (Exception) { }
        try { await process.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
    }

    private ConfigMgrSessionException Fail(ConfigMgrFailure failure, string message)
    {
        _log?.Error(LogArea, message);
        return new ConfigMgrSessionException(failure, message);
    }

    private static string ProviderText(ConfigMgrConnection connection) =>
        connection.NormalizedProvider.Length == 0 ? "this computer" : connection.NormalizedProvider;

    private static string AccessDeniedMessage(ConfigMgrConnection connection) =>
        connection.NormalizedProvider.Length == 0
            ? $"The SMS Provider on this computer denied access. The account needs a ConfigMgr security role for site {connection.NormalizedSiteCode}."
            : $"The SMS Provider {connection.NormalizedProvider} denied access. The account needs a ConfigMgr security role for site {connection.NormalizedSiteCode}.";

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
}
