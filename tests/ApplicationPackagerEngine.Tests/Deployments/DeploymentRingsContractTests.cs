using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.Deployments.Rings;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>
/// The Rings tab against the ring service contract: Abandon ring, unreadable rings, the plan file stamp,
/// the ring audit events in History, the shown success rate and a create that made nothing.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DeploymentRingsContractTests
{
    private readonly DeploymentPageHarness _h = new();

    private DeploymentHelperViewModel? _page;

    private async Task<DeploymentRingsViewModel> RingsAsync() => (_page = await _h.OpenAsync(DeploymentHelperViewModel.RingsTab)).Rings;

    private async Task<DeploymentRingsViewModel> SelectedAsync(string runId = "RUN-100")
    {
        var rings = await RingsAsync();
        rings.SelectedRun = rings.Runs.Single(r => r.RunId == runId);
        await rings.CountsTask;
        return rings;
    }

    private ChangePlanViewModel Dialog => _h.Plans.LastViewModel!;

    private RingRun Replace(RingRun run)
    {
        lock (_h.Rings.Runs) _h.Rings.Runs[_h.Rings.Runs.FindIndex(r => r.RunId == run.RunId)] = run;
        return run;
    }

    // ---- Abandon ring ---------------------------------------------------------------------------

    private RingRun CreatingRun() =>
        _h.Rings.AddRun("RUN-100", _h.SevenZip, ("Pilot", "LAB00010", "Created", null, "Available"), ("Early", "LAB00011", "Creating", null, "Available"),
            ("Broad", "LAB00012", "Held", null, "Available"));

    [Fact]
    public async Task AbandonRingIsOfferedOnlyForTheSelectedRingInCreating()
    {
        CreatingRun();
        var rings = await SelectedAsync();
        Assert.False(rings.AbandonRingCommand.CanExecute(null));
        rings.SelectedRunRing = rings.RunRings[0];
        Assert.False(rings.AbandonRingCommand.CanExecute(null));
        rings.SelectedRunRing = rings.RunRings[2];
        Assert.False(rings.AbandonRingCommand.CanExecute(null));
        rings.SelectedRunRing = rings.RunRings[1];
        Assert.True(rings.AbandonRingCommand.CanExecute(null));
        _page!.Shared.IsWriting = true;
        Assert.False(rings.AbandonRingCommand.CanExecute(null));
    }

    [Fact]
    public async Task AbandonRingShowsItsOwnPlanAndBoxSendsAbandonRingAndTheRingIsHeldAfterTheReload()
    {
        CreatingRun();
        var rings = await SelectedAsync();
        rings.SelectedRunRing = rings.RunRings[1];
        await rings.AbandonRingCommand.ExecuteAsync(null);

        var plan = _h.Plans.Shown.Single();
        Assert.Equal("Abandon ring", plan.Items.Single().Action);
        Assert.Equal("Ring Early: Collection LAB00011 (LAB00011)", plan.Items.Single().Target);
        Assert.Contains("may exist", plan.Items.Single().Detail);
        Assert.StartsWith("I understand that a deployment of ring Early may exist in Collection LAB00011 (LAB00011)", plan.ExtraConfirmation);
        var sent = _h.Rings.Applied.Single().Single();
        Assert.Equal((RingChangeKind.AbandonRing, "RUN-100", 2, false), (sent.Kind, sent.RunId, sent.RingIndex, sent.RequiredConfirmed));
        Assert.True(Dialog.ResultRows.Single().Succeeded);
        Assert.Equal(["Created", "Held", "Held"], rings.RunRings.Select(r => r.Status));
        Assert.Equal("1 created, 2 held", rings.Runs.Single().Rings);
    }

    private static readonly FixedClock LabClock = new(new DateTimeOffset(DeploymentHarness.UtcNow), DeploymentHarness.Local);

    private static RingWriter Writer(RingHarness h) =>
        new(h.Service, new RecordingSiteHistory(), new RecordingMaintenanceLog(), () => "LAB", _ => Task.FromResult(true));

    private static async Task<ChangePlanViewModel> ConfirmAsync(RingWriter writer, RingRunPlan plan, bool acknowledge = true)
    {
        var dialog = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan)) { ExtraAcknowledged = acknowledge, DestructiveAcknowledged = true };
        await dialog.ConfirmCommand.ExecuteAsync(null);
        return dialog;
    }

    private static RingPlan HeldPackagePlan => RingHarness.ThreeRings(hold: true) with { Rings = [.. RingHarness.ThreeRings().Rings.Select(r => r with { SuccessThresholdPercent = null })] };

    /// <summary>A package run whose ring 1 is Creating, with a deployment that holds its values but whose status message has not arrived.</summary>
    private static async Task<RingRun> UnprovenRunAsync(RingHarness h)
    {
        var plan = HeldPackagePlan;
        var package = DeploymentHarness.Package;
        h.Site.SetOption("NoStatus", true);
        var run = h.Runs.Create(new RingRun(RunId: Guid.NewGuid().ToString(), FileName: "", PlanName: plan.Name, HoldLaterRings: true, TimeBasedOn: "LocalTime",
            ObjectType: DeploymentObjectType.Package, ObjectId: package.Id, ObjectName: package.Name, Program: package.Program, CreatedBy: RingHarness.Account, CreatedUtc: h.UtcNow,
            Rings: [.. RingPlanRules.Expand(plan, RingHarness.Start, DeploymentHarness.Local).Select(r => new RingRunRing(Index: r.Index, Name: r.Name, CollectionId: r.CollectionId,
                CollectionName: "", Purpose: r.Purpose, AvailableTime: r.AvailableTime, Deadline: r.Deadline, Status: r.Index == 1 ? RingStatuses.Creating : RingStatuses.Held,
                CreatingAt: r.Index == 1 ? DeploymentHarness.LocalNow : null, DeploymentId: null, ShiftedMinutes: 0, DpGroup: "", SuccessThresholdPercent: null)
            { Definition = r.Definition, CreatingAtUtc = r.Index == 1 ? h.UtcNow : null, CreatingBy = r.Index == 1 ? RingHarness.Account : null })], IsClosed: false), package.Id);
        var options = RingPlanRules.Options(DeploymentObjectType.Package, plan.Rings[0], RingHarness.Start, null, false);
        var made = (await h.Deployments.ApplyAsync([(await h.Deployments.PrepareAsync(DeploymentHarness.Create(package, options), CancellationToken.None))
            with { Ring = new DeploymentRingContext(plan.Name, 1, "QA", run.RunId) }], new Progress<DeploymentOutcome>(), CancellationToken.None)).Single();
        Assert.True(made.Succeeded, made.Message);
        return run;
    }

    [Fact]
    public async Task TheWriterAbandonsACreatingRingOverTheRealServiceOnlyWithItsBox()
    {
        using var h = new RingHarness();
        var run = await UnprovenRunAsync(h);
        var prepared = await h.PrepareAsync(RingHarness.Request(RingChangeKind.AbandonRing, run.RunId, 1));
        var (plan, problem) = RingText.BuildAbandonPlan(prepared, run.RunId);
        Assert.Null(problem);
        Assert.NotNull(plan!.Plan.ExtraConfirmation);
        Assert.Contains("may exist", plan.Plan.Items.Single().Detail);

        var forged = new RingRunPlan(plan.Action, plan.Plan.Title, plan.Plan.ActionName, prepared, plan.Entries) { RequiresExtraConfirmation = true };
        var refused = await ConfirmAsync(Writer(h), forged);
        Assert.Equal(RingWriter.UnconfirmedText, refused.ResultRows.Single().Message);
        Assert.Equal(RingStatuses.Creating, h.Run(run.RunId).Rings[0].Status);

        var dialog = await ConfirmAsync(Writer(h), plan);
        Assert.True(dialog.ResultRows.Single().Succeeded, dialog.ResultRows.Single().Message);
        Assert.Equal(RingStatuses.Held, h.Run(run.RunId).Rings[0].Status);
        Assert.Single(h.Records, r => r.Event == AuditEvents.Abandon);
        Assert.Single(h.Site.List("Adverts"));
    }

    [Fact]
    public async Task TheWriterReportsAnAbandonOfARingThatIsNotCreatingAsRefused()
    {
        using var h = new RingHarness();
        var created = await h.CreateRunAsync(HeldPackagePlan, DeploymentHarness.Package);
        var prepared = await h.PrepareAsync(RingHarness.Request(RingChangeKind.AbandonRing, created.Run!.RunId, 2));
        var (plan, problem) = RingText.BuildAbandonPlan(prepared, created.Run.RunId);
        Assert.Null(plan);
        Assert.StartsWith("Ring Pilot cannot be abandoned: ", problem);
    }

    // ---- Unreadable rings ------------------------------------------------------------------------

    private const string Problem = "Unreadable ring 2 'Early': the threshold 12.5 is not a whole number.";

    [Fact]
    public async Task AnUnreadableRingIsShownAndBlocksPromoteAndUndoRunWithTheReason()
    {
        var run = _h.Rings.AddRun("RUN-100", _h.SevenZip, ("Pilot", "LAB00010", "Created", null, "Available"), ("Early", "LAB00011", "Held", null, "Available"));
        Replace(run with { Problems = [Problem], Rings = [run.Rings[0], run.Rings[1] with { Problem = Problem }] });
        var rings = await SelectedAsync();

        Assert.Contains("1 problem", rings.Runs.Single().Rings);
        Assert.Contains(Problem, rings.RunProblems);
        Assert.True(rings.HasRunProblems);
        Assert.False(rings.PromoteRingCommand.CanExecute(null));
        Assert.Contains(Problem, rings.PromoteReason);
        Assert.False(rings.UndoRunCommand.CanExecute(null));
        rings.SelectedRunRing = rings.RunRings[1];
        Assert.StartsWith(Problem, rings.RunRings[1].Detail);
        Assert.Equal("Held (unreadable)", rings.RunRings[1].Status);

        await rings.UndoRunCommand.ExecuteAsync(null);
        Assert.Empty(_h.Plans.Shown);
        Assert.Contains(Problem, rings.RunsMessage);
    }

    [Fact]
    public async Task ARunWithoutProblemsShowsNoProblemLine()
    {
        _h.Rings.AddRun("RUN-100", _h.SevenZip, ("Pilot", "LAB00010", "Created", null, "Available"), ("Early", "LAB00011", "Held", null, "Available"));
        var rings = await SelectedAsync();
        Assert.Equal("", rings.RunProblems);
        Assert.False(rings.HasRunProblems);
        Assert.True(rings.UndoRunCommand.CanExecute(null));
    }

    [Fact]
    public void AStaleLockSaysToCheckTheOtherSessionBeforeTheLockIsRemoved()
    {
        var stale = DeploymentRingsViewModel.LockText(new RingLockInfo(Path: @"\\fs01\runs\RUN-100.lock", Owner: @"LAB\other on PC2", Age: TimeSpan.FromHours(3), Stale: true));
        Assert.Contains("Another session may still be running: check before you remove the lock", stale);
        Assert.Contains("never removed automatically", stale);
    }

    // ---- The plan file stamp ---------------------------------------------------------------------

    private static RingPlan LabPlan => new(Name: "Lab-Rings", Description: "Two rings.", HoldLaterRings: false, TimeBasedOn: "LocalTime",
        Rings: [FakeRingService.Ring("Pilot", "LAB00010", "Pilot Workstations"), FakeRingService.Ring("Early", "LAB00011", "Server Group A", available: 2)], IsSeed: false);

    private async Task<(DeploymentRingsViewModel Rings, RingPlanStore Other, string Folder)> StoredPlanAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "apd-ring-stamp-" + Guid.NewGuid().ToString("N"));
        var mine = new RingPlanStore(() => folder);
        mine.Save(LabPlan, replace: false);
        _h.Rings.PlanStore = mine;
        var rings = await RingsAsync();
        rings.SelectedPlan = rings.Plans.Single(p => p.Name == LabPlan.Name);
        return (rings, new RingPlanStore(() => folder), folder);
    }

    private RingPlanEditorViewModel Edit(DeploymentRingsViewModel rings)
    {
        RingPlanEditorViewModel? editor = null;
        _h.Dialogs.RingEditor = e => editor = e;
        rings.EditPlanCommand.Execute(null);
        return editor!;
    }

    [Fact]
    public async Task APlanAnotherWriterChangedIsNotOverwrittenAndTheEditorKeepsTheEdits()
    {
        var (rings, other, folder) = await StoredPlanAsync();
        try
        {
            var editor = Edit(rings);
            var theirs = other.List().Single(p => p.Name == LabPlan.Name);
            other.Save(theirs with { Description = "Changed in another session." }, replace: true);

            editor.Rings[0].Name = "Pilot first";
            await editor.SaveCommand.ExecuteAsync(null);
            Assert.False(editor.Saved);
            Assert.Equal(DeploymentRingsViewModel.PlanFileChanged, editor.ValidationMessage);
            Assert.Equal("Pilot first", editor.Rings[0].Name);
            Assert.True(editor.HasChanges);
            Assert.Equal("Changed in another session.", other.List().Single(p => p.Name == LabPlan.Name).Description);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task ASavedPlanCanBeEditedAndSavedAgainWithTheStampItCameBackWith()
    {
        var (rings, other, folder) = await StoredPlanAsync();
        try
        {
            var editor = Edit(rings);
            editor.Description = "First edit.";
            await editor.SaveCommand.ExecuteAsync(null);
            Assert.True(editor.Saved, editor.ValidationMessage);

            var again = Edit(rings);
            again.Description = "Second edit.";
            await again.SaveCommand.ExecuteAsync(null);
            Assert.True(again.Saved, again.ValidationMessage);
            Assert.Equal("Second edit.", other.List().Single(p => p.Name == LabPlan.Name).Description);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task ANewPlanWithATakenNameIsRefusedWithAPlainMessage()
    {
        var (rings, other, folder) = await StoredPlanAsync();
        try
        {
            var editor = Edit(rings);
            other.Save(LabPlan with { Name = "Lab-Rings 2" }, replace: false);
            editor.Name = "Lab-Rings 2";
            await editor.SaveCommand.ExecuteAsync(null);
            Assert.False(editor.Saved);
            Assert.NotEqual("", editor.ValidationMessage);
            Assert.DoesNotContain("Exception", editor.ValidationMessage, StringComparison.Ordinal);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    // ---- History: the ring audit events -----------------------------------------------------------

    private DeploymentAuditRecord RingEvent(string auditEvent, string runId, int? index, string ring) =>
        _h.Record("Create", DeploymentObjectType.Application, "7-Zip", "LAB00010", "") with
        {
            Event = auditEvent,
            Extra = index is { } i
                ? [new("PlanName", "Pilot-Rings"), new("RingIndex", i.ToString(System.Globalization.CultureInfo.InvariantCulture)), new("RingName", ring), new("RunId", runId)]
                : [new("PlanName", "Pilot-Rings"), new("RunId", runId)]
        };

    [Fact]
    public async Task TheHistoryNamesCloseCancelRingAndAbandonAndFiltersByThem()
    {
        _h.Audit.Records.Add(RingEvent(AuditEvents.Close, "RUN-100", null, ""));
        _h.Audit.Records.Add(RingEvent(AuditEvents.CancelRing, "RUN-100", 3, "Broad"));
        _h.Audit.Records.Add(RingEvent(AuditEvents.Abandon, "RUN-200", 2, "Early"));
        var history = (await _h.OpenAsync(DeploymentHelperViewModel.HistoryTab)).History;
        Assert.Equal(["All", "Create", "Distribute", "Undo", "Close run", "Cancel ring", "Abandon ring"], history.Events);
        Assert.True(history.HasRingRecords);

        var close = history.Rows.Single(r => r.Event == "Close run");
        Assert.Equal(("Pilot-Rings", "", "", "RUN-100"), (close.PlanName, close.RingIndex, close.RingName, close.RunId));
        Assert.False(close.CanUndo);
        var cancel = history.Rows.Single(r => r.Event == "Cancel ring");
        Assert.Equal(("3", "Broad"), (cancel.RingIndex, cancel.RingName));

        history.EventFilter = "Abandon ring";
        var abandon = history.Rows.Single();
        Assert.Equal(("2", "Early", "RUN-200"), (abandon.RingIndex, abandon.RingName, abandon.RunId));
        history.EventFilter = "Close run";
        Assert.Equal("RUN-100", history.Rows.Single().RunId);
    }

    // ---- The shown success rate --------------------------------------------------------------------

    [Fact]
    public async Task ARateJustBelowAWholeThresholdIsNeverShownAtTheThreshold()
    {
        _h.Rings.AddRun("RUN-100", _h.SevenZip, ("Pilot", "LAB00010", "Created", 100, "Available"), ("Early", "LAB00011", "Held", null, "Available"));
        _h.Rings.Counts["RUN-100"] = [new RingLiveCounts(Index: 1, DeploymentId: "MCM20000", State: "Summarized", Targeted: 2001, Success: 2000, Error: 1, InProgress: 0, Unknown: 0,
            CountsKnown: true, Message: "")];
        var rings = await SelectedAsync();
        Assert.False(rings.PromoteRingCommand.CanExecute(null));
        Assert.Contains("99.9 %", rings.PromoteReason);
        Assert.DoesNotContain("100 % success", rings.PromoteReason);
        Assert.Equal("99.9 %", rings.RunRings[0].SuccessRate);
    }

    [Fact]
    public void ARateWithoutThresholdIsNeverShownAsAHundredBeforeEveryClientSucceeded()
    {
        var counts = new RingLiveCounts(Index: 1, DeploymentId: "MCM20000", State: "Summarized", Targeted: 2001, Success: 2000, Error: 1, InProgress: 0, Unknown: 0, CountsKnown: true, Message: "");
        Assert.Equal(99.9, RingText.ShownRate(counts, null));
        Assert.Equal(100, RingText.ShownRate(counts with { Success = 2001, Error = 0 }, null));
        Assert.Null(RingText.ShownRate(counts with { CountsKnown = false }, null));
    }

    // ---- A create that made nothing ------------------------------------------------------------------

    [Fact]
    public async Task ARingCreateThatMadeNothingIsShownAsNothingCreatedAndTheRingIsHeldAgain()
    {
        using var h = new RingHarness();
        var plan = RingHarness.ThreeRings();
        var prepared = await h.PrepareAsync(RingHarness.CreateRun(plan, DeploymentHarness.App, await h.ExpandAsync(plan, DeploymentHarness.App)));
        var (runPlan, _) = RingText.BuildCreateRunPlan(prepared, LabClock);
        h.Site.Fail("create:Application");
        var dialog = await ConfirmAsync(Writer(h), runPlan!);
        var first = dialog.ResultRows[0];
        Assert.True(first.Skipped);
        Assert.StartsWith(RingWriter.NothingCreatedText, first.Message);
        Assert.Empty(h.Site.List("AppAssignments"));
        var run = (await h.Service.ListRunsAsync(false, CancellationToken.None)).Single();
        Assert.Equal(RingStatuses.Held, run.Rings[0].Status);
    }

    [Fact]
    public async Task ANotCreatedRingOfThePageReturnsToHeldInTheGridAfterTheReload()
    {
        var rings = await RingsAsync();
        rings.SelectedObject = rings.Objects.Single(o => o.Object.Id == _h.SevenZip.Id);
        rings.SelectedPlan = rings.Plans.Single(p => p.Name == _h.PilotRings.Name);
        rings.HoldLaterRings = true;
        await rings.ExpandCommand.ExecuteAsync(null);
        _h.Rings.NotCreatedRing = row => row.Index == 1 ? "ConfigMgr did not create the deployment of 7-Zip to Pilot Workstations. The log has the details." : null;
        await rings.CreateRunCommand.ExecuteAsync(null);
        var row = Dialog.ResultRows[0];
        Assert.True(row.Skipped);
        Assert.Equal($"{RingWriter.NothingCreatedText} ConfigMgr did not create the deployment of 7-Zip to Pilot Workstations. The log has the details.", row.Message);
        rings.SelectedRun = rings.Runs.Single();
        Assert.All(rings.RunRings, r => Assert.Equal("Held", r.Status));
    }
}
