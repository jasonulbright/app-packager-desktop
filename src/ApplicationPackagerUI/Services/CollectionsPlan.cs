using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerUI.Services;

/// <summary>One line of a collection plan: the change it makes, or the reason it is skipped.</summary>
public sealed record CollectionPlanEntry(ChangePlanItem Item, CollectionChange? Change, string? SkipReason = null)
{
    public static CollectionPlanEntry Write(ChangePlanItem item, CollectionChange change) => new(item, change);

    public static CollectionPlanEntry Skip(ChangePlanItem item, string reason) => new(item, null, reason);
}

/// <summary>
/// The changes behind a change plan. The ConfigMgr changes travel only with the plan items they
/// belong to, and <see cref="CollectionWriter"/> sends them only for a plan the operator
/// confirmed. The outcomes of the last run stay here for the undo.
/// </summary>
public sealed class CollectionPlan
{
    private readonly List<CollectionOutcome> _outcomes = [];

    public CollectionPlan(string action, string title, string actionName, IEnumerable<CollectionPlanEntry> entries, bool isDestructive = false, bool hasUndo = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        Action = action;
        Entries = entries.ToArray();
        if (Entries.All(e => e.Change is null)) throw new ArgumentException("A collection plan makes at least one change.", nameof(entries));
        Plan = new ChangePlan(title, actionName, Entries.Select(e => e.Item), isDestructive, hasUndo);
    }

    /// <summary>True for a plan made of the undo changes of an earlier run.</summary>
    public bool IsUndo { get; init; }

    /// <summary>The short action name for the log and the site history, for example "Remove".</summary>
    public string Action { get; }

    public ChangePlan Plan { get; }

    public IReadOnlyList<CollectionPlanEntry> Entries { get; }

    public IReadOnlyList<CollectionChange> Changes => Entries.Where(e => e.Change is not null).Select(e => e.Change!).ToList();

    public IReadOnlyList<CollectionOutcome> Outcomes
    {
        get { lock (_outcomes) return _outcomes.ToArray(); }
    }

    /// <summary>The undo changes of the outcomes that changed ConfigMgr, failed outcomes that made part of their change included.</summary>
    public IReadOnlyList<(CollectionPlanEntry Entry, CollectionChange Undo)> UndoChanges()
    {
        var undo = new List<(CollectionPlanEntry, CollectionChange)>();
        foreach (var outcome in Outcomes)
        {
            if (outcome.UndoChange is not { } change) continue;
            if (EntryFor(outcome.Change) is { } entry) undo.Add((entry, change));
        }
        return undo;
    }

    /// <summary>The outcomes of the last undo run from the plan window, one for each undo change sent.</summary>
    public IReadOnlyList<CollectionOutcome> UndoOutcomes
    {
        get { lock (_undoOutcomes) return _undoOutcomes.ToArray(); }
    }

    private readonly List<CollectionOutcome> _undoOutcomes = [];

    /// <summary>
    /// This plan with only the outcomes whose undo change is still to be made after an undo run
    /// reported <paramref name="undoOutcomes"/>; null when every undo change succeeded.
    /// </summary>
    public CollectionPlan? Remaining(IReadOnlyList<CollectionOutcome> undoOutcomes)
    {
        bool Done(CollectionChange undo) => undoOutcomes.Any(o => o.Succeeded && (ReferenceEquals(o.Change, undo) || SameTarget(o.Change, undo)));
        var left = Outcomes.Where(o => o.UndoChange is { } undo && EntryFor(o.Change) is not null && !Done(undo)).ToList();
        if (left.Count == 0) return null;
        var copy = new CollectionPlan(Action, Plan.Title, Plan.ActionName, Entries, Plan.IsDestructive, Plan.HasUndo) { IsUndo = IsUndo };
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

    internal void AddOutcome(CollectionOutcome outcome)
    {
        lock (_outcomes) _outcomes.Add(outcome);
    }

    internal void AddUndoOutcome(CollectionOutcome outcome)
    {
        lock (_undoOutcomes) _undoOutcomes.Add(outcome);
    }

    /// <summary>The entry of a change: the same object, or the same kind, collection, rule and query.</summary>
    public CollectionPlanEntry? EntryFor(CollectionChange change) =>
        Entries.FirstOrDefault(e => ReferenceEquals(e.Change, change))
        ?? Entries.FirstOrDefault(e => e.Change is { } c && SameTarget(c, change));

    internal static bool SameTarget(CollectionChange a, CollectionChange b)
    {
        static string Rule(CollectionChange c) => (c.RuleAfter ?? c.RuleBefore) is { } r ? $"{r.Kind}|{r.RuleName}|{r.ResourceId}|{r.IncludedCollectionId}" : "";
        static string Query(CollectionChange c) => (c.QueryAfter ?? c.QueryBefore) is { } q ? $"{q.QueryId}|{q.Name}" : "";
        static string Name(CollectionChange c) => c.AfterRow?.Name ?? c.CollectionName;
        return a.Kind == b.Kind
            && string.Equals(a.CollectionId, b.CollectionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Name(a), Name(b), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Rule(a), Rule(b), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Query(a), Query(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the confirmed plan is this plan: the same items, in the same order.</summary>
    public bool IsConfirmedPlan(ConfirmedChangePlan confirmed)
    {
        var items = confirmed.Plan.Items;
        if (items.Count != Entries.Count) return false;
        for (var i = 0; i < items.Count; i++)
            if (!ReferenceEquals(items[i], Entries[i].Item)) return false;
        return true;
    }
}

/// <summary>
/// The only caller of <see cref="ICollectionService.ApplyAsync"/> in the desktop. Every method
/// takes a <see cref="ConfirmedChangePlan"/>, which only the change plan window issues, and sends
/// the changes of that plan's items and nothing else.
/// </summary>
public sealed class CollectionWriter(ICollectionService service, ISiteOperationHistory history, IAppLog log, Func<string> siteTarget)
{
    public const string ToolName = "Collection and Compliance";
    public const string LogArea = "Collections";
    public const string NoResultText = "No result: the service did not report this change.";
    public const string SkippedPrefix = "Skipped";
    public const string RecreatedNote = "A recreated collection is a new collection with a new ID. Deployments and maintenance windows are not restored.";
    public const string NewQueryIdNote = "A recreated query has a new query ID.";

    public ChangePlanRun RunFor(CollectionPlan plan) => (confirmed, progress, ct) => ApplyAsync(confirmed, plan, progress, ct);

    public ChangePlanRun UndoFor(CollectionPlan plan) => (confirmed, progress, ct) => UndoAsync(confirmed, plan, progress, ct);

    public async Task<ChangePlanResult> ApplyAsync(ConfirmedChangePlan confirmed, CollectionPlan plan, IProgress<ChangePlanItemResult> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsConfirmedPlan(confirmed)) throw new InvalidOperationException("The confirmed plan is not the plan of these changes.");
        plan.ClearOutcomes();

        var results = new Dictionary<ChangePlanItem, ChangePlanItemResult>(ReferenceEqualityComparer.Instance);
        foreach (var entry in plan.Entries.Where(e => e.Change is null))
        {
            var skipped = ChangePlanItemResult.Skip(entry.Item, entry.SkipReason ?? "Skipped.");
            results[entry.Item] = skipped;
            progress.Report(skipped);
        }

        var reporter = new OutcomeReporter(plan, results, progress, null);
        var stop = SiteOperationOutcome.Failed;
        try
        {
            var outcomes = await Task.Run(() => service.ApplyAsync(plan.Changes, reporter, ct), ct).ConfigureAwait(true);
            foreach (var outcome in outcomes) reporter.Report(outcome);
            var rows = new ChangePlanResult(plan.Entries.Select(e => results.TryGetValue(e.Item, out var r) ? r : new ChangePlanItemResult(e.Item, false, NoResultText)).ToList());
            var cancelled = ct.IsCancellationRequested && LeftUndone(plan.Entries.Where(e => e.Change is not null).Select(e => e.Item), rows.Items);
            var result = rows with { Stop = cancelled ? ChangePlanStop.Cancelled : ChangePlanStop.Completed };
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
    public async Task<ChangePlanResult> UndoAsync(ConfirmedChangePlan confirmed, CollectionPlan plan, IProgress<ChangePlanItemResult> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsConfirmedPlan(confirmed)) throw new InvalidOperationException("The confirmed plan is not the plan of these changes.");
        var undo = plan.UndoChanges();
        plan.ClearUndoOutcomes();
        var results = new Dictionary<ChangePlanItem, ChangePlanItemResult>(ReferenceEqualityComparer.Instance);
        if (undo.Count == 0) return new ChangePlanResult([]);

        var reporter = new OutcomeReporter(plan, results, progress, change =>
            undo.FirstOrDefault(u => ReferenceEquals(u.Undo, change)).Entry
            ?? undo.FirstOrDefault(u => CollectionPlan.SameTarget(u.Undo, change)).Entry);
        var stop = SiteOperationOutcome.Failed;
        try
        {
            var outcomes = await Task.Run(() => service.ApplyAsync(undo.Select(u => u.Undo).ToList(), reporter, ct), ct).ConfigureAwait(true);
            foreach (var outcome in outcomes) reporter.Report(outcome);
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

    private static SiteOperationOutcome Outcome(ChangePlanResult result, bool cancelled) =>
        cancelled ? SiteOperationOutcome.Cancelled
        : result.FailedCount == 0 ? SiteOperationOutcome.Succeeded
        : result.SucceededCount > 0 || result.PartialCount > 0 ? SiteOperationOutcome.PartlySucceeded
        : SiteOperationOutcome.Failed;

    /// <summary>True when a change of the run was not made because of the cancel; a cancel during the last change leaves none.</summary>
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

    /// <summary>Turns each service outcome into the result of its plan item once; an outcome reported and returned again counts once.</summary>
    private sealed class OutcomeReporter(
        CollectionPlan plan,
        Dictionary<ChangePlanItem, ChangePlanItemResult> results,
        IProgress<ChangePlanItemResult> progress,
        Func<CollectionChange, CollectionPlanEntry?>? undoEntry) : IProgress<CollectionOutcome>
    {
        private readonly HashSet<CollectionOutcome> _seen = new(ReferenceEqualityComparer.Instance);

        public void Report(CollectionOutcome value)
        {
            ArgumentNullException.ThrowIfNull(value);
            ChangePlanItemResult result;
            lock (results)
            {
                if (!_seen.Add(value)) return;
                var entry = undoEntry is null ? plan.EntryFor(value.Change) : undoEntry(value.Change);
                if (entry is null || results.ContainsKey(entry.Item)) return;
                if (undoEntry is null) plan.AddOutcome(value);
                else plan.AddUndoOutcome(value);
                var message = SecretMask.Apply(value.Message);
                if ((undoEntry is not null || plan.IsUndo) && value.Succeeded)
                {
                    if (value.Change.Kind == CollectionChangeKind.Create) message = $"{message} {RecreatedNote}".Trim();
                    if (value.Change.Kind == CollectionChangeKind.SaveQuery) message = $"{message} {NewQueryIdNote}".Trim();
                }
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
