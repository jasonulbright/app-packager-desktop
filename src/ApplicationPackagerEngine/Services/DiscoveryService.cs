using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services;

/// <summary>Where the site database lives.</summary>
internal sealed record DiscoveryDatabaseTarget(string Server, string Database);

/// <summary>
/// Discover unmanaged software: reads the site database through Windows PowerShell's
/// System.Data.SqlClient (Windows authentication, read-only intent, bound parameters) and
/// matches hidden packagers in this process. Standard output of the child is never logged,
/// because the inventory lines carry per-scan client ordinals.
/// </summary>
public sealed partial class DiscoveryService : IDiscoveryService
{
    public const int MaximumTimeoutSeconds = 600;
    public const int ConnectTimeoutSeconds = 30;
    internal const string InventoryResource = "ApplicationPackagerEngine.Services.DiscoveryScripts.DiscoveryInventory.ps1";
    internal const string SiteDefinitionResource = "ApplicationPackagerEngine.Services.DiscoveryScripts.DiscoverySiteDefinition.ps1";
    internal const string SettingsLocation = "Settings, ConfigMgr Site Settings";

    private static readonly int[] LoginErrors = [18452, 18456, 18470, 18486, 18487, 18488];
    private static readonly int[] DatabaseErrors = [911, 4060];
    private static readonly int[] PermissionErrors = [229, 230];
    private static readonly int[] LoginDroppedErrors = [233];
    private static readonly int[] NetworkErrors = [-1, 2, 26, 40, 53, 64, 121, 1225, 10053, 10054, 10060, 10061, 11001, 11004];
    private static readonly int[] TimeoutErrors = [-2];

    /// <summary>Runs one script in Windows PowerShell and returns its exit code and output.</summary>
    internal delegate Task<(int ExitCode, string StdOut, string StdErr)> ScriptRunner(string script, CancellationToken ct);

    private readonly ScriptRunner _run;
    private readonly TimeProvider _time;

    public DiscoveryService() : this(
        (script, ct) => WindowsPowerShellHost.RunAsync(script, null, ct, "ap-discovery"), TimeProvider.System)
    {
    }

    internal DiscoveryService(ScriptRunner run, TimeProvider time)
    {
        _run = run;
        _time = time;
    }

    [GeneratedRegex("^[A-Za-z0-9]{3}$")]
    private static partial Regex SiteCodeRegex();

    [GeneratedRegex(@"^(?<instance>[^\\]+)\\(?<db>[^\\]+)$")]
    private static partial Regex NamedInstanceRegex();

    public async Task<DiscoveryResult> ScanAsync(DiscoveryRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var timeout = Math.Clamp(request.TimeoutSeconds, 1, MaximumTimeoutSeconds);
        var packagers = DiscoveryMatcher.Prepare(request.Packagers, request.HiddenIds, request.DismissedIds);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(timeout));
        try
        {
            var target = await ResolveTargetAsync(request, limit.Token).ConfigureAwait(false);
            var snapshot = await ReadSnapshotAsync(target, timeout, limit.Token).ConfigureAwait(false);
            var candidates = DiscoveryMatcher.FindCandidates(snapshot, packagers);
            var status = DiscoveryMatcher.StatusText(snapshot, candidates.Count, _time.GetLocalNow().DateTime);
            return new DiscoveryResult(candidates, status);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DiscoveryException($"The scan stopped after {timeout.ToString(CultureInfo.InvariantCulture)} seconds.");
        }
    }

    /// <summary>
    /// Overrides first, then the site definition on the SMS Provider, with CM_&lt;SiteCode&gt;
    /// as the database default. Both overrides together need no site code and no provider.
    /// </summary>
    internal async Task<DiscoveryDatabaseTarget> ResolveTargetAsync(DiscoveryRequest request, CancellationToken ct)
    {
        var server = request.ServerOverride?.Trim() ?? "";
        var database = request.DatabaseOverride?.Trim() ?? "";
        if (server.Length > 0 && database.Length > 0) return new DiscoveryDatabaseTarget(server, database);

        var code = request.SiteCode?.Trim() ?? "";
        if (code.Length == 0)
            throw new DiscoveryException($"A site code or SQL overrides are required. Set the site code, or the SQL server and database, in {SettingsLocation}.");
        if (!SiteCodeRegex().IsMatch(code))
            throw new DiscoveryException($"The site code {code} is not three letters or digits. Correct it in {SettingsLocation}.");
        code = code.ToUpperInvariant();

        var provider = request.ProviderMachineName?.Trim() ?? "";
        string lookupError;
        if (provider.Length == 0)
        {
            lookupError = "no SMS Provider is configured";
        }
        else
        {
            var site = await ReadSiteDefinitionAsync(provider, code, ct).ConfigureAwait(false);
            if (site is null)
            {
                lookupError = $"the SMS Provider {provider} did not return the site definition";
            }
            else if (!site.Value.Found)
            {
                lookupError = $"the SMS Provider {provider} has no site definition for {code}";
            }
            else
            {
                lookupError = "";
                var siteServer = site.Value.Server.Trim();
                var siteDatabase = site.Value.Database.Trim();
                // A named instance is stored as INSTANCE\CM_<code> in SQLDatabaseName.
                var named = NamedInstanceRegex().Match(siteDatabase);
                if (named.Success)
                {
                    siteDatabase = named.Groups["db"].Value;
                    var instance = named.Groups["instance"].Value;
                    if (siteServer.Length > 0 && !string.Equals(instance, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase))
                        siteServer = $@"{siteServer}\{instance}";
                }
                if (siteServer.Length == 0) lookupError = "the site definition has no SQL Server name";
                if (server.Length == 0) server = siteServer;
                if (database.Length == 0) database = siteDatabase;
            }
        }

        if (database.Length == 0) database = $"CM_{code}";
        if (server.Length == 0)
            throw new DiscoveryException($"The SQL Server for site {code} is not known ({lookupError}). Enter the SQL Server name in {SettingsLocation}.");
        return new DiscoveryDatabaseTarget(server, database);
    }

    private async Task<string> RunAsync(string script, CancellationToken ct)
    {
        try
        {
            var run = await _run(script, ct).ConfigureAwait(false);
            return run.StdOut ?? "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DiscoveryException("Windows PowerShell could not start. Discover needs Windows PowerShell 5.1 (powershell.exe).");
        }
    }

    private async Task<(bool Found, string Server, string Database)?> ReadSiteDefinitionAsync(string provider, string code, CancellationToken ct)
    {
        var stdout = await RunAsync(BuildSiteDefinitionScript(provider, code), ct).ConfigureAwait(false);
        var lines = ReadLines(stdout);
        if (lines is null) return null;
        foreach (var line in lines)
        {
            if (Text(line, "t") != "site") continue;
            var found = line.TryGetProperty("found", out var f) && f.ValueKind == JsonValueKind.True;
            return (found, Text(line, "server"), Text(line, "database"));
        }
        return null;
    }

    private async Task<InventorySnapshot> ReadSnapshotAsync(DiscoveryDatabaseTarget target, int timeout, CancellationToken ct)
    {
        var stdout = await RunAsync(BuildInventoryScript(target, timeout), ct).ConfigureAwait(false);
        return ParseInventory(stdout, target, timeout);
    }

    internal static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded script {name} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The inventory script with its inputs as escaped single-quoted literals ahead of the embedded text.</summary>
    internal static string BuildInventoryScript(DiscoveryDatabaseTarget target, int commandTimeoutSeconds)
    {
        var sb = new StringBuilder();
        sb.Append("$DiscoveryServer = ").AppendLine(PowerShellLiteral.SingleQuoted(target.Server));
        sb.Append("$DiscoveryDatabase = ").AppendLine(PowerShellLiteral.SingleQuoted(target.Database));
        sb.Append("$DiscoveryConnectTimeout = ").AppendLine(Math.Min(ConnectTimeoutSeconds, commandTimeoutSeconds).ToString(CultureInfo.InvariantCulture));
        sb.Append("$DiscoveryCommandTimeout = ").AppendLine(commandTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("$DiscoveryRun = $true");
        sb.Append(ReadResource(InventoryResource));
        return sb.ToString();
    }

    internal static string BuildSiteDefinitionScript(string provider, string siteCode)
    {
        if (!SiteCodeRegex().IsMatch(siteCode)) throw new ArgumentException("The site code is not three letters or digits.", nameof(siteCode));
        var sb = new StringBuilder();
        sb.Append("$DiscoveryProvider = ").AppendLine(PowerShellLiteral.SingleQuoted(provider));
        sb.Append("$DiscoverySiteCode = ").AppendLine(PowerShellLiteral.SingleQuoted(siteCode));
        sb.AppendLine("$DiscoveryRun = $true");
        sb.Append(ReadResource(SiteDefinitionResource));
        return sb.ToString();
    }

    /// <summary>The JSON lines of a run, or null when the run did not reach its end line.</summary>
    private static List<JsonElement>? ReadLines(string stdout)
    {
        var lines = new List<JsonElement>();
        var ended = false;
        foreach (var raw in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var text = raw.Trim();
            if (!text.StartsWith("{\"t\":", StringComparison.Ordinal)) continue;
            JsonElement element;
            try { element = JsonDocument.Parse(text).RootElement.Clone(); }
            catch (JsonException) { continue; }
            if (Text(element, "t") == "end") { ended = true; break; }
            lines.Add(element);
        }
        return ended ? lines : null;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    /// <summary>Parses the inventory output. Throws a plain <see cref="DiscoveryException"/> for an error line or a broken run.</summary>
    internal static InventorySnapshot ParseInventory(string stdout, DiscoveryDatabaseTarget target, int timeout)
    {
        var lines = ReadLines(stdout)
            ?? throw new DiscoveryException($"The site database query on {target.Server} did not finish. Check that Windows PowerShell 5.1 runs on this computer.");

        var rows = new List<InventoryRow>();
        var applications = new List<string>();
        var summary = false;
        foreach (var line in lines)
        {
            switch (Text(line, "t"))
            {
                case "error":
                    throw new DiscoveryException(SqlMessage(line, target, timeout));
                case "row":
                    rows.Add(new InventoryRow(Text(line, "n"), Text(line, "v"), Text(line, "a")));
                    break;
                case "summary":
                    summary = true;
                    break;
                case "app":
                    var title = Text(line, "n");
                    if (title.Trim().Length > 0) applications.Add(title);
                    break;
            }
        }
        if (!summary) throw new DiscoveryException($"The site database query on {target.Server} returned an unexpected result.");
        return new InventorySnapshot(rows, applications);
    }

    /// <summary>The plain message for an error line: SQL error numbers and two flags, never the raw text.</summary>
    internal static string SqlMessage(JsonElement line, DiscoveryDatabaseTarget target, int timeout)
    {
        var numbers = new List<int>();
        if (line.TryGetProperty("numbers", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var n in array.EnumerateArray())
                if (n.TryGetInt32(out var value)) numbers.Add(value);
        var sspi = line.TryGetProperty("sspi", out var s) && s.ValueKind == JsonValueKind.True;
        var anonymous = line.TryGetProperty("anonymous", out var a) && a.ValueKind == JsonValueKind.True;
        bool Has(int[] set) => numbers.Any(set.Contains);
        var server = target.Server;
        var database = target.Database;

        if (Has(DatabaseErrors))
            return $"Database {database} does not exist on SQL Server {server}, or the Windows login cannot open it. Check the database name.";
        if (Has(LoginErrors))
        {
            if (anonymous)
                return $"SQL Server on {server} received an anonymous login. Windows credentials do not pass through a second remoting hop without Kerberos delegation. Run the scan from a session that holds your own logon.";
            return $"SQL Server on {server} denied the Windows login. The account needs the db_datareader role on {database}.";
        }
        // A wrong database over the Shared Memory provider (Data Source=localhost) reports
        // 233 instead of 4060: the server drops the pipe during the login.
        if (Has(LoginDroppedErrors))
            return $"SQL Server on {server} closed the connection during the login. Check the database name {database}, and that the account has the db_datareader role on it.";
        if (sspi)
            return $"Windows authentication to SQL Server {server} failed. Run the app as a domain account on a domain-joined computer, and check the SQL Server service principal name.";
        if (Has(PermissionErrors))
            return $"The Windows login cannot read the inventory views in {database} on {server}. The account needs the db_datareader role on {database}.";
        if (Has(TimeoutErrors))
            return $"SQL Server {server} did not answer within {timeout.ToString(CultureInfo.InvariantCulture)} seconds.";
        if (Has(NetworkErrors))
            return $"Cannot reach SQL Server {server}. Check the server name, the network path and the SQL Server firewall port.";
        return numbers.Count > 0
            ? $"The site database query on {server} failed with SQL error {numbers[0].ToString(CultureInfo.InvariantCulture)}."
            : $"The site database query on {server} failed.";
    }
}
