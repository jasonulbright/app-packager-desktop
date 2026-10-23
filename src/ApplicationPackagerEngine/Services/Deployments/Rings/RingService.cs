using System.Globalization;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Ring deployments: one object through one plan into one ordinary deployment per ring. Every
/// ring is a part 1 create (the five checks, the schedule rules, the prepared snapshot, the
/// Required acknowledgement, the read-back, the audit record) made by the deployment service;
/// this service adds the run file, the order of the rings, Promote, Reconcile and Undo run.
/// <para>
/// A run file is written before the first create, all rings Held. Each ring is saved Creating
/// with the time and account before its create and Created with the deployment ID after the
/// read-back, so a crash leaves a Creating ring that Reconcile resolves by the creation proof.
/// Promote, Reconcile, Undo run and Close run take the run's lock file and read the file again
/// under it; Apply refuses a run file whose fingerprint differs from the one Prepare read.
/// </para>
/// <para>
/// Limits: a deployment another admin makes with identical values to the ring's collection in
/// the window between a crash and Reconcile can be adopted as the ring's own; the lock only
/// excludes sessions that share the runs folder; the audit chain and the creation proof stop
/// mistakes and casual edits, not a process of the same account that writes matching records.
/// </para>
/// </summary>
public sealed class RingService : IRingService
{
    public const string LogArea = "Deployments";

    /// <summary>
    /// A Creating ring with no matching deployment returns to Held after this time. It is also the
    /// adoption window: a deployment ConfigMgr shows as made later than the create start plus this
    /// time is never adopted.
    /// </summary>
    public static readonly TimeSpan CreatingWait = TimeSpan.FromMinutes(5);

    /// <summary>A deployment made this much before the recorded create start still counts: clock skew between this computer and the site server.</summary>
    public static readonly TimeSpan CreationSkew = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A Creating ring that Reconcile still cannot resolve this long after its create started gets
    /// a note that offers Abandon Creating ring; nothing is abandoned without the operator.
    /// </summary>
    public static readonly TimeSpan AbandonAfter = TimeSpan.FromHours(1);

    private static readonly string Skipped = DeploymentService.SkippedPrefix;

    private readonly IDeploymentService _deployments;
    private readonly IDeploymentSiteReader _site;
    private readonly IDeploymentAuditLog _audit;
    private readonly RingPlanStore _plans;
    private readonly RingRunStore _runs;
    private readonly IAppLog? _log;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeZoneInfo _local;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The run store of the current operation, pinned to one folder at its start.</summary>
    private readonly AsyncLocal<RingRunStore?> _pinned = new();

    private RingRunStore Runs => _pinned.Value ?? _runs;

    private void Pin() => _pinned.Value = _runs.Pinned();

    public RingService(IDeploymentService deployments, IDeploymentSiteReader site, IDeploymentAuditLog audit, RingPlanStore plans, RingRunStore runs, IAppLog? log = null,
        Func<DateTime>? utcNow = null, TimeZoneInfo? local = null)
    {
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        _site = site ?? throw new ArgumentNullException(nameof(site));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _local = local ?? TimeZoneInfo.Local;
    }

    /// <summary>The most audit records read to find the create record of a ring deployment.</summary>
    public int OwnershipRecords { get; init; } = 100_000;

    private DateTime UtcNow => DateTime.SpecifyKind(_utcNow(), DateTimeKind.Utc);
    private DateTime LocalNow => DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, _local), DateTimeKind.Unspecified);
    private DateTime NowIn(string timeBasedOn) => RingPlanRules.NowIn(timeBasedOn, UtcNow, _local);

    private static PreflightCheck Check(string result, string message) => new(RingPlanRules.CheckName, result, message);
    private static PreflightCheck Fail(string message) => Check(DeploymentCheckResults.Fail, message);
    private static string Label(int index, string name) => RingPlanRules.Label(index, name);
    private static string Count(int n, string noun) => DeploymentService.Count(n, noun);

    // ---- Plans and folders --------------------------------------------------------------------

    public IReadOnlyList<RingPlan> ListPlans() => _plans.List();

    public string? PlanMessage => _plans.Message;

    public RingPlan? SavePlan(RingPlan plan, bool replace) => _plans.Save(plan, replace);

    public void DeletePlan(string name) => _plans.Delete(name);

    public string? RunMessage { get; private set; }

    public RingFolderProbe ProbeFolders()
    {
        var problems = new List<string>();
        var plans = _plans.Folder;
        var runs = _runs.Folder;
        var plansWritable = Writable(plans, "ring plans", problems);
        var runsWritable = Writable(runs, "ring runs", problems);
        var lockCreated = false;
        if (runsWritable)
        {
            var path = Path.Combine(runs, "probe-" + Guid.NewGuid().ToString("N") + ".lock");
            try
            {
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                File.Delete(path);
                lockCreated = true;
            }
            catch (Exception ex) when (RingPlanStore.IsStorageFailure(ex))
            {
                problems.Add($"A lock file cannot be made in the ring runs folder: {runs}.");
            }
        }
        var unc = runs.StartsWith(@"\\", StringComparison.Ordinal);
        return new RingFolderProbe(plans, plansWritable, runs, runsWritable, lockCreated, unc, string.Join(" ", problems), RingRunStore.StaleLockAge);
    }

    private static bool Writable(string folder, string what, List<string> problems)
    {
        try
        {
            if (!Path.IsPathFullyQualified(folder))
            {
                problems.Add($"The {what} folder must be a full path: {folder}.");
                return false;
            }
            if (DataFolderLinks.HasLink(folder, Path.GetPathRoot(folder) ?? folder))
            {
                problems.Add($"The {what} folder is a junction or symbolic link: {folder}.");
                return false;
            }
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "probe-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(path, "probe");
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (RingPlanStore.IsStorageFailure(ex))
        {
            problems.Add($"The {what} folder cannot be written: {folder}.");
            return false;
        }
    }

    // ---- Expand -------------------------------------------------------------------------------

    public async Task<IReadOnlyList<RingExpansionRow>> ExpandAsync(RingPlan plan, DeployableObject obj, DateTime planStart, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(obj);
        var errors = RingPlanRules.Validate(plan);
        if (errors.Count > 0) throw new DeploymentServiceException("The plan cannot be used: " + string.Join(" ", errors));
        var rows = RingPlanRules.Expand(plan, planStart, _local);
        var collections = (await _deployments.ListCollectionsAsync(ct).ConfigureAwait(false)).ToDictionary(c => c.CollectionId, StringComparer.OrdinalIgnoreCase);
        var rules = RingPlanRules.CheckRows(rows, NowIn(plan.TimeBasedOn));
        var result = rows.Select(r =>
        {
            var checks = r.Checks.ToList();
            checks.AddRange(rules.Errors.Where(e => e.Index == r.Index || (e.Index == 0 && r.Index == 1)).Select(e => Fail(e.Message)));
            checks.AddRange(rules.Warnings.Where(w => w.Index == r.Index).Select(w => Check(DeploymentCheckResults.Warn, w.Message)));
            if (r.CollectionId.Length > 0 && !DeploymentService.IsBuiltInCollection(r.CollectionId) && !collections.ContainsKey(r.CollectionId))
                checks.Add(Fail($"{Label(r.Index, r.Name)} collection {r.CollectionId} is not a device collection on the site."));
            if (r.DpGroup.Length > 0 && obj.Type == DeploymentObjectType.SoftwareUpdateGroup)
                checks.Add(Check(DeploymentCheckResults.Warn, $"{Label(r.Index, r.Name)} names DP group {r.DpGroup}; a software update group has no content to distribute, so it is not used."));
            return collections.TryGetValue(r.CollectionId, out var c)
                ? r with { CollectionName = c.Name, MemberCount = c.MemberCount, Checks = checks }
                : r with { Checks = checks };
        }).ToList();
        _log?.Info(LogArea, $"Expanded a ring plan with {result.Count} rings.");
        return result;
    }

    // ---- Runs and live counts -----------------------------------------------------------------

    public Task<IReadOnlyList<RingRun>> ListRunsAsync(bool closed, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var runs = _runs.Pinned().List(closed, out var message);
        RunMessage = message;
        return runs;
    }, ct);

    public async Task<IReadOnlyList<RingLiveCounts>> LiveCountsAsync(RingRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var created = run.Rings.Where(r => r.Status == RingStatuses.Created).ToList();
        if (created.Count == 0) return [];
        var rows = await _site.LiveAsync(run.ObjectType, created.Select(r => r.DeploymentId ?? "").ToList(), ct).ConfigureAwait(false);
        return created.Select((r, i) => Live(run.ObjectType, r, rows[i])).ToList();
    }

    private static RingLiveCounts Live(DeploymentObjectType type, RingRunRing ring, DeploymentLiveRow row)
    {
        var id = ring.DeploymentId ?? "";
        if (!DeploymentService.IsDeploymentId(type, id))
            return new RingLiveCounts(ring.Index, id, RingLiveStates.Error, 0, 0, 0, 0, 0, false,
                $"Deployment ID '{id}' does not have the form of {(type is DeploymentObjectType.Application or DeploymentObjectType.SoftwareUpdateGroup ? "an AssignmentUniqueID" : "an AdvertisementID")}, so a deletion cannot be confirmed.");
        if (row.Exists is null)
            return new RingLiveCounts(ring.Index, id, RingLiveStates.Error, 0, 0, 0, 0, 0, false, "The deployment could not be read from ConfigMgr. The counts are not known.");
        if (row.Exists == false)
            return new RingLiveCounts(ring.Index, id, RingLiveStates.NotFound, 0, 0, 0, 0, 0, false, $"Deployment {id} does not exist any more.");
        if (row.Targeted is { } targeted)
            return new RingLiveCounts(ring.Index, id, RingLiveStates.Summarized, targeted, row.Success, row.Errors, row.InProgress, row.Unknown, true, "")
            { Other = row.Other, SummarizationTime = row.SummarizationTime };
        return new RingLiveCounts(ring.Index, id, RingLiveStates.Found, 0, 0, 0, 0, 0, false,
            row.SummaryRead ? "The site has not summarized this deployment yet." : "The deployment exists; its summary could not be read.");
    }

    // ---- Reconcile ----------------------------------------------------------------------------

    public async Task<RingRun> ReconcileAsync(string runId, CancellationToken ct)
    {
        Pin();
        var found = FindOpen(runId);
        using var held = Runs.Lock(found.FileName);
        var run = Runs.Read(found.FileName);
        var rings = run.Rings.ToList();
        var changed = false;
        for (var i = 0; i < rings.Count; i++)
        {
            if (rings[i].Status != RingStatuses.Creating) continue;
            var resolved = await ResolveCreatingAsync(run, rings[i], ct).ConfigureAwait(false);
            changed |= resolved.Status != rings[i].Status;
            rings[i] = resolved;
        }
        var created = rings.Where(r => r.Status == RingStatuses.Created).ToList();
        if (created.Count > 0)
        {
            var live = await _site.LiveAsync(run.ObjectType, created.Select(r => r.DeploymentId ?? "").ToList(), ct).ConfigureAwait(false);
            for (var k = 0; k < created.Count; k++)
            {
                var counts = Live(run.ObjectType, created[k], live[k]);
                var i = rings.FindIndex(r => r.Index == created[k].Index);
                if (counts.State == RingLiveStates.NotFound)
                {
                    rings[i] = rings[i] with { Status = RingStatuses.Removed, Note = $"Deployment {counts.DeploymentId} does not exist any more; the ring is marked Removed." };
                    changed = true;
                }
                else if (counts.State == RingLiveStates.Error) rings[i] = rings[i] with { Note = counts.Message };
            }
        }
        var notes = rings.ToDictionary(r => r.Index, r => r.Note);
        var current = run with { Rings = rings };
        if (changed) current = Runs.Save(current, held);
        if (Finished(current.Rings))
        {
            current = Runs.Close(current, held);
            _log?.Info(LogArea, "Reconcile closed a finished ring run.");
        }
        _log?.Info(LogArea, $"Reconciled a ring run: {rings.Count(r => r.Status == RingStatuses.Created)} created, {rings.Count(r => r.Status == RingStatuses.Creating)} unresolved, {rings.Count(r => r.Status == RingStatuses.Removed)} removed.");
        return current with { Rings = [.. current.Rings.Select(r => r with { Note = notes.GetValueOrDefault(r.Index, "") })] };
    }

    /// <summary>
    /// A Creating ring: exactly one deployment of the run's object to the ring's collection made by
    /// the account that started the create, no earlier than the create start less the clock skew,
    /// holding the ring's planned values and recorded by no other ring, is the ring's own; two or
    /// more stay Creating; none returns the ring to Held once <see cref="CreatingWait"/> passed.
    /// </summary>
    private async Task<RingRunRing> ResolveCreatingAsync(RingRun run, RingRunRing ring, CancellationToken ct)
    {
        if (ObjectOf(run) is not { } obj)
            return ring with { Note = "This run was written by another tool without the application CI_ID, so the create cannot be resolved here. Reconcile it in that tool." };
        if (ring.Problem.Length > 0 || ring.Definition is null)
            return ring with { Note = $"{(ring.Problem.Length > 0 ? ring.Problem : "The options of the ring cannot be read.")} The create cannot be resolved; check the deployments in the console." };
        var startUtc = ring.CreatingAtUtc ?? LocalToUtc(ring.CreatingAt);
        if (startUtc is null) return ring with { Note = "The run file holds no time for this create, so it cannot be resolved. Check the deployments in the console." };
        var options = RingPlanRules.Options(run.ObjectType, ring.Definition, ring.AvailableTime, ring.Deadline, run.TimeBasedOn == RingPlanRules.Utc);
        IReadOnlyList<DeploymentCandidate> candidates;
        IReadOnlySet<string> otherRuns;
        IReadOnlyList<DeploymentAuditRecord> records;
        try
        {
            candidates = await _site.CandidatesAsync(obj, ring.CollectionId, options, ct).ConfigureAwait(false);
            otherRuns = Runs.OtherRunDeploymentIds(run.RunId, out var unreadable);
            if (unreadable is not null)
                return ring with { Note = $"A run file cannot be read, so it cannot be shown that a deployment belongs to no other run ({unreadable}). The ring stays Creating." };
            records = _audit.Read(OwnershipRecords);
        }
        catch (DeploymentServiceException ex)
        {
            return ring with { Note = $"The create could not be resolved: {ex.Message} The ring stays Creating." };
        }
        var account = ring.CreatingBy ?? run.CreatedBy;
        var recorded = run.Rings.Where(r => r.Index != ring.Index).Select(r => r.DeploymentId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A deployment another run lists, or whose create record names another run, another ring or
        // no run (a single deployment), belongs to someone else even when it holds the same values.
        bool Foreign(DeploymentCandidate c) => recorded.Contains(c.Deployment.DeploymentId) || otherRuns.Contains(c.Deployment.DeploymentId)
            || records.Any(r => r.Event == AuditEvents.Create && string.Equals(r.DeploymentUniqueID, c.Deployment.DeploymentId, StringComparison.OrdinalIgnoreCase)
                && !(string.Equals(r.RunId, run.RunId, StringComparison.OrdinalIgnoreCase) && r.RingIndex == ring.Index));
        bool InWindow(DateTime created) => created >= startUtc.Value - CreationSkew && created <= startUtc.Value + CreatingWait;
        var own = candidates.Where(c => c.Differences.Count == 0 && !Foreign(c)).ToList();
        var matches = own.Where(c => c.CreatedUtc is { } created && InWindow(created)
            && string.Equals(c.CreatedBy.Trim(), account.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        // A deployment that holds the ring's values but whose creator and time ConfigMgr does not
        // show yet may be the ring's own: it is neither adopted nor ignored.
        var unproven = own.Where(c => (c.CreatedUtc is null || string.IsNullOrWhiteSpace(c.CreatedBy))
            && (c.CreatedUtc is not { } at || InWindow(at))
            && (string.IsNullOrWhiteSpace(c.CreatedBy) || string.Equals(c.CreatedBy.Trim(), account.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
        var abandon = UtcNow - startUtc.Value >= AbandonAfter
            ? $" The create started more than {AbandonAfter.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes ago. Check collection {ring.CollectionId} in the console; if no deployment of this ring must stay there, use Abandon Creating ring."
            : "";
        if (matches.Count + unproven.Count > 1)
            return ring with { Note = $"{Count(matches.Count + unproven.Count, "deployment")} match this ring ({string.Join(", ", matches.Concat(unproven).Select(m => m.Deployment.DeploymentId))}). Remove the ones that must not stay in the console, then reconcile again.{abandon}" };
        if (unproven.Count == 1)
            return ring with { Note = $"Deployment {unproven[0].Deployment.DeploymentId} holds the ring's values, but ConfigMgr does not show yet who created it and when. The ring stays Creating; reconcile again in a minute.{abandon}" };
        if (matches.Count == 1)
            return ring with
            {
                Status = RingStatuses.Created, DeploymentId = matches[0].Deployment.DeploymentId, CreatingAt = null, CreatingAtUtc = null, CreatingBy = null,
                Note = $"Deployment {matches[0].Deployment.DeploymentId} was made by {account} after the create started and holds the ring's values; it was adopted into the run."
            };
        if (matches.Count > 1)
            return ring with { Note = $"{Count(matches.Count, "deployment")} match this ring ({string.Join(", ", matches.Select(m => m.Deployment.DeploymentId))}). Remove the ones that must not stay in the console, then reconcile again." };
        if (UtcNow - startUtc.Value >= CreatingWait)
            return ring with { Status = RingStatuses.Held, CreatingAt = null, CreatingAtUtc = null, CreatingBy = null, Note = "No matching deployment was found; the ring returned to Held and can be promoted again." };
        return ring with { Note = $"No matching deployment is visible yet. Reconcile again when {CreatingWait.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes have passed since the create started." };
    }

    private DateTime? LocalToUtc(DateTime? local)
    {
        if (local is not { } value) return null;
        var wall = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        if (_local.IsInvalidTime(wall)) wall = wall.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(wall, _local);
    }

    /// <summary>No ring is Held or Creating, or the first such ring is Held after a Removed or Cancelled ring.</summary>
    internal static bool Finished(IReadOnlyList<RingRunRing> rings)
    {
        var ordered = rings.OrderBy(r => r.Index).ToList();
        var next = ordered.FindIndex(r => r.Status is RingStatuses.Held or RingStatuses.Creating);
        if (next < 0) return true;
        if (ordered[next].Status == RingStatuses.Creating) return false;
        return next > 0 && ordered[next - 1].Status is RingStatuses.Removed or RingStatuses.Cancelled;
    }

    // ---- Shared helpers -----------------------------------------------------------------------

    private RingRun FindOpen(string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) throw new DeploymentServiceException("Select a run.");
        return Runs.Find(runId) ?? throw new DeploymentServiceException("The run is closed or its file is not in the runs folder any more. Refresh the runs.", DeploymentFailureKind.Stale);
    }

    /// <summary>The run's object for the deployment service; null for an application run of another tool that holds no CI_ID.</summary>
    private static DeployableObject? ObjectOf(RingRun run)
    {
        if (!DeploymentService.IsObjectId(run.ObjectType, run.ObjectId)) return null;
        return new DeployableObject(run.ObjectType, run.ObjectId, run.ObjectName, "", run.Program, false, run.ModelId);
    }

    /// <summary>The problems of the run file that block Promote and Undo run; null when there are none.</summary>
    private static string? RunProblem(RingRun run) =>
        run.Problems.Count == 0 ? null : $"The run file cannot be used for this action: {string.Join(" ", run.Problems)} Fix the run file or close the run.";

    private static RingDefinition Plain(RingRunRing ring) =>
        new(ring.Name, ring.CollectionId, ring.CollectionName, ring.Purpose, 0, null, "DisplayAll", false, false, false, ring.DpGroup, ring.SuccessThresholdPercent, null, null, null);

    private static bool ContentInProgress(DeploymentChange change) =>
        change.Checks.Any(c => c.Name == DeploymentCheckNames.ContentDistributed && c.Result == DeploymentCheckResults.Warn
            && c.Message.StartsWith("Content distribution in progress", StringComparison.Ordinal));

    /// <summary>The part 1 create of one ring, before PrepareAsync.</summary>
    private static DeploymentChange RingCreate(DeploymentObjectType type, DeployableObject obj, RingDefinition ring, string collectionId, string collectionName, int members,
        DateTime available, DateTime? deadline, bool utc, RingChange request, DeploymentRingContext context)
    {
        var groups = ring.DpGroup.Length > 0 && type != DeploymentObjectType.SoftwareUpdateGroup ? new[] { ring.DpGroup } : [];
        return new DeploymentChange(DeploymentChangeKind.Create, obj, new DeploymentCollection(collectionId, collectionName, "", members),
            RingPlanRules.Options(type, ring, available, deadline, utc), groups, request.CreateNowContentDistributing && groups.Length > 0, request.RequiredConfirmed,
            request.BackdatedAcknowledged, null, [], [], null) { Ring = context };
    }

    /// <summary>The prepared create with the "create now" choice kept only when its content is still distributing.</summary>
    private async Task<DeploymentChange> PrepareRingAsync(DeploymentChange change, CancellationToken ct)
    {
        DeploymentChange prepared;
        try
        {
            prepared = await _deployments.PrepareAsync(change, ct).ConfigureAwait(false);
        }
        catch (DeploymentServiceException ex)
        {
            return change with { Checks = [new PreflightCheck(DeploymentCheckNames.TargetExists, DeploymentCheckResults.Unknown, ex.Message)], Impact = [] };
        }
        if (prepared.CreateNowContentDistributing && !ContentInProgress(prepared))
            prepared = prepared with
            {
                CreateNowContentDistributing = false,
                Impact = [.. prepared.Impact.Select(l => l.StartsWith("Then creates the deployment while", StringComparison.Ordinal)
                    ? "Then creates the deployment; the content check passes now." : l)]
            };
        return prepared with { Ring = change.Ring };
    }

    private static IEnumerable<PreflightCheck> Labelled(int index, string name, IEnumerable<PreflightCheck> checks) =>
        checks.Select(c => c.Message.StartsWith("Ring ", StringComparison.Ordinal) ? c : c with { Message = $"{Label(index, name)}: {c.Message}" });

    private static IEnumerable<string> RingLines(int index, string name, IEnumerable<string> lines) => lines.Select(l => $"{Label(index, name)}: {l}");

    /// <summary>The status a ring takes after its create: Created with an ID, Held when the deployment service marks that nothing was created, Creating when the result is not known.</summary>
    private static (string Status, string? Id) After(DeploymentOutcome outcome)
    {
        if (outcome.UndoChange?.CreatedId is { Length: > 0 } id) return (RingStatuses.Created, id);
        if (outcome.NotCreated) return (RingStatuses.Held, null);
        return (RingStatuses.Creating, null);
    }

    private static DeploymentOutcome NotAttempted(DeploymentChange change, string text) =>
        new(change, false, text.StartsWith(Skipped, StringComparison.Ordinal) ? text : Skipped + text, null, []) { NotCreated = true };

    private static RingChange UndoRequest(string runId) => new(RingChangeKind.UndoRun, null, null, [], null, runId, null, false, false, false, [], [], [], null);

    private static RingOutcome Refused(RingChange change, string message, RingRun? run = null) =>
        new(change, false, message.StartsWith(Skipped, StringComparison.Ordinal) ? message : Skipped + message, [], null, []) { Run = run };

    // ---- Prepare ------------------------------------------------------------------------------

    public async Task<RingChange> PrepareAsync(RingChange request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Pin();
        var prepared = request.Kind switch
        {
            RingChangeKind.CreateRun => await PrepareCreateRunAsync(request, ct).ConfigureAwait(false),
            RingChangeKind.Promote => await PreparePromoteAsync(request, ct).ConfigureAwait(false),
            RingChangeKind.UndoRun => await PrepareUndoRunAsync(request, ct).ConfigureAwait(false),
            RingChangeKind.CloseRun => PrepareCloseRun(request),
            _ => throw new DeploymentServiceException("The ring change kind is not known.")
        };
        _log?.Info(LogArea, $"Prepared ring {request.Kind}: {prepared.RingChanges.Count} deployments, {prepared.Checks.Count(c => c.Result is DeploymentCheckResults.Fail or DeploymentCheckResults.Unknown)} checks block.");
        return prepared;
    }

    private async Task<RingChange> PrepareCreateRunAsync(RingChange request, CancellationToken ct)
    {
        var plan = request.Plan ?? throw new DeploymentServiceException("Select a ring plan.");
        var obj = request.Object ?? throw new DeploymentServiceException("Select the object to deploy.");
        var rows = request.Rows ?? [];
        if (rows.Count == 0) throw new DeploymentServiceException("Expand the plan first.");
        var checks = RingPlanRules.Validate(plan).Select(Fail).ToList();
        var rules = RingPlanRules.CheckRows(rows, NowIn(plan.TimeBasedOn));
        checks.AddRange(rules.Errors.Where(e => e.Index == 0).Select(e => Fail(e.Message)));
        var utc = plan.TimeBasedOn == RingPlanRules.Utc;
        var preparedRows = new List<RingExpansionRow>();
        var ringChanges = new List<DeploymentChange>();
        var impact = new List<string>
        {
            plan.HoldLaterRings
                ? $"Starts a run of plan {plan.Name} for {DeploymentService.ObjectText(obj)}: creates ring 1 now and holds {Count(rows.Count - 1, "later ring")} for Promote."
                : $"Starts a run of plan {plan.Name} for {DeploymentService.ObjectText(obj)}: creates {Count(rows.Count, "deployment")}, one per ring, in ring order. The run stops at the first ring that is not created.",
            utc ? "All times are UTC." : "All times are the local time of the clients.",
            $"A run file is written to {Runs.Folder} before the first create."
        };
        foreach (var row in rows.OrderBy(r => r.Index))
        {
            var ring = row.Definition ?? (row.Index >= 1 && row.Index <= plan.Rings.Count ? plan.Rings[row.Index - 1] : Plain(new RingRunRing(row.Index, row.Name, row.CollectionId ?? "", row.CollectionName ?? "",
                row.Purpose, row.AvailableTime, row.Deadline, RingStatuses.Held, null, null, 0, row.DpGroup, row.SuccessThresholdPercent)));
            ring = ring with { CollectionId = row.CollectionId ?? "", CollectionName = row.CollectionName ?? "", Purpose = row.Purpose, DpGroup = row.DpGroup, SuccessThresholdPercent = row.SuccessThresholdPercent };
            var rowChecks = rules.Errors.Where(e => e.Index == row.Index).Select(e => Fail(e.Message))
                .Concat(rules.Warnings.Where(w => w.Index == row.Index).Select(w => Check(DeploymentCheckResults.Warn, w.Message))).ToList();
            var id = (row.CollectionId ?? "").Trim().ToUpperInvariant();
            var held = plan.HoldLaterRings && row.Index != rows.Min(r => r.Index);
            if (id.Length == 8 && !DeploymentService.IsBuiltInCollection(id))
            {
                var change = RingCreate(obj.Type, obj, ring, id, row.CollectionName ?? "", row.MemberCount, row.AvailableTime, row.Deadline, utc, request,
                    new DeploymentRingContext(plan.Name, row.Index, row.Name, ""));
                var prepared = await PrepareRingAsync(change, ct).ConfigureAwait(false);
                rowChecks.AddRange(prepared.Checks);
                preparedRows.Add(row with { CollectionId = id, CollectionName = prepared.Collection?.Name ?? row.CollectionName ?? "", MemberCount = prepared.Collection?.MemberCount ?? row.MemberCount, Checks = rowChecks });
                impact.AddRange(RingLines(row.Index, row.Name, held ? [$"held: created later by Promote. {prepared.Impact.FirstOrDefault(l => l.StartsWith("Creates a", StringComparison.Ordinal)) ?? ""}"] : prepared.Impact));
                if (!held) ringChanges.Add(prepared);
            }
            else preparedRows.Add(row with { Checks = rowChecks });
            checks.AddRange(Labelled(row.Index, row.Name, rowChecks));
        }
        var required = ringChanges.Where(c => c.NeedsRequiredConfirmation).ToList();
        if (required.Count > 0)
            impact.Add($"{Count(required.Count, "Required ring")} enforce on {Count(required.Sum(c => c.Collection?.MemberCount ?? 0), "device")} in total now. This needs its own confirmation.");
        impact.Add("Undo run removes the deployments this run created when each still holds the values it was created with; software that clients already installed stays installed.");
        return request with { Rows = preparedRows, RingChanges = ringChanges, Checks = checks, Impact = impact, Fingerprint = null };
    }

    /// <summary>Why the ring cannot be promoted now; null when it can.</summary>
    internal static string? PromoteProblem(RingRun run, int index)
    {
        if (run.IsClosed) return "The run is closed.";
        var ordered = run.Rings.OrderBy(r => r.Index).ToList();
        var position = ordered.FindIndex(r => r.Index == index);
        if (position < 0) return $"Ring {index.ToString(CultureInfo.InvariantCulture)} is not in this run.";
        var ring = ordered[position];
        if (ring.Status == RingStatuses.Creating) return $"{Label(ring.Index, ring.Name)} has a create whose result is not known. Reconcile the run first.";
        if (ring.Status != RingStatuses.Held) return $"{Label(ring.Index, ring.Name)} is {ring.Status}, not Held. The run file changed after it was loaded.";
        var next = ordered.FirstOrDefault(r => r.Status == RingStatuses.Held);
        if (next is not null && next.Index != index) return $"Ring {index.ToString(CultureInfo.InvariantCulture)} is not the next held ring; ring {next.Index.ToString(CultureInfo.InvariantCulture)} is.";
        if (position > 0 && ordered[position - 1].Status != RingStatuses.Created)
            return $"{Label(ordered[position - 1].Index, ordered[position - 1].Name)} is {ordered[position - 1].Status}; Promote needs it Created.";
        return null;
    }

    /// <summary>The threshold of the ring before the promoted one, read from the site now; null when the gate is open.</summary>
    private async Task<string?> ThresholdProblemAsync(RingRun run, int index, CancellationToken ct)
    {
        var ordered = run.Rings.OrderBy(r => r.Index).ToList();
        var position = ordered.FindIndex(r => r.Index == index);
        if (position <= 0) return null;
        var previous = ordered[position - 1];
        if (previous.SuccessThresholdPercent is null) return null;
        var counts = (await LiveCountsAsync(run with { Rings = [previous] }, ct).ConfigureAwait(false)).FirstOrDefault();
        var gate = RingPlanRules.Threshold(counts, previous.SuccessThresholdPercent);
        return gate.Met ? null : $"{Label(previous.Index, previous.Name)}: {gate.Reason}";
    }

    private async Task<RingChange> PreparePromoteAsync(RingChange request, CancellationToken ct)
    {
        var run = FindOpen(request.RunId);
        var index = request.RingIndex ?? throw new DeploymentServiceException("Select the ring to promote.");
        var ring = run.Rings.FirstOrDefault(r => r.Index == index) ?? throw new DeploymentServiceException($"Ring {index.ToString(CultureInfo.InvariantCulture)} is not in this run.");
        var checks = new List<PreflightCheck>();
        if (PromoteProblem(run, index) is { } problem) checks.Add(Fail(problem));
        if (RunProblem(run) is { } unreadable) checks.Add(Fail(unreadable));
        if (await ThresholdProblemAsync(run, index, ct).ConfigureAwait(false) is { } gate) checks.Add(Fail(gate));
        var obj = ObjectOf(run);
        if (obj is null) checks.Add(Fail("This run was written by another tool without the application CI_ID. Promote it in that tool."));
        var now = NowIn(run.TimeBasedOn);
        var shift = RingPlanRules.PromoteShift(ring.AvailableTime, ring.Deadline, now);
        var available = ring.AvailableTime.AddMinutes(shift);
        var deadline = ring.Deadline?.AddMinutes(shift);
        var row = new RingExpansionRow(ring.Index, ring.Name, ring.CollectionId, ring.CollectionName, 0, ring.Purpose, available, deadline, ring.DpGroup, ring.SuccessThresholdPercent, []) { Definition = ring.Definition };
        var rules = RingPlanRules.CheckRows([row], now);
        checks.AddRange(rules.Errors.Select(e => Fail(e.Message)));
        checks.AddRange(rules.Warnings.Select(w => Check(DeploymentCheckResults.Warn, w.Message)));
        var impact = new List<string> { $"Promotes {Label(ring.Index, ring.Name)} of plan {run.PlanName} for {(obj is null ? run.ObjectName : DeploymentService.ObjectText(obj))}." };
        if (shift > 0)
            impact.Add($"The planned deadline {ring.Deadline!.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} has passed. This ring and every later held ring moves {shift.ToString(CultureInfo.InvariantCulture)} minutes later, so the ring keeps its time from available to deadline.");
        var ringChanges = new List<DeploymentChange>();
        if (obj is not null && ring.Definition is not null && run.Problems.Count == 0 && ring.CollectionId.Length == 8 && !DeploymentService.IsBuiltInCollection(ring.CollectionId))
        {
            var change = RingCreate(run.ObjectType, obj, ring.Definition with { DpGroup = ring.DpGroup }, ring.CollectionId, ring.CollectionName, 0, available, deadline,
                run.TimeBasedOn == RingPlanRules.Utc, request, new DeploymentRingContext(run.PlanName, ring.Index, ring.Name, run.RunId));
            var prepared = await PrepareRingAsync(change, ct).ConfigureAwait(false);
            if (IdentityProblem(run, prepared.Object) is { } identity) checks.Add(Fail(identity));
            checks.AddRange(prepared.Checks);
            impact.AddRange(prepared.Impact);
            row = row with { CollectionName = prepared.Collection?.Name ?? row.CollectionName, MemberCount = prepared.Collection?.MemberCount ?? 0 };
            ringChanges.Add(prepared);
        }
        row = row with { Checks = checks };
        return request with { Rows = [row], RingChanges = ringChanges, Checks = [.. Labelled(ring.Index, ring.Name, checks)], Impact = impact, Fingerprint = run.Fingerprint, ShiftMinutes = shift };
    }

    /// <summary>Why the object the site holds now is not the object of the run; null when it is.</summary>
    private static string? IdentityProblem(RingRun run, DeployableObject? current)
    {
        if (current is null) return null;
        var what = DeploymentService.Noun(run.ObjectType).ToLowerInvariant();
        if (!string.Equals(current.Id, run.ObjectId, StringComparison.OrdinalIgnoreCase))
            return $"The run was started for {what} {run.ObjectId}, and ConfigMgr now holds {what} {current.Id} (a new revision or another object). Promote creates only the object of the run: start a new run.";
        if (!string.Equals(current.Name.Trim(), run.ObjectName.Trim(), StringComparison.OrdinalIgnoreCase))
            return $"The {what} of the run is now named {current.Name}, not {run.ObjectName}. Promote creates only the object of the run: start a new run.";
        if (run.ObjectType == DeploymentObjectType.Package && !string.Equals(current.Program, run.Program, StringComparison.OrdinalIgnoreCase))
            return $"Package {run.ObjectId} has no program {run.Program} any more.";
        return null;
    }

    private async Task<RingChange> PrepareUndoRunAsync(RingChange request, CancellationToken ct)
    {
        var (run, closed) = Locate(request.RunId);
        var checks = new List<PreflightCheck>();
        if (run.Rings.Any(r => r.Status == RingStatuses.Creating))
            checks.Add(Fail("A ring of the run has a create whose result is not known. Reconcile the run first."));
        if (closed && run.Rings.All(r => r.Status != RingStatuses.Created))
            checks.Add(Fail("The run is closed and holds no created deployment, so there is nothing to undo."));
        if (RunProblem(run) is { } unreadable) checks.Add(Fail(unreadable));
        IReadOnlyList<DeploymentAuditRecord> records = [];
        try
        {
            records = _audit.Read(OwnershipRecords);
        }
        catch (DeploymentServiceException ex)
        {
            checks.Add(Check(DeploymentCheckResults.Unknown, ex.Message));
        }
        var removes = new List<DeploymentChange>();
        var kept = new List<int>();
        var rows = new List<RingExpansionRow>();
        var impact = new List<string> { $"Undoes the run of plan {run.PlanName} for {run.ObjectName}." };
        foreach (var ring in run.Rings.Where(r => r.Status == RingStatuses.Created).OrderByDescending(r => r.Index))
        {
            // Only a create record bound to this run and this ring proves the deployment is the run's
            // own; a genuine record of another run or of a single deployment never does.
            var record = records.LastOrDefault(r => r.Event == AuditEvents.Create && r.Result is AuditResults.Success or AuditResults.Partial
                && string.Equals(r.DeploymentUniqueID, ring.DeploymentId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.RunId, run.RunId, StringComparison.OrdinalIgnoreCase) && r.RingIndex == ring.Index
                && string.Equals(r.ObjectId.Trim(), run.ObjectId.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.CollectionID.Trim(), ring.CollectionId.Trim(), StringComparison.OrdinalIgnoreCase));
            var remove = record is null ? null : DeploymentChangeBuilder.RemoveFromAudit(record);
            List<PreflightCheck> ringChecks;
            if (remove is null)
            {
                ringChecks = [new PreflightCheck(DeploymentService.CreatedHere, DeploymentCheckResults.Fail,
                    $"Deployment {ring.DeploymentId} cannot be proven to belong to this run: the audit log holds no intact create record of it for this run, ring {ring.Index.ToString(CultureInfo.InvariantCulture)}, object {run.ObjectId} and collection {ring.CollectionId}.")];
            }
            else
            {
                var prepared = await PrepareRingAsync(remove with { Ring = new DeploymentRingContext(run.PlanName, ring.Index, ring.Name, run.RunId) }, ct).ConfigureAwait(false);
                ringChecks = [.. prepared.Checks];
                if (!prepared.Blocked)
                {
                    removes.Add(prepared);
                    impact.AddRange(RingLines(ring.Index, ring.Name, prepared.Impact));
                }
            }
            var blocking = ringChecks.Where(c => c.Result is DeploymentCheckResults.Fail or DeploymentCheckResults.Unknown).ToList();
            if (blocking.Count > 0)
            {
                kept.Add(ring.Index);
                impact.Add($"{Label(ring.Index, ring.Name)}: deployment {ring.DeploymentId} is kept: {string.Join(" ", blocking.Select(c => c.Message))}");
            }
            rows.Add(new RingExpansionRow(ring.Index, ring.Name, ring.CollectionId, ring.CollectionName, 0, ring.Purpose, ring.AvailableTime, ring.Deadline, ring.DpGroup, ring.SuccessThresholdPercent, ringChecks));
        }
        var cancelled = run.Rings.Where(r => r.Status == RingStatuses.Held).Select(r => r.Index).Order().ToList();
        if (cancelled.Count > 0) impact.Add($"Cancels {Count(cancelled.Count, "held ring")} ({string.Join(", ", cancelled)}). Held rings were never created, so nothing changes in ConfigMgr for them.");
        impact.Add(closed
            ? "The run is closed and stays closed; its rings are marked Removed or Cancelled."
            : kept.Count == 0
                ? "Then closes the run."
                : $"The run stays open because {Count(kept.Count, "deployment")} is kept.");
        impact.Add("Software that clients already installed stays installed.");
        return request with { Rows = [.. rows.OrderBy(r => r.Index)], RingChanges = removes, Checks = checks, Impact = impact, Fingerprint = run.Fingerprint, KeptRings = [.. kept.Order()], CancelledRings = cancelled };
    }

    private RingChange PrepareCloseRun(RingChange request)
    {
        var run = FindOpen(request.RunId);
        var checks = new List<PreflightCheck>();
        if (run.Rings.Any(r => r.Status == RingStatuses.Creating))
            checks.Add(Fail("A ring of the run has a create whose result is not known. Reconcile the run first."));
        var cancelled = run.Rings.Where(r => r.Status == RingStatuses.Held).Select(r => r.Index).Order().ToList();
        var impact = new List<string>
        {
            $"Closes the run of plan {run.PlanName} for {run.ObjectName} and moves its file to the closed folder.",
            $"The deployments of {Count(run.Rings.Count(r => r.Status == RingStatuses.Created), "created ring")} stay in ConfigMgr. A closed run has no Undo run."
        };
        if (cancelled.Count > 0) impact.Add($"Cancels {Count(cancelled.Count, "held ring")} ({string.Join(", ", cancelled)}).");
        return request with { Checks = checks, Impact = impact, Fingerprint = run.Fingerprint, CancelledRings = cancelled, RingChanges = [] };
    }

    // ---- Apply --------------------------------------------------------------------------------

    public async Task<IReadOnlyList<RingOutcome>> ApplyAsync(IReadOnlyList<RingChange> changes, IProgress<RingOutcome> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(progress);
        Pin();
        var outcomes = new List<RingOutcome>();
        foreach (var change in changes)
        {
            RingOutcome outcome;
            if (ct.IsCancellationRequested) outcome = Refused(change, "the run was cancelled before this change.");
            else if (!await _gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
                outcome = Refused(change, "another ring change of this session is running. Try again when it finishes.");
            else
            {
                try
                {
                    outcome = change.Kind switch
                    {
                        RingChangeKind.CreateRun => await ApplyCreateRunAsync(change, progress, ct).ConfigureAwait(false),
                        RingChangeKind.Promote => await ApplyPromoteAsync(change, progress).ConfigureAwait(false),
                        RingChangeKind.UndoRun => await ApplyUndoRunAsync(change, progress).ConfigureAwait(false),
                        RingChangeKind.CloseRun => ApplyCloseRun(change),
                        _ => Refused(change, "the ring change kind is not known.")
                    };
                }
                catch (DeploymentServiceException ex)
                {
                    outcome = Refused(change, ex.Message);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log?.Warning(LogArea, $"A ring {change.Kind} stopped with an unexpected error: {ex.GetType().Name}.");
                    outcome = new RingOutcome(change, false, "The ring change stopped with an unexpected error. Refresh and reconcile the run to see the current state. The log has the details.", [], change.RunId is { } id ? UndoRequest(id) : null, []);
                }
                finally
                {
                    _gate.Release();
                }
            }
            _log?.Info(LogArea, $"Ring {change.Kind}: {(outcome.Succeeded ? "succeeded" : "not completed")}, {outcome.RingOutcomes.Count(o => o.Succeeded)} ring changes succeeded.");
            outcomes.Add(outcome);
            progress.Report(outcome);
        }
        return outcomes;
    }

    private static string? Unprepared(RingChange change)
    {
        if (change.Checks.Count == 0 && change.Impact.Count == 0) return "the change has no prepared checks, so it was not applied. Validate again.";
        if (change.Blocked)
            return $"the plan has a check that did not pass ({string.Join(" ", change.Checks.Where(c => c.Result is DeploymentCheckResults.Fail or DeploymentCheckResults.Unknown).Select(c => c.Message).Take(3))}).";
        return null;
    }

    private static string? ConfirmationProblem(RingChange change)
    {
        if (change.NeedsRequiredConfirmation && !change.RequiredConfirmed)
            return $"{Count(change.RequiredRings, "Required ring")} enforce on every device of {(change.RequiredRings == 1 ? "its collection" : "their collections")} and need their own confirmation, so nothing was created.";
        if (change.NeedsBackdatedAcknowledgement && !change.BackdatedAcknowledged)
            return "A ring is available in the past and needs its own confirmation, so nothing was created.";
        return null;
    }

    /// <summary>One ring's create with the run lock held: Creating saved first, the create, then the status it ended in.</summary>
    private async Task<(DeploymentOutcome Outcome, RingRun Run, string? SaveProblem)> CreateRingAsync(RingRunLock held, RingRun run, int index, DeploymentChange prepared, RingChange change)
    {
        var label = Label(index, prepared.Ring?.RingName ?? run.Rings.FirstOrDefault(r => r.Index == index)?.Name ?? "");
        RingRun current;
        try
        {
            current = Runs.Read(run.FileName);
            var ring = current.Rings.FirstOrDefault(r => r.Index == index);
            if (ring?.Status != RingStatuses.Held)
            {
                var text = $"{label} is {ring?.Status ?? "missing"} in the run file, not Held: another writer changed the run. Nothing was created for this ring; refresh the runs.";
                return (NotAttempted(prepared, text), current, text);
            }
            var account = Runs.User;
            current = Runs.Save(current with { Rings = [.. current.Rings.Select(r => r.Index == index ? r with { Status = RingStatuses.Creating, CreatingAt = LocalNow, CreatingAtUtc = UtcNow, CreatingBy = account, DeploymentId = null } : r)] }, held);
        }
        catch (DeploymentServiceException ex)
        {
            var text = $"The run file could not be updated before the create of {label}, so the ring was not attempted. {ex.Message}";
            return (NotAttempted(prepared, text), run, text);
        }
        var context = (prepared.Ring ?? new DeploymentRingContext(run.PlanName, index, "", run.RunId)) with { RunId = run.RunId };
        var ringChange = prepared with
        {
            RequiredConfirmed = change.RequiredConfirmed,
            BackdatedAcknowledged = change.BackdatedAcknowledged,
            CreateNowContentDistributing = change.CreateNowContentDistributing && prepared.DpGroups is { Count: > 0 } && ContentInProgress(prepared),
            Ring = context
        };
        var outcome = (await _deployments.ApplyAsync([ringChange], new Progress<DeploymentOutcome>(), CancellationToken.None).ConfigureAwait(false)).Single();
        var (status, id) = After(outcome);
        try
        {
            // Read again so the save changes only this ring of what the file holds now.
            var latest = Runs.Read(run.FileName);
            current = Runs.Save(latest with
            {
                Rings = [.. latest.Rings.Select(r => r.Index == index
                    ? status == RingStatuses.Creating ? r : r with { Status = status, DeploymentId = id, CreatingAt = null, CreatingAtUtc = null, CreatingBy = null }
                    : r)]
            }, held);
            return (outcome, current, null);
        }
        catch (DeploymentServiceException ex)
        {
            var problem = id is null
                ? $"The run file could not be updated after the create ({ex.Message}). The ring stays Creating; Reconcile the run."
                : $"Deployment {id} was created, but the run file could not be updated ({ex.Message}). The ring stays Creating; Reconcile the run to adopt the deployment. Do not create it again by hand.";
            return (outcome, current, problem);
        }
    }

    private async Task<RingOutcome> ApplyCreateRunAsync(RingChange change, IProgress<RingOutcome> progress, CancellationToken ct)
    {
        if (Unprepared(change) is { } unprepared) return Refused(change, unprepared);
        if (change.Plan is not { } plan || change.Object is null || change.RingChanges.Count == 0) return Refused(change, "the change has no prepared rings, so it was not applied. Validate again.");
        if (ConfirmationProblem(change) is { } confirmation) return Refused(change, confirmation);
        if (change.RingChanges.Any(c => c.Ring is null || c.Prepared is null)) return Refused(change, "a ring of the change was not prepared, so nothing was created. Validate again.");

        var first = change.RingChanges[0];
        var obj = first.Object!;
        var key = obj.Type == DeploymentObjectType.Application && first.Prepared!.ContentIds.Count == 1 ? first.Prepared.ContentIds[0] : obj.Id;
        var run = new RingRun(Guid.NewGuid().ToString(), "", plan.Name, plan.HoldLaterRings, plan.TimeBasedOn, obj.Type, obj.Id, obj.Name, obj.Type == DeploymentObjectType.Package ? obj.Program : "",
            Runs.User, UtcNow, [.. change.Rows.OrderBy(r => r.Index).Select(r => new RingRunRing(r.Index, r.Name, r.CollectionId, r.CollectionName, r.Purpose, r.AvailableTime, r.Deadline,
                RingStatuses.Held, null, null, 0, r.DpGroup, r.SuccessThresholdPercent) { Definition = (r.Definition ?? Plain(new RingRunRing(r.Index, r.Name, r.CollectionId, r.CollectionName, r.Purpose,
                    r.AvailableTime, r.Deadline, RingStatuses.Held, null, null, 0, r.DpGroup, r.SuccessThresholdPercent))) with { CollectionId = r.CollectionId, CollectionName = r.CollectionName } })], false)
        { ModelId = obj.ModelId ?? "" };
        RingRunLock held;
        try
        {
            (run, held) = Runs.CreateLocked(run, key);
        }
        catch (DeploymentServiceException ex)
        {
            return Refused(change, $"the run file could not be written, so nothing was created. {ex.Message}");
        }
        var outcomes = new List<DeploymentOutcome>();
        var steps = new List<string>();
        string? stop = null;
        using (held)
        {
            foreach (var ringChange in change.RingChanges)
            {
                var index = ringChange.Ring!.RingIndex;
                var label = Label(index, ringChange.Ring.RingName);
                if (stop is not null && !ct.IsCancellationRequested)
                {
                    outcomes.Add(NotAttempted(ringChange, "an earlier ring of the run did not succeed, so this ring was not attempted."));
                    continue;
                }
                if (ct.IsCancellationRequested)
                {
                    outcomes.Add(NotAttempted(ringChange, "the run was cancelled before this ring."));
                    stop = "the run was cancelled";
                    continue;
                }
                held.Touch();
                var (outcome, current, saveProblem) = await CreateRingAsync(held, run, index, ringChange, change).ConfigureAwait(false);
                run = current;
                outcomes.Add(outcome);
                steps.Add($"{label}: {outcome.Message}");
                if (saveProblem is not null) { steps.Add($"{label}: {saveProblem}"); stop = saveProblem; }
                else if (!outcome.Succeeded)
                    stop = After(outcome).Status == RingStatuses.Created ? $"{label} was created with a problem: {outcome.Message}" : $"{label} was not created: {outcome.Message}";
                progress.Report(new RingOutcome(change, outcome.Succeeded, $"{label}: {outcome.Message}", [.. outcomes], null, [.. steps]) { Final = false, Run = run });
            }
        }
        var created = outcomes.Count(o => After(o).Status == RingStatuses.Created);
        var unknown = outcomes.Count(o => After(o).Status == RingStatuses.Creating);
        var finalRun = TryRead(run);
        var message = stop is null
            ? plan.HoldLaterRings
                ? $"Created ring 1 of plan {plan.Name}. {Count(change.Rows.Count - 1, "later ring")} {(change.Rows.Count - 1 == 1 ? "is" : "are")} held for Promote."
                : $"Created {Count(created, "deployment")} of plan {plan.Name}, one per ring. ConfigMgr enforces each ring's times."
            : unknown > 0
                ? $"The run stopped: {stop} {Count(created, "deployment")} of the run {(created == 1 ? "was" : "were")} created; the result of {Count(unknown, "ring")} is not known. Reconcile the run before Promote or Undo run."
            : created == 0
                ? $"The run stopped: {stop} Nothing was created."
                : $"The run stopped: {stop} {Count(created, "deployment")} of the run {(created == 1 ? "was" : "were")} created and stay{(created == 1 ? "s" : "")}; Undo run removes them.";
        return new RingOutcome(change, stop is null, message, outcomes, created > 0 ? UndoRequest(run.RunId) : null, steps) { Run = finalRun };
    }

    private RingRun? TryRead(RingRun run)
    {
        try
        {
            return Runs.Read(run.FileName);
        }
        catch (DeploymentServiceException)
        {
            return null;
        }
    }

    /// <summary>The run under its lock, read again; a refusal when the file changed since Prepare. A closed run is found only with <paramref name="allowClosed"/>.</summary>
    private (RingRun Run, RingRunLock Lock, bool Closed)? Locked(RingChange change, out string? problem, bool allowClosed = false)
    {
        problem = null;
        RingRun found;
        var closed = false;
        try
        {
            if (allowClosed) (found, closed) = Locate(change.RunId);
            else found = FindOpen(change.RunId);
        }
        catch (DeploymentServiceException ex)
        {
            problem = ex.Message;
            return null;
        }
        RingRunLock held;
        try
        {
            held = Runs.Lock(found.FileName, closed);
        }
        catch (DeploymentServiceException ex)
        {
            problem = ex.Message;
            return null;
        }
        try
        {
            var run = Runs.Read(found.FileName, closed);
            if (!string.Equals(run.Fingerprint, change.Fingerprint, StringComparison.Ordinal))
            {
                held.Dispose();
                problem = "the run file changed since the plan was shown, so nothing was changed. Prepare the action again.";
                return null;
            }
            return (run, held, closed);
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    /// <summary>The open or closed run with the RunId and whether it is in the closed folder.</summary>
    private (RingRun Run, bool Closed) Locate(string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) throw new DeploymentServiceException("Select a run.");
        return Runs.Locate(runId) ?? throw new DeploymentServiceException("The run file is not in the runs folder or its closed folder any more. Refresh the runs.", DeploymentFailureKind.Stale);
    }

    private async Task<RingOutcome> ApplyPromoteAsync(RingChange change, IProgress<RingOutcome> progress)
    {
        if (Unprepared(change) is { } unprepared) return Refused(change, unprepared);
        if (change.RingIndex is not { } index || change.RingChanges.Count != 1 || change.RingChanges[0].Prepared is null)
            return Refused(change, "the change has no prepared ring, so it was not applied. Validate again.");
        if (ConfirmationProblem(change) is { } confirmation) return Refused(change, confirmation);
        if (Locked(change, out var problem) is not { } locked) return Refused(change, problem!);
        using var held = locked.Lock;
        var run = locked.Run;
        if (PromoteProblem(run, index) is { } promote) return Refused(change, promote, run);
        if (RunProblem(run) is { } unreadable) return Refused(change, unreadable, run);
        if (await ThresholdProblemAsync(run, index, CancellationToken.None).ConfigureAwait(false) is { } gate) return Refused(change, gate, run);
        if (change.ShiftMinutes != 0)
        {
            run = Runs.Save(run with
            {
                Rings = [.. run.Rings.Select(r => r.Index >= index && r.Status == RingStatuses.Held
                    ? r with { AvailableTime = r.AvailableTime.AddMinutes(change.ShiftMinutes), Deadline = r.Deadline?.AddMinutes(change.ShiftMinutes), ShiftedMinutes = r.ShiftedMinutes + change.ShiftMinutes }
                    : r)]
            }, held);
        }
        var (outcome, current, saveProblem) = await CreateRingAsync(held, run, index, change.RingChanges[0], change).ConfigureAwait(false);
        var label = Label(index, change.RingChanges[0].Ring?.RingName ?? "");
        var created = After(outcome).Status == RingStatuses.Created;
        var message = saveProblem is not null ? $"{label}: {outcome.Message} {saveProblem}"
            : created && outcome.Succeeded ? $"Promoted {label}: {outcome.Message}"
            : created ? $"{label} was created with a problem: {outcome.Message}"
            : outcome.Message.StartsWith(Skipped, StringComparison.Ordinal) ? outcome.Message
            : $"{label} was not promoted: {outcome.Message}";
        return new RingOutcome(change, outcome.Succeeded && saveProblem is null, message, [outcome], created ? UndoRequest(run.RunId) : null, [.. outcome.Steps]) { Run = TryRead(current) };
    }

    private async Task<RingOutcome> ApplyUndoRunAsync(RingChange change, IProgress<RingOutcome> progress)
    {
        if (Unprepared(change) is { } unprepared) return Refused(change, unprepared);
        if (Locked(change, out var problem, allowClosed: true) is not { } locked) return Refused(change, problem!);
        using var held = locked.Lock;
        var run = locked.Run;
        if (run.Rings.Any(r => r.Status == RingStatuses.Creating)) return Refused(change, "a ring of the run has a create whose result is not known. Reconcile the run first.", run);
        if (RunProblem(run) is { } unreadable) return Refused(change, unreadable, run);
        var outcomes = new List<DeploymentOutcome>();
        var removed = new HashSet<int>();
        var failed = new List<string>();
        foreach (var remove in change.RingChanges)
        {
            var index = remove.Ring?.RingIndex ?? 0;
            var ring = run.Rings.FirstOrDefault(r => r.Index == index);
            if (ring is null || ring.Status != RingStatuses.Created || !string.Equals(ring.DeploymentId, remove.Existing?.DeploymentId, StringComparison.OrdinalIgnoreCase))
            {
                outcomes.Add(new DeploymentOutcome(remove, false, Skipped + "the ring no longer holds this deployment in the run file.", null, []));
                continue;
            }
            var outcome = (await _deployments.ApplyAsync([remove], new Progress<DeploymentOutcome>(), CancellationToken.None).ConfigureAwait(false)).Single();
            outcomes.Add(outcome);
            if (outcome.Succeeded) removed.Add(index);
            else failed.Add($"{Label(index, ring.Name)}: deployment {ring.DeploymentId} was kept: {outcome.Message}");
            progress.Report(new RingOutcome(change, outcome.Succeeded, $"{Label(index, ring.Name)}: {outcome.Message}", [.. outcomes], null, []) { Final = false, Run = run });
        }
        var updated = run with
        {
            Rings = [.. run.Rings.Select(r => removed.Contains(r.Index) ? r with { Status = RingStatuses.Removed }
                : r.Status == RingStatuses.Held ? r with { Status = RingStatuses.Cancelled } : r)]
        };
        try
        {
            updated = Runs.Save(updated, held, locked.Closed);
        }
        catch (DeploymentServiceException ex)
        {
            return new RingOutcome(change, false,
                $"Removed {Count(removed.Count, "deployment")} of the run, but the run file could not be updated: {ex.Message} The run file still lists the removed deployments as Created; Reconcile marks them Removed.",
                outcomes, null, []) { Run = TryRead(run) };
        }
        var kept = updated.Rings.Where(r => r.Status == RingStatuses.Created).ToList();
        if (kept.Count == 0 && !locked.Closed)
        {
            try
            {
                updated = Runs.Close(updated, held);
            }
            catch (DeploymentServiceException ex)
            {
                return new RingOutcome(change, false, $"Undid the run, but it could not be closed: {ex.Message} Close the run when the folder can be written.", outcomes, null, []) { Run = TryRead(updated) };
            }
        }
        var message = kept.Count == 0
            ? $"Undid the run: removed {Count(removed.Count, "deployment")}, cancelled {Count(change.CancelledRings.Count, "held ring")}; {(locked.Closed ? "the run stays closed" : "the run is closed")}. Software that clients already installed stays installed."
            : $"Removed {Count(removed.Count, "deployment")} of the run. {Count(kept.Count, "deployment")} {(kept.Count == 1 ? "was" : "were")} kept: {string.Join(" ", kept.Select(r => failed.FirstOrDefault(f => f.StartsWith(Label(r.Index, r.Name), StringComparison.Ordinal)) ?? $"{Label(r.Index, r.Name)}: deployment {r.DeploymentId} was kept because it failed a check of the plan."))} The run stays {(locked.Closed ? "closed" : "open")}.";
        return new RingOutcome(change, kept.Count == 0, message, outcomes, null, []) { Run = updated };
    }

    private RingOutcome ApplyCloseRun(RingChange change)
    {
        if (Unprepared(change) is { } unprepared) return Refused(change, unprepared);
        if (Locked(change, out var problem) is not { } locked) return Refused(change, problem!);
        using var held = locked.Lock;
        var run = locked.Run;
        if (run.Rings.Any(r => r.Status == RingStatuses.Creating)) return Refused(change, "a ring of the run has a create whose result is not known. Reconcile the run first.", run);
        var cancelled = run.Rings.Count(r => r.Status == RingStatuses.Held);
        var closed = Runs.Close(run with { Rings = [.. run.Rings.Select(r => r.Status == RingStatuses.Held ? r with { Status = RingStatuses.Cancelled } : r)] }, held);
        return new RingOutcome(change, true, $"Closed the run of plan {run.PlanName}{(cancelled > 0 ? $" and cancelled {Count(cancelled, "held ring")}" : "")}. The deployments of the run stay in ConfigMgr.", [], null, []) { Run = closed };
    }
}
