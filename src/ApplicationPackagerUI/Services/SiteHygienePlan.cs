using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.Services;

/// <summary>One line of a Site Hygiene plan: the finding it fixes and its plan item.</summary>
public sealed record SiteHygienePlanEntry(ChangePlanItem Item, HygieneFinding Finding)
{
    public string Key { get; } = SiteHygieneLabels.Key(Finding);
}

/// <summary>
/// The prepared change behind a Site Hygiene change plan. The change travels only with the plan the
/// operator confirmed, and <see cref="SiteHygieneWriter"/> sends it only for that plan.
/// </summary>
public sealed class SiteHygienePlan
{
    private readonly List<HygieneOutcome> _outcomes = [];

    public SiteHygienePlan(string action, HygieneChange change, ChangePlan plan, IReadOnlyList<SiteHygienePlanEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(plan);
        if (entries.Count == 0) throw new ArgumentException("A Site Hygiene plan fixes at least one finding.", nameof(entries));
        if (entries.Count != plan.Items.Count || entries.Where((e, i) => !ReferenceEquals(e.Item, plan.Items[i])).Any())
            throw new ArgumentException("The entries are the items of the plan.", nameof(entries));
        Action = action;
        Change = change;
        Plan = plan;
        Entries = entries;
    }

    /// <summary>The fixed action name for the log and the site history, for example "Delete application".</summary>
    public string Action { get; }

    public HygieneChange Change { get; }

    public ChangePlan Plan { get; }

    public IReadOnlyList<SiteHygienePlanEntry> Entries { get; }

    public HygieneFixKind Kind => Change.Kind;

    /// <summary>The final outcomes of the last run of this plan.</summary>
    public IReadOnlyList<HygieneOutcome> Outcomes
    {
        get { lock (_outcomes) return _outcomes.ToArray(); }
    }

    internal void ClearOutcomes()
    {
        lock (_outcomes) _outcomes.Clear();
    }

    internal void AddOutcome(HygieneOutcome outcome)
    {
        lock (_outcomes) _outcomes.Add(outcome);
    }

    /// <summary>The keys of the findings whose final result is Done: the fix was made and read back.</summary>
    public IReadOnlySet<string> DoneKeys() =>
        Outcomes.SelectMany(o => o.Objects).Where(o => o.Result == SiteHygieneWriter.Done).Select(o => SiteHygieneLabels.Key(o.Finding)).ToHashSet(StringComparer.Ordinal);

    /// <summary>True when the confirmed plan is this plan: the same items, in the same order, with the same confirmations.</summary>
    public bool IsConfirmedPlan(ConfirmedChangePlan confirmed)
    {
        var items = confirmed.Plan.Items;
        if (items.Count != Entries.Count) return false;
        for (var i = 0; i < items.Count; i++)
            if (!ReferenceEquals(items[i], Entries[i].Item)) return false;
        return confirmed.Plan.ExtraConfirmation == Plan.ExtraConfirmation && confirmed.Plan.IsDestructive == Plan.IsDestructive;
    }
}

/// <summary>
/// The only caller of <see cref="ISiteHygieneService.ApplyAsync"/> in the desktop. It takes a
/// <see cref="ConfirmedChangePlan"/>, which only the change plan window issues, and sends the
/// prepared change of that plan and nothing else. A delete reaches the service with DeleteConfirmed
/// only when the confirmed plan asked its second check box; the flag the change already had never travels.
/// </summary>
public sealed class SiteHygieneWriter(ISiteHygieneService service, ISiteOperationHistory history, IAppLog log, Func<string> siteTarget)
{
    public const string ToolName = "Site Hygiene";
    public const string LogArea = "SiteHygiene";
    public const string SkippedPrefix = "Skipped";
    public const string NoResultText = "No result: the service did not report this object.";
    public const string Done = "Done";
    public const string Unchanged = "Unchanged";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";

    private readonly object _runLock = new();
    private Task _running = Task.CompletedTask;

    /// <summary>The run that a closed plan window left working; a completed task when none works.</summary>
    public Task Running
    {
        get { lock (_runLock) return _running; }
    }

    private Task<ChangePlanResult> Track(Task<ChangePlanResult> run)
    {
        lock (_runLock) _running = run;
        return run;
    }

    public ChangePlanRun RunFor(SiteHygienePlan plan) => (confirmed, progress, ct) => Track(ApplyAsync(confirmed, plan, progress, ct));

    /// <summary>The change to send: DeleteConfirmed only for a delete kind whose confirmed plan asked the second box.</summary>
    public static HygieneChange ConfirmedChange(HygieneChange change, ConfirmedChangePlan confirmed) => change with
    {
        DeleteConfirmed = SiteHygieneLabels.IsDelete(change.Kind) && confirmed.Plan.IsDestructive && confirmed.Plan.ExtraConfirmation is not null
    };

    public async Task<ChangePlanResult> ApplyAsync(ConfirmedChangePlan confirmed, SiteHygienePlan plan, IProgress<ChangePlanItemResult> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsConfirmedPlan(confirmed)) throw new InvalidOperationException("The confirmed plan is not the plan of this change.");
        plan.ClearOutcomes();
        var results = new Dictionary<ChangePlanItem, ChangePlanItemResult>(ReferenceEqualityComparer.Instance);
        var reporter = new OutcomeReporter(plan, results, progress);
        var change = ConfirmedChange(plan.Change, confirmed);
        var stop = SiteOperationOutcome.Failed;
        try
        {
            var outcome = await Task.Run(() => service.ApplyAsync(change, reporter, ct), ct).ConfigureAwait(true);
            reporter.Report(outcome.Final ? outcome : outcome with { Final = true });
            var rows = Complete(plan, results, progress);
            var cancelled = ct.IsCancellationRequested && rows.Any(r => r.Skipped || r.Message == NoResultText);
            var failed = rows.Count(r => !r.Succeeded && !r.Skipped);
            var changed = rows.Count(r => r.Succeeded && !r.Skipped);
            stop = cancelled ? SiteOperationOutcome.Cancelled
                : failed == 0 && rows.All(r => !r.Skipped) ? SiteOperationOutcome.Succeeded
                : changed > 0 ? SiteOperationOutcome.PartlySucceeded
                : SiteOperationOutcome.Failed;
            return new ChangePlanResult(rows, cancelled ? ChangePlanStop.Cancelled : ChangePlanStop.Completed)
            {
                UndoAvailable = false,
                Headline = Headline(rows)
            };
        }
        catch (OperationCanceledException)
        {
            stop = SiteOperationOutcome.Cancelled;
            throw;
        }
        finally
        {
            Record(plan.Action, stop, results);
        }
    }

    /// <summary>The first sentence of a result: the number of objects of each result.</summary>
    public static string Headline(IReadOnlyList<ChangePlanItemResult> rows)
    {
        var parts = new List<string>();
        foreach (var label in new[] { Done, Unchanged, Failed, Skipped })
        {
            var count = rows.Count(r => string.Equals(r.OutcomeText, label, StringComparison.Ordinal));
            if (count > 0) parts.Add($"{SiteHygieneLabels.Number(count)} {label.ToLowerInvariant()}");
        }
        return parts.Count == 0 ? "No object reported a result." : string.Join(", ", parts) + ".";
    }

    /// <summary>The row of an object outcome: Done and Unchanged count as done, Skipped as not attempted, anything else as failed.</summary>
    public static ChangePlanItemResult Row(ChangePlanItem item, string result, string message)
    {
        var text = SecretMask.Apply(message);
        return result switch
        {
            Done or Unchanged => new ChangePlanItemResult(item, true, text) { ResultLabel = result },
            Skipped => ChangePlanItemResult.Skip(item, text.StartsWith(SkippedPrefix, StringComparison.OrdinalIgnoreCase) ? text : $"{SkippedPrefix}: {text}") with { ResultLabel = Skipped },
            _ => new ChangePlanItemResult(item, false, text) { ResultLabel = Failed }
        };
    }

    /// <summary>Every line once the service returned: an object without its own outcome takes the message of the whole change, or no result.</summary>
    private static List<ChangePlanItemResult> Complete(SiteHygienePlan plan, Dictionary<ChangePlanItem, ChangePlanItemResult> results, IProgress<ChangePlanItemResult> progress)
    {
        var whole = plan.Outcomes.LastOrDefault();
        var rows = new List<ChangePlanItemResult>(plan.Entries.Count);
        foreach (var entry in plan.Entries)
        {
            ChangePlanItemResult? row;
            lock (results) row = results.TryGetValue(entry.Item, out var done) ? done : null;
            if (row is null)
            {
                row = whole is null || whole.Message.Length == 0
                    ? new ChangePlanItemResult(entry.Item, false, NoResultText) { ResultLabel = Failed }
                    : Row(entry.Item, whole.Message.StartsWith(SkippedPrefix, StringComparison.OrdinalIgnoreCase) ? Skipped : Failed, whole.Message);
                lock (results) results[entry.Item] = row;
                progress.Report(row);
            }
            rows.Add(row);
        }
        return rows;
    }

    private void Record(string action, SiteOperationOutcome outcome, Dictionary<ChangePlanItem, ChangePlanItemResult> results)
    {
        List<ChangePlanItemResult> rows;
        lock (results) rows = results.Values.ToList();
        int Count(string label) => rows.Count(r => r.OutcomeText == label);
        var counts = new Dictionary<string, int>
        {
            ["changed"] = Count(Done),
            ["failed"] = rows.Count(r => !r.Succeeded && !r.Skipped),
            ["skipped"] = rows.Count(r => r.Skipped),
            ["unchanged"] = Count(Unchanged)
        };
        log.Info(LogArea, $"{action}: outcome={outcome} " + string.Join(" ", counts.Select(c => $"{c.Key}={c.Value}")) + ".");
        string target;
        try { target = siteTarget(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { target = ""; }
        history.Record(new SiteOperationEntry(ToolName, action, target, outcome, counts));
    }

    /// <summary>
    /// Turns the object outcomes of each final outcome into the results of their plan lines. A progress
    /// outcome (Final false) is shown as a step only and never becomes a result or a history count.
    /// </summary>
    private sealed class OutcomeReporter(SiteHygienePlan plan, Dictionary<ChangePlanItem, ChangePlanItemResult> results, IProgress<ChangePlanItemResult> progress)
        : IProgress<HygieneOutcome>
    {
        private readonly HashSet<HygieneOutcome> _seen = [];
        private readonly Dictionary<string, SiteHygienePlanEntry> _byKey = plan.Entries.GroupBy(e => e.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        public void Report(HygieneOutcome value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var rows = new List<ChangePlanItemResult>();
            lock (results)
            {
                if (!_seen.Add(value)) return;
                if (!value.Final)
                {
                    if (plan.Entries.Count > 0) rows.Add(new ChangePlanItemResult(plan.Entries[0].Item, true, SecretMask.Apply(value.Message)) { Interim = true });
                }
                else
                {
                    plan.AddOutcome(value);
                    foreach (var o in value.Objects)
                    {
                        if (!_byKey.TryGetValue(SiteHygieneLabels.Key(o.Finding), out var entry)) continue;
                        var row = Row(entry.Item, o.Result, o.Message);
                        // The service reports its final outcome through progress and also returns it; a repeated result is not a second line.
                        if (results.TryGetValue(entry.Item, out var known) && known == row) continue;
                        results[entry.Item] = row;
                        rows.Add(row);
                    }
                }
            }
            foreach (var row in rows) progress.Report(row);
        }
    }
}
