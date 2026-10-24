using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.Services;

/// <summary>One line of a DP content plan: the pair it changes and its plan item.</summary>
public sealed record DpContentPlanEntry(ChangePlanItem Item, PairRow Pair)
{
    public string Key { get; } = DpContentLabels.Key(Pair);
}

/// <summary>
/// The prepared change behind a DP content change plan. The change travels only with the plan the
/// operator confirmed, and <see cref="DpContentWriter"/> sends it only for that plan. The outcomes of
/// the last run stay here for the Undo.
/// </summary>
public sealed class DpContentPlan
{
    private readonly List<DpContentOutcome> _outcomes = [];

    public DpContentPlan(string action, DpContentChange change, ChangePlan plan, IReadOnlyList<DpContentPlanEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(plan);
        if (entries.Count == 0) throw new ArgumentException("A DP content plan changes at least one pair.", nameof(entries));
        if (entries.Count != plan.Items.Count || entries.Where((e, i) => !ReferenceEquals(e.Item, plan.Items[i])).Any())
            throw new ArgumentException("The entries are the items of the plan.", nameof(entries));
        Action = action;
        Change = change;
        Plan = plan;
        Entries = entries;
    }

    /// <summary>The short action name for the log and the site history, for example "Remove from DP".</summary>
    public string Action { get; }

    public DpContentChange Change { get; }

    public ChangePlan Plan { get; }

    public IReadOnlyList<DpContentPlanEntry> Entries { get; }

    public DpContentChangeKind Kind => Change.Kind;

    public IReadOnlyList<DpContentOutcome> Outcomes
    {
        get { lock (_outcomes) return _outcomes.ToArray(); }
    }

    internal void ClearOutcomes()
    {
        lock (_outcomes) _outcomes.Clear();
    }

    internal void AddOutcome(DpContentOutcome outcome)
    {
        lock (_outcomes) _outcomes.Add(outcome);
    }

    /// <summary>
    /// The Distribute again of the pairs the service named in the Undo of each outcome, as one
    /// change; null when no outcome carries an Undo.
    /// </summary>
    public DpContentChange? UndoChange()
    {
        var pairs = new List<PairRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var undo in Outcomes.Select(o => o.UndoChange).OfType<DpContentChange>())
            foreach (var pair in undo.Pairs)
                if (seen.Add(DpContentLabels.Key(pair))) pairs.Add(pair);
        return pairs.Count == 0
            ? null
            : new DpContentChange(Kind: DpContentChangeKind.DistributeAgain, Pairs: pairs, RemovalConfirmed: false, LastCopyAcknowledged: false, Impact: [], Checks: [], Fingerprint: null);
    }

    /// <summary>True when the confirmed plan is this plan: the same items, in the same order, with the same confirmations.</summary>
    public bool IsConfirmedPlan(ConfirmedChangePlan confirmed)
    {
        var items = confirmed.Plan.Items;
        if (items.Count != Entries.Count) return false;
        for (var i = 0; i < items.Count; i++)
            if (!ReferenceEquals(items[i], Entries[i].Item)) return false;
        return confirmed.Plan.ExtraConfirmation == Plan.ExtraConfirmation && confirmed.Plan.SecondConfirmation == Plan.SecondConfirmation
            && confirmed.Plan.IsDestructive == Plan.IsDestructive;
    }
}

/// <summary>
/// The only caller of <see cref="IDpContentService.ApplyAsync"/> in the desktop. It takes a
/// <see cref="ConfirmedChangePlan"/>, which only the change plan window issues, and sends the
/// prepared change of that plan and nothing else. A removal reaches the service with
/// RemovalConfirmed only when the confirmed plan asked its second check box, and with
/// LastCopyAcknowledged only when it asked the last-copy check box; the flags the change already
/// had never travel.
/// </summary>
public sealed class DpContentWriter(IDpContentService service, ISiteOperationHistory history, IAppLog log, Func<string> siteTarget)
{
    public const string ToolName = "DP Content Manager";
    public const string LogArea = "DpContent";
    public const string SkippedPrefix = "Skipped";
    public const string NoResultText = "No result: the service did not report this pair.";
    public const string Requested = "Requested";
    public const string Confirmed = "Confirmed";
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

    public ChangePlanRun RunFor(DpContentPlan plan) => (confirmed, progress, ct) => Track(ApplyAsync(confirmed, plan, progress, ct));

    /// <summary>The change to send: the removal flags only as the confirmed plan asked them, and none for a change that removes nothing.</summary>
    public static DpContentChange ConfirmedChange(DpContentChange change, ConfirmedChangePlan confirmed)
    {
        var removal = change.Kind == DpContentChangeKind.Remove;
        return change with
        {
            RemovalConfirmed = removal && confirmed.Plan.IsDestructive && confirmed.Plan.ExtraConfirmation is not null,
            LastCopyAcknowledged = removal && confirmed.Plan.SecondConfirmation is not null
        };
    }

    public async Task<ChangePlanResult> ApplyAsync(ConfirmedChangePlan confirmed, DpContentPlan plan, IProgress<ChangePlanItemResult> progress, CancellationToken ct)
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
            IReadOnlyList<DpContentOutcome> outcomes = await Task.Run(() => service.ApplyAsync([change], reporter, ct), ct).ConfigureAwait(true);
            foreach (var outcome in outcomes) reporter.Report(outcome);
            var rows = Complete(plan, results, progress);
            var cancelled = ct.IsCancellationRequested && rows.Any(r => r.Skipped || r.Message == NoResultText);
            var result = new ChangePlanResult(rows, cancelled ? ChangePlanStop.Cancelled : ChangePlanStop.Completed)
            {
                UndoAvailable = false,
                Headline = Headline(rows),
                Note = plan.UndoChange() is { } undo ? $"Undo last change can distribute again {DpContentLabels.Count(undo.Pairs.Count, "pair")} that {(undo.Pairs.Count == 1 ? "was" : "were")} Installed." : null
            };
            var failed = rows.Count(r => !r.Succeeded && !r.Skipped);
            var changed = rows.Count(r => r.Succeeded && !r.Skipped);
            stop = cancelled ? SiteOperationOutcome.Cancelled
                : failed == 0 && rows.All(r => !r.Skipped) ? SiteOperationOutcome.Succeeded
                : changed > 0 ? SiteOperationOutcome.PartlySucceeded
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
            Record(plan.Action, stop, results);
        }
    }

    /// <summary>The first sentence of a result: the number of pairs of each result.</summary>
    public static string Headline(IReadOnlyList<ChangePlanItemResult> rows)
    {
        var parts = new List<string>();
        foreach (var label in new[] { Requested, Confirmed, Unchanged, Failed, Skipped })
        {
            var count = rows.Count(r => string.Equals(r.OutcomeText, label, StringComparison.Ordinal));
            if (count > 0) parts.Add($"{DpContentLabels.Number(count)} {label.ToLowerInvariant()}");
        }
        return parts.Count == 0 ? "No pair reported a result." : string.Join(", ", parts) + ".";
    }

    /// <summary>The row of a pair outcome: Requested, Confirmed and Unchanged count as done, Skipped as not attempted, anything else as failed.</summary>
    public static ChangePlanItemResult Row(ChangePlanItem item, string result, string message)
    {
        var text = SecretMask.Apply(message);
        return result switch
        {
            Requested or Confirmed or Unchanged => new ChangePlanItemResult(item, true, text) { ResultLabel = result },
            Skipped => ChangePlanItemResult.Skip(item, text.StartsWith(SkippedPrefix, StringComparison.OrdinalIgnoreCase) ? text : $"{SkippedPrefix}: {text}") with { ResultLabel = Skipped },
            _ => new ChangePlanItemResult(item, false, text) { ResultLabel = Failed }
        };
    }

    /// <summary>Every line once the service returned: a pair without its own outcome takes the message of the whole change, or no result.</summary>
    private static List<ChangePlanItemResult> Complete(DpContentPlan plan, Dictionary<ChangePlanItem, ChangePlanItemResult> results, IProgress<ChangePlanItemResult> progress)
    {
        var whole = plan.Outcomes.LastOrDefault(o => o.Pairs.Count == 0);
        var rows = new List<ChangePlanItemResult>(plan.Entries.Count);
        foreach (var entry in plan.Entries)
        {
            ChangePlanItemResult? row;
            lock (results) row = results.TryGetValue(entry.Item, out var done) ? done : null;
            if (row is null)
            {
                row = whole is null ? new ChangePlanItemResult(entry.Item, false, NoResultText) { ResultLabel = Failed }
                    : Row(entry.Item, whole.Message.StartsWith(SkippedPrefix, StringComparison.OrdinalIgnoreCase) ? Skipped : whole.Succeeded ? Requested : Failed, whole.Message);
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
            ["changed"] = Count(Requested) + Count(Confirmed),
            ["failed"] = rows.Count(r => !r.Succeeded && !r.Skipped),
            ["skipped"] = rows.Count(r => r.Skipped),
            ["requested"] = Count(Requested),
            ["confirmed"] = Count(Confirmed),
            ["unchanged"] = Count(Unchanged)
        };
        log.Info(LogArea, $"{action}: outcome={outcome} " + string.Join(" ", counts.Select(c => $"{c.Key}={c.Value}")) + ".");
        string target;
        try { target = siteTarget(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { target = ""; }
        history.Record(new SiteOperationEntry(ToolName, action, target, outcome, counts));
    }

    /// <summary>Turns the pair outcomes of each reported outcome into the results of their plan lines, each once.</summary>
    private sealed class OutcomeReporter(DpContentPlan plan, Dictionary<ChangePlanItem, ChangePlanItemResult> results, IProgress<ChangePlanItemResult> progress)
        : IProgress<DpContentOutcome>
    {
        private readonly HashSet<DpContentOutcome> _seen = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, DpContentPlanEntry> _byKey = plan.Entries.GroupBy(e => e.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        public void Report(DpContentOutcome value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var rows = new List<ChangePlanItemResult>();
            lock (results)
            {
                if (!_seen.Add(value)) return;
                plan.AddOutcome(value);
                foreach (var pair in value.Pairs)
                {
                    if (!_byKey.TryGetValue(DpContentLabels.Key(pair.Pair), out var entry) || results.ContainsKey(entry.Item)) continue;
                    var row = Row(entry.Item, pair.Result, pair.Message);
                    results[entry.Item] = row;
                    rows.Add(row);
                }
            }
            foreach (var row in rows) progress.Report(row);
        }
    }
}
