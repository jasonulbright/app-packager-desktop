namespace ApplicationPackagerEngine.Services;

/// <summary>
/// One ring of a plan. Offsets are whole days from the plan start; the time of day of the start
/// is kept. The positional members carry the meaning of the plan file fields of the same names
/// (AllowMetered is AllowMeteredConnection, DpGroup is DPGroup, CollectionId is CollectionID).
/// </summary>
/// <param name="Purpose">Available or Required.</param>
/// <param name="DeadlineOffsetDays">Required: days from the plan start to the deadline; null for Available.</param>
/// <param name="DpGroup">A DP group name to distribute to before the ring's create; empty for none.</param>
/// <param name="SuccessThresholdPercent">The success rate of this ring that opens Promote of the next ring; null for no gate.</param>
/// <param name="FastNetworkOption">Package: null for DownloadContentFromDistributionPointAndRunLocally.</param>
/// <param name="SlowNetworkOption">Package: null for DoNotRunProgram.</param>
/// <param name="RerunBehavior">Package and task sequence: null for NeverRerunDeployedProgram.</param>
/// <param name="AllowBoundaryFallback">Software update group: install from the default site boundary group.</param>
/// <param name="TaskSequenceDeploymentOption">Task sequence: null for DownloadAllContentLocallyBeforeStartingTaskSequence when available to clients only, else DownloadContentLocallyWhenNeededByRunningTaskSequence.</param>
public sealed record RingDefinition(
    string Name,
    string CollectionId,
    string CollectionName,
    string Purpose,
    int AvailableOffsetDays,
    int? DeadlineOffsetDays,
    string UserNotification,
    bool OverrideServiceWindow,
    bool RebootOutsideServiceWindow,
    bool AllowMetered,
    string DpGroup,
    int? SuccessThresholdPercent,
    string? FastNetworkOption,
    string? SlowNetworkOption,
    string? RerunBehavior,
    bool AllowBoundaryFallback = true,
    bool AllowMicrosoftUpdate = false,
    bool RequirePostRebootFullScan = true,
    bool ShowTaskSequenceProgress = true,
    string TaskSequenceAvailability = "Clients",
    string? TaskSequenceDeploymentOption = null);

/// <param name="TimeBasedOn">LocalTime or Utc.</param>
/// <param name="IsSeed">True for the two plans the app writes; a seed is never replaced or deleted, only copied.</param>
/// <param name="FileName">The file name of the plan in the plans folder; empty for a plan that was not read from a file.</param>
public sealed record RingPlan(string Name, string Description, bool HoldLaterRings, string TimeBasedOn, IReadOnlyList<RingDefinition> Rings, bool IsSeed, string FileName = "");

/// <summary>
/// One ring expanded for one plan start. Times are wall-clock values in the plan's time basis.
/// The preview may edit CollectionId, CollectionName, AvailableTime and Deadline for the run.
/// </summary>
/// <param name="Index">1 for the first ring.</param>
/// <param name="MemberCount">The member count of the collection when the row was made; 0 when not known.</param>
/// <param name="Checks">Expand: the plan rule results of the ring; Prepare: the part 1 checks of the ring's create and the plan rules.</param>
/// <param name="Definition">The ring of the plan the row was expanded from; null for a row read from a run file.</param>
public sealed record RingExpansionRow(int Index, string Name, string CollectionId, string CollectionName, int MemberCount, string Purpose, DateTime AvailableTime, DateTime? Deadline,
    string DpGroup, int? SuccessThresholdPercent, IReadOnlyList<PreflightCheck> Checks)
{
    public RingDefinition? Definition { get; init; }
}

/// <summary>One ring of a run file.</summary>
/// <param name="Status">Held, Creating, Created, Removed or Cancelled.</param>
/// <param name="CreatingAt">The local time of the computer that started the create, as the run file holds it; null unless Creating.</param>
/// <param name="DeploymentId">The AssignmentUniqueID of an application or update group deployment, the AdvertisementID otherwise.</param>
/// <param name="ShiftedMinutes">The minutes Promote moved the ring's times later.</param>
public sealed record RingRunRing(int Index, string Name, string CollectionId, string CollectionName, string Purpose, DateTime AvailableTime, DateTime? Deadline,
    string Status, DateTime? CreatingAt, string? DeploymentId, int ShiftedMinutes, string DpGroup, int? SuccessThresholdPercent)
{
    /// <summary>The ring's options as the run file holds them; null for a ring whose fields cannot be read.</summary>
    public RingDefinition? Definition { get; init; }

    /// <summary>The UTC time the create started, when the run file holds it; null otherwise.</summary>
    public DateTime? CreatingAtUtc { get; init; }

    /// <summary>The account that started the create, when the run file holds it; null otherwise.</summary>
    public string? CreatingBy { get; init; }

    /// <summary>A plain note of the last Reconcile about this ring (an unresolved create, a read error); empty otherwise. Not stored in the file.</summary>
    public string Note { get; init; } = "";
}

/// <summary>A run file. ObjectId is the CI_ID of an application or update group and the PackageID of a package or task sequence.</summary>
/// <param name="FileName">The file name in the runs folder or its closed subfolder.</param>
/// <param name="CreatedUtc">The UTC time the run started.</param>
public sealed record RingRun(string RunId, string FileName, string PlanName, bool HoldLaterRings, string TimeBasedOn, DeploymentObjectType ObjectType, string ObjectId, string ObjectName,
    string Program, string CreatedBy, DateTime CreatedUtc, IReadOnlyList<RingRunRing> Rings, bool IsClosed)
{
    /// <summary>The version stamp of the file when it was read: last write time, length and a content hash.</summary>
    public string Fingerprint { get; init; } = "";

    /// <summary>The lock file of the run when one exists; null otherwise.</summary>
    public RingLockInfo? Lock { get; init; }

    /// <summary>The ModelID of an application; empty for other types and for a run written by another tool.</summary>
    public string ModelId { get; init; } = "";
}

/// <summary>A lock file of a run.</summary>
/// <param name="Owner">The owner the lock file names (user, computer, process, time), or a plain text when it cannot be read.</param>
/// <param name="Age">The time since the lock file was written.</param>
/// <param name="Stale">True when the age passed the stale lock age of <see cref="RingFolderProbe"/>; a stale lock is reported, never removed.</param>
public sealed record RingLockInfo(string Path, string Owner, TimeSpan Age, bool Stale);

/// <summary>The live counts of one created ring.</summary>
/// <param name="State">Found (the deployment exists, no summary yet), Summarized (counts read), NotFound (the deployment is gone by its ID) or Error (not known).</param>
/// <param name="CountsKnown">True only when a summary row was read; the counts are 0 and mean nothing otherwise.</param>
public sealed record RingLiveCounts(int Index, string DeploymentId, string State, int Targeted, int Success, int Error, int InProgress, int Unknown, bool CountsKnown, string Message)
{
    /// <summary>Clients whose requirements are not met (NumberOther); 0 when not known.</summary>
    public int Other { get; init; }

    /// <summary>The time the site summarized the counts, as the provider returns it; null when not known.</summary>
    public DateTime? SummarizationTime { get; init; }
}

public enum RingChangeKind { CreateRun, Promote, UndoRun, CloseRun }

/// <summary>
/// One ring action. PrepareAsync fills RingChanges (the part 1 changes the service built), Rows,
/// Impact, Checks and Fingerprint; ApplyAsync runs only what the prepared change names.
/// </summary>
/// <param name="Rows">CreateRun: the expanded rows with the run's edits; Prepare returns them with their checks.</param>
/// <param name="PlanStart">CreateRun: the start the rows were expanded from.</param>
/// <param name="RunId">Promote, UndoRun, CloseRun: the run.</param>
/// <param name="RingIndex">Promote: the ring to create.</param>
/// <param name="RequiredConfirmed">The second confirmation for the Required rings of the change.</param>
/// <param name="BackdatedAcknowledged">The confirmation for an available time in the past.</param>
/// <param name="CreateNowContentDistributing">A ring with a DP group is created while its content still distributes.</param>
/// <param name="Fingerprint">The run file state at Prepare; Apply refuses a run file that changed since.</param>
public sealed record RingChange(RingChangeKind Kind, RingPlan? Plan, DeployableObject? Object, IReadOnlyList<RingExpansionRow> Rows, DateTime? PlanStart, string? RunId, int? RingIndex,
    bool RequiredConfirmed, bool BackdatedAcknowledged, bool CreateNowContentDistributing, IReadOnlyList<DeploymentChange> RingChanges,
    IReadOnlyList<string> Impact, IReadOnlyList<PreflightCheck> Checks, string? Fingerprint)
{
    /// <summary>True when a check failed or could not be made: the change does not run.</summary>
    public bool Blocked => Checks.Any(c => c.Result is DeploymentCheckResults.Fail or DeploymentCheckResults.Unknown);

    /// <summary>True when a ring the change creates is Required.</summary>
    public bool NeedsRequiredConfirmation => RingChanges.Any(c => c.NeedsRequiredConfirmation);

    /// <summary>True when a ring the change creates is available in the past.</summary>
    public bool NeedsBackdatedAcknowledgement => RingChanges.Any(c => c.NeedsBackdatedAcknowledgement);

    /// <summary>Required rings and their devices now, for the text of the second confirmation.</summary>
    public int RequiredRings => RingChanges.Count(c => c.NeedsRequiredConfirmation);
    public int RequiredDevices => RingChanges.Where(c => c.NeedsRequiredConfirmation).Sum(c => c.Collection?.MemberCount ?? 0);

    /// <summary>Promote: the minutes the promoted ring and every later Held ring move later because the ring's deadline passed; 0 for none.</summary>
    public int ShiftMinutes { get; init; }

    /// <summary>UndoRun: the indexes of the Created rings whose deployment fails a check and is kept.</summary>
    public IReadOnlyList<int> KeptRings { get; init; } = [];

    /// <summary>UndoRun: the indexes of the Held rings that are cancelled.</summary>
    public IReadOnlyList<int> CancelledRings { get; init; } = [];
}

/// <param name="Message">Plain text without raw exception text; starts with "Skipped" when nothing was attempted.</param>
/// <param name="RingOutcomes">The part 1 outcomes of the rings, in ring order.</param>
/// <param name="UndoChange">An UndoRun request for the run when the change created a deployment; null otherwise. It needs PrepareAsync.</param>
public sealed record RingOutcome(RingChange Change, bool Succeeded, string Message, IReadOnlyList<DeploymentOutcome> RingOutcomes, RingChange? UndoChange, IReadOnlyList<string> Steps)
{
    /// <summary>False for a progress report after one ring; true for the result of the whole change.</summary>
    public bool Final { get; init; } = true;

    /// <summary>The run as the file holds it after the change; null when the run file could not be read.</summary>
    public RingRun? Run { get; init; }
}

/// <summary>The checks of the plans folder and the runs folder.</summary>
/// <param name="LockCreated">True when a lock file could be made and removed in the runs folder.</param>
/// <param name="IsUnc">True when the runs folder is a UNC path.</param>
/// <param name="Message">Plain text that names each problem; empty when both folders work.</param>
public sealed record RingFolderProbe(string PlansFolder, bool PlansWritable, string RunsFolder, bool RunsWritable, bool LockCreated, bool IsUnc, string Message, TimeSpan StaleLockAge);

/// <summary>The folders the page sets; null or empty means the default under the data root.</summary>
public interface IRingFolderPathProvider
{
    string? PlansFolder { get; }
    string? RunsFolder { get; }
}

/// <summary>The default folders: Deployments\Rings and Deployments\Rings\runs under the data root.</summary>
public sealed class DefaultRingFolderPaths : IRingFolderPathProvider
{
    public string? PlansFolder => null;
    public string? RunsFolder => null;
}

public interface IRingService
{
    /// <summary>The seeds and the user plans; a file that cannot be used is left out and named in <see cref="PlanMessage"/>.</summary>
    IReadOnlyList<RingPlan> ListPlans();

    string? PlanMessage { get; }

    /// <summary>Validates and writes the plan; refuses a seed name and, without <paramref name="replace"/>, a name that is taken.</summary>
    RingPlan? SavePlan(RingPlan plan, bool replace);

    /// <summary>Deletes a user plan; refuses a seed.</summary>
    void DeletePlan(string name);

    /// <summary>The rings with absolute times for the start and the member counts of the collections now.</summary>
    Task<IReadOnlyList<RingExpansionRow>> ExpandAsync(RingPlan plan, DeployableObject obj, DateTime planStart, CancellationToken ct);

    Task<IReadOnlyList<RingRun>> ListRunsAsync(bool closed, CancellationToken ct);

    /// <summary>The counts of every Created ring of the run; reads only, changes no file.</summary>
    Task<IReadOnlyList<RingLiveCounts>> LiveCountsAsync(RingRun run, CancellationToken ct);

    /// <summary>Takes the lock, resolves Creating rings, marks Removed rings and closes a finished run.</summary>
    Task<RingRun> ReconcileAsync(string runId, CancellationToken ct);

    Task<RingChange> PrepareAsync(RingChange request, CancellationToken ct);

    Task<IReadOnlyList<RingOutcome>> ApplyAsync(IReadOnlyList<RingChange> changes, IProgress<RingOutcome> progress, CancellationToken ct);

    RingFolderProbe ProbeFolders();

    /// <summary>A plain note about run files that could not be read by the last list, or null.</summary>
    string? RunMessage => null;
}

/// <summary>The limits of ring plans and runs.</summary>
public static class RingPlanLimits
{
    /// <summary>The most rings a plan or a run holds: a safety cap; a plan with more rings is refused with a message that names it.</summary>
    public const int MaxRings = 100;
}

public static class RingStatuses
{
    public const string Held = "Held";
    public const string Creating = "Creating";
    public const string Created = "Created";
    public const string Removed = "Removed";
    public const string Cancelled = "Cancelled";
}

public static class RingLiveStates
{
    public const string Found = "Found";
    public const string Summarized = "Summarized";
    public const string NotFound = "NotFound";
    public const string Error = "Error";
}
