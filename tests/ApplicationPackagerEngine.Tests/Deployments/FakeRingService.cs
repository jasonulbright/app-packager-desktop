using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>
/// A ring service over plan and run lists. Like the real service it refuses a Required ring without
/// the run's acknowledgement, refuses a change whose run file changed after Prepare, names the
/// owner of a held lock and stops a create-all at the first failed ring.
/// </summary>
internal sealed class FakeRingService : IRingService
{
    public const string RequiredRefused = "Refused: a Required ring needs the second confirmation. Nothing was changed.";
    public const string StaleRun = "Skipped: the run file changed after the plan was shown. Nothing was changed. Refresh and show the plan again.";
    public const string CancelledSkip = "Skipped: the run was cancelled before this ring.";

    private int _nextDeployment = 0x500;
    private int _nextRun = 1;

    public List<RingPlan> Plans { get; } = [];
    public string? PlanMessage { get; set; }
    public List<RingRun> Runs { get; } = [];
    public Dictionary<string, int> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<RingLiveCounts>> Counts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> MemberCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Deployment IDs another process changed: Undo run keeps them.</summary>
    public HashSet<string> ChangedByOthers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The owner of a held lock; set, every lock-taking call is refused with it.</summary>
    public string? LockOwner;

    public int ListPlanCalls, ExpandCalls, ListRunCalls, CountCalls, ReconcileCalls, PrepareCalls, ProbeCalls;
    public TaskCompletionSource? ExpandGate, PrepareGate, RunsGate, CountsGate, ApplyGate;
    public Func<RingChange, Exception?>? PrepareFails;
    public Exception? SaveFailure;
    public bool SaveReturnsNull;
    public Func<RingExpansionRow, string?>? FailRing;
    public Func<RingChange, RingChange>? AfterPrepare;
    public Func<bool>? InConfirmedRun;
    public bool IgnoreCancel;
    public Action<int>? AfterRing;
    public Action? BeforeApply;

    public List<IReadOnlyList<RingChange>> Applied { get; } = [];
    public List<RingChange> Prepared { get; } = [];
    public List<(RingPlan Plan, bool Replace)> Saved { get; } = [];
    public List<string> Deleted { get; } = [];
    public List<CancellationToken> Tokens { get; } = [];
    public RingFolderProbe NextProbe { get; set; } = new(PlanFolder: @"C:\Data\Deployments\Rings", RunFolder: @"C:\Data\Deployments\Rings\runs",
        PlanFolderWritable: true, RunFolderWritable: true, LockCreatable: true, IsUnc: false, StaleLockAge: TimeSpan.FromHours(2), Message: "");

    private void Token(CancellationToken ct)
    {
        lock (Tokens) Tokens.Add(ct);
    }

    public static RingDefinition Ring(string name, string collectionId, string collectionName, string purpose = "Available", int available = 0, int? deadline = null,
        int? threshold = null, string dpGroup = "") =>
        new(Name: name, CollectionId: collectionId, CollectionName: collectionName, Purpose: purpose, AvailableOffsetDays: available, DeadlineOffsetDays: deadline,
            UserNotification: "DisplayAll", OverrideServiceWindow: false, RebootOutsideServiceWindow: false, AllowMetered: false, DpGroup: dpGroup,
            SuccessThresholdPercent: threshold, FastNetworkOption: null, SlowNetworkOption: null, RerunBehavior: null);

    public IReadOnlyList<RingPlan> ListPlans()
    {
        Interlocked.Increment(ref ListPlanCalls);
        lock (Plans) return Plans.ToList();
    }

    public RingPlan? SavePlan(RingPlan plan, bool replace)
    {
        if (SaveFailure is { } failure) throw failure;
        lock (Plans)
        {
            Saved.Add((plan, replace));
            if (SaveReturnsNull) return null;
            var existing = Plans.FindIndex(p => string.Equals(p.Name, plan.Name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0 && (Plans[existing].IsSeed || !replace)) throw new DeploymentServiceException("A plan with this name exists. Nothing was saved.");
            if (existing >= 0) Plans[existing] = plan;
            else Plans.Add(plan);
            return plan;
        }
    }

    public void DeletePlan(string name)
    {
        lock (Plans)
        {
            if (Plans.Any(p => p.IsSeed && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) throw new DeploymentServiceException("A shipped plan cannot be deleted.");
            Deleted.Add(name);
            Plans.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    private int Members(string collectionId) => MemberCounts.TryGetValue(collectionId, out var count) ? count : 3;

    public async Task<IReadOnlyList<RingExpansionRow>> ExpandAsync(RingPlan plan, DeployableObject obj, DateTime planStart, CancellationToken ct)
    {
        Interlocked.Increment(ref ExpandCalls);
        Token(ct);
        if (ExpandGate is { } gate) await gate.Task.WaitAsync(ct);
        lock (Plans)
            return plan.Rings.Select((r, i) => new RingExpansionRow(Index: i + 1, Name: r.Name, CollectionId: r.CollectionId, CollectionName: r.CollectionName,
                MemberCount: r.CollectionId.Length == 0 ? 0 : Members(r.CollectionId), Purpose: r.Purpose, AvailableTime: planStart.AddDays(r.AvailableOffsetDays),
                Deadline: r.DeadlineOffsetDays is { } d && string.Equals(r.Purpose, "Required", StringComparison.OrdinalIgnoreCase) ? planStart.AddDays(d) : null,
                DpGroup: r.DpGroup, SuccessThresholdPercent: r.SuccessThresholdPercent, Checks: [])).ToList();
    }

    public async Task<IReadOnlyList<RingRun>> ListRunsAsync(bool closed, CancellationToken ct)
    {
        Interlocked.Increment(ref ListRunCalls);
        Token(ct);
        if (RunsGate is { } gate) await gate.Task.WaitAsync(ct);
        lock (Runs) return Runs.Where(r => r.IsClosed == closed).ToList();
    }

    public async Task<IReadOnlyList<RingLiveCounts>> LiveCountsAsync(RingRun run, CancellationToken ct)
    {
        Interlocked.Increment(ref CountCalls);
        Token(ct);
        if (CountsGate is { } gate) await gate.Task.WaitAsync(ct);
        lock (Runs) return Counts.TryGetValue(run.RunId, out var list) ? list.ToList() : [];
    }

    private void CheckLock(string runId)
    {
        if (LockOwner is { } owner)
            throw new DeploymentServiceException($"The run {runId} is locked by another session ({owner}). Lock file: {runId}.lock. Nothing was changed.");
    }

    private RingRun RunOf(string runId)
    {
        lock (Runs) return Runs.SingleOrDefault(r => string.Equals(r.RunId, runId, StringComparison.OrdinalIgnoreCase))
            ?? throw new DeploymentServiceException($"The run {runId} does not exist any more.", DeploymentFailureKind.Stale);
    }

    private string Fingerprint(string runId)
    {
        lock (Runs) return $"v{(Versions.TryGetValue(runId, out var v) ? v : 0)}";
    }

    private void Replace(RingRun run)
    {
        lock (Runs)
        {
            var i = Runs.FindIndex(r => string.Equals(r.RunId, run.RunId, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) Runs[i] = run;
            else Runs.Add(run);
            Versions[run.RunId] = (Versions.TryGetValue(run.RunId, out var v) ? v : 0) + 1;
        }
    }

    /// <summary>Changes the run file as another session would; a plan prepared before it is stale.</summary>
    public void Touch(string runId)
    {
        lock (Runs) Versions[runId] = (Versions.TryGetValue(runId, out var v) ? v : 0) + 1;
    }

    public Task<RingRun> ReconcileAsync(string runId, CancellationToken ct)
    {
        Interlocked.Increment(ref ReconcileCalls);
        Token(ct);
        CheckLock(runId);
        var run = RunOf(runId);
        var finished = run.Rings.All(r => r.Status is "Created" or "Removed" or "Cancelled");
        var result = finished ? run with { IsClosed = true } : run;
        Replace(result);
        return Task.FromResult(result);
    }

    private static PreflightCheck Pass(string name) => new(Name: name, Result: "Pass", Message: "");

    private List<PreflightCheck> RingChecks(RingExpansionRow row)
    {
        var checks = new List<PreflightCheck> { Pass("TargetExists"), Pass("ContentDistributed") };
        checks.Add(row.CollectionId.Length == 0
            ? new PreflightCheck(Name: "CollectionValid", Result: "Fail", Message: "The ring has no collection.")
            : Pass("CollectionValid"));
        checks.Add(row.CollectionId.StartsWith("SMS", StringComparison.OrdinalIgnoreCase)
            ? new PreflightCheck(Name: "CollectionSafe", Result: "Fail", Message: "Built-in collections cannot be ring targets.")
            : Pass("CollectionSafe"));
        checks.Add(Pass("NoDuplicate"));
        return checks;
    }

    private static DeploymentChange CreateChange(DeployableObject obj, RingExpansionRow row, string timeBasedOn, int members) => new(
        Kind: DeploymentChangeKind.Create, Object: obj, Collection: new DeploymentCollection(row.CollectionId, row.CollectionName, "", members),
        Options: new DeploymentOptions(Purpose: row.Purpose, AvailableTime: row.AvailableTime, Deadline: row.Deadline, UtcBasis: string.Equals(timeBasedOn, "Utc", StringComparison.OrdinalIgnoreCase),
            UserNotification: "DisplayAll", OverrideServiceWindow: false, RebootOutsideServiceWindow: false, AllowMetered: false, NetworkBehavior: "", RerunBehavior: "",
            ShowProgress: true, AllowFallback: false, FallbackToDp: true, PostRebootScan: true, SuppressRestart: false),
        DpGroups: row.DpGroup.Length > 0 ? [row.DpGroup] : [], CreateNowContentDistributing: false, RequiredConfirmed: false, BackdatedAcknowledged: false,
        Existing: null, Checks: [], Impact: [], CreatedId: null);

    public async Task<RingChange> PrepareAsync(RingChange request, CancellationToken ct)
    {
        Interlocked.Increment(ref PrepareCalls);
        Token(ct);
        if (PrepareGate is { } gate) await gate.Task.WaitAsync(ct);
        if (PrepareFails?.Invoke(request) is { } failure) throw failure;
        RingChange prepared;
        switch (request.Kind)
        {
            case RingChangeKind.CreateRun:
            {
                var rows = request.Rows.Select(r => r with { MemberCount = r.CollectionId.Length == 0 ? 0 : Members(r.CollectionId) })
                    .Select(r => r with { Checks = RingChecks(r) }).ToList();
                var shared = rows.Where(r => r.CollectionId.Length > 0).GroupBy(r => r.CollectionId, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
                var plan = request.Plan!;
                var now = plan.HoldLaterRings ? rows.Take(1) : rows;
                prepared = request with
                {
                    Rows = rows,
                    Checks = [shared ? new PreflightCheck(Name: "PlanRules", Result: "Fail", Message: "Two rings name the same collection.") : Pass("PlanRules"), Pass("AuditLog")],
                    RingChanges = now.Select(r => CreateChange(request.Object!, r, plan.TimeBasedOn, r.MemberCount)).ToList()
                };
                break;
            }
            case RingChangeKind.Promote:
            {
                CheckLock(request.RunId!);
                var run = RunOf(request.RunId!);
                var ring = run.Rings.Single(r => r.Index == request.RingIndex);
                var row = new RingExpansionRow(Index: ring.Index, Name: ring.Name, CollectionId: ring.CollectionId, CollectionName: ring.CollectionName,
                    MemberCount: Members(ring.CollectionId), Purpose: ring.Purpose, AvailableTime: ring.AvailableTime, Deadline: ring.Deadline, DpGroup: ring.DpGroup,
                    SuccessThresholdPercent: ring.SuccessThresholdPercent, Checks: []);
                row = row with { Checks = RingChecks(row) };
                var obj = new DeployableObject(Type: run.ObjectType, Id: run.ObjectId, Name: run.ObjectName, Version: "", Program: run.Program, PackagedByProduct: false);
                prepared = request with
                {
                    Rows = [row], Object = obj, RingChanges = [CreateChange(obj, row, run.TimeBasedOn, row.MemberCount)], Checks = [Pass("Threshold")],
                    Impact = ["The previous ring is Created."], Fingerprint = Fingerprint(run.RunId)
                };
                break;
            }
            case RingChangeKind.UndoRun:
            {
                CheckLock(request.RunId!);
                var run = RunOf(request.RunId!);
                var removals = run.Rings.Where(r => r.Status == "Created" && r.DeploymentId is not null).Select(r =>
                {
                    var record = new DeploymentRecord(DeploymentId: r.DeploymentId!, Type: run.ObjectType, ObjectId: run.ObjectId, ObjectName: run.ObjectName,
                        CollectionId: r.CollectionId, CollectionName: r.CollectionName, Purpose: r.Purpose, AvailableTime: r.AvailableTime, Deadline: r.Deadline,
                        Created: run.CreatedUtc, CreatedBy: run.CreatedBy);
                    var changed = ChangedByOthers.Contains(r.DeploymentId!);
                    return new DeploymentChange(Kind: DeploymentChangeKind.RemoveDeployment, Object: null, Collection: null, Options: null, DpGroups: [],
                        CreateNowContentDistributing: false, RequiredConfirmed: false, BackdatedAcknowledged: false, Existing: record,
                        Checks: [changed ? new PreflightCheck(Name: "DeploymentUnchanged", Result: "Fail", Message: "Another process changed the deadline.") : Pass("DeploymentUnchanged"),
                            Pass("CreatedHere")],
                        Impact: [], CreatedId: r.DeploymentId) { Site = record };
                }).ToList();
                var held = run.Rings.Where(r => r.Status == "Held").Select(r => new RingExpansionRow(Index: r.Index, Name: r.Name, CollectionId: r.CollectionId,
                    CollectionName: r.CollectionName, MemberCount: 0, Purpose: r.Purpose, AvailableTime: r.AvailableTime, Deadline: r.Deadline, DpGroup: r.DpGroup,
                    SuccessThresholdPercent: r.SuccessThresholdPercent, Checks: [])).ToList();
                prepared = request with { RingChanges = removals, Rows = held, Fingerprint = Fingerprint(run.RunId), Impact = [$"Run file {run.FileName}."] };
                break;
            }
            default:
            {
                CheckLock(request.RunId!);
                var run = RunOf(request.RunId!);
                prepared = request with { Fingerprint = Fingerprint(run.RunId), Impact = [$"Run file {run.FileName}."] };
                break;
            }
        }
        if (AfterPrepare is { } after) prepared = after(prepared);
        lock (Prepared) Prepared.Add(prepared);
        return prepared;
    }

    public async Task<IReadOnlyList<RingOutcome>> ApplyAsync(IReadOnlyList<RingChange> changes, IProgress<RingOutcome> progress, CancellationToken ct)
    {
        if (InConfirmedRun is { } inRun) Assert.True(inRun(), "A ring write ran outside a confirmed change plan.");
        lock (Applied) Applied.Add(changes);
        await Task.Yield();
        BeforeApply?.Invoke();
        if (ApplyGate is { } hold) await hold.Task;
        var outcomes = new List<RingOutcome>();
        foreach (var change in changes)
        {
            var outcome = change.Kind switch
            {
                RingChangeKind.CreateRun => CreateRun(change, progress, ct),
                RingChangeKind.Promote => Promote(change),
                RingChangeKind.UndoRun => Undo(change),
                _ => Close(change)
            };
            outcomes.Add(outcome);
            progress.Report(outcome);
        }
        return outcomes;
    }

    private bool Stale(RingChange change) => change.RunId is { } id && change.Fingerprint is { } print && print != Fingerprint(id);

    private DeploymentOutcome CreateOne(DeploymentChange change, bool runAcknowledged, RingExpansionRow row, out string? id)
    {
        id = null;
        if (DeploymentWriter.IsRequiredCreate(change) && !runAcknowledged)
            return new DeploymentOutcome(Change: change, Succeeded: false, Message: RequiredRefused, UndoChange: null, Steps: []);
        if (FailRing?.Invoke(row) is { } failure)
            return new DeploymentOutcome(Change: change, Succeeded: false, Message: failure, UndoChange: null, Steps: []);
        id = $"MCM{_nextDeployment++:X5}";
        var undo = change with { Kind = DeploymentChangeKind.RemoveDeployment, CreatedId = id };
        return new DeploymentOutcome(Change: change with { CreatedId = id }, Succeeded: true, Message: $"Created deployment {id}.", UndoChange: undo, Steps: ["Create: done.", "Read back: equal."]);
    }

    private RingOutcome CreateRun(RingChange change, IProgress<RingOutcome> progress, CancellationToken ct)
    {
        var plan = change.Plan!;
        var rows = change.Rows.OrderBy(r => r.Index).ToList();
        var now = plan.HoldLaterRings ? rows.Take(1).ToList() : rows;
        var runId = $"RUN-{_nextRun++:000}";
        var rings = rows.Select(r => new RingRunRing(Index: r.Index, Name: r.Name, CollectionId: r.CollectionId, CollectionName: r.CollectionName, Purpose: r.Purpose,
            AvailableTime: r.AvailableTime, Deadline: r.Deadline, Status: "Held", CreatingAt: null, DeploymentId: null, ShiftedMinutes: 0, DpGroup: r.DpGroup,
            SuccessThresholdPercent: r.SuccessThresholdPercent)).ToList();
        var outcomes = new List<DeploymentOutcome>();
        var failed = false;
        for (var i = 0; i < now.Count; i++)
        {
            var row = now[i];
            var part = change.RingChanges.FirstOrDefault(c => string.Equals(c.Collection?.CollectionId, row.CollectionId, StringComparison.OrdinalIgnoreCase))
                ?? CreateChange(change.Object!, row, plan.TimeBasedOn, row.MemberCount);
            if (!IgnoreCancel && ct.IsCancellationRequested)
            {
                outcomes.Add(new DeploymentOutcome(Change: part, Succeeded: false, Message: CancelledSkip, UndoChange: null, Steps: []));
                continue;
            }
            var outcome = CreateOne(part, change.RequiredConfirmed, row, out var id);
            outcomes.Add(outcome);
            if (id is not null) rings[rows.IndexOf(row)] = rings[rows.IndexOf(row)] with { Status = "Created", DeploymentId = id };
            progress.Report(new RingOutcome(Change: change, Succeeded: false, Message: "Running.", RingOutcomes: outcomes.ToList(), UndoChange: null, Steps: []));
            AfterRing?.Invoke(i);
            if (!outcome.Succeeded)
            {
                failed = true;
                break;
            }
        }
        if (!plan.HoldLaterRings)
            for (var i = 0; i < rings.Count; i++)
                if (rings[i].Status == "Held") rings[i] = rings[i] with { Status = failed ? "Cancelled" : rings[i].Status };
        var obj = change.Object!;
        Replace(new RingRun(RunId: runId, FileName: $"{plan.Name}_{obj.Id}_{runId}.json", PlanName: plan.Name, HoldLaterRings: plan.HoldLaterRings, TimeBasedOn: plan.TimeBasedOn,
            ObjectType: obj.Type, ObjectId: obj.Id, ObjectName: obj.Name, Program: obj.Program, CreatedBy: @"LAB\admin", CreatedUtc: new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc),
            Rings: rings, IsClosed: false));
        var succeeded = !failed && outcomes.All(o => o.Succeeded);
        return new RingOutcome(Change: change with { RunId = runId }, Succeeded: succeeded, Message: succeeded ? $"Run {runId} created." : $"Run {runId} stopped at a failed ring.",
            RingOutcomes: outcomes, UndoChange: null, Steps: []);
    }

    private RingOutcome Promote(RingChange change)
    {
        if (Stale(change)) return new RingOutcome(Change: change, Succeeded: false, Message: StaleRun, RingOutcomes: [], UndoChange: null, Steps: []);
        var run = RunOf(change.RunId!);
        var row = change.Rows.Single();
        var part = change.RingChanges.Single();
        var outcome = CreateOne(part, change.RequiredConfirmed, row, out var id);
        if (id is not null)
            Replace(run with { Rings = run.Rings.Select(r => r.Index == row.Index ? r with { Status = "Created", DeploymentId = id } : r).ToList() });
        return new RingOutcome(Change: change, Succeeded: outcome.Succeeded, Message: outcome.Message, RingOutcomes: [outcome], UndoChange: null, Steps: []);
    }

    private RingOutcome Undo(RingChange change)
    {
        if (Stale(change)) return new RingOutcome(Change: change, Succeeded: false, Message: StaleRun, RingOutcomes: [], UndoChange: null, Steps: []);
        var run = RunOf(change.RunId!);
        var outcomes = new List<DeploymentOutcome>();
        var rings = run.Rings.ToList();
        foreach (var removal in change.RingChanges)
        {
            var id = removal.Existing!.DeploymentId;
            var keep = ChangedByOthers.Contains(id);
            outcomes.Add(new DeploymentOutcome(Change: removal, Succeeded: !keep, Message: keep ? $"Skipped: {id} was changed by another process and is kept." : "Removed the deployment. It is gone.",
                UndoChange: null, Steps: []));
            if (!keep) rings = rings.Select(r => r.DeploymentId == id ? r with { Status = "Removed" } : r).ToList();
        }
        rings = rings.Select(r => r.Status == "Held" ? r with { Status = "Cancelled" } : r).ToList();
        Replace(run with { Rings = rings, IsClosed = true });
        return new RingOutcome(Change: change, Succeeded: true, Message: "The run was undone and closed.", RingOutcomes: outcomes, UndoChange: null, Steps: []);
    }

    private RingOutcome Close(RingChange change)
    {
        if (Stale(change)) return new RingOutcome(Change: change, Succeeded: false, Message: StaleRun, RingOutcomes: [], UndoChange: null, Steps: []);
        var run = RunOf(change.RunId!);
        Replace(run with { IsClosed = true });
        return new RingOutcome(Change: change, Succeeded: true, Message: "The run was closed.", RingOutcomes: [], UndoChange: null, Steps: []);
    }

    public RingFolderProbe ProbeFolders()
    {
        Interlocked.Increment(ref ProbeCalls);
        return NextProbe;
    }

    /// <summary>A run with created and held rings, as a run file holds it.</summary>
    public RingRun AddRun(string runId, DeployableObject obj, params (string Name, string CollectionId, string Status, int? Threshold, string Purpose)[] rings)
    {
        var start = new DateTime(2026, 10, 21, 8, 0, 0);
        var run = new RingRun(RunId: runId, FileName: $"Pilot-Rings_{obj.Id}_{runId}.json", PlanName: "Pilot-Rings", HoldLaterRings: true, TimeBasedOn: "LocalTime",
            ObjectType: obj.Type, ObjectId: obj.Id, ObjectName: obj.Name, Program: obj.Program, CreatedBy: @"LAB\admin", CreatedUtc: new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc),
            Rings: rings.Select((r, i) => new RingRunRing(Index: i + 1, Name: r.Name, CollectionId: r.CollectionId, CollectionName: r.CollectionId == "LAB00010" ? "Pilot Workstations" : $"Collection {r.CollectionId}",
                Purpose: r.Purpose, AvailableTime: start.AddDays(i * 7), Deadline: r.Purpose == "Required" ? start.AddDays(i * 7 + 2) : null, Status: r.Status, CreatingAt: null,
                DeploymentId: r.Status is "Created" or "Removed" ? $"MCM2{i:0000}" : null, ShiftedMinutes: 0, DpGroup: "", SuccessThresholdPercent: r.Threshold)).ToList(),
            IsClosed: false);
        lock (Runs)
        {
            Runs.Add(run);
            Versions[runId] = 1;
        }
        return run;
    }
}
