namespace ApplicationPackagerEngine.Services;

public enum DeploymentObjectType
{
    Application,
    Package,
    TaskSequence,
    SoftwareUpdateGroup
}

/// <param name="Id">CI_ID of an application or a software update group; PackageID of a package or a task sequence.</param>
/// <param name="Program">The program of a package; empty for the other types.</param>
/// <param name="PackagedByProduct">True for an application this product packaged; the signing rule applies to it.</param>
public sealed record DeployableObject(DeploymentObjectType Type, string Id, string Name, string Version, string Program, bool PackagedByProduct);

/// <summary>A device collection; the service excludes built-in collections.</summary>
public sealed record DeploymentCollection(string CollectionId, string Name, string FolderPath, int MemberCount);

/// <param name="Status">Distributed, InProgress, NotTargeted, Failed or Unknown.</param>
public sealed record DpGroupInfo(string Name, string Id, string Status, int Targeted, int Success, int InProgress, int Error);

/// <param name="Purpose">Available or Required.</param>
/// <param name="UserNotification">DisplayAll, DisplaySoftwareCenterOnly or HideAll.</param>
/// <param name="OverrideServiceWindow">Application, task sequence and software update group.</param>
/// <param name="NetworkBehavior">Package and task sequence.</param>
/// <param name="FallbackToDp">Software update group.</param>
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
    bool SuppressRestart);

/// <param name="Name">TargetExists, ContentDistributed, CollectionValid, CollectionSafe or NoDuplicate.</param>
/// <param name="Result">Pass, Fail, Warn or Unknown.</param>
public sealed record PreflightCheck(string Name, string Result, string Message);

/// <param name="DeploymentId">AssignmentUniqueID of an application or a software update group deployment; AdvertisementID of a package or a task sequence deployment.</param>
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
    string CreatedBy);

public enum DeploymentChangeKind
{
    Create,
    Distribute,
    RemoveDeployment
}

/// <param name="Existing">For RemoveDeployment: the captured deployment.</param>
/// <param name="Impact">Plain lines that PrepareAsync fills.</param>
/// <param name="CreatedId">The deployment ID a create read back; the undo uses it.</param>
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
    string? CreatedId);

/// <param name="Message">Plain text without raw exception text; starts with "Skipped" when the change was not attempted.</param>
public sealed record DeploymentOutcome(DeploymentChange Change, bool Succeeded, string Message, DeploymentChange? UndoChange, IReadOnlyList<string> Steps);

public interface IDeploymentService
{
    Task<IReadOnlyList<DeployableObject>> ListObjectsAsync(DeploymentObjectType type, CancellationToken ct);

    Task<IReadOnlyList<string>> ListProgramsAsync(string packageId, CancellationToken ct);

    Task<IReadOnlyList<DeploymentCollection>> ListCollectionsAsync(CancellationToken ct);

    Task<IReadOnlyList<DpGroupInfo>> ListDpGroupsAsync(DeployableObject obj, CancellationToken ct);

    /// <summary>The existing deployments of the object.</summary>
    Task<IReadOnlyList<DeploymentRecord>> ListDeploymentsAsync(DeployableObject obj, CancellationToken ct);

    /// <summary>Runs the five checks and the schedule rules, reads the current state, and fills Checks and Impact.</summary>
    Task<DeploymentChange> PrepareAsync(DeploymentChange request, CancellationToken ct);

    Task<IReadOnlyList<DeploymentOutcome>> ApplyAsync(IReadOnlyList<DeploymentChange> changes, IProgress<DeploymentOutcome> progress, CancellationToken ct);
}

/// <summary>A plain refusal of the deployment service; the message is safe to show.</summary>
public sealed class DeploymentServiceException(string message) : Exception(message);

public sealed record AuditProbe(bool Writable, string Message);

public sealed record AuditWrite(bool Succeeded, string Message);

/// <summary>One line of the audit log. The names follow the records the suite tool writes, so both tools can share one file.</summary>
/// <param name="Event">Create, Distribute or Undo.</param>
/// <param name="Result">Success or Failed.</param>
public sealed record DeploymentAuditRecord(
    DateTime Timestamp,
    string User,
    string DeploymentType,
    string ApplicationName,
    string ApplicationVersion,
    string CollectionName,
    string CollectionID,
    int? MemberCount,
    string DeployPurpose,
    string DeployAction,
    DateTime? DeadlineDateTime,
    string DeploymentID,
    string Result)
{
    public string Event { get; init; } = "Create";
    public string ObjectId { get; init; } = "";
    public DateTime? AvailableDateTime { get; init; }
    public string Message { get; init; } = "";
}

public interface IDeploymentAuditLog
{
    AuditProbe Probe();
    AuditWrite Append(DeploymentAuditRecord r);
    IReadOnlyList<DeploymentAuditRecord> Read(int max);

    /// <summary>The file the log uses now; it follows the setting at each call.</summary>
    string Path { get; }
}

/// <summary>A deployment template; nothing about collections or objects is stored.</summary>
/// <param name="DeadlineOffsetHours">Hours from the available time to the deadline of a Required deployment; 0 for none.</param>
/// <param name="IsSeeded">True for the four defaults the app writes; a user template never replaces one.</param>
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
    bool PostRebootScan,
    int DeadlineOffsetHours,
    bool IsSeeded);

public interface IDeploymentTemplateStore
{
    IReadOnlyList<DeploymentTemplate> All();
    void Save(DeploymentTemplate t);
    void Delete(string id);

    /// <summary>A plain note about files that could not be read, or null.</summary>
    string? Message { get; }
}

public interface IDeploymentReportWriter
{
    string WriteCsv(IReadOnlyList<DeploymentAuditRecord> rows);
    string WriteHtml(IReadOnlyList<DeploymentAuditRecord> rows);
}
