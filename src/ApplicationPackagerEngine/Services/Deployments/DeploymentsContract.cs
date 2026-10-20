using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApplicationPackagerEngine.Services;

public enum DeploymentObjectType { Application, Package, TaskSequence, SoftwareUpdateGroup }

/// <summary>
/// An object that can be deployed. <paramref name="Id"/> is the CI_ID of an application or a
/// software update group and the PackageID of a package or a task sequence.
/// </summary>
/// <param name="Program">The program of a package; empty for the other types.</param>
/// <param name="PackagedByProduct">True for an application this product published to the site.</param>
/// <param name="ModelId">The ModelID of an application, the same for all its revisions; empty for the other types.</param>
/// <param name="UpdateCount">The number of updates of a software update group.</param>
public sealed record DeployableObject(
    DeploymentObjectType Type,
    string Id,
    string Name,
    string Version,
    string Program,
    bool PackagedByProduct,
    string ModelId = "",
    int UpdateCount = 0);

/// <summary>A device collection that is not built in.</summary>
public sealed record DeploymentCollection(string CollectionId, string Name, string FolderPath, int MemberCount);

/// <param name="Status">Distributed, InProgress, NotTargeted, Failed or Unknown.</param>
public sealed record DpGroupInfo(string Name, string Id, string Status, int Targeted, int Success, int InProgress, int Error);

/// <summary>
/// The settings of one deployment. Times are wall-clock values in the time basis:
/// UTC when <paramref name="UtcBasis"/> is true, the local time of the clients otherwise.
/// </summary>
/// <param name="Purpose">Available or Required.</param>
/// <param name="UserNotification">DisplayAll, DisplaySoftwareCenterOnly or HideAll (application, software update group).</param>
/// <param name="NetworkBehavior">
/// Package: the fast network option (DownloadContentFromDistributionPointAndRunLocally or RunProgramFromDistributionPoint).
/// Task sequence: the deployment option (DownloadContentLocallyWhenNeededByRunningTaskSequence,
/// DownloadAllContentLocallyBeforeStartingTaskSequence or RunFromDistributionPoint).
/// </param>
/// <param name="RerunBehavior">NeverRerunDeployedProgram, AlwaysRerunProgram, RerunIfFailedPreviousAttempt or RerunIfSucceededOnPreviousAttempt (Required only).</param>
/// <param name="SlowNetworkBehavior">Package: DoNotRunProgram or DownloadContentFromDistributionPointAndLocally.</param>
/// <param name="TaskSequenceAvailability">Clients, ClientsMediaAndPxe, MediaAndPxe or MediaAndPxeHidden.</param>
/// <param name="AllowMicrosoftUpdate">Software update group: download from Microsoft Update when no distribution point has the content.</param>
public sealed record DeploymentOptions(
    string Purpose,
    DateTime AvailableTime,
    DateTime? Deadline,
    bool UtcBasis,
    string UserNotification,
    bool OverrideServiceWindow,
    bool RebootOutsideServiceWindow,
    bool AllowMetered,
    string NetworkBehavior,
    string RerunBehavior,
    bool ShowProgress,
    bool AllowFallback,
    bool FallbackToDp,
    bool PostRebootScan,
    bool SuppressRestart,
    string SlowNetworkBehavior = "DoNotRunProgram",
    string TaskSequenceAvailability = "Clients",
    bool AllowMicrosoftUpdate = false);

/// <param name="Name">TargetExists, ContentDistributed, CollectionValid, CollectionSafe, NoDuplicate, and also Schedule, AuditLog and Signing.</param>
/// <param name="Result">Pass, Fail, Warn or Unknown. Fail and Unknown block the change.</param>
public sealed record PreflightCheck(string Name, string Result, string Message);

/// <summary>
/// A deployment as ConfigMgr stores it. <paramref name="DeploymentId"/> is the AssignmentUniqueID
/// of an application or software update group deployment and the AdvertisementID of a package
/// or task sequence deployment. Times are the stored wall-clock values in the deployment's time basis.
/// </summary>
/// <param name="AssignmentId">The numeric AssignmentID of an application or software update group deployment; empty for the other types.</param>
public sealed record DeploymentRecord(
    string DeploymentId,
    DeploymentObjectType Type,
    string ObjectId,
    string ObjectName,
    string CollectionId,
    string CollectionName,
    string Purpose,
    DateTime AvailableTime,
    DateTime? Deadline,
    DateTime Created,
    string CreatedBy,
    bool UtcBasis = false,
    string Program = "",
    string AssignmentId = "");

public enum DeploymentChangeKind { Create, Distribute, RemoveDeployment }

/// <summary>
/// One change of a plan. PrepareAsync fills <paramref name="Checks"/> and <paramref name="Impact"/>;
/// ApplyAsync runs only what the plan names.
/// </summary>
/// <param name="DpGroups">Distribute: the DP group names. Create: groups to distribute to before the create, in the same change.</param>
/// <param name="CreateNowContentDistributing">Create the deployment although the content is still distributing.</param>
/// <param name="RequiredConfirmed">The second confirmation for a Required deployment.</param>
/// <param name="BackdatedAcknowledged">The confirmation for an available time in the past.</param>
/// <param name="Existing">RemoveDeployment: the captured deployment.</param>
/// <param name="CreatedId">Filled by the run; used by Undo.</param>
public sealed record DeploymentChange(
    DeploymentChangeKind Kind,
    DeployableObject? Object,
    DeploymentCollection? Collection,
    DeploymentOptions? Options,
    IReadOnlyList<string> DpGroups,
    bool CreateNowContentDistributing,
    bool RequiredConfirmed,
    bool BackdatedAcknowledged,
    DeploymentRecord? Existing,
    IReadOnlyList<PreflightCheck> Checks,
    IReadOnlyList<string> Impact,
    string? CreatedId)
{
    /// <summary>True when a check failed or could not be made: the change does not run.</summary>
    public bool Blocked => Checks.Any(c => c.Result is DeploymentCheckResults.Fail or DeploymentCheckResults.Unknown);

    /// <summary>True when the available time lies in the past and needs <see cref="BackdatedAcknowledged"/>.</summary>
    public bool NeedsBackdatedAcknowledgement { get; init; }

    /// <summary>True for a Required create: it needs <see cref="RequiredConfirmed"/>.</summary>
    public bool NeedsRequiredConfirmation => Kind == DeploymentChangeKind.Create && Options?.Purpose == DeploymentPurposes.Required;
}

/// <param name="Message">Plain text without raw exception text; starts with "Skipped" when the change was not attempted.</param>
/// <param name="UndoChange">The RemoveDeployment that undoes a create; null for every other change.</param>
public sealed record DeploymentOutcome(DeploymentChange Change, bool Succeeded, string Message, DeploymentChange? UndoChange, IReadOnlyList<string> Steps);

public static class DeploymentCheckResults
{
    public const string Pass = "Pass";
    public const string Fail = "Fail";
    public const string Warn = "Warn";
    public const string Unknown = "Unknown";
}

public static class DeploymentCheckNames
{
    public const string TargetExists = "TargetExists";
    public const string ContentDistributed = "ContentDistributed";
    public const string CollectionValid = "CollectionValid";
    public const string CollectionSafe = "CollectionSafe";
    public const string NoDuplicate = "NoDuplicate";
    public const string Schedule = "Schedule";
    public const string AuditLog = "AuditLog";
    public const string Signing = "Signing";
}

public static class DeploymentPurposes
{
    public const string Available = "Available";
    public const string Required = "Required";
}

public interface IDeploymentService
{
    Task<IReadOnlyList<DeployableObject>> ListObjectsAsync(DeploymentObjectType type, CancellationToken ct);
    Task<IReadOnlyList<string>> ListProgramsAsync(string packageId, CancellationToken ct);

    /// <summary>Device collections that are not built in.</summary>
    Task<IReadOnlyList<DeploymentCollection>> ListCollectionsAsync(CancellationToken ct);
    Task<IReadOnlyList<DpGroupInfo>> ListDpGroupsAsync(DeployableObject obj, CancellationToken ct);

    /// <summary>The existing deployments of the object.</summary>
    Task<IReadOnlyList<DeploymentRecord>> ListDeploymentsAsync(DeployableObject obj, CancellationToken ct);

    /// <summary>Reads the current state, runs the five checks and the schedule rules, and fills Checks and Impact.</summary>
    Task<DeploymentChange> PrepareAsync(DeploymentChange request, CancellationToken ct);

    /// <summary>
    /// Runs the changes in order. Cancellation takes effect between changes; the rest are reported
    /// as skipped. A change that ConfigMgr made carries its undo.
    /// </summary>
    Task<IReadOnlyList<DeploymentOutcome>> ApplyAsync(IReadOnlyList<DeploymentChange> changes, IProgress<DeploymentOutcome> progress, CancellationToken ct);
}

/// <param name="Writable">True when an append can run now.</param>
public sealed record AuditProbe(bool Writable, string Message);

public sealed record AuditWrite(bool Written, string Message);

/// <summary>
/// One line of the audit log. The property names are those the suite tool writes, so records of
/// both tools can share one file; the properties after <see cref="Result"/> are written by this
/// product only. Properties a record carries that this type does not name are kept in <see cref="Extra"/>.
/// </summary>
public sealed class DeploymentAuditRecord
{
    public string? Timestamp { get; set; }
    public string? User { get; set; }
    public string? DeploymentType { get; set; }
    public string? ApplicationName { get; set; }
    public string? ApplicationVersion { get; set; }
    public string? CollectionName { get; set; }
    public string? CollectionID { get; set; }
    public JsonElement? MemberCount { get; set; }
    public string? DeployPurpose { get; set; }
    public string? DeployAction { get; set; }
    public string? DeadlineDateTime { get; set; }
    public JsonElement? DeploymentID { get; set; }
    public string? Result { get; set; }

    /// <summary>Create, Distribute or RemoveDeployment.</summary>
    public string? Event { get; set; }
    public string? Source { get; set; }
    public string? DeploymentUniqueID { get; set; }
    public string? ObjectID { get; set; }
    public string? ProgramName { get; set; }
    public string? AvailableDateTime { get; set; }
    public string? TimeBasedOn { get; set; }
    public string? DistributionPointGroups { get; set; }

    /// <summary>RemoveDeployment: the DeploymentUniqueID of the create it undoes.</summary>
    public string? Undoes { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public interface IDeploymentAuditLog
{
    AuditProbe Probe();
    AuditWrite Append(DeploymentAuditRecord r);

    /// <summary>The last <paramref name="max"/> records, oldest first; a line that cannot be read is skipped.</summary>
    IReadOnlyList<DeploymentAuditRecord> Read(int max);
    string Path { get; }
}

/// <summary>
/// A deployment template. The JSON property names are those of the suite tool's template files;
/// nothing about collections or objects is stored.
/// </summary>
public sealed record DeploymentTemplate(
    string Id,
    string Name,
    string Purpose,
    string UserNotification,
    bool UtcBasis,
    bool OverrideServiceWindow,
    bool RebootOutsideServiceWindow,
    bool AllowMetered,
    bool AllowBoundaryFallback,
    bool AllowMicrosoftUpdate,
    bool RequirePostRebootFullScan,
    int DefaultDeadlineOffsetHours,
    bool IsSeeded);

public interface IDeploymentTemplateStore
{
    IReadOnlyList<DeploymentTemplate> All();
    void Save(DeploymentTemplate t);
    void Delete(string id);

    /// <summary>A plain note about the last read, such as a file that could not be read; null when there is none.</summary>
    string? Message { get; }
}

public interface IDeploymentReportWriter
{
    string WriteCsv(IReadOnlyList<DeploymentAuditRecord> rows);
    string WriteHtml(IReadOnlyList<DeploymentAuditRecord> rows);
}

/// <summary>A refusal or failure with a plain message for the page.</summary>
public sealed class DeploymentServiceException(string message) : Exception(message);
