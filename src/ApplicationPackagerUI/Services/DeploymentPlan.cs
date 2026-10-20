using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerUI.Services;

/// <summary>One line of a deployment plan: the change it makes, or the reason it is skipped.</summary>
public sealed record DeploymentPlanEntry(ChangePlanItem Item, DeploymentChange? Change, string? SkipReason = null)
{
    public static DeploymentPlanEntry Write(ChangePlanItem item, DeploymentChange change) => new(item, change);

    public static DeploymentPlanEntry Skip(ChangePlanItem item, string reason) => new(item, null, reason);
}

/// <summary>
/// The changes behind a deployment change plan. The ConfigMgr changes travel only with the plan
/// items they belong to, and <see cref="DeploymentWriter"/> sends them only for a plan the
/// operator confirmed. The outcomes of the last run stay here for the undo.
/// </summary>
public sealed class DeploymentPlan
{
    private readonly List<DeploymentOutcome> _outcomes = [];
    private readonly List<DeploymentOutcome> _undoOutcomes = [];

    public DeploymentPlan(string action, string title, string actionName, IEnumerable<DeploymentPlanEntry> entries, bool isDestructive = false,
        bool hasUndo = true, string? extraConfirmation = null, string? optionalChoice = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        Action = action;
        Entries = entries.ToArray();
        if (Entries.All(e => e.Change is null)) throw new ArgumentException("A deployment plan makes at least one change.", nameof(entries));
        Plan = new ChangePlan(title, actionName, Entries.Select(e => e.Item), isDestructive, hasUndo)
        {
            ExtraConfirmation = extraConfirmation, OptionalChoice = optionalChoice, LogName = action
        };
    }

    /// <summary>True for a plan made of the undo changes of an earlier run.</summary>
    public bool IsUndo { get; init; }

    /// <summary>True when the second check box names an available time in the past.</summary>
    public bool AcknowledgesBackdated { get; init; }

    /// <summary>The short action name for the log and the site history, for example "Create deployment".</summary>
    public string Action { get; }

    public ChangePlan Plan { get; }

    public IReadOnlyList<DeploymentPlanEntry> Entries { get; }

    public IReadOnlyList<DeploymentChange> Changes => Entries.Where(e => e.Change is not null).Select(e => e.Change!).ToList();

    public IReadOnlyList<DeploymentOutcome> Outcomes
    {
        get { lock (_outcomes) return _outcomes.ToArray(); }
    }

    public IReadOnlyList<DeploymentOutcome> UndoOutcomes
    {
        get { lock (_undoOutcomes) return _undoOutcomes.ToArray(); }
    }

    /// <summary>The undo changes of the outcomes that changed ConfigMgr, failed outcomes that made part of their change included.</summary>
    public IReadOnlyList<(DeploymentPlanEntry Entry, DeploymentChange Undo)> UndoChanges()
    {
        var undo = new List<(DeploymentPlanEntry, DeploymentChange)>();
        foreach (var outcome in Outcomes)
        {
            if (outcome.UndoChange is not { } change) continue;
            if (EntryFor(outcome.Change) is { } entry) undo.Add((entry, change));
        }
        return undo;
    }

    /// <summary>This plan with only the outcomes whose undo change is still to be made; null when every undo change succeeded.</summary>
    public DeploymentPlan? Remaining(IReadOnlyList<DeploymentOutcome> undoOutcomes)
    {
        bool Done(DeploymentChange undo) => undoOutcomes.Any(o => o.Succeeded && (ReferenceEquals(o.Change, undo) || SameTarget(o.Change, undo)));
        var left = Outcomes.Where(o => o.UndoChange is { } undo && EntryFor(o.Change) is not null && !Done(undo)).ToList();
        if (left.Count == 0) return null;
        var copy = new DeploymentPlan(Action, Plan.Title, Plan.ActionName, Entries, Plan.IsDestructive, Plan.HasUndo, Plan.ExtraConfirmation, Plan.OptionalChoice)
        {
            IsUndo = IsUndo, AcknowledgesBackdated = AcknowledgesBackdated
        };
        foreach (var outcome in left) copy.AddOutcome(outcome);
        return copy;
    }

    internal void ClearOutcomes()
    {
        lock (_outcomes) _outcomes.Clear();
        ClearUndoOutcomes();
    }

    internal void ClearUndoOutcomes()
    {
        lock (_undoOutcomes) _undoOutcomes.Clear();
    }

    internal void AddOutcome(DeploymentOutcome outcome)
    {
        lock (_outcomes) _outcomes.Add(outcome);
    }

    internal void AddUndoOutcome(DeploymentOutcome outcome)
    {
        lock (_undoOutcomes) _undoOutcomes.Add(outcome);
    }

    public DeploymentPlanEntry? EntryFor(DeploymentChange change) =>
        Entries.FirstOrDefault(e => ReferenceEquals(e.Change, change))
        ?? Entries.FirstOrDefault(e => e.Change is { } c && SameTarget(c, change));

    /// <summary>The same kind of change on the same object, collection, deployment and DP groups.</summary>
    public static bool SameTarget(DeploymentChange a, DeploymentChange b)
    {
        static string Key(DeploymentChange c) =>
            $"{c.Kind}|{c.Object?.Type}|{c.Object?.Id}|{c.Object?.Program}|{c.Collection?.CollectionId}|{c.Existing?.DeploymentId}|{string.Join(",", c.DpGroups)}";
        return string.Equals(Key(a), Key(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the confirmed plan is this plan: the same items, in the same order, with the same second confirmation and optional choice.</summary>
    public bool IsConfirmedPlan(ConfirmedChangePlan confirmed)
    {
        var items = confirmed.Plan.Items;
        if (items.Count != Entries.Count) return false;
        for (var i = 0; i < items.Count; i++)
            if (!ReferenceEquals(items[i], Entries[i].Item)) return false;
        return confirmed.Plan.ExtraConfirmation == Plan.ExtraConfirmation && confirmed.Plan.OptionalChoice == Plan.OptionalChoice;
    }
}

/// <summary>
/// The only caller of <see cref="IDeploymentService.ApplyAsync"/> in the desktop. Every method
/// takes a <see cref="ConfirmedChangePlan"/>, which only the change plan window issues, and sends
/// the changes of that plan's items and nothing else. A Required create reaches the service with
/// its acknowledgement only when the confirmed plan asked the second check box.
/// </summary>
public sealed class DeploymentWriter(IDeploymentService service, ISiteOperationHistory history, IAppLog log, Func<string> siteTarget,
    Func<string, Task<bool>> confirmRemoval)
{
    public const string RemovalNotConfirmed = "Skipped: the removal was not confirmed. Nothing was changed.";

    public const string ToolName = "Deployment Helper";
    public const string LogArea = "Deployments";
    public const string NoResultText = "No result: the service did not report this change.";
    public const string SkippedPrefix = "Skipped";
    public const string NotAcknowledgedText =
        "Skipped: this Required deployment installs software on every device of the collection, and the plan did not ask the second confirmation for it. Nothing was changed. Show the plan again.";

    /// <summary>The run that a closed plan window left working; a completed task when none works.</summary>
    public Task Running
    {
        get { lock (_runLock) return _running; }
    }

    private readonly object _runLock = new();
    private Task _running = Task.CompletedTask;

    private Task<ChangePlanResult> Track(Task<ChangePlanResult> run)
    {
        lock (_runLock) _running = run;
        return run;
    }

    public ChangePlanRun RunFor(DeploymentPlan plan) => (confirmed, progress, ct) => Track(ApplyAsync(confirmed, plan, progress, ct));

    /// <summary>
    /// The Undo of the plan window. The plan the operator confirmed created the deployments, so
    /// their removal asks its own confirmation first; a declined removal changes nothing and the
    /// Undo stays offered on the page.
    /// </summary>
    public ChangePlanRun UndoFor(DeploymentPlan plan) => async (confirmed, progress, ct) =>
    {
        var undo = plan.UndoChanges();
        if (undo.Count > 0 && !await confirmRemoval(RemovalQuestion(undo.Select(u => u.Undo).ToList())))
        {
            log.Info(LogArea, $"Undo {plan.Action}: the removal was not confirmed.");
            var rows = undo.Select(u => ChangePlanItemResult.Skip(u.Entry.Item, RemovalNotConfirmed)).ToList();
            foreach (var row in rows) progress.Report(row);
            return new ChangePlanResult(rows, ChangePlanStop.Completed);
        }
        return await Track(UndoAsync(confirmed, plan, progress, ct));
    };

    /// <summary>The question that names each deployment the Undo removes.</summary>
    public static string RemovalQuestion(IReadOnlyList<DeploymentChange> removals)
    {
        static string Name(DeploymentChange change)
        {
            var existing = change.Existing;
            var obj = existing?.ObjectName is { Length: > 0 } name ? name : change.Object?.Name ?? existing?.ObjectId ?? "";
            var collectionName = existing?.CollectionName is { Length: > 0 } c ? c : change.Collection?.Name ?? "";
            var collectionId = existing?.CollectionId is { Length: > 0 } id ? id : change.Collection?.CollectionId ?? "";
            var collection = collectionId.Length > 0 ? $"{collectionName} ({collectionId})".Trim() : collectionName;
            return $"{obj} from {collection}";
        }
        var names = removals.Select(Name).ToList();
        var subject = names.Count == 1 ? "the deployment " + names[0] : "the deployments " + string.Join("; ", names);
        return $"Remove {subject}? Software that clients already installed stays installed.";
    }

    public async Task<ChangePlanResult> ApplyAsync(ConfirmedChangePlan confirmed, DeploymentPlan plan, IProgress<ChangePlanItemResult> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsConfirmedPlan(confirmed)) throw new InvalidOperationException("The confirmed plan is not the plan of these changes.");
        var acknowledged = confirmed.Plan.ExtraConfirmation is not null;
        var (changes, held) = Confirmed(plan.Changes, acknowledged, acknowledged && plan.AcknowledgesBackdated, confirmed.OptionChosen);
        plan.ClearOutcomes();

        var results = new Dictionary<ChangePlanItem, ChangePlanItemResult>(ReferenceEqualityComparer.Instance);
        foreach (var entry in plan.Entries.Where(e => e.Change is null))
        {
            var skipped = ChangePlanItemResult.Skip(entry.Item, entry.SkipReason ?? "Skipped.");
            results[entry.Item] = skipped;
            progress.Report(skipped);
        }

        var reporter = new OutcomeReporter(plan, results, progress, null);
        foreach (var change in held) reporter.Final(new DeploymentOutcome(Change: change, Succeeded: false, Message: NotAcknowledgedText, UndoChange: null, Steps: []));
        var stop = SiteOperationOutcome.Failed;
        try
        {
            IReadOnlyList<DeploymentOutcome> outcomes = changes.Count == 0 ? [] : await Task.Run(() => service.ApplyAsync(changes, reporter, ct), ct).ConfigureAwait(true);
            foreach (var outcome in outcomes) reporter.Final(outcome);
            var rows = new ChangePlanResult(plan.Entries.Select(e => results.TryGetValue(e.Item, out var r) ? r : new ChangePlanItemResult(e.Item, false, NoResultText)).ToList());
            var cancelled = ct.IsCancellationRequested && LeftUndone(plan.Entries.Where(e => e.Change is not null).Select(e => e.Item), rows.Items);
            var result = rows with { Stop = cancelled ? ChangePlanStop.Cancelled : ChangePlanStop.Completed, UndoAvailable = plan.UndoChanges().Count > 0 };
            stop = Outcome(result, cancelled);
            return result;
        }
        catch (OperationCanceledException)
        {
            stop = SiteOperationOutcome.Cancelled;
            throw;
        }
        finally
        {
            Record(plan.Action, stop, results.Values);
        }
    }

    /// <summary>Runs the undo changes of the last run of the plan; each result belongs to the plan item it undoes.</summary>
    public async Task<ChangePlanResult> UndoAsync(ConfirmedChangePlan confirmed, DeploymentPlan plan, IProgress<ChangePlanItemResult> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsConfirmedPlan(confirmed)) throw new InvalidOperationException("The confirmed plan is not the plan of these changes.");
        var undo = plan.UndoChanges();
        plan.ClearUndoOutcomes();
        var results = new Dictionary<ChangePlanItem, ChangePlanItemResult>(ReferenceEqualityComparer.Instance);
        if (undo.Count == 0) return new ChangePlanResult([]);

        // An undo never carries an acknowledgement: it removes a deployment and creates none.
        var changes = undo.Select(u => Unacknowledged(u.Undo)).ToList();
        var reporter = new OutcomeReporter(plan, results, progress, change =>
            undo.Where((_, i) => ReferenceEquals(changes[i], change)).Select(u => u.Entry).FirstOrDefault()
            ?? undo.FirstOrDefault(u => DeploymentPlan.SameTarget(u.Undo, change)).Entry);
        var stop = SiteOperationOutcome.Failed;
        try
        {
            IReadOnlyList<DeploymentOutcome> outcomes = await Task.Run(() => service.ApplyAsync(changes, reporter, ct), ct).ConfigureAwait(true);
            foreach (var outcome in outcomes) reporter.Final(outcome);
            var rows = undo.Select(u => results.TryGetValue(u.Entry.Item, out var r) ? r : new ChangePlanItemResult(u.Entry.Item, false, NoResultText)).ToList();
            var cancelled = ct.IsCancellationRequested && LeftUndone(undo.Select(u => u.Entry.Item), rows);
            var result = new ChangePlanResult(rows, cancelled ? ChangePlanStop.Cancelled : ChangePlanStop.Completed);
            stop = Outcome(result, cancelled);
            return result;
        }
        catch (OperationCanceledException)
        {
            stop = SiteOperationOutcome.Cancelled;
            throw;
        }
        finally
        {
            Record("Undo " + plan.Action, stop, results.Values);
        }
    }

    private static DeploymentChange Unacknowledged(DeploymentChange change) =>
        change.RequiredConfirmed || change.BackdatedAcknowledged || change.CreateNowContentDistributing
            ? change with { RequiredConfirmed = false, BackdatedAcknowledged = false, CreateNowContentDistributing = false }
            : change;

    /// <summary>True when the change creates a deployment that enforces at a deadline.</summary>
    public static bool IsRequiredCreate(DeploymentChange change) =>
        change.Kind == DeploymentChangeKind.Create && string.Equals(change.Options?.Purpose, "Required", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The changes to send and the changes held back; the flags a change already had never travel.
    /// Without <paramref name="acknowledged"/> a Required create is held back. A create carries
    /// RequiredConfirmed when it is Required and the plan's second box was acknowledged,
    /// BackdatedAcknowledged when that box also named the past available time, and
    /// CreateNowContentDistributing when the operator chose it and the create distributes first.
    /// </summary>
    public static (IReadOnlyList<DeploymentChange> Send, IReadOnlyList<DeploymentChange> Held) Confirmed(
        IReadOnlyList<DeploymentChange> changes, bool acknowledged, bool backdated, bool createNow)
    {
        var send = new List<DeploymentChange>(changes.Count);
        var held = new List<DeploymentChange>();
        foreach (var change in changes)
        {
            if (!acknowledged && IsRequiredCreate(change))
            {
                held.Add(change);
                continue;
            }
            send.Add(change.Kind == DeploymentChangeKind.Create
                ? change with
                {
                    RequiredConfirmed = acknowledged && IsRequiredCreate(change),
                    BackdatedAcknowledged = acknowledged && backdated,
                    CreateNowContentDistributing = createNow && change.DpGroups.Count > 0
                }
                : Unacknowledged(change));
        }
        return (send, held);
    }

    private static SiteOperationOutcome Outcome(ChangePlanResult result, bool cancelled) =>
        cancelled ? SiteOperationOutcome.Cancelled
        : result.FailedCount == 0 ? SiteOperationOutcome.Succeeded
        : result.SucceededCount > 0 || result.PartialCount > 0 ? SiteOperationOutcome.PartlySucceeded
        : SiteOperationOutcome.Failed;

    private static bool LeftUndone(IEnumerable<ChangePlanItem> writes, IReadOnlyList<ChangePlanItemResult> rows)
    {
        var items = new HashSet<ChangePlanItem>(writes, ReferenceEqualityComparer.Instance);
        return rows.Any(r => items.Contains(r.Item) && (r.Skipped || r.Message == NoResultText));
    }

    private void Record(string action, SiteOperationOutcome outcome, IEnumerable<ChangePlanItemResult> results)
    {
        var rows = results.ToList();
        var changed = rows.Count(r => !r.Skipped && (r.Succeeded || r.PartialChange));
        var failed = rows.Count(r => !r.Succeeded && !r.Skipped);
        var skipped = rows.Count(r => r.Skipped);
        log.Info(LogArea, $"{action}: outcome={outcome} changed={changed} failed={failed} skipped={skipped}.");
        string target;
        try { target = siteTarget(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { target = ""; }
        history.Record(new SiteOperationEntry(ToolName, action, target, outcome,
            new Dictionary<string, int> { ["changed"] = changed, ["failed"] = failed, ["skipped"] = skipped }));
    }

    /// <summary>Turns each service outcome into the result of its plan item once.</summary>
    private sealed class OutcomeReporter(
        DeploymentPlan plan,
        Dictionary<ChangePlanItem, ChangePlanItemResult> results,
        IProgress<ChangePlanItemResult> progress,
        Func<DeploymentChange, DeploymentPlanEntry?>? undoEntry) : IProgress<DeploymentOutcome>
    {
        private readonly HashSet<DeploymentOutcome> _seen = new(ReferenceEqualityComparer.Instance);

        // The writer sends copies with the acknowledgements set; a copy finds the entry of its original by target.
        private DeploymentPlanEntry? EntryOf(DeploymentChange change) => undoEntry is null ? plan.EntryFor(change) : undoEntry(change);

        public void Report(DeploymentOutcome value) => Final(value);

        public void Final(DeploymentOutcome value)
        {
            ArgumentNullException.ThrowIfNull(value);
            ChangePlanItemResult result;
            lock (results)
            {
                if (!_seen.Add(value)) return;
                var entry = EntryOf(value.Change);
                if (entry is null || results.ContainsKey(entry.Item)) return;
                if (undoEntry is null) plan.AddOutcome(value);
                else plan.AddUndoOutcome(value);
                var message = SecretMask.Apply(value.Message);
                // A cancelled run returns the changes it did not start as failed outcomes without an
                // undo whose message starts with the skipped prefix.
                var skipped = !value.Succeeded && value.UndoChange is null
                    && value.Message.StartsWith(SkippedPrefix, StringComparison.OrdinalIgnoreCase);
                result = skipped
                    ? ChangePlanItemResult.Skip(entry.Item, message)
                    : new ChangePlanItemResult(entry.Item, value.Succeeded, message) { PartialChange = !value.Succeeded && value.UndoChange is not null };
                results[entry.Item] = result;
            }
            progress.Report(result);
        }
    }
}
