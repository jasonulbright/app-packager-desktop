using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>What the Deployment Helper page reads and writes; its logic depends on nothing else.</summary>
public sealed record DeploymentPageServices(
    IDeploymentService Deployments,
    ICollectionService Collections,
    IDeploymentAuditLog AuditLog,
    IDeploymentTemplateStore Templates,
    IDeploymentReportWriter Reports,
    IDeploymentHelperDialogs Dialogs,
    IChangePlanDialogs Plans,
    DeploymentWriter Writer,
    Func<AppSettings> LoadSettings,
    Func<DeploymentHelperSettings?> LoadPreferences,
    Func<DeploymentHelperSettings, bool> SavePreferences,
    Func<Task<string?>> StartRingTool,
    IAppLog Log,
    TimeProvider Clock);

/// <summary>
/// The state the three Deployment Helper tabs share: the connection message, the status line, the
/// results and Undo of the last write and the plan runner. <see cref="DeploymentWriter"/> is the
/// only caller of the service write.
/// </summary>
public sealed partial class DeploymentShared(DeploymentPageServices services) : ObservableObject
{
    public const string LogArea = DeploymentWriter.LogArea;
    public const string AccessHint = " The ConfigMgr console role needs deployment rights for the object type and distribute content rights.";
    public const string PrepareFailed = "The current state could not be read. The log has the details.";
    public const string NotConfirmed = "Nothing was changed.";
    public const string NothingToUndo = "There is no change to undo.";
    public const string BackgroundRunText =
        "The plan window was closed while the run was still working. The run continues in the background and can still change ConfigMgr. Wait until it ends before you start another change.";
    public const string BackgroundRunEndedText = "The run that continued in the background has ended. Refresh the lists to see the result.";

    private bool _background;
    private readonly List<Func<Task>> _refreshers = [];
    private CancellationTokenSource _visit = new();

    public DeploymentPageServices Services { get; } = services;

    /// <summary>Changes when the operator leaves the page; a result that belongs to an older value is discarded.</summary>
    public int Generation { get; private set; }

    /// <summary>Cancelled when the operator leaves the page; every read of the visit takes it.</summary>
    public CancellationToken VisitToken => _visit.Token;

    public bool IsActive { get; set; }

    public ObservableCollection<ChangePlanItemResult> LastResults { get; } = [];

    [ObservableProperty] private string _statusMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionMessage))]
    private string _connectionMessage = "";

    [ObservableProperty] private string _resultSummary = "";

    [ObservableProperty] private bool _showResultRows = true;

    /// <summary>The last confirmed plan with its outcomes; it outlives the visit, so its Undo stays offered until a new run replaces it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUndo), nameof(HasResultPanel))]
    [NotifyCanExecuteChangedFor(nameof(UndoLastCommand))]
    private DeploymentPlan? _lastPlan;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoLastCommand))]
    private bool _isWriting;

    public bool HasConnectionMessage => ConnectionMessage.Length > 0;
    public bool HasResults => LastResults.Count > 0;
    public bool HasUndo => LastPlan is not null;
    public bool HasResultPanel => HasResults || HasUndo;

    /// <summary>Registers the refresh of a tab; it runs after each write.</summary>
    public void OnAfterWrite(Func<Task> refresh) => _refreshers.Add(refresh);

    /// <summary>The setting to change before the page can reach ConfigMgr, or null.</summary>
    public string? ConnectionProblem()
    {
        var problem = CollectionComplianceViewModel.ConnectionProblem(Services.LoadSettings());
        ConnectionMessage = problem ?? "";
        return problem;
    }

    /// <summary>A message for the operator: the service's own plain text with the setting to check, or the fallback.</summary>
    public static string PlainMessage(Exception ex, string fallback) => ex switch
    {
        DeploymentServiceException d => SecretMask.Apply(d.Message),
        CollectionServiceException c => SecretMask.Apply(c.Message),
        ConfigMgrSessionException { Failure: ConfigMgrFailure.InvalidSiteCode or ConfigMgrFailure.DriveNotConnected or ConfigMgrFailure.ModuleNotFound } s
            => SecretMask.Apply(s.Message) + CollectionComplianceViewModel.SiteHint,
        ConfigMgrSessionException { Failure: ConfigMgrFailure.AccessDenied } s => SecretMask.Apply(s.Message) + AccessHint,
        ConfigMgrSessionException { Failure: ConfigMgrFailure.Unexpected } => fallback,
        ConfigMgrSessionException s => SecretMask.Apply(s.Message),
        _ => fallback
    };

    public static string FailureName(Exception ex) =>
        ex switch
        {
            ConfigMgrSessionException s => $"{nameof(ConfigMgrSessionException)} {s.Failure}",
            _ => ex.GetType().Name
        };

    /// <summary>
    /// Releases the data of the visit and cancels its reads; a result that arrives later is
    /// discarded. The last plan stays, so its Undo is offered on the next visit.
    /// </summary>
    public void Release()
    {
        Generation++;
        IsActive = false;
        var visit = _visit;
        _visit = new CancellationTokenSource();
        try { visit.Cancel(); } catch (AggregateException ex) { Services.Log.Warning(LogArea, $"A read did not stop cleanly ({ex.InnerException?.GetType().Name})."); }
        StatusMessage = "";
        ConnectionMessage = "";
        LastResults.Clear();
        NotifyResults();
        ResultSummary = LastPlan is { } last ? LastChangeText(last) : "";
    }

    /// <summary>The summary of a plan whose result rows were released.</summary>
    public static string LastChangeText(DeploymentPlan plan) => $"{plan.Plan.Title}: Undo last change can remove what it created.";

    private void NotifyResults()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasResultPanel));
    }

    /// <summary>
    /// Reads the current state of each request, builds the plan from the prepared changes and shows
    /// it. The writer applies only the changes of the plan the operator confirms. Null when no plan
    /// was shown; the reason is in <see cref="StatusMessage"/> and passed to <paramref name="report"/>.
    /// A request whose state cannot be read stops the plan.
    /// </summary>
    public async Task<(DeploymentPlan Plan, ChangePlanOutcome Outcome)?> RunPlanAsync(
        IReadOnlyList<DeploymentChange> requests,
        Func<IReadOnlyList<DeploymentChange>, (DeploymentPlan? Plan, string? Problem)> build,
        Action<IReadOnlyList<DeploymentChange>>? prepared = null,
        Action<string>? report = null,
        bool checkState = true)
    {
        if (IsWriting)
        {
            if (_background)
            {
                StatusMessage = BackgroundRunText;
                report?.Invoke(BackgroundRunText);
            }
            return null;
        }
        IsWriting = true;
        var detached = false;
        try
        {
            var generation = Generation;
            void Refuse(string message)
            {
                StatusMessage = message;
                report?.Invoke(message);
            }

            if (ConnectionProblem() is { } problem)
            {
                Refuse(problem);
                return null;
            }
            IReadOnlyList<DeploymentChange> ready;
            if (!checkState) ready = requests;
            else
            {
                StatusMessage = "Reading the current state.";
                var visit = VisitToken;
                try
                {
                    ready = await Task.Run(async () =>
                    {
                        var list = new List<DeploymentChange>(requests.Count);
                        foreach (var request in requests) list.Add(await Services.Deployments.PrepareAsync(request, visit));
                        return (IReadOnlyList<DeploymentChange>)list;
                    }, visit);
                }
                catch (OperationCanceledException) when (visit.IsCancellationRequested)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    Services.Log.Warning(LogArea, $"The state before a write could not be read ({FailureName(ex)}).");
                    if (generation == Generation) Refuse(PlainMessage(ex, PrepareFailed));
                    return null;
                }
                if (generation != Generation) return null;
                StatusMessage = "";
                prepared?.Invoke(ready);
            }

            var (plan, refusal) = build(ready);
            if (plan is null)
            {
                if (refusal is not null) Refuse(refusal);
                return null;
            }

            var outcome = Services.Plans.Show(plan.Plan, Services.Writer.RunFor(plan), plan.Plan.HasUndo ? Services.Writer.UndoFor(plan) : null);
            if (outcome.Result?.Stop == ChangePlanStop.Detached || outcome.UndoResult?.Stop == ChangePlanStop.Detached)
            {
                detached = true;
                _ = WaitForBackgroundRunAsync(plan, outcome.UndoResult is null);
                if (generation == Generation) Refuse(BackgroundRunText);
                return null;
            }
            if (generation != Generation) return null;
            ShowOutcome(plan, outcome);
            return (plan, outcome);
        }
        finally
        {
            if (!detached) IsWriting = false;
        }
    }

    /// <summary>Keeps every write blocked until the run that a closed plan window left working ends.</summary>
    private async Task WaitForBackgroundRunAsync(DeploymentPlan plan, bool offerUndo)
    {
        _background = true;
        try { await Services.Writer.Running; }
        catch (Exception ex)
        {
            Services.Log.Warning(LogArea, $"A run that continued in the background failed ({FailureName(ex)}).");
        }
        finally
        {
            if (offerUndo && plan.UndoChanges().Count > 0) LastPlan = plan;
            if (IsActive) StatusMessage = BackgroundRunEndedText;
            else if (LastPlan is { } last && !HasResults) ResultSummary = LastChangeText(last);
            _background = false;
            IsWriting = false;
        }
        await AfterWriteAsync();
    }

    private void ShowOutcome(DeploymentPlan plan, ChangePlanOutcome outcome)
    {
        if (!outcome.Confirmed || outcome.Result is not { } result)
        {
            StatusMessage = NotConfirmed;
            return;
        }
        LastResults.Clear();
        foreach (var row in result.Items) LastResults.Add(row);
        var summary = result.Summary;
        if (outcome.UndoResult is { } undo)
        {
            foreach (var row in undo.Items) LastResults.Add(row with { Message = "Undo: " + row.Message });
            summary += " Undo: " + undo.Summary;
        }
        NotifyResults();
        ResultSummary = $"{plan.Plan.Title}: {summary}";
        StatusMessage = ResultSummary;
        LastPlan = outcome.UndoResult is null
            ? plan.UndoChanges().Count > 0 ? plan : null
            : plan.Remaining(plan.UndoOutcomes);
    }

    /// <summary>Reads the data of every tab that has data again; each tab waits for its own running load first.</summary>
    public async Task AfterWriteAsync()
    {
        if (!IsActive) return;
        var status = StatusMessage;
        foreach (var refresh in _refreshers.ToList())
        {
            if (!IsActive) return;
            await refresh();
        }
        if (IsActive && StatusMessage.Length == 0) StatusMessage = status;
    }

    private bool CanUndoLast() => !IsWriting && LastPlan is not null;

    /// <summary>
    /// Removes the deployments of the last write through a new plan made from the prepared removals;
    /// a removal with a failed or unknown check is skipped with its reasons, and undo changes that
    /// did not succeed stay offered.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUndoLast))]
    private async Task UndoLast()
    {
        if (LastPlan is not { } last) return;
        var undo = last.UndoChanges();
        if (undo.Count == 0)
        {
            StatusMessage = NothingToUndo;
            LastPlan = null;
            return;
        }
        var run = await RunPlanAsync(undo.Select(u => u.Undo).ToList(), prepared =>
        {
            var entries = prepared.Select((change, i) =>
            {
                var (entry, _) = undo[i];
                var blocked = DeploymentText.Blocking(change).Count > 0;
                var lines = DeploymentText.CheckLines(change.Checks, change.Existing?.Type).Concat(change.Impact)
                    .Append(DeploymentText.UndoNote.Replace("Undo removes", "Removes", StringComparison.Ordinal));
                var item = new ChangePlanItem("Remove deployment", entry.Item.Target, string.Join(Environment.NewLine, lines));
                return blocked
                    ? DeploymentPlanEntry.Skip(item, $"{DeploymentWriter.SkippedPrefix}: {DeploymentText.BlockingText(change)}")
                    : DeploymentPlanEntry.Write(item, change);
            }).ToList();
            if (entries.All(e => e.Change is null))
                return (null, "The deployment cannot be removed: " + string.Join(" ", prepared.Select(DeploymentText.BlockingText)));
            return (new DeploymentPlan("Undo " + last.Action, "Undo: " + last.Plan.Title, $"Remove {DeploymentText.Count(entries.Count(e => e.Change is not null), "deployment")}", entries,
                isDestructive: true, hasUndo: false) { IsUndo = true }, null);
        });
        if (run is not { Outcome.Confirmed: true } done) return;
        LastPlan = last.Remaining(done.Plan.Outcomes);
        await AfterWriteAsync();
    }
}
