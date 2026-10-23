using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using VM = ApplicationPackagerUI.ViewModels.Pages.DpContentManagerViewModel;

namespace ApplicationPackagerEngine.Tests.DpContent;

/// <summary>The DP Content Manager page without a window, over the fake service: every flow of the brief.</summary>
public sealed class PageViewModelTests
{
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task TheConstructorReadsNothingAndTheFirstVisitReadsTheSiteOnce()
    {
        var h = new DpContentPageHarness();
        var vm = h.Create();
        Assert.Equal(0, h.Service.LoadCalls);
        Assert.Empty(vm.DpRows);
        await vm.OnNavigatedToAsync();
        Assert.Equal(1, h.Service.LoadCalls);
        Assert.Equal(3, vm.DpRows.Count);
        Assert.Equal(3, vm.ContentRows.Count);
        Assert.Equal(3, vm.IssueRows.Count);
        Assert.Equal("3 DPs, 3 contents, 6 pairs, 3 status issues.", vm.LoadSummary);
        vm.SelectedTabIndex = VM.ContentTab;
        await vm.EnsureTabLoadedAsync();
        vm.SelectedDp = vm.DpRows[0];
        vm.SelectedContent = vm.ContentRows[0];
        Assert.Equal(1, h.Service.LoadCalls);
    }

    [Fact]
    public async Task APageWithoutASiteShowsOnePlainMessageAndReadsNothing()
    {
        var h = new DpContentPageHarness();
        h.Settings.SiteCode = "";
        var vm = await h.OpenAsync();
        Assert.Equal(0, h.Service.LoadCalls);
        Assert.True(vm.HasConnectionMessage);
        Assert.False(vm.IsLoaded);
    }

    [Fact]
    public async Task CancelStopsTheReadAndSaysSo()
    {
        var h = new DpContentPageHarness();
        h.Service.LoadGate = new TaskCompletionSource();
        var vm = h.Create();
        var visit = vm.OnNavigatedToAsync();
        await WaitUntil(() => vm.IsLoading);
        Assert.True(vm.CancelLoadCommand.CanExecute(null));
        Assert.NotEmpty(vm.LoadProgress);
        vm.CancelLoadCommand.Execute(null);
        await visit;
        Assert.False(vm.IsLoading);
        Assert.Equal(VM.ReadCancelled, vm.StatusMessage);
        Assert.Empty(vm.DpRows);
    }

    [Fact]
    public async Task AReadThatEndsAfterTheOperatorLeftIsDiscardedAndTheDataIsReleased()
    {
        var h = new DpContentPageHarness();
        var vm = await h.OpenAsync();
        Assert.True(vm.IsLoaded);
        h.Service.LoadGate = new TaskCompletionSource();
        var refresh = vm.RefreshCommand.ExecuteAsync(null);
        await WaitUntil(() => h.Service.LoadCalls == 2);
        await vm.OnNavigatedFromAsync();
        Assert.True(h.Service.LoadTokens[^1].IsCancellationRequested);
        Assert.False(vm.IsLoaded);
        Assert.Empty(vm.DpRows);
        Assert.Empty(vm.IssueRows);
        h.Service.LoadGate.SetResult();
        await refresh;
        Assert.Empty(vm.DpRows);
        Assert.Equal("", vm.LoadSummary);
    }

    [Fact]
    public async Task ASecondReadReplacesTheFirstAndTheFirstResultIsDiscarded()
    {
        var h = new DpContentPageHarness();
        var vm = await h.OpenAsync();
        h.Service.LoadGate = new TaskCompletionSource();
        var first = vm.LoadAsync();
        Assert.Same(first, vm.LoadAsync());
        h.Service.Pairs.Clear();
        h.Service.LoadGate.SetResult();
        await first;
        Assert.Empty(vm.IssueRows);
    }

    [Fact]
    public async Task ATruncatedReadSaysSoInTheSummaryAndInTheReport()
    {
        var h = new DpContentPageHarness();
        h.Service.Truncated = true;
        h.Service.Note = "The site returned 45,000 rows; the page reads 6.";
        var vm = await h.OpenAsync();
        Assert.True(vm.IsTruncated);
        Assert.Contains("Truncated: showing 6 pairs. The site returned 45,000 rows", vm.LoadSummary);
        await vm.ExportCsvCommand.ExecuteAsync(null);
        var (_, report) = Assert.Single(h.Reports.Written);
        Assert.StartsWith("Truncated: showing 6 pairs.", report.Note);
        Assert.Contains("Truncated", vm.StatusMessage);
    }

    [Fact]
    public async Task ALoadFailureShowsAPlainMessageWithoutTheExceptionText()
    {
        var h = new DpContentPageHarness();
        h.Service.LoadFailure = new InvalidOperationException(@"WMI said: \\DP01\secret failed");
        var vm = await h.OpenAsync();
        Assert.Equal(VM.LoadFailed, vm.StatusMessage);
        Assert.DoesNotContain(h.Log.Entries, e => e.Message.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFiltersOfEachTabFilterAndKeepTheSelectionByKeyAcrossAFilterAndARefresh()
    {
        var h = new DpContentPageHarness();
        var vm = await h.OpenAsync();
        vm.SelectedDp = vm.DpRows.Single(r => r.DpName == "DP02.lab.local");
        vm.DpText = "branch";
        Assert.Equal(["DP01.lab.local.branch"], vm.DpRows.Select(r => r.DpName));
        Assert.Null(vm.SelectedDp);
        vm.DpText = "";
        Assert.Equal("DP02.lab.local", vm.SelectedDp?.DpName);
        vm.DpStatus = VM.HasFailures;
        Assert.Equal(["DP01.lab.local", "DP02.lab.local"], vm.DpRows.Select(r => r.DpName));
        vm.DpStatus = VM.HealthyOnly;
        Assert.Empty(vm.DpRows);
        vm.DpStatus = VM.HasInProgress;
        Assert.Equal(["DP01.lab.local.branch"], vm.DpRows.Select(r => r.DpName));
        vm.DpStatus = VM.AllText;

        vm.ContentType = "Boot image";
        Assert.Equal(["LAB00003"], vm.ContentRows.Select(r => r.PackageId));
        vm.ContentType = VM.AllText;
        vm.ContentText = "lab00002";
        Assert.Equal(["Office Tools"], vm.ContentRows.Select(r => r.Name));
        vm.ContentText = "";

        vm.IssueState = VM.FailedOnly;
        Assert.Equal([3, 6], vm.IssueRows.Select(r => r.State).Order());
        vm.IssueState = VM.InProgressOnly;
        Assert.Equal([2], vm.IssueRows.Select(r => r.State));
        vm.IssueState = VM.AllText;
        vm.SetSelectedIssues(vm.IssueRows.Where(r => r.State == 3));

        var before = vm.SelectedDp;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.NotSame(before, vm.SelectedDp);
        Assert.Equal("DP02.lab.local", vm.SelectedDp?.DpName);
        Assert.Equal([h.SevenZipOnDp02.PackageId], vm.SelectedIssues.Select(r => r.PackageId));
    }

    [Fact]
    public async Task ATextFilterWaitsForTheLastKey()
    {
        var h = new DpContentPageHarness { FilterDelay = TimeSpan.FromMilliseconds(250) };
        var vm = await h.OpenAsync();
        vm.DpText = "D";
        vm.DpText = "DP0";
        vm.DpText = "DP02";
        Assert.Equal(3, vm.DpRows.Count);
        await WaitUntil(() => vm.DpRows.Count == 1);
        Assert.Equal("DP02.lab.local", vm.DpRows[0].DpName);
    }

    [Fact]
    public async Task TheDpDetailAndThePerDpStatusComeFromTheLoadedRows()
    {
        var h = new DpContentPageHarness();
        var vm = await h.OpenAsync();
        vm.SelectedDp = vm.DpRows.Single(r => r.DpName == "DP01.lab.local");
        Assert.Equal(["7-Zip 26.04", "Boot image (x64)", "Office Tools"], vm.DpPairRows.Select(r => r.ContentName).Order());
        Assert.Contains("3 contents, total size 12.5 GB, 1 failed, 0 in progress.", vm.DpDetail);
        vm.SelectedContent = vm.ContentRows.Single(r => r.PackageId == "LAB00001");
        Assert.Equal(3, vm.ContentPairRows.Count);
        Assert.Contains("Deployments: 2.", vm.ContentDetail);
        Assert.Equal("unknown", vm.ContentRows.Single(r => r.PackageId == "LAB00002").Deployments);
        Assert.Equal(1, h.Service.LoadCalls);
        Assert.Equal(0, h.Service.PrepareCalls);
    }

    private static async Task<VM> ContentTab(DpContentPageHarness h, ContentRow content)
    {
        var vm = await h.OpenAsync(VM.ContentTab);
        vm.SelectedContent = vm.ContentRows.Single(r => r.PackageId == content.PackageId);
        return vm;
    }

    [Fact]
    public async Task RedistributeFailedPlansTheFailedPairsAndShowsTheRequestedResult()
    {
        var h = new DpContentPageHarness();
        var vm = await ContentTab(h, h.SevenZip);
        await vm.RedistributeFailedCommand.ExecuteAsync(null);
        var picker = Assert.Single(h.Dialogs.Shown);
        Assert.Equal([h.SevenZipOnDp02.NalPath], picker.Rows.Select(r => r.Pair.NalPath));
        var plan = Assert.Single(h.Plans.Shown);
        Assert.Equal("Redistribute 1 pair", plan.ActionName);
        Assert.False(plan.IsDestructive);
        Assert.Null(plan.ExtraConfirmation);
        Assert.Contains(DpContentText.AsyncNote, plan.Notes);
        var applied = Assert.Single(h.Service.Applied);
        Assert.Equal(DpContentChangeKind.Redistribute, applied.Single().Kind);
        Assert.False(applied.Single().RemovalConfirmed);
        var row = Assert.Single(vm.LastResults);
        Assert.Equal("Requested", row.OutcomeText);
        Assert.StartsWith("Redistribute content to distribution points: 1 requested.", vm.ResultSummary);
        Assert.Equal(2, h.Service.LoadCalls);
        Assert.False(vm.HasUndo);
    }

    [Fact]
    public async Task AnUntickedPairIsNeitherPreparedNorChanged()
    {
        var h = new DpContentPageHarness();
        var vm = await ContentTab(h, h.SevenZip);
        h.Dialogs.OnPairs = p => p.Rows.Single(r => r.Pair.NalPath == h.Dp01Branch.NalPath).IsChecked = false;
        await vm.ValidateCommand.ExecuteAsync(null);
        var prepared = Assert.Single(h.Service.Prepared);
        Assert.Equal(2, prepared.Pairs.Count);
        Assert.DoesNotContain(prepared.Pairs, p => p.NalPath == h.Dp01Branch.NalPath);
        Assert.Equal(2, h.Service.Applied.Single().Single().Pairs.Count);
        Assert.All(vm.LastResults, r => Assert.Equal("Unchanged", r.OutcomeText));
    }

    [Fact]
    public async Task ThePairListPreChecksTheSelectedDpsAndClearCheckShownAndCancelWork()
    {
        var h = new DpContentPageHarness();
        var vm = await ContentTab(h, h.SevenZip);
        vm.SetSelectedContentPairs(vm.ContentPairRows.Where(r => r.Pair.NalPath == h.Dp02.NalPath));
        h.Dialogs.Cancel = true;
        DpContentPairsViewModel? seen = null;
        h.Dialogs.OnPairs = p =>
        {
            seen = p;
            Assert.Equal(1, p.CheckedCount);
            p.ClearCommand.Execute(null);
            Assert.False(p.ContinueCommand.CanExecute(null));
            p.FilterText = "branch";
            p.CheckShownCommand.Execute(null);
            Assert.Equal(1, p.CheckedCount);
            Assert.Contains("The filter shows 1", p.CountText);
        };
        await vm.RemoveFromDpCommand.ExecuteAsync(null);
        Assert.NotNull(seen);
        Assert.Null(seen!.Result);
        Assert.Equal(0, h.Service.PrepareCalls);
        Assert.Equal(VM.NotConfirmed, vm.StatusMessage);
    }

    [Fact]
    public async Task ARemovalCannotRunWithoutItsSecondBox()
    {
        var h = new DpContentPageHarness();
        var vm = await ContentTab(h, h.SevenZip);
        h.Dialogs.OnPairs = p =>
        {
            p.ClearCommand.Execute(null);
            p.Rows.Single(r => r.Pair.NalPath == h.Dp01.NalPath).IsChecked = true;
        };
        h.Plans.AcknowledgeExtra = false;
        await vm.RemoveFromDpCommand.ExecuteAsync(null);
        var plan = Assert.Single(h.Plans.Shown);
        Assert.True(plan.IsDestructive);
        Assert.Equal("Remove 1 pair from distribution points. Clients that use these DPs cannot get the content from them.", plan.ExtraConfirmation);
        Assert.Null(plan.SecondConfirmation);
        Assert.False(h.Plans.LastViewModel!.ConfirmCommand.CanExecute(null));
        Assert.Empty(h.Service.Applied);
        Assert.Equal(VM.NotConfirmed, vm.StatusMessage);
    }

    [Fact]
    public async Task ARemovalWithItsSecondBoxReachesTheServiceConfirmedAndOffersTheUndo()
    {
        var h = new DpContentPageHarness();
        var vm = await ContentTab(h, h.SevenZip);
        h.Dialogs.OnPairs = p =>
        {
            p.ClearCommand.Execute(null);
            p.Rows.Single(r => r.Pair.NalPath == h.Dp01.NalPath).IsChecked = true;
        };
        await vm.RemoveFromDpCommand.ExecuteAsync(null);
        var sent = h.Service.Applied.Single().Single();
        Assert.True(sent.RemovalConfirmed);
        Assert.False(sent.LastCopyAcknowledged);
        var plan = h.Plans.Shown.Single();
        Assert.Contains("DPs that hold the content after this plan: 2.", plan.Items.Single().Detail);
        Assert.Contains("Deployments that use the content: 2.", plan.Items.Single().Detail);
        Assert.Equal("Confirmed", vm.LastResults.Single().OutcomeText);
        Assert.True(vm.HasUndo);
        Assert.Equal([h.SevenZipOnDp01.NalPath], vm.LastUndo!.Pairs.Select(p => p.NalPath));
    }

    [Fact]
    public async Task TheLastCopyOfAContentNeedsTheLastCopyBoxAndTheServiceGetsTheFlagOnlyThen()
    {
        var h = new DpContentPageHarness();
        var vm = await ContentTab(h, h.Office);
        h.Plans.AcknowledgeSecond = false;
        await vm.RemoveFromDpCommand.ExecuteAsync(null);
        var plan = h.Plans.Shown.Single();
        Assert.Equal("Remove 1 pair: 1 content will have no DP left.", plan.ExtraConfirmation);
        Assert.Equal("1 content becomes unavailable to every client: no distribution point holds it after this removal.", plan.SecondConfirmation);
        Assert.Contains("No DP holds the content after this plan.", plan.Items.Single().Detail);
        Assert.Contains("Deployments that use the content: unknown.", plan.Items.Single().Detail);
        Assert.Empty(h.Service.Applied);

        h.Plans.AcknowledgeSecond = true;
        await vm.RemoveFromDpCommand.ExecuteAsync(null);
        var sent = h.Service.Applied.Single().Single();
        Assert.True(sent.RemovalConfirmed);
        Assert.True(sent.LastCopyAcknowledged);
        Assert.Equal("Confirmed", vm.LastResults.Single().OutcomeText);
    }

    [Fact]
    public async Task UndoLastChangeIsAPlanOfItsOwnThatDistributesTheInstalledPairsAgain()
    {
        var h = new DpContentPageHarness();
        var vm = await ContentTab(h, h.SevenZip);
        await vm.RemoveFromDpCommand.ExecuteAsync(null);
        Assert.Equal(["Confirmed", "Confirmed", "Confirmed"], vm.LastResults.Select(r => r.OutcomeText));
        Assert.Equal([h.SevenZipOnDp01.NalPath], vm.LastUndo!.Pairs.Select(p => p.NalPath));
        Assert.Contains("Undo last change can distribute again 1 pair that was Installed.", vm.ResultSummary);

        await vm.UndoLastCommand.ExecuteAsync(null);
        Assert.Equal(2, h.Plans.Shown.Count);
        var undo = h.Plans.Shown[1];
        Assert.Equal("Distribute again 1 pair", undo.ActionName);
        Assert.Contains(DpContentText.DistributeAgainNote, undo.Notes);
        Assert.False(undo.IsDestructive);
        var sent = h.Service.Applied[1].Single();
        Assert.Equal(DpContentChangeKind.DistributeAgain, sent.Kind);
        Assert.False(sent.RemovalConfirmed);
        Assert.Equal("Requested", vm.LastResults.Single().OutcomeText);
        Assert.False(vm.HasUndo);
    }

    [Fact]
    public async Task APartialRunKeepsEveryResultAndAFailedPairShowsFailed()
    {
        var h = new DpContentPageHarness();
        var vm = await h.OpenAsync(VM.IssuesTab);
        h.Service.Fail = p => p.NalPath == h.Dp02.NalPath ? "The DP did not accept the request." : null;
        vm.SetSelectedIssues(vm.IssueRows);
        await vm.RedistributeIssuesCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.LastResults.Count);
        Assert.Equal(["Failed", "Requested", "Requested"], vm.LastResults.Select(r => r.OutcomeText).Order());
        Assert.Contains("2 requested, 1 failed.", vm.ResultSummary);
        var entry = Assert.Single(h.History.Entries);
        Assert.Equal(SiteOperationOutcome.PartlySucceeded, entry.Outcome);
        Assert.Equal(2, entry.Counts!["changed"]);
        Assert.Equal(1, entry.Counts["failed"]);
    }

    [Fact]
    public async Task RemoveFailuresTakesOnlyTheFailedRowsOfTheSelection()
    {
        var h = new DpContentPageHarness();
        var vm = await h.OpenAsync(VM.IssuesTab);
        vm.SetSelectedIssues(vm.IssueRows.Where(r => r.State == 2));
        await vm.RemoveFailuresCommand.ExecuteAsync(null);
        Assert.Equal(VM.SelectFailedIssues, vm.StatusMessage);
        Assert.Empty(h.Dialogs.Shown);
        vm.SetSelectedIssues(vm.IssueRows);
        await vm.RemoveFailuresCommand.ExecuteAsync(null);
        Assert.Equal([3, 6], h.Dialogs.Shown.Single().Rows.Select(r => r.Pair.State).Order());
        Assert.Equal(DpContentChangeKind.Remove, h.Service.Applied.Single().Single().Kind);
        Assert.False(vm.HasUndo);
    }

    [Fact]
    public async Task APairThatChangedBeforeTheReadIsDroppedAndAllDroppedShowsNoPlan()
    {
        var h = new DpContentPageHarness();
        var vm = await h.OpenAsync(VM.IssuesTab);
        vm.SetSelectedIssues(vm.IssueRows);
        h.Service.RemovePair(h.SevenZipOnBranch);
        await vm.RedistributeIssuesCommand.ExecuteAsync(null);
        var plan = h.Plans.Shown.Single();
        Assert.Equal(2, plan.Items.Count);
        Assert.Contains(plan.Notes, n => n.StartsWith("Skipped:", StringComparison.Ordinal));

        h.Service.Pairs.Clear();
        await vm.RefreshCommand.ExecuteAsync(null);
        h.Service.AddPair(h.SevenZip, h.Dp02, 3);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SetSelectedIssues(vm.IssueRows);
        h.Service.Pairs.Clear();
        await vm.RedistributeIssuesCommand.ExecuteAsync(null);
        Assert.Single(h.Plans.Shown);
        Assert.StartsWith(DpContentText.NothingLeft, vm.StatusMessage);
    }

    [Fact]
    public async Task APairThatChangesBetweenThePlanAndTheRunIsSkipped()
    {
        var h = new DpContentPageHarness();
        var vm = await ContentTab(h, h.SevenZip);
        h.Service.BetweenPrepareAndApply = () => h.Service.SetState(h.SevenZipOnDp02, 0);
        await vm.RedistributeFailedCommand.ExecuteAsync(null);
        var row = vm.LastResults.Single();
        Assert.Equal("Skipped", row.OutcomeText);
        Assert.StartsWith("Skipped", row.Message);
        Assert.Contains("1 skipped.", vm.ResultSummary);
    }

    [Fact]
    public async Task AFailedCheckOfTheServiceStopsThePlanWithItsMessage()
    {
        var h = new DpContentPageHarness();
        var vm = await ContentTab(h, h.SevenZip);
        h.Service.BlockingCheck = new PreflightCheck(Name: "Cap", Result: "Fail", Message: "The run has more than 5,000 pairs. Filter the list to fewer pairs.");
        await vm.ValidateCommand.ExecuteAsync(null);
        Assert.Empty(h.Plans.Shown);
        Assert.Equal("The plan cannot run: The run has more than 5,000 pairs. Filter the list to fewer pairs.", vm.StatusMessage);
    }

    [Fact]
    public async Task APlanIsDiscardedWhenTheSelectionChangedDuringTheRead()
    {
        var h = new DpContentPageHarness();
        var vm = await ContentTab(h, h.SevenZip);
        h.Service.PrepareGate = new TaskCompletionSource();
        var run = vm.ValidateCommand.ExecuteAsync(null);
        await WaitUntil(() => h.Service.PrepareCalls == 1);
        vm.SelectedContent = vm.ContentRows.Single(r => r.PackageId == h.Office.PackageId);
        h.Service.PrepareGate.SetResult();
        await run;
        Assert.Empty(h.Plans.Shown);
        Assert.Equal(VM.SelectionChanged, vm.StatusMessage);
    }

    [Fact]
    public async Task ADoubleClickOnAnActionStartsOneReadAndASecondActionWaits()
    {
        var h = new DpContentPageHarness();
        var vm = await ContentTab(h, h.SevenZip);
        h.Service.PrepareGate = new TaskCompletionSource();
        var first = vm.ValidateCommand.ExecuteAsync(null);
        await WaitUntil(() => h.Service.PrepareCalls == 1);
        Assert.False(vm.ValidateCommand.CanExecute(null));
        Assert.False(vm.RemoveFromDpCommand.CanExecute(null));
        Assert.False(vm.RedistributeFailedCommand.CanExecute(null));
        vm.ValidateCommand.Execute(null);
        h.Service.PrepareGate.SetResult();
        await first;
        Assert.Equal(1, h.Service.PrepareCalls);
        Assert.Single(h.Plans.Shown);
        Assert.True(vm.ValidateCommand.CanExecute(null));
    }

    [Fact]
    public async Task LeavingThePageKeepsTheUndoOfTheLastRemovalAndTheNextVisitReadsAgain()
    {
        var h = new DpContentPageHarness();
        var vm = await ContentTab(h, h.SevenZip);
        await vm.RemoveFromDpCommand.ExecuteAsync(null);
        Assert.True(vm.HasUndo);
        await vm.OnNavigatedFromAsync();
        Assert.True(vm.HasUndo);
        Assert.Empty(vm.LastResults);
        Assert.StartsWith("Undo last change can distribute again 1 pair", vm.ResultSummary);
        Assert.Empty(vm.ContentRows);
        var loads = h.Service.LoadCalls;
        await vm.OnNavigatedToAsync();
        Assert.Equal(loads + 1, h.Service.LoadCalls);
        Assert.True(vm.UndoLastCommand.CanExecute(null));
    }

    /// <summary>A plan window that the operator force-closed while the run still works.</summary>
    private sealed class DetachingPlanDialogs : IChangePlanDialogs
    {
        public Task? Run;
        public ChangePlanOutcome Show(ChangePlan plan, ChangePlanRun run, ChangePlanRun? undo = null)
        {
            var dialog = new ChangePlanViewModel(plan, run) { DestructiveAcknowledged = true, ExtraAcknowledged = true, SecondAcknowledged = true };
            Run = dialog.ConfirmCommand.ExecuteAsync(null);
            return new ChangePlanOutcome(true, new ChangePlanResult([], ChangePlanStop.Detached), null);
        }
    }

    [Fact]
    public async Task ARunLeftWorkingBlocksTheNextWriteAndKeepsItsUndoWhenItEndsAfterTheOperatorLeft()
    {
        var h = new DpContentPageHarness();
        var detaching = new DetachingPlanDialogs();
        h.PlanDialogs = detaching;
        h.Service.InConfirmedRun = null;
        h.Service.ApplyGate = new TaskCompletionSource();
        var vm = await ContentTab(h, h.SevenZip);
        await vm.RemoveFromDpCommand.ExecuteAsync(null);
        Assert.Equal(VM.BackgroundRunText, vm.StatusMessage);
        Assert.True(vm.IsWriting);
        Assert.False(vm.ValidateCommand.CanExecute(null));
        await vm.OnNavigatedFromAsync();
        h.Service.ApplyGate.SetResult();
        await detaching.Run!;
        await WaitUntil(() => !vm.IsWriting);
        Assert.True(vm.HasUndo);
        Assert.StartsWith("Undo last change can distribute again 1 pair", vm.ResultSummary);
    }

    [Fact]
    public async Task ExportWritesTheRowsTheFiltersShowAndRefusesBeforeARead()
    {
        var h = new DpContentPageHarness();
        h.Settings.SiteCode = "";
        var vm = await h.OpenAsync();
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(VM.ExportNotLoaded, vm.StatusMessage);
        Assert.Empty(h.Reports.Written);

        h.Settings.SiteCode = "LAB";
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedTabIndex = VM.IssuesTab;
        vm.IssueState = VM.FailedOnly;
        await vm.ExportHtmlCommand.ExecuteAsync(null);
        var (format, report) = Assert.Single(h.Reports.Written);
        Assert.Equal("html", format);
        Assert.Equal(2, report.Rows.Count);
        Assert.Null(report.Note);
        Assert.Equal(8, report.Columns.Count);
        Assert.All(report.Rows, r => Assert.Equal(8, r.Count));
        Assert.True(vm.OpenReportsFolderCommand.CanExecute(null));
        vm.OpenReportsFolderCommand.Execute(null);
        Assert.Equal([@"C:\Reports"], h.Dialogs.OpenedFolders);

        h.Reports.Fails = true;
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(VM.ReportFailed("CSV"), vm.StatusMessage);
    }

    private static DpContentAuditRecord Audit(string action, string result, DateTime when, bool? verified = true) =>
        new(Timestamp: when, User: @"LAB\admin", Action: action, Result: result, PairCount: 2, Succeeded: 2, Failed: 0, Skipped: 0,
            PackageIds: "LAB00001; LAB00002", DpNames: "DP01.lab.local", Message: "Done.") { Verified = verified, HasChain = verified is not null };

    [Fact]
    public async Task TheHistoryTabReadsTheAuditLogWithoutTheSiteAndFiltersIt()
    {
        var h = new DpContentPageHarness();
        h.Settings.SiteCode = "";
        h.Audit.Records.Add(Audit("Remove", "Success", new DateTime(2026, 10, 23, 8, 0, 0)));
        h.Audit.Records.Add(Audit("Redistribute", "Partial", new DateTime(2026, 10, 23, 9, 0, 0), verified: false) with { ChainBrokenAt = 2 });
        h.Audit.Records.Add(Audit("DistributeAgain", "Success", new DateTime(2026, 10, 23, 10, 0, 0)));
        var vm = await h.OpenAsync(VM.HistoryTab);
        Assert.Equal(0, h.Service.LoadCalls);
        Assert.Equal(["Distribute again", "Redistribute", "Remove"], vm.HistoryRows.Select(r => r.Action));
        Assert.Equal("! Not verified", vm.HistoryRows[1].Verified);
        Assert.Equal("Not verified (chain broken at line 2)", vm.HistoryRows[1].VerifiedText);
        vm.HistoryAction = "Remove";
        Assert.Equal(["Remove"], vm.HistoryRows.Select(r => r.Action));
        vm.HistoryAction = VM.AllText;
        vm.HistoryText = "partial";
        Assert.Single(vm.HistoryRows);
        vm.SelectedHistory = vm.HistoryRows[0];
        Assert.Contains("Content: LAB00001; LAB00002", vm.HistoryDetails);
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.Single(h.Reports.Written.Single().Report.Rows);
        Assert.Equal(@"C:\Data\DpContent\dp-content-audit.jsonl", vm.HistoryPath);
    }

    [Fact]
    public async Task ThePageChoicesComeBackOnTheNextVisitAndOlderFilesUseTheDefaults()
    {
        var h = new DpContentPageHarness();
        var vm = await h.OpenAsync();
        vm.SelectedTabIndex = VM.IssuesTab;
        vm.IssueState = VM.FailedOnly;
        vm.DpText = "DP0";
        vm.ContentType = "Package";
        vm.Sort(VM.DpsGrid, nameof(DpGridRow.Failed));
        vm.Sort(VM.DpsGrid, nameof(DpGridRow.Failed));
        await vm.OnNavigatedFromAsync();
        Assert.NotNull(h.Saved);
        Assert.Equal("Failed|Descending", h.Saved!.Sorts![VM.DpsGrid]);

        var again = h.Create();
        await again.OnNavigatedToAsync();
        Assert.Equal(VM.IssuesTab, again.SelectedTabIndex);
        Assert.Equal(VM.FailedOnly, again.IssueState);
        Assert.Equal("DP0", again.DpText);
        Assert.Equal("Package", again.ContentType);
        Assert.Equal(("Failed", false), again.SortOf(VM.DpsGrid));
        Assert.Equal("DP01.lab.local.branch", again.DpRows[^1].DpName);

        h.Saved = new DpContentSettings { SelectedTab = 9, DpStatus = "nonsense", ContentType = null, IssueText = null, Sorts = new() { ["dps"] = "../x|Ascending" } };
        var old = h.Create();
        await old.OnNavigatedToAsync();
        Assert.Equal(VM.HistoryTab, old.SelectedTabIndex);
        Assert.Equal(VM.AllText, old.DpStatus);
        Assert.Equal(VM.AllText, old.ContentType);
        Assert.Equal("", old.IssueText);
        Assert.Null(old.SortOf(VM.DpsGrid));
    }

    [Fact]
    public async Task ASortStaysAfterAFilterChangeAndARefresh()
    {
        var h = new DpContentPageHarness();
        var vm = await h.OpenAsync(VM.ContentTab);
        vm.Sort(VM.ContentGrid, nameof(ContentGridRow.Failed));
        vm.Sort(VM.ContentGrid, nameof(ContentGridRow.Failed));
        Assert.Equal("LAB00001", vm.ContentRows[0].PackageId);
        vm.ContentText = "LAB";
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(["LAB00001", "LAB00003", "LAB00002"], vm.ContentRows.Select(r => r.PackageId));
    }

    [Fact]
    public async Task TheLogHoldsCountsAndOutcomesButNoDpOrContentName()
    {
        var h = new DpContentPageHarness();
        var vm = await ContentTab(h, h.SevenZip);
        await vm.RemoveFromDpCommand.ExecuteAsync(null);
        await vm.UndoLastCommand.ExecuteAsync(null);
        Assert.Contains(h.Log.Entries, e => e.Area == "DpContent" && e.Message.Contains("outcome=", StringComparison.Ordinal));
        foreach (var name in new[] { "DP01", "DP02", "7-Zip", "Office", "LAB0000", "lab.local" })
            Assert.DoesNotContain(h.Log.Entries, e => e.Message.Contains(name, StringComparison.OrdinalIgnoreCase));
        Assert.All(h.History.Entries, e => Assert.Equal("DP Content Manager", e.Tool));
    }

    [Fact]
    public async Task ThirtyThousandPairsLoadFilterAndSelectInMemoryWithOneRead()
    {
        var h = DpContentPageHarness.Large(300, 100);
        var vm = await h.OpenAsync(VM.IssuesTab);
        Assert.Equal(30_000, vm.Services.Content.LoadAsync(new Progress<string>(), CancellationToken.None).Result.Pairs.Count);
        Assert.Equal(300, vm.DpRows.Count);
        Assert.Equal(6_000, vm.IssueRows.Count);
        vm.IssueState = VM.FailedOnly;
        Assert.Equal(3_000, vm.IssueRows.Count);
        vm.SelectedTabIndex = VM.ContentTab;
        vm.SelectedContent = vm.ContentRows[5];
        Assert.Equal(300, vm.ContentPairRows.Count);
        vm.SelectedDp = vm.DpRows[7];
        Assert.Equal(100, vm.DpPairRows.Count);
        Assert.Equal(2, h.Service.LoadCalls);
    }
}
