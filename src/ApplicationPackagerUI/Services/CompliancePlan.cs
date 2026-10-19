using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerUI.Services;

/// <summary>One line of a compliance plan: the change it makes, or the reason it is skipped.</summary>
public sealed record CompliancePlanEntry(ChangePlanItem Item, ComplianceChange? Change, string? SkipReason = null)
{
    public static CompliancePlanEntry Write(ChangePlanItem item, ComplianceChange change) => new(item, change);

    public static CompliancePlanEntry Skip(ChangePlanItem item, string reason) => new(item, null, reason);
}

/// <summary>
/// The changes behind a compliance change plan. The ConfigMgr changes travel only with the plan
/// items they belong to, and <see cref="ComplianceWriter"/> sends them only for a plan the
/// operator confirmed. The outcomes of the last run stay here for the undo.
/// </summary>
public sealed class CompliancePlan
{
    private readonly List<ComplianceOutcome> _outcomes = [];
    private readonly List<ComplianceOutcome> _undoOutcomes = [];

    public CompliancePlan(string action, string title, string actionName, IEnumerable<CompliancePlanEntry> entries, bool isDestructive = false,
        bool hasUndo = true, string? extraConfirmation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        Action = action;
        Entries = entries.ToArray();
        if (Entries.All(e => e.Change is null)) throw new ArgumentException("A compliance plan makes at least one change.", nameof(entries));
        Plan = new ChangePlan(title, actionName, Entries.Select(e => e.Item), isDestructive, hasUndo) { ExtraConfirmation = extraConfirmation };
    }

    /// <summary>True for a plan made of the undo changes of an earlier run.</summary>
    public bool IsUndo { get; init; }

    /// <summary>The short action name for the log and the site history, for example "Delete deployments".</summary>
    public string Action { get; }

    public ChangePlan Plan { get; }

    public IReadOnlyList<CompliancePlanEntry> Entries { get; }

    public IReadOnlyList<ComplianceChange> Changes => Entries.Where(e => e.Change is not null).Select(e => e.Change!).ToList();

    public IReadOnlyList<ComplianceOutcome> Outcomes
    {
        get { lock (_outcomes) return _outcomes.ToArray(); }
    }

    public IReadOnlyList<ComplianceOutcome> UndoOutcomes
    {
        get { lock (_undoOutcomes) return _undoOutcomes.ToArray(); }
    }

    /// <summary>The undo changes of the outcomes that changed ConfigMgr, failed outcomes that made part of their change included.</summary>
    public IReadOnlyList<(CompliancePlanEntry Entry, ComplianceChange Undo)> UndoChanges()
    {
        var undo = new List<(CompliancePlanEntry, ComplianceChange)>();
        foreach (var outcome in Outcomes)
        {
            if (outcome.UndoChange is not { } change) continue;
            if (EntryFor(outcome.Change) is { } entry) undo.Add((entry, change));
        }
        return undo;
    }

    /// <summary>This plan with only the outcomes whose undo change is still to be made; null when every undo change succeeded.</summary>
    public CompliancePlan? Remaining(IReadOnlyList<ComplianceOutcome> undoOutcomes)
    {
        bool Done(ComplianceChange undo) => undoOutcomes.Any(o => o.Succeeded && (ReferenceEquals(o.Change, undo) || SameTarget(o.Change, undo)));
        var left = Outcomes.Where(o => o.UndoChange is { } undo && EntryFor(o.Change) is not null && !Done(undo)).ToList();
        if (left.Count == 0) return null;
        var copy = new CompliancePlan(Action, Plan.Title, Plan.ActionName, Entries, Plan.IsDestructive, Plan.HasUndo, Plan.ExtraConfirmation) { IsUndo = IsUndo };
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

    internal void AddOutcome(ComplianceOutcome outcome)
    {
        lock (_outcomes) _outcomes.Add(outcome);
    }

    internal void AddUndoOutcome(ComplianceOutcome outcome)
    {
        lock (_undoOutcomes) _undoOutcomes.Add(outcome);
    }

    public CompliancePlanEntry? EntryFor(ComplianceChange change) =>
        Entries.FirstOrDefault(e => ReferenceEquals(e.Change, change))
        ?? Entries.FirstOrDefault(e => e.Change is { } c && SameTarget(c, change));

    /// <summary>The same kind of change on the same catalog entry, deployment, collection, item and setting.</summary>
    public static bool SameTarget(ComplianceChange a, ComplianceChange b)
    {
        static string Key(ComplianceChange c)
        {
            var deployment = c.DeploymentBefore is { } d ? $"{d.DeploymentId}|{d.BaselineId}|{d.CollectionId}" : "";
            var setting = (c.SettingBefore ?? c.SettingAfter) is { } s ? $"{s.SettingId}|{s.Name}" : "";
            return $"{c.Kind}|{c.CatalogId}|{deployment}|{c.ToCollectionId}|{c.CiId}|{setting}|{string.Join(",", c.TargetCollectionIds)}";
        }
        return string.Equals(Key(a), Key(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the confirmed plan is this plan: the same items, in the same order.</summary>
    public bool IsConfirmedPlan(ConfirmedChangePlan confirmed)
    {
        var items = confirmed.Plan.Items;
        if (items.Count != Entries.Count) return false;
        for (var i = 0; i < items.Count; i++)
            if (!ReferenceEquals(items[i], Entries[i].Item)) return false;
        return confirmed.Plan.ExtraConfirmation == Plan.ExtraConfirmation;
    }
}

/// <summary>
/// The only caller of <see cref="IComplianceService.ApplyAsync"/> in the desktop. Every method
/// takes a <see cref="ConfirmedChangePlan"/>, which only the change plan window issues, and sends
/// the changes of that plan's items and nothing else.
/// </summary>
public sealed class ComplianceWriter(IComplianceService service, ISiteOperationHistory history, IAppLog log, Func<string> siteTarget)
{
    public const string ToolName = "Collection and Compliance";
    public const string LogArea = "Compliance";
    public const string NoResultText = "No result: the service did not report this change.";
    public const string SkippedPrefix = "Skipped";
    public const string RecreatedNote = "A recreated object has a new ID.";

    public ChangePlanRun RunFor(CompliancePlan plan) => (confirmed, progress, ct) => ApplyAsync(confirmed, plan, progress, ct);

    public ChangePlanRun UndoFor(CompliancePlan plan) => (confirmed, progress, ct) => UndoAsync(confirmed, plan, progress, ct);

    public async Task<ChangePlanResult> ApplyAsync(ConfirmedChangePlan confirmed, CompliancePlan plan, IProgress<ChangePlanItemResult> progress, CancellationToken ct)
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
            foreach (var outcome in outcomes) reporter.Final(outcome);
            var rows = new ChangePlanResult(plan.Entries.Select(e => results.TryGetValue(e.Item, out var r) ? r : new ChangePlanItemResult(e.Item, false, NoResultText)).ToList());
            var cancelled = ct.IsCancellationRequested && LeftUndone(plan.Entries.Where(e => e.Change is not null).Select(e => e.Item), rows.Items);
            var result = rows with { Stop = cancelled ? ChangePlanStop.Cancelled : ChangePlanStop.Completed };
            stop = Outcome(result, cancelled);
            return result;
        }
        catch (OperationCanceledException)
        {
            reporter.KeepSteps();
            stop = SiteOperationOutcome.Cancelled;
            throw;
        }
        catch
        {
            reporter.KeepSteps();
            throw;
        }
        finally
        {
            Record(plan.Action, stop, results.Values);
        }
    }

    /// <summary>Runs the undo changes of the last run of the plan; each result belongs to the plan item it undoes.</summary>
    public async Task<ChangePlanResult> UndoAsync(ConfirmedChangePlan confirmed, CompliancePlan plan, IProgress<ChangePlanItemResult> progress, CancellationToken ct)
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
            ?? undo.FirstOrDefault(u => CompliancePlan.SameTarget(u.Undo, change)).Entry);
        var stop = SiteOperationOutcome.Failed;
        try
        {
            var outcomes = await Task.Run(() => service.ApplyAsync(undo.Select(u => u.Undo).ToList(), reporter, ct), ct).ConfigureAwait(true);
            foreach (var outcome in outcomes) reporter.Final(outcome);
            var rows = undo.Select(u => results.TryGetValue(u.Entry.Item, out var r) ? r : new ChangePlanItemResult(u.Entry.Item, false, NoResultText)).ToList();
            var cancelled = ct.IsCancellationRequested && LeftUndone(undo.Select(u => u.Entry.Item), rows);
            var result = new ChangePlanResult(rows, cancelled ? ChangePlanStop.Cancelled : ChangePlanStop.Completed);
            stop = Outcome(result, cancelled);
            return result;
        }
        catch (OperationCanceledException)
        {
            reporter.KeepSteps();
            stop = SiteOperationOutcome.Cancelled;
            throw;
        }
        catch
        {
            reporter.KeepSteps();
            throw;
        }
        finally
        {
            Record("Undo " + plan.Action, stop, results.Values);
        }
    }

    /// <summary>True when the undo of the change creates its object again.</summary>
    public static bool Recreates(ComplianceChange? change) =>
        change?.Kind is ComplianceChangeKind.DeleteDeployment or ComplianceChangeKind.RemoveSetting or ComplianceChangeKind.Redeploy;

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

    /// <summary>
    /// Turns each service outcome into the result of its plan item once. A catalog deploy reports
    /// its steps while the builder runs: those reports show as steps, and the outcome the service
    /// returns is the result. When the run ends without returning, the last step reported is kept
    /// as the result, with its undo.
    /// </summary>
    private sealed class OutcomeReporter(
        CompliancePlan plan,
        Dictionary<ChangePlanItem, ChangePlanItemResult> results,
        IProgress<ChangePlanItemResult> progress,
        Func<ComplianceChange, CompliancePlanEntry?>? undoEntry) : IProgress<ComplianceOutcome>
    {
        private readonly HashSet<ComplianceOutcome> _seen = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ChangePlanItem, ComplianceOutcome> _steps = new(ReferenceEqualityComparer.Instance);

        private CompliancePlanEntry? EntryOf(ComplianceChange change) => undoEntry is null ? plan.EntryFor(change) : undoEntry(change);

        public void Report(ComplianceOutcome value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Change.Kind != ComplianceChangeKind.CatalogDeploy)
            {
                Final(value);
                return;
            }
            ChangePlanItemResult step;
            lock (results)
            {
                if (EntryOf(value.Change) is not { } entry || results.ContainsKey(entry.Item)) return;
                _steps[entry.Item] = value;
                var text = value.Steps.Count > 0 ? value.Steps[^1] : value.Message;
                step = new ChangePlanItemResult(entry.Item, value.Succeeded, SecretMask.Apply(text)) { Interim = true };
            }
            progress.Report(step);
        }

        public void KeepSteps()
        {
            List<ComplianceOutcome> last;
            lock (results) last = _steps.Where(s => !results.ContainsKey(s.Key)).Select(s => s.Value).ToList();
            foreach (var outcome in last) Final(outcome with { Succeeded = false, Message = outcome.Steps.Count > 0 ? outcome.Steps[^1] : outcome.Message });
        }

        public void Final(ComplianceOutcome value)
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
                if (undoEntry is not null && value.Succeeded && Recreates(entry.Change)) message = $"{message} {RecreatedNote}".Trim();
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
