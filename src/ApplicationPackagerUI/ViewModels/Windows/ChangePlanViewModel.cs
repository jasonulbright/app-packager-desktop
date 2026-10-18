using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Windows;

public enum ChangePlanStage
{
    Review,
    Running,
    Done,
    Undoing,
    Undone
}

/// <summary>
/// The change plan window: the list of changes, Confirm named for the action, an extra
/// check for a destructive plan, the result report and Undo when the plan declares one.
/// </summary>
public sealed partial class ChangePlanViewModel : ObservableObject
{
    public const string CancelledText = "The run was cancelled. Changes made before the cancel stay in place.";
    public const string DestructiveCheckText = "I understand that this change removes or replaces data in ConfigMgr.";

    private readonly ChangePlan _plan;
    private readonly ChangePlanRun _run;
    private readonly ChangePlanRun? _undo;
    private ConfirmedChangePlan? _confirmed;
    private CancellationTokenSource? _cts;

    public ChangePlanViewModel(ChangePlan plan, ChangePlanRun run, ChangePlanRun? undo = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(run);
        if (plan.HasUndo && undo is null) throw new ArgumentException("The plan declares an undo; pass the undo operation.", nameof(undo));
        if (!plan.HasUndo && undo is not null) throw new ArgumentException("The plan declares no undo.", nameof(undo));
        _plan = plan;
        _run = run;
        _undo = undo;
        Items = new ObservableCollection<ChangePlanItem>(plan.Items);
    }

    public string Title => _plan.Title;
    public string ConfirmText => _plan.ActionName;

    /// <summary>The Confirm button content; an underscore in the action name stays literal.</summary>
    public string ConfirmContent => _plan.ActionName.Replace("_", "__", StringComparison.Ordinal);

    public bool IsDestructive => _plan.IsDestructive;
    public ObservableCollection<ChangePlanItem> Items { get; }
    public ObservableCollection<ChangePlanItemResult> ResultRows { get; } = [];

    public string SummaryText => (Items.Count == 1 ? "1 change" : $"{Items.Count} changes") + ". Review the list, then confirm.";

    public ChangePlanOutcome Outcome { get; private set; } = ChangePlanOutcome.NotConfirmed;

    public event EventHandler? CloseRequested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _destructiveAcknowledged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanClose), nameof(ShowPlan), nameof(ShowResult), nameof(ShowConfirm), nameof(ShowUndo), nameof(ShowClose))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand), nameof(UndoCommand), nameof(CloseCommand))]
    private ChangePlanStage _stage = ChangePlanStage.Review;

    [ObservableProperty] private string _resultText = "";

    public bool IsBusy => Stage is ChangePlanStage.Running or ChangePlanStage.Undoing;
    public bool CanClose => !IsBusy;
    public bool ShowPlan => Stage == ChangePlanStage.Review;
    public bool ShowResult => Stage != ChangePlanStage.Review;
    public bool ShowConfirm => Stage is ChangePlanStage.Review or ChangePlanStage.Running;
    public bool ShowClose => Stage is ChangePlanStage.Done or ChangePlanStage.Undone;
    public bool ShowUndo => _plan.HasUndo && (Stage is ChangePlanStage.Done or ChangePlanStage.Undoing or ChangePlanStage.Undone);

    private bool CanConfirm() => Stage == ChangePlanStage.Review && (!IsDestructive || DestructiveAcknowledged);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        if (!CanConfirm()) return;
        _confirmed = ConfirmedChangePlan.Issue(_plan, DestructiveAcknowledged);
        Stage = ChangePlanStage.Running;
        ResultText = "Running...";
        var result = await RunAsync(_run, "");
        Outcome = new ChangePlanOutcome(true, result, null);
        Stage = ChangePlanStage.Done;
    }

    private bool CanUndo() =>
        _undo is not null && _confirmed is not null && Stage == ChangePlanStage.Done && Outcome.Result is { SucceededCount: > 0 };

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (!CanUndo()) return;
        Stage = ChangePlanStage.Undoing;
        ResultText = "Undoing...";
        var result = await RunAsync(_undo!, "Undo: ");
        Outcome = Outcome with { UndoResult = result };
        Stage = ChangePlanStage.Undone;
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy)
        {
            _cts?.Cancel();
            return;
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanCloseWindow() => ShowClose;

    [RelayCommand(CanExecute = nameof(CanCloseWindow))]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Asks a running operation to stop; the window stays open until it reports.</summary>
    public void RequestCancel() => _cts?.Cancel();

    private async Task<ChangePlanResult?> RunAsync(ChangePlanRun operation, string prefix)
    {
        using var cts = new CancellationTokenSource();
        _cts = cts;
        ResultRows.Clear();
        try
        {
            var result = await operation(_confirmed!, cts.Token);
            foreach (var row in result.Items) ResultRows.Add(row);
            ResultText = prefix + result.Summary;
            return result;
        }
        catch (OperationCanceledException)
        {
            ResultText = prefix + CancelledText;
            return null;
        }
        catch (Exception ex)
        {
            ResultText = prefix + "The run failed: " + ex.Message;
            return null;
        }
        finally
        {
            _cts = null;
        }
    }
}
