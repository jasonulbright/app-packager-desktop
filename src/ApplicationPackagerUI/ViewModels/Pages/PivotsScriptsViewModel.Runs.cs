using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

public sealed partial class PivotsScriptsViewModel
{
    public const string NotConfirmed = "Nothing was sent.";
    public const string WriteRunning = "An action is still running. Wait until it ends, then start the next one.";
    public const string WaitRunning = "The page still waits for the results of the last run. Select Stop waiting or wait until it ends, then start the next run.";
    public const string PrepareFailed = "The script and the targets could not be read again. Nothing was sent. The log has the details.";
    public const string FormChanged = "The form changed while the script and the targets were read again. Nothing was sent. Start the action again.";
    public const string QueryEmpty = "Type a CMPivot query first.";
    public const string NoTargets = "The targets resolve to no device. Nothing can be sent.";
    public const string BackgroundRunText =
        "The plan window was closed while the action was still working. It continues in the background and can still reach the site and the clients. Wait until it ends before you start another action.";
    public const string BackgroundRunEndedText = "The action that continued in the background has ended.";
    public const string AlreadyApproved = "The script is already approved.";
    public const string AlreadyDeclined = "The script is already declined.";

    private bool _background;
    private PivotsChange? _cleanup;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunPivotCommand), nameof(RunScriptCommand), nameof(SubmitDraftCommand), nameof(ApproveScriptCommand), nameof(DenyScriptCommand),
        nameof(RemoveScriptCommand), nameof(RemoveLeftoverCommand), nameof(CheckDraftCommand), nameof(WaitAgainCommand))]
    private bool _isWriting;

    private bool CanWrite() => !IsWriting;

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private Task RunPivot()
    {
        if (Busy() is { } busy) return Say(busy);
        if (QueryText.Trim().Length == 0) return Say(QueryEmpty);
        if (CurrentTargets() is not { } targets) return Say(ResolveFirst);
        if (targets.Devices.Count == 0) return Say(NoTargets);
        var query = QueryText;
        string Form() => $"pivot|{TargetKey()}|{QueryText}";
        var before = Form();
        var request = new PivotsChange(Kind: PivotsChangeKind.RunPivot, Targets: targets, QueryText: query, ScriptGuid: null, ScriptHash: null, Parameters: null,
            SubmitName: null, SubmitText: null, Comment: null, RunConfirmed: false, EveryDeviceAcknowledged: false, Impact: [], Checks: [], Fingerprint: null) { ResultWindow = PivotsChange.SendOnly };
        return PlanAndRunAsync(request, () => Form() == before);
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task RunScript()
    {
        if (Busy() is { } busy)
        {
            await Say(busy);
            return;
        }
        if (SelectedApproved is not { } row)
        {
            await Say(SelectApproved);
            return;
        }
        if (await SelectedScriptAsync(row) is not { } script) return;
        await RunScriptAsync(script);
    }

    private Task RunScriptAsync(SiteScript script)
    {
        if (script.ParametersUnreadable) return Say(ParametersUnreadable);
        if (CurrentTargets() is not { } targets) return Say(ResolveFirst);
        if (targets.Devices.Count == 0) return Say(NoTargets);
        var (values, problem) = RunParameters(script);
        if (problem is not null) return Say(problem);
        string Form() => $"script|{TargetKey()}|{SelectedApproved?.Key}|{SelectedApproved?.Full?.Hash}|{_parametersFor}|{ParametersText}";
        var before = Form();
        var request = new PivotsChange(Kind: PivotsChangeKind.RunScript, Targets: targets, QueryText: null, ScriptGuid: script.Guid,
            ScriptHash: script.Hash.Length > 0 ? script.Hash : null, Parameters: values, SubmitName: null, SubmitText: null, Comment: null, RunConfirmed: false,
            EveryDeviceAcknowledged: false, Impact: [], Checks: [], Fingerprint: null) { ResultWindow = PivotsChange.SendOnly };
        return PlanAndRunAsync(request, () => Form() == before);
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private Task SubmitDraft()
    {
        if (Busy() is { } busy) return Say(busy);
        var name = DraftName.Trim();
        if (name.Length == 0) return Say(DraftNeedsName);
        if (name.Length > MaxScriptNameLength) return Say($"The script name has more than {MaxScriptNameLength} characters.");
        if (DraftText.Trim().Length == 0) return Say(DraftNeedsText);
        var text = DraftText;
        string Form() => $"submit|{DraftName.Trim()}|{DraftText}";
        var before = Form();
        var request = new PivotsChange(Kind: PivotsChangeKind.SubmitScript, Targets: null, QueryText: null, ScriptGuid: null, ScriptHash: null, Parameters: null,
            SubmitName: name, SubmitText: text, Comment: null, RunConfirmed: false, EveryDeviceAcknowledged: false, Impact: [], Checks: [], Fingerprint: null);
        return PlanAndRunAsync(request, () => Form() == before);
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private Task ApproveScript() => ScriptAction(PivotsChangeKind.ApproveScript);

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private Task DenyScript() => ScriptAction(PivotsChangeKind.DenyScript);

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private Task RemoveScript() => ScriptAction(PivotsChangeKind.RemoveScript);

    private Task ScriptAction(PivotsChangeKind kind)
    {
        if (Busy() is { } busy) return Say(busy);
        if (SelectedSiteScript is not { } row) return Say(SelectSiteScript);
        if (kind == PivotsChangeKind.ApproveScript && row.IsApproved) return Say(AlreadyApproved);
        if (kind == PivotsChangeKind.DenyScript && string.Equals(row.Script.State, SiteScriptStates.Declined, StringComparison.OrdinalIgnoreCase)) return Say(AlreadyDeclined);
        string? comment = null;
        if (kind == PivotsChangeKind.DenyScript)
        {
            comment = Services.Dialogs.AskComment($"Deny {row.Name}", "Type the reason for the denial. The site stores it with the script.");
            if (comment is null) return Say(NotConfirmed);
        }
        var key = row.Key;
        var request = new PivotsChange(Kind: kind, Targets: null, QueryText: null, ScriptGuid: row.Script.Guid, ScriptHash: row.Script.Hash.Length > 0 ? row.Script.Hash : null, Parameters: null,
            SubmitName: null, SubmitText: null, Comment: comment, RunConfirmed: false, EveryDeviceAcknowledged: false, Impact: [], Checks: [], Fingerprint: null);
        return PlanAndRunAsync(request, () => SelectedSiteScript?.Key == key);
    }

    private bool CanRemoveLeftover() => !IsWriting && LeftoverGuid is not null;

    /// <summary>Shows the plan that removes only the script this page created in the failed submit, named by the GUID the create call returned.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveLeftover))]
    private Task RemoveLeftover()
    {
        if (Busy() is { } busy) return Say(busy);
        if (LeftoverGuid is not { } guid) return Task.CompletedTask;
        // The service names the clean-up as a change of its own; only the GUID the create call returned may be removed.
        var request = _cleanup is { Kind: PivotsChangeKind.RemoveScript } cleanup && string.Equals(cleanup.ScriptGuid, guid, StringComparison.OrdinalIgnoreCase)
            ? cleanup with { RunConfirmed = false, EveryDeviceAcknowledged = false, Fingerprint = null }
            : new PivotsChange(Kind: PivotsChangeKind.RemoveScript, Targets: null, QueryText: null, ScriptGuid: guid, ScriptHash: null, Parameters: null,
                SubmitName: null, SubmitText: null, Comment: null, RunConfirmed: false, EveryDeviceAcknowledged: false, Impact: [], Checks: [], Fingerprint: null);
        return PlanAndRunAsync(request, () => LeftoverGuid == guid);
    }

    partial void OnLeftoverGuidChanged(string? value) => RemoveLeftoverCommand.NotifyCanExecuteChanged();

    private string? Busy() =>
        IsWriting ? _background ? BackgroundRunText : WriteRunning
        : IsWaiting ? WaitRunning
        : ConnectionProblem();

    private Task Say(string message)
    {
        StatusMessage = message;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads the script and the targets again, shows the plan made from the prepared change and sends
    /// it when the operator confirms. The form was read once before the read; a plan whose form changed
    /// during the read is discarded.
    /// </summary>
    private async Task PlanAndRunAsync(PivotsChange request, Func<bool> sameForm)
    {
        var generation = Generation;
        var detached = false;
        PivotsPlan? ran = null;
        IsWriting = true;
        try
        {
            StatusMessage = "Reading the script and the targets again.";
            var visit = VisitToken;
            PivotsChange prepared;
            try { prepared = await Task.Run(() => Services.Service.PrepareAsync(request, visit), visit); }
            catch (OperationCanceledException) when (visit.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Services.Log.Warning(LogArea, $"The state before an action could not be read ({FailureName(ex)}).");
                if (generation == Generation) StatusMessage = PlainMessage(ex, PrepareFailed);
                return;
            }
            if (generation != Generation) return;
            if (!sameForm())
            {
                StatusMessage = FormChanged;
                return;
            }
            var (plan, refusal) = PivotsScriptsText.BuildPlan(prepared);
            if (plan is null)
            {
                StatusMessage = refusal ?? NotConfirmed;
                return;
            }
            StatusMessage = "";
            var outcome = Services.Plans.Show(plan.Plan, Services.Writer.RunFor(plan));
            if (outcome.Result?.Stop == ChangePlanStop.Detached)
            {
                detached = true;
                _ = WaitForBackgroundRunAsync(plan);
                if (generation == Generation) StatusMessage = BackgroundRunText;
                return;
            }
            if (!outcome.Confirmed || outcome.Result is not { } result)
            {
                StatusMessage = NotConfirmed;
                return;
            }
            ran = plan;
            StatusMessage = $"{plan.Plan.Title}: {result.Summary}";
        }
        finally
        {
            if (!detached) IsWriting = false;
        }
        if (ran is not null) await AfterSendAsync(ran);
    }

    /// <summary>Keeps every action blocked until the run that a closed plan window left working ends, then goes on as after a send.</summary>
    private async Task WaitForBackgroundRunAsync(PivotsPlan plan)
    {
        _background = true;
        try { await Services.Writer.Running; }
        catch (Exception ex)
        {
            Services.Log.Warning(LogArea, $"An action that continued in the background failed ({FailureName(ex)}).");
        }
        finally
        {
            _background = false;
            IsWriting = false;
            if (_active) StatusMessage = BackgroundRunEndedText;
        }
        await AfterSendAsync(plan);
    }

    /// <summary>A run that the site accepted starts the wait for its results; a script action reads the scripts again.</summary>
    private async Task AfterSendAsync(PivotsPlan plan)
    {
        if (plan.Outcome is not { } outcome) return;
        if (plan.Kind is PivotsChangeKind.RunPivot or PivotsChangeKind.RunScript)
        {
            var header = $"{PivotsLabels.Kind(plan.Kind)}: {SecretMask.Apply(outcome.Message)}";
            if (outcome.OperationId is { } id && outcome.Sent && outcome.Devices.Any(d => !d.IsFinal)) StartWait(plan, outcome, id);
            else if (outcome.Sent || outcome.Devices.Count > 0) await ShowAsync(outcome.Devices, outcome.Partial, header, plan.Change.Targets, outcome.SessionId, final: true);
            // A refusal sent nothing: the results and the headline of the last run stay.
            else StatusMessage = header;
        }
        else
        {
            if (plan.Kind == PivotsChangeKind.SubmitScript)
            {
                // A later submit that leaves nothing behind keeps the clean-up of an older incomplete script.
                if (!outcome.Succeeded && outcome.CreatedScriptGuid is { Length: > 0 } created)
                {
                    _cleanup = outcome.CleanupChange;
                    LeftoverGuid = created;
                    StatusMessage = $"The site created the script {plan.Change.SubmitName} ({created}), but a later step failed: {SecretMask.Apply(outcome.Message)} "
                        + "The script exists on the site and is not complete. Select Remove created script to remove only this script.";
                }
            }
            else if (plan.Kind == PivotsChangeKind.RemoveScript && outcome.Succeeded && string.Equals(plan.Change.ScriptGuid, LeftoverGuid, StringComparison.OrdinalIgnoreCase))
            {
                _cleanup = null;
                LeftoverGuid = null;
            }
            if (_active) await LoadScriptsAsync();
        }
        if (_active && IsHistoryLoaded) await LoadHistoryAsync();
    }
}
