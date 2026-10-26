using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using VM = ApplicationPackagerUI.ViewModels.Pages.DpContentManagerViewModel;

namespace ApplicationPackagerEngine.Tests.DpContent;

/// <summary>The DP Content Manager page: the Undo after an Unchanged distribution, error messages in Status Issues and the audit read window.</summary>
public sealed class PageRound2Tests
{
    [Fact]
    public async Task APairWhoseDistributeAgainEndedUnchangedStaysInTheUndo()
    {
        var h = new DpContentPageHarness();
        var vm = await h.OpenAsync(VM.ContentTab);
        vm.SelectedContent = vm.ContentRows.Single(r => r.PackageId == h.SevenZip.PackageId);
        h.Dialogs.OnPairs = p => p.CheckShownCommand.Execute(null);
        await vm.RemoveFromDpCommand.ExecuteAsync(null);
        Assert.Equal([h.SevenZipOnDp01.NalPath], vm.LastUndo!.Pairs.Select(p => p.NalPath));

        h.Service.DistributeAgainResult = "Unchanged";
        await vm.UndoLastCommand.ExecuteAsync(null);
        Assert.Equal("Unchanged", vm.LastResults.Single().OutcomeText);
        Assert.True(vm.HasUndo);
        Assert.Equal([h.SevenZipOnDp01.NalPath], vm.LastUndo!.Pairs.Select(p => p.NalPath));
    }

    [Fact]
    public async Task AnUndoPairDroppedDuringPrepareStaysAvailableAfterOtherPairsAreDistributed()
    {
        var h = new DpContentPageHarness();
        h.Service.SetState(h.SevenZipOnDp02, DpContentStates.Installed);
        h.Service.SetState(h.SevenZipOnBranch, DpContentStates.Installed);
        var vm = await h.OpenAsync(VM.ContentTab);
        vm.SelectedContent = vm.ContentRows.Single(r => r.PackageId == h.SevenZip.PackageId);
        h.Dialogs.OnPairs = p => p.CheckShownCommand.Execute(null);
        await vm.RemoveFromDpCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.LastUndo!.Pairs.Count);

        h.Service.DropDistributeAgain = pair => pair.NalPath == h.SevenZipOnBranch.NalPath;
        await vm.UndoLastCommand.ExecuteAsync(null);

        Assert.Equal(2, h.Service.Applied[1].Single().Pairs.Count);
        Assert.Equal([h.SevenZipOnBranch.NalPath], vm.LastUndo!.Pairs.Select(p => p.NalPath));
    }

    [Fact]
    public async Task APairWithAnErrorMessageCountsAsFailedInStatusIssuesAndRemoveFailures()
    {
        var h = new DpContentPageHarness();
        var updating = h.Service.AddPair(h.Office, h.Dp02, DpContentStates.ContentUpdating);
        h.Service.Pairs[^1] = updating with { MessageState = 4, MessageId = 2306 };
        var installed = h.Service.AddPair(h.Boot, h.Dp01Branch, DpContentStates.Installed);
        h.Service.Pairs[^1] = installed with { MessageState = 4, MessageId = 2306 };
        var vm = await h.OpenAsync(VM.IssuesTab);

        var row = vm.IssueRows.Single(r => r.Key == DpContentLabels.Key(updating));
        Assert.Equal(DpContentLabels.FailedLabel, row.Status);
        Assert.Contains(vm.IssueRows, r => r.Key == DpContentLabels.Key(installed));
        vm.IssueState = VM.FailedOnly;
        Assert.Contains(vm.IssueRows, r => r.Key == DpContentLabels.Key(updating));
        vm.IssueState = VM.InProgressOnly;
        Assert.DoesNotContain(vm.IssueRows, r => r.Key == DpContentLabels.Key(updating));
        vm.IssueState = VM.AllText;

        vm.SetSelectedIssues(vm.IssueRows.Where(r => r.Key == DpContentLabels.Key(updating)));
        await vm.RemoveFailuresCommand.ExecuteAsync(null);
        Assert.Equal([DpContentLabels.Key(updating)], h.Dialogs.Shown.Single().Rows.Select(r => r.Key));
    }

    [Fact]
    public async Task AnAuditReadThatLeftOutOlderRecordsShowsTheNoteAndMarksTheExportTruncated()
    {
        var h = new DpContentPageHarness();
        h.Audit.Dropped = true;
        h.Audit.Records.Add(new DpContentAuditRecord(Timestamp: new DateTime(2026, 10, 23, 8, 0, 0), User: @"LAB\admin", Event: "Validate", Phase: "Finished", Result: "Succeeded",
            RunId: "run-1", Chunk: 1, Pairs: [], Message: "Done."));
        var vm = await h.OpenAsync(VM.HistoryTab);

        Assert.True(vm.IsHistoryCapped);
        Assert.Equal(VM.HistoryWindowNote, vm.HistoryCapText);
        await vm.ExportCsvCommand.ExecuteAsync(null);
        var report = h.Reports.Written.Single().Report;
        Assert.True(report.Truncated);
        Assert.Equal(VM.HistoryWindowNote, report.Note);
    }
}
