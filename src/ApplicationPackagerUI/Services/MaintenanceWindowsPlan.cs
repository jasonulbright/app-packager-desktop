using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerUI.Services;

/// <summary>One line of a maintenance window plan: the change it makes, or the reason it is skipped.</summary>
public sealed record MaintenancePlanEntry(ChangePlanItem Item, MaintenanceChange? Change, string? SkipReason = null)
{
    public static MaintenancePlanEntry Write(ChangePlanItem item, MaintenanceChange change) => new(item, change);

    public static MaintenancePlanEntry Skip(ChangePlanItem item, string reason) => new(item, null, reason);
}

/// <summary>
/// The changes behind a change plan. The ConfigMgr changes travel only with the plan items
/// they belong to, and <see cref="MaintenanceWindowWriter"/> sends them only for a plan the
/// operator confirmed. The outcomes of the last run stay here for the undo.
/// </summary>
public sealed class MaintenancePlan
{
    private readonly List<MaintenanceOutcome> _outcomes = [];

    public MaintenancePlan(string action, string title, string actionName, IEnumerable<MaintenancePlanEntry> entries, bool isDestructive = false, bool hasUndo = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        Action = action;
        Entries = entries.ToArray();
        if (Entries.All(e => e.Change is null)) throw new ArgumentException("A maintenance plan makes at least one change.", nameof(entries));
        Plan = new ChangePlan(title, actionName, Entries.Select(e => e.Item), isDestructive, hasUndo);
    }

    /// <summary>The short action name for the log and the site history, for example "Remove".</summary>
    public string Action { get; }

    public ChangePlan Plan { get; }

    public IReadOnlyList<MaintenancePlanEntry> Entries { get; }

    public IReadOnlyList<MaintenanceChange> Changes => Entries.Where(e => e.Change is not null).Select(e => e.Change!).ToList();

    /// <summary>The outcomes the last run reported, in report order.</summary>
    public IReadOnlyList<MaintenanceOutcome> Outcomes
    {
        get { lock (_outcomes) return _outcomes.ToArray(); }
    }

    /// <summary>
    /// The undo changes of the outcomes that changed ConfigMgr. A failed outcome can carry one
    /// too: a rename whose remove failed created the new window.
    /// </summary>
    public IReadOnlyList<(MaintenancePlanEntry Entry, MaintenanceChange Undo)> UndoChanges()
    {
        var undo = new List<(MaintenancePlanEntry, MaintenanceChange)>();
        foreach (var outcome in Outcomes)
        {
            if (outcome.UndoChange is not { } change) continue;
            if (EntryFor(outcome.Change) is { } entry) undo.Add((entry, change));
        }
        return undo;
    }

    internal void ClearOutcomes()
    {
        lock (_outcomes) _outcomes.Clear();
    }

    internal void AddOutcome(MaintenanceOutcome outcome)
    {
        lock (_outcomes) _outcomes.Add(outcome);
    }

    /// <summary>The entry of a change: the same object, or the same kind, collection and window.</summary>
    public MaintenancePlanEntry? EntryFor(MaintenanceChange change) =>
        Entries.FirstOrDefault(e => ReferenceEquals(e.Change, change))
        ?? Entries.FirstOrDefault(e => e.Change is { } c && SameTarget(c, change));

    internal static bool SameTarget(MaintenanceChange a, MaintenanceChange b) =>
        a.Kind == b.Kind
        && string.Equals(a.CollectionId, b.CollectionId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.WindowName, b.WindowName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.NewWindowName ?? "", b.NewWindowName ?? "", StringComparison.OrdinalIgnoreCase);

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
/// The only caller of <see cref="IMaintenanceWindowService.ApplyAsync"/> in the desktop. Every
/// method takes a <see cref="ConfirmedChangePlan"/>, which only the change plan window issues,
/// and sends the changes of that plan's items and nothing else.
/// </summary>
public sealed class MaintenanceWindowWriter(IMaintenanceWindowService service, ISiteOperationHistory history, IAppLog log, Func<string> siteTarget)
{
    public const string ToolName = "Maintenance Windows";
    public const string LogArea = "MaintenanceWindows";
    public const string NoResultText = "No result: the service did not report this change.";
    public const string RecreatedNote = "A recreated window is a new window with a new id.";

    public ChangePlanRun RunFor(MaintenancePlan plan) => (confirmed, progress, ct) => ApplyAsync(confirmed, plan, progress, ct);

    public ChangePlanRun UndoFor(MaintenancePlan plan) => (confirmed, progress, ct) => UndoAsync(confirmed, plan, progress, ct);

    public async Task<ChangePlanResult> ApplyAsync(ConfirmedChangePlan confirmed, MaintenancePlan plan, IProgress<ChangePlanItemResult> progress, CancellationToken ct)
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

        var changes = plan.Changes;
        var reporter = new OutcomeReporter(plan, results, progress, null);
        var stop = SiteOperationOutcome.Failed;
        try
        {
            var outcomes = await Task.Run(() => service.ApplyAsync(changes, reporter, ct), ct).ConfigureAwait(true);
            foreach (var outcome in outcomes) reporter.Report(outcome);
            var rows = Result(plan, results);
            var cancelled = ct.IsCancellationRequested && LeftUndone(plan.Entries.Where(e => e.Change is not null).Select(e => e.Item), rows.Items);
            var result = rows with { Stop = cancelled ? ChangePlanStop.Cancelled : ChangePlanStop.Completed };
            stop = cancelled ? SiteOperationOutcome.Cancelled
                : result.FailedCount == 0 ? SiteOperationOutcome.Succeeded
                : result.SucceededCount > 0 ? SiteOperationOutcome.PartlySucceeded
                : SiteOperationOutcome.Failed;
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
    public async Task<ChangePlanResult> UndoAsync(ConfirmedChangePlan confirmed, MaintenancePlan plan, IProgress<ChangePlanItemResult> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsConfirmedPlan(confirmed)) throw new InvalidOperationException("The confirmed plan is not the plan of these changes.");
        var undo = plan.UndoChanges();
        var results = new Dictionary<ChangePlanItem, ChangePlanItemResult>(ReferenceEqualityComparer.Instance);
        if (undo.Count == 0) return new ChangePlanResult([]);

        var reporter = new OutcomeReporter(plan, results, progress, change =>
            undo.FirstOrDefault(u => ReferenceEquals(u.Undo, change)).Entry
            ?? undo.FirstOrDefault(u => MaintenancePlan.SameTarget(u.Undo, change)).Entry);
        var stop = SiteOperationOutcome.Failed;
        try
        {
            var outcomes = await Task.Run(() => service.ApplyAsync(undo.Select(u => u.Undo).ToList(), reporter, ct), ct).ConfigureAwait(true);
            foreach (var outcome in outcomes) reporter.Report(outcome);
            var rows = undo.Select(u => results.TryGetValue(u.Entry.Item, out var r) ? r : new ChangePlanItemResult(u.Entry.Item, false, NoResultText)).ToList();
            var cancelled = ct.IsCancellationRequested && LeftUndone(undo.Select(u => u.Entry.Item), rows);
            var result = new ChangePlanResult(rows, cancelled ? ChangePlanStop.Cancelled : ChangePlanStop.Completed);
            stop = cancelled ? SiteOperationOutcome.Cancelled
                : result.FailedCount == 0 ? SiteOperationOutcome.Succeeded
                : result.SucceededCount > 0 ? SiteOperationOutcome.PartlySucceeded
                : SiteOperationOutcome.Failed;
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

    /// <summary>
    /// True when a change of the run was not made because of the cancel. The service ends a
    /// cancelled run normally and returns the changes it did not start as skipped outcomes; a
    /// cancel that arrives during the last change leaves none, and the run counts as completed.
    /// </summary>
    private static bool LeftUndone(IEnumerable<ChangePlanItem> writes, IReadOnlyList<ChangePlanItemResult> rows)
    {
        var items = new HashSet<ChangePlanItem>(writes, ReferenceEqualityComparer.Instance);
        return rows.Any(r => items.Contains(r.Item) && (r.Skipped || r.Message == NoResultText));
    }

    private static ChangePlanResult Result(MaintenancePlan plan, Dictionary<ChangePlanItem, ChangePlanItemResult> results) =>
        new(plan.Entries.Select(e => results.TryGetValue(e.Item, out var r) ? r : new ChangePlanItemResult(e.Item, false, NoResultText)).ToList());

    private void Record(string action, SiteOperationOutcome outcome, IEnumerable<ChangePlanItemResult> results)
    {
        var rows = results.ToList();
        var changed = rows.Count(r => r.Succeeded && !r.Skipped);
        var failed = rows.Count(r => !r.Succeeded && !r.Skipped);
        var skipped = rows.Count(r => r.Skipped);
        log.Info(LogArea, $"{action}: outcome={outcome} changed={changed} failed={failed} skipped={skipped}.");
        string target;
        try { target = siteTarget(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { target = ""; }
        history.Record(new SiteOperationEntry(ToolName, action, target, outcome,
            new Dictionary<string, int> { ["changed"] = changed, ["failed"] = failed, ["skipped"] = skipped }));
    }

    /// <summary>
    /// Turns each service outcome into the result of its plan item once; an outcome reported
    /// through progress and returned again is not counted twice.
    /// </summary>
    private sealed class OutcomeReporter(
        MaintenancePlan plan,
        Dictionary<ChangePlanItem, ChangePlanItemResult> results,
        IProgress<ChangePlanItemResult> progress,
        Func<MaintenanceChange, MaintenancePlanEntry?>? undoEntry) : IProgress<MaintenanceOutcome>
    {
        private readonly HashSet<MaintenanceOutcome> _seen = new(ReferenceEqualityComparer.Instance);

        public void Report(MaintenanceOutcome value)
        {
            ArgumentNullException.ThrowIfNull(value);
            ChangePlanItemResult result;
            lock (results)
            {
                if (!_seen.Add(value)) return;
                var entry = undoEntry is null ? plan.EntryFor(value.Change) : undoEntry(value.Change);
                if (entry is null || results.ContainsKey(entry.Item)) return;
                if (undoEntry is null) plan.AddOutcome(value);
                var message = SecretMask.Apply(value.Message);
                if (undoEntry is not null && value.Succeeded && value.Change.Kind is ChangeKind.Create or ChangeKind.ApplyTemplate
                    && entry.Change?.Kind == ChangeKind.Remove)
                    message = $"{message} {RecreatedNote}".Trim();
                result = !value.Succeeded && value.UndoChange is null && value.Message.StartsWith(MaintenanceWindowService.SkippedPrefix, StringComparison.Ordinal)
                    ? ChangePlanItemResult.Skip(entry.Item, message)
                    : new ChangePlanItemResult(entry.Item, value.Succeeded, message);
                results[entry.Item] = result;
            }
            progress.Report(result);
        }
    }
}
