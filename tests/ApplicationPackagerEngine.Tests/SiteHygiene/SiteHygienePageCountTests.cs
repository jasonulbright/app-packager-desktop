using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using static ApplicationPackagerEngine.Tests.SiteHygiene.SiteHygienePageHarness;
using VM = ApplicationPackagerUI.ViewModels.Pages.SiteHygieneViewModel;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>
/// The counts of a scan that left checks out, the Fix column and Run fix of a finding the service refuses,
/// Run fix before a scan, and the site history of a run that changed nothing.
/// </summary>
public sealed class SiteHygienePageCountTests
{
    /// <summary>A scan of the Applications and Collections areas: the checks of every other area are Not selected, as the service reports them.</summary>
    private static SiteHygienePageHarness PartialScan()
    {
        var h = new SiteHygienePageHarness();
        h.Service.Findings.RemoveAll(f => f.Category is not ("Applications" or "Collections"));
        h.Service.Checks.Clear();
        foreach (var c in HygieneCatalog.Checks)
        {
            var area = c.Category is "Applications" or "Collections";
            var state = c.Id is "APP-04" or "COL-10" ? HygieneCheckStates.NotSelected : area ? HygieneCheckStates.Ran : HygieneCheckStates.NotSelected;
            h.Service.Checks.Add(new HygieneCheckStatus(c.Id, c.Title, c.Category, state, state == HygieneCheckStates.Ran ? h.Service.Findings.Count(f => f.CheckId == c.Id) : 0,
                state == HygieneCheckStates.Ran ? null : "The scan did not cover the area.") { Severity = c.Severity });
        }
        return h;
    }

    [Fact]
    public async Task ACountOfACategoryAndSeverityWithACheckThatWasNotSelectedIsNotAPlainNumber()
    {
        var h = PartialScan();
        var vm = await h.OpenAsync();
        vm.AreaChoices.Where(a => a.Id is not ("Applications" or "Collections")).ToList().ForEach(a => a.IsChecked = false);
        await vm.ScanAsync();

        var all = vm.CategoryRows.Single(r => r.Category == "All categories");
        // Error checks of other areas did not run, so the one error is a lower bound.
        Assert.Equal(("At least 1", "At least 3", "At least 1", "At least 5"), (all.Error, all.Warning, all.Info, all.Total));
        var apps = vm.CategoryRows.Single(r => r.Category == "Applications");
        // APP-04 (Warning or Info) was not selected; APP-02 (Error) and APP-01 (Warning) ran.
        Assert.Equal(("1", "At least 2", "Not known", "At least 3"), (apps.Error, apps.Warning, apps.Info, apps.Total));
        var collections = vm.CategoryRows.Single(r => r.Category == "Collections");
        // COL-10 (Info) was not selected; COL-01 (Info) and COL-05 (Warning) ran and found one each; COL-08 (Error) ran and found none.
        Assert.Equal(("0", "1", "At least 1", "At least 2"), (collections.Error, collections.Warning, collections.Info, collections.Total));
        Assert.Equal("0", collections.NotRun);
        var devices = vm.CategoryRows.Single(r => r.Category == "Devices");
        Assert.Equal(("Not known", "Not known", "Not known", "Not known"), (devices.Error, devices.Warning, devices.Info, devices.Total));
        Assert.StartsWith("at least 1 error, at least 3 warnings, at least 1 info. ", vm.SummaryLine);
    }

    [Fact]
    public async Task ACheckWhoseFindingsCanHaveAnotherSeverityMakesThatCellALowerBoundToo()
    {
        var h = new SiteHygienePageHarness();
        h.Service.Checks.Add(new HygieneCheckStatus("UPD-03", "Automatic deployment rule disabled, stale, or erroring", "Updates", HygieneCheckStates.NotRun, 0, "The read of rules failed.")
            { Severity = HygieneSeverity.Warning });
        var vm = await h.ScannedAsync();
        var updates = vm.CategoryRows.Single(r => r.Category == "Updates");
        Assert.Equal(("Not known", "Not known", "Not known"), (updates.Error, updates.Warning, updates.Info));
        var all = vm.CategoryRows.Single(r => r.Category == "All categories");
        Assert.Equal("At least 1", all.Error);
    }

    [Fact]
    public async Task RunFixIsDisabledWithTheReasonBeforeAnyScanAndWithoutASelection()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.OpenAsync();
        Assert.False(vm.RunFixCommand.CanExecute(null));
        Assert.Equal(VM.NotScanned, vm.RunFixHint);
        Assert.True(vm.HasRunFixHint);

        await vm.ScanAsync();
        vm.SetSelectedFindings([]);
        Assert.False(vm.RunFixCommand.CanExecute(null));
        Assert.Equal(VM.SelectFindings, vm.RunFixHint);

        Select(vm, h.UnusedApp);
        Assert.True(vm.RunFixCommand.CanExecute(null));
        Assert.False(vm.HasRunFixHint);
    }

    [Fact]
    public async Task RunFixIsDisabledWithTheReasonBeforeTheStoredScanIsShownToo()
    {
        var h = new SiteHygienePageHarness();
        h.ScanStore.Stored = h.Service.Result();
        var vm = h.Create();
        Assert.False(vm.RunFixCommand.CanExecute(null));
        Assert.Equal(VM.NotScanned, vm.RunFixHint);
        await vm.OnNavigatedToAsync();
        Assert.Equal(VM.SelectFindings, vm.RunFixHint);
    }

    [Fact]
    public async Task AFindingTheServiceRefusesShowsReportOnlyAndDisablesRunFixWithTheRefusal()
    {
        var h = new SiteHygienePageHarness();
        const string refusal = "1 automatic deployment rule targets the collection, so the page does not delete the collection. Remove that use first.";
        var adrTarget = Finding("COL-01", "Collections", HygieneSeverity.Info, "Collection", "MCM00223", "SHL-Col-ADRRef", HygieneFixKind.None) with { FixRefusal = refusal };
        h.Service.Findings.Add(adrTarget);
        var vm = await h.ScannedAsync();
        Assert.Equal("Report only", Row(vm, adrTarget).Fix);

        Select(vm, adrTarget);
        Assert.False(vm.RunFixCommand.CanExecute(null));
        Assert.Equal(refusal + " Use the recommended action in the ConfigMgr console.", vm.RunFixHint);
        Assert.StartsWith(refusal, vm.DetailFixText);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Equal(0, h.Service.PrepareCalls);

        var (report, _) = vm.ActiveReport();
        var fixColumn = report!.Columns.ToList().IndexOf("Fix");
        Assert.Equal("Report only", report.Rows.Single(r => r[4] == "MCM00223")[fixColumn]);
    }

    [Fact]
    public async Task AFindingWithAFixKindAndARefusalFromAStoredScanIsNotFixable()
    {
        var h = new SiteHygienePageHarness();
        var refused = h.EmptyCollection with { FixRefusal = "1 client settings deployment targets the collection, so the page does not delete the collection. Remove that use first." };
        h.Service.Findings[h.Service.Findings.IndexOf(h.EmptyCollection)] = refused;
        var vm = await h.ScannedAsync();
        Assert.Equal("Report only", Row(vm, refused).Fix);
        Select(vm, refused);
        Assert.False(vm.RunFixCommand.CanExecute(null));
        Assert.StartsWith("1 client settings deployment targets the collection", vm.RunFixHint);
    }

    [Fact]
    public async Task ARunInWhichEveryObjectWasSkippedIsRecordedAsSkippedNotFailed()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        h.Service.BetweenPrepareAndApply = () => h.Service.StillHolds = _ => false;
        Select(vm, h.EmptyCollection);
        await vm.RunFixCommand.ExecuteAsync(null);
        var entry = Assert.Single(h.History.Entries);
        Assert.Equal(SiteOperationOutcome.Skipped, entry.Outcome);
        Assert.Equal((0, 0, 1), (entry.Counts["changed"], entry.Counts["failed"], entry.Counts["skipped"]));
    }

    public static TheoryData<string[], bool, SiteOperationOutcome> Outcomes => new()
    {
        { ["Done", "Done"], false, SiteOperationOutcome.Succeeded },
        { ["Done", "Skipped"], false, SiteOperationOutcome.PartlySucceeded },
        { ["Skipped", "Skipped"], false, SiteOperationOutcome.Skipped },
        { ["Skipped", "Failed"], false, SiteOperationOutcome.Failed },
        { ["Unchanged"], false, SiteOperationOutcome.Failed },
        { ["Skipped"], true, SiteOperationOutcome.Cancelled }
    };

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void TheHistoryOutcomeOfARunFollowsItsObjects(string[] results, bool cancelled, SiteOperationOutcome expected)
    {
        var rows = results.Select((r, i) => SiteHygieneWriter.Row(new ChangePlanItem("Delete collection", $"Collection {i}", "COL-01"), r, r == "Skipped" ? "Skipped: the finding no longer holds." : r)).ToList();
        Assert.Equal(expected, SiteHygieneWriter.Outcome(rows, cancelled));
    }
}
