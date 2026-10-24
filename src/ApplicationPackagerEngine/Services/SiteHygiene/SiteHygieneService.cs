using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Scans a ConfigMgr site with the 57 checks of the source catalog and runs their typed fixes through the
/// ConfigMgr session. A scan is one script run per read set, each one bulk query of the SMS Provider
/// (three sets read one object per row, each under <see cref="MaxPerObjectReads"/>), then every check in
/// memory in linear time. A set the site holds more rows of than <see cref="MaxRows"/>, or whose read
/// failed, makes the checks that need it NotRun; such a check never reports "no findings".
/// </summary>
public sealed partial class SiteHygieneService : ISiteHygieneService
{
    public const string LogArea = "SiteHygiene";

    /// <summary>Starts the message of an outcome or an object that changed nothing on purpose.</summary>
    public const string SkippedPrefix = "Skipped: ";

    /// <summary>The most rows one read set keeps; above it the set is cut and its checks do not run.</summary>
    public const int DefaultMaxRows = 200_000;

    /// <summary>The most objects a per-object set reads (collections with a full schedule, collections with settings, applications a task sequence references).</summary>
    public const int MaxPerObjectReads = 2_000;

    /// <summary>APP-04: the most content source paths one scan probes, and the time one probe may take.</summary>
    public const int MaxProbePaths = 200;

    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The time one read may take in the session.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(15);

    internal const string ScriptResource = "ApplicationPackagerEngine.Services.SiteHygiene.Scripts.SiteHygiene.ps1";

    internal const string AccessHint =
        "The account needs a ConfigMgr security role with read rights on the objects the scan reads, and for a fix the rights to delete or change the object.";

    private static readonly ConfigMgrScript Script = ConfigMgrScript.FromResource(ScriptResource);

    private readonly IConfigMgrSession _session;
    private readonly IHygieneAuditLog _audit;
    private readonly IAppLog? _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<string, TimeSpan, CancellationToken, Task<string>> _probe;

    private readonly ConcurrentDictionary<string, PreparedChange> _prepared = new(StringComparer.Ordinal);

    public SiteHygieneService(
        IConfigMgrSession session,
        IHygieneAuditLog audit,
        IAppLog? log = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTime>? utcNow = null,
        Func<string, TimeSpan, CancellationToken, Task<string>>? probe = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _log = log;
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _probe = probe ?? ProbePathAsync;
    }

    public int MaxRows { get; init; } = DefaultMaxRows;
    public int MaxObjects { get; init; } = MaxPerObjectReads;

    // ---- Scan ---------------------------------------------------------------------------------

    private static readonly IReadOnlySet<string> AllAreas = HygieneAreas.All.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlySet<string> ResolveAreas(IReadOnlySet<string>? areas)
    {
        var picked = (areas ?? new HashSet<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        if (picked.Count == 0) return AllAreas;
        var unknown = picked.Where(a => !AllAreas.Contains(a)).ToList();
        if (unknown.Count > 0) throw new HygieneServiceException("The scan area list holds an area this page does not know: " + string.Join(", ", unknown.Take(5)) + ".");
        return picked.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<HygieneScanResult> ScanAsync(HygieneScanOptions options, IProgress<string> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);
        var areas = ResolveAreas(options.Areas);
        var sets = HygieneChecks.RequiredSets(areas);
        var data = new HygieneSiteRead();
        var pause = options.PauseBetweenCalls < TimeSpan.Zero ? TimeSpan.Zero : options.PauseBetweenCalls;
        var pacing = (int)Math.Min(60_000, pause.TotalMilliseconds);
        for (var i = 0; i < sets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i > 0 && pause > TimeSpan.Zero) await _delay(pause, ct).ConfigureAwait(false);
            var set = sets[i];
            progress.Report($"Reading {Label(set)} ({N(i + 1)} of {N(sets.Count)}).");
            var parameters = new ConfigMgrParameters { { "Operation", "Read" }, { "Sets", new[] { set } }, { "MaxRows", MaxRows }, { "PacingMs", pacing }, { "MaxObjects", MaxObjects } };
            try
            {
                data.Merge(Parse(await _session.InvokeAsync(Script, parameters, ct, ReadTimeout).ConfigureAwait(false)));
            }
            catch (ConfigMgrSessionException ex) when (ex.Failure is ConfigMgrFailure.ScriptFailed or ConfigMgrFailure.Timeout or ConfigMgrFailure.ProcessDied or ConfigMgrFailure.Unexpected)
            {
                _log?.Warning(LogArea, $"The read of {set} failed in the ConfigMgr session: {ex.Failure}.");
                data.Tables[set] = new HygieneTable(set, []) { State = HygieneSetStates.Failed, Failure = ex.Failure.ToString() };
            }
            catch (ConfigMgrSessionException ex)
            {
                _log?.Warning(LogArea, $"The scan stopped: the ConfigMgr session failed ({ex.Failure}).");
                throw new HygieneServiceException(ComplianceService.SessionProblem(ex.Failure).Replace(ComplianceService.AccessHint, AccessHint, StringComparison.Ordinal));
            }
            if (!data.Tables.ContainsKey(set)) data.Tables[set] = new HygieneTable(set, []) { State = HygieneSetStates.Failed, Failure = "no-status" };
        }
        ct.ThrowIfCancellationRequested();

        IReadOnlyDictionary<string, string>? probes = null;
        if (options.ProbeContentSources && areas.Contains("AppRelationships") && data.IsRead(HygieneSets.DeploymentTypes) && data.IsRead(HygieneSets.AppContent))
        {
            progress.Report("Probing the content source paths.");
            probes = await ProbeAsync(data, ct).ConfigureAwait(false);
        }

        progress.Report("Running the checks.");
        var context = new HygieneContext(data, options.Thresholds ?? new HygieneThresholds(), LocalNow(), data.SiteCode)
        {
            ProbeResults = probes,
            ProbeTimeoutSeconds = (int)ProbeTimeout.TotalSeconds
        };
        var (findings, checks) = HygieneEngine.Run(context, areas, options.ProbeContentSources);
        var cut = data.Tables.Values.Where(t => t.State == HygieneSetStates.Cut).ToList();
        var failed = data.Tables.Values.Where(t => t.State == HygieneSetStates.Failed).ToList();
        var notes = new List<string>();
        if (cut.Count > 0) notes.Add("Reads cut at their cap: " + string.Join(", ", cut.Select(t => $"{Label(t.Name)} ({N(t.Rows.Count)} of {N(t.Total)})")) + ". The checks that need them did not run.");
        if (failed.Count > 0) notes.Add("Reads that failed: " + string.Join(", ", failed.Select(t => Label(t.Name))) + ". The checks that need them did not run.");
        var result = new HygieneScanResult(findings, checks, _utcNow(), cut.Count > 0, notes.Count > 0 ? string.Join(" ", notes) : null)
        {
            Areas = areas.Order(StringComparer.Ordinal).ToList(),
            QueryCount = data.Queries,
            SiteCode = data.SiteCode
        };
        _log?.Info(LogArea, $"Scan: {N(sets.Count)} reads, {N(data.Queries)} queries, {N(findings.Count)} findings, {N(result.ChecksNotRun)} checks did not run.");
        progress.Report($"Scan finished: {Count(findings.Count, "finding")}, {Count(result.ChecksNotRun, "check")} did not run.");
        return result;
    }

    internal static string Label(string set) => set switch
    {
        HygieneSets.Applications => "applications",
        HygieneSets.AppRevisions => "old application revisions",
        HygieneSets.TsReferences => "task sequence references",
        HygieneSets.TsAppDefinitions => "task sequence application definitions",
        HygieneSets.DeviceAgents => "device discovery times",
        HygieneSets.CollectionDetails => "collection schedule details",
        HygieneSets.AppDependencies => "application dependencies",
        HygieneSets.AppRelations => "application relations",
        HygieneSets.AppContent => "application content sources",
        HygieneSets.CollectionEvalFull => "full evaluation timings",
        HygieneSets.CollectionEvalIncremental => "incremental evaluation timings",
        _ => string.Concat(set.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + char.ToLowerInvariant(ch) : (i == 0 ? char.ToLowerInvariant(ch).ToString() : ch.ToString())))
    };

    private async Task<IReadOnlyDictionary<string, string>> ProbeAsync(HygieneSiteRead data, CancellationToken ct)
    {
        var paths = HygieneChecks.ContentLocations(data).Select(l => l.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Take(MaxProbePaths))
        {
            ct.ThrowIfCancellationRequested();
            string state;
            try
            {
                state = await _probe(path, ProbeTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                state = HygieneProbe.Unknown;
            }
            results[path] = state;
        }
        foreach (var path in paths.Skip(MaxProbePaths)) results[path] = HygieneProbe.NotProbed;
        return results;
    }

    /// <summary>Ok when the folder exists, Missing when it does not, Unknown when the probe did not finish in time.</summary>
    internal static async Task<string> ProbePathAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal) && !Path.IsPathFullyQualified(path)) return HygieneProbe.Missing;
        var probe = Task.Run(() => Directory.Exists(path), CancellationToken.None);
        var done = await Task.WhenAny(probe, Task.Delay(timeout, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (done != probe) return HygieneProbe.Unknown;
        return await probe.ConfigureAwait(false) ? HygieneProbe.Ok : HygieneProbe.Missing;
    }

    // ---- Session helpers ----------------------------------------------------------------------

    private HygieneSiteRead Parse(ConfigMgrCommandResult output)
    {
        try
        {
            return HygieneSiteRead.Parse(output.Output);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            _log?.Warning(LogArea, $"ConfigMgr returned Site Hygiene data that could not be read ({ex.GetType().Name}).");
            throw new HygieneServiceException("ConfigMgr returned data that could not be read. The log has the details.");
        }
    }

    private async Task<HygieneSiteRead> RunAsync(ConfigMgrParameters parameters, CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            return Parse(await _session.InvokeAsync(Script, parameters, ct, timeout).ConfigureAwait(false));
        }
        catch (ConfigMgrSessionException ex)
        {
            _log?.Warning(LogArea, $"A Site Hygiene command failed in the ConfigMgr session: {ex.Failure}.");
            throw new HygieneServiceException(ex.Failure == ConfigMgrFailure.ScriptFailed
                ? "The Site Hygiene data could not be read from ConfigMgr. The log has the kind of failure."
                : ComplianceService.SessionProblem(ex.Failure).Replace(ComplianceService.AccessHint, AccessHint, StringComparison.Ordinal));
        }
    }

    /// <summary>The local time of the clock the service runs on.</summary>
    private DateTime LocalNow() => DateTime.SpecifyKind(_utcNow(), DateTimeKind.Utc).ToLocalTime();

    internal static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    internal static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{N(n)} {noun}s";
}

/// <summary>The states of a content source probe.</summary>
public static class HygieneProbe
{
    public const string Ok = "Ok";
    public const string Missing = "Missing";
    public const string Unknown = "Unknown";
    public const string NotProbed = "NotProbed";
}
