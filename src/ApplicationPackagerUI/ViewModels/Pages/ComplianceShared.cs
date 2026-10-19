using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>What the Compliance section reads and writes; its logic depends on nothing else.</summary>
public sealed record CompliancePageServices(
    IComplianceService Compliance,
    ICatalogService Catalog,
    ICollectionService Collections,
    IComplianceReportWriter Reports,
    IComplianceDialogs Dialogs,
    IChangePlanDialogs Plans,
    ComplianceWriter Writer,
    Func<AppSettings> LoadSettings,
    Func<ComplianceSettings?> LoadPreferences,
    Action<ComplianceSettings> SavePreferences,
    IAppLog Log);

/// <summary>
/// The state the four Compliance tabs share: the connection message, the status line, the
/// results and Undo of the last write, the device collections of the site and the plan runner.
/// <see cref="ComplianceWriter"/> is the only caller of the service write.
/// </summary>
public sealed partial class ComplianceShared(CompliancePageServices services) : ObservableObject
{
    public const string LogArea = ComplianceWriter.LogArea;
    public const string AccessHint = " The ConfigMgr console role needs Compliance Settings Manager rights.";
    public const string PrepareFailed = "The current state could not be read. The log has the details.";
    public const string CollectionsFailed = "The device collections could not be read. The log has the details.";
    public const string NotConfirmed = "Nothing was changed.";
    public const string NothingToUndo = "There is no change to undo.";
    public const string BackgroundRunText =
        "The plan window was closed while the run was still working. The run continues in the background and can still change ConfigMgr. Wait until it ends before you start another change.";
    public const string BackgroundRunEndedText = "The run that continued in the background has ended. Refresh the lists to see the result.";

    private bool _background;

    private readonly List<Func<Task>> _refreshers = [];
    private IReadOnlyList<CollectionRow>? _collections;
    private Task<IReadOnlyList<CollectionRow>?>? _collectionsLoad;

    public CompliancePageServices Services { get; } = services;

    /// <summary>Changes when the operator leaves the page; a result that belongs to an older value is discarded.</summary>
    public int Generation { get; private set; }

    public bool IsActive { get; set; }

    public ObservableCollection<ChangePlanItemResult> LastResults { get; } = [];

    [ObservableProperty] private string _statusMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionMessage))]
    private string _connectionMessage = "";

    [ObservableProperty] private string _resultSummary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUndo))]
    [NotifyCanExecuteChangedFor(nameof(UndoLastCommand))]
    private CompliancePlan? _lastPlan;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoLastCommand))]
    private bool _isWriting;

    public bool HasConnectionMessage => ConnectionMessage.Length > 0;
    public bool HasResults => LastResults.Count > 0;
    public bool HasUndo => LastPlan is not null;

    /// <summary>Registers the refresh of a tab; it runs after each write.</summary>
    public void OnAfterWrite(Func<Task> refresh) => _refreshers.Add(refresh);

    /// <summary>The setting to change before the section can reach ConfigMgr, or null.</summary>
    public string? ConnectionProblem()
    {
        var problem = CollectionComplianceViewModel.ConnectionProblem(Services.LoadSettings());
        ConnectionMessage = problem ?? (ConnectionMessage == CollectionComplianceViewModel.NotInUseMessage || ConnectionMessage == CollectionComplianceViewModel.SiteCodeMessage ? "" : ConnectionMessage);
        return problem;
    }

    /// <summary>A message for the operator: the service's own plain text with the setting to check, or the fallback.</summary>
    public static string PlainMessage(Exception ex, string fallback) => ex switch
    {
        ComplianceServiceException c => SecretMask.Apply(c.Message),
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
            ComplianceServiceException c => $"{nameof(ComplianceServiceException)} {c.Kind}",
            _ => ex.GetType().Name
        };

    /// <summary>
    /// The device collections of the site, read once a visit; null when they cannot be read, with
    /// the reason in the status line.
    /// </summary>
    public async Task<IReadOnlyList<CollectionRow>?> DeviceCollectionsAsync()
    {
        if (_collections is { } known) return known;
        if (ConnectionProblem() is not null) return null;
        var generation = Generation;
        _collectionsLoad ??= Task.Run(async () =>
        {
            try { return (IReadOnlyList<CollectionRow>?)await Services.Collections.ListCollectionsAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                Services.Log.Warning(LogArea, $"The device collections could not be read ({FailureName(ex)}).");
                throw;
            }
        });
        var load = _collectionsLoad;
        try
        {
            var rows = await load;
            if (generation != Generation) return null;
            _collections = rows;
            return rows;
        }
        catch (Exception ex)
        {
            if (generation != Generation) return null;
            if (ReferenceEquals(_collectionsLoad, load)) _collectionsLoad = null;
            StatusMessage = PlainMessage(ex, CollectionsFailed);
            return null;
        }
    }

    /// <summary>Drops the device collections so the next use reads them again.</summary>
    public void ForgetCollections()
    {
        _collections = null;
        _collectionsLoad = null;
    }

    /// <summary>Releases the shared data; a result that arrives later is discarded.</summary>
    public void Release()
    {
        Generation++;
        IsActive = false;
        ForgetCollections();
        StatusMessage = "";
        ConnectionMessage = "";
        LastPlan = null;
        LastResults.Clear();
        OnPropertyChanged(nameof(HasResults));
        ResultSummary = "";
    }

    /// <summary>
    /// Reads the current state of each request, builds the plan from it and shows the plan. The
    /// writer applies only the changes of the plan the operator confirms. Null when no plan was
    /// shown; the reason is in <see cref="StatusMessage"/> and passed to <paramref name="report"/>.
    /// With more than one request, a request whose state cannot be read becomes a Skip line.
    /// <paramref name="checkState"/> false sends the requests as they are: the service checks the
    /// state of each change again when it writes it.
    /// </summary>
    public async Task<(CompliancePlan Plan, ChangePlanOutcome Outcome)?> RunPlanAsync(
        IReadOnlyList<ComplianceChange> requests,
        Func<IReadOnlyList<ComplianceChange>, (CompliancePlan? Plan, string? Problem)> build,
        Func<ComplianceChange, string> describe,
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
            var prepared = new List<ComplianceChange>(requests.Count);
            var skips = new List<CompliancePlanEntry>();
            if (!checkState) prepared.AddRange(requests);
            else
            {
                var checks = await Task.Run(async () =>
                {
                    var list = new List<(ComplianceChange Request, ComplianceChange? Prepared, Exception? Failure)>(requests.Count);
                    foreach (var request in requests)
                    {
                        try { list.Add((request, await Services.Compliance.PrepareAsync(request, CancellationToken.None), null)); }
                        catch (Exception ex) { list.Add((request, null, ex)); }
                    }
                    return list;
                });
                var failed = new List<CompliancePlanEntry>();
                foreach (var (request, ready, failure) in checks)
                {
                    if (failure is null)
                    {
                        prepared.Add(ready!);
                        continue;
                    }
                    Services.Log.Warning(LogArea, $"The state before a write could not be read ({FailureName(failure)}).");
                    var reason = PlainMessage(failure, PrepareFailed);
                    failed.Add(CompliancePlanEntry.Skip(new ChangePlanItem("Skip", describe(request), reason), reason));
                }
                if (failed.Count > 0 && (requests.Count == 1 || prepared.Count == 0))
                {
                    if (generation == Generation) Refuse(failed[0].SkipReason!);
                    return null;
                }
                skips.AddRange(failed);
            }
            if (generation != Generation) return null;

            var (built, refusal) = build(prepared);
            if (built is null)
            {
                if (refusal is not null) Refuse(refusal);
                return null;
            }
            var plan = skips.Count == 0 ? built
                : new CompliancePlan(built.Action, built.Plan.Title, built.Plan.ActionName, [.. skips, .. built.Entries], built.Plan.IsDestructive,
                    built.Plan.HasUndo, built.Plan.ExtraConfirmation) { IsUndo = built.IsUndo };

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

    /// <summary>
    /// Keeps every write blocked until the run that a closed plan window left working ends. No
    /// list is read meanwhile; the Undo of the run is offered when it ends.
    /// </summary>
    private async Task WaitForBackgroundRunAsync(CompliancePlan plan, bool offerUndo)
    {
        _background = true;
        try { await Services.Writer.Running; }
        catch (Exception ex)
        {
            Services.Log.Warning(LogArea, $"A run that continued in the background failed ({FailureName(ex)}).");
        }
        finally
        {
            if (IsActive)
            {
                if (offerUndo && plan.UndoChanges().Count > 0) LastPlan = plan;
                StatusMessage = BackgroundRunEndedText;
            }
            _background = false;
            IsWriting = false;
        }
    }

    private void ShowOutcome(CompliancePlan plan, ChangePlanOutcome outcome)
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
        OnPropertyChanged(nameof(HasResults));
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
        ForgetCollections();
        foreach (var refresh in _refreshers.ToList())
        {
            if (!IsActive) return;
            await refresh();
        }
        if (IsActive && StatusMessage.Length == 0) StatusMessage = status;
    }

    private bool CanUndoLast() => !IsWriting && LastPlan is not null;

    /// <summary>Undoes the last write set through a new plan; undo changes that did not succeed stay offered.</summary>
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
            var entries = prepared.Select((change, i) => CompliancePlanEntry.Write(new ChangePlanItem("Undo " + undo[i].Entry.Item.Action,
                undo[i].Entry.Item.Target, UndoDetail(undo[i].Entry.Change, change)), change)).ToList();
            var destructive = prepared.Any(c => c.Kind is ComplianceChangeKind.DeleteDeployment or ComplianceChangeKind.RemoveSetting or ComplianceChangeKind.RemoveCatalogObjects);
            return (new CompliancePlan("Undo " + last.Action, "Undo: " + last.Plan.Title, $"Undo {ComplianceText.Count(entries.Count, "change")}", entries, destructive,
                extraConfirmation: ComplianceLiveViewModel.RemediationConfirmation(prepared)) { IsUndo = true }, null);
        }, change => change.Kind.ToString(), checkState: false);
        if (run is not { Outcome.Confirmed: true } done) return;
        LastPlan = done.Outcome.UndoResult is null ? last.Remaining(done.Plan.Outcomes) : null;
        await AfterWriteAsync();
    }

    /// <summary>What the undo of <paramref name="original"/> does, in plain words.</summary>
    public static string UndoDetail(ComplianceChange? original, ComplianceChange undo) =>
        undo.Impact.Count > 0 ? string.Join(" ", undo.Impact) : undo.Kind switch
        {
            ComplianceChangeKind.RemoveCatalogObjects => $"Removes the deployments, baselines and configuration items the run created ({ComplianceText.Count(undo.CreatedIds.Count, "object")}). An object that existed before the run stays.",
            ComplianceChangeKind.SetRemediation => $"Sets the mode back to {ComplianceText.Mode(undo.RemediateAfter == true)}.",
            ComplianceChangeKind.CreateDeployment => $"Creates the deployment again from the captured values. {ComplianceWriter.RecreatedNote}",
            ComplianceChangeKind.DeleteDeployment => "Removes the deployment that the change made.",
            ComplianceChangeKind.Reassign => "Moves the deployment back to its earlier collection. The deployments have new IDs.",
            ComplianceChangeKind.RemoveSetting => $"Removes the setting {undo.SettingBefore?.Name ?? original?.SettingAfter?.Name} that the change added.",
            ComplianceChangeKind.EditSetting => "Sets the old values of the setting back.",
            ComplianceChangeKind.AddSetting => $"Adds the setting {undo.SettingAfter?.Name ?? original?.SettingBefore?.Name} again from the captured values. {ComplianceWriter.RecreatedNote}",
            _ => "Reverses the change."
        };
}
