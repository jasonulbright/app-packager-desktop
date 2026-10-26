using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using VM = ApplicationPackagerUI.ViewModels.Pages.DpContentManagerViewModel;

namespace ApplicationPackagerEngine.Tests.DpContent;

/// <summary>The DP Content Manager page: final results, the plan text from the prepared change, the Undo, the history and the pair list defaults.</summary>
public sealed class PageReviewTests
{
    private const string Cm01 = StubDpContentSite.Nal;

    private static async Task<(ChangePlanResult Result, DpContentPlan Plan, RecordingSiteHistory History, List<string> Steps)> RunWithTheRealService(
        DpContentHarness h, DpContentChangeKind kind, string packageId)
    {
        var steps = new List<string>();
        var snapshot = await h.Service.LoadAsync(new Progress<string>(), CancellationToken.None);
        var pair = snapshot.Pairs.Single(p => p.PackageId == packageId && p.NalPath == Cm01);
        var prepared = await h.Service.PrepareAsync(new DpContentChange(Kind: kind, Pairs: [pair], RemovalConfirmed: false, LastCopyAcknowledged: false, Impact: [], Checks: [],
            Fingerprint: null), CancellationToken.None);
        var (plan, problem) = DpContentText.BuildPlan(prepared, new DpContentIndex(snapshot));
        Assert.Null(problem);
        var history = new RecordingSiteHistory();
        var writer = new DpContentWriter(h.Service, history, new RecordingMaintenanceLog(), () => "MCM");
        var dialog = new ChangePlanViewModel(plan!.Plan, writer.RunFor(plan)) { DestructiveAcknowledged = true, ExtraAcknowledged = true, SecondAcknowledged = true };
        dialog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChangePlanViewModel.StepText)) lock (steps) steps.Add(dialog.StepText);
        };
        ScriptedPlanDialogs.Wait(dialog.ConfirmCommand.ExecuteAsync(null));
        Assert.True(dialog.Outcome.Confirmed);
        return (dialog.Outcome.Result!, plan, history, steps);
    }

    [Fact]
    public async Task TheFinalReadBackResultOfTheRealServiceReplacesItsInterimRequested()
    {
        using var h = new DpContentHarness();
        h.Site.SetOption("RefreshError:MCM000A7", true);
        var (result, plan, history, steps) = await RunWithTheRealService(h, DpContentChangeKind.Redistribute, "MCM000A7");

        Assert.Contains("Running: part 1 of 1 done.", steps);
        var row = Assert.Single(result.Items);
        Assert.Equal("Failed", row.OutcomeText);
        Assert.Contains("status message 2306", row.Message);
        Assert.Equal("1 failed.", result.Headline);
        var counts = history.Entries.Single().Counts!;
        Assert.Equal(1, counts["failed"]);
        Assert.Equal(0, counts["requested"]);
        Assert.Equal(SiteOperationOutcome.Failed, history.Entries.Single().Outcome);
        Assert.All(plan.Outcomes, o => Assert.True(o.Final));
    }

    [Fact]
    public async Task ARemovalWhoseFinalResultIsFailedShowsFailedAndOffersNoUndo()
    {
        using var h = new DpContentHarness();
        h.Site.SetOption("RemoveFails:MCM000A6", true);
        var (result, plan, history, _) = await RunWithTheRealService(h, DpContentChangeKind.Remove, "MCM000A6");

        Assert.Equal("Failed", Assert.Single(result.Items).OutcomeText);
        Assert.Contains("Removal failed", result.Items[0].Message);
        Assert.Null(plan.UndoChange());
        Assert.Null(result.Note);
        Assert.Equal(1, history.Entries.Single().Counts!["failed"]);
    }

    private static DpContentPageHarness Harness() => new();

    [Fact]
    public void ThePlanTakesTheRemainingDpsAndTheDeploymentsFromThePreparedImpact()
    {
        var h = Harness();
        var prepared = new DpContentChange(Kind: DpContentChangeKind.Remove, Pairs: [h.SevenZipOnDp01], RemovalConfirmed: false, LastCopyAcknowledged: false,
            Impact: [], Checks: [], Fingerprint: "fp")
        {
            ContentImpact = [new DpContentImpact(PackageId: "LAB00001", Name: "7-Zip 26.04", ContentType: "Application", Pairs: 1, DpsNow: 3, DpsRemaining: 0, DeploymentCount: 4,
                DeploymentNote: "Counted: application deployments.")],
            NeedsLastCopyAcknowledgement = true
        };
        var (plan, problem) = DpContentText.BuildPlan(prepared, new DpContentIndex(h.Service.Snapshot()));

        Assert.Null(problem);
        var detail = plan!.Plan.Items.Single().Detail;
        Assert.Contains("No DP holds the content after this plan.", detail);
        Assert.Contains("Deployments that use the content: 4.", detail);
        Assert.Equal("Remove 1 pair: 1 content will have no DP left.", plan.Plan.ExtraConfirmation);
        Assert.NotNull(plan.Plan.SecondConfirmation);
    }

    [Fact]
    public void WithoutAPreparedImpactAPairThatFailedToInstallIsNotCountedAsACopy()
    {
        var h = Harness();
        var prepared = new DpContentChange(Kind: DpContentChangeKind.Remove, Pairs: [h.SevenZipOnDp01], RemovalConfirmed: false, LastCopyAcknowledged: false,
            Impact: [], Checks: [], Fingerprint: "fp");
        var (plan, _) = DpContentText.BuildPlan(prepared, new DpContentIndex(h.Service.Snapshot()));

        Assert.Contains("No DP holds the content after this plan.", plan!.Plan.Items.Single().Detail);
        Assert.Equal("Remove 1 pair: 1 content will have no DP left.", plan.Plan.ExtraConfirmation);
    }

    private static DpContentChange WithDropped(DpContentPageHarness h, IReadOnlyList<PairRow> pairs) =>
        new DpContentChange(Kind: DpContentChangeKind.Redistribute, Pairs: pairs, RemovalConfirmed: false, LastCopyAcknowledged: false,
            Impact: ["Redistribute 1 pair on 1 distribution point."],
            Checks: [new PreflightCheck(Name: "Pairs", Result: "Pass", Message: "1 pair on 1 distribution point."), new PreflightCheck(Name: "Dropped", Result: "Warn", Message: "1 pair was left out of the plan.")],
            Fingerprint: "fp")
        {
            Dropped = [new DpContentPairOutcome(Pair: h.BootOnDp01, Result: "Skipped", Message: "Skipped: the state changed from Removal failed to Installed since the list was loaded.", StateAfter: 0)]
        };

    [Fact]
    public void APairTheServiceLeftOutIsNamedWithItsReasonAndTheChecksThatNeedAttentionComeFirst()
    {
        var h = Harness();
        var (plan, _) = DpContentText.BuildPlan(WithDropped(h, [h.SevenZipOnDp02]), new DpContentIndex(h.Service.Snapshot()));

        var notes = plan!.Plan.Notes;
        Assert.Equal("Dropped: Warn. 1 pair was left out of the plan.", notes[0]);
        Assert.Equal("Left out: Boot image (x64) (LAB00003) on DP01.lab.local: Skipped: the state changed from Removal failed to Installed since the list was loaded.", notes[1]);
        Assert.Equal("Redistribute 1 pair on 1 distribution point.", notes[2]);
        Assert.Equal("Pairs: Pass. 1 pair on 1 distribution point.", notes[3]);
    }

    [Fact]
    public void APlanWithNoPairLeftNamesThePairsTheServiceLeftOut()
    {
        var h = Harness();
        var (plan, problem) = DpContentText.BuildPlan(WithDropped(h, []), new DpContentIndex(h.Service.Snapshot()));

        Assert.Null(plan);
        Assert.Contains("Left out: Boot image (x64) (LAB00003) on DP01.lab.local: Skipped: the state changed", problem);
    }

    private static async Task<VM> ContentTab(DpContentPageHarness h, ContentRow content)
    {
        var vm = await h.OpenAsync(VM.ContentTab);
        vm.SelectedContent = vm.ContentRows.Single(r => r.PackageId == content.PackageId);
        return vm;
    }

    [Fact]
    public async Task AChangeWithoutAnUndoKeepsTheUndoOfTheLastRemoval()
    {
        var h = Harness();
        var vm = await ContentTab(h, h.SevenZip);
        h.Dialogs.OnPairs = p =>
        {
            p.ClearCommand.Execute(null);
            p.Rows.Single(r => r.Pair.NalPath == h.Dp01.NalPath).IsChecked = true;
        };
        await vm.RemoveFromDpCommand.ExecuteAsync(null);
        Assert.True(vm.HasUndo);

        h.Dialogs.OnPairs = null;
        await vm.ValidateCommand.ExecuteAsync(null);
        Assert.Equal(2, h.Service.Applied.Count);
        Assert.Equal(DpContentChangeKind.Validate, h.Service.Applied[1].Single().Kind);
        Assert.True(vm.HasUndo);
        Assert.Equal([h.SevenZipOnDp01.NalPath], vm.LastUndo!.Pairs.Select(p => p.NalPath));
    }

    private static DpContentAuditRecord Record(string action, DateTime when, string runId) =>
        new(Timestamp: when, User: @"LAB\admin", Event: action, Phase: "Finished", Result: "Succeeded", RunId: runId, Chunk: 1,
            Pairs: [new DpContentAuditPair(PackageId: "LAB00001", ContentName: "7-Zip 26.04", DpName: "DP01.lab.local", NalPath: "nal1", StateBefore: 0, Result: "Confirmed")],
            Message: "Done.");

    [Fact]
    public async Task TheHistoryTabIsReadAgainAfterARun()
    {
        var h = Harness();
        h.Audit.Records.Add(Record("Remove", new DateTime(2026, 10, 23, 8, 0, 0), "run-1"));
        var vm = await h.OpenAsync(VM.HistoryTab);
        Assert.Single(vm.HistoryRows);

        vm.SelectedTabIndex = VM.ContentTab;
        await vm.EnsureTabLoadedAsync();
        vm.SelectedContent = vm.ContentRows.Single(r => r.PackageId == h.SevenZip.PackageId);
        var added = false;
        h.Service.AfterChunk = _ =>
        {
            if (added) return;
            added = true;
            h.Audit.Records.Add(Record("Validate", new DateTime(2026, 10, 23, 9, 0, 0), "run-2"));
        };
        await vm.ValidateCommand.ExecuteAsync(null);

        Assert.Equal(["Validate", "Remove"], vm.HistoryRows.Select(r => r.Action));
    }

    [Fact]
    public async Task ARemovalWithNoSelectedDpChecksNoPairAndAValidationChecksEvery()
    {
        var h = Harness();
        var vm = await ContentTab(h, h.SevenZip);
        int? checkedCount = null;
        h.Dialogs.OnPairs = p => checkedCount = p.CheckedCount;
        await vm.RemoveFromDpCommand.ExecuteAsync(null);
        Assert.Equal(0, checkedCount);
        Assert.Empty(h.Service.Applied);
        Assert.Equal(VM.NotConfirmed, vm.StatusMessage);

        await vm.ValidateCommand.ExecuteAsync(null);
        Assert.Equal(3, checkedCount);
    }

    [Fact]
    public async Task TheSelectedHistoryRowStaysSelectedAfterARefresh()
    {
        var h = Harness();
        h.Audit.Records.Add(Record("Remove", new DateTime(2026, 10, 23, 8, 0, 0), "run-1"));
        h.Audit.Records.Add(Record("Validate", new DateTime(2026, 10, 23, 9, 0, 0), "run-2"));
        var vm = await h.OpenAsync(VM.HistoryTab);
        vm.SelectedHistory = vm.HistoryRows.Single(r => r.Record.RunId == "run-1");

        await vm.RefreshHistoryCommand.ExecuteAsync(null);
        Assert.Equal("run-1", vm.SelectedHistory?.Record.RunId);
    }

    [Theory]
    [InlineData(VM.MaxHistoryRecords, false)]
    [InlineData(VM.MaxHistoryRecords + 1, true)]
    public async Task TheHistoryIsCappedOnlyWhenTheLogHoldsMoreRecordsThanTheList(int records, bool capped)
    {
        var h = Harness();
        var start = new DateTime(2026, 10, 1, 0, 0, 0);
        for (var i = 0; i < records; i++) h.Audit.Records.Add(Record("Validate", start.AddMinutes(i), "run-" + i));
        var vm = await h.OpenAsync(VM.HistoryTab);

        Assert.Equal(capped, vm.IsHistoryCapped);
        Assert.Equal(VM.MaxHistoryRecords, vm.HistoryRows.Count);
        Assert.Equal("run-" + (records - 1), vm.HistoryRows[0].Record.RunId);
    }

    [Fact]
    public void AContentWithoutKnownCountsShowsUnknown()
    {
        var row = new ContentGridRow(new ContentRow(PackageId: "LAB00009", Name: "Unread", ContentType: "Package", SizeMb: 1, DpsTargeted: 0, Installed: 0, InProgress: 0,
            Failed: 0, PercentOk: 0) { CountsKnown = false });

        Assert.Equal(DpContentLabels.UnknownLabel, row.Status);
        Assert.Equal([DpContentLabels.UnknownText], new[] { row.Dps, row.Installed, row.InProgress, row.Failed, row.PercentOk }.Distinct());
    }
}
