using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

public sealed partial class SiteHygieneService
{
    /// <summary>The most objects one delete run writes; a larger plan is refused at Prepare and at Apply.</summary>
    public const int MaxDeletesPerRun = 100;

    /// <summary>The most objects one change run (settings, a rule run) writes.</summary>
    public const int MaxChangesPerRun = 200;

    /// <summary>A prepared plan older than this is refused at Apply; the operator prepares it again on fresh data.</summary>
    public static readonly TimeSpan MaxPlanAge = TimeSpan.FromMinutes(15);

    /// <summary>How long the read-back of an automatic deployment rule run waits for a new last run time.</summary>
    public static readonly TimeSpan RuleRunWindow = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan RuleRunPoll = TimeSpan.FromSeconds(5);

    /// <summary>The kinds this page writes; every other kind is report-only.</summary>
    public static readonly IReadOnlySet<HygieneFixKind> SupportedKinds = new HashSet<HygieneFixKind>
    {
        HygieneFixKind.RemoveApplication, HygieneFixKind.RemoveApplicationDeployment, HygieneFixKind.RemoveApplicationRevisionHistory, HygieneFixKind.RemovePackage,
        HygieneFixKind.RemoveCollection, HygieneFixKind.RemoveDevice, HygieneFixKind.RemoveBootImage, HygieneFixKind.RemoveDriverPackage, HygieneFixKind.RemoveAutoDeploymentRule,
        HygieneFixKind.RemoveMaintenanceWindow, HygieneFixKind.SetCollectionRefreshType, HygieneFixKind.SetApplication, HygieneFixKind.SetSiteMaintenanceTask, HygieneFixKind.RunAutoDeploymentRule
    };

    private const int MaxPrepared = 32;

    public static class CheckNames
    {
        public const string Objects = "Objects";
        public const string Cap = "Cap";
        public const string AuditLog = "AuditLog";
        public const string Dropped = "Dropped";
        public const string Kind = "Kind";
    }

    /// <param name="Connection">The connection the plan was read through; null when the service binds nothing.</param>
    /// <param name="Site">The site code for the audit records: of the connection, or of the session when the service binds nothing.</param>
    private sealed record PreparedChange(HygieneFixKind Kind, IReadOnlyDictionary<string, HygieneFinding> Findings, IReadOnlyList<HygieneTarget> Targets, HygieneThresholds Thresholds, DateTime PreparedUtc,
        ConfigMgrConnection? Connection, string Site, string Provider);

    public int MaxDeletes { get; init; } = MaxDeletesPerRun;

    public HygieneThresholds FixThresholds { get; init; } = new();

    /// <summary>The objects one audit record lists; at most <see cref="HygieneAuditLog.MaxObjectsPerRecord"/>.</summary>
    internal int AuditObjectsPerRecord { get; init; } = HygieneAuditLog.MaxObjectsPerRecord;

    public static int CapOf(HygieneFixKind kind) => HygieneFixKinds.IsDelete(kind) ? MaxDeletesPerRun : MaxChangesPerRun;

    private int Cap(HygieneFixKind kind) => HygieneFixKinds.IsDelete(kind) ? MaxDeletes : MaxChangesPerRun;

    private static HygieneObjectOutcome Skip(HygieneFinding f, string reason) => new(f, HygieneResults.Skipped, SkippedPrefix + reason);

    /// <summary>The State read of the given findings: the facts each rule needs, in one script run.</summary>
    private async Task<HygieneSiteRead> ReadStateAsync(HygieneFixKind kind, IEnumerable<HygieneFinding> findings, ConfigMgrConnection? connection, CancellationToken ct)
    {
        var keys = findings.SelectMany(f => HygieneFixRules.StateKeys(kind, f)).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var read = await RunAsync(new ConfigMgrParameters { { "Operation", "State" }, { "Kind", kind.ToString() }, { "Keys", keys }, { "MaxObjects", MaxObjects } }, connection, ct).ConfigureAwait(false);
        if (read.StateRead != true) throw new HygieneServiceException("ConfigMgr did not return the current state of the objects. Try again; the log has the kind of failure.");
        return read;
    }

    public async Task<HygieneChange> PrepareAsync(HygieneChange request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dropped = new List<HygieneObjectOutcome>();
        var wanted = new List<HygieneFinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var supported = SupportedKinds.Contains(request.Kind);
        foreach (var f in request.Findings ?? [])
        {
            if (f is null) continue;
            if (f.FixKind != request.Kind) { dropped.Add(Skip(f, "the finding has another fix than the plan.")); continue; }
            if (supported && HygieneFixRules.Gate(request.Kind, f) is { } refusal) { dropped.Add(Skip(f, refusal)); continue; }
            if (seen.Add(f.Key)) wanted.Add(f);
        }
        var cleared = request with { ChangeConfirmed = false, DeleteConfirmed = false, Targets = [], FixText = "", Fingerprint = null, PreparedUtc = null };
        if (!supported)
            return cleared with
            {
                Findings = [],
                Dropped = dropped.Concat(wanted.Select(f => Skip(f, "this page reports the finding but has no fix for it."))).ToList(),
                Impact = [],
                Checks = [new PreflightCheck(CheckNames.Kind, DeploymentCheckResults.Fail, "This kind of fix is report-only on this page. Use the guidance of the finding in the ConfigMgr console.")]
            };
        // Each finding writes at least one object, so a request over the cap is refused before the State read.
        if (wanted.Count > Cap(request.Kind))
        {
            _log?.Info(LogArea, $"Prepare {request.Kind} refused: {wanted.Count} findings over the cap.");
            return cleared with
            {
                Findings = [],
                Dropped = dropped,
                Impact = [],
                Checks = [new PreflightCheck(CheckNames.Cap, DeploymentCheckResults.Fail,
                    $"The plan holds {Count(wanted.Count, "finding")}. One run {(request.IsDelete ? "deletes" : "changes")} at most {Count(Cap(request.Kind), "object")}. Filter the findings and run them in parts. The site was not read.")]
            };
        }

        var connection = Connection();
        HygieneSiteRead site;
        try
        {
            site = wanted.Count == 0 ? new HygieneSiteRead() : await ReadStateAsync(request.Kind, wanted, connection, ct).ConfigureAwait(false);
        }
        catch (OtherConnectionException)
        {
            _log?.Warning(LogArea, $"Prepare {request.Kind} stopped: the site or SMS Provider of the settings changed during the check.");
            throw new HygieneServiceException("The findings were not checked: the settings changed to another site or SMS Provider during the check. Nothing was changed. Try again.");
        }
        var siteCode = connection?.NormalizedSiteCode is { Length: > 0 } code ? code : site.SiteCode;
        var provider = connection?.NormalizedProvider ?? "";
        var now = LocalNow();
        var kept = new List<HygieneFinding>();
        var targets = new List<HygieneTarget>();
        var commands = new List<string>();
        foreach (var f in wanted)
        {
            var e = HygieneFixRules.Evaluate(request.Kind, f, site, now, FixThresholds);
            if (!e.Holds) { dropped.Add(Skip(f, "the finding no longer holds: " + e.Reason)); continue; }
            var text = string.Join("\r\n", e.Writes.Select(w => HygieneFixText.Line(w.Command)));
            kept.Add(f with { FixText = text });
            targets.AddRange(e.Writes.Select(w => w.Line));
            commands.Add(text);
        }

        var checks = new List<PreflightCheck>();
        var cap = Cap(request.Kind);
        checks.Add(kept.Count == 0
            ? new PreflightCheck(CheckNames.Objects, DeploymentCheckResults.Fail, "No finding of the plan holds now. Scan again to see the current state.")
            : new PreflightCheck(CheckNames.Objects, DeploymentCheckResults.Pass, $"{Count(kept.Count, "finding")} {(kept.Count == 1 ? "holds" : "hold")} on fresh data; the fix writes {Count(targets.Count, "object")}."));
        checks.Add(targets.Count > cap
            ? new PreflightCheck(CheckNames.Cap, DeploymentCheckResults.Fail, $"The plan writes {Count(targets.Count, "object")}. One run {(request.IsDelete ? "deletes" : "changes")} at most {N(cap)}. Filter the findings and run them in parts.")
            : new PreflightCheck(CheckNames.Cap, DeploymentCheckResults.Pass, $"{Count(targets.Count, "object")} of at most {N(cap)} per run."));
        var probe = _audit.Probe();
        checks.Add(new PreflightCheck(CheckNames.AuditLog, probe.Writable ? DeploymentCheckResults.Pass : DeploymentCheckResults.Fail,
            probe.Writable ? probe.Message : "The fix is blocked: " + probe.Message));
        if (dropped.Count > 0)
            checks.Add(new PreflightCheck(CheckNames.Dropped, DeploymentCheckResults.Warn, $"{Count(dropped.Count, "finding")} {(dropped.Count == 1 ? "was" : "were")} left out of the plan. The list of left-out findings says why."));

        string? fingerprint = null;
        DateTime? prepared = null;
        if (kept.Count > 0 && targets.Count <= cap)
        {
            fingerprint = FingerprintOf(request.Kind, targets);
            prepared = _utcNow();
            Remember(fingerprint, new PreparedChange(request.Kind, kept.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase), targets, FixThresholds, prepared.Value, connection, siteCode, provider));
        }
        _log?.Info(LogArea, $"Prepared {request.Kind}: {kept.Count} findings kept, {targets.Count} objects, {dropped.Count} left out.");
        var impact = ImpactLines(request.Kind, kept, targets, dropped);
        if (connection is not null)
            impact.Add($"The plan is for site {HygieneSite.Text(siteCode, provider)}. The fix stops when the settings name another site or SMS Provider.");
        return cleared with
        {
            Findings = kept,
            Dropped = dropped,
            Targets = targets,
            Impact = impact,
            Checks = checks,
            FixText = string.Join("\r\n", commands),
            Fingerprint = fingerprint,
            PreparedUtc = prepared,
            SiteCode = siteCode,
            Provider = provider
        };
    }

    private static List<string> ImpactLines(HygieneFixKind kind, IReadOnlyList<HygieneFinding> kept, IReadOnlyList<HygieneTarget> targets, IReadOnlyList<HygieneObjectOutcome> dropped)
    {
        var lines = new List<string>();
        var what = Count(targets.Count, "object");
        if (HygieneFixKinds.IsDelete(kind))
        {
            lines.Add($"Delete {what}: {Verb(kind)}. A delete cannot be undone. There is no Undo.");
            lines.Add("Before each delete the page reads the object again and skips it when the finding no longer holds or something references it now.");
            if (kind is HygieneFixKind.RemoveApplication or HygieneFixKind.RemovePackage or HygieneFixKind.RemoveCollection or HygieneFixKind.RemoveBootImage or HygieneFixKind.RemoveDriverPackage or HygieneFixKind.RemoveAutoDeploymentRule)
                lines.Add("The delete command reads the references again in the same call, immediately before the delete, and does not delete an object that is in use.");
        }
        else
        {
            lines.Add($"Change {what}: {Verb(kind)}.");
            lines.Add("Before each change the page reads the object again and skips it when the finding no longer holds.");
        }
        if (kind == HygieneFixKind.SetApplication) lines.Add("Each change makes a new revision of the application.");
        if (kind == HygieneFixKind.RunAutoDeploymentRule) lines.Add("The rule runs now: it can create or change a software update group and its deployment, and download update content.");
        if (kind == HygieneFixKind.RemoveBootImage)
            lines.Add("Boot media, prestaged media and PXE images that were made from the boot image are not recorded on the site. Check that no media in use needs the boot image before you delete it.");
        if (kind == HygieneFixKind.RemoveDriverPackage)
            lines.Add("An Auto Apply Drivers step finds drivers in the driver catalog and gets their content from any driver package, without a task sequence reference. The page refuses the delete while a task sequence has an Auto Apply Drivers step.");
        if (kind == HygieneFixKind.RemoveCollection)
            lines.Add("Before each delete the page reads the deployments, collection rules and settings, automatic deployment rules, client settings deployments and administrative users that use the collection, and skips the collection when one of them exists.");
        if (kind == HygieneFixKind.RemoveMaintenanceWindow) lines.Add("The window is removed by its ID from the collection settings. Remove-CMMaintenanceWindow is not used: it removes every window whose name contains the name.");
        lines.Add("The run writes one object at a time and reads each one back. Cancel stops between objects: the object in work is finished, and the rest are skipped.");
        if (dropped.Count > 0) lines.Add($"{Count(dropped.Count, "finding")} {(dropped.Count == 1 ? "is" : "are")} left out.");
        return lines;
    }

    internal static string Verb(HygieneFixKind kind) => kind switch
    {
        HygieneFixKind.RemoveApplication => "remove the applications with every revision",
        HygieneFixKind.RemoveApplicationDeployment => "remove the application deployments",
        HygieneFixKind.RemoveApplicationRevisionHistory => "remove the old application revisions",
        HygieneFixKind.RemovePackage => "remove the packages and their content",
        HygieneFixKind.RemoveCollection => "remove the collections",
        HygieneFixKind.RemoveDevice => "remove the device records",
        HygieneFixKind.RemoveBootImage => "remove the boot images",
        HygieneFixKind.RemoveDriverPackage => "remove the driver packages",
        HygieneFixKind.RemoveAutoDeploymentRule => "remove the automatic deployment rules",
        HygieneFixKind.RemoveMaintenanceWindow => "remove the maintenance windows",
        HygieneFixKind.SetCollectionRefreshType => "set the refresh type of the collections",
        HygieneFixKind.SetApplication => "set the task sequence install setting of the applications",
        HygieneFixKind.SetSiteMaintenanceTask => "enable the site maintenance tasks",
        HygieneFixKind.RunAutoDeploymentRule => "run the automatic deployment rules",
        _ => "nothing"
    };

    /// <summary>The fingerprint of a prepared plan: the kind and each object it writes, in order.</summary>
    internal static string FingerprintOf(HygieneFixKind kind, IEnumerable<HygieneTarget> targets)
    {
        var text = new StringBuilder(kind.ToString());
        foreach (var t in targets) text.Append('\n').Append(t.FindingKey).Append('|').Append(t.ObjectType).Append('|').Append(t.ObjectKey).Append('|').Append(t.Detail);
        text.Append('\n').Append(Guid.NewGuid().ToString("N"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private void Remember(string fingerprint, PreparedChange prepared)
    {
        if (_prepared.Count >= MaxPrepared)
            foreach (var old in _prepared.OrderBy(p => p.Value.PreparedUtc).Take(_prepared.Count - MaxPrepared + 1).Select(p => p.Key).ToList())
                _prepared.TryRemove(old, out _);
        _prepared[fingerprint] = prepared;
    }
}
