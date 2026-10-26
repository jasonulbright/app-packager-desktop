using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Deployments;

public sealed class DeploymentPageReviewTests
{
    private readonly DeploymentPageHarness _h = new();

    private async Task<DeploymentFormViewModel> FormAsync(DeployableObject? obj = null, string collection = "LAB00010")
    {
        var page = await _h.OpenAsync();
        await DeploymentPageHarness.PickAsync(page.Deploy, obj ?? _h.SevenZip, collection);
        return page.Deploy;
    }

    private async Task<DeploymentTemplate> SaveTemplateAsync(DeploymentFormViewModel form, string name)
    {
        TextPromptViewModel? shown = null;
        _h.Dialogs.Prompt = p => shown = p;
        form.SaveAsTemplateCommand.Execute(null);
        shown!.Text = name;
        await shown.AcceptCommand.ExecuteAsync(null);
        Assert.True(shown.Accepted);
        return _h.Templates.Templates.Single(t => t.Id == DeploymentFormViewModel.TemplateIdFor(name));
    }

    // ---- Save as template and Apply template keep the fallback choice of the type ----------------

    [Theory]
    [InlineData(DeploymentObjectType.Application, false)]
    [InlineData(DeploymentObjectType.Application, true)]
    [InlineData(DeploymentObjectType.Package, false)]
    [InlineData(DeploymentObjectType.Package, true)]
    [InlineData(DeploymentObjectType.TaskSequence, false)]
    [InlineData(DeploymentObjectType.TaskSequence, true)]
    [InlineData(DeploymentObjectType.SoftwareUpdateGroup, false)]
    [InlineData(DeploymentObjectType.SoftwareUpdateGroup, true)]
    public async Task SaveAndApplyTemplateKeepTheFallbackChoiceOfEveryType(DeploymentObjectType type, bool fallback)
    {
        var page = await _h.OpenAsync();
        var form = page.Deploy;
        form.TypeIndex = (int)type;
        void Set(bool value)
        {
            if (type == DeploymentObjectType.SoftwareUpdateGroup) form.FallbackToDp = value;
            else form.AllowFallback = value;
        }
        bool Shown() => type == DeploymentObjectType.SoftwareUpdateGroup ? form.FallbackToDp : form.AllowFallback;

        Set(fallback);
        var saved = await SaveTemplateAsync(form, $"Round trip {type} {fallback}");
        Assert.Equal(fallback, saved.AllowBoundaryFallback);

        Set(!fallback);
        form.SelectedTemplate = form.Templates.Single(t => t.Template.Id == saved.Id);
        form.ApplyTemplateCommand.Execute(null);
        Assert.Equal(fallback, Shown());
    }

    // ---- History type filter ------------------------------------------------------------------------

    [Theory]
    [InlineData("Application", 3)]
    [InlineData("Package", 1)]
    [InlineData("TaskSequence", 2)]
    [InlineData("SoftwareUpdateGroup", 2)]
    public async Task TheHistoryTypeFilterMatchesEveryTypeAndTheRecordedSpellings(string filter, int expected)
    {
        foreach (var recorded in new[] { "Application", "App", "Apps", "Package", "TaskSequence", "Task Sequence", "SUG", "SoftwareUpdateGroup" })
            _h.Audit.Records.Add(_h.Record("Create", DeploymentObjectType.Application, recorded, "LAB00010", "") with { DeploymentType = recorded });
        var page = await _h.OpenAsync(DeploymentHelperViewModel.HistoryTab);
        page.History.TypeFilter = filter;
        Assert.Equal(expected, page.History.Rows.Count);
    }

    // ---- Validation results after a write and after a DP group check -----------------------------

    [Fact]
    public async Task AConfirmedDeployClearsTheChecksOfTheLastValidate()
    {
        var form = await FormAsync();
        await form.ValidateCommand.ExecuteAsync(null);
        Assert.Equal(5, form.Checks.Count);
        await form.DeployCommand.ExecuteAsync(null);
        Assert.Single(_h.Service.Deployments);
        Assert.Empty(form.Checks);
        Assert.Equal(DeploymentFormViewModel.WrittenText, form.ValidationMessage);
    }

    [Fact]
    public async Task AConfirmedDistributionClearsTheChecksOfTheLastValidate()
    {
        var form = await FormAsync();
        form.DpGroups[0].IsChecked = true;
        await form.ValidateCommand.ExecuteAsync(null);
        Assert.Equal(5, form.Checks.Count);
        await form.DistributeCommand.ExecuteAsync(null);
        Assert.Single(_h.Service.Applied);
        Assert.Empty(form.Checks);
    }

    [Fact]
    public async Task ADpGroupCheckChangeMakesTheChecksStale()
    {
        var form = await FormAsync();
        await form.ValidateCommand.ExecuteAsync(null);
        Assert.Equal(5, form.Checks.Count);
        form.DpGroups[1].IsChecked = true;
        Assert.Empty(form.Checks);
        Assert.Equal(DeploymentFormViewModel.ValidateAgain, form.ValidationMessage);
    }

    // ---- Stale messages -------------------------------------------------------------------------------

    [Fact]
    public async Task AFormChangeClearsTheMessagesOfTheLastAction()
    {
        var form = await FormAsync(collection: "SMS00001");
        await form.DeployCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentFormViewModel.BuiltInRefused, form.FormMessage);
        form.Notification = DeploymentText.Notifications[1];
        Assert.Equal("", form.FormMessage);

        await form.DistributeCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentFormViewModel.NoGroupsChecked, form.DpMessage);
        form.DpGroups[0].IsChecked = true;
        Assert.Equal("", form.DpMessage);
    }

    [Fact]
    public void TheCreateNowHintNamesBothChoices()
    {
        Assert.Contains("with the box checked, the deployment is created at once", DeploymentText.CreateNowHint);
        Assert.Contains("Without it, the deployment is created only when the content check passes", DeploymentText.CreateNowHint);
    }

    // ---- History cap ------------------------------------------------------------------------------------

    [Fact]
    public async Task AHistoryAtTheReadLimitSaysItShowsTheNewestRecordsAndTheExportSaysSoToo()
    {
        for (var i = 0; i < DeploymentHistoryViewModel.MaxRecords + 5; i++)
            _h.Audit.Records.Add(_h.Record("Create", DeploymentObjectType.Application, $"App {i}", "LAB00010", "", when: new DateTime(2026, 1, 1).AddMinutes(i)));
        var page = await _h.OpenAsync(DeploymentHelperViewModel.HistoryTab);
        var history = page.History;
        Assert.Equal(DeploymentHistoryViewModel.MaxRecords, history.Rows.Count);
        Assert.True(history.IsCapped);
        Assert.Equal(DeploymentHistoryViewModel.CappedNote, history.CapNote);
        await history.ExportCsvCommand.ExecuteAsync(null);
        Assert.EndsWith(DeploymentHistoryViewModel.CappedNote, page.Shared.StatusMessage);
    }

    [Fact]
    public async Task AHistoryBelowTheReadLimitHasNoCapNote()
    {
        _h.Audit.Records.Add(_h.Record("Create", DeploymentObjectType.Application, "7-Zip", "LAB00010", "{DEP-1}"));
        var page = await _h.OpenAsync(DeploymentHelperViewModel.HistoryTab);
        Assert.False(page.History.IsCapped);
        Assert.Equal("", page.History.CapNote);
        await page.History.ExportCsvCommand.ExecuteAsync(null);
        Assert.DoesNotContain(DeploymentHistoryViewModel.CappedNote, page.Shared.StatusMessage);
    }

    // ---- A failed save of the audit path ---------------------------------------------------------------

    [Fact]
    public async Task AnAuditPathThatCannotBeSavedSaysSoAndTheNextSaveTriesAgain()
    {
        _h.Saved = new DeploymentHelperSettings { AuditLogPath = @"E:\old.jsonl", SelectedTab = DeploymentHelperViewModel.StoredTab(DeploymentHelperViewModel.OptionsTab) };
        var page = await _h.OpenAsync(DeploymentHelperViewModel.OptionsTab);
        var probes = _h.Audit.Probes;
        _h.SaveSucceeds = false;
        page.Options.AuditLogPath = @"D:\Logs\deployment-audit.jsonl";
        await page.Options.SavePathCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentOptionsViewModel.PathNotSaved, page.Options.ProbeText);
        Assert.False(page.Options.ProbeWritable);
        Assert.Equal(probes, _h.Audit.Probes);
        Assert.Equal(@"E:\old.jsonl", _h.Saved!.AuditLogPath);

        _h.SaveSucceeds = true;
        await page.Options.SavePathCommand.ExecuteAsync(null);
        Assert.Equal(@"D:\Logs\deployment-audit.jsonl", _h.Saved!.AuditLogPath);
        Assert.True(page.Options.ProbeWritable);
    }

    [Fact]
    public async Task APageChoiceThatCannotBeSavedIsTriedAgainOnTheNextSave()
    {
        var page = await _h.OpenAsync();
        _h.SaveSucceeds = false;
        page.Deploy.TypeIndex = (int)DeploymentObjectType.Package;
        Assert.False(await page.SaveAsync());
        var saves = _h.PreferenceSaves;
        _h.SaveSucceeds = true;
        Assert.True(await page.SaveAsync());
        Assert.Equal(saves + 1, _h.PreferenceSaves);
        Assert.Equal((int)DeploymentObjectType.Package, _h.Saved!.ObjectType);
    }

    // ---- Reads end when the operator leaves the page ---------------------------------------------------

    [Fact]
    public async Task LeavingThePageCancelsTheValidateRead()
    {
        var page = await _h.OpenAsync();
        await DeploymentPageHarness.PickAsync(page.Deploy, _h.SevenZip, "LAB00010");
        _h.Service.PrepareGate = new TaskCompletionSource();
        var validate = page.Deploy.ValidateCommand.ExecuteAsync(null);
        await DeploymentPageHarness.WaitAsync(() => _h.Service.PrepareCalls == 1);
        await page.OnNavigatedFromAsync();
        await validate;
        Assert.True(_h.Service.PrepareTokens.Single().IsCancellationRequested);
        Assert.False(page.Deploy.IsValidating);
    }

    [Fact]
    public async Task LeavingThePageCancelsThePrepareOfADeploy()
    {
        var page = await _h.OpenAsync();
        await DeploymentPageHarness.PickAsync(page.Deploy, _h.SevenZip, "LAB00010");
        _h.Service.PrepareGate = new TaskCompletionSource();
        var deploy = page.Deploy.DeployCommand.ExecuteAsync(null);
        await DeploymentPageHarness.WaitAsync(() => _h.Service.PrepareCalls == 1);
        await page.OnNavigatedFromAsync();
        await deploy;
        Assert.True(_h.Service.PrepareTokens.Single().IsCancellationRequested);
        Assert.Empty(_h.Plans.Shown);
        Assert.False(page.Shared.IsWriting);
        Assert.DoesNotContain(_h.Log.Entries, e => e.Message.Contains("could not be read"));
    }

    [Fact]
    public async Task LeavingThePageCancelsTheDpGroupAndObjectReads()
    {
        var page = await _h.OpenAsync();
        var form = page.Deploy;
        _h.Service.GroupGate = new TaskCompletionSource();
        form.SelectedObject = form.Objects[0];
        await DeploymentPageHarness.WaitAsync(() => _h.Service.GroupTokens.Count == 1);
        _h.Service.ListGates[DeploymentObjectType.Package] = new TaskCompletionSource();
        form.TypeIndex = (int)DeploymentObjectType.Package;
        await DeploymentPageHarness.WaitAsync(() => _h.Service.ListTokens.Count == 2);
        await page.OnNavigatedFromAsync();
        Assert.True(_h.Service.GroupTokens.Single().IsCancellationRequested);
        Assert.True(_h.Service.ListTokens[1].IsCancellationRequested);
    }

    // ---- Refresh and Cancel on the Deploy and Options tabs ---------------------------------------------

    [Fact]
    public async Task CancelStopsAnObjectReadOfTheDeployTab()
    {
        var page = await _h.OpenAsync();
        var form = page.Deploy;
        _h.Service.ListGates[DeploymentObjectType.Package] = new TaskCompletionSource();
        form.TypeIndex = (int)DeploymentObjectType.Package;
        await DeploymentPageHarness.WaitAsync(() => _h.Service.ListTokens.Count == 2);
        Assert.True(form.CancelLoadCommand.CanExecute(null));
        form.CancelLoadCommand.Execute(null);
        await DeploymentPageHarness.WaitAsync(() => !form.IsLoading);
        Assert.Equal(DeploymentTabViewModel.ReadCancelled, page.Shared.StatusMessage);
        Assert.Empty(form.Objects);

        _h.Service.ListGates.Remove(DeploymentObjectType.Package);
        await form.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(["Office Tools"], form.Objects.Select(o => o.Name));
    }

    [Fact]
    public async Task RefreshOfTheOptionsTabProbesTheAuditLogAgain()
    {
        var page = await _h.OpenAsync(DeploymentHelperViewModel.OptionsTab);
        Assert.True(page.Options.ProbeWritable);
        _h.Audit.NextProbe = new AuditProbe(false, "Another program holds the file.");
        await page.Options.RefreshCommand.ExecuteAsync(null);
        Assert.False(page.Options.ProbeWritable);
    }

    // ---- The plan dialog Undo asks its own confirmation ------------------------------------------------

    [Fact]
    public async Task ThePlanDialogUndoAsksBeforeItRemovesTheDeploymentAndDoesNothingWhenDeclined()
    {
        var form = await FormAsync();
        _h.Plans.RunUndo = true;
        _h.Dialogs.ConfirmRemoval = false;
        await form.DeployCommand.ExecuteAsync(null);
        var prompt = Assert.Single(_h.Dialogs.RemovalPrompts);
        Assert.Equal("Remove the deployment 7-Zip from Pilot Workstations (LAB00010)? Software that clients already installed stays installed.", prompt);
        Assert.Single(_h.Service.Applied);
        Assert.Single(_h.Service.Deployments);
        Assert.True(form.Shared.HasUndo);
        Assert.Equal("Changed", Assert.Single(form.Shared.LastResults).OutcomeText);
        Assert.True(_h.Plans.LastViewModel!.UndoCommand.CanExecute(null));
        Assert.EndsWith(ChangePlanViewModel.UndoDeclinedText, _h.Plans.LastViewModel.ResultText);
    }

    [Fact]
    public async Task ThePlanDialogUndoRemovesTheDeploymentWhenTheOperatorConfirms()
    {
        var form = await FormAsync();
        _h.Plans.RunUndo = true;
        await form.DeployCommand.ExecuteAsync(null);
        Assert.Single(_h.Dialogs.RemovalPrompts);
        Assert.Equal(2, _h.Service.Applied.Count);
        Assert.Empty(_h.Service.Deployments);
        Assert.False(form.Shared.HasUndo);
    }

    [Fact]
    public async Task UndoLastChangeAndHistoryRemoveShowAPlanAndAskNoExtraQuestion()
    {
        var form = await FormAsync();
        await form.DeployCommand.ExecuteAsync(null);
        await form.Shared.UndoLastCommand.ExecuteAsync(null);
        Assert.True(_h.Plans.Shown[^1].IsDestructive);
        Assert.Empty(_h.Service.Deployments);
        Assert.Empty(_h.Dialogs.RemovalPrompts);
    }

    // ---- A run left working after a force close ---------------------------------------------------------

    /// <summary>Confirms the plan, asks the run to stop and closes the window while the run still works.</summary>
    private sealed class DetachingPlanDialogs(Func<bool> started) : IChangePlanDialogs
    {
        public ChangePlanOutcome Show(ChangePlan plan, ChangePlanRun run, ChangePlanRun? undo = null)
        {
            var dialog = new ChangePlanViewModel(plan, run, undo, TimeSpan.Zero) { DestructiveAcknowledged = true, ExtraAcknowledged = true };
            _ = dialog.ConfirmCommand.ExecuteAsync(null);
            Assert.True(SpinWait.SpinUntil(started, TimeSpan.FromSeconds(10)));
            dialog.RequestCancel();
            Assert.True(SpinWait.SpinUntil(() => dialog.ShowForceClose, TimeSpan.FromSeconds(10)));
            dialog.ForceCloseCommand.Execute(null);
            return dialog.Outcome;
        }
    }

    private DeploymentHelperViewModel DetachingPage()
    {
        _h.Service.InConfirmedRun = null;
        _h.Service.IgnoreCancel = true;
        _h.Service.ApplyGate = new TaskCompletionSource();
        _h.Service.Audited = outcome =>
        {
            if (outcome.Change.Kind == DeploymentChangeKind.Create)
                lock (_h.Audit.Records) _h.Audit.Records.Add(_h.Record("Create", DeploymentObjectType.Application, "7-Zip", "LAB00010", outcome.Change.CreatedId ?? ""));
        };
        var dialogs = new DetachingPlanDialogs(() => { lock (_h.Service.Applied) return _h.Service.Applied.Count > 0; });
        return new DeploymentHelperViewModel(_h.Services() with { Plans = dialogs });
    }

    [Fact]
    public async Task ARunThatEndsAfterTheOperatorLeftOffersItsUndoAndItsHistoryOnTheReturn()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var page = DetachingPage();
        await page.OnNavigatedToAsync();
        await DeploymentPageHarness.PickAsync(page.Deploy, _h.SevenZip, "LAB00010");
        await page.Deploy.DeployCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentShared.BackgroundRunText, page.Shared.StatusMessage);
        Assert.True(page.Shared.IsWriting);

        await page.OnNavigatedFromAsync();
        _h.Service.ApplyGate!.SetResult();
        await DeploymentPageHarness.WaitAsync(() => !page.Shared.IsWriting);

        await page.OnNavigatedToAsync();
        Assert.True(page.Shared.HasUndo);
        Assert.True(page.Shared.UndoLastCommand.CanExecute(null));
        page.SelectedTabIndex = DeploymentHelperViewModel.HistoryTab;
        await page.History.EnsureLoadedAsync();
        Assert.Single(page.History.Rows);
    }

    [Fact]
    public async Task ARunThatEndsWhileThePageIsOpenRefreshesTheHistory()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var page = DetachingPage();
        await page.OnNavigatedToAsync();
        page.SelectedTabIndex = DeploymentHelperViewModel.HistoryTab;
        await page.History.EnsureLoadedAsync();
        Assert.Empty(page.History.Rows);
        page.SelectedTabIndex = DeploymentHelperViewModel.DeployTab;
        await DeploymentPageHarness.PickAsync(page.Deploy, _h.SevenZip, "LAB00010");
        await page.Deploy.DeployCommand.ExecuteAsync(null);

        _h.Service.ApplyGate!.SetResult();
        await DeploymentPageHarness.WaitAsync(() => !page.Shared.IsWriting);
        await DeploymentPageHarness.WaitAsync(() => page.History.Rows.Count == 1);
        Assert.True(page.Shared.HasUndo);
        Assert.Equal(DeploymentShared.BackgroundRunEndedText, page.Shared.StatusMessage);
    }

    [Fact]
    public async Task TheUndoOfTheLastChangeStaysOfferedAfterTheOperatorLeavesAndReturns()
    {
        var page = await _h.OpenAsync();
        await DeploymentPageHarness.PickAsync(page.Deploy, _h.SevenZip, "LAB00010");
        await page.Deploy.DeployCommand.ExecuteAsync(null);
        await page.OnNavigatedFromAsync();
        await page.OnNavigatedToAsync();
        Assert.True(page.Shared.HasUndo);
        Assert.True(page.Shared.HasResultPanel);
        Assert.False(page.Shared.HasResults);
        await page.Shared.UndoLastCommand.ExecuteAsync(null);
        Assert.Empty(_h.Service.Deployments);
    }
}
