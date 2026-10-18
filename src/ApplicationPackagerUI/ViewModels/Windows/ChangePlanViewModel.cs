using System.Runtime.CompilerServices;
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
    public const string ForceCloseText = "The run has not stopped after the cancel. Close without waiting to leave it running; it may still be changing ConfigMgr.";
    public const string CancelledReason = "No result: the run was cancelled before this change reported.";
    public const string FailedReason = "No result: the run failed before this change reported.";
    public const string DetachedReason = "No result: the window was closed while the run was still working.";

    public static readonly TimeSpan DefaultForceCloseDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The capability <see cref="ConfirmedChangePlan.Issue"/> requires. The constructor is
    /// private and the only instance is stored in a private field of this view model.
    /// </summary>
    internal sealed class IssueKey
    {
        private IssueKey() { }

        static IssueKey() => s_issueKey = new IssueKey();
    }

    private static IssueKey? s_issueKey;

    static ChangePlanViewModel() => RuntimeHelpers.RunClassConstructor(typeof(IssueKey).TypeHandle);

    private readonly ChangePlan _plan;
    private readonly ChangePlanRun _run;
    private readonly ChangePlanRun? _undo;
    private readonly TimeSpan _forceCloseDelay;
    private ConfirmedChangePlan? _confirmed;
    private CancellationTokenSource? _cts;
    private ReportedItems? _reported;
    private bool _cancelRequested;

    public ChangePlanViewModel(ChangePlan plan, ChangePlanRun run, ChangePlanRun? undo = null, TimeSpan? forceCloseDelay = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(run);
        if (plan.HasUndo && undo is null) throw new ArgumentException("The plan declares an undo; pass the undo operation.", nameof(undo));
        if (!plan.HasUndo && undo is not null) throw new ArgumentException("The plan declares no undo.", nameof(undo));
        _plan = plan;
        _run = run;
        _undo = undo;
        _forceCloseDelay = forceCloseDelay ?? DefaultForceCloseDelay;
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

    /// <summary>True once the operator closed the window while a run ignored its cancel.</summary>
    public bool IsDetached { get; private set; }

    public event EventHandler? CloseRequested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _destructiveAcknowledged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanClose), nameof(ShowPlan), nameof(ShowResult), nameof(ShowConfirm), nameof(ShowUndo), nameof(ShowClose))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand), nameof(UndoCommand), nameof(CloseCommand))]
    private ChangePlanStage _stage = ChangePlanStage.Review;

    [ObservableProperty] private string _resultText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ForceCloseCommand))]
    private bool _showForceClose;

    public bool IsBusy => Stage is ChangePlanStage.Running or ChangePlanStage.Undoing;
    public bool CanClose => !IsBusy || IsDetached;
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
        _confirmed = ConfirmedChangePlan.Issue(s_issueKey!, _plan, DestructiveAcknowledged);
        Stage = ChangePlanStage.Running;
        ResultText = "Running...";
        var result = await RunAsync(_run, "");
        if (IsDetached) return;
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
        if (IsDetached) return;
        Outcome = Outcome with { UndoResult = result };
        Stage = ChangePlanStage.Undone;
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy)
        {
            RequestCancel();
            return;
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanCloseWindow() => ShowClose;

    [RelayCommand(CanExecute = nameof(CanCloseWindow))]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Asks a running operation to stop; the window stays open until it reports. When it
    /// has not reported after the force-close delay, <see cref="ShowForceClose"/> turns on.
    /// </summary>
    public void RequestCancel()
    {
        var cts = _cts;
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { return; }
        if (_cancelRequested) return;
        _cancelRequested = true;
        _ = OfferForceCloseAsync(cts);
    }

    private async Task OfferForceCloseAsync(CancellationTokenSource run)
    {
        await Task.Delay(_forceCloseDelay).ConfigureAwait(true);
        if (IsBusy && !IsDetached && ReferenceEquals(_cts, run)) ShowForceClose = true;
    }

    private bool CanForceClose() => ShowForceClose && IsBusy && !IsDetached;

    /// <summary>
    /// Closes the window without waiting for a run that ignores its cancel. The outcome
    /// keeps the items reported so far; the run's later result is never shown.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanForceClose))]
    private void ForceClose()
    {
        if (!CanForceClose()) return;
        var partial = Partial(_reported?.Snapshot() ?? [], ChangePlanStop.Detached, DetachedReason);
        Outcome = Stage == ChangePlanStage.Undoing
            ? Outcome with { UndoResult = partial }
            : new ChangePlanOutcome(true, partial, null);
        IsDetached = true;
        ShowForceClose = false;
        OnPropertyChanged(nameof(CanClose));
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task<ChangePlanResult> RunAsync(ChangePlanRun operation, string prefix)
    {
        var cts = new CancellationTokenSource();
        var reported = new ReportedItems();
        _cts = cts;
        _reported = reported;
        _cancelRequested = false;
        ShowForceClose = false;
        ResultRows.Clear();
        ChangePlanResult result;
        string text;
        try
        {
            result = await operation(_confirmed!, reported, cts.Token);
            text = result.Summary;
        }
        catch (OperationCanceledException)
        {
            result = Partial(reported.Snapshot(), ChangePlanStop.Cancelled, CancelledReason);
            text = CancelledText + ReportedBefore(reported.Snapshot(), "cancel");
        }
        catch (Exception ex)
        {
            result = Partial(reported.Snapshot(), ChangePlanStop.Failed, FailedReason);
            text = "The run failed: " + ex.Message + ReportedBefore(reported.Snapshot(), "failure");
        }
        finally
        {
            _cts = null;
            ShowForceClose = false;
            cts.Dispose();
        }

        if (!IsDetached)
        {
            foreach (var row in result.Items) ResultRows.Add(row);
            ResultText = prefix + text;
        }
        return result;
    }

    private static string ReportedBefore(IReadOnlyList<ChangePlanItemResult> reported, string what)
    {
        if (reported.Count == 0) return "";
        var changed = reported.Count(r => r.Succeeded);
        return $" Before the {what}: {changed} changed, {reported.Count - changed} failed.";
    }

    /// <summary>The reported outcomes, then every plan item without one as failed with the reason.</summary>
    private ChangePlanResult Partial(IReadOnlyList<ChangePlanItemResult> reported, ChangePlanStop stop, string reason)
    {
        var unmatched = reported.Select(r => r.Item).ToList();
        var rows = new List<ChangePlanItemResult>(reported);
        foreach (var item in (_confirmed?.Plan ?? _plan).Items)
        {
            var index = unmatched.IndexOf(item);
            if (index >= 0) unmatched.RemoveAt(index);
            else rows.Add(new ChangePlanItemResult(item, false, reason));
        }
        return new ChangePlanResult(rows.AsReadOnly(), stop);
    }

    // Unlike Progress<T>, a report is stored on the reporting thread before Report returns,
    // so an outcome reported just before a throw is never lost to a pending post.
    private sealed class ReportedItems : IProgress<ChangePlanItemResult>
    {
        private readonly List<ChangePlanItemResult> _items = [];

        public void Report(ChangePlanItemResult value)
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_items) _items.Add(value);
        }

        public IReadOnlyList<ChangePlanItemResult> Snapshot()
        {
            lock (_items) return _items.ToArray();
        }
    }
}
