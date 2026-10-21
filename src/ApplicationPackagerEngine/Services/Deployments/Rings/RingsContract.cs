namespace ApplicationPackagerEngine.Services;

/// <summary>One ring of a plan: a device collection, a purpose and offsets in days from the plan start.</summary>
public sealed record RingDefinition(string Name, string CollectionId, string CollectionName, string Purpose, int AvailableOffsetDays, int? DeadlineOffsetDays,
    string UserNotification, bool OverrideServiceWindow, bool RebootOutsideServiceWindow, bool AllowMetered, string DpGroup, int? SuccessThresholdPercent,
    string? FastNetworkOption, string? SlowNetworkOption, string? RerunBehavior);

/// <param name="TimeBasedOn">LocalTime or Utc.</param>
public sealed record RingPlan(string Name, string Description, bool HoldLaterRings, string TimeBasedOn, IReadOnlyList<RingDefinition> Rings, bool IsSeed);

public sealed record RingExpansionRow(int Index, string Name, string CollectionId, string CollectionName, int MemberCount, string Purpose, DateTime AvailableTime, DateTime? Deadline,
    string DpGroup, int? SuccessThresholdPercent, IReadOnlyList<PreflightCheck> Checks);

/// <param name="Status">Held, Creating, Created, Removed or Cancelled.</param>
public sealed record RingRunRing(int Index, string Name, string CollectionId, string CollectionName, string Purpose, DateTime AvailableTime, DateTime? Deadline,
    string Status, DateTime? CreatingAt, string? DeploymentId, int ShiftedMinutes, string DpGroup, int? SuccessThresholdPercent);

public sealed record RingRun(string RunId, string FileName, string PlanName, bool HoldLaterRings, string TimeBasedOn, DeploymentObjectType ObjectType, string ObjectId, string ObjectName,
    string Program, string CreatedBy, DateTime CreatedUtc, IReadOnlyList<RingRunRing> Rings, bool IsClosed);

/// <param name="State">Found, Summarized, NotFound or Error.</param>
public sealed record RingLiveCounts(int Index, string DeploymentId, string State, int Targeted, int Success, int Error, int InProgress, int Unknown, bool CountsKnown, string Message);

public static class RingPlanLimits
{
    /// <summary>The most rings a plan holds; a plan with more is refused.</summary>
    public const int MaxRings = 100;
}

public enum RingChangeKind { CreateRun, Promote, UndoRun, CloseRun }

/// <param name="RingChanges">The part 1 changes the service built.</param>
/// <param name="Fingerprint">The run file state at Prepare: a version stamp that Apply compares.</param>
public sealed record RingChange(RingChangeKind Kind, RingPlan? Plan, DeployableObject? Object, IReadOnlyList<RingExpansionRow> Rows, DateTime? PlanStart, string? RunId, int? RingIndex,
    bool RequiredConfirmed, bool BackdatedAcknowledged, bool CreateNowContentDistributing, IReadOnlyList<DeploymentChange> RingChanges,
    IReadOnlyList<string> Impact, IReadOnlyList<PreflightCheck> Checks, string? Fingerprint);

public sealed record RingOutcome(RingChange Change, bool Succeeded, string Message, IReadOnlyList<DeploymentOutcome> RingOutcomes, RingChange? UndoChange, IReadOnlyList<string> Steps);

/// <summary>Whether the plan folder and the run folder can be written and a lock file can be made in the run folder.</summary>
/// <param name="StaleLockAge">A lock older than this is reported as stale; it is never removed automatically.</param>
public sealed record RingFolderProbe(string PlanFolder, string RunFolder, bool PlanFolderWritable, bool RunFolderWritable, bool LockCreatable, bool IsUnc, TimeSpan StaleLockAge, string Message);

public interface IRingService
{
    /// <summary>The seed plans and the user plans; <see cref="PlanMessage"/> names the files that were skipped.</summary>
    IReadOnlyList<RingPlan> ListPlans();

    string? PlanMessage { get; }

    RingPlan? SavePlan(RingPlan plan, bool replace);

    /// <summary>Refuses a seed plan.</summary>
    void DeletePlan(string name);

    Task<IReadOnlyList<RingExpansionRow>> ExpandAsync(RingPlan plan, DeployableObject obj, DateTime planStart, CancellationToken ct);

    Task<IReadOnlyList<RingRun>> ListRunsAsync(bool closed, CancellationToken ct);

    Task<IReadOnlyList<RingLiveCounts>> LiveCountsAsync(RingRun run, CancellationToken ct);

    /// <summary>Takes the lock; resolves Creating rings, marks Removed rings and closes a finished run.</summary>
    Task<RingRun> ReconcileAsync(string runId, CancellationToken ct);

    Task<RingChange> PrepareAsync(RingChange request, CancellationToken ct);

    Task<IReadOnlyList<RingOutcome>> ApplyAsync(IReadOnlyList<RingChange> changes, IProgress<RingOutcome> progress, CancellationToken ct);

    /// <summary>The plan folder, the run folder, lock creation and UNC.</summary>
    RingFolderProbe ProbeFolders();
}
