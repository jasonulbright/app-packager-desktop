namespace ApplicationPackagerEngine.Services;

public enum HygieneSeverity { Info, Low, Medium, High }

/// <summary>The typed fixes of a finding. Every Remove kind deletes a site object and cannot be undone.</summary>
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

/// <param name="ObjectKey">The stable identifier of the object: CI_ID, PackageID, CollectionID, ResourceID and so on.</param>
/// <param name="FixKind">None for a finding with no safe typed fix (report only).</param>
/// <param name="FixParameters">The exact identifiers the fix acts on.</param>
/// <param name="FixText">The equivalent PowerShell of the typed fix, read-only for the operator; null for a report-only finding.</param>
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
    bool IsSuppressed);

/// <param name="State">Ran, NotRun, NeedsSiteDatabase or Failed.</param>
/// <param name="Findings">The findings of the check; meaningful only when <paramref name="State"/> is Ran.</param>
public sealed record HygieneCheckStatus(string CheckId, string Name, string Category, string State, int Findings, string? Reason);

public sealed record HygieneScanResult(IReadOnlyList<HygieneFinding> Findings, IReadOnlyList<HygieneCheckStatus> Checks, DateTime ScannedUtc, bool Truncated, string? Note);

/// <param name="Areas">The scope groups to scan, by id.</param>
/// <param name="ProbeContentSources">Lets the content source check reach file servers; off by default.</param>
public sealed record HygieneScanOptions(IReadOnlySet<string> Areas, TimeSpan PauseBetweenCalls, bool ProbeContentSources);

/// <summary>
/// One fix of one kind over a list of findings. PrepareAsync re-evaluates the check of each finding on
/// fresh data, keeps the findings that still hold, and fills Impact, Checks and the Fingerprint.
/// </summary>
/// <param name="DeleteConfirmed">The second confirmation of a delete kind; the service refuses a delete without it.</param>
public sealed record HygieneChange(
    HygieneFixKind Kind,
    IReadOnlyList<HygieneFinding> Findings,
    bool DeleteConfirmed,
    IReadOnlyList<string> Impact,
    IReadOnlyList<PreflightCheck> Checks,
    string? Fingerprint);

/// <param name="Result">Done, Unchanged, Failed or Skipped.</param>
public sealed record HygieneObjectOutcome(HygieneFinding Finding, string Result, string Message);

/// <param name="Message">Plain text without raw exception text; starts with "Skipped" when nothing was attempted.</param>
/// <param name="Final">False for a progress report while the change still runs; only a final outcome is a result.</param>
public sealed record HygieneOutcome(HygieneChange Change, bool Succeeded, string Message, IReadOnlyList<HygieneObjectOutcome> Objects, bool Final);

/// <param name="Kind">Supersedes or DependsOn.</param>
public sealed record RelationEdge(string FromKey, string FromName, string ToKey, string ToName, string Kind, string Health);

public sealed record RelationInventory(IReadOnlyList<RelationEdge> Edges, bool Truncated, string? Note);

public sealed record Suppression(string CheckId, string ObjectKey, string Comment, DateTime CreatedUtc);

public interface ISiteHygieneService
{
    Task<HygieneScanResult> ScanAsync(HygieneScanOptions options, IProgress<string> progress, CancellationToken ct);

    Task<RelationInventory> LoadRelationsAsync(CancellationToken ct);

    /// <summary>Re-evaluates the check per object on fresh data, fills Impact and Checks, and sets the Fingerprint.</summary>
    Task<HygieneChange> PrepareAsync(HygieneChange request, CancellationToken ct);

    Task<HygieneOutcome> ApplyAsync(HygieneChange change, IProgress<HygieneOutcome> progress, CancellationToken ct);
}

public interface IHygieneSuppressionStore
{
    IReadOnlyList<Suppression> Load();

    void Add(Suppression suppression);

    void Remove(string checkId, string objectKey);
}

/// <summary>The last scan result, kept between sessions for the New column and the first visit of the page.</summary>
public interface IHygieneScanStore
{
    void Save(HygieneScanResult result);

    /// <summary>The saved result; null when none was saved or the file cannot be read.</summary>
    HygieneScanResult? Load();

    /// <summary>The finding keys (check ID and object key) of the saved result.</summary>
    IReadOnlySet<string> LastKeys();
}

/// <summary>One exported view: the column names and the text of each cell, as the page shows them.</summary>
/// <param name="Truncated">The rows come from a truncated read; <paramref name="Note"/> names it in the file header.</param>
public sealed record HygieneReport(string View, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, bool Truncated, string? Note)
{
    /// <summary>The filters of the view as text; empty when no filter is set.</summary>
    public string Filter { get; init; } = "";
}

public interface IHygieneReportWriter
{
    string WriteCsv(HygieneReport report);

    string WriteHtml(HygieneReport report);

    string Folder { get; }
}

/// <param name="Event">The fix kind.</param>
/// <param name="Phase">Started or Finished.</param>
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
    public bool? Verified { get; init; }
    public bool HasChain { get; init; }
}

public sealed record HygieneAuditObject(string CheckId, string ObjectType, string ObjectKey, string ObjectName, string Result);

public interface IHygieneAuditLog
{
    AuditProbe Probe();

    AuditWrite Append(HygieneAuditRecord record);

    IReadOnlyList<HygieneAuditRecord> Read(int max);

    string Path { get; }
}

/// <summary>A plain refusal or failure of the Site Hygiene services; the message is safe to show.</summary>
public sealed class SiteHygieneServiceException(string message) : Exception(message);
