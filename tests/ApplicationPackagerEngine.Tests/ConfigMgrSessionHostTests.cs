using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Channels;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Tests;

public sealed class ConfigMgrSessionHostTests
{
    /// <summary>A stand-in for the PowerShell child: it answers each request line through a handler.</summary>
    private sealed class FakeProcess(FakeFactory factory, int number) : IConfigMgrSessionProcess
    {
        private readonly Channel<string?> _out = Channel.CreateUnbounded<string?>();
        public int Number { get; } = number;
        public List<JsonElement> Requests { get; } = [];
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public bool HasExited { get; private set; }

        public Task WriteLineAsync(string line)
        {
            if (HasExited) throw new IOException("The pipe has been ended.");
            var request = JsonDocument.Parse(line).RootElement.Clone();
            lock (Requests) Requests.Add(request);
            factory.Handler(this, request);
            return Task.CompletedTask;
        }

        public async Task<string?> ReadLineAsync()
        {
            try { return await _out.Reader.ReadAsync(); }
            catch (ChannelClosedException) { return null; }
        }

        public void Emit(string line) => _out.Writer.TryWrite(line);

        public void Reply(JsonElement request, params string[] bodies)
        {
            var id = request.GetProperty("id").GetString();
            foreach (var body in bodies) Emit("{\"id\":\"" + id + "\"," + body + "}");
            Emit("{\"id\":\"" + id + "\",\"t\":\"end\"}");
        }

        public void Die()
        {
            HasExited = true;
            _out.Writer.TryComplete();
        }

        public void Kill()
        {
            Killed = true;
            Die();
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            Die();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeFactory : IConfigMgrSessionProcessFactory
    {
        public List<FakeProcess> Started { get; } = [];
        public Action<FakeProcess, JsonElement> Handler { get; set; }
        public Exception? StartFailure { get; set; }

        public FakeFactory() => Handler = Default;

        public static void Default(FakeProcess process, JsonElement request)
        {
            switch (request.GetProperty("op").GetString())
            {
                case "connect":
                    process.Reply(request);
                    break;
                case "run":
                    process.Reply(request, "\"t\":\"out\",\"v\":\"ok\"");
                    break;
            }
        }

        public IConfigMgrSessionProcess Start()
        {
            if (StartFailure is not null) throw StartFailure;
            var process = new FakeProcess(this, Started.Count + 1);
            lock (Started) Started.Add(process);
            return process;
        }
    }

    private sealed class RecordingLog : IAppLog
    {
        public ConcurrentQueue<AppLogEntry> Entries { get; } = new();
        private readonly List<string> _secrets = [];
        public string? SessionFile => null;
        public AppLogLevel MinimumLevel { get; set; } = AppLogLevel.Debug;
        public event EventHandler<AppLogEntry>? Written;
        public void RegisterSecret(string value) { lock (_secrets) _secrets.Add(value); }
        public void Write(AppLogLevel level, string area, string message)
        {
            lock (_secrets) foreach (var s in _secrets) message = message.Replace(s, SecretMask.Mask, StringComparison.Ordinal);
            var entry = new AppLogEntry(DateTimeOffset.UtcNow, level, area, message);
            Entries.Enqueue(entry);
            Written?.Invoke(this, entry);
        }
        public IReadOnlyList<AppLogEntry> Tail() => Entries.ToList();
    }

    private static readonly ConfigMgrScript Script = ConfigMgrScript.FromText("Test", "param($Name) $Name");

    private static (ConfigMgrSessionHost Host, FakeFactory Factory) NewHost(
        Func<ConfigMgrConnection>? connection = null, ConfigMgrSessionOptions? options = null, IAppLog? log = null)
    {
        var factory = new FakeFactory();
        var host = new ConfigMgrSessionHost(
            connection ?? (() => new ConfigMgrConnection("ABC", "cm01.lab.test")),
            factory, log, options ?? new ConfigMgrSessionOptions(), () => @"C:\Console\bin\ConfigurationManager.psd1");
        return (host, factory);
    }

    private static List<JsonElement> RunRequests(FakeProcess process)
    {
        lock (process.Requests) return process.Requests.Where(r => r.GetProperty("op").GetString() == "run").ToList();
    }

    [Fact]
    public async Task TheFirstCommandStartsOneProcessConnectsThenRunsWithParametersOutsideTheScriptText()
    {
        var (host, factory) = NewHost();
        await using var _ = host;

        var result = await host.InvokeAsync(Script, new ConfigMgrParameters { ["Name"] = "O'Brien; Remove-Item C:\\" });

        Assert.Equal(["ok"], result.Lines);
        var process = Assert.Single(factory.Started);
        var connect = process.Requests[0];
        Assert.Equal("connect", connect.GetProperty("op").GetString());
        Assert.Equal("ABC", connect.GetProperty("siteCode").GetString());
        Assert.Equal("cm01.lab.test", connect.GetProperty("provider").GetString());
        Assert.Equal(@"C:\Console\bin\ConfigurationManager.psd1", connect.GetProperty("modulePath").GetString());
        var run = Assert.Single(RunRequests(process));
        Assert.Equal("param($Name) $Name", run.GetProperty("script").GetString());
        Assert.Equal("O'Brien; Remove-Item C:\\", run.GetProperty("parameters").GetProperty("Name").GetString());
        Assert.Equal("Test", run.GetProperty("name").GetString());
    }

    [Fact]
    public async Task LaterCommandsReuseTheSameProcess()
    {
        var (host, factory) = NewHost();
        await using var _ = host;
        await host.InvokeAsync(Script);
        await host.InvokeAsync(Script);
        Assert.Single(factory.Started);
        Assert.Equal(2, RunRequests(factory.Started[0]).Count);
    }

    [Fact]
    public async Task OnlyLinesWithTheCommandIdBelongToTheCommand()
    {
        var (host, factory) = NewHost();
        await using var _ = host;
        factory.Handler = (p, r) =>
        {
            if (r.GetProperty("op").GetString() != "run") { FakeFactory.Default(p, r); return; }
            p.Emit("WARNING: text written to the host");
            p.Emit("{\"id\":\"someone-else\",\"t\":\"out\",\"v\":\"stray\"}");
            p.Emit("{not json");
            p.Reply(r, "\"t\":\"out\",\"v\":{\"SiteCode\":\"ABC\",\"Count\":3}", "\"t\":\"warn\",\"m\":\"slow provider\"", "\"t\":\"out\",\"v\":\"second\"");
        };

        var result = await host.InvokeAsync(Script);

        Assert.Equal(2, result.Output.Count);
        Assert.Equal("{\"SiteCode\":\"ABC\",\"Count\":3}", result.Lines[0]);
        Assert.Equal("second", result.Lines[1]);
        Assert.Equal(["slow provider"], result.Warnings);
        Assert.Equal(3, result.Output[0].GetProperty("Count").GetInt32());
    }

    private sealed record SiteRow(string SiteCode, int Count);

    [Fact]
    public async Task ATypedResultReadsEachObjectLine()
    {
        var (host, factory) = NewHost();
        await using var _ = host;
        factory.Handler = (p, r) =>
        {
            if (r.GetProperty("op").GetString() != "run") { FakeFactory.Default(p, r); return; }
            p.Reply(r, "\"t\":\"out\",\"v\":{\"siteCode\":\"ABC\",\"count\":1}", "\"t\":\"out\",\"v\":{\"SiteCode\":\"DEF\",\"Count\":2}");
        };
        var rows = (await host.InvokeAsync(Script)).As<SiteRow>();
        Assert.Equal([new SiteRow("ABC", 1), new SiteRow("DEF", 2)], rows);
    }

    [Fact]
    public async Task CommandsRunOneAtATimeInTheOrderTheyWereQueued()
    {
        var (host, factory) = NewHost();
        await using var _ = host;
        var held = new ConcurrentQueue<(FakeProcess, JsonElement)>();
        factory.Handler = (p, r) =>
        {
            if (r.GetProperty("op").GetString() == "run") held.Enqueue((p, r));
            else FakeFactory.Default(p, r);
        };

        var first = host.InvokeAsync(Script, new ConfigMgrParameters { ["Name"] = "1" });
        await WaitUntil(() => held.Count == 1);
        var second = host.InvokeAsync(Script, new ConfigMgrParameters { ["Name"] = "2" });
        var third = host.InvokeAsync(Script, new ConfigMgrParameters { ["Name"] = "3" });
        await Task.Delay(100);
        Assert.Single(RunRequests(factory.Started[0]));

        for (var expected = 1; expected <= 3; expected++)
        {
            await WaitUntil(() => !held.IsEmpty);
            Assert.True(held.TryDequeue(out var item));
            Assert.Equal(expected.ToString(), item.Item2.GetProperty("parameters").GetProperty("Name").GetString());
            item.Item1.Reply(item.Item2, "\"t\":\"out\",\"v\":\"" + expected + "\"");
        }

        Assert.Equal(["1"], (await first).Lines);
        Assert.Equal(["2"], (await second).Lines);
        Assert.Equal(["3"], (await third).Lines);
        Assert.Single(factory.Started);
    }

    [Fact]
    public async Task CancelEndsTheRunningCommandByRestartingTheProcess()
    {
        var (host, factory) = NewHost();
        await using var _ = host;
        factory.Handler = (p, r) =>
        {
            if (r.GetProperty("op").GetString() == "run" && p.Number == 1) return;
            FakeFactory.Default(p, r);
        };
        using var cts = new CancellationTokenSource();
        var running = host.InvokeAsync(Script, ct: cts.Token);
        await WaitUntil(() => factory.Started.Count == 1 && RunRequests(factory.Started[0]).Count == 1);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(factory.Started[0].Killed);
        Assert.Equal(["ok"], (await host.InvokeAsync(Script)).Lines);
        Assert.Equal(2, factory.Started.Count);
        Assert.Equal("connect", factory.Started[1].Requests[0].GetProperty("op").GetString());
    }

    [Fact]
    public async Task CancelOfAQueuedCommandLeavesTheRunningCommandAndTheProcessAlone()
    {
        var (host, factory) = NewHost();
        await using var _ = host;
        var held = new ConcurrentQueue<(FakeProcess, JsonElement)>();
        factory.Handler = (p, r) =>
        {
            if (r.GetProperty("op").GetString() == "run") held.Enqueue((p, r));
            else FakeFactory.Default(p, r);
        };
        var first = host.InvokeAsync(Script);
        await WaitUntil(() => held.Count == 1);
        using var cts = new CancellationTokenSource();
        var queued = host.InvokeAsync(Script, ct: cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        held.TryDequeue(out var item);
        item.Item1.Reply(item.Item2, "\"t\":\"out\",\"v\":\"done\"");
        Assert.Equal(["done"], (await first).Lines);
        Assert.False(factory.Started[0].Killed);
        await Task.Delay(50);
        Assert.Single(RunRequests(factory.Started[0]));
    }

    [Fact]
    public async Task ACommandThatOverrunsItsTimeoutFailsPlainlyAndRestartsTheProcess()
    {
        var (host, factory) = NewHost(options: new ConfigMgrSessionOptions { CommandTimeout = TimeSpan.FromMilliseconds(200) });
        await using var _ = host;
        factory.Handler = (p, r) =>
        {
            if (r.GetProperty("op").GetString() == "run" && p.Number == 1) return;
            FakeFactory.Default(p, r);
        };

        var failure = await Assert.ThrowsAsync<ConfigMgrSessionException>(() => host.InvokeAsync(Script));

        Assert.Equal(ConfigMgrFailure.Timeout, failure.Failure);
        Assert.Equal("The ConfigMgr command Test did not finish within 0.2 seconds. The session was restarted.", failure.Message);
        Assert.True(factory.Started[0].Killed);
        Assert.Equal(["ok"], (await host.InvokeAsync(Script)).Lines);
    }

    [Fact]
    public async Task APerCommandTimeoutOverridesTheDefault()
    {
        var (host, factory) = NewHost(options: new ConfigMgrSessionOptions { CommandTimeout = TimeSpan.FromMinutes(10) });
        await using var _ = host;
        factory.Handler = (p, r) =>
        {
            if (r.GetProperty("op").GetString() == "run") return;
            FakeFactory.Default(p, r);
        };
        var failure = await Assert.ThrowsAsync<ConfigMgrSessionException>(() => host.InvokeAsync(Script, timeout: TimeSpan.FromMilliseconds(100)));
        Assert.Equal(ConfigMgrFailure.Timeout, failure.Failure);
    }

    [Fact]
    public async Task AChangedSiteOrProviderReconnectsAndTheSameSiteInAnotherCaseDoesNot()
    {
        var connection = new ConfigMgrConnection("ABC", "cm01.lab.test");
        var (host, factory) = NewHost(() => connection);
        await using var _ = host;

        await host.InvokeAsync(Script);
        connection = new ConfigMgrConnection(" abc ", "CM01.lab.test");
        await host.InvokeAsync(Script);
        Assert.Single(factory.Started);

        connection = new ConfigMgrConnection("DEF", "cm01.lab.test");
        await host.InvokeAsync(Script);
        Assert.Equal(2, factory.Started.Count);
        Assert.True(factory.Started[0].Killed);
        Assert.Equal("DEF", factory.Started[1].Requests[0].GetProperty("siteCode").GetString());

        connection = new ConfigMgrConnection("DEF", "cm02.lab.test");
        await host.InvokeAsync(Script);
        Assert.Equal(3, factory.Started.Count);
        Assert.Equal("cm02.lab.test", factory.Started[2].Requests[0].GetProperty("provider").GetString());
    }

    [Fact]
    public async Task AProcessThatDiedBetweenCommandsIsStartedAgainWithoutAFailure()
    {
        var (host, factory) = NewHost();
        await using var _ = host;
        await host.InvokeAsync(Script);
        factory.Started[0].Die();

        Assert.Equal(["ok"], (await host.InvokeAsync(Script)).Lines);
        Assert.Equal(2, factory.Started.Count);
    }

    [Fact]
    public async Task AProcessThatDiesDuringACommandFailsThatCommandPlainlyAndTheNextOneStartsANewSession()
    {
        var (host, factory) = NewHost();
        await using var _ = host;
        factory.Handler = (p, r) =>
        {
            if (r.GetProperty("op").GetString() == "run" && p.Number == 1) { p.Die(); return; }
            FakeFactory.Default(p, r);
        };

        var failure = await Assert.ThrowsAsync<ConfigMgrSessionException>(() => host.InvokeAsync(Script));
        Assert.Equal(ConfigMgrFailure.ProcessDied, failure.Failure);
        Assert.Equal("The Windows PowerShell session for ConfigMgr ended while the command Test ran. The next command starts a new session.", failure.Message);

        Assert.Equal(["ok"], (await host.InvokeAsync(Script)).Lines);
        Assert.Equal(2, factory.Started.Count);
    }

    [Theory]
    [InlineData("module", ConfigMgrFailure.ModuleNotFound,
        "The ConfigurationManager PowerShell module was not found. Install the Configuration Manager console on this computer.")]
    [InlineData("drive", ConfigMgrFailure.DriveNotConnected,
        "The CMSite drive for site ABC on cm01.lab.test could not connect. Check the site code and the SMS Provider in Settings, ConfigMgr Site Settings.")]
    [InlineData("access", ConfigMgrFailure.AccessDenied,
        "The SMS Provider cm01.lab.test denied access. The account needs a ConfigMgr security role for site ABC.")]
    public async Task AConnectFailureHasAPlainMessageEndsTheProcessAndTheNextCommandTriesAgain(string kind, ConfigMgrFailure expected, string message)
    {
        var (host, factory) = NewHost();
        await using var _ = host;
        factory.Handler = (p, r) =>
        {
            if (r.GetProperty("op").GetString() == "connect" && p.Number == 1)
            {
                p.Reply(r, "\"t\":\"error\",\"kind\":\"" + kind + "\",\"m\":\"raw provider text 0x80041001\"");
                return;
            }
            FakeFactory.Default(p, r);
        };

        var failure = await Assert.ThrowsAsync<ConfigMgrSessionException>(() => host.InvokeAsync(Script));

        Assert.Equal(expected, failure.Failure);
        Assert.Equal(message, failure.Message);
        Assert.DoesNotContain("0x80041001", failure.Message);
        Assert.Empty(RunRequests(factory.Started[0]));
        Assert.True(factory.Started[0].Killed);
        Assert.Equal(["ok"], (await host.InvokeAsync(Script)).Lines);
    }

    [Fact]
    public async Task ALocalProviderIsNamedAsThisComputerInTheMessage()
    {
        var (host, factory) = NewHost(() => new ConfigMgrConnection("ABC", ""));
        await using var _ = host;
        factory.Handler = (p, r) => p.Reply(r, "\"t\":\"error\",\"kind\":\"drive\",\"m\":\"x\"");
        var failure = await Assert.ThrowsAsync<ConfigMgrSessionException>(() => host.InvokeAsync(Script));
        Assert.Contains("on this computer", failure.Message);
        Assert.Equal("", factory.Started[0].Requests[0].GetProperty("provider").GetString());
    }

    [Fact]
    public async Task AScriptErrorFailsOnlyThatCommandAndKeepsTheSession()
    {
        var (host, factory) = NewHost();
        await using var _ = host;
        var calls = 0;
        factory.Handler = (p, r) =>
        {
            if (r.GetProperty("op").GetString() == "run" && Interlocked.Increment(ref calls) == 1)
            {
                p.Reply(r, "\"t\":\"error\",\"kind\":\"script\",\"m\":\"No object corresponds to the specified parameters.\"");
                return;
            }
            FakeFactory.Default(p, r);
        };

        var failure = await Assert.ThrowsAsync<ConfigMgrSessionException>(() => host.InvokeAsync(Script));
        Assert.Equal(ConfigMgrFailure.ScriptFailed, failure.Failure);
        Assert.Equal("The ConfigMgr command Test failed: No object corresponds to the specified parameters.", failure.Message);

        await host.InvokeAsync(Script);
        Assert.Single(factory.Started);
        Assert.False(factory.Started[0].Killed);
    }

    [Fact]
    public async Task AnUnexpectedExceptionReachesTheCallerAsAFixedMessageAndOnlyTheMaskedTextIsLogged()
    {
        var log = new RecordingLog();
        var (host, factory) = NewHost(log: log);
        await using var _ = host;
        factory.Handler = (p, r) =>
        {
            if (r.GetProperty("op").GetString() == "run") throw new InvalidOperationException("Pipe state broke near password=Zq9secret at C:\\Internal\\Path");
            FakeFactory.Default(p, r);
        };

        var failure = await Assert.ThrowsAsync<ConfigMgrSessionException>(() => host.InvokeAsync(Script));

        Assert.Equal(ConfigMgrFailure.Unexpected, failure.Failure);
        Assert.Equal("The ConfigMgr session failed unexpectedly. The log has the details.", failure.Message);
        Assert.Null(failure.InnerException);
        var logged = string.Join("\n", log.Entries.Select(e => e.Message));
        Assert.Contains("Pipe state broke", logged);
        Assert.DoesNotContain("Zq9secret", logged);
    }

    [Fact]
    public async Task AnAccessDeniedDuringACommandHasThePlainMessage()
    {
        var (host, factory) = NewHost();
        await using var _ = host;
        factory.Handler = (p, r) =>
        {
            if (r.GetProperty("op").GetString() == "run") { p.Reply(r, "\"t\":\"error\",\"kind\":\"access\",\"m\":\"Access is denied.\""); return; }
            FakeFactory.Default(p, r);
        };
        var failure = await Assert.ThrowsAsync<ConfigMgrSessionException>(() => host.InvokeAsync(Script));
        Assert.Equal(ConfigMgrFailure.AccessDenied, failure.Failure);
        Assert.StartsWith("The SMS Provider cm01.lab.test denied access.", failure.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AB")]
    [InlineData("AB;C")]
    public async Task AnInvalidSiteCodeFailsWithoutStartingPowerShell(string siteCode)
    {
        var (host, factory) = NewHost(() => new ConfigMgrConnection(siteCode, "cm01"));
        await using var _ = host;
        var failure = await Assert.ThrowsAsync<ConfigMgrSessionException>(() => host.InvokeAsync(Script));
        Assert.Equal(ConfigMgrFailure.InvalidSiteCode, failure.Failure);
        Assert.Contains("Settings, ConfigMgr Site Settings", failure.Message);
        Assert.Empty(factory.Started);
    }

    [Fact]
    public async Task PowerShellThatCannotStartHasAPlainMessage()
    {
        var (host, factory) = NewHost();
        await using var _ = host;
        factory.StartFailure = new Win32Exception(2, "The system cannot find the file specified");
        var failure = await Assert.ThrowsAsync<ConfigMgrSessionException>(() => host.InvokeAsync(Script));
        Assert.Equal(ConfigMgrFailure.PowerShellNotStarted, failure.Failure);
        Assert.Equal("Windows PowerShell 5.1 (powershell.exe) could not start. ConfigMgr commands need it.", failure.Message);
    }

    [Fact]
    public async Task AConnectThatDoesNotAnswerTimesOut()
    {
        var (host, factory) = NewHost(options: new ConfigMgrSessionOptions { ConnectTimeout = TimeSpan.FromMilliseconds(150) });
        await using var _ = host;
        factory.Handler = (_, _) => { };
        var failure = await Assert.ThrowsAsync<ConfigMgrSessionException>(() => host.InvokeAsync(Script));
        Assert.Equal(ConfigMgrFailure.Timeout, failure.Failure);
        Assert.Contains("did not connect", failure.Message);
        Assert.True(factory.Started[0].Killed);
    }

    [Fact]
    public async Task DisposeEndsTheProcessAndFailsQueuedCommands()
    {
        var (host, factory) = NewHost();
        factory.Handler = (p, r) =>
        {
            if (r.GetProperty("op").GetString() == "run") return;
            FakeFactory.Default(p, r);
        };
        var running = host.InvokeAsync(Script);
        await WaitUntil(() => factory.Started.Count == 1 && RunRequests(factory.Started[0]).Count == 1);
        var queued = host.InvokeAsync(Script);

        await host.DisposeAsync();

        Assert.True(factory.Started[0].Killed);
        Assert.True(factory.Started[0].Disposed);
        await Assert.ThrowsAnyAsync<Exception>(() => running);
        await Assert.ThrowsAnyAsync<Exception>(() => queued);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.InvokeAsync(Script));
        await host.DisposeAsync();
    }

    [Fact]
    public async Task TheLogNamesTheCommandAndMasksSecretParameterValues()
    {
        var log = new RecordingLog();
        var (host, factory) = NewHost(log: log);
        await using var _ = host;
        factory.Handler = (p, r) =>
        {
            if (r.GetProperty("op").GetString() == "run") { p.Reply(r, "\"t\":\"error\",\"kind\":\"script\",\"m\":\"bad value Sup3rS3cret\""); return; }
            FakeFactory.Default(p, r);
        };
        var parameters = new ConfigMgrParameters { ["Name"] = "Visible" };
        parameters.AddSecret("Password", "Sup3rS3cret");

        var failure = await Assert.ThrowsAsync<ConfigMgrSessionException>(() => host.InvokeAsync(Script, parameters));

        Assert.DoesNotContain("Sup3rS3cret", failure.Message);
        var text = string.Join("\n", log.Entries.Select(e => e.Message));
        Assert.DoesNotContain("Sup3rS3cret", text);
        Assert.Contains("Test", text);
        Assert.Contains("Name=Visible", text);
        Assert.Contains("Password=" + SecretMask.Mask, text);
        Assert.All(log.Entries, e => Assert.Equal("ConfigMgr", e.Area));
        Assert.Contains(log.Entries, e => e.Level == AppLogLevel.Error);
        // The secret still reaches the child as a parameter value.
        Assert.Equal("Sup3rS3cret", RunRequests(factory.Started[0])[0].GetProperty("parameters").GetProperty("Password").GetString());
    }

    [Fact]
    public async Task WarningsAreMaskedLikeErrorText()
    {
        var (host, factory) = NewHost();
        await using var _ = host;
        factory.Handler = (p, r) =>
        {
            if (r.GetProperty("op").GetString() == "run")
            {
                p.Reply(r, "\"t\":\"warn\",\"m\":\"retrying with Sup3rS3cret\"", "\"t\":\"warn\",\"m\":\"connection string password=abc123 ignored\"", "\"t\":\"out\",\"v\":\"Sup3rS3cret raw\"");
                return;
            }
            FakeFactory.Default(p, r);
        };
        var parameters = new ConfigMgrParameters();
        parameters.AddSecret("Password", "Sup3rS3cret");

        var result = await host.InvokeAsync(Script, parameters);

        Assert.Equal(["retrying with ***", "connection string password=*** ignored"], result.Warnings);
        // Output values are raw: a script that returns a secret returns it to its caller.
        Assert.Equal(["Sup3rS3cret raw"], result.Lines);
    }

    [Fact]
    public async Task ARequestOverTheSizeLimitIsRefusedPlainlyBeforeItIsSent()
    {
        var log = new RecordingLog();
        var (host, factory) = NewHost(log: log);
        await using var _ = host;
        await host.InvokeAsync(Script);

        var failure = await Assert.ThrowsAsync<ConfigMgrSessionException>(() =>
            host.InvokeAsync(Script, new ConfigMgrParameters { ["Name"] = new string('x', ConfigMgrSessionHost.MaxRequestBytes) }));

        Assert.Equal(ConfigMgrFailure.RequestTooLarge, failure.Failure);
        Assert.Equal("The ConfigMgr command Test was not sent: its script and values are larger than the 1 MB limit for one command. Send the values in smaller parts.", failure.Message);
        Assert.Single(RunRequests(factory.Started[0]));
        Assert.False(factory.Started[0].Killed);
        Assert.Equal(["ok"], (await host.InvokeAsync(Script)).Lines);
        Assert.Single(factory.Started);
    }

    [Fact]
    public async Task ARequestJustUnderTheSizeLimitIsSent()
    {
        var (host, factory) = NewHost();
        await using var _ = host;
        await host.InvokeAsync(Script, new ConfigMgrParameters { ["Name"] = new string('x', ConfigMgrSessionHost.MaxRequestBytes - 1024) });
        Assert.Single(RunRequests(factory.Started[0]));
    }

    [Theory]
    [InlineData("1abc")]
    [InlineData("a-b")]
    [InlineData("a b")]
    [InlineData("")]
    public void AParameterNameMustBeAPowerShellIdentifier(string name)
    {
        Assert.Throws<ArgumentException>(() => new ConfigMgrParameters { [name] = "x" });
    }

    [Fact]
    public void AResourceScriptIsReadFromTheEngineAssembly()
    {
        var script = ConfigMgrScript.FromResource(ConfigMgrScripts.GetSites);
        Assert.Equal("GetSites", script.Name);
        Assert.Contains("Get-CMSite", script.Text);
        Assert.Throws<InvalidOperationException>(() => ConfigMgrScript.FromResource("ApplicationPackagerEngine.Missing.ps1"));
    }

    [Fact]
    public void TheShippedScriptsAreEmbedded()
    {
        foreach (var name in new[] { ConfigMgrScripts.Host, ConfigMgrScripts.GetSites, ConfigMgrScripts.GetCollections })
            Assert.False(string.IsNullOrWhiteSpace(ConfigMgrScripts.Read(name)), name);
    }

    [Fact]
    public void TheRealProcessStartsWindowsPowerShellWithRedirectedUtf8Pipes()
    {
        var info = PowerShellSessionProcess.BuildStartInfo(@"C:\Temp\host.ps1");
        Assert.Equal("powershell.exe", info.FileName);
        Assert.Contains("-NonInteractive", info.ArgumentList);
        Assert.Contains("-NoProfile", info.ArgumentList);
        Assert.Equal(@"C:\Temp\host.ps1", info.ArgumentList[info.ArgumentList.IndexOf("-File") + 1]);
        Assert.True(info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError);
        Assert.Equal("utf-8", info.StandardOutputEncoding!.WebName);
        Assert.Equal("utf-8", info.StandardInputEncoding!.WebName);
        Assert.Empty(info.StandardInputEncoding.GetPreamble());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not met.");
            await Task.Delay(10);
        }
    }
}
