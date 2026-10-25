namespace ApplicationPackagerEngine.Services;

/// <summary>The severity a check assigns.</summary>
public enum HygieneSeverity { Info, Warning, Error }

/// <summary>
/// The typed fix of a finding. <see cref="None"/> is report-only. A kind in this list that no check
/// assigns stays report-only; <see cref="HygieneFixKinds.IsDelete"/> names the kinds that delete.
/// </summary>
public enum HygieneFixKind
{
    None,
    RemoveApplication,
    RemoveApplicationDeployment,
    RemoveApplicationRevisionHistory,
    RemovePackage,
    RemoveCollection,
    RemoveDevice,
    RemoveBootImage,
    RemoveDriverPackage,
    RemoveAutoDeploymentRule,
    RemoveMaintenanceWindow,
    SetCollectionSchedule,
    SetCollectionRefreshType,
    SetApplication,
    ResumeApplication,
    SetBoundaryGroup,
    SetSiteMaintenanceTask,
    RunAutoDeploymentRule
}

public static class HygieneFixKinds
{
    /// <summary>True for a kind that deletes a site object: it needs the second confirmation and the delete cap.</summary>
    public static bool IsDelete(HygieneFixKind kind) => kind is HygieneFixKind.RemoveApplication or HygieneFixKind.RemoveApplicationDeployment
        or HygieneFixKind.RemoveApplicationRevisionHistory or HygieneFixKind.RemovePackage or HygieneFixKind.RemoveCollection or HygieneFixKind.RemoveDevice
        or HygieneFixKind.RemoveBootImage or HygieneFixKind.RemoveDriverPackage or HygieneFixKind.RemoveAutoDeploymentRule or HygieneFixKind.RemoveMaintenanceWindow;
}

/// <summary>
/// One finding of a scan. <paramref name="ObjectKey"/> is the stable identifier of the object (ModelName of
/// an application, PackageID, CollectionID, ResourceID, AssignmentID, AutoDeploymentID, "CollectionID|window
/// name", maintenance task name); it never is a display name alone. <paramref name="FixParameters"/> holds the
/// exact identifiers the typed fix uses; <paramref name="FixText"/> is the equivalent PowerShell for the
/// operator to read, generated from the same kind and parameters; null for a report-only finding, which may
/// carry guidance in <see cref="Guidance"/>.
/// </summary>
public sealed record HygieneFinding(
    string CheckId,
    string Category,
    HygieneSeverity Severity,
    string ObjectType,
    string ObjectKey,
    string ObjectName,
    string Evidence,
    string Recommendation,
    HygieneFixKind FixKind,
    IReadOnlyDictionary<string, string> FixParameters,
    string? FixText,
    bool IsNew,
    bool IsSuppressed)
{
    /// <summary>The identity of the finding for suppressions and the New column: check ID and object key, never the name.</summary>
    public string Key => KeyOf(CheckId, ObjectKey);

    public static string KeyOf(string checkId, string objectKey) => (checkId ?? "").ToUpperInvariant() + "|" + (objectKey ?? "");

    /// <summary>Console guidance of a report-only finding, as plain text; empty when none.</summary>
    public string Guidance { get; init; } = "";

    /// <summary>
    /// Why the page does not fix this finding although its check has a fix kind: the delete rule of the
    /// service refuses the object, for the reason the scan read (an automatic deployment rule targets the
    /// collection, a task sequence has an Auto Apply Drivers step). The finding is report-only then; empty
    /// for every other finding.
    /// </summary>
    public string FixRefusal { get; init; } = "";

    /// <summary>True when the finding has a typed fix the service can run.</summary>
    public bool HasFix => FixKind != HygieneFixKind.None;
}

/// <summary>The states of a check after a scan.</summary>
public static class HygieneCheckStates
{
    public const string Ran = "Ran";

    /// <summary>The check did not run: a dataset it needs failed or was cut. A check that did not run never reports "no findings".</summary>
    public const string NotRun = "NotRun";

    /// <summary>No selected area covers the check, or an option of the scan leaves it out (APP-04 with the content source probe off).</summary>
    public const string NotSelected = "NotSelected";

    /// <summary>The check reads the site database; it moves to part 2.</summary>
    public const string NeedsSiteDatabase = "NeedsSiteDatabase";

    /// <summary>The check threw while it ran; its findings are missing.</summary>
    public const string Failed = "Failed";
}

/// <param name="State">See <see cref="HygieneCheckStates"/>.</param>
/// <param name="Findings">The findings of the check; 0 only when <paramref name="State"/> is Ran.</param>
/// <param name="Reason">Why the check did not run; null when it ran.</param>
public sealed record HygieneCheckStatus(string CheckId, string Name, string Category, string State, int Findings, string? Reason)
{
    public HygieneSeverity Severity { get; init; }
}

/// <param name="Truncated">True when a read was cut at its cap; the affected checks are NotRun and <paramref name="Note"/> says which reads.</param>
public sealed record HygieneScanResult(IReadOnlyList<HygieneFinding> Findings, IReadOnlyList<HygieneCheckStatus> Checks, DateTime ScannedUtc, bool Truncated, string? Note)
{
    /// <summary>The areas the scan covered.</summary>
    public IReadOnlyList<string> Areas { get; init; } = [];

    /// <summary>The number of provider queries the scan made; 0 for a stored result.</summary>
    public int QueryCount { get; init; }

    /// <summary>True for a result read from the scan store (no site call was made).</summary>
    public bool Stored { get; init; }

    public string SiteCode { get; init; } = "";

    /// <summary>The SMS Provider the scan read from, as the settings named it; empty for the local computer, null when not recorded.</summary>
    public string? Provider { get; init; }

    /// <summary>True when the result was read from the site and provider of the connection; a result without a recorded provider belongs to none.</summary>
    public bool IsFor(string? siteCode, string? provider) =>
        Provider is not null && HygieneSite.Same(SiteCode, Provider, siteCode, provider);

    /// <summary>The checks that did not run (state NotRun, NeedsSiteDatabase or Failed).</summary>
    public int ChecksNotRun => Checks.Count(c => c.State is HygieneCheckStates.NotRun or HygieneCheckStates.NeedsSiteDatabase or HygieneCheckStates.Failed);
}

/// <summary>
/// The identity of a connection: the site code and the SMS Provider machine, compared without case,
/// surrounding blanks and a trailing backslash. An empty provider is the local computer. Two
/// connections with the same site code and another provider are different sites: the same site code
/// can exist in more than one hierarchy.
/// </summary>
public static class HygieneSite
{
    public static string Code(string? siteCode) => (siteCode ?? "").Trim().ToUpperInvariant();

    public static string Provider(string? provider) => (provider ?? "").Trim().TrimEnd('\\').Trim().ToUpperInvariant();

    public static string Key(string? siteCode, string? provider) => Code(siteCode) + "|" + Provider(provider);

    /// <summary>True when both name the same non-empty site code on the same provider.</summary>
    public static bool Same(string? siteCode, string? provider, string? otherSiteCode, string? otherProvider) =>
        Code(siteCode).Length > 0 && string.Equals(Key(siteCode, provider), Key(otherSiteCode, otherProvider), StringComparison.Ordinal);

    /// <summary>The site and provider for a message: "MCM on CM01", or "MCM on this computer".</summary>
    public static string Text(string? siteCode, string? provider) =>
        $"{(Code(siteCode).Length > 0 ? Code(siteCode) : "(not known)")} on {((provider ?? "").Trim().Length > 0 ? (provider ?? "").Trim() : "this computer")}";
}

/// <param name="Areas">Area IDs of <see cref="HygieneAreas"/>; empty selects every area.</param>
/// <param name="PauseBetweenCalls">The pause before every provider call after the first; zero sends calls back to back.</param>
/// <param name="ProbeContentSources">APP-04: probe the content source paths over SMB (bounded count and time). Off by default.</param>
public sealed record HygieneScanOptions(IReadOnlySet<string> Areas, TimeSpan PauseBetweenCalls, bool ProbeContentSources)
{
    public HygieneThresholds Thresholds { get; init; } = new();
}

/// <summary>The tunables of the checks that take one.</summary>
public sealed record HygieneThresholds
{
    public int AppUnusedMinAgeDays { get; init; } = 30;
    public int IncrementalCeiling { get; init; } = 200;
    public int DeadlineGraceDays { get; init; } = 7;
    public int FailurePctThreshold { get; init; } = 20;
    public int AvailableUnusedMinAgeDays { get; init; } = 30;
    public int InactiveDeviceDays { get; init; } = 90;
    public int SugExpiredPctThreshold { get; init; } = 30;
    public int AdrStaleDays { get; init; } = 45;
    public int ColRefDepthMax { get; init; } = 3;
    public int ColFullEvalHotSpotCount { get; init; } = 10;
    public int ContentStuckDays { get; init; } = 2;
    public long ColEvalSlowMs { get; init; } = 5000;
    public int SugMaxUpdates { get; init; } = 1000;
}

/// <summary>
/// One fix over findings of one kind. PrepareAsync re-evaluates the check of every finding on fresh site
/// data, drops the findings that no longer hold (each in <see cref="Dropped"/> with a message that starts
/// with "Skipped"), fills <paramref name="Impact"/>, <paramref name="Checks"/> and <paramref name="Fingerprint"/>.
/// ApplyAsync runs only a change a Prepare of this service returned, once, within the plan age limit, and
/// re-evaluates each finding again immediately before its write.
/// </summary>
/// <param name="DeleteConfirmed">The second confirmation of a delete kind, which names the object count and says the delete cannot be undone. The service refuses a delete without it.</param>
public sealed record HygieneChange(
    HygieneFixKind Kind,
    IReadOnlyList<HygieneFinding> Findings,
    bool DeleteConfirmed,
    IReadOnlyList<string> Impact,
    IReadOnlyList<PreflightCheck> Checks,
    string? Fingerprint)
{
    /// <summary>True when a check failed or could not be made: the change does not run.</summary>
    public bool Blocked => Checks.Any(c => c.Result is DeploymentCheckResults.Fail or DeploymentCheckResults.Unknown);

    public bool IsDelete => HygieneFixKinds.IsDelete(Kind);

    /// <summary>The operator confirmed the plan. ApplyAsync refuses every change without it; PrepareAsync clears it.</summary>
    public bool ChangeConfirmed { get; init; }

    /// <summary>The findings PrepareAsync left out, each with a message that starts with "Skipped".</summary>
    public IReadOnlyList<HygieneObjectOutcome> Dropped { get; init; } = [];

    /// <summary>The objects the change writes, one line per object, read at Prepare (for example each deployment of an application).</summary>
    public IReadOnlyList<HygieneTarget> Targets { get; init; } = [];

    /// <summary>The equivalent PowerShell of the prepared change, read-only, in the order the service runs it.</summary>
    public string FixText { get; init; } = "";

    public DateTime? PreparedUtc { get; init; }

    /// <summary>The site code of the connection PrepareAsync read; ApplyAsync writes only through the same site and provider.</summary>
    public string SiteCode { get; init; } = "";

    /// <summary>The SMS Provider of the connection PrepareAsync read; empty for the local computer.</summary>
    public string Provider { get; init; } = "";
}

/// <summary>One object a prepared change writes. <paramref name="FindingKey"/> names the finding it belongs to.</summary>
public sealed record HygieneTarget(string FindingKey, string ObjectType, string ObjectKey, string ObjectName, string Detail);

/// <summary>The result of one object a finding writes.</summary>
/// <param name="Result">Done, Unchanged, Failed or Skipped; see <see cref="HygieneResults"/>.</param>
/// <param name="Message">Plain text without raw exception text; starts with "Skipped" when nothing was attempted.</param>
public sealed record HygieneTargetOutcome(HygieneTarget Target, string Result, string Message);

/// <param name="Result">Done, Unchanged, Failed, Skipped, or Partial when the objects of the finding have different results; see <see cref="HygieneResults"/>.</param>
/// <param name="Message">Plain text without raw exception text; starts with "Skipped" when nothing was attempted.</param>
public sealed record HygieneObjectOutcome(HygieneFinding Finding, string Result, string Message)
{
    /// <summary>The result of each object of the plan the finding writes, in plan order; empty when the run named no object.</summary>
    public IReadOnlyList<HygieneTargetOutcome> Targets { get; init; } = [];
}

public static class HygieneResults
{
    /// <summary>The write ran and the read-back shows the expected result.</summary>
    public const string Done = "Done";

    /// <summary>The write ran and the read-back shows the object as before.</summary>
    public const string Unchanged = "Unchanged";

    /// <summary>The write failed, or it ran and its read-back could not be made, so the result is not known.</summary>
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";

    /// <summary>A finding whose objects have different results; <see cref="HygieneObjectOutcome.Targets"/> holds each one.</summary>
    public const string Partial = "Partial";
}

/// <param name="Message">Starts with "Skipped" when nothing was attempted.</param>
/// <param name="Final">False for a progress report after an object while the change runs; true for the last report.</param>
public sealed record HygieneOutcome(HygieneChange Change, bool Succeeded, string Message, IReadOnlyList<HygieneObjectOutcome> Objects, bool Final)
{
    /// <summary>
    /// True when the run wrote a Started record but the audit log has no Finished record for it. The site
    /// results stay as reported; <paramref name="Message"/> names the audit log file.
    /// </summary>
    public bool AuditIncomplete { get; init; }

    /// <summary>The audit log file the Finished record is missing from; empty when <see cref="AuditIncomplete"/> is false.</summary>
    public string AuditPath { get; init; } = "";
}

/// <param name="Kind">Supersedes or DependsOn; see <see cref="RelationKinds"/>.</param>
/// <param name="FromKey">The ModelName of the source application.</param>
/// <param name="ToKey">The ModelName the rule references; the target may not exist.</param>
/// <param name="Health">Healthy, Orphaned, Expired Target, Disabled Source, Disabled Target, Missing Content or Circular.</param>
public sealed record RelationEdge(string FromKey, string FromName, string ToKey, string ToName, string Kind, string Health)
{
    public string FromDeploymentType { get; init; } = "";
    public string FromVersion { get; init; } = "";
    public string ToVersion { get; init; } = "";

    /// <summary>Dependencies: Required, Optional or AppDependence; empty for supersedence.</summary>
    public string DependencyState { get; init; } = "";

    public bool ToExists { get; init; } = true;
    public bool Circular { get; init; }

    /// <summary>Supersedence: the number of further supersedence hops below the target; a loop stops the count.</summary>
    public int ChainDepth { get; init; }
}

public static class RelationKinds
{
    public const string Supersedes = "Supersedes";
    public const string DependsOn = "DependsOn";
}

public static class RelationHealth
{
    public const string Healthy = "Healthy";
    public const string Orphaned = "Orphaned";
    public const string ExpiredTarget = "Expired Target";
    public const string DisabledSource = "Disabled Source";
    public const string DisabledTarget = "Disabled Target";
    public const string MissingContent = "Missing Content";
    public const string Circular = "Circular";
}

/// <param name="Truncated">True when the application read was cut at its cap; <paramref name="Note"/> then says so.</param>
public sealed record RelationInventory(IReadOnlyList<RelationEdge> Edges, bool Truncated, string? Note)
{
    /// <summary>The applications the edges name, by ModelName: display name, version and health flags.</summary>
    public IReadOnlyDictionary<string, RelationApp> Apps { get; init; } = new Dictionary<string, RelationApp>();
}

public sealed record RelationApp(string ModelName, string Name, string Version, bool IsEnabled, bool IsExpired, bool HasContent)
{
    /// <summary>Healthy for an enabled current application with content; otherwise the reason.</summary>
    public string Health => IsExpired ? "Retired" : !IsEnabled ? "Disabled" : !HasContent ? "No content" : RelationHealth.Healthy;
}

/// <summary>A suppression by check ID and object key, never by display name, for one site or for every site.</summary>
public sealed record Suppression(string CheckId, string ObjectKey, string Comment, DateTime CreatedUtc)
{
    public string Key => HygieneFinding.KeyOf(CheckId, ObjectKey);

    /// <summary>The site code the suppression is for; empty for every site (the suppressions of a file from an earlier version).</summary>
    public string SiteCode { get; init; } = "";

    /// <summary>
    /// The SMS Provider of the site the suppression is for; empty for the local computer. Null for a
    /// suppression without a recorded provider: it holds on the site through any provider.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>The identity in the store: the site code, the provider, the check ID and the object key.</summary>
    public string StoreKey => HygieneSite.Code(SiteCode) + "|" + (Provider is null ? "*" : HygieneSite.Provider(Provider)) + "|" + Key;

    /// <summary>
    /// True when the suppression holds on the site and provider; an empty <paramref name="siteCode"/> matches
    /// every suppression, and a null <paramref name="provider"/> matches every provider of the site.
    /// </summary>
    public bool AppliesTo(string siteCode, string? provider = null)
    {
        if (string.IsNullOrEmpty(SiteCode) || string.IsNullOrEmpty(siteCode)) return true;
        if (!string.Equals(HygieneSite.Code(SiteCode), HygieneSite.Code(siteCode), StringComparison.Ordinal)) return false;
        return Provider is null || provider is null || string.Equals(HygieneSite.Provider(Provider), HygieneSite.Provider(provider), StringComparison.Ordinal);
    }
}

public interface ISiteHygieneService
{
    /// <summary>Bulk reads for the selected areas, then the checks in memory. Cancel takes effect between reads.</summary>
    Task<HygieneScanResult> ScanAsync(HygieneScanOptions options, IProgress<string> progress, CancellationToken ct);

    /// <summary>Every supersedence and dependency edge with its health.</summary>
    Task<RelationInventory> LoadRelationsAsync(CancellationToken ct);

    /// <summary>Re-evaluates the check per finding on fresh data, fills Impact, Checks, Targets, FixText and the Fingerprint.</summary>
    Task<HygieneChange> PrepareAsync(HygieneChange request, CancellationToken ct);

    /// <summary>
    /// Runs a prepared, confirmed change one object at a time: the audit record before the first write (a
    /// failed write blocks the run), a fresh re-evaluation before each write, a read-back after it. Cancel
    /// stops between objects; the objects not attempted are Skipped.
    /// </summary>
    Task<HygieneOutcome> ApplyAsync(HygieneChange change, IProgress<HygieneOutcome> progress, CancellationToken ct);
}

public interface IHygieneSuppressionStore
{
    /// <summary>The suppressions of every site; empty when the file is missing or cannot be read.</summary>
    IReadOnlyList<Suppression> Load();

    /// <summary>Adds or replaces the suppression of the same site code, check ID and object key.</summary>
    void Add(Suppression suppression);

    /// <summary>
    /// Adds or replaces each suppression with one read and one write of the file. Throws
    /// <see cref="HygieneServiceException"/> and changes nothing when the file exists but cannot be read.
    /// </summary>
    void AddRange(IReadOnlyList<Suppression> suppressions);

    /// <summary>Removes the suppressions of the check ID and object key on every site.</summary>
    void Remove(string checkId, string objectKey);

    /// <summary>
    /// Removes each suppression of the keys that holds on the site and provider (its own, those without a
    /// recorded provider and the every-site ones) with one read and one write of the file; a null
    /// <paramref name="provider"/> matches every provider of the site. Throws
    /// <see cref="HygieneServiceException"/> and changes nothing when the file exists but cannot be read.
    /// </summary>
    void RemoveRange(string siteCode, IReadOnlyList<(string CheckId, string ObjectKey)> keys, string? provider = null);

    string Path { get; }
}

public interface IHygieneScanStore
{
    /// <summary>
    /// Writes the result as the last scan (atomic replace); throws <see cref="HygieneServiceException"/> when
    /// it cannot, and when the file would be larger than a read takes.
    /// </summary>
    void Save(HygieneScanResult result);

    /// <summary>The last scan with <see cref="HygieneScanResult.Stored"/> true; null when absent or unreadable.</summary>
    HygieneScanResult? Load();

    /// <summary>The finding keys of the last scan; null when there is no last scan (every finding then reads as not new).</summary>
    IReadOnlySet<string>? Keys();

    string Path { get; }
}

/// <summary>One exported view: the column names and the text of each cell, as the page shows them.</summary>
/// <param name="View">Findings, Inventory, Summary or Check status.</param>
/// <param name="Truncated">The rows come from a truncated scan; the writer refuses it without a <paramref name="Note"/>.</param>
public sealed record HygieneReport(string View, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, bool Truncated, string? Note)
{
    public string Filter { get; init; } = "";

    /// <summary>The checks that did not run, as "ID: reason"; each is a header line of the file.</summary>
    public IReadOnlyList<string> ChecksNotRun { get; init; } = [];

    public DateTime? ScannedUtc { get; init; }
}

public interface IHygieneReportWriter
{
    string WriteCsv(HygieneReport report);
    string WriteHtml(HygieneReport report);
    string Folder { get; }
}

/// <summary>The phases of the Site Hygiene audit records.</summary>
public static class HygieneAuditPhases
{
    /// <summary>Exactly one per run, before the first write; it lists the first planned objects.</summary>
    public const string Started = "Started";

    /// <summary>A continuation that lists more objects of the same run ID: planned objects before the first write, results before the Finished record.</summary>
    public const string Objects = "Objects";

    /// <summary>Exactly one per run that has a Started record, after the last object.</summary>
    public const string Finished = "Finished";
}

/// <summary>
/// One line of the Site Hygiene audit log. A fix writes exactly one Started record before the first write
/// (a failed write blocks the run) and exactly one Finished record after the last object; a run with more
/// objects than one record holds lists the rest in records of phase Objects with the same run ID.
/// </summary>
/// <param name="Event">The fix kind.</param>
/// <param name="Phase">See <see cref="HygieneAuditPhases"/>.</param>
/// <param name="Result">Started and planned Objects: empty. Finished and result Objects: Succeeded, Partial, Failed or Skipped.</param>
public sealed record HygieneAuditRecord(
    DateTime Timestamp,
    string User,
    string Event,
    string Phase,
    string Result,
    string RunId,
    IReadOnlyList<HygieneAuditObject> Objects,
    string Message)
{
    public string Source { get; init; } = "";

    /// <summary>The site code the run wrote to.</summary>
    public string Site { get; init; } = "";

    /// <summary>The SMS Provider the run wrote through, as the settings named it; empty for the local computer.</summary>
    public string Provider { get; init; } = "";

    /// <summary>True, false or null as <see cref="DeploymentAuditRecord.Verified"/>: the hash chain of the line.</summary>
    public bool? Verified { get; init; }

    public bool HasChain { get; init; }
    public bool AfterChainStart { get; init; }
    public long? ChainBrokenAt { get; init; }
}

/// <param name="Result">Empty in a Started record.</param>
public sealed record HygieneAuditObject(string CheckId, string ObjectType, string ObjectKey, string ObjectName, string Result);

public interface IHygieneAuditLog
{
    AuditProbe Probe();
    AuditWrite Append(HygieneAuditRecord r);

    /// <summary>The last <paramref name="max"/> records, oldest first; a line that cannot be read is skipped.</summary>
    IReadOnlyList<HygieneAuditRecord> Read(int max);

    string Path { get; }

    bool OlderRecordsDropped => false;
}

/// <summary>The selectable scan areas.</summary>
public static class HygieneAreas
{
    public sealed record Area(string Id, string Title);

    public static readonly IReadOnlyList<Area> All =
    [
        new("Applications", "Applications"),
        new("AppRelationships", "Application relationships and content paths"),
        new("Content", "Content distribution"),
        new("Packages", "Packages"),
        new("Collections", "Collections"),
        new("CollectionSchedules", "Collection evaluation schedules"),
        new("Deployments", "Deployments"),
        new("Devices", "Devices"),
        new("Boundaries", "Boundaries"),
        new("TaskSequences", "Task sequences"),
        new("Updates", "Software updates"),
        new("UpdateContent", "Software update package content"),
        new("DistributionPoints", "Distribution points"),
        new("Compliance", "Compliance and client settings"),
        new("Drivers", "Drivers"),
        new("Security", "Administrative users"),
        new("MaintenanceWindows", "Maintenance windows"),
        new("Site", "Site maintenance")
    ];
}

/// <summary>The 57 checks with their ID, category, severity and title.</summary>
public static class HygieneCatalog
{
    public sealed record Check(string Id, string Category, HygieneSeverity Severity, string Title);

    private const HygieneSeverity E = HygieneSeverity.Error, W = HygieneSeverity.Warning, I = HygieneSeverity.Info;

    public static readonly IReadOnlyList<Check> Checks =
    [
        new("APP-01", "Applications", W, "Application with no deployments or references"),
        new("APP-02", "Applications", E, "Retired application still deployed"),
        new("APP-03", "Applications", W, "Superseded application still deployed"),
        new("APP-04", "Applications", W, "Deployment type content source missing or unreachable"),
        new("APP-05", "Applications", I, "Application with old revisions"),
        new("PKG-01", "Packages", W, "Package with no programs and no references"),
        new("CNT-01", "Content", W, "Content failed on one or more distribution points"),
        new("CNT-02", "Content", I, "Content distribution in progress beyond threshold"),
        new("CNT-03", "Content", E, "Deployed content on no distribution point"),
        new("SUP-01", "Relationships", E, "Supersedence referencing a deleted application"),
        new("SUP-02", "Relationships", E, "Circular supersedence chain"),
        new("SUP-03", "Relationships", W, "Superseding application disabled"),
        new("SUP-04", "Relationships", W, "Supersedence target retired or expired"),
        new("DEP-01", "Relationships", E, "Dependency referencing a deleted application"),
        new("DEP-02", "Relationships", E, "Circular dependency"),
        new("DEP-03", "Relationships", W, "Dependency target disabled"),
        new("DEP-04", "Relationships", W, "Dependency target retired or expired"),
        new("DEP-05", "Relationships", I, "Dependency target reports no packaged content"),
        new("REL-01", "Relationships", I, "Application relationships without manufacturer metadata"),
        new("DEV-01", "Devices", W, "Inactive devices beyond threshold"),
        new("DEV-02", "Devices", W, "Duplicate device records"),
        new("DEV-03", "Devices", I, "Clients below the newest client version"),
        new("BND-01", "Boundaries", W, "Boundary in no boundary group"),
        new("BND-02", "Boundaries", W, "Boundary group with no site systems"),
        new("BND-03", "Boundaries", I, "Overlapping IP-range boundaries"),
        new("TSQ-01", "Task Sequences", E, "Task sequence referencing deleted content"),
        new("TSQ-02", "Task Sequences", W, "Boot image or driver package referenced by nothing"),
        new("TSQ-03", "Task Sequences", W, "Application in a task sequence without the task sequence install setting"),
        new("TSQ-04", "Task Sequences", E, "Application in a task sequence with content on no distribution point"),
        new("UPD-01", "Updates", W, "Update group with high expired-update ratio"),
        new("UPD-03", "Updates", W, "Automatic deployment rule disabled, stale, or erroring"),
        new("MNT-01", "Site", I, "Recommended maintenance tasks disabled"),
        new("MNT-02", "Site", W, "Site backup task disabled"),
        new("COL-01", "Collections", I, "Empty collection nothing references"),
        new("COL-02", "Collections", W, "Deployment targeting an empty collection"),
        new("COL-03", "Collections", W, "Incremental-evaluation collection count over ceiling"),
        new("COL-04", "Collections", W, "Collection evaluation run time over threshold"),
        new("COL-05", "Collections", W, "Sub-daily full evaluation on an incremental collection"),
        new("COL-06", "Collections", I, "Scheduled evaluation on a direct-rule-only collection"),
        new("COL-07", "Collections", W, "Include/exclude reference chain over depth threshold"),
        new("COL-08", "Collections", E, "Circular collection reference"),
        new("COL-09", "Collections", I, "Full-update start-time hot spot"),
        new("DPL-01", "Deployments", I, "Deployment past its expiration time"),
        new("DPL-02", "Deployments", E, "Required deployment past deadline with high failures"),
        new("COL-10", "Collections", I, "Expired one-time maintenance window"),
        new("DPL-03", "Deployments", I, "Available deployment with no takers"),
        new("DPL-04", "Deployments", W, "Required deployment to a built-in all-resources collection"),
        new("DPL-05", "Deployments", W, "Deployment of a disabled task sequence or program"),
        new("UPD-04", "Updates", W, "Update group over the per-deployment update limit"),
        new("UPD-05", "Updates", I, "Deployment package holding expired update content"),
        new("DPT-01", "Distribution Points", W, "Distribution point in no boundary group"),
        new("DPT-02", "Distribution Points", I, "Distribution point group with no members"),
        new("CFG-01", "Compliance", I, "Configuration baseline deployed nowhere"),
        new("CFG-02", "Compliance", I, "Configuration item in no baseline"),
        new("CFG-03", "Compliance", I, "Custom client settings deployed to no collection"),
        new("DRV-01", "Drivers", I, "Driver in no driver package or boot image"),
        new("SEC-01", "Security", W, "Administrative user with a deleted directory account")
    ];
}

/// <summary>A plain refusal or failure of the Site Hygiene services; the message is safe to show.</summary>
public sealed class HygieneServiceException(string message) : Exception(message);
