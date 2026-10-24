using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.Services;

/// <summary>One line of a Pivots and Scripts plan: its plan item and, for a run, the device it targets.</summary>
public sealed record PivotsPlanEntry(ChangePlanItem Item, int? ResourceId);

/// <summary>
/// The prepared change behind a Pivots and Scripts plan. The change travels only with the plan the
/// operator confirmed, and <see cref="PivotsScriptsWriter"/> sends it only for that plan. The final
/// outcome of the send stays here for the page.
/// </summary>
public sealed class PivotsPlan
{
    private readonly object _gate = new();
    private PivotsOutcome? _outcome;

    public PivotsPlan(string action, PivotsChange change, ChangePlan plan, IReadOnlyList<PivotsPlanEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(plan);
        if (entries.Count == 0) throw new ArgumentException("A plan has at least one line.", nameof(entries));
        if (entries.Count != plan.Items.Count || entries.Where((e, i) => !ReferenceEquals(e.Item, plan.Items[i])).Any())
            throw new ArgumentException("The entries are the items of the plan.", nameof(entries));
        Action = action;
        Change = change;
        Plan = plan;
        Entries = entries;
    }

    public string Action { get; }
    public PivotsChange Change { get; }
    public ChangePlan Plan { get; }
    public IReadOnlyList<PivotsPlanEntry> Entries { get; }
    public PivotsChangeKind Kind => Change.Kind;

    /// <summary>The outcome the service returned for the send; null before a run or when the run threw.</summary>
    public PivotsOutcome? Outcome
    {
        get { lock (_gate) return _outcome; }
    }

    internal void SetOutcome(PivotsOutcome? outcome)
    {
        lock (_gate) _outcome = outcome;
    }

    /// <summary>True when the confirmed plan is this plan: the same items, in the same order, with the same confirmations.</summary>
    public bool IsConfirmedPlan(ConfirmedChangePlan confirmed)
    {
        var items = confirmed.Plan.Items;
        if (items.Count != Entries.Count) return false;
        for (var i = 0; i < items.Count; i++)
            if (!ReferenceEquals(items[i], Entries[i].Item)) return false;
        return confirmed.Plan.ExtraConfirmation == Plan.ExtraConfirmation && confirmed.Plan.SecondConfirmation == Plan.SecondConfirmation
            && confirmed.Plan.IsDestructive == Plan.IsDestructive && confirmed.Plan.DestructiveConfirmation == Plan.DestructiveConfirmation;
    }
}

/// <summary>
/// The only caller of <see cref="IPivotsScriptsService.ApplyAsync"/> in the desktop. It takes a
/// <see cref="ConfirmedChangePlan"/>, which only the change plan window issues, and sends the prepared
/// change of that plan and nothing else. A script run reaches the service with RunConfirmed only when
/// the confirmed plan asked its destructive box and its device-count box, and a built-in collection
/// with EveryDeviceAcknowledged only when the plan asked its every-device box; the flags the change
/// already had never travel.
/// </summary>
public sealed class PivotsScriptsWriter(IPivotsScriptsService service, ISiteOperationHistory history, IAppLog log, Func<string> siteTarget)
{
    public const string ToolName = "Pivots and Scripts";
    public const string LogArea = "PivotsScripts";
    public const string SkippedPrefix = "Skipped";
    public const string NoResultText = "No result: the service did not report this device.";
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

    public ChangePlanRun RunFor(PivotsPlan plan) => (confirmed, progress, ct) => Track(ApplyAsync(confirmed, plan, progress, ct));

    /// <summary>The change to send: the confirmation flags only as the confirmed plan asked them.</summary>
    public static PivotsChange ConfirmedChange(PivotsChange change, ConfirmedChangePlan confirmed)
    {
        var runConfirmed = change.Kind switch
        {
            PivotsChangeKind.RunPivot => true,
            PivotsChangeKind.RunScript => confirmed.Plan.IsDestructive && confirmed.Plan.ExtraConfirmation is not null,
            _ => false
        };
        var everyDevice = change.Kind is PivotsChangeKind.RunPivot or PivotsChangeKind.RunScript
            && change.Targets is { IsBuiltInCollection: true } && confirmed.Plan.SecondConfirmation is not null;
        return change with { RunConfirmed = runConfirmed, EveryDeviceAcknowledged = everyDevice };
    }

    public async Task<ChangePlanResult> ApplyAsync(ConfirmedChangePlan confirmed, PivotsPlan plan, IProgress<ChangePlanItemResult> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsConfirmedPlan(confirmed)) throw new InvalidOperationException("The confirmed plan is not the plan of this change.");
        plan.SetOutcome(null);
        var change = ConfirmedChange(plan.Change, confirmed);
        var steps = new StepReporter(plan, progress);
        var stop = SiteOperationOutcome.Failed;
        List<ChangePlanItemResult> rows = [];
        try
        {
            var outcome = await Task.Run(() => service.ApplyAsync(change, steps, ct), ct).ConfigureAwait(true);
            plan.SetOutcome(outcome);
            rows = Rows(plan, outcome);
            foreach (var row in rows) progress.Report(row);
            var failed = rows.Count(r => !r.Succeeded && !r.Skipped);
            var done = rows.Count(r => r.Succeeded && !r.Skipped);
            var cancelled = ct.IsCancellationRequested && rows.Any(r => r.Skipped);
            stop = cancelled ? SiteOperationOutcome.Cancelled
                : outcome.Succeeded && failed == 0 && rows.All(r => !r.Skipped) ? SiteOperationOutcome.Succeeded
                : done > 0 ? SiteOperationOutcome.PartlySucceeded
                : SiteOperationOutcome.Failed;
            return new ChangePlanResult(rows, cancelled ? ChangePlanStop.Cancelled : ChangePlanStop.Completed)
            {
                UndoAvailable = false,
                Headline = Headline(rows),
                Note = WaitNote(plan.Kind, outcome)
            };
        }
        catch (OperationCanceledException)
        {
            stop = SiteOperationOutcome.Cancelled;
            throw;
        }
        finally
        {
            Record(plan, stop, rows);
        }
    }

    private static string? WaitNote(PivotsChangeKind kind, PivotsOutcome outcome) =>
        kind is PivotsChangeKind.RunPivot or PivotsChangeKind.RunScript && outcome.OperationId is not null && outcome.Succeeded
            ? "The page waits for the results and shows them in the result tabs."
            : outcome.Message.Length > 0 ? SecretMask.Apply(outcome.Message) : null;

    /// <summary>The label of a successful script action.</summary>
    public static string DoneLabel(PivotsChangeKind kind) => kind switch
    {
        PivotsChangeKind.SubmitScript => "Submitted",
        PivotsChangeKind.ApproveScript => "Approved",
        PivotsChangeKind.DenyScript => "Denied",
        PivotsChangeKind.RemoveScript => "Removed",
        _ => "Sent"
    };

    /// <summary>The row of a device result: Sent, Running and Completed count as done, Skipped as not attempted, anything else as failed.</summary>
    public static ChangePlanItemResult DeviceRow(ChangePlanItem item, DeviceResult device)
    {
        var text = SecretMask.Apply(device.Message);
        if (device.State == PivotsDeviceStates.Skipped || text.StartsWith(SkippedPrefix, StringComparison.OrdinalIgnoreCase))
            return ChangePlanItemResult.Skip(item, text.StartsWith(SkippedPrefix, StringComparison.OrdinalIgnoreCase) ? text : $"{SkippedPrefix}: {text}") with { ResultLabel = Skipped };
        var label = PivotsLabels.DeviceState(device.State);
        return device.State is PivotsDeviceStates.Sent or PivotsDeviceStates.Running or PivotsDeviceStates.Completed
            ? new ChangePlanItemResult(item, true, text) { ResultLabel = label }
            : new ChangePlanItemResult(item, false, text) { ResultLabel = label };
    }

    /// <summary>A row from the message of the whole outcome: a Skipped message stays skipped.</summary>
    public static ChangePlanItemResult WholeRow(ChangePlanItem item, PivotsChangeKind kind, PivotsOutcome outcome)
    {
        var text = SecretMask.Apply(outcome.Message);
        if (text.StartsWith(SkippedPrefix, StringComparison.OrdinalIgnoreCase)) return ChangePlanItemResult.Skip(item, text) with { ResultLabel = Skipped };
        return outcome.Succeeded ? new ChangePlanItemResult(item, true, text) { ResultLabel = DoneLabel(kind) } : new ChangePlanItemResult(item, false, text) { ResultLabel = Failed };
    }

    private static List<ChangePlanItemResult> Rows(PivotsPlan plan, PivotsOutcome outcome)
    {
        var byId = new Dictionary<int, DeviceResult>();
        foreach (var device in outcome.Devices) byId.TryAdd(device.ResourceId, device);
        var rows = new List<ChangePlanItemResult>(plan.Entries.Count);
        foreach (var entry in plan.Entries)
        {
            if (entry.ResourceId is not { } id) rows.Add(WholeRow(entry.Item, plan.Kind, outcome));
            else if (byId.TryGetValue(id, out var device)) rows.Add(DeviceRow(entry.Item, device));
            else if (!outcome.Succeeded || outcome.Message.StartsWith(SkippedPrefix, StringComparison.OrdinalIgnoreCase)) rows.Add(WholeRow(entry.Item, plan.Kind, outcome));
            else rows.Add(new ChangePlanItemResult(entry.Item, false, NoResultText) { ResultLabel = Failed });
        }
        return rows;
    }

    /// <summary>The first sentence of a result: the number of lines of each result.</summary>
    public static string Headline(IReadOnlyList<ChangePlanItemResult> rows)
    {
        var parts = rows.GroupBy(r => r.OutcomeText, StringComparer.Ordinal).Select(g => $"{PivotsLabels.Number(g.Count())} {g.Key.ToLowerInvariant()}").ToList();
        return parts.Count == 0 ? "No line reported a result." : string.Join(", ", parts) + ".";
    }

    private void Record(PivotsPlan plan, SiteOperationOutcome outcome, IReadOnlyList<ChangePlanItemResult> rows)
    {
        var counts = new Dictionary<string, int>
        {
            ["targets"] = plan.Entries.Count,
            ["done"] = rows.Count(r => r.Succeeded && !r.Skipped),
            ["failed"] = rows.Count(r => !r.Succeeded && !r.Skipped),
            ["skipped"] = rows.Count(r => r.Skipped)
        };
        log.Info(LogArea, $"{plan.Action}: outcome={outcome} " + string.Join(" ", counts.Select(c => $"{c.Key}={c.Value}")) + ".");
        string target;
        try { target = siteTarget(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { target = ""; }
        history.Record(new SiteOperationEntry(ToolName, plan.Action, target, outcome, counts));
    }

    /// <summary>The progress of the send is a step text only; it never becomes a result.</summary>
    private sealed class StepReporter(PivotsPlan plan, IProgress<ChangePlanItemResult> progress) : IProgress<PivotsOutcome>
    {
        public void Report(PivotsOutcome value)
        {
            ArgumentNullException.ThrowIfNull(value);
            progress.Report(new ChangePlanItemResult(plan.Entries[0].Item, true, value.Message) { Interim = true });
        }
    }
}
