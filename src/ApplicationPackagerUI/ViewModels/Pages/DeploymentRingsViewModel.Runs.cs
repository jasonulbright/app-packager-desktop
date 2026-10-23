using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerUI.ViewModels.Pages;

public sealed partial class DeploymentRingsViewModel
{
    public const string RunsFailedText = "The runs could not be read. The log has the details.";
    public const string CountsFailedText = "The live counts could not be read. The log has the details.";
    public const string ReconcileFailedText = "The run could not be reconciled. The log has the details.";
    public const string PickRun = "Select a run in the list.";
    public const string RunClosed = "The run is closed. Nothing can be changed.";

    private int _runsGeneration;
    private int _runLoads;
    private int _runLoadEpoch;
    private int _countsGeneration;
    private CancellationTokenSource? _runsCts;
    private Dictionary<int, RingLiveCounts> _counts = [];

    public ObservableCollection<RingRunRow> Runs { get; } = [];
    public ObservableCollection<RingRunRingRow> RunRings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRun))]
    private RingRunRow? _selectedRun;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AbandonRingCommand))]
    private RingRunRingRow? _selectedRunRing;

    /// <summary>The values of the selected run file that cannot be read; they block Promote and Undo run. Empty when every value was read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunProblems))]
    private string _runProblems = "";

    public bool HasRunProblems => RunProblems.Length > 0;

    /// <summary>Lists the closed runs after the open ones; a closed run is read only.</summary>
    [ObservableProperty] private bool _showClosedRuns;

    /// <summary>The answer to the last action of the Open runs area, such as the owner of a held lock.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunsMessage))]
    private string _runsMessage = "";

    /// <summary>What Promote does now, or why it cannot run.</summary>
    [ObservableProperty] private string _promoteReason = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReconcileCommand))]
    private bool _isLoadingRuns;

    [ObservableProperty] private bool _isLoadingCounts;

    [ObservableProperty] private string _runCount = "";

    public bool HasSelectedRun => SelectedRun is not null;
    public bool HasRunsMessage => RunsMessage.Length > 0;

    /// <summary>The state of Promote for the selected run with the counts read last.</summary>
    public RingPromoteState Promote => RingText.PromoteState(SelectedRun?.Run, _counts);

    partial void OnSelectedRunChanged(RingRunRow? oldValue, RingRunRow? newValue)
    {
        var value = newValue;
        var sameRun = string.Equals(oldValue?.RunId, newValue?.RunId, StringComparison.OrdinalIgnoreCase);
        if (!sameRun)
        {
            RunsMessage = "";
            ClearRefusal();
        }
        var selectedRing = sameRun ? SelectedRunRing?.Index : null;
        _countsGeneration++;
        _counts = [];
        SelectedRunRing = null;
        RunRings.Clear();
        if (value is not null)
            foreach (var ring in value.Run.Rings.OrderBy(r => r.Index)) RunRings.Add(new RingRunRingRow(ring, value.Run.TimeBasedOn));
        SelectedRunRing = RunRings.FirstOrDefault(r => r.Index == selectedRing);
        RunProblems = RingText.ProblemText(value?.Run);
        IsLoadingCounts = false;
        UpdatePromote();
        LockMessage = value?.Run.Lock is { } held ? LockText(held) : "";
        if (value is { Run.IsClosed: false } && Shared.IsActive && CollectionComplianceViewModel.ConnectionProblem(Services.LoadSettings()) is null)
            CountsTask = LoadCountsAsync(value.Run);
    }

    /// <summary>The lock file of the selected run with its owner and age; empty when the run has no lock file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLockMessage))]
    private string _lockMessage = "";

    public bool HasLockMessage => LockMessage.Length > 0;

    /// <summary>A note about run files the last list could not read; empty when every file was read.</summary>
    [ObservableProperty] private string _runListNote = "";

    public static string LockText(RingLockInfo held) =>
        $"Another session holds the lock of this run: {SecretMask.Apply(held.Owner)}, {RingFolderOptionsViewModel.AgeText(held.Age)} ago. Lock file: {held.Path}. "
        + (held.Stale
            ? "The lock is older than the stale lock age. Another session may still be running: check before you remove the lock file by hand. It is never removed automatically."
            : "Promote, Reconcile, Close and Undo run wait until that session releases it.");

    /// <summary>True when Undo run can be offered: an open run, or a closed run that still has a created deployment.</summary>
    public static bool CanUndo(RingRun? run) =>
        run is not null && (!run.IsClosed || run.Rings.Any(r => string.Equals(r.Status, RingStatuses.Created, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The read of the live counts started last, or a completed task.</summary>
    public Task CountsTask { get; private set; } = Task.CompletedTask;

    partial void OnShowClosedRunsChanged(bool value)
    {
        if (Shared.IsActive && IsLoaded) _ = LoadRunsAsync();
    }

    private void UpdatePromote()
    {
        PromoteReason = Promote.Reason;
        OnPropertyChanged(nameof(Promote));
        NotifyRunCommands();
    }

    private void NotifyRunCommands()
    {
        PromoteRingCommand.NotifyCanExecuteChanged();
        UndoRunCommand.NotifyCanExecuteChanged();
        CloseRunCommand.NotifyCanExecuteChanged();
        AbandonRingCommand.NotifyCanExecuteChanged();
    }

    private async Task<IReadOnlyList<RingRun>> ReadRunsAsync(bool showClosed, CancellationToken ct)
    {
        var open = await Services.Rings.ListRunsAsync(false, ct);
        if (!showClosed) return open;
        var closed = await Services.Rings.ListRunsAsync(true, ct);
        return [.. open, .. closed];
    }

    private void ShowRuns(IReadOnlyList<RingRun> runs, bool showClosed)
    {
        var selected = SelectedRun?.RunId;
        var wanted = runs.Where(r => showClosed || !r.IsClosed).OrderBy(r => r.IsClosed).ThenByDescending(r => r.CreatedUtc).ToList();
        Runs.Clear();
        foreach (var run in wanted) Runs.Add(new RingRunRow(run));
        // New rows replace the old ones, so the selection moves to the new row of the same run and its live counts are read again.
        SelectedRun = Runs.FirstOrDefault(r => string.Equals(r.RunId, selected, StringComparison.OrdinalIgnoreCase));
        RunListNote = Services.Rings.RunMessage is { Length: > 0 } note ? SecretMask.Apply(note) : "";
        var open = wanted.Count(r => !r.IsClosed);
        RunCount = wanted.Count == open ? DeploymentText.Count(open, "open run") : $"{DeploymentText.Count(open, "open run")}, {DeploymentText.Count(wanted.Count - open, "closed run")}";
    }

    /// <summary>Reads the runs again and keeps the selected run; a read that ends after a newer read or the visit is discarded.</summary>
    public async Task LoadRunsAsync()
    {
        var mine = Interlocked.Increment(ref _runsGeneration);
        var generation = Shared.Generation;
        var showClosed = ShowClosedRuns;
        try { _runsCts?.Cancel(); } catch (ObjectDisposedException) { }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Shared.VisitToken);
        _runsCts = cts;
        RunsMessage = "";
        var epoch = _runLoadEpoch;
        _runLoads++;
        IsLoadingRuns = true;
        try
        {
            var runs = await Task.Run(() => ReadRunsAsync(showClosed, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (mine != _runsGeneration || generation != Shared.Generation) return;
            ShowRuns(runs, showClosed);
            ClearRefusal();
        }
        catch (OperationCanceledException)
        {
            if (mine == _runsGeneration && generation == Shared.Generation) RunsMessage = ReadCancelled;
        }
        catch (Exception ex)
        {
            if (mine != _runsGeneration || generation != Shared.Generation) return;
            RunsMessage = DeploymentShared.PlainMessage(ex, RunsFailedText);
            Services.Log.Warning(DeploymentShared.LogArea, $"Rings: the runs could not be read ({DeploymentShared.FailureName(ex)}).");
        }
        finally
        {
            if (ReferenceEquals(_runsCts, cts)) _runsCts = null;
            // A load started before the release counts in the old epoch only.
            if (epoch == _runLoadEpoch)
            {
                _runLoads = Math.Max(0, _runLoads - 1);
                IsLoadingRuns = _runLoads > 0;
            }
        }
    }

    /// <summary>Reads the live counts of the run's created rings; counts for a run that is no longer selected are discarded.</summary>
    private async Task LoadCountsAsync(RingRun run)
    {
        var mine = ++_countsGeneration;
        var generation = Shared.Generation;
        IsLoadingCounts = true;
        try
        {
            var visit = Shared.VisitToken;
            var counts = await Task.Run(() => Services.Rings.LiveCountsAsync(run, visit), visit);
            if (mine != _countsGeneration || generation != Shared.Generation || !string.Equals(SelectedRun?.RunId, run.RunId, StringComparison.OrdinalIgnoreCase)) return;
            _counts = counts.GroupBy(c => c.Index).ToDictionary(g => g.Key, g => g.Last());
            foreach (var row in RunRings) row.Counts = _counts.TryGetValue(row.Index, out var c) ? c : null;
            UpdatePromote();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (mine != _countsGeneration || generation != Shared.Generation) return;
            RunsMessage = DeploymentShared.PlainMessage(ex, CountsFailedText);
            Services.Log.Warning(DeploymentShared.LogArea, $"Rings: the live counts could not be read ({DeploymentShared.FailureName(ex)}).");
        }
        finally
        {
            if (mine == _countsGeneration) IsLoadingCounts = false;
        }
    }

    private void CancelRunReads()
    {
        try { _runsCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Starts a read of the runs by the tab load: a read that <see cref="LoadRunsAsync"/> started before is
    /// cancelled and its result discarded, and the tab load shows its runs only while no newer read started.
    /// Runs off the UI thread.
    /// </summary>
    private int BeginTabRunsRead()
    {
        var mine = Interlocked.Increment(ref _runsGeneration);
        try { Volatile.Read(ref _runsCts)?.Cancel(); } catch (ObjectDisposedException) { }
        return mine;
    }

    private void ReleaseRuns()
    {
        Interlocked.Increment(ref _runsGeneration);
        _countsGeneration++;
        CancelRunReads();
        _runsCts = null;
        _counts = [];
        SelectedRunRing = null;
        SelectedRun = null;
        Runs.Clear();
        RunRings.Clear();
        RunsMessage = "";
        RunCount = "";
        PromoteReason = "";
        RunProblems = "";
        _runLoads = 0;
        _runLoadEpoch++;
        IsLoadingRuns = false;
        IsLoadingCounts = false;
        ShowClosedRuns = false;
    }

    private bool CanReconcile() => !Shared.IsWriting && !IsLoadingRuns;

    /// <summary>
    /// Reconciles every open run: each takes its lock, resolves Creating rings, marks a ring Removed
    /// only when its deployment is gone by its ID and closes a finished run. A run whose lock another
    /// session holds is named with the lock owner and left as it is.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReconcile))]
    private async Task Reconcile()
    {
        if (Shared.IsWriting)
        {
            RunsMessage = _background ? DeploymentShared.BackgroundRunText : WriteRunning;
            return;
        }
        if (Shared.ConnectionProblem() is { } connection)
        {
            RunsMessage = connection;
            return;
        }
        var generation = Shared.Generation;
        ClearRefusal();
        var ids = Runs.Where(r => !r.Run.IsClosed).Select(r => r.RunId).ToList();
        Shared.IsWriting = true;
        var problems = new List<string>();
        var done = 0;
        try
        {
            RunsMessage = "Reconciling the open runs.";
            var visit = Shared.VisitToken;
            foreach (var id in ids)
            {
                if (visit.IsCancellationRequested) break;
                try
                {
                    await Task.Run(() => Services.Rings.ReconcileAsync(id, visit), visit);
                    done++;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    problems.Add($"{id}: {DeploymentShared.PlainMessage(ex, ReconcileFailedText)}");
                    Services.Log.Warning(DeploymentShared.LogArea, $"Rings: a run could not be reconciled ({DeploymentShared.FailureName(ex)}).");
                }
            }
        }
        finally { Shared.IsWriting = false; }
        if (generation != Shared.Generation) return;
        Services.Log.Info(DeploymentShared.LogArea, $"Rings: reconciled {done} of {ids.Count} runs.");
        await LoadRunsAsync();
        if (generation != Shared.Generation) return;
        RunsMessage = $"Reconciled {DeploymentText.Count(done, "run")}." + (problems.Count == 0 ? "" : " " + string.Join(" ", problems));
    }

    private bool CanPromote() => !Shared.IsWriting && Promote is { Open: true, Next: not null };

    /// <summary>Shows the plan that creates the next held ring of the selected run; the service checks the ring, the threshold and the object again.</summary>
    [RelayCommand(CanExecute = nameof(CanPromote))]
    private async Task PromoteRing()
    {
        if (SelectedRun?.Run is not { } run)
        {
            RunsMessage = PickRun;
            return;
        }
        if (Promote is not { Open: true, Next: { } next })
        {
            RunsMessage = Promote.Reason;
            return;
        }
        var request = new RingChange(Kind: RingChangeKind.Promote, Plan: null, Object: null, Rows: [], PlanStart: null, RunId: run.RunId, RingIndex: next.Index,
            RequiredConfirmed: false, BackdatedAcknowledged: false, CreateNowContentDistributing: false, RingChanges: [], Impact: [], Checks: [], Fingerprint: null);
        var result = await RunRingPlanAsync(request, prepared => RingText.BuildPromotePlan(prepared, Services.Clock, run.TimeBasedOn), null, message => RunsMessage = message);
        if (result is { Outcome.Confirmed: true }) await AfterRingWriteAsync();
    }

    private bool CanChangeRun() => !Shared.IsWriting && SelectedRun is { Run.IsClosed: false };

    private bool CanUndoRun() => !Shared.IsWriting && CanUndo(SelectedRun?.Run) && SelectedRun?.Run.Problems.Count == 0;

    /// <summary>
    /// Shows the destructive plan that removes the deployments this run created, cancels its held
    /// rings and closes it; a closed run that still has a created deployment can be undone too.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUndoRun))]
    private async Task UndoRun()
    {
        if (SelectedRun?.Run is not { } run)
        {
            RunsMessage = PickRun;
            return;
        }
        if (!CanUndo(run))
        {
            RunsMessage = "The run is closed and has no created deployment. There is nothing to undo.";
            return;
        }
        if (run.Problems.Count > 0)
        {
            RunsMessage = RingText.ProblemText(run);
            return;
        }
        var request = new RingChange(Kind: RingChangeKind.UndoRun, Plan: null, Object: null, Rows: [], PlanStart: null, RunId: run.RunId, RingIndex: null,
            RequiredConfirmed: false, BackdatedAcknowledged: false, CreateNowContentDistributing: false, RingChanges: [], Impact: [], Checks: [], Fingerprint: null);
        var result = await RunRingPlanAsync(request, prepared => RingText.BuildUndoRunPlan(prepared, RunName(run), run), null, message => RunsMessage = message);
        if (result is { Outcome.Confirmed: true }) await AfterRingWriteAsync();
    }

    /// <summary>Shows the plan that closes the selected run; no deployment changes.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeRun))]
    private async Task CloseRun()
    {
        if (SelectedRun?.Run is not { } run)
        {
            RunsMessage = PickRun;
            return;
        }
        if (run.IsClosed)
        {
            RunsMessage = RunClosed;
            return;
        }
        var request = new RingChange(Kind: RingChangeKind.CloseRun, Plan: null, Object: null, Rows: [], PlanStart: null, RunId: run.RunId, RingIndex: null,
            RequiredConfirmed: false, BackdatedAcknowledged: false, CreateNowContentDistributing: false, RingChanges: [], Impact: [], Checks: [], Fingerprint: null);
        var result = await RunRingPlanAsync(request, prepared => RingText.BuildClosePlan(prepared, RunName(run)), null, message => RunsMessage = message);
        if (result is { Outcome.Confirmed: true }) await AfterRingWriteAsync();
    }

    private bool CanAbandonRing() =>
        !Shared.IsWriting && SelectedRun is { Run.IsClosed: false } && SelectedRunRing is { } ring
        && string.Equals(ring.Ring.Status, RingStatuses.Creating, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Shows the plan that returns the selected ring in Creating to Held when Reconcile cannot prove
    /// its create; the plan names the deployment that may exist and asks its own check box.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAbandonRing))]
    private async Task AbandonRing()
    {
        if (SelectedRun?.Run is not { } run || SelectedRunRing?.Ring is not { } ring)
        {
            RunsMessage = "Select a run and its ring in Creating.";
            return;
        }
        if (run.IsClosed)
        {
            RunsMessage = RunClosed;
            return;
        }
        if (!string.Equals(ring.Status, RingStatuses.Creating, StringComparison.OrdinalIgnoreCase))
        {
            RunsMessage = $"Ring {ring.Name} is {ring.Status.ToLowerInvariant()}. Only a ring in Creating can be abandoned.";
            return;
        }
        var request = new RingChange(Kind: RingChangeKind.AbandonRing, Plan: null, Object: null, Rows: [], PlanStart: null, RunId: run.RunId, RingIndex: ring.Index,
            RequiredConfirmed: false, BackdatedAcknowledged: false, CreateNowContentDistributing: false, RingChanges: [], Impact: [], Checks: [], Fingerprint: null);
        var result = await RunRingPlanAsync(request, prepared => RingText.BuildAbandonPlan(prepared, RunName(run)), null, message => RunsMessage = message);
        if (result is { Outcome.Confirmed: true }) await AfterRingWriteAsync();
    }

    private static string RunName(RingRun run) => $"{run.PlanName}: {run.ObjectName} ({run.RunId})";
}
