using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Services;

/// <summary>One change a plan makes: the action, the object it changes and an optional detail.</summary>
public sealed record ChangePlanItem(string Action, string Target, string? Detail)
{
    public override string ToString() => $"{Action} {Target}";
}

/// <summary>
/// What a write operation will change, shown to the operator before anything runs.
/// <paramref name="actionName"/> names the Confirm button (for example "Remove 2 windows").
/// The items are copied when the plan is made, so a later change to the caller's list
/// cannot change what the operator confirms.
/// </summary>
public sealed class ChangePlan
{
    public ChangePlan(string title, string actionName, IEnumerable<ChangePlanItem> items, bool isDestructive = false, bool hasUndo = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);
        ArgumentNullException.ThrowIfNull(items);
        Items = new ReadOnlyCollection<ChangePlanItem>(items.ToArray());
        if (Items.Count == 0) throw new ArgumentException("A change plan lists at least one change.", nameof(items));
        Title = title.Trim();
        ActionName = actionName.Trim();
        IsDestructive = isDestructive;
        HasUndo = hasUndo;
    }

    public string Title { get; }
    public string ActionName { get; }
    public IReadOnlyList<ChangePlanItem> Items { get; }
    public bool IsDestructive { get; }
    public bool HasUndo { get; }

    /// <summary>
    /// The text of a second check box the operator checks before Confirm, for a change with an
    /// effect beyond ConfigMgr itself; null for none.
    /// </summary>
    public string? ExtraConfirmation { get; init; }

    /// <summary>
    /// The text of a check box that changes how the plan runs and is off until the operator
    /// selects it; null for none. Confirm does not need it.
    /// </summary>
    public string? OptionalChoice { get; init; }

    /// <summary>The fixed action name the log uses instead of the title, which can hold object names; null logs a generic name.</summary>
    public string? LogName { get; init; }

    internal ChangePlan Copy() => new(Title, ActionName, Items, IsDestructive, HasUndo) { ExtraConfirmation = ExtraConfirmation, OptionalChoice = OptionalChoice, LogName = LogName };
}

/// <summary>
/// A plan the operator confirmed in the change plan window. Only the window's view model
/// holds the key that <see cref="Issue"/> requires, so a write operation that takes this
/// type cannot run unconfirmed.
/// </summary>
public sealed class ConfirmedChangePlan
{
    private ConfirmedChangePlan(ChangePlan plan, bool optionChosen)
    {
        Plan = plan;
        OptionChosen = optionChosen;
    }

    public ChangePlan Plan { get; }

    /// <summary>True when the plan has an optional choice and the operator selected it.</summary>
    public bool OptionChosen { get; }

    internal static ConfirmedChangePlan Issue(ChangePlanViewModel.IssueKey key, ChangePlan plan, bool destructiveAcknowledged, bool extraAcknowledged = false, bool optionChosen = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.IsDestructive && !destructiveAcknowledged)
            throw new InvalidOperationException("A destructive plan needs the extra confirmation.");
        if (plan.ExtraConfirmation is not null && !extraAcknowledged)
            throw new InvalidOperationException("The plan needs its second confirmation.");
        return new ConfirmedChangePlan(plan.Copy(), optionChosen && plan.OptionalChoice is not null);
    }
}

/// <summary>
/// A write operation or its undo; it runs only with a confirmed plan. It reports each
/// item's outcome through <c>progress</c> as soon as it is known, so the outcomes already
/// reported survive a run that throws, is cancelled or is left running.
/// </summary>
public delegate Task<ChangePlanResult> ChangePlanRun(ConfirmedChangePlan plan, IProgress<ChangePlanItemResult> progress, CancellationToken ct);

public sealed record ChangePlanItemResult(ChangePlanItem Item, bool Succeeded, string Message)
{
    /// <summary>The run left the item unchanged on purpose; a skipped item counts neither as changed nor as failed.</summary>
    public bool Skipped { get; init; }

    /// <summary>The item failed after part of its change was made; it counts as failed and as changed.</summary>
    public bool PartialChange { get; init; }

    /// <summary>A step of a change that still runs: shown while the run works, never a result of its own.</summary>
    public bool Interim { get; init; }

    public string OutcomeText => Skipped ? "Skipped" : Succeeded ? "Changed" : "Failed";

    public static ChangePlanItemResult Skip(ChangePlanItem item, string message) => new(item, false, message) { Skipped = true };

    public override string ToString() => $"{OutcomeText}, {Item}, {Message}";
}

/// <summary>How a run ended.</summary>
public enum ChangePlanStop
{
    Completed,
    Cancelled,
    Failed,
    Detached
}

public sealed record ChangePlanResult(IReadOnlyList<ChangePlanItemResult> Items, ChangePlanStop Stop = ChangePlanStop.Completed)
{
    public int SucceededCount => Items.Count(i => i.Succeeded && !i.Skipped);
    public int FailedCount => Items.Count(i => !i.Succeeded && !i.Skipped);
    public int SkippedCount => Items.Count(i => i.Skipped);
    public int PartialCount => Items.Count(i => i.PartialChange && !i.Succeeded && !i.Skipped);

    /// <summary>False when the run changed nothing that its undo reverses; null leaves Undo to the counts.</summary>
    public bool? UndoAvailable { get; init; }

    /// <summary>True for an undo the operator declined before it started: nothing ran, so the earlier result and its Undo stay.</summary>
    public bool Declined { get; init; }

    /// <summary>A sentence the summary ends with; null for none.</summary>
    public string? Note { get; init; }

    /// <summary>The first sentence of the summary in the words of the operation, for example "2 removed, 1 kept."; null uses the counts.</summary>
    public string? Headline { get; init; }

    public string Summary => (Headline is { Length: > 0 } headline ? headline
        : SkippedCount == 0
        ? $"{SucceededCount} changed, {FailedCount} failed."
        : $"{SucceededCount} changed, {FailedCount} failed, {SkippedCount} skipped.")
        + PartialCount switch
        {
            0 => "",
            1 => " 1 failed change was made in part; Undo can reverse it.",
            var n => $" {n} failed changes were made in part; Undo can reverse them."
        }
        + (Note is { Length: > 0 } note ? " " + note : "");
}

/// <summary>What happened in the change plan window.</summary>
public sealed record ChangePlanOutcome(bool Confirmed, ChangePlanResult? Result, ChangePlanResult? UndoResult)
{
    public static ChangePlanOutcome NotConfirmed { get; } = new(false, null, null);
}
