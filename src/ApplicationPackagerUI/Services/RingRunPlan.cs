using System.Globalization;
using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.Services;

/// <summary>What one line of a ring plan does.</summary>
public enum RingEntryKind
{
    /// <summary>Creates the deployment of a ring now.</summary>
    Create,

    /// <summary>Writes the ring as Held; Promote creates it later.</summary>
    Hold,

    /// <summary>Creates the deployment of the next held ring.</summary>
    Promote,

    /// <summary>Removes a deployment the run created.</summary>
    Remove,

    /// <summary>Keeps a deployment a check refused to remove; nothing is sent for it.</summary>
    Keep,

    /// <summary>Marks a held ring Cancelled in the run file.</summary>
    Cancel,

    /// <summary>Moves the run file to the closed folder.</summary>
    Close,

    /// <summary>Returns a ring in Creating to Held in the run file; nothing changes in ConfigMgr.</summary>
    Abandon
}

/// <summary>
/// One line of a ring plan. <paramref name="Key"/> finds the service outcome of the line: the
/// collection ID of a ring for Create, Hold and Promote, the deployment ID for Remove and Keep.
/// </summary>
public sealed record RingPlanEntry(ChangePlanItem Item, RingEntryKind Kind, string Key, string? Reason = null)
{
    /// <summary>True for a line whose change the service makes.</summary>
    public bool Writes => Kind is RingEntryKind.Create or RingEntryKind.Promote or RingEntryKind.Remove or RingEntryKind.Cancel or RingEntryKind.Close or RingEntryKind.Abandon;
}

/// <summary>
/// The ring change behind a change plan. The change travels only with the plan the operator
/// confirmed, and <see cref="RingWriter"/> sends it only for that plan.
/// </summary>
public sealed class RingRunPlan
{
    private readonly List<RingOutcome> _outcomes = [];

    public RingRunPlan(string action, string title, string actionName, RingChange change, IEnumerable<RingPlanEntry> entries, bool isDestructive = false,
        string? extraConfirmation = null, string? optionalChoice = null, string? removalQuestion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(change);
        Action = action;
        Change = change;
        Entries = entries.ToArray();
        if (!Entries.Any(e => e.Writes)) throw new ArgumentException("A ring plan makes at least one change.", nameof(entries));
        RemovalQuestion = removalQuestion;
        Plan = new ChangePlan(title, actionName, Entries.Select(e => e.Item), isDestructive, hasUndo: false)
        {
            ExtraConfirmation = extraConfirmation, OptionalChoice = optionalChoice, LogName = action
        };
    }

    /// <summary>The short action name for the log and the site history, for example "Create ring run".</summary>
    public string Action { get; }

    public ChangePlan Plan { get; }

    public RingChange Change { get; }

    public IReadOnlyList<RingPlanEntry> Entries { get; }

    /// <summary>The question the writer asks before a removal starts; null for a plan that removes nothing.</summary>
    public string? RemovalQuestion { get; }

    /// <summary>True when a ring the plan creates is Required: the second check box acknowledges it.</summary>
    public bool NeedsRequiredConfirmation { get; init; }

    /// <summary>True when the second check box also names an available time in the past.</summary>
    public bool AcknowledgesBackdated { get; init; }

    /// <summary>True when the change runs only after the operator checked the plan's own check box, as for Abandon ring.</summary>
    public bool RequiresExtraConfirmation { get; init; }

    public IReadOnlyList<RingOutcome> Outcomes
    {
        get { lock (_outcomes) return _outcomes.ToArray(); }
    }

    /// <summary>The newest outcome the service reported for the change, or null.</summary>
    public RingOutcome? LastOutcome
    {
        get { lock (_outcomes) return _outcomes.Count == 0 ? null : _outcomes[^1]; }
    }

    internal void ClearOutcomes()
    {
        lock (_outcomes) _outcomes.Clear();
    }

    internal void AddOutcome(RingOutcome outcome)
    {
        lock (_outcomes) _outcomes.Add(outcome);
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
/// The only caller of <see cref="IRingService.ApplyAsync"/> in the desktop. It takes a
/// <see cref="ConfirmedChangePlan"/>, which only the change plan window issues, and sends the change
/// of that plan and nothing else. A run with a Required ring reaches the service with its
/// acknowledgement only when the confirmed plan asked the second check box; the flags that the part
/// 1 changes inside it carry never travel, so the ring service alone decides them per ring.
/// </summary>
public sealed class RingWriter(IRingService service, ISiteOperationHistory history, IAppLog log, Func<string> siteTarget, Func<string, Task<bool>> confirmRemoval)
{
    public const string HeldText = "Held: Promote creates this ring later.";
    public const string StoppedText = "Skipped: the run stopped before this ring. Nothing was created for it.";
    public const string CancelledText = "Cancelled in the run file. No deployment was changed.";
    public const string ClosedText = "The run was closed. Its file moved to the closed folder.";
    public const string NotAcknowledgedText =
        "Skipped: a Required ring installs software on every device of its collection, and the plan did not ask the second confirmation for it. Nothing was changed. Show the plan again.";
    public const string UnconfirmedText = "Skipped: the plan did not ask its own confirmation. Nothing was changed. Show the plan again.";
    public const string NothingCreatedText = "Nothing was created.";

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

    public ChangePlanRun RunFor(RingRunPlan plan) => (confirmed, progress, ct) => Track(ApplyAsync(confirmed, plan, progress, ct));

    /// <summary>
    /// The change to send: RequiredConfirmed and BackdatedAcknowledged only as the confirmed plan
    /// asked them, CreateNowContentDistributing only when the operator chose it; every part 1 change
    /// inside it without its own flags.
    /// </summary>
    public static RingChange Confirmed(RingChange change, bool required, bool backdated, bool createNow) => change with
    {
        RequiredConfirmed = required,
        BackdatedAcknowledged = backdated,
        CreateNowContentDistributing = createNow,
        RingChanges = change.RingChanges
            .Select(c => c.RequiredConfirmed || c.BackdatedAcknowledged || c.CreateNowContentDistributing
                ? c with { RequiredConfirmed = false, BackdatedAcknowledged = false, CreateNowContentDistributing = false }
                : c)
            .ToList()
    };

    public async Task<ChangePlanResult> ApplyAsync(ConfirmedChangePlan confirmed, RingRunPlan plan, IProgress<ChangePlanItemResult> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsConfirmedPlan(confirmed)) throw new InvalidOperationException("The confirmed plan is not the plan of this change.");
        var results = new Dictionary<ChangePlanItem, ChangePlanItemResult>(ReferenceEqualityComparer.Instance);
        void Report(ChangePlanItemResult result)
        {
            lock (results) results[result.Item] = result;
            progress.Report(result);
        }

        if (plan.RemovalQuestion is { } question && !await confirmRemoval(question))
        {
            log.Info(DeploymentWriter.LogArea, $"{plan.Action}: the removal was not confirmed.");
            return new ChangePlanResult(plan.Entries.Select(e => ChangePlanItemResult.Skip(e.Item, e.Writes ? DeploymentWriter.RemovalNotConfirmed : e.Reason ?? DeploymentWriter.RemovalNotConfirmed)).ToList())
            {
                Declined = true
            };
        }

        var acknowledged = confirmed.Plan.ExtraConfirmation is not null;
        plan.ClearOutcomes();
        foreach (var entry in plan.Entries.Where(e => e.Kind == RingEntryKind.Keep)) Report(ChangePlanItemResult.Skip(entry.Item, entry.Reason ?? DeploymentWriter.SkippedPrefix + "."));
        if ((plan.NeedsRequiredConfirmation || plan.RequiresExtraConfirmation) && !acknowledged)
        {
            var refusal = plan.NeedsRequiredConfirmation ? NotAcknowledgedText : UnconfirmedText;
            foreach (var entry in plan.Entries.Where(e => e.Kind != RingEntryKind.Keep)) Report(ChangePlanItemResult.Skip(entry.Item, refusal));
            Record(plan.Action, SiteOperationOutcome.Failed, results);
            return new ChangePlanResult(plan.Entries.Select(e => results[e.Item]).ToList());
        }

        var change = Confirmed(plan.Change, acknowledged && plan.NeedsRequiredConfirmation, acknowledged && plan.AcknowledgesBackdated,
            confirmed.OptionChosen && plan.Plan.OptionalChoice is not null);
        var reporter = new OutcomeReporter(plan, results, progress);
        var stop = SiteOperationOutcome.Failed;
        var kept = new HashSet<ChangePlanItem>(ReferenceEqualityComparer.Instance);
        try
        {
            IReadOnlyList<RingOutcome> outcomes = await Task.Run(() => service.ApplyAsync([change], reporter, ct), ct).ConfigureAwait(true);
            foreach (var outcome in outcomes) reporter.Report(outcome);
            if (plan.Change.Kind == RingChangeKind.UndoRun)
                foreach (var entry in plan.Entries.Where(e => e.Kind is RingEntryKind.Keep or RingEntryKind.Remove && Get(results, e.Item) is { Skipped: true }))
                    kept.Add(entry.Item);
            var rows = Complete(plan, results, progress);
            var cancelled = ct.IsCancellationRequested && rows.Any(r => r.Skipped || r.Message == DeploymentWriter.NoResultText);
            var result = new ChangePlanResult(rows, cancelled ? ChangePlanStop.Cancelled : ChangePlanStop.Completed)
            {
                UndoAvailable = false, Headline = plan.Change.Kind == RingChangeKind.UndoRun && !cancelled ? UndoHeadline(plan, rows, kept) : null
            };
            // A run that stopped at a ring reports the later rings as skipped, not failed, so the service's own result decides too.
            // An Undo run that keeps a deployment does not succeed as a whole either; its kept deployments count as kept, not failed.
            var stopped = plan.LastOutcome is { Succeeded: false } && (plan.Change.Kind != RingChangeKind.UndoRun || kept.Count == 0);
            stop = cancelled ? SiteOperationOutcome.Cancelled
                : result.FailedCount == 0 && !stopped && kept.Count == 0 ? SiteOperationOutcome.Succeeded
                : result.SucceededCount > 0 || result.PartialCount > 0 ? SiteOperationOutcome.PartlySucceeded
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
            Record(plan.Action, stop, results, kept);
        }
    }

    /// <summary>The first sentence of an Undo run result: the deployments removed and kept, the held rings cancelled, and the reason of each kept deployment.</summary>
    private static string UndoHeadline(RingRunPlan plan, IReadOnlyList<ChangePlanItemResult> rows, HashSet<ChangePlanItem> kept)
    {
        int Count(RingEntryKind kind, Func<ChangePlanItemResult, bool> test) => plan.Entries.Zip(rows).Count(p => p.First.Kind == kind && test(p.Second));
        var removed = Count(RingEntryKind.Remove, r => r.Succeeded && !r.Skipped);
        var cancelledRings = Count(RingEntryKind.Cancel, r => r.Succeeded && !r.Skipped);
        var failed = rows.Count(r => !r.Succeeded && !r.Skipped);
        var skipped = rows.Count(r => r.Skipped && !kept.Contains(r.Item));
        var parts = new List<string> { $"{removed.ToString(CultureInfo.CurrentCulture)} removed", $"{kept.Count.ToString(CultureInfo.CurrentCulture)} kept" };
        if (cancelledRings > 0) parts.Add($"{DeploymentText.Count(cancelledRings, "held ring")} cancelled");
        if (failed > 0) parts.Add($"{failed.ToString(CultureInfo.CurrentCulture)} failed");
        if (skipped > 0) parts.Add($"{skipped.ToString(CultureInfo.CurrentCulture)} skipped");
        var text = string.Join(", ", parts) + ".";
        var reasons = rows.Where(r => kept.Contains(r.Item)).Select(r => $"{r.Item.Target}: {Reason(r.Message)}").ToList();
        if (reasons.Count == 0) return text;
        text += " Kept: " + string.Join(" ", reasons.Take(MaxKeptReasons));
        return reasons.Count > MaxKeptReasons ? $"{text} {DeploymentText.Count(reasons.Count - MaxKeptReasons, "more kept deployment")}: the rows name each." : text;
    }

    private const int MaxKeptReasons = 5;

    private static string Reason(string message)
    {
        var text = message.StartsWith(DeploymentService.SkippedPrefix, StringComparison.OrdinalIgnoreCase) ? message[DeploymentService.SkippedPrefix.Length..] : message;
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>
    /// The result of every line once the service returned: a line without its own outcome follows
    /// the outcome of the whole change. A held ring stays held after a run that succeeded; a ring
    /// after the first failed ring was not attempted; when no ring reported, the change's message
    /// applies to every line that writes.
    /// </summary>
    private static List<ChangePlanItemResult> Complete(RingRunPlan plan, Dictionary<ChangePlanItem, ChangePlanItemResult> results, IProgress<ChangePlanItemResult> progress)
    {
        var last = plan.LastOutcome;
        var anyRing = plan.Entries.Any(e => e.Kind is RingEntryKind.Create or RingEntryKind.Promote or RingEntryKind.Remove && Has(results, e.Item));
        var rows = new List<ChangePlanItemResult>(plan.Entries.Count);
        foreach (var entry in plan.Entries)
        {
            if (Get(results, entry.Item) is { } done)
            {
                rows.Add(done);
                continue;
            }
            var message = last is null ? DeploymentWriter.NoResultText : SecretMask.Apply(last.Message);
            var skippedRun = last is { Succeeded: false } && last.Message.StartsWith(DeploymentWriter.SkippedPrefix, StringComparison.OrdinalIgnoreCase);
            ChangePlanItemResult row = entry.Kind switch
            {
                RingEntryKind.Hold => ChangePlanItemResult.Skip(entry.Item, last is { Succeeded: true } ? HeldText : anyRing ? StoppedText : message),
                RingEntryKind.Cancel when last is { Succeeded: true } || CancelledIn(last, entry.Key) => new ChangePlanItemResult(entry.Item, true, CancelledText),
                RingEntryKind.Close when last is { Succeeded: true } => new ChangePlanItemResult(entry.Item, true, ClosedText),
                _ when last is null => new ChangePlanItemResult(entry.Item, false, DeploymentWriter.NoResultText),
                RingEntryKind.Create when anyRing && !last.Succeeded => ChangePlanItemResult.Skip(entry.Item, StoppedText),
                _ when last.Succeeded => new ChangePlanItemResult(entry.Item, true, message),
                _ when skippedRun => ChangePlanItemResult.Skip(entry.Item, message),
                _ => new ChangePlanItemResult(entry.Item, false, message)
            };
            lock (results) results[entry.Item] = row;
            progress.Report(row);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>True when the run file the service returned marks the held ring of <paramref name="collectionId"/> Cancelled, also after an outcome that did not succeed as a whole.</summary>
    private static bool CancelledIn(RingOutcome? outcome, string collectionId) =>
        outcome?.Run?.Rings.Any(r => string.Equals(r.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase) && r.Status == RingStatuses.Cancelled) == true;

    private static bool Has(Dictionary<ChangePlanItem, ChangePlanItemResult> results, ChangePlanItem item)
    {
        lock (results) return results.ContainsKey(item);
    }

    private static ChangePlanItemResult? Get(Dictionary<ChangePlanItem, ChangePlanItemResult> results, ChangePlanItem item)
    {
        lock (results) return results.TryGetValue(item, out var row) ? row : null;
    }

    private void Record(string action, SiteOperationOutcome outcome, Dictionary<ChangePlanItem, ChangePlanItemResult> results, IReadOnlySet<ChangePlanItem>? kept = null)
    {
        List<ChangePlanItemResult> rows;
        lock (results) rows = results.Values.ToList();
        var changed = rows.Count(r => !r.Skipped && (r.Succeeded || r.PartialChange));
        var failed = rows.Count(r => !r.Succeeded && !r.Skipped);
        var keptCount = kept is null ? 0 : rows.Count(r => r.Skipped && kept.Contains(r.Item));
        var skipped = rows.Count(r => r.Skipped) - keptCount;
        var counts = new Dictionary<string, int> { ["changed"] = changed, ["failed"] = failed, ["skipped"] = skipped };
        if (keptCount > 0) counts["kept"] = keptCount;
        log.Info(DeploymentWriter.LogArea, $"{action}: outcome={outcome} changed={changed} failed={failed} skipped={skipped} kept={keptCount}.");
        string target;
        try { target = siteTarget(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { target = ""; }
        history.Record(new SiteOperationEntry(DeploymentWriter.ToolName, action, target, outcome, counts));
    }

    /// <summary>Turns the ring outcomes of each reported change into the results of their plan lines, each once.</summary>
    private sealed class OutcomeReporter(RingRunPlan plan, Dictionary<ChangePlanItem, ChangePlanItemResult> results, IProgress<ChangePlanItemResult> progress)
        : IProgress<RingOutcome>
    {
        private readonly HashSet<DeploymentOutcome> _seen = new(ReferenceEqualityComparer.Instance);

        public void Report(RingOutcome value)
        {
            ArgumentNullException.ThrowIfNull(value);
            plan.AddOutcome(value);
            foreach (var ring in value.RingOutcomes)
            {
                ChangePlanItemResult result;
                lock (results)
                {
                    if (!_seen.Add(ring)) continue;
                    var entry = EntryOf(ring.Change);
                    if (entry is null || results.ContainsKey(entry.Item)) continue;
                    var message = SecretMask.Apply(ring.Message);
                    var notAttempted = ring.Message.StartsWith(DeploymentWriter.SkippedPrefix, StringComparison.OrdinalIgnoreCase);
                    var skipped = !ring.Succeeded && ring.UndoChange is null && (notAttempted || ring.NotCreated && ring.Distribution is not { Succeeded: true });
                    if (skipped && !notAttempted) message = $"{NothingCreatedText} {message}";
                    result = skipped
                        ? ChangePlanItemResult.Skip(entry.Item, message)
                        : new ChangePlanItemResult(entry.Item, ring.Succeeded, message) { PartialChange = !ring.Succeeded && ring.UndoChange is not null };
                    results[entry.Item] = result;
                }
                progress.Report(result);
            }
        }

        private RingPlanEntry? EntryOf(DeploymentChange change)
        {
            var key = change.Kind == DeploymentChangeKind.RemoveDeployment ? change.Existing?.DeploymentId ?? change.CreatedId : change.Collection?.CollectionId;
            if (string.IsNullOrEmpty(key)) return null;
            return plan.Entries.FirstOrDefault(e => e.Kind is RingEntryKind.Create or RingEntryKind.Promote or RingEntryKind.Remove
                && string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
        }
    }
}
