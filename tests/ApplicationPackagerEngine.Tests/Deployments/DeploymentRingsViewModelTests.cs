using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>The Rings tab over a fake ring service: every flow, the plan made from the prepared change, and late results discarded.</summary>
public sealed class DeploymentRingsViewModelTests
{
    private readonly DeploymentPageHarness _h = new();

    private static Task WaitAsync(Func<bool> condition) => DeploymentPageHarness.WaitAsync(condition);

    private async Task<DeploymentHelperViewModel> PageAsync() => await _h.OpenAsync(DeploymentHelperViewModel.RingsTab);

    private async Task<DeploymentRingsViewModel> RingsAsync() => (await PageAsync()).Rings;

    private static async Task Pick(DeploymentRingsViewModel rings, DeployableObject obj, RingPlan plan, bool hold = false)
    {
        rings.SelectedObject = rings.Objects.Single(o => o.Object.Id == obj.Id);
        rings.SelectedPlan = rings.Plans.Single(p => p.Name == plan.Name);
        rings.HoldLaterRings = hold;
        await rings.ExpandCommand.ExecuteAsync(null);
    }

    private async Task<DeploymentRingsViewModel> ExpandedAsync(bool hold = false)
    {
        var rings = await RingsAsync();
        await Pick(rings, _h.SevenZip, _h.PilotRings, hold);
        return rings;
    }

    private ChangePlanViewModel Dialog => _h.Plans.LastViewModel!;

    // ---- Loading ----------------------------------------------------------------------------------

    [Fact]
    public async Task NothingOfTheRingsTabIsReadBeforeTheTabIsShown()
    {
        var page = await _h.OpenAsync();
        Assert.Equal(0, _h.Rings.ListPlanCalls);
        Assert.Equal(0, _h.Rings.ListRunCalls);
        Assert.Empty(page.Rings.Plans);

        page.SelectedTabIndex = DeploymentHelperViewModel.RingsTab;
        await page.Rings.EnsureLoadedAsync();
        Assert.Equal(1, _h.Rings.ListPlanCalls);
        Assert.Equal(["Workstation-Rings", "Pilot-Rings"], page.Rings.Plans.Select(p => p.Name));
        Assert.Equal("Shipped", page.Rings.Plans[0].Source);
        Assert.Contains(page.Rings.Objects, o => o.Object.Id == _h.SevenZip.Id);
    }

    [Fact]
    public async Task LeavingThePageDuringALoadDiscardsItAndReleasesTheData()
    {
        var page = await _h.OpenAsync();
        _h.Rings.RunsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.SelectedTabIndex = DeploymentHelperViewModel.RingsTab;
        await WaitAsync(() => _h.Rings.ListRunCalls == 1);
        var load = page.Rings.LoadTask;
        await page.OnNavigatedFromAsync();
        Assert.True(_h.Rings.Tokens.Last().IsCancellationRequested);
        _h.Rings.RunsGate.SetResult();
        await load;
        Assert.Empty(page.Rings.Plans);
        Assert.False(page.Rings.IsLoaded);
        Assert.Empty(page.Rings.Runs);
    }

    // ---- Expand and edits for this run ------------------------------------------------------------

    [Fact]
    public async Task ExpandShowsEachRingWithItsDatesAndMemberCount()
    {
        var rings = await ExpandedAsync();
        Assert.Equal(["Pilot", "Early", "Broad"], rings.PreviewRows.Select(r => r.Name));
        Assert.Equal("2026-10-20 09:05", rings.PreviewRows[0].Available);
        Assert.Equal("", rings.PreviewRows[0].Deadline);
        Assert.Equal("2026-10-23 09:05", rings.PreviewRows[1].Deadline);
        Assert.Equal("40", rings.PreviewRows[2].MemberCount);
        Assert.Equal("90 %", rings.PreviewRows[0].Threshold);
        Assert.Equal(1, _h.Rings.ExpandCalls);
    }

    [Fact]
    public async Task ACollectionSetForThisRunIsMarkedAndTravelsWithTheRunOnly()
    {
        var rings = await ExpandedAsync();
        _h.Collections.Add("LAB00077", "Finance Pilot", @"Deploy\Finance", members: 9);
        await rings.RefreshCommand.ExecuteAsync(null);
        await Pick(rings, _h.SevenZip, _h.PilotRings);
        rings.SelectedPreviewRow = rings.PreviewRows[0];
        _h.Dialogs.PickedCollection = "LAB00077";
        rings.SetCollectionCommand.Execute(null);

        Assert.Equal("Finance Pilot (LAB00077)", rings.PreviewRows[0].Collection);
        Assert.Equal("9", rings.PreviewRows[0].MemberCount);
        Assert.Equal("Collection set for this run", rings.PreviewRows[0].Change);
        var request = rings.BuildRunRequest()!;
        Assert.Equal("LAB00077", request.Plan!.Rings[0].CollectionId);
        Assert.Equal("LAB00077", request.Rows[0].CollectionId);
        Assert.Equal("LAB00010", _h.Rings.Plans.Single(p => p.Name == "Pilot-Rings").Rings[0].CollectionId);
        Assert.Empty(_h.Rings.Saved);
    }

    [Fact]
    public async Task ABuiltInOrSharedCollectionCannotBeSetForARing()
    {
        var rings = await ExpandedAsync();
        rings.SelectedPreviewRow = rings.PreviewRows[0];
        _h.Dialogs.PickedCollection = "SMS00001";
        rings.SetCollectionCommand.Execute(null);
        Assert.Equal(DeploymentFormViewModel.BuiltInRefused, rings.FormMessage);
        _h.Dialogs.PickedCollection = "LAB00011";
        rings.SetCollectionCommand.Execute(null);
        Assert.Equal("Ring Early already uses Server Group A (LAB00011). Each ring needs its own collection.", rings.FormMessage);
        Assert.Equal("LAB00010", rings.PreviewRows[0].Row.CollectionId);
    }

    [Fact]
    public async Task AnExpansionThatEndsAfterTheFormChangedIsDiscarded()
    {
        var rings = await RingsAsync();
        rings.SelectedObject = rings.Objects.Single(o => o.Object.Id == _h.SevenZip.Id);
        rings.SelectedPlan = rings.Plans.Single(p => p.Name == "Pilot-Rings");
        _h.Rings.ExpandGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expand = rings.ExpandCommand.ExecuteAsync(null);
        await WaitAsync(() => _h.Rings.ExpandCalls == 1);
        rings.PlanStartTimeText = "10:00";
        _h.Rings.ExpandGate.SetResult();
        await expand;
        Assert.Empty(rings.PreviewRows);
        Assert.Equal(DeploymentRingsViewModel.ChangedDuringExpand, rings.ValidationMessage);
    }

    [Fact]
    public async Task ChangingThePlanOrTheStartAfterExpandClearsTheRings()
    {
        var rings = await ExpandedAsync();
        rings.PlanStartTimeText = "11:00";
        Assert.Empty(rings.PreviewRows);
        Assert.Equal(DeploymentRingsViewModel.ExpandAgain, rings.ValidationMessage);
        Assert.Null(rings.BuildRunRequest());
        Assert.Equal(DeploymentRingsViewModel.ExpandFirst, rings.FormMessage);
    }

    // ---- Validate ----------------------------------------------------------------------------------

    [Fact]
    public async Task ValidateShowsTheChecksOfTheRunAndOfEachRing()
    {
        var rings = await ExpandedAsync();
        await rings.ValidateCommand.ExecuteAsync(null);
        Assert.Contains(rings.Checks, c => c.Ring == "Run" && c.Check == "PlanRules" && c.Result == "Pass");
        Assert.Equal(5, rings.Checks.Count(c => c.Ring == "Early"));
        Assert.All(rings.PreviewRows, r => Assert.Equal("Passed", r.CheckText));
        Assert.Equal("Every check passed.", rings.ValidationMessage);
    }

    [Fact]
    public async Task AValidationResultIsDiscardedWhenTheFormChangedWhileItRan()
    {
        var rings = await ExpandedAsync();
        _h.Rings.PrepareGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validate = rings.ValidateCommand.ExecuteAsync(null);
        await WaitAsync(() => _h.Rings.PrepareCalls == 1);
        rings.HoldLaterRings = true;
        _h.Rings.PrepareGate.SetResult();
        await validate;
        Assert.Empty(rings.Checks);
        Assert.Equal(DeploymentRingsViewModel.ChangedDuringCheck, rings.ValidationMessage);
    }

    [Fact]
    public async Task AnEditAfterValidateDropsTheChecks()
    {
        var rings = await ExpandedAsync();
        await rings.ValidateCommand.ExecuteAsync(null);
        Assert.NotEmpty(rings.Checks);
        _h.Collections.Add("LAB00078", "Finance Early", "", members: 2);
        await rings.RefreshCommand.ExecuteAsync(null);
        await Pick(rings, _h.SevenZip, _h.PilotRings);
        await rings.ValidateCommand.ExecuteAsync(null);
        rings.SelectedPreviewRow = rings.PreviewRows[1];
        _h.Dialogs.PickedCollection = "LAB00078";
        rings.SetCollectionCommand.Execute(null);
        Assert.Empty(rings.Checks);
        Assert.Equal(DeploymentRingsViewModel.ValidateAgain, rings.ValidationMessage);
        Assert.All(rings.PreviewRows, r => Assert.Equal("", r.CheckText));
    }

    // ---- Create run --------------------------------------------------------------------------------

    [Fact]
    public async Task CreateAllShowsEveryDeploymentAndTheRequiredBoxReachesTheService()
    {
        var rings = await ExpandedAsync();
        await rings.CreateRunCommand.ExecuteAsync(null);

        var plan = _h.Plans.Shown.Single();
        Assert.Equal(["Create deployment", "Create Required deployment", "Create Required deployment"], plan.Items.Select(i => i.Action));
        Assert.Equal("I understand that 2 Required rings enforce on 44 devices in total at their deadlines.", plan.ExtraConfirmation);
        Assert.Contains("Collection: Broad Workstations (LAB00012), 40 members now.", plan.Items[2].Detail);
        Assert.Contains(RingText.UndoRunNote, plan.Items[0].Detail);
        var sent = _h.Rings.Applied.Single().Single();
        Assert.True(sent.RequiredConfirmed);
        Assert.All(sent.RingChanges, c => Assert.False(c.RequiredConfirmed));
        Assert.Equal(3, Dialog.ResultRows.Count(r => r.Succeeded));
        Assert.Single(rings.Runs);
        Assert.Equal("3 created", rings.Runs[0].Rings);
        Assert.StartsWith("Ring run of 7-Zip with Pilot-Rings: 3 changed, 0 failed.", rings.Shared.ResultSummary);
    }

    [Fact]
    public async Task AFailedSecondRingStopsTheRunAndTheThirdIsNotAttempted()
    {
        var rings = await ExpandedAsync();
        _h.Rings.FailRing = row => row.Name == "Early" ? "A deployment of this object to the collection exists. Nothing was created." : null;
        await rings.CreateRunCommand.ExecuteAsync(null);
        var rows = Dialog.ResultRows;
        Assert.True(rows[0].Succeeded);
        Assert.False(rows[1].Succeeded);
        Assert.False(rows[1].Skipped);
        Assert.True(rows[2].Skipped);
        Assert.Equal(RingWriter.StoppedText, rows[2].Message);
        Assert.Equal("1 created, 2 cancelled", rings.Runs.Single().Rings);
        var entry = _h.History.Entries.Single();
        Assert.Equal((1, 1, 1), (entry.Counts["changed"], entry.Counts["failed"], entry.Counts["skipped"]));
    }

    [Fact]
    public async Task HoldCreatesTheFirstRingAndHoldsTheOthers()
    {
        var rings = await ExpandedAsync(hold: true);
        await rings.CreateRunCommand.ExecuteAsync(null);
        var plan = _h.Plans.Shown.Single();
        Assert.Equal(["Create deployment", "Hold ring", "Hold ring"], plan.Items.Select(i => i.Action));
        Assert.Null(plan.ExtraConfirmation);
        var sent = _h.Rings.Applied.Single().Single();
        Assert.True(sent.Plan!.HoldLaterRings);
        Assert.False(sent.RequiredConfirmed);
        Assert.Equal(RingWriter.HeldText, Dialog.ResultRows[1].Message);
        Assert.Equal("1 created, 2 held", rings.Runs.Single().Rings);
        Assert.False(_h.Rings.Plans.Single(p => p.Name == "Pilot-Rings").HoldLaterRings);
    }

    [Fact]
    public async Task ARunThatDistributesFirstOffersCreateNowAndOnlyAChosenBoxTravels()
    {
        _h.Rings.Plans[1] = _h.PilotRings with { Rings = [_h.PilotRings.Rings[0] with { DpGroup = "All DPs" }, .. _h.PilotRings.Rings.Skip(1)] };
        var rings = await RingsAsync();
        await Pick(rings, _h.SevenZip, _h.PilotRings);
        await rings.CreateRunCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentText.CreateNowChoice, _h.Plans.Shown.Single().OptionalChoice);
        Assert.False(_h.Rings.Applied.Single().Single().CreateNowContentDistributing);
    }

    [Fact]
    public async Task APlanThatACheckBlocksIsNotShown()
    {
        _h.Rings.AfterPrepare = c => c.Kind == RingChangeKind.CreateRun
            ? c with { Checks = [new PreflightCheck(Name: "PlanRules", Result: "Fail", Message: "The deadline of ring Broad is in the past.")] }
            : c;
        var rings = await ExpandedAsync();
        await rings.CreateRunCommand.ExecuteAsync(null);
        Assert.Empty(_h.Plans.Shown);
        Assert.Equal("The run cannot be created: PlanRules: The deadline of ring Broad is in the past.", rings.FormMessage);
        Assert.Empty(_h.Rings.Applied);
    }

    [Fact]
    public async Task ThePlanComesFromThePreparedChangeEvenWhenTheFormChangesDuringPrepare()
    {
        var rings = await ExpandedAsync();
        _h.Rings.PrepareGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var create = rings.CreateRunCommand.ExecuteAsync(null);
        await WaitAsync(() => _h.Rings.PrepareCalls == 1);
        rings.HoldLaterRings = true;
        _h.Rings.MemberCounts["LAB00012"] = 41;
        _h.Rings.PrepareGate.SetResult();
        await create;
        var plan = _h.Plans.Shown.Single();
        Assert.Equal(3, plan.Items.Count(i => i.Action.StartsWith("Create", StringComparison.Ordinal)));
        Assert.Contains("41 members now", plan.Items[2].Detail);
        Assert.False(_h.Rings.Applied.Single().Single().Plan!.HoldLaterRings);
    }

    [Fact]
    public async Task ADoubleClickOnCreateRunPreparesOnceAndTheSecondIsRefusedPlainly()
    {
        var rings = await ExpandedAsync();
        _h.Rings.PrepareGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = rings.CreateRunCommand.ExecuteAsync(null);
        await WaitAsync(() => _h.Rings.PrepareCalls == 1);
        Assert.False(rings.CreateRunCommand.CanExecute(null));
        await rings.CreateRunCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentRingsViewModel.WriteRunning, rings.FormMessage);
        _h.Rings.PrepareGate.SetResult();
        await first;
        Assert.Equal(1, _h.Rings.PrepareCalls);
        Assert.Single(_h.Rings.Applied);
    }

    [Fact]
    public async Task ADeclinedPlanChangesNothing()
    {
        var rings = await ExpandedAsync();
        _h.Plans.Confirm = false;
        await rings.CreateRunCommand.ExecuteAsync(null);
        Assert.Empty(_h.Rings.Applied);
        Assert.Equal(DeploymentShared.NotConfirmed, rings.Shared.StatusMessage);
    }

    [Fact]
    public async Task APreparationFailureShowsThePlainServiceMessageOnly()
    {
        var rings = await ExpandedAsync();
        _h.Rings.PrepareFails = _ => new InvalidOperationException(@"raw provider text \\cm01\secret");
        await rings.CreateRunCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentShared.PrepareFailed, rings.FormMessage);
        _h.Rings.PrepareFails = _ => new DeploymentServiceException("The run folder cannot be written.");
        await rings.CreateRunCommand.ExecuteAsync(null);
        Assert.Equal("The run folder cannot be written.", rings.FormMessage);
        Assert.DoesNotContain(_h.Log.Entries, e => e.Message.Contains("secret", StringComparison.Ordinal));
    }

    // ---- Open runs ----------------------------------------------------------------------------------

    private RingRun HeldRun(int? threshold = null, string secondPurpose = "Required") =>
        _h.Rings.AddRun("RUN-100", _h.SevenZip, ("Pilot", "LAB00010", "Created", threshold, "Available"), ("Early", "LAB00011", "Held", null, secondPurpose),
            ("Broad", "LAB00012", "Held", null, "Required"));

    private async Task<DeploymentRingsViewModel> RunSelectedAsync()
    {
        var rings = await RingsAsync();
        rings.SelectedRun = rings.Runs.Single(r => r.RunId == "RUN-100");
        await rings.CountsTask;
        return rings;
    }

    [Fact]
    public async Task TheSelectedRunShowsItsRingsAndUnknownCountsAsUnknown()
    {
        HeldRun();
        _h.Rings.Counts["RUN-100"] = [new RingLiveCounts(Index: 1, DeploymentId: "MCM20000", State: "Found", Targeted: 0, Success: 0, Error: 0, InProgress: 0, Unknown: 0,
            CountsKnown: false, Message: "ConfigMgr has no summary of this deployment yet.")];
        var rings = await RunSelectedAsync();
        Assert.Equal(["Created", "Held", "Held"], rings.RunRings.Select(r => r.Status));
        Assert.Equal(RingText.UnknownCount, rings.RunRings[0].Targeted);
        Assert.Equal("", rings.RunRings[1].Targeted);
        Assert.Equal("ConfigMgr has no summary of this deployment yet.", rings.RunRings[0].CountsNote);
        Assert.Equal("1 created, 2 held", rings.Runs[0].Rings);
    }

    [Fact]
    public async Task LiveCountsThatArriveAfterAnotherRunWasSelectedAreDiscarded()
    {
        HeldRun();
        _h.Rings.AddRun("RUN-200", _h.Notepad, ("Pilot", "LAB00010", "Created", null, "Available"));
        _h.Rings.Counts["RUN-100"] = [new RingLiveCounts(Index: 1, DeploymentId: "MCM20000", State: "Summarized", Targeted: 10, Success: 9, Error: 1, InProgress: 0, Unknown: 0, CountsKnown: true, Message: "")];
        var rings = await RingsAsync();
        _h.Rings.CountsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rings.SelectedRun = rings.Runs.Single(r => r.RunId == "RUN-100");
        var late = rings.CountsTask;
        rings.SelectedRun = rings.Runs.Single(r => r.RunId == "RUN-200");
        _h.Rings.CountsGate.SetResult();
        await late;
        await rings.CountsTask;
        Assert.Equal("RUN-200", rings.SelectedRun!.RunId);
        Assert.Equal(RingText.UnknownCount, rings.RunRings.Single().Targeted);
    }

    [Fact]
    public async Task PromoteOfARequiredRingShowsItsOwnPlanWithTheSecondBox()
    {
        HeldRun();
        var rings = await RunSelectedAsync();
        Assert.True(rings.PromoteRingCommand.CanExecute(null));
        Assert.Equal("Promote creates the deployment of ring Early. The live counts are read again before the plan.", rings.PromoteReason);
        await rings.PromoteRingCommand.ExecuteAsync(null);
        var plan = _h.Plans.Shown.Single();
        Assert.Equal("Create Required deployment", plan.Items.Single().Action);
        Assert.StartsWith("I understand that the Required ring Early installs software on every device of Collection LAB00011 (4 devices now)", plan.ExtraConfirmation);
        var sent = _h.Rings.Applied.Single().Single();
        Assert.Equal((RingChangeKind.Promote, 2, true), (sent.Kind, sent.RingIndex, sent.RequiredConfirmed));
        Assert.Equal("v1", sent.Fingerprint);
        Assert.Equal("2 created, 1 held", rings.Runs.Single().Rings);
        Assert.Equal("Promote creates the deployment of ring Broad. The live counts are read again before the plan.", rings.PromoteReason);
    }

    [Fact]
    public async Task TheThresholdGateStaysShutWithoutCountsAndBelowTheThresholdAndSaysWhy()
    {
        HeldRun(threshold: 90);
        var rings = await RunSelectedAsync();
        Assert.False(rings.PromoteRingCommand.CanExecute(null));
        Assert.Equal("Ring Pilot needs 90 % success before ring Early, and its live counts are not known yet. Select Refresh and reconcile, then try again.", rings.PromoteReason);

        _h.Rings.Counts["RUN-100"] = [new RingLiveCounts(Index: 1, DeploymentId: "MCM20000", State: "Summarized", Targeted: 20, Success: 9, Error: 2, InProgress: 9, Unknown: 0, CountsKnown: true, Message: "")];
        await rings.LoadRunsAsync();
        await rings.CountsTask;
        Assert.False(rings.PromoteRingCommand.CanExecute(null));
        Assert.Equal("Ring Pilot has 45 % success (9 of 20); ring Early needs 90 %.", rings.PromoteReason);

        _h.Rings.Counts["RUN-100"] = [new RingLiveCounts(Index: 1, DeploymentId: "MCM20000", State: "Summarized", Targeted: 20, Success: 19, Error: 1, InProgress: 0, Unknown: 0, CountsKnown: true, Message: "")];
        await rings.LoadRunsAsync();
        await rings.CountsTask;
        Assert.True(rings.PromoteRingCommand.CanExecute(null));
        Assert.Equal("95 %", rings.RunRings[0].SuccessRate);
    }

    [Fact]
    public async Task PromoteIsOffWhenThePreviousRingIsNotCreated()
    {
        _h.Rings.AddRun("RUN-100", _h.SevenZip, ("Pilot", "LAB00010", "Removed", null, "Available"), ("Early", "LAB00011", "Held", null, "Available"));
        var rings = await RunSelectedAsync();
        Assert.False(rings.PromoteRingCommand.CanExecute(null));
        Assert.Equal("Ring Pilot is removed, not created, so ring Early cannot be promoted. Select Refresh and reconcile.", rings.PromoteReason);
    }

    [Fact]
    public async Task APlanPreparedOnAnOlderRunFileIsSkippedWithAPlainMessage()
    {
        HeldRun(secondPurpose: "Available");
        var rings = await RunSelectedAsync();
        _h.Rings.BeforeApply = () => _h.Rings.Touch("RUN-100");
        await rings.PromoteRingCommand.ExecuteAsync(null);
        var row = Dialog.ResultRows.Single();
        Assert.True(row.Skipped);
        Assert.Equal(FakeRingService.StaleRun, row.Message);
        Assert.Equal("1 created, 2 held", rings.Runs.Single().Rings);
    }

    [Fact]
    public async Task AHeldLockRefusesTheActionAndNamesItsOwner()
    {
        HeldRun();
        var rings = await RunSelectedAsync();
        _h.Rings.LockOwner = @"LAB\other on WS02, process 4242, since 2026-10-20 08:55";
        await rings.ReconcileCommand.ExecuteAsync(null);
        Assert.Contains(@"locked by another session (LAB\other on WS02, process 4242, since 2026-10-20 08:55). Lock file: RUN-100.lock.", rings.RunsMessage);
        Assert.StartsWith("Reconciled 0 runs.", rings.RunsMessage);

        await rings.PromoteRingCommand.ExecuteAsync(null);
        Assert.Empty(_h.Plans.Shown);
        Assert.Contains("Lock file: RUN-100.lock", rings.RunsMessage);
        Assert.Empty(_h.Rings.Applied);
    }

    [Fact]
    public async Task ReconcileReadsEveryOpenRunAndAFinishedRunMovesToTheClosedList()
    {
        _h.Rings.AddRun("RUN-100", _h.SevenZip, ("Pilot", "LAB00010", "Created", null, "Available"), ("Early", "LAB00011", "Removed", null, "Available"));
        HeldRunAs("RUN-300");
        var rings = await RingsAsync();
        await rings.ReconcileCommand.ExecuteAsync(null);
        Assert.Equal(2, _h.Rings.ReconcileCalls);
        Assert.Equal("Reconciled 2 runs.", rings.RunsMessage);
        Assert.Equal(["RUN-300"], rings.Runs.Select(r => r.RunId));
        rings.ShowClosedRuns = true;
        await WaitAsync(() => rings.Runs.Count == 2);
        Assert.True(rings.Runs.Last().Run.IsClosed);
        rings.SelectedRun = rings.Runs.Last();
        Assert.True(rings.UndoRunCommand.CanExecute(null));
        Assert.False(rings.CloseRunCommand.CanExecute(null));
        Assert.False(rings.PromoteRingCommand.CanExecute(null));
        Assert.Equal("The run is closed. Nothing can be promoted.", rings.PromoteReason);
    }

    private void HeldRunAs(string id) =>
        _h.Rings.AddRun(id, _h.Office with { Program = "Install" }, ("Pilot", "LAB00010", "Created", null, "Available"), ("Early", "LAB00011", "Held", null, "Available"));

    [Fact]
    public async Task UndoRunKeepsAChangedDeploymentCancelsHeldRingsAndAsksTheRemovalQuestion()
    {
        _h.Rings.AddRun("RUN-100", _h.SevenZip, ("Pilot", "LAB00010", "Created", null, "Available"), ("Early", "LAB00011", "Created", null, "Required"),
            ("Broad", "LAB00012", "Held", null, "Required"));
        _h.Rings.ChangedByOthers.Add("MCM20001");
        var rings = await RunSelectedAsync();
        await rings.UndoRunCommand.ExecuteAsync(null);

        var plan = _h.Plans.Shown.Single();
        Assert.True(plan.IsDestructive);
        Assert.Equal(["Remove deployment", "Keep deployment", "Cancel held ring", "Close run"], plan.Items.Select(i => i.Action));
        Assert.Contains("Another process changed the deadline.", plan.Items[1].Detail);
        Assert.Equal("Remove the deployment 7-Zip from Pilot Workstations (LAB00010)? Software that clients already installed stays installed.", _h.Dialogs.RemovalPrompts.Single());
        var sent = _h.Rings.Applied.Single().Single();
        Assert.Equal(RingChangeKind.UndoRun, sent.Kind);
        var rows = Dialog.ResultRows;
        Assert.True(rows[0].Succeeded);
        Assert.True(rows[1].Skipped);
        Assert.StartsWith("Skipped: kept because a check did not pass.", rows[1].Message);
        Assert.Equal(RingWriter.CancelledText, rows[2].Message);
        Assert.Equal(RingWriter.ClosedText, rows[3].Message);
        Assert.Empty(rings.Runs);
    }

    [Fact]
    public async Task ADeclinedRemovalQuestionChangesNothing()
    {
        HeldRun();
        var rings = await RunSelectedAsync();
        _h.Dialogs.ConfirmRemoval = false;
        await rings.UndoRunCommand.ExecuteAsync(null);
        Assert.Empty(_h.Rings.Applied);
        Assert.All(Dialog.ResultRows, r => Assert.True(r.Skipped));
        Assert.Contains(Dialog.ResultRows, r => r.Message == DeploymentWriter.RemovalNotConfirmed);
        Assert.Single(rings.Runs);
    }

    [Fact]
    public async Task CloseRunShowsItsPlanAndMovesTheRunToTheClosedList()
    {
        HeldRun();
        var rings = await RunSelectedAsync();
        await rings.CloseRunCommand.ExecuteAsync(null);
        var plan = _h.Plans.Shown.Single();
        Assert.Equal("Close run", plan.Items.Single().Action);
        Assert.False(plan.IsDestructive);
        Assert.Contains("No deployment is changed.", plan.Items.Single().Detail);
        Assert.Empty(rings.Runs);
    }

    [Fact]
    public async Task ASecondActionWhileARunIsWritingIsRefusedWithAPlainMessage()
    {
        HeldRun();
        var rings = await RunSelectedAsync();
        _h.Rings.PrepareGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promote = rings.PromoteRingCommand.ExecuteAsync(null);
        await WaitAsync(() => _h.Rings.PrepareCalls == 1);
        await rings.CloseRunCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentRingsViewModel.WriteRunning, rings.RunsMessage);
        await rings.ReconcileCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentRingsViewModel.WriteRunning, rings.RunsMessage);
        Assert.Equal(0, _h.Rings.ReconcileCalls);
        _h.Rings.PrepareGate.SetResult();
        await promote;
        Assert.Equal(1, _h.Rings.PrepareCalls);
    }

    [Fact]
    public async Task ADoubleClickOnPromoteAndUndoRunPreparesOnce()
    {
        HeldRun();
        var rings = await RunSelectedAsync();
        _h.Rings.PrepareGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = rings.PromoteRingCommand.ExecuteAsync(null);
        await WaitAsync(() => _h.Rings.PrepareCalls == 1);
        await rings.PromoteRingCommand.ExecuteAsync(null);
        await rings.UndoRunCommand.ExecuteAsync(null);
        _h.Rings.PrepareGate.SetResult();
        await first;
        Assert.Equal(1, _h.Rings.PrepareCalls);
        Assert.Single(_h.Plans.Shown);
    }

    /// <summary>A plan window whose operator closes it while the run still works.</summary>
    private sealed class DetachingPlans(FakeRingService rings) : IChangePlanDialogs
    {
        public ChangePlanViewModel? Dialog;

        public ChangePlanOutcome Show(ChangePlan plan, ChangePlanRun run, ChangePlanRun? undo = null)
        {
            Dialog = new ChangePlanViewModel(plan, run, undo, forceCloseDelay: TimeSpan.Zero) { ExtraAcknowledged = true, DestructiveAcknowledged = true };
            _ = Dialog.ConfirmCommand.ExecuteAsync(null);
            ScriptedPlanDialogs.Wait(DeploymentPageHarness.WaitAsync(() => rings.Applied.Count == 1));
            Dialog.RequestCancel();
            ScriptedPlanDialogs.Wait(DeploymentPageHarness.WaitAsync(() => Dialog.ForceCloseCommand.CanExecute(null)));
            Dialog.ForceCloseCommand.Execute(null);
            return Dialog.Outcome;
        }
    }

    [Fact]
    public async Task ARunLeftWorkingKeepsWritesBlockedAndRefreshesTheRunsWhenItEnds()
    {
        HeldRun();
        var plans = new DetachingPlans(_h.Rings);
        var services = _h.Services() with { Plans = plans };
        var page = new DeploymentHelperViewModel(services);
        await page.OnNavigatedToAsync();
        page.SelectedTabIndex = DeploymentHelperViewModel.RingsTab;
        await page.Rings.EnsureLoadedAsync();
        var rings = page.Rings;
        rings.SelectedRun = rings.Runs.Single();
        await rings.CountsTask;
        _h.Rings.InConfirmedRun = null;
        _h.Rings.ApplyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await rings.PromoteRingCommand.ExecuteAsync(null);
        Assert.True(plans.Dialog!.IsDetached);
        Assert.True(page.Shared.IsWriting);
        Assert.Equal(DeploymentShared.BackgroundRunText, rings.RunsMessage);
        await rings.CloseRunCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentShared.BackgroundRunText, rings.RunsMessage);

        _h.Rings.ApplyGate.SetResult();
        await WaitAsync(() => !page.Shared.IsWriting);
        await WaitAsync(() => rings.Runs.Count == 1 && rings.Runs[0].Rings == "2 created, 1 held");
        Assert.Equal(DeploymentShared.BackgroundRunEndedText, page.Shared.StatusMessage);
    }

    [Fact]
    public async Task LeavingThePageDuringAWriteKeepsTheRunAndTheNextVisitShowsIt()
    {
        HeldRun();
        var plans = new DetachingPlans(_h.Rings);
        var page = new DeploymentHelperViewModel(_h.Services() with { Plans = plans });
        await page.OnNavigatedToAsync();
        page.SelectedTabIndex = DeploymentHelperViewModel.RingsTab;
        await page.Rings.EnsureLoadedAsync();
        page.Rings.SelectedRun = page.Rings.Runs.Single();
        await page.Rings.CountsTask;
        _h.Rings.InConfirmedRun = null;
        _h.Rings.ApplyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.Rings.PromoteRingCommand.ExecuteAsync(null);
        await page.OnNavigatedFromAsync();
        Assert.Empty(page.Rings.Runs);
        _h.Rings.ApplyGate.SetResult();
        await WaitAsync(() => !page.Shared.IsWriting);

        await page.OnNavigatedToAsync();
        await WaitAsync(() => page.Rings.IsLoaded);
        Assert.Equal("2 created, 1 held", page.Rings.Runs.Single().Rings);
    }

    // ---- Plans -------------------------------------------------------------------------------------

    [Fact]
    public async Task ShippedPlansCannotBeEditedOrDeletedOnlyCopied()
    {
        var rings = await RingsAsync();
        rings.SelectedPlan = rings.Plans.Single(p => p.Name == "Workstation-Rings");
        rings.EditPlanCommand.Execute(null);
        Assert.Equal(DeploymentRingsViewModel.SeedReadOnly, rings.PlansMessage);
        await rings.DeletePlanCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentRingsViewModel.SeedReadOnly, rings.PlansMessage);
        Assert.Equal(0, _h.Dialogs.RingEditors);
        Assert.Equal(0, _h.Dialogs.PlanDeletePrompts);

        RingPlanEditorViewModel? editor = null;
        _h.Dialogs.RingEditor = e => editor = e;
        rings.CopyPlanCommand.Execute(null);
        Assert.Equal("Workstation-Rings copy", editor!.Name);
        Assert.True(editor.IsCopy);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Ring Pilot has no collection, so the plan cannot run. Pick a device collection.", editor.ValidationMessage);
        editor.SelectedRing = editor.Rings[0];
        _h.Dialogs.PickedCollection = "LAB00010";
        editor.PickCollectionCommand.Execute(null);
        editor.SelectedRing = editor.Rings[1];
        _h.Dialogs.PickedCollection = "LAB00011";
        editor.PickCollectionCommand.Execute(null);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.True(editor.Saved);
        var saved = _h.Rings.Saved.Single();
        Assert.Equal(("Workstation-Rings copy", false, false), (saved.Plan.Name, saved.Replace, saved.Plan.IsSeed));
        Assert.Equal("Workstation-Rings copy", rings.SelectedPlan!.Name);
        Assert.Equal("Saved the plan Workstation-Rings copy.", rings.PlansMessage);
    }

    [Fact]
    public async Task EditingAUserPlanUnderANewNameSavesTheNewAndDeletesTheOld()
    {
        var rings = await RingsAsync();
        rings.SelectedPlan = rings.Plans.Single(p => p.Name == "Pilot-Rings");
        RingPlanEditorViewModel? editor = null;
        _h.Dialogs.RingEditor = e => editor = e;
        rings.EditPlanCommand.Execute(null);
        editor!.Name = "Pilot-Rings 2026";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.True(editor.Saved);
        Assert.Equal(["Pilot-Rings"], _h.Rings.Deleted);
        Assert.Contains(rings.Plans, p => p.Name == "Pilot-Rings 2026");
        Assert.DoesNotContain(rings.Plans, p => p.Name == "Pilot-Rings");
    }

    [Fact]
    public async Task APlanTheServiceRefusesKeepsTheEditorOpenWithItsPlainMessage()
    {
        var rings = await RingsAsync();
        RingPlanEditorViewModel? editor = null;
        _h.Dialogs.RingEditor = e => editor = e;
        rings.NewPlanCommand.Execute(null);
        editor!.Name = "Kiosks";
        editor.SelectedRing = editor.Rings[0];
        _h.Dialogs.PickedCollection = "LAB00010";
        editor.PickCollectionCommand.Execute(null);
        _h.Rings.SaveFailure = new DeploymentServiceException("The plan folder cannot be written.");
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.False(editor.Saved);
        Assert.Equal("The plan folder cannot be written.", editor.ValidationMessage);
        _h.Rings.SaveFailure = new IOException(@"Access to \\fs01\secret denied.");
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentRingsViewModel.PlanSaveFailed, editor.ValidationMessage);
        _h.Rings.SaveFailure = null;
        _h.Rings.SaveReturnsNull = true;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentRingsViewModel.PlanSaveFailed, editor.ValidationMessage);
    }

    [Fact]
    public async Task DeletingAUserPlanAsksFirstAndKeepsItWhenDeclined()
    {
        var rings = await RingsAsync();
        rings.SelectedPlan = rings.Plans.Single(p => p.Name == "Pilot-Rings");
        _h.Dialogs.ConfirmDeletePlan = false;
        await rings.DeletePlanCommand.ExecuteAsync(null);
        Assert.Empty(_h.Rings.Deleted);
        _h.Dialogs.ConfirmDeletePlan = true;
        await rings.DeletePlanCommand.ExecuteAsync(null);
        Assert.Equal(["Pilot-Rings"], _h.Rings.Deleted);
        Assert.Equal(["Workstation-Rings"], rings.Plans.Select(p => p.Name));
        Assert.Equal("Deleted the plan Pilot-Rings. Runs made with it keep their run files.", rings.PlansMessage);
    }

    // ---- Persistence and log ------------------------------------------------------------------------

    [Fact]
    public async Task TheRingsTabAndThePlanComeBackOnTheNextStart()
    {
        var page = await PageAsync();
        page.Rings.SelectedPlan = page.Rings.Plans.Single(p => p.Name == "Pilot-Rings");
        await page.OnNavigatedFromAsync();
        Assert.Equal(3, _h.Saved!.SelectedTab);
        Assert.Equal("Pilot-Rings", _h.Saved.RingPlanName);

        var next = _h.Create();
        await next.OnNavigatedToAsync();
        Assert.Equal(DeploymentHelperViewModel.RingsTab, next.SelectedTabIndex);
        Assert.Equal("Pilot-Rings", next.Rings.SelectedPlan?.Name);
    }

    [Theory]
    [InlineData(0, DeploymentHelperViewModel.DeployTab)]
    [InlineData(1, DeploymentHelperViewModel.HistoryTab)]
    [InlineData(2, DeploymentHelperViewModel.OptionsTab)]
    [InlineData(3, DeploymentHelperViewModel.RingsTab)]
    [InlineData(9, DeploymentHelperViewModel.DeployTab)]
    public void ASettingsFileOfTheThreeTabPageOpensTheSameTab(int stored, int tab)
    {
        Assert.Equal(tab, DeploymentHelperViewModel.TabOf(stored));
        if (stored <= 3) Assert.Equal(stored, DeploymentHelperViewModel.StoredTab(tab));
    }

    [Fact]
    public async Task TheLogAndTheSiteHistoryOfARunKeepCountsOnly()
    {
        var rings = await ExpandedAsync();
        await rings.ValidateCommand.ExecuteAsync(null);
        await rings.CreateRunCommand.ExecuteAsync(null);
        var entry = _h.History.Entries.Single();
        Assert.Equal(("Deployment Helper", "Create ring run", 3), (entry.Tool, entry.Action, entry.Counts["changed"]));
        var text = string.Join("\n", _h.Log.Entries.Select(e => e.Message));
        foreach (var name in new[] { "7-Zip", "Pilot", "Broad", "LAB000", "RUN-", "MCM" }) Assert.DoesNotContain(name, text);
        Assert.All(_h.Log.Entries, e => Assert.Equal(DeploymentShared.LogArea, e.Area));
    }

    // ---- Writer --------------------------------------------------------------------------------------

    [Fact]
    public async Task TheWriterHoldsBackARunWithARequiredRingWhenThePlanAskedNoSecondBox()
    {
        var rings = await ExpandedAsync();
        await rings.ValidateCommand.ExecuteAsync(null);
        var prepared = await _h.Rings.PrepareAsync(rings.BuildRunRequest()!, CancellationToken.None);
        var (plan, _) = RingText.BuildCreateRunPlan(prepared, _h.Clock);
        var forged = new RingRunPlan(plan!.Action, plan.Plan.Title, plan.Plan.ActionName, prepared with { RequiredConfirmed = true }, plan.Entries) { NeedsRequiredConfirmation = true };
        _h.Rings.InConfirmedRun = null;
        var writer = _h.RingWriter();
        var dialog = new ChangePlanViewModel(forged.Plan, writer.RunFor(forged));
        await dialog.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(_h.Rings.Applied);
        Assert.All(dialog.ResultRows, r => Assert.Equal(RingWriter.NotAcknowledgedText, r.Message));
    }

    [Fact]
    public void TheWriterSendsEachFlagOnlyAsThePlanAskedAndClearsThePartOneFlags()
    {
        var part = new DeploymentChange(Kind: DeploymentChangeKind.Create, Object: _h.SevenZip, Collection: new DeploymentCollection("LAB00010", "Pilot Workstations", "", 3),
            Options: null, DpGroups: [], CreateNowContentDistributing: true, RequiredConfirmed: true, BackdatedAcknowledged: true, Existing: null, Checks: [], Impact: [], CreatedId: null);
        var change = new RingChange(Kind: RingChangeKind.CreateRun, Plan: _h.PilotRings, Object: _h.SevenZip, Rows: [], PlanStart: null, RunId: null, RingIndex: null,
            RequiredConfirmed: true, BackdatedAcknowledged: true, CreateNowContentDistributing: true, RingChanges: [part], Impact: [], Checks: [], Fingerprint: null);
        var sent = RingWriter.Confirmed(change, required: false, backdated: false, createNow: false);
        Assert.False(sent.RequiredConfirmed || sent.BackdatedAcknowledged || sent.CreateNowContentDistributing);
        var inner = sent.RingChanges.Single();
        Assert.False(inner.RequiredConfirmed || inner.BackdatedAcknowledged || inner.CreateNowContentDistributing);
        Assert.True(RingWriter.Confirmed(change, required: true, backdated: false, createNow: true) is { RequiredConfirmed: true, BackdatedAcknowledged: false, CreateNowContentDistributing: true });
    }

    [Fact]
    public async Task TheWriterRefusesAConfirmedPlanThatIsNotItsPlan()
    {
        var rings = await ExpandedAsync();
        var prepared = await _h.Rings.PrepareAsync(rings.BuildRunRequest()!, CancellationToken.None);
        var (plan, _) = RingText.BuildCreateRunPlan(prepared, _h.Clock);
        var (other, _) = RingText.BuildCreateRunPlan(prepared, _h.Clock);
        ConfirmedChangePlan? confirmed = null;
        var dialog = new ChangePlanViewModel(other!.Plan, (c, _, _) => { confirmed = c; return Task.FromResult(new ChangePlanResult([])); }) { ExtraAcknowledged = true };
        await dialog.ConfirmCommand.ExecuteAsync(null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.RingWriter().ApplyAsync(confirmed!, plan!, new Progress<ChangePlanItemResult>(), CancellationToken.None));
        Assert.Empty(_h.Rings.Applied);
    }

    // ---- Options -------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRingFoldersAreSavedCheckedAndTheStaleLockAgeIsShown()
    {
        var page = await _h.OpenAsync(DeploymentHelperViewModel.OptionsTab);
        var folders = page.Options.RingFolders!;
        Assert.Equal(1, _h.Rings.ProbeCalls);
        Assert.Contains("A lock file can be made in the run folder.", folders.ProbeText);
        Assert.Contains("only the sessions that use this folder", folders.ProbeText);
        Assert.Equal("A lock file older than 2 hours is reported as stale. It is never removed automatically: remove it by hand when the message names a session that ended.",
            folders.StaleLockText);

        folders.RunsFolder = "relative\\runs";
        await folders.SaveFoldersCommand.ExecuteAsync(null);
        Assert.Equal(RingFolderOptionsViewModel.FolderNotRooted, folders.ProbeText);
        Assert.Null(_h.Saved?.RunsFolder is { Length: > 0 } ? _h.Saved : null);

        folders.RingsFolder = @"\\fs01\deploy$\Rings";
        folders.RunsFolder = @"\\fs01\deploy$\Rings\runs";
        _h.Rings.NextProbe = _h.Rings.NextProbe with { IsUnc = true, LockCreated = false };
        await folders.SaveFoldersCommand.ExecuteAsync(null);
        Assert.Equal((@"\\fs01\deploy$\Rings", @"\\fs01\deploy$\Rings\runs"), (_h.Saved!.RingsFolder, _h.Saved.RunsFolder));
        Assert.Contains("No lock file can be made in the run folder", folders.ProbeText);
        Assert.Contains("The run folder is a share", folders.ProbeText);

        _h.SaveSucceeds = false;
        folders.RingsFolder = @"D:\Rings";
        await folders.SaveFoldersCommand.ExecuteAsync(null);
        Assert.Equal(RingFolderOptionsViewModel.FoldersNotSaved, folders.ProbeText);
    }

    // ---- History ---------------------------------------------------------------------------------------

    private DeploymentAuditRecord RingRecord(string runId, int index, string ring, string deploymentId) =>
        _h.Record("Create", DeploymentObjectType.Application, "7-Zip", "LAB00010", deploymentId) with
        {
            Extra = [new("PlanName", "Pilot-Rings"), new("RingIndex", index.ToString(System.Globalization.CultureInfo.InvariantCulture)), new("RingName", ring), new("RunId", runId)]
        };

    [Fact]
    public async Task TheHistoryShowsTheRingFieldsAndFiltersByRun()
    {
        _h.Audit.Records.Add(_h.Record("Create", DeploymentObjectType.Application, "Notepad++", "LAB00011", "{DEP-1}"));
        _h.Audit.Records.Add(RingRecord("RUN-100", 1, "Pilot", "{DEP-2}"));
        _h.Audit.Records.Add(RingRecord("RUN-200", 2, "Early", "{DEP-3}"));
        var page = await _h.OpenAsync(DeploymentHelperViewModel.HistoryTab);
        var history = page.History;
        Assert.True(history.HasRingRecords);
        Assert.Equal(["All", "RUN-100", "RUN-200"], history.Runs);
        var ring = history.Rows.Single(r => r.RunId == "RUN-200");
        Assert.Equal(("Pilot-Rings", "2", "Early"), (ring.PlanName, ring.RingIndex, ring.RingName));
        Assert.Equal("", history.Rows.Single(r => r.Object.StartsWith("Notepad", StringComparison.Ordinal)).RunId);

        history.RunFilter = "RUN-100";
        Assert.Equal("{DEP-2}", history.Rows.Single().DeploymentId);
        history.RunFilter = "All";
        history.TextFilter = "Early";
        Assert.Equal("{DEP-3}", history.Rows.Single().DeploymentId);
    }

    [Fact]
    public async Task UndoFromHistoryWorksForARingRecordAsForASingleOne()
    {
        _h.Service.Deployments.Add(new DeploymentRecord(DeploymentId: "{0A1B2C3D-0000-4000-8000-000000000002}", Type: DeploymentObjectType.Application, ObjectId: "16777300", ObjectName: "7-Zip",
            CollectionId: "LAB00010", CollectionName: "Pilot Workstations", Purpose: "Available", AvailableTime: new DateTime(2026, 10, 19, 8, 0, 0), Deadline: null,
            Created: new DateTime(2026, 10, 19, 8, 0, 0), CreatedBy: @"LAB\admin"));
        _h.Audit.Records.Add(RingRecord("RUN-100", 1, "Pilot", "{0A1B2C3D-0000-4000-8000-000000000002}"));
        var page = await _h.OpenAsync(DeploymentHelperViewModel.HistoryTab);
        page.History.SelectedRow = page.History.Rows.Single();
        await page.History.RemoveDeploymentCommand.ExecuteAsync(null);
        Assert.Equal("Remove deployment", _h.Plans.Shown.Single().Items.Single().Action);
        Assert.Equal("{0A1B2C3D-0000-4000-8000-000000000002}", _h.Service.Applied.Single().Single().Existing!.DeploymentId);
        Assert.Empty(_h.Service.Deployments);
    }

    [Fact]
    public async Task TheHistoryHidesTheRingColumnsWhenNoRecordIsARingRecord()
    {
        _h.Audit.Records.Add(_h.Record("Create", DeploymentObjectType.Application, "Notepad++", "LAB00011", "{DEP-1}"));
        var page = await _h.OpenAsync(DeploymentHelperViewModel.HistoryTab);
        Assert.False(page.History.HasRingRecords);
        Assert.Equal(["All"], page.History.Runs);
    }
}
