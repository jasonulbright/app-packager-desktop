using System.Globalization;
using System.Text;
using System.Text.Json;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Reads the Live views. Each SMS Provider source is one call of the read-only Live script in the ConfigMgr
/// session (one or a few bulk queries, never one per object); the two SQL views are one read-only Windows
/// PowerShell process with two SELECT statements, as Discover reads the site database. A failed source
/// marks only its view failed; a cut source marks its view truncated with every count null. Messages are
/// fixed text; no provider or SQL error text reaches a view.
/// </summary>
public sealed class SiteHygieneLiveService : ISiteHygieneLiveService
{
    public const string LogArea = "SiteHygiene.Live";

    internal const string ScriptResource = "ApplicationPackagerEngine.Services.SiteHygiene.Scripts.SiteHygieneLive.ps1";
    internal const string SqlResource = "ApplicationPackagerEngine.Services.SiteHygiene.Scripts.SiteHygieneLiveSql.ps1";

    public const int DefaultMaxDeployments = 100_000;
    public const int DefaultMaxPairs = 1_000_000;
    public const int DefaultMaxDps = 10_000;
    public const int DefaultMaxSiteRows = 50_000;
    public const int DefaultMaxSqlRows = 200_000;

    public const int SqlConnectTimeoutSeconds = DiscoveryService.ConnectTimeoutSeconds;
    public const int SqlCommandTimeoutSeconds = 120;

    /// <summary>The time one provider read may take in the session.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The time the search for the site database on the SMS Provider may take.</summary>
    public static readonly TimeSpan DefaultTargetTimeout = TimeSpan.FromSeconds(120);

    internal const string TargetTimeoutMessage = "The SMS Provider did not answer the search for the site database in time. Refresh again, or enter the SQL Server in Options.";

    /// <summary>The provider views in the order a refresh reads them, then the SQL views.</summary>
    public static readonly IReadOnlyList<LiveView> Order =
        [LiveView.Deployments, LiveView.Content, LiveView.DistributionPoints, LiveView.SiteHealth, LiveView.ClientHealth, LiveView.InactiveDevices];

    private static readonly ConfigMgrScript Script = ConfigMgrScript.FromResource(ScriptResource);

    private readonly IConfigMgrSession _session;
    private readonly IAppLog? _log;
    private readonly Func<ConfigMgrConnection>? _connection;
    private readonly Func<DateTime> _utcNow;
    private readonly DiscoveryService.ScriptRunner _run;
    private readonly object _gate = new();
    private (string Key, DiscoveryDatabaseTarget Target)? _target;

    public SiteHygieneLiveService(IConfigMgrSession session, IAppLog? log = null, Func<ConfigMgrConnection>? connection = null, Func<DateTime>? utcNow = null)
        : this(session, (script, ct) => WindowsPowerShellHost.RunAsync(script, null, ct, "ap-sitehygiene-live"), log, connection, utcNow)
    {
    }

    internal SiteHygieneLiveService(IConfigMgrSession session, DiscoveryService.ScriptRunner run, IAppLog? log = null, Func<ConfigMgrConnection>? connection = null, Func<DateTime>? utcNow = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _log = log;
        _connection = connection;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public int MaxDeployments { get; init; } = DefaultMaxDeployments;
    public int MaxPairs { get; init; } = DefaultMaxPairs;
    public int MaxDps { get; init; } = DefaultMaxDps;
    public int MaxSiteRows { get; init; } = DefaultMaxSiteRows;
    public int MaxSqlRows { get; init; } = DefaultMaxSqlRows;
    public TimeSpan TargetTimeout { get; init; } = DefaultTargetTimeout;

    private ConfigMgrConnection? Connection()
    {
        if (_connection is null) return null;
        var c = _connection();
        return new ConfigMgrConnection(HygieneSite.Code(c.SiteCode), (c.ProviderMachine ?? "").Trim());
    }

    public async Task<LiveSnapshot> RefreshAsync(LiveSettings settings, IReadOnlySet<LiveView> views, IProgress<string> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(progress);
        var started = _utcNow();
        var wanted = Order.Where(views.Contains).ToList();
        var results = new Dictionary<LiveView, LiveViewResult>();
        var connection = Connection();
        var step = 0;
        foreach (var view in wanted.Where(v => v is not (LiveView.ClientHealth or LiveView.InactiveDevices)))
        {
            ct.ThrowIfCancellationRequested();
            progress.Report($"Reading {LiveText.Title(view)} ({N(++step)} of {N(wanted.Count)}).");
            results[view] = await ReadProviderViewAsync(view, connection, ct).ConfigureAwait(false);
        }
        var sqlViews = wanted.Where(v => v is LiveView.ClientHealth or LiveView.InactiveDevices).ToList();
        if (sqlViews.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report($"Reading {string.Join(" and ", sqlViews.Select(LiveText.Title))} from the site database ({N(step + 1)} of {N(wanted.Count)}).");
            foreach (var (view, result) in await ReadSqlViewsAsync(settings, connection, sqlViews, ct).ConfigureAwait(false)) results[view] = result;
        }
        ct.ThrowIfCancellationRequested();
        var snapshot = new LiveSnapshot(results, started, _utcNow());
        var failed = results.Values.Count(r => r.Failed);
        var cut = results.Values.Count(r => r.Truncated);
        _log?.Info(LogArea, $"Live refresh: {LiveText.Count(results.Count, "view")}, {LiveText.Count(snapshot.QueryCount, "query", "queries")}, {N(failed)} failed, {N(cut)} cut.");
        progress.Report($"Refresh finished: {N(results.Count - failed)} of {LiveText.Count(results.Count, "view")} read.");
        return snapshot;
    }

    // ---- SMS Provider views --------------------------------------------------------------------

    private static string Operation(LiveView view) => view switch
    {
        LiveView.Deployments => "Deployments",
        LiveView.Content => "Content",
        LiveView.DistributionPoints => "DistributionPoints",
        LiveView.SiteHealth => "SiteHealth",
        _ => throw new ArgumentOutOfRangeException(nameof(view))
    };

    private static string[] Sets(LiveView view) => view switch
    {
        LiveView.Deployments => ["Deployments"],
        LiveView.Content => ["Pairs", "Messages", "Names"],
        LiveView.DistributionPoints => ["Dps", "DpStatus"],
        LiveView.SiteHealth => ["Components", "SiteSystems"],
        _ => []
    };

    private int Cap(LiveView view) => view switch
    {
        LiveView.Deployments => MaxDeployments,
        LiveView.Content => MaxPairs,
        LiveView.DistributionPoints => MaxDps,
        _ => MaxSiteRows
    };

    internal const string OtherConnectionMessage = "The ConfigMgr session is connected to another site or SMS Provider than the settings name. Refresh again.";
    internal const string UnreadableMessage = "ConfigMgr returned data that could not be read. The log has the details.";

    internal static string FailureMessage(LiveView view, string kind) => kind switch
    {
        "access" => $"The SMS Provider denied the read of {LiveText.Title(view)}. The account needs a ConfigMgr security role with read rights on the site.",
        "not-found" => $"The SMS Provider does not offer the data the {LiveText.Title(view)} view reads.",
        "timeout" => $"The SMS Provider did not answer the read of {LiveText.Title(view)} in time. Refresh again.",
        _ => $"The read of {LiveText.Title(view)} failed. The log has the kind of failure."
    };

    private LiveViewResult Failed(LiveView view, string message, int queries = 0) =>
        new(view, LiveAggregator.Columns(view), [], LiveCounts.Unknown(view), Truncated: false, Failed: true, message, _utcNow()) { QueryCount = queries };

    private async Task<LiveViewResult> ReadProviderViewAsync(LiveView view, ConfigMgrConnection? connection, CancellationToken ct)
    {
        var parameters = new ConfigMgrParameters { { "Operation", Operation(view) }, { "MaxRows", Cap(view) } };
        if (connection is not null)
        {
            parameters["ExpectSite"] = connection.NormalizedSiteCode;
            parameters["ExpectProvider"] = connection.NormalizedProvider;
        }
        ConfigMgrCommandResult output;
        try
        {
            output = await _session.InvokeAsync(Script, parameters, ct, ReadTimeout).ConfigureAwait(false);
        }
        catch (ConfigMgrSessionException ex)
        {
            _log?.Warning(LogArea, $"The read of {view} failed in the ConfigMgr session: {ex.Failure}.");
            return Failed(view, ex.Failure == ConfigMgrFailure.ScriptFailed
                ? FailureMessage(view, "error")
                : ComplianceService.SessionProblem(ex.Failure).Replace(ComplianceService.AccessHint, SiteHygieneService.AccessHint, StringComparison.Ordinal));
        }

        ct.ThrowIfCancellationRequested();
        try
        {
            var items = output.Output;
            var queries = 0;
            string? failure = null;
            foreach (var e in items)
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("Kind", out var k) || k.ValueKind != JsonValueKind.String) continue;
                switch (k.GetString())
                {
                    case "Connection":
                        _log?.Warning(LogArea, $"The read of {view} found the session on another site or provider and read nothing.");
                        return Failed(view, OtherConnectionMessage);
                    case "Failed":
                        failure = e.TryGetProperty("Failure", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : "error";
                        break;
                    case "Queries":
                        if (e.TryGetProperty("Count", out var q) && q.TryGetInt32(out var n)) queries = n;
                        break;
                }
            }
            if (failure is not null)
            {
                _log?.Warning(LogArea, $"The read of {view} failed: {failure}.");
                return Failed(view, FailureMessage(view, failure), queries);
            }
            var read = HygieneSiteRead.Parse(items);
            var sets = Sets(view);
            if (sets.Any(s => !read.Tables.TryGetValue(s, out var t) || t.State == HygieneSetStates.Failed))
            {
                _log?.Warning(LogArea, $"The read of {view} returned no status for a set.");
                return Failed(view, FailureMessage(view, "error"), queries);
            }
            var tables = sets.Select(s => read.Tables[s]).ToList();
            var cut = tables.Any(t => t.State == HygieneSetStates.Cut);
            var complete = !cut;
            ct.ThrowIfCancellationRequested();
            var (rows, counts) = view switch
            {
                LiveView.Deployments => LiveAggregator.Deployments(tables[0], complete, ct),
                LiveView.Content => LiveAggregator.Content(tables[0], tables[1], tables[2], complete, ct),
                LiveView.DistributionPoints => LiveAggregator.DistributionPoints(tables[0], tables[1], complete, ct),
                _ => LiveAggregator.SiteHealth(tables[0], tables[1], complete, ct)
            };
            string? message = null;
            if (cut)
            {
                var first = tables.First(t => t.State == HygieneSetStates.Cut);
                message = $"The read was cut at {N(first.Rows.Count)} of {LiveText.Count(first.Total, "row")}. The counts of this view are not known.";
                _log?.Warning(LogArea, $"The read of {view} was cut at its cap ({N(first.Rows.Count)} of {N(first.Total)}).");
            }
            else if (view == LiveView.Content && counts[LiveMetrics.ContentIssues] is null)
            {
                message = LiveAggregator.ContentUnknownMessage;
                _log?.Warning(LogArea, $"The read of {view} has a pair with a state that is not known.");
            }
            return new LiveViewResult(view, LiveAggregator.Columns(view), rows, counts, Truncated: cut, Failed: false, message, _utcNow())
            {
                QueryCount = queries,
                SourceRows = tables[0].Total
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            _log?.Warning(LogArea, $"ConfigMgr returned {view} data that could not be read ({ex.GetType().Name}).");
            return Failed(view, UnreadableMessage);
        }
    }

    // ---- SQL views -----------------------------------------------------------------------------

    private sealed class SqlNotSetUpException(string reason) : Exception(reason);

    /// <summary>The site database: the Discover settings first, then the site definition as Discover finds it. Kept per site, provider and settings.</summary>
    private async Task<DiscoveryDatabaseTarget> TargetAsync(LiveSettings settings, ConfigMgrConnection? connection, CancellationToken ct)
    {
        var site = connection?.NormalizedSiteCode ?? "";
        var provider = connection?.NormalizedProvider ?? "";
        var key = string.Join("|", site, provider.ToUpperInvariant(), (settings.SqlServer ?? "").Trim().ToUpperInvariant(), (settings.SqlDatabase ?? "").Trim().ToUpperInvariant());
        lock (_gate)
        {
            if (_target is { } t && t.Key == key) return t.Target;
        }
        var discovery = new DiscoveryService(_run, TimeProvider.System);
        var request = new DiscoveryRequest(site, provider, settings.SqlServer ?? "", settings.SqlDatabase ?? "", [], [], [], SqlCommandTimeoutSeconds);
        DiscoveryDatabaseTarget target;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TargetTimeout);
        try
        {
            target = await discovery.ResolveTargetAsync(request, limit.Token).ConfigureAwait(false);
        }
        catch (DiscoveryException ex)
        {
            throw new SqlNotSetUpException(ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.Warning(LogArea, "The search for the site database did not end in time.");
            throw new TimeoutException();
        }
        lock (_gate) _target = (key, target);
        return target;
    }

    private void ForgetTarget()
    {
        lock (_gate) _target = null;
    }

    internal static string BuildSqlScript(DiscoveryDatabaseTarget target, int cap, int days, bool trust, bool testOnly)
    {
        var sb = new StringBuilder();
        sb.Append("$LiveServer = ").AppendLine(PowerShellLiteral.SingleQuoted(target.Server));
        sb.Append("$LiveDatabase = ").AppendLine(PowerShellLiteral.SingleQuoted(target.Database));
        sb.Append("$LiveConnectTimeout = ").AppendLine(SqlConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        sb.Append("$LiveCommandTimeout = ").AppendLine(SqlCommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        sb.Append("$LiveCap = ").AppendLine(Math.Max(1, cap).ToString(CultureInfo.InvariantCulture));
        sb.Append("$LiveDays = ").AppendLine(LiveSettings.ClampInactive(days).ToString(CultureInfo.InvariantCulture));
        sb.Append("$LiveTrust = ").AppendLine(trust ? "$true" : "$false");
        sb.Append("$LiveTestOnly = ").AppendLine(testOnly ? "$true" : "$false");
        sb.AppendLine("$LiveRun = $true");
        sb.Append(DiscoveryService.ReadResource(SqlResource));
        return sb.ToString();
    }

    /// <summary>The parsed SQL output: rows by set, the error line, and whether the run reached its end line.</summary>
    internal sealed record SqlOutput(Dictionary<string, List<string[]>> Rows, JsonElement? Error, bool Ended, int? TestCount);

    internal static SqlOutput ParseSql(string stdout)
    {
        var rows = new Dictionary<string, List<string[]>>(StringComparer.Ordinal) { ["C"] = [], ["I"] = [] };
        JsonElement? error = null;
        var ended = false;
        int? test = null;
        foreach (var raw in (stdout ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var text = raw.Trim();
            if (!text.StartsWith("{\"t\":", StringComparison.Ordinal)) continue;
            JsonElement e;
            try { e = JsonDocument.Parse(text).RootElement.Clone(); }
            catch (JsonException) { continue; }
            var t = e.TryGetProperty("t", out var tv) && tv.ValueKind == JsonValueKind.String ? tv.GetString() : "";
            switch (t)
            {
                case "end":
                    ended = true;
                    break;
                case "error":
                    error = e;
                    break;
                case "test":
                    if (e.TryGetProperty("clients", out var c) && c.TryGetInt32(out var n)) test = n;
                    break;
                case "r":
                    var set = e.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
                    if (!rows.TryGetValue(set, out var list) || !e.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Array) continue;
                    list.Add(v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.GetRawText()).ToArray());
                    break;
            }
            if (ended) break;
        }
        return new SqlOutput(rows, error, ended, test);
    }

    private async Task<string> RunSqlAsync(string script, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(SqlConnectTimeoutSeconds + SqlCommandTimeoutSeconds + 30));
        try
        {
            var run = await _run(script, limit.Token).ConfigureAwait(false);
            return run.StdOut ?? "";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private async Task<IReadOnlyList<(LiveView View, LiveViewResult Result)>> ReadSqlViewsAsync(LiveSettings settings, ConfigMgrConnection? connection, IReadOnlyList<LiveView> views, CancellationToken ct)
    {
        IReadOnlyList<(LiveView, LiveViewResult)> All(Func<LiveView, LiveViewResult> make) => views.Select(v => (v, make(v))).ToList();
        DiscoveryDatabaseTarget target;
        try
        {
            target = await TargetAsync(settings, connection, ct).ConfigureAwait(false);
        }
        catch (SqlNotSetUpException ex)
        {
            _log?.Info(LogArea, "The SQL views were not read: no SQL Server is set up or found.");
            return All(v => new LiveViewResult(v, LiveAggregator.Columns(v), [], LiveCounts.Unknown(v), Truncated: false, Failed: false, LiveText.SqlNotSetUp + " " + ex.Message, _utcNow()) { Skipped = true });
        }
        catch (TimeoutException)
        {
            return All(v => Failed(v, TargetTimeoutMessage));
        }

        string stdout;
        try
        {
            stdout = await RunSqlAsync(BuildSqlScript(target, MaxSqlRows, settings.InactiveDays, settings.TrustSqlCertificate, testOnly: false), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ForgetTarget();
            return All(v => Failed(v, $"SQL Server {target.Server} did not answer in time."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warning(LogArea, $"Windows PowerShell did not start for the SQL views ({ex.GetType().Name}).");
            return All(v => Failed(v, "Windows PowerShell could not start. The SQL views need Windows PowerShell 5.1 (powershell.exe)."));
        }

        ct.ThrowIfCancellationRequested();
        var parsed = ParseSql(stdout);
        ct.ThrowIfCancellationRequested();
        if (parsed.Error is { } error)
        {
            ForgetTarget();
            var message = DiscoveryService.SqlMessage(error, target, SqlCommandTimeoutSeconds).Replace("the inventory views", "the client health views", StringComparison.Ordinal);
            _log?.Warning(LogArea, "The SQL views failed: " + message);
            return All(v => Failed(v, message, 1));
        }
        if (!parsed.Ended)
        {
            ForgetTarget();
            return All(v => Failed(v, $"The site database query on {target.Server} did not finish. Check that Windows PowerShell 5.1 runs on this computer."));
        }

        var results = new List<(LiveView, LiveViewResult)>();
        foreach (var view in views)
        {
            var data = parsed.Rows[view == LiveView.ClientHealth ? "C" : "I"];
            var cut = data.Count > MaxSqlRows;
            if (cut) data = data.Take(MaxSqlRows).ToList();
            var (rows, counts) = view == LiveView.ClientHealth ? LiveAggregator.Clients(data, !cut, ct) : LiveAggregator.Inactive(data, !cut, ct);
            var message = cut ? $"The read was cut at {LiveText.Count(MaxSqlRows, "row")}. The counts of this view are not known." : null;
            results.Add((view, new LiveViewResult(view, LiveAggregator.Columns(view), rows, counts, Truncated: cut, Failed: false, message, _utcNow())
            {
                QueryCount = 1,
                SourceRows = cut ? 0 : data.Count
            }));
        }
        return results;
    }

    public async Task<string> TestSqlAsync(LiveSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DiscoveryDatabaseTarget target;
        try
        {
            target = await TargetAsync(settings, Connection(), ct).ConfigureAwait(false);
        }
        catch (SqlNotSetUpException ex)
        {
            return LiveText.SqlNotSetUp + " " + ex.Message;
        }
        catch (TimeoutException)
        {
            return TargetTimeoutMessage;
        }
        string stdout;
        try
        {
            stdout = await RunSqlAsync(BuildSqlScript(target, 1, settings.InactiveDays, settings.TrustSqlCertificate, testOnly: true), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ForgetTarget();
            return $"SQL Server {target.Server} did not answer in time.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warning(LogArea, $"Test SQL could not run ({ex.GetType().Name}).");
            return "Windows PowerShell could not start. The SQL views need Windows PowerShell 5.1 (powershell.exe).";
        }
        var parsed = ParseSql(stdout);
        if (parsed.Error is { } error)
        {
            ForgetTarget();
            return DiscoveryService.SqlMessage(error, target, SqlCommandTimeoutSeconds).Replace("the inventory views", "the client health views", StringComparison.Ordinal);
        }
        if (!parsed.Ended || parsed.TestCount is null) return $"The site database query on {target.Server} did not finish.";
        return $"Connected to {target.Database} on {target.Server}. The client health view holds {LiveText.Count(parsed.TestCount.Value, "row")}.";
    }

    private static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
