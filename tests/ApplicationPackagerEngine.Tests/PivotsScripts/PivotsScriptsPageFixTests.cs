using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using static ApplicationPackagerEngine.Tests.PivotsScripts.PivotsScriptsHarness;
using VM = ApplicationPackagerUI.ViewModels.Pages.PivotsScriptsViewModel;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>The plan text, the result tabs, the export and the summary of the page over the fake service.</summary>
public sealed class PivotsScriptsPageFixTests
{
    private static async Task<VM> ResolvedAsync(PivotsScriptsHarness h, int tab = VM.PivotTab, RunTargetKind kind = RunTargetKind.Collection, string text = "LAB00012")
    {
        var vm = await h.OpenAsync(tab);
        await ResolveAsync(vm, kind, text);
        return vm;
    }

    private static async Task RunQueryAsync(VM vm)
    {
        vm.QueryText = "OS | project Caption";
        await vm.RunPivotCommand.ExecuteAsync(null);
        await vm.WaitTask;
    }

    [Fact]
    public async Task TheDeviceCountBoxOfAScriptRunNamesTheHashPrefixAndTheParameters()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h, VM.ScriptTab);
        vm.SelectedApproved = vm.ApprovedScripts.Single(r => r.Name == "Read key");
        var longValue = "HKLM:\\SOFTWARE\\" + new string('k', 200);
        h.Dialogs.OnParameters = p => p.Rows.Single(r => r.Name == "Path").Value = longValue;
        vm.EditParametersCommand.Execute(null);
        await vm.RunScriptCommand.ExecuteAsync(null);
        var plan = Assert.Single(h.Plans.Shown);
        var hash = FakePivotsService.Hash("param([string]$Path, [int]$Depth) Get-ItemProperty $Path");
        Assert.Contains($"SHA-256 of the script text starts with {hash[..PivotsScriptsText.HashPrefixLength]}.", plan.ExtraConfirmation);
        Assert.Contains("Parameters: ", plan.ExtraConfirmation);
        Assert.Contains("Path = HKLM:\\SOFTWARE\\", plan.ExtraConfirmation);
        Assert.Contains("(the notes hold the whole value)", plan.ExtraConfirmation);
        Assert.DoesNotContain(longValue, plan.ExtraConfirmation);
        Assert.Contains(plan.Notes, n => n.Contains(longValue, StringComparison.Ordinal));
        await vm.WaitTask;
    }

    [Fact]
    public async Task ATargetThatHoldsEveryClientOfTheSiteGetsTheEveryDeviceBoxInThePlan()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h);
        var every = vm.Resolved! with { CoversEverySiteDevice = true, SiteClientCount = 6 };
        var change = new PivotsChange(Kind: PivotsChangeKind.RunPivot, Targets: every, QueryText: "OS", ScriptGuid: null, ScriptHash: null, Parameters: null, SubmitName: null,
            SubmitText: null, Comment: null, RunConfirmed: false, EveryDeviceAcknowledged: false, Impact: [], Checks: [], Fingerprint: "fp") { DeviceCount = 6 };
        var (plan, problem) = PivotsScriptsText.BuildPlan(change);
        Assert.Null(problem);
        Assert.Equal("This runs on every device of the site: Collection Workstations (LAB00012).", plan!.Plan.SecondConfirmation);
        Assert.Contains(plan.Plan.Notes, n => n.StartsWith("The target holds every client of the site (6 clients)", StringComparison.Ordinal));
        Assert.Contains(VM.EveryClientWarning, VM.ResolvedText(every));
    }

    [Fact]
    public async Task ARefusedRunKeepsTheEarlierResultsAndTheHeadlineOfTheLastRun()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h);
        await RunQueryAsync(vm);
        var header = vm.ResultHeader;
        var headline = vm.LastRunHeadline;
        Assert.Equal(6, vm.DeviceRows.Count);
        h.Service.RefuseAll = "Skipped: the plan is older than 30 minutes. Prepare the plan again. Nothing was sent.";
        vm.QueryText = "Service";
        await vm.RunPivotCommand.ExecuteAsync(null);
        Assert.Equal(header, vm.ResultHeader);
        Assert.Equal(headline, vm.LastRunHeadline);
        Assert.Equal(6, vm.DeviceRows.Count);
        Assert.Contains("older than 30 minutes", vm.StatusMessage);
    }

    [Fact]
    public async Task TheExportNoteNamesTheDevicesWithMoreRowsAndTheColumnLimit()
    {
        var h = new PivotsScriptsHarness();
        h.Service.Shape = r => r.State != PivotsDeviceStates.Completed ? r : r with
        {
            MoreResults = r.ResourceId == 16777201,
            Rows = [Enumerable.Range(0, VM.MaxResultColumns + 3).ToDictionary(i => $"Column{i}", i => $"v{i}")]
        };
        var vm = await ResolvedAsync(h);
        await RunQueryAsync(vm);
        vm.ResultTabIndex = VM.ResultsView;
        await vm.ExportCsvCommand.ExecuteAsync(null);
        var (_, report) = Assert.Single(h.Reports.Written);
        Assert.Equal(VM.MaxResultColumns + 1, report.Columns.Count);
        Assert.NotNull(report.Note);
        Assert.Contains("1 device holds more result rows than the site returned", report.Note);
        Assert.Contains(VM.ColumnsCutNote, report.Note);
        vm.ResultTabIndex = VM.DevicesView;
        await vm.ExportJsonCommand.ExecuteAsync(null);
        Assert.Contains("more result rows", h.Reports.Written[1].Report.Note);
        Assert.DoesNotContain(VM.ColumnsCutNote, h.Reports.Written[1].Report.Note);
    }

    [Fact]
    public async Task AResultCellShowsTheFirstLineAndTheExportHoldsTheWholeOutput()
    {
        var h = new PivotsScriptsHarness();
        h.Service.Shape = r => r with { Output = "first line\r\nsecond line\r\nthird line" };
        var vm = await ResolvedAsync(h, VM.ScriptTab);
        vm.SelectedApproved = vm.ApprovedScripts.Single(r => r.Name == "Disk space");
        await vm.RunScriptCommand.ExecuteAsync(null);
        await vm.WaitTask;
        vm.ResultTabIndex = VM.ResultsView;
        Assert.Equal(["State", "Output"], vm.ResultColumns);
        var row = vm.ResultRows[0];
        Assert.Equal("first line …", row.Cells[1]);
        Assert.Equal("first line\r\nsecond line\r\nthird line", row.FullCells[1]);
        Assert.Equal("first line\r\nsecond line\r\nthird line", vm.ActiveReport()!.Rows[0][2]);
        vm.ResultFilter = "third line";
        vm.FlushFilters();
        Assert.Equal(6, vm.ResultRows.Count);
    }

    [Fact]
    public async Task NewResultColumnsNeverMeetRowsWithFewerCells()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h, VM.ScriptTab);
        vm.SelectedApproved = vm.ApprovedScripts.Single(r => r.Name == "Disk space");
        await vm.RunScriptCommand.ExecuteAsync(null);
        await vm.WaitTask;
        Assert.Equal(2, vm.ResultColumns.Count);
        var mismatches = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VM.ResultColumns) && vm.ResultRows.Any(r => r.Cells.Count < vm.ResultColumns.Count)) mismatches++;
        };
        h.Service.Shape = r => r.State != PivotsDeviceStates.Completed ? r : r with { Rows = [Enumerable.Range(0, 5).ToDictionary(i => $"C{i}", i => $"v{i}")] };
        vm.SelectedTabIndex = VM.PivotTab;
        await RunQueryAsync(vm);
        Assert.Equal(5, vm.ResultColumns.Count);
        Assert.Equal(0, mismatches);
    }

    [Fact]
    public async Task TheSummaryLineFollowsTheCountOfAWait()
    {
        var h = new PivotsScriptsHarness();
        h.Service.WaitGate = new TaskCompletionSource();
        h.Service.ProgressState = id => id % 2 == 0 ? PivotsDeviceStates.Completed : PivotsDeviceStates.Running;
        var vm = await ResolvedAsync(h);
        vm.QueryText = "OS";
        await vm.RunPivotCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.WaitProgress.Contains("3 of 6", StringComparison.Ordinal));
        Assert.Contains("3 of 6 devices answered", vm.PageSummary);
        h.Service.WaitGate.SetResult();
        await vm.WaitTask;
    }

    [Fact]
    public async Task ALaterSubmitThatSucceedsKeepsTheCleanUpOfAnOlderIncompleteScript()
    {
        var h = new PivotsScriptsHarness();
        h.Service.SubmitFailsAfterCreate = true;
        var vm = await h.OpenAsync(VM.SiteScriptsTab);
        vm.DraftName = "Lab probe";
        vm.DraftText = "param([string]$Name) Get-Service $Name";
        await vm.SubmitDraftCommand.ExecuteAsync(null);
        var orphan = vm.LeftoverGuid;
        Assert.NotNull(orphan);
        h.Service.SubmitFailsAfterCreate = false;
        vm.DraftName = "Lab probe two";
        await vm.SubmitDraftCommand.ExecuteAsync(null);
        Assert.Equal(orphan, vm.LeftoverGuid);
        Assert.True(vm.RemoveLeftoverCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnExportThatTheWriterRefusesShowsTheMessageOfTheWriter()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h);
        await RunQueryAsync(vm);
        h.Reports.Failure = new PivotsServiceException("The report has more than 500,000 rows. Filter the view and export it in parts.");
        vm.ResultTabIndex = VM.DevicesView;
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal("The report has more than 500,000 rows. Filter the view and export it in parts.", vm.StatusMessage);
    }

    [Fact]
    public async Task AFullListOfSavedSessionsSaysThatOlderSessionsAreNotShown()
    {
        var h = new PivotsScriptsHarness();
        for (var i = 0; i < VM.MaxSessions + 3; i++)
            h.Sessions.Sessions.Add(new PivotsSessionRecord($"s{i}", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i), "u", "LAB", "cm01.lab.local", PivotsChangeKind.RunPivot,
                "t", null, [], "OS", null, null, null, new Dictionary<string, string>()));
        var vm = await h.OpenAsync(VM.HistoryTab);
        Assert.Equal(VM.MaxSessions, vm.SessionRows.Count);
        Assert.Contains(VM.SessionsCappedNote, vm.SessionCount);
    }
}
