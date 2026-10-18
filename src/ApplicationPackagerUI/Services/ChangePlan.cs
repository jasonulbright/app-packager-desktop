namespace ApplicationPackagerUI.Services;

/// <summary>One change a plan makes: the action, the object it changes and an optional detail.</summary>
public sealed record ChangePlanItem(string Action, string Target, string? Detail);

/// <summary>
/// What a write operation will change, shown to the operator before anything runs.
/// <paramref name="actionName"/> names the Confirm button (for example "Remove 2 windows").
/// </summary>
public sealed class ChangePlan
{
    public ChangePlan(string title, string actionName, IEnumerable<ChangePlanItem> items, bool isDestructive = false, bool hasUndo = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);
        ArgumentNullException.ThrowIfNull(items);
        Items = items.ToList();
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
}

/// <summary>
/// A plan the operator confirmed in the change plan window. It has no public constructor
/// and no public factory: the window's view model issues it after Confirm, so a write
/// operation that takes it cannot run unconfirmed.
/// </summary>
public sealed class ConfirmedChangePlan
{
    private ConfirmedChangePlan(ChangePlan plan) => Plan = plan;

    public ChangePlan Plan { get; }

    internal static ConfirmedChangePlan Issue(ChangePlan plan, bool destructiveAcknowledged)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.IsDestructive && !destructiveAcknowledged)
            throw new InvalidOperationException("A destructive plan needs the extra confirmation.");
        return new ConfirmedChangePlan(plan);
    }
}

/// <summary>A write operation or its undo; it runs only with a confirmed plan.</summary>
public delegate Task<ChangePlanResult> ChangePlanRun(ConfirmedChangePlan plan, CancellationToken ct);

public sealed record ChangePlanItemResult(ChangePlanItem Item, bool Succeeded, string Message)
{
    public string OutcomeText => Succeeded ? "Changed" : "Failed";
}

public sealed record ChangePlanResult(IReadOnlyList<ChangePlanItemResult> Items)
{
    public int SucceededCount => Items.Count(i => i.Succeeded);
    public int FailedCount => Items.Count(i => !i.Succeeded);
    public string Summary => $"{SucceededCount} changed, {FailedCount} failed.";
}

/// <summary>What happened in the change plan window.</summary>
public sealed record ChangePlanOutcome(bool Confirmed, ChangePlanResult? Result, ChangePlanResult? UndoResult)
{
    public static ChangePlanOutcome NotConfirmed { get; } = new(false, null, null);
}
