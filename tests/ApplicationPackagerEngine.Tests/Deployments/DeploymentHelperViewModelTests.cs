using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Deployments;

public sealed class DeploymentHelperViewModelTests
{
    private readonly DeploymentPageHarness _h = new();

    private async Task<DeploymentFormViewModel> FormAsync(DeployableObject? obj = null, string collection = "LAB00010")
    {
        var page = await _h.OpenAsync();
        var form = page.Deploy;
        await DeploymentPageHarness.PickAsync(form, obj ?? _h.SevenZip, collection);
        return form;
    }

    private static void SetRequired(DeploymentFormViewModel form, string deadlineTime = "18:00", int days = 1)
    {
        form.IsRequired = true;
        form.DeadlineDate = form.AvailableDate!.Value.AddDays(days);
        form.DeadlineTimeText = deadlineTime;
    }

    [Fact]
    public void TheConstructorReadsNothing()
    {
        var page = _h.Create();
        Assert.Equal(0, _h.Service.ListCalls);
        Assert.Equal(0, _h.Collections.ListCalls);
        Assert.Equal(0, _h.PreferenceLoads);
        Assert.Equal(0, _h.Audit.Reads + _h.Audit.Probes);
        Assert.Empty(page.Deploy.Objects);
    }

    [Fact]
    public async Task TheFirstVisitReadsTheObjectsCollectionsAndTemplatesAndTheTypeSwitchReadsTheOtherType()
    {
        var page = await _h.OpenAsync();
        var form = page.Deploy;
        Assert.Equal(["7-Zip", "Notepad++"], form.Objects.Select(o => o.Name));
        Assert.Equal(3, form.Collections.Count);
        Assert.Equal("All Systems", form.Collections[^1].Name);
        Assert.Equal("Built-in", form.Collections[^1].Kind);
        Assert.Equal(3, form.Templates.Count);
        Assert.Equal("Workstation Pilot", form.Templates[0].Name);

        form.TypeIndex = (int)DeploymentObjectType.Package;
        Assert.Empty(form.Objects);
        await DeploymentPageHarness.WaitAsync(() => form.Objects.Count == 1 && !form.IsLoading);
        Assert.Equal(["Office Tools"], form.Objects.Select(o => o.Name));
        Assert.True(form.IsPackage);
        Assert.Equal([DeploymentObjectType.Application, DeploymentObjectType.Package], _h.Service.ListedTypes);
        Assert.Equal(1, _h.Collections.ListCalls);
    }

    [Fact]
    public async Task TheFiltersNarrowTheListsAndKeepTheSelection()
    {
        var form = await FormAsync();
        form.ObjectFilter = "note";
        Assert.Equal(["Notepad++"], form.Objects.Select(o => o.Name));
        Assert.Equal("1 of 2 objects", form.ObjectCount);
        Assert.Null(form.SelectedObject);
        form.ObjectFilter = "";
        form.SelectedObject = form.Objects[0];
        form.ObjectFilter = "7";
        Assert.Same(form.Objects[0], form.SelectedObject);

        form.CollectionFilter = "Servers";
        Assert.Equal(["Server Group A"], form.Collections.Select(c => c.Name));
    }

    [Fact]
    public async Task AnObjectListThatEndsAfterAnotherTypeWasChosenIsDiscarded()
    {
        var page = await _h.OpenAsync();
        var form = page.Deploy;
        var gate = new TaskCompletionSource();
        _h.Service.ListGates[DeploymentObjectType.Package] = gate;
        form.TypeIndex = (int)DeploymentObjectType.Package;
        var late = form.ReloadObjectsAsync();
        form.TypeIndex = (int)DeploymentObjectType.TaskSequence;
        await form.ReloadObjectsAsync();
        gate.SetResult();
        await late;
        Assert.Equal(["Windows 11 Upgrade"], form.Objects.Select(o => o.Name));
    }

    [Fact]
    public async Task ABuiltInCollectionIsListedButCannotBeChosen()
    {
        var form = await FormAsync(collection: "SMS00001");
        Assert.Equal(DeploymentFormViewModel.BuiltInRefused, form.CollectionMessage);
        await form.DeployCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentFormViewModel.BuiltInRefused, form.FormMessage);
        Assert.Empty(_h.Plans.Shown);
        Assert.Equal(0, _h.Service.PrepareCalls);
    }

    [Fact]
    public async Task AWriteRunsOnlyThroughAConfirmedPlan()
    {
        var form = await FormAsync();
        _h.Plans.Confirm = false;
        await form.DeployCommand.ExecuteAsync(null);
        Assert.Single(_h.Plans.Shown);
        Assert.Empty(_h.Service.Applied);
        Assert.Equal(DeploymentShared.NotConfirmed, form.Shared.StatusMessage);

        _h.Plans.Confirm = true;
        await form.DeployCommand.ExecuteAsync(null);
        var applied = Assert.Single(_h.Service.Applied);
        var change = Assert.Single(applied);
        Assert.Equal(DeploymentChangeKind.Create, change.Kind);
        Assert.False(change.RequiredConfirmed);
        Assert.Single(_h.Service.Deployments);
        Assert.True(form.Shared.HasUndo);
        Assert.Contains("1 changed, 0 failed.", form.Shared.ResultSummary);
    }

    [Fact]
    public async Task ThePlanNamesTheObjectCollectionScheduleOptionsChecksAndTheUndo()
    {
        var form = await FormAsync();
        await form.DeployCommand.ExecuteAsync(null);
        var plan = _h.Plans.Shown.Single();
        var item = Assert.Single(plan.Items);
        Assert.Equal("Create deployment", item.Action);
        Assert.Equal("Application 7-Zip, version 26.04 (16777300) to Pilot Workstations (LAB00010)", item.Target);
        Assert.Contains("Collection: Pilot Workstations (LAB00010), 3 members now.", item.Detail);
        Assert.Contains("Purpose: Available. Available: 2026-10-20 09:05 local time.", item.Detail);
        Assert.Contains("Check No duplicate deployment: Pass.", item.Detail);
        Assert.Contains("The audit log can be written.", item.Detail);
        Assert.Contains(DeploymentText.UndoNote, item.Detail);
        Assert.True(plan.HasUndo);
        Assert.Null(plan.ExtraConfirmation);
        Assert.Equal("Create deployment", plan.ActionName);
    }

    [Fact]
    public async Task ARequiredDeploymentNeedsTheSecondCheckBoxAndTheServiceGetsTheAcknowledgement()
    {
        var form = await FormAsync();
        SetRequired(form);
        form.RebootOutsideServiceWindow = true;
        _h.Plans.AcknowledgeExtra = false;
        await form.DeployCommand.ExecuteAsync(null);
        var plan = _h.Plans.Shown.Single();
        Assert.Equal("Create Required deployment", plan.ActionName);
        Assert.StartsWith("I understand that this Required deployment installs 7-Zip on every device of Pilot Workstations (3 devices now) at the deadline 2026-10-21 18:00 local time",
            plan.ExtraConfirmation);
        Assert.Contains("restarts devices outside maintenance windows", plan.ExtraConfirmation);
        Assert.Equal(ChangePlanStage.Review, _h.Plans.LastViewModel!.Stage);
        Assert.Empty(_h.Service.Applied);

        _h.Plans.AcknowledgeExtra = true;
        await form.DeployCommand.ExecuteAsync(null);
        var change = Assert.Single(Assert.Single(_h.Service.Applied));
        Assert.True(change.RequiredConfirmed);
        Assert.Single(_h.Service.Deployments);
    }

    [Fact]
    public async Task ABackdatedAvailableTimeShowsAWarningAndNeedsTheExtraBox()
    {
        var form = await FormAsync();
        form.AvailableTimeText = "07:00";
        await form.ValidateCommand.ExecuteAsync(null);
        Assert.Contains("the available time is in the past", form.ValidationMessage);
        await form.DeployCommand.ExecuteAsync(null);
        var plan = _h.Plans.Shown.Single();
        Assert.Equal("I understand that the available time 2026-10-20 07:00 local time is in the past, so the deployment is available at once.", plan.ExtraConfirmation);
        Assert.Contains("Warning: the available time is in the past.", plan.Items[0].Detail);
        var change = Assert.Single(Assert.Single(_h.Service.Applied));
        Assert.True(change.BackdatedAcknowledged);
        Assert.False(change.RequiredConfirmed);
    }

    [Theory]
    [InlineData("08:00", 0, "The deadline must be later than the available time.")]
    [InlineData("09:05", 0, "The deadline must be later than the available time.")]
    [InlineData("25:00", 1, "Deadline: Enter the time as hours and minutes, for example 08:30.")]
    public async Task AnInvertedOrMalformedDeadlineIsBlockedBeforeAnyRead(string time, int days, string message)
    {
        var form = await FormAsync();
        SetRequired(form, time, days);
        await form.DeployCommand.ExecuteAsync(null);
        await form.ValidateCommand.ExecuteAsync(null);
        Assert.Equal(message, form.FormMessage);
        Assert.Equal(0, _h.Service.PrepareCalls);
        Assert.Empty(_h.Plans.Shown);
    }

    [Fact]
    public async Task ADeadlineInThePastIsBlocked()
    {
        var form = await FormAsync();
        form.IsRequired = true;
        form.AvailableDate = new DateTime(2026, 10, 18);
        form.AvailableTimeText = "08:00";
        form.DeadlineDate = new DateTime(2026, 10, 19);
        form.DeadlineTimeText = "08:00";
        await form.DeployCommand.ExecuteAsync(null);
        Assert.Equal("The deadline is in the past. Set a deadline later than now.", form.FormMessage);
        Assert.Empty(_h.Plans.Shown);
    }

    [Fact]
    public async Task AnAvailableDeploymentThatHidesAllNotificationsIsBlocked()
    {
        var form = await FormAsync();
        form.Notification = DeploymentText.Notifications.Single(n => n.Value == DeploymentText.HideAll);
        await form.DeployCommand.ExecuteAsync(null);
        Assert.StartsWith("An Available deployment cannot hide all notifications", form.FormMessage);
        Assert.Empty(_h.Plans.Shown);
    }

    [Fact]
    public async Task AFailedCheckBlocksThePlanWithItsReason()
    {
        var form = await FormAsync();
        await form.DeployCommand.ExecuteAsync(null);
        _h.Plans.Shown.Clear();
        await form.DeployCommand.ExecuteAsync(null);
        Assert.Empty(_h.Plans.Shown);
        Assert.Equal("The deployment cannot be created: No duplicate deployment: The object already has a deployment to this collection. "
            + "ConfigMgr does not allow a second deployment of an application to the same collection.", form.FormMessage);
        Assert.Equal(form.FormMessage, form.Shared.StatusMessage);
        Assert.Contains(form.Checks, c => c.Name == DeploymentText.NoDuplicate && c.Result == DeploymentText.Fail);
    }

    [Fact]
    public async Task MissingContentBlocksTheCreateUnlessThePlanDistributesItFirst()
    {
        var form = await FormAsync(_h.Notepad);
        await form.DeployCommand.ExecuteAsync(null);
        Assert.Empty(_h.Plans.Shown);
        Assert.Contains("Content distributed: No distribution point group has the content.", form.FormMessage);

        form.DpGroups[0].IsChecked = true;
        await form.DeployCommand.ExecuteAsync(null);
        Assert.Empty(_h.Plans.Shown);

        form.DpGroups[0].IsChecked = true;
        form.DistributeFirst = true;
        await form.DeployCommand.ExecuteAsync(null);
        var plan = _h.Plans.Shown.Single();
        Assert.Equal(["Distribute and create deployment"], plan.Items.Select(i => i.Action));
        Assert.Contains("Distribution point groups: All DPs. The distribution is requested first.", plan.Items[0].Detail);
        Assert.Contains(DeploymentText.CreateNowHint, plan.Items[0].Detail);
        Assert.Contains("Check Content distributed: Warn.", plan.Items[0].Detail);
        Assert.Equal("Distribute and deploy", plan.ActionName);
        var applied = Assert.Single(Assert.Single(_h.Service.Applied));
        Assert.Equal(DeploymentChangeKind.Create, applied.Kind);
        Assert.Equal(["All DPs"], applied.DpGroups);
        Assert.False(applied.CreateNowContentDistributing);
    }

    [Fact]
    public async Task TheCreateNowChoiceIsOnThePlanOfACombinedDeployAndReachesTheService()
    {
        var form = await FormAsync(_h.Notepad);
        form.DpGroups[1].IsChecked = true;
        form.DistributeFirst = true;
        _h.Plans.ChooseOption = true;
        await form.DeployCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentText.CreateNowChoice, _h.Plans.Shown.Single().OptionalChoice);
        Assert.True(_h.Service.Applied.Single().Single().CreateNowContentDistributing);
        Assert.Equal(["Branch DPs"], _h.Service.Applied.Single().Single().DpGroups);
    }

    [Fact]
    public async Task DistributeContentRunsThroughAPlanWithoutUndo()
    {
        var form = await FormAsync(_h.Notepad);
        await form.DistributeCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentFormViewModel.NoGroupsChecked, form.DpMessage);
        Assert.Empty(_h.Plans.Shown);

        form.DpGroups[0].IsChecked = true;
        await form.DistributeCommand.ExecuteAsync(null);
        var plan = _h.Plans.Shown.Single();
        Assert.False(plan.HasUndo);
        Assert.Equal("Distribute content", plan.ActionName);
        Assert.Contains("Distribution point group All DPs: NotTargeted now.", plan.Items[0].Detail);
        Assert.Contains("No Undo", plan.Items[0].Detail);
        Assert.Equal(DeploymentChangeKind.Distribute, _h.Service.Applied.Single().Single().Kind);
        Assert.False(form.Shared.HasUndo);
        await DeploymentPageHarness.WaitAsync(() => form.DpGroups.Count > 0 && form.DpGroups[0].Status == "Distributed");
    }

    [Fact]
    public async Task ThePlanComesFromThePreparedChangeWhenTheFormChangesDuringPrepare()
    {
        var form = await FormAsync();
        _h.Service.MemberCounts["LAB00010"] = 7;
        _h.Service.PrepareGate = new TaskCompletionSource();
        var deploy = form.DeployCommand.ExecuteAsync(null);
        await DeploymentPageHarness.WaitAsync(() => _h.Service.PrepareCalls == 1);
        form.SelectedCollection = form.Collections.Single(c => c.CollectionId == "LAB00011");
        SetRequired(form);
        form.Notification = DeploymentText.Notifications[2];
        _h.Service.PrepareGate.SetResult();
        await deploy;

        var plan = _h.Plans.Shown.Single();
        Assert.Null(plan.ExtraConfirmation);
        Assert.Contains("Collection: Pilot Workstations (LAB00010), 7 members now.", plan.Items[0].Detail);
        Assert.Contains("Purpose: Available.", plan.Items[0].Detail);
        var change = _h.Service.Applied.Single().Single();
        Assert.Equal("LAB00010", change.Collection!.CollectionId);
        Assert.Equal("Available", change.Options!.Purpose);
        Assert.Equal("DisplayAll", change.Options.UserNotification);
        Assert.Empty(form.Checks);
    }

    [Fact]
    public async Task ACheckResultIsDiscardedWhenTheFormChangedWhileItRan()
    {
        var form = await FormAsync();
        _h.Service.PrepareGate = new TaskCompletionSource();
        var validate = form.ValidateCommand.ExecuteAsync(null);
        await DeploymentPageHarness.WaitAsync(() => _h.Service.PrepareCalls == 1);
        form.OverrideServiceWindow = true;
        _h.Service.PrepareGate.SetResult();
        await validate;
        Assert.Empty(form.Checks);
        Assert.Equal(DeploymentFormViewModel.ChangedDuringCheck, form.ValidationMessage);

        await form.ValidateCommand.ExecuteAsync(null);
        Assert.Equal(5, form.Checks.Count);
        Assert.Equal("Every check passed.", form.ValidationMessage);
        form.AllowMetered = true;
        Assert.Empty(form.Checks);
        Assert.Equal(DeploymentFormViewModel.ValidateAgain, form.ValidationMessage);
    }

    [Fact]
    public async Task ValidateShowsTheFiveChecksAndChangesNothing()
    {
        var form = await FormAsync(collection: "LAB00011");
        await form.ValidateCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentText.CheckNames, form.Checks.Select(c => c.Name));
        Assert.All(form.Checks, c => Assert.Equal("Pass", c.Result));
        Assert.Empty(_h.Service.Applied);
        Assert.Empty(_h.Plans.Shown);
    }

    [Fact]
    public async Task ASecondClickOnValidateDeployOrDistributeDoesNothingWhileTheFirstRuns()
    {
        var form = await FormAsync();
        form.DpGroups[0].IsChecked = true;
        _h.Service.PrepareGate = new TaskCompletionSource();
        var first = form.ValidateCommand.ExecuteAsync(null);
        Assert.False(form.ValidateCommand.CanExecute(null));
        await form.ValidateCommand.ExecuteAsync(null);
        var deploy = form.DeployCommand.ExecuteAsync(null);
        Assert.False(form.DeployCommand.CanExecute(null));
        Assert.False(form.DistributeCommand.CanExecute(null));
        await form.DeployCommand.ExecuteAsync(null);
        await form.DistributeCommand.ExecuteAsync(null);
        _h.Service.PrepareGate.SetResult();
        await Task.WhenAll(first, deploy);
        Assert.Equal(2, _h.Service.PrepareCalls);
        Assert.Single(_h.Plans.Shown);
        Assert.Single(_h.Service.Applied);
    }

    [Fact]
    public async Task AStateThatChangedSinceTheFormWasFilledGivesThePlainMessageAndNoPlan()
    {
        var form = await FormAsync();
        _h.Service.PrepareFails = _ => new DeploymentServiceException("The collection LAB00010 no longer exists. Nothing was changed.");
        await form.DeployCommand.ExecuteAsync(null);
        Assert.Equal("The collection LAB00010 no longer exists. Nothing was changed.", form.FormMessage);
        Assert.Equal(form.FormMessage, form.Shared.StatusMessage);
        Assert.Empty(_h.Plans.Shown);

        _h.Service.PrepareFails = _ => new InvalidOperationException(@"raw text C:\secret");
        await form.DeployCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentShared.PrepareFailed, form.FormMessage);
        Assert.DoesNotContain(_h.Log.Entries, e => e.Message.Contains("secret"));
    }

    [Fact]
    public async Task UndoLastChangeRemovesOnlyTheCreatedDeploymentThroughADestructivePlan()
    {
        var form = await FormAsync();
        await form.DeployCommand.ExecuteAsync(null);
        var created = _h.Service.Deployments.Single();
        Assert.True(form.Shared.UndoLastCommand.CanExecute(null));

        await form.Shared.UndoLastCommand.ExecuteAsync(null);
        var plan = _h.Plans.Shown[^1];
        Assert.True(plan.IsDestructive);
        Assert.Equal("Remove 1 deployment", plan.ActionName);
        Assert.Contains("Software that clients already installed stays installed.", plan.Items[0].Detail);
        var remove = _h.Service.Applied[^1].Single();
        Assert.Equal(DeploymentChangeKind.RemoveDeployment, remove.Kind);
        Assert.Equal(created.DeploymentId, remove.Existing!.DeploymentId);
        Assert.Empty(_h.Service.Deployments);
        Assert.False(form.Shared.HasUndo);
    }

    [Fact]
    public async Task TheUndoOfARequiredCreateNeverCarriesTheAcknowledgement()
    {
        var form = await FormAsync();
        SetRequired(form);
        _h.Plans.RunUndo = true;
        await form.DeployCommand.ExecuteAsync(null);
        Assert.Equal(2, _h.Service.Applied.Count);
        var undo = _h.Service.Applied[1].Single();
        Assert.Equal(DeploymentChangeKind.RemoveDeployment, undo.Kind);
        Assert.False(undo.RequiredConfirmed);
        Assert.False(undo.BackdatedAcknowledged);
        Assert.Empty(_h.Service.Deployments);
        Assert.Contains("Undo: 1 changed", form.Shared.ResultSummary);
    }

    [Fact]
    public async Task ACreateThatFailsAfterItsWriteIsShownAsPartAndKeepsItsUndo()
    {
        var form = await FormAsync();
        _h.Service.Fail = _ => "The read-back found a different deadline: stored 2026-10-21 19:00.";
        _h.Service.FailAfterWrite = true;
        await form.DeployCommand.ExecuteAsync(null);
        var row = Assert.Single(form.Shared.LastResults);
        Assert.False(row.Succeeded);
        Assert.True(row.PartialChange);
        Assert.Contains("Undo can reverse it", form.Shared.ResultSummary);
        Assert.True(form.Shared.HasUndo);
        Assert.Equal(ApplicationPackagerEngine.Services.SiteOperationOutcome.PartlySucceeded, _h.History.Entries.Single().Outcome);
    }

    [Fact]
    public async Task LeavingThePageDuringALoadDiscardsItAndReleasesTheData()
    {
        var page = _h.Create();
        var gate = new TaskCompletionSource();
        _h.Service.ListGates[DeploymentObjectType.Application] = gate;
        var visit = page.OnNavigatedToAsync();
        await DeploymentPageHarness.WaitAsync(() => _h.Service.ListCalls == 1);
        await page.OnNavigatedFromAsync();
        gate.SetResult();
        await visit;
        Assert.Empty(page.Deploy.Objects);
        Assert.Empty(page.Deploy.Collections);
        Assert.False(page.Deploy.IsLoading);
    }

    [Fact]
    public async Task LeavingThePageDuringPrepareShowsNoPlanAndReleasesTheForm()
    {
        var page = await _h.OpenAsync();
        await DeploymentPageHarness.PickAsync(page.Deploy, _h.SevenZip, "LAB00010");
        page.Deploy.IsRequired = true;
        _h.Service.PrepareGate = new TaskCompletionSource();
        var deploy = page.Deploy.DeployCommand.ExecuteAsync(null);
        await DeploymentPageHarness.WaitAsync(() => _h.Service.PrepareCalls == 1);
        await page.OnNavigatedFromAsync();
        _h.Service.PrepareGate.SetResult();
        await deploy;
        Assert.Empty(_h.Plans.Shown);
        Assert.Null(page.Deploy.SelectedObject);
        Assert.False(page.Deploy.IsRequired);
        Assert.Equal("", page.Shared.StatusMessage);

        _h.Service.PrepareGate = null;
        await page.OnNavigatedToAsync();
        Assert.Equal(2, page.Deploy.Objects.Count);
        Assert.False(page.Shared.HasResults);
    }

    [Fact]
    public async Task AFreshDetailReadForAnotherObjectWinsOverALateOne()
    {
        var form = await FormAsync();
        _h.Service.GroupGate = new TaskCompletionSource();
        form.SelectedObject = form.Objects.Single(o => o.Name == "Notepad++");
        form.SelectedObject = form.Objects.Single(o => o.Name == "7-Zip");
        _h.Service.GroupGate.SetResult();
        await DeploymentPageHarness.WaitAsync(() => !form.IsLoadingDetails);
        Assert.All(form.DpGroups, g => Assert.Equal("Distributed", g.Status));
    }

    [Fact]
    public async Task APackageNeedsItsProgramAndThePlanNamesIt()
    {
        var page = await _h.OpenAsync();
        var form = page.Deploy;
        form.TypeIndex = (int)DeploymentObjectType.Package;
        await form.ReloadObjectsAsync();
        form.SelectedObject = form.Objects.Single();
        form.SelectedCollection = form.Collections.Single(c => c.CollectionId == "LAB00011");
        await DeploymentPageHarness.WaitAsync(() => form.Programs.Count == 2);
        await form.DeployCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentFormViewModel.PickProgram, form.FormMessage);
        form.SelectedProgram = "Install";
        form.RerunBehavior = DeploymentText.RerunBehaviors[2];
        await form.DeployCommand.ExecuteAsync(null);
        var change = _h.Service.Applied.Single().Single();
        Assert.Equal("Install", change.Object!.Program);
        Assert.Equal("RerunIfFailedPreviousAttempt", change.Options!.RerunBehavior);
        Assert.Contains("Package Office Tools, program Install (LAB00020)", _h.Plans.Shown.Single().Items[0].Target);
    }

    [Theory]
    [InlineData(DeploymentObjectType.Application, true, false, false)]
    [InlineData(DeploymentObjectType.Package, true, true, false)]
    [InlineData(DeploymentObjectType.TaskSequence, true, true, true)]
    [InlineData(DeploymentObjectType.SoftwareUpdateGroup, true, false, false)]
    public async Task OnlyTheOptionsOfTheTypeShow(DeploymentObjectType type, bool metered, bool rerun, bool progress)
    {
        var page = await _h.OpenAsync();
        page.Deploy.TypeIndex = (int)type;
        Assert.Equal(metered, page.Deploy.ShowMetered);
        Assert.Equal(rerun, page.Deploy.ShowRerun);
        Assert.Equal(progress, page.Deploy.IsTaskSequence);
        Assert.Equal(type == DeploymentObjectType.SoftwareUpdateGroup, page.Deploy.IsUpdateGroup);
    }

    [Fact]
    public async Task ApplyTemplateFillsTheFormAndKeepsTheObjectAndCollection()
    {
        var form = await FormAsync();
        form.SelectedTemplate = form.Templates.Single(t => t.Name == "Workstation Production");
        form.ApplyTemplateCommand.Execute(null);
        Assert.True(form.IsRequired);
        Assert.Equal("DisplayAll", form.Notification.Value);
        Assert.True(form.RebootOutsideServiceWindow);
        Assert.True(form.AllowMetered);
        Assert.Equal(new DateTime(2026, 10, 27), form.DeadlineDate);
        Assert.Equal("09:05", form.DeadlineTimeText);
        Assert.Equal("7-Zip", form.SelectedObject!.Name);
        Assert.Equal("LAB00010", form.SelectedCollection!.CollectionId);
        Assert.Equal("Applied the template Workstation Production.", form.TemplateMessage);
    }

    [Fact]
    public async Task SaveAsTemplateStoresTheFormAndRefusesAShippedName()
    {
        var form = await FormAsync();
        SetRequired(form, "09:05", 2);
        form.UtcBasis = true;
        TextPromptViewModel? shown = null;
        _h.Dialogs.Prompt = p => shown = p;
        form.SaveAsTemplateCommand.Execute(null);
        shown!.Text = "Workstation Pilot";
        await shown.AcceptCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentFormViewModel.TemplateNameTaken, shown.ValidationMessage);
        Assert.False(shown.Accepted);

        shown.Text = "Night Shift: Required";
        await shown.AcceptCommand.ExecuteAsync(null);
        Assert.True(shown.Accepted);
        var saved = _h.Templates.Templates.Single(t => t.Id == "night-shift-required");
        Assert.Equal("Required", saved.Purpose);
        Assert.True(saved.UtcBasis);
        Assert.Equal(48, saved.DeadlineOffsetHours);
        Assert.False(saved.IsSeeded);
        Assert.Contains(form.Templates, t => t.Name == "Night Shift: Required");
        Assert.Equal("night-shift-required", form.TemplateId);
    }

    [Fact]
    public async Task TheTypeTabAndTemplateComeBackOnTheNextStartAndAnOlderFileUsesTheDefaults()
    {
        var page = await _h.OpenAsync();
        page.Deploy.TypeIndex = (int)DeploymentObjectType.TaskSequence;
        page.Deploy.SelectedTemplate = page.Deploy.Templates.Single(t => t.Name == "Lab Nightly");
        page.SelectedTabIndex = DeploymentHelperViewModel.OptionsTab;
        await page.OnNavigatedFromAsync();
        Assert.Equal(2, _h.Saved!.SelectedTab);
        Assert.Equal(2, _h.Saved.ObjectType);
        Assert.Equal("lab-nightly", _h.Saved.TemplateId);

        var next = await new DeploymentHarnessView(_h).OpenAgainAsync();
        Assert.Equal(DeploymentHelperViewModel.OptionsTab, next.SelectedTabIndex);
        Assert.Equal((int)DeploymentObjectType.TaskSequence, next.Deploy.TypeIndex);
        next.SelectedTabIndex = DeploymentHelperViewModel.DeployTab;
        await next.Deploy.EnsureLoadedAsync();
        Assert.Equal("Lab Nightly", next.Deploy.SelectedTemplate!.Name);

        var older = new DeploymentPageHarness { Saved = null };
        var fresh = await older.OpenAsync();
        Assert.Equal(DeploymentHelperViewModel.DeployTab, fresh.SelectedTabIndex);
        Assert.Equal(0, fresh.Deploy.TypeIndex);
        Assert.Equal(0, older.PreferenceSaves);
    }

    [Fact]
    public async Task WithoutASiteCodeThePageSaysSoAndReadsNothingFromConfigMgr()
    {
        _h.Settings.SiteCode = "";
        var page = await _h.OpenAsync();
        Assert.True(page.Shared.HasConnectionMessage);
        Assert.Equal(0, _h.Service.ListCalls);
        Assert.Equal(0, _h.Collections.ListCalls);
    }

    [Fact]
    public async Task TheLogAndTheSiteHistoryKeepCountsOnly()
    {
        var form = await FormAsync();
        await form.DeployCommand.ExecuteAsync(null);
        var entry = _h.History.Entries.Single();
        Assert.Equal(DeploymentWriter.ToolName, entry.Tool);
        Assert.Equal(1, entry.Counts["changed"]);
        var text = string.Join("\n", _h.Log.Entries.Select(e => e.Message));
        Assert.DoesNotContain("7-Zip", text);
        Assert.DoesNotContain("Pilot", text);
        Assert.DoesNotContain("LAB00010", text);
        Assert.DoesNotContain("DEP-", text);
        Assert.All(_h.Log.Entries, e => Assert.Equal(DeploymentShared.LogArea, e.Area));
    }

    [Fact]
    public async Task AFailedReadShowsAPlainMessageWithoutTheRawText()
    {
        _h.Service.ListFailure = new InvalidOperationException(@"provider text C:\secret");
        var page = await _h.OpenAsync();
        Assert.Equal(DeploymentFormViewModel.ReadFailedText, page.Shared.StatusMessage);
        Assert.False(page.Deploy.IsLoaded);
        Assert.DoesNotContain(_h.Log.Entries, e => e.Message.Contains("secret"));

        _h.Service.ListFailure = new DeploymentServiceException("The SMS Provider refused the read.");
        await page.Deploy.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("The SMS Provider refused the read.", page.Shared.StatusMessage);
    }

    [Fact]
    public async Task ATemplateThatCannotBeSavedKeepsThePromptOpenWithAPlainMessage()
    {
        var form = await FormAsync();
        _h.Templates.SaveFailure = new IOException(@"Access to C:\secret is denied.");
        TextPromptViewModel? shown = null;
        _h.Dialogs.Prompt = p => shown = p;
        form.SaveAsTemplateCommand.Execute(null);
        shown!.Text = "Kiosk";
        await shown.AcceptCommand.ExecuteAsync(null);
        Assert.False(shown.Accepted);
        Assert.Equal(DeploymentFormViewModel.TemplateSaveFailed, shown.ValidationMessage);
    }

    [Fact]
    public async Task TheRingDeploymentButtonStartsTheSuiteToolAndShowsAProblem()
    {
        var page = _h.Create();
        await page.StartRingToolCommand.ExecuteAsync(null);
        Assert.Equal(1, _h.RingStarts);
        Assert.Equal(DeploymentHelperViewModel.RingStarted, page.Shared.StatusMessage);
        _h.RingProblem = "Deployment Helper needs AppPackager Suite, which is not installed.";
        await page.StartRingToolCommand.ExecuteAsync(null);
        Assert.Equal(_h.RingProblem, page.Shared.StatusMessage);
    }

    /// <summary>A second page over the same saved choices, as on the next start.</summary>
    private sealed class DeploymentHarnessView(DeploymentPageHarness harness)
    {
        public async Task<DeploymentHelperViewModel> OpenAgainAsync()
        {
            var page = harness.Create();
            await page.OnNavigatedToAsync();
            return page;
        }
    }
}
