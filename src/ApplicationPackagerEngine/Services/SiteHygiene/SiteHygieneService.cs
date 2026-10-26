using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Scans a ConfigMgr site with the 57 checks of <see cref="HygieneCatalog"/> and runs their typed fixes through the
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
    private readonly Func<ConfigMgrConnection>? _connection;

    private readonly ConcurrentDictionary<string, PreparedChange> _prepared = new(StringComparer.Ordinal);

    /// <param name="connection">
    /// The site and provider the session connects to, read at each call as the session reads them. A scan
    /// and a plan capture it once; every script call then names it, and the script does nothing in a
    /// session on another site or provider. Null binds nothing.
    /// </param>
    public SiteHygieneService(
        IConfigMgrSession session,
        IHygieneAuditLog audit,
        IAppLog? log = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTime>? utcNow = null,
        Func<string, TimeSpan, CancellationToken, Task<string>>? probe = null,
        Func<ConfigMgrConnection>? connection = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _log = log;
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _probe = probe ?? ProbePathAsync;
        _connection = connection;
    }

    internal const string ConnectionChanged = "the connection changed since the plan. The settings name another site or SMS Provider now. Nothing more was changed. Prepare the plan again.";

    /// <summary>The script found the session on another site or provider than the scan or plan, and did nothing.</summary>
    private sealed class OtherConnectionException() : Exception("The ConfigMgr session is connected to another site or SMS Provider.");

    /// <summary>The connection of the settings now; null when the service binds nothing.</summary>
    private ConfigMgrConnection? Connection()
    {
        if (_connection is null) return null;
        var c = _connection();
        return new ConfigMgrConnection(HygieneSite.Code(c.SiteCode), (c.ProviderMachine ?? "").Trim());
    }

    private static bool SameConnection(ConfigMgrConnection? expected, ConfigMgrConnection? now) =>
        expected is null || (now is not null && HygieneSite.Same(expected.SiteCode, expected.ProviderMachine, now.SiteCode, now.ProviderMachine));

    /// <summary>Names the expected site and provider for the script, which refuses to run in a session on another one.</summary>
    private static ConfigMgrParameters Bound(ConfigMgrParameters parameters, ConfigMgrConnection? expected)
    {
        if (expected is null) return parameters;
        parameters["ExpectSite"] = expected.NormalizedSiteCode;
        parameters["ExpectProvider"] = expected.NormalizedProvider;
        return parameters;
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
        if (options.ProbeContentSources && areas.Contains("AppRelationships"))
            sets = sets.Append(HygieneSets.DistributionPoints).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var data = new HygieneSiteRead();
        var pause = options.PauseBetweenCalls < TimeSpan.Zero ? TimeSpan.Zero : options.PauseBetweenCalls;
        var pacing = (int)Math.Min(60_000, pause.TotalMilliseconds);
        var connection = Connection();
        for (var i = 0; i < sets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i > 0 && pause > TimeSpan.Zero) await _delay(pause, ct).ConfigureAwait(false);
            var set = sets[i];
            progress.Report($"Reading {Label(set)} ({N(i + 1)} of {N(sets.Count)}).");
            if (!SameConnection(connection, Connection())) throw ScanConnectionChanged();
            var parameters = Bound(new ConfigMgrParameters { { "Operation", "Read" }, { "Sets", new[] { set } }, { "MaxRows", MaxRows }, { "PacingMs", pacing }, { "MaxObjects", MaxObjects } }, connection);
            if (string.Equals(set, HygieneSets.TsAppDefinitions, StringComparison.OrdinalIgnoreCase)) parameters["Keys"] = StepApplications(data);
            try
            {
                var part = Parse(await _session.InvokeAsync(Script, parameters, ct, ReadTimeout).ConfigureAwait(false));
                if (part.OtherConnection) throw ScanConnectionChanged();
                data.Merge(part);
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
            probes = await ProbeAsync(data, connection, ct).ConfigureAwait(false);
        }

        progress.Report("Running the checks.");
        var context = new HygieneContext(data, options.Thresholds ?? new HygieneThresholds(), LocalNow(), data.SiteCode)
        {
            ProbeResults = probes,
            ProbeTimeoutSeconds = (int)ProbeTimeout.TotalSeconds
        };
        var (findings, checks) = HygieneEngine.Run(context, areas, options.ProbeContentSources, _log);
        var cut = data.Tables.Values.Where(t => t.State == HygieneSetStates.Cut).ToList();
        var failed = data.Tables.Values.Where(t => t.State == HygieneSetStates.Failed).ToList();
        var notes = new List<string>();
        if (cut.Count > 0) notes.Add("Reads cut at their cap: " + string.Join(", ", cut.Select(t => $"{Label(t.Name)} ({N(t.Rows.Count)} of {N(t.Total)})")) + ". The checks that need them did not run.");
        if (failed.Count > 0) notes.Add("Reads that failed: " + string.Join(", ", failed.Select(t => Label(t.Name))) + ". The checks that need them did not run.");
        var result = new HygieneScanResult(findings, checks, _utcNow(), cut.Count > 0, notes.Count > 0 ? string.Join(" ", notes) : null)
        {
            Areas = areas.Order(StringComparer.Ordinal).ToList(),
            QueryCount = data.Queries,
            SiteCode = connection?.NormalizedSiteCode is { Length: > 0 } code ? code : data.SiteCode,
            Provider = connection?.NormalizedProvider
        };
        _log?.Info(LogArea, $"Scan: {N(sets.Count)} reads, {N(data.Queries)} queries, {N(findings.Count)} findings, {N(result.ChecksNotRun)} checks did not run.");
        progress.Report($"Scan finished: {Count(findings.Count, "finding")}, {Count(result.ChecksNotRun, "check")} did not run.");
        return result;
    }

    /// <summary>The ModelNames the References of the task sequences hold, for the definition read of TSQ-03.</summary>
    internal static string[] StepApplications(HygieneSiteRead data) =>
        data.Table(HygieneSets.TsSteps).Records.Where(r => r.S("Type") == "1").Select(r => r.S("ObjectID")).Where(m => m.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();

    private HygieneServiceException ScanConnectionChanged()
    {
        _log?.Warning(LogArea, "The scan stopped: the site or SMS Provider of the settings changed during the scan.");
        return new HygieneServiceException("The scan stopped: the settings name another site or SMS Provider than at the start of the scan. Select Scan again.");
    }

    internal static string Label(string set) => set switch
    {
        HygieneSets.Applications => "applications",
        HygieneSets.AppRevisions => "old application revisions",
        HygieneSets.TsReferences => "task sequence references",
        HygieneSets.TsAppDefinitions => "task sequence application definitions",
        HygieneSets.TsSteps => "task sequence steps",
        HygieneSets.AutoApplySequences => "Auto Apply Drivers steps",
        HygieneSets.CiAssignments => "deployments by target collection",
        HygieneSets.DeviceAgents => "device discovery times",
        HygieneSets.CollectionDetails => "collection schedule details",
        HygieneSets.AppDependencies => "application dependencies",
        HygieneSets.AppRelations => "application relations",
        HygieneSets.AppContent => "application content sources",
        HygieneSets.CollectionEvalFull => "full evaluation timings",
        HygieneSets.CollectionEvalIncremental => "incremental evaluation timings",
        _ => string.Concat(set.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + char.ToLowerInvariant(ch) : (i == 0 ? char.ToLowerInvariant(ch).ToString() : ch.ToString())))
    };

    private async Task<IReadOnlyDictionary<string, string>> ProbeAsync(HygieneSiteRead data, ConfigMgrConnection? connection, CancellationToken ct)
    {
        var paths = HygieneChecks.ContentLocations(data).Select(l => l.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddProbeHost(allowedHosts, connection?.NormalizedProvider);
        if (data.IsRead(HygieneSets.DistributionPoints))
            foreach (var point in data.Table(HygieneSets.DistributionPoints).Records)
                AddProbeHost(allowedHosts, point.S("Name"));
        foreach (var path in paths.Take(MaxProbePaths))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsSafeProbePath(path, allowedHosts))
            {
                results[path] = HygieneProbe.NotProbed;
                continue;
            }
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

    internal static bool IsSafeProbePath(string path, IReadOnlySet<string> allowedHosts)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            var uncPath = path.Replace('/', '\\');
            var remainder = uncPath[2..];
            if (remainder.Length == 0 || remainder[0] is '?' or '.') return false;
            var separator = remainder.IndexOf('\\');
            if (separator <= 0 || separator == remainder.Length - 1) return false;
            var host = NormalizeProbeHost(remainder[..separator]);
            return allowedHosts.Contains(host)
                || (!host.Contains('.', StringComparison.Ordinal) && allowedHosts.Any(allowed =>
                    string.Equals(allowed.Split('.')[0], host, StringComparison.OrdinalIgnoreCase)));
        }

        return IsSafeLocalProbePath(path);
    }

    private static void AddProbeHost(HashSet<string> hosts, string? value)
    {
        var host = NormalizeProbeHost(value ?? "");
        if (host.Length > 0 && !host.Contains('\\') && !host.Contains('/') && !host.Any(char.IsWhiteSpace)) hosts.Add(host);
    }

    private static string NormalizeProbeHost(string host) => HygieneSite.Provider(host).TrimEnd('.');

    private static bool IsSafeLocalProbePath(string path)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (root is not { Length: >= 3 } || root[1] != ':') return false;
            var driveType = new DriveInfo(root).DriveType;
            if (driveType is DriveType.Network or DriveType.Unknown) return false;
            var current = root;
            foreach (var segment in fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { break; }
                catch (DirectoryNotFoundException) { break; }
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
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
            _log?.Warning(LogArea, $"ConfigMgr returned Site Hygiene data that could not be read ({ex.GetType().Name}): {ex.Message}");
            throw new HygieneServiceException("ConfigMgr returned data that could not be read. The log has the details.");
        }
    }

    /// <summary>
    /// One script call bound to <paramref name="expected"/>: refused before the call when the settings name
    /// another connection now, and by the script when the session is on another one.
    /// </summary>
    private async Task<HygieneSiteRead> RunAsync(ConfigMgrParameters parameters, ConfigMgrConnection? expected, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (!SameConnection(expected, Connection())) throw new OtherConnectionException();
        try
        {
            var read = Parse(await _session.InvokeAsync(Script, Bound(parameters, expected), ct, timeout).ConfigureAwait(false));
            if (read.OtherConnection) throw new OtherConnectionException();
            return read;
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
