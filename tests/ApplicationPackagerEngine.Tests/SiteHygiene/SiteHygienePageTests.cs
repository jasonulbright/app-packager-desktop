using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using static ApplicationPackagerEngine.Tests.SiteHygiene.SiteHygienePageHarness;
using VM = ApplicationPackagerUI.ViewModels.Pages.SiteHygieneViewModel;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>The Site Hygiene page without a window, over the fake service: scan, filters, suppressions, fixes, relationships, export and choices.</summary>
public sealed class SiteHygienePageTests
{
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task TheConstructorReadsNothingAndTheFirstVisitShowsTheStoredScanWithoutASiteCall()
    {
        var h = new SiteHygienePageHarness();
        h.ScanStore.Stored = h.Service.Result() with { ScannedUtc = new DateTime(2026, 10, 20, 7, 0, 0, DateTimeKind.Utc) };
        var vm = h.Create();
        Assert.Equal(0, h.ScanStore.Loads);
        Assert.Equal(0, h.Suppressions.Loads);
        Assert.False(vm.HasResult);

        await vm.OnNavigatedToAsync();
        Assert.Equal(0, h.Service.ScanCalls);
        Assert.Equal(0, h.Service.RelationCalls);
        Assert.Equal(1, h.ScanStore.Loads);
        Assert.True(vm.IsStoredResult);
        Assert.Contains("stored result, not read from the site in this session", vm.SummaryLine);
        Assert.Equal(8, vm.FindingRows.Count);

        await vm.OnNavigatedFromAsync();
        await vm.OnNavigatedToAsync();
        Assert.Equal(1, h.ScanStore.Loads);
        Assert.Contains("stored result", vm.SummaryLine);
    }

    [Fact]
    public async Task WithNoStoredScanThePageSaysSoAndReadsNothingFromTheSite()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.OpenAsync();
        Assert.False(vm.HasResult);
        Assert.Equal("No scan result yet. Select Scan to read the site.", vm.SummaryLine);
        Assert.Equal(0, h.Service.ScanCalls);
        Assert.Empty(vm.FindingRows);
    }

    [Fact]
    public async Task WithoutASiteCodeThePageShowsOnePlainMessageAndTheScanAsksNothing()
    {
        var h = new SiteHygienePageHarness();
        h.Settings.SiteCode = "";
        var vm = await h.OpenAsync();
        Assert.True(vm.HasConnectionMessage);
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(0, h.Service.ScanCalls);
        Assert.Equal(vm.ConnectionMessage, vm.StatusMessage);
    }

    [Fact]
    public async Task AScanShowsTheCountsBySeverityTheChecksThatDidNotRunAndTheirReason()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        var options = Assert.Single(h.Service.ScanOptions);
        Assert.Equal(SiteHygieneLabels.Areas.Count, options.Areas.Count);
        Assert.False(options.ProbeContentSources);
        Assert.Equal(TimeSpan.Zero, options.PauseBetweenCalls);
        Assert.StartsWith("1 error, at least 6 warnings, at least 1 info. 0 suppressed. Last scan ", vm.SummaryLine);
        Assert.Contains("2 checks did not run.", vm.SummaryLine);
        Assert.DoesNotContain("stored result", vm.SummaryLine);
        Assert.Contains("APP-04 (not run: The content source probe is off.)", vm.NotRunText);
        Assert.Contains("DEV-03 (needs the site database: ", vm.NotRunText);
        Assert.Equal("Not known", vm.CheckRows.Single(c => c.CheckId == "APP-04").Findings);
        Assert.Equal("Needs the site database", vm.CheckRows.Single(c => c.CheckId == "DEV-03").State);
        Assert.Equal("2", vm.CheckRows.Single(c => c.CheckId == "APP-01").Findings);
        var all = vm.CategoryRows[^1];
        Assert.Equal(("All categories", "1", "At least 6", "At least 1", "At least 8", "2"), (all.Category, all.Error, all.Warning, all.Info, all.Total, all.NotRun));
        Assert.Equal("Error", SiteHygieneLabels.SeverityName(vm.FindingRows[0].Finding.Severity));
        Assert.Empty(h.ScanStore.Saved);
        Assert.Contains("1 check did not run, so the result was not stored as the last scan.", vm.StatusMessage);
        Assert.DoesNotContain(h.Log.Entries, e => e.Message.Contains("Contoso", StringComparison.Ordinal) || e.Message.Contains("MCM000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APartialScanUsesTheCheckedAreasAndIsNotStoredAsTheLastScan()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.OpenAsync();
        vm.ClearAreasCommand.Execute(null);
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(VM.SelectArea, vm.StatusMessage);
        Assert.Equal(0, h.Service.ScanCalls);
        vm.AreaChoices.Single(a => a.Id == "Applications").IsChecked = true;
        vm.AreaChoices.Single(a => a.Id == "Collections").IsChecked = true;
        vm.PauseMilliseconds = 250;
        vm.ProbeContentSources = true;
        Assert.StartsWith("Scan areas: 2 of 18.", vm.AreasText);
        await vm.ScanCommand.ExecuteAsync(null);
        var options = Assert.Single(h.Service.ScanOptions);
        Assert.Equal(["Applications", "Collections"], options.Areas.Order());
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.PauseBetweenCalls);
        Assert.True(options.ProbeContentSources);
        Assert.Empty(h.ScanStore.Saved);
        Assert.Contains("not stored as the last scan", vm.StatusMessage);
    }

    [Fact]
    public async Task ACancelledScanThatEndsLaterIsDiscardedAndTheEarlierResultStays()
    {
        var h = new SiteHygienePageHarness();
        h.ScanStore.Stored = h.Service.Result();
        var vm = await h.OpenAsync();
        h.Service.Findings.RemoveAt(0);
        h.Service.ScanGate = new TaskCompletionSource();
        h.Service.IgnoreCancel = true;
        var scan = vm.ScanCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.IsScanning && h.Service.ScanCalls == 1);
        Assert.False(vm.ScanCommand.CanExecute(null));
        vm.CancelScanCommand.Execute(null);
        Assert.Equal(VM.ScanCancelling, vm.StatusMessage);
        h.Service.ScanGate.SetResult();
        await scan;
        Assert.Equal(VM.ScanCancelled, vm.StatusMessage);
        Assert.True(vm.IsStoredResult);
        Assert.Equal(8, vm.FindingRows.Count);
        Assert.False(vm.IsScanning);
        Assert.Empty(h.ScanStore.Saved);
    }

    [Fact]
    public async Task AScanThatEndsAfterTheSiteSettingsChangedIsDiscarded()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.OpenAsync();
        h.Service.ScanGate = new TaskCompletionSource();
        var scan = vm.ScanAsync();
        await WaitUntil(() => h.Service.ScanCalls == 1);
        h.Settings = new AppSettings { SiteCode = "LAB", ProviderMachine = "cm02.contoso.com" };
        h.Service.ScanGate.SetResult();
        await scan;
        Assert.Equal(VM.SiteChanged, vm.StatusMessage);
        Assert.False(vm.HasResult);
    }

    [Fact]
    public async Task ATruncatedReadMarksEveryCountNotKnownAndNoCheckRanNeverReadsAsZero()
    {
        var h = new SiteHygienePageHarness();
        h.Service.Truncated = true;
        var vm = await h.ScannedAsync();
        Assert.StartsWith("The read was truncated, so the counts are not known.", vm.SummaryLine);
        Assert.All(vm.CategoryRows, r => Assert.Equal("Not known", r.Total));

        var none = new SiteHygienePageHarness();
        none.Service.Findings.Clear();
        for (var i = 0; i < none.Service.Checks.Count; i++) none.Service.Checks[i] = none.Service.Checks[i] with { State = "Failed", Reason = "The read failed." };
        var vm2 = await none.ScannedAsync();
        Assert.StartsWith("No check ran, so the counts are not known.", vm2.SummaryLine);
        Assert.DoesNotContain("0 errors", vm2.SummaryLine);
        Assert.All(vm2.CategoryRows, r => Assert.Equal("Not known", r.Error));
    }

    [Fact]
    public async Task EveryFilterNarrowsTheRowsAndTheCountSaysHowMany()
    {
        var h = new SiteHygienePageHarness();
        h.ScanStore.Stored = h.Service.Result() with { Findings = h.Service.Findings.Where(f => f != h.UnusedApp).ToList() };
        var vm = await h.ScannedAsync();
        vm.CategoryFilter = "Collections";
        Assert.Equal(["COL-01", "COL-05"], vm.FindingRows.Select(r => r.CheckId).Order());
        Assert.Equal("2 of 8 findings", vm.FindingCount);
        vm.CategoryFilter = VM.AllText;
        vm.SeverityFilter = "Error";
        Assert.Equal("APP-02", Assert.Single(vm.FindingRows).CheckId);
        vm.SeverityFilter = VM.AllText;
        vm.FindingText = "smbios";
        Assert.Equal("DEV-02", Assert.Single(vm.FindingRows).CheckId);
        vm.FindingText = "mcm00012";
        Assert.Equal("COL-01", Assert.Single(vm.FindingRows).CheckId);
        vm.FindingText = "";
        Assert.Equal(8, vm.FindingRows.Count);
        Assert.Equal("New", Row(vm, h.UnusedApp).New);
        Assert.Equal("", Row(vm, h.UnusedApp2).New);
    }

    [Fact]
    public async Task TheSelectionIsKeptByKeyAcrossAFilterASortAndANewScan()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        Select(vm, h.UnusedApp, h.EmptyCollection);
        vm.CategoryFilter = "Applications";
        Assert.Equal([SiteHygieneLabels.Key(h.UnusedApp)], vm.SelectedFindings.Select(r => r.Key));
        vm.CategoryFilter = VM.AllText;
        Assert.Equal(2, vm.SelectedFindings.Count);
        vm.Sort(VM.FindingsGrid, nameof(FindingGridRow.ObjectName));
        Assert.Equal(2, vm.SelectedFindings.Count);
        Assert.Same(Row(vm, h.EmptyCollection), vm.DetailFinding);
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(new[] { SiteHygieneLabels.Key(h.UnusedApp), SiteHygieneLabels.Key(h.EmptyCollection) }.Order(), vm.SelectedFindings.Select(r => r.Key).Order());
        Assert.Equal(SiteHygieneLabels.Key(h.EmptyCollection), vm.DetailFinding!.Key);
        Assert.Contains("Remove-CMCollection -Id MCM00012", vm.DetailFixText);
    }

    [Fact]
    public async Task SuppressSavesACommentByCheckAndObjectKeyHidesTheRowsAndUnsuppressShowsThemAgain()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        Select(vm, h.UnusedApp, h.Boundary);
        await vm.SuppressCommand.ExecuteAsync(null);
        Assert.Equal(2, h.Suppressions.Items.Count);
        Assert.All(h.Suppressions.Items, s => Assert.Equal("Kept for the audit in November.", s.Comment));
        Assert.Contains(h.Suppressions.Items, s => s is { CheckId: "BND-01", ObjectKey: "16777217" });
        Assert.Equal(6, vm.FindingRows.Count);
        Assert.Contains("2 suppressed.", vm.SummaryLine);
        Assert.StartsWith("1 error, at least 4 warnings", vm.SummaryLine);
        Assert.Equal("6 findings, 2 suppressed and hidden", vm.FindingCount);
        Assert.Equal("Suppressed 2 findings. Show suppressed lists them.", vm.StatusMessage);

        vm.ShowSuppressed = true;
        Assert.Equal(8, vm.FindingRows.Count);
        Assert.Equal("Yes", Row(vm, h.Boundary).Suppressed);
        Select(vm, h.Boundary);
        Assert.Contains("suppressed on ", vm.DetailTitle);
        Assert.Contains("Kept for the audit in November.", vm.DetailTitle);
        await vm.UnsuppressCommand.ExecuteAsync(null);
        Assert.Single(h.Suppressions.Items);
        Assert.Equal("", Row(vm, h.Boundary).Suppressed);
        Assert.Contains("1 suppressed.", vm.SummaryLine);

        h.Dialogs.Comment = null;
        Select(vm, h.Duplicate);
        await vm.SuppressCommand.ExecuteAsync(null);
        Assert.Equal(VM.NotConfirmed, vm.StatusMessage);
        Assert.Single(h.Suppressions.Items);
    }

    [Fact]
    public async Task ASuppressionStoreThatFailsChangesNoRowAndGivesThePlainReason()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        h.Suppressions.FailAfter = 0;
        Select(vm, h.UnusedApp, h.UnusedApp2);
        await vm.SuppressCommand.ExecuteAsync(null);
        Assert.Empty(h.Suppressions.Items);
        Assert.Equal(VM.SuppressFailed + " No suppression was changed.", vm.StatusMessage);
        Assert.DoesNotContain("Access", vm.StatusMessage);
        Assert.Equal(8, vm.FindingRows.Count);
    }

    [Fact]
    public async Task ASuppressionOfAnotherSessionIsReadFromTheStoreAndNotFromTheScan()
    {
        var h = new SiteHygienePageHarness();
        h.Suppressions.Items.Add(new Suppression("DEV-02", "16777221", "Known duplicate.", DateTime.UtcNow));
        var vm = await h.ScannedAsync();
        Assert.Equal(7, vm.FindingRows.Count);
        Assert.Contains("1 suppressed.", vm.SummaryLine);
    }

    [Fact]
    public async Task ADeleteShowsThePlanFromThePreparedChangeSendsTheSecondConfirmationAndTakesTheFixedRowOut()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        Select(vm, h.EmptyCollection);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Empty(h.Dialogs.Shown);
        var plan = Assert.Single(h.Plans.Shown);
        Assert.True(plan.IsDestructive);
        Assert.Equal(SiteHygieneText.DeleteConfirmation(1), plan.ExtraConfirmation);
        Assert.Contains("cannot be undone", plan.ExtraConfirmation);
        Assert.Contains("1 object", plan.ExtraConfirmation);
        Assert.Equal("Remove-CMCollection -Id MCM00012 -Force", plan.Body);
        Assert.Equal(SiteHygieneText.BodyName, plan.BodyName);
        Assert.Equal("Collection SHL Empty Collection (MCM00012)", Assert.Single(plan.Items).Target);
        Assert.Equal("Deletes 1 objects. The delete cannot be undone.", plan.Notes[0]);
        var sent = Assert.Single(h.Service.Applied);
        Assert.True(sent.DeleteConfirmed);
        Assert.Equal("fp-1", sent.Fingerprint);
        Assert.Equal("Done", Assert.Single(vm.LastResults).OutcomeText);
        Assert.DoesNotContain(vm.FindingRows, r => r.CheckId == "COL-01");
        Assert.Contains("1 finding fixed since the scan.", vm.SummaryLine);
        Assert.StartsWith("Site Hygiene: delete collection (cannot be undone): 1 done.", vm.StatusMessage);
        var entry = h.History.Entries.Single();
        Assert.Equal(SiteHygieneWriter.ToolName, entry.Tool);
        Assert.Equal(1, entry.Counts!["changed"]);
        Assert.DoesNotContain(h.Log.Entries, e => e.Message.Contains("SHL", StringComparison.Ordinal) || e.Message.Contains("MCM00012", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABulkFixShowsThePickerAndOnlyTheCheckedObjectsArePreparedAndPlanned()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        Select(vm, h.UnusedApp, h.UnusedApp2);
        h.Dialogs.OnFindings = p => p.Rows.Single(r => r.Finding == h.UnusedApp2).IsChecked = false;
        await vm.RunFixCommand.ExecuteAsync(null);
        var picker = Assert.Single(h.Dialogs.Shown);
        Assert.Equal("Delete application", picker.Title);
        Assert.Contains("cannot be undone", picker.Intro);
        Assert.Equal([h.UnusedApp], Assert.Single(h.Service.PrepareRequests).Findings);
        Assert.Equal([h.UnusedApp], Assert.Single(h.Service.Applied).Findings);
        Assert.Contains(vm.FindingRows, r => r.Finding == h.UnusedApp2);

        h.Dialogs.CancelPicker = true;
        Select(vm, h.UnusedApp2, h.RetiredDeployed);
        h.Service.Findings.Add(h.UnusedApp);
        await vm.ScanCommand.ExecuteAsync(null);
        Select(vm, h.UnusedApp, h.UnusedApp2);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Equal(VM.NotConfirmed, vm.StatusMessage);
        Assert.Single(h.Service.PrepareRequests);
    }

    [Fact]
    public async Task MixedFixesReportOnlyFindingsAndSuppressedFindingsMakeNoPlan()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        Select(vm, h.UnusedApp, h.EmptyCollection);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Equal(VM.MixedKinds([HygieneFixKind.RemoveApplication, HygieneFixKind.RemoveCollection]), vm.StatusMessage);
        Select(vm, h.Boundary);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Equal(SiteHygieneLabels.ReportOnlyReason(HygieneFixKind.None), vm.StatusMessage);
        vm.SetSelectedFindings([]);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Equal(VM.SelectFindings, vm.StatusMessage);
        Select(vm, h.Duplicate);
        await vm.SuppressCommand.ExecuteAsync(null);
        vm.ShowSuppressed = true;
        Select(vm, h.Duplicate);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Equal(VM.SuppressedSelection, vm.StatusMessage);
        Assert.Equal(0, h.Service.PrepareCalls);
        Assert.Empty(h.Plans.Shown);
    }

    [Fact]
    public async Task ADeleteWithoutTheSecondBoxCannotRunAndSendsNothing()
    {
        var h = new SiteHygienePageHarness();
        h.Plans.AcknowledgeExtra = false;
        var vm = await h.ScannedAsync();
        Select(vm, h.UnusedApp);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Single(h.Plans.Shown);
        Assert.False(h.Plans.LastOutcome!.Confirmed);
        Assert.Empty(h.Service.Applied);
        Assert.Equal(VM.NotConfirmed, vm.StatusMessage);
        Assert.Contains(vm.FindingRows, r => r.Finding == h.UnusedApp);
    }

    [Fact]
    public async Task AFindingThatStopsHoldingBetweenPlanAndConfirmIsSkippedAndStaysInTheList()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        h.Service.BetweenPrepareAndApply = () => h.Service.StillHolds = f => f != h.UnusedApp;
        Select(vm, h.UnusedApp);
        await vm.RunFixCommand.ExecuteAsync(null);
        var result = Assert.Single(vm.LastResults);
        Assert.Equal("Skipped", result.OutcomeText);
        Assert.Equal(FakeSiteHygieneService.NoLongerHolds, result.Message);
        Assert.Contains(vm.FindingRows, r => r.Finding == h.UnusedApp);
        Assert.Equal(0, h.History.Entries.Single().Counts!["changed"]);
    }

    [Fact]
    public async Task AFindingThatStoppedHoldingBeforeThePlanMakesNoPlanAndSaysWhy()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        h.Service.StillHolds = _ => false;
        Select(vm, h.UnusedApp);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Empty(h.Plans.Shown);
        Assert.StartsWith(SiteHygieneText.NothingLeft, vm.StatusMessage);
        Assert.Contains("Left out: Application Contoso Fonts 2.1 (16777300). Skipped: the finding no longer holds", vm.StatusMessage);
    }

    [Fact]
    public async Task ADeleteAboveTheCapIsRefusedWithTheMessageOfTheService()
    {
        var h = SiteHygienePageHarness.Large(0);
        var many = Enumerable.Range(0, FakeSiteHygieneService.Cap + 1).Select(i =>
            Finding("APP-01", "Applications", HygieneSeverity.Warning, "Application", (16_777_300 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), $"App {i}", HygieneFixKind.RemoveApplication)).ToList();
        h.Service.Findings.AddRange(many);
        var vm = await h.ScannedAsync();
        vm.SetSelectedFindings(vm.FindingRows);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Empty(h.Plans.Shown);
        Assert.Equal($"The plan cannot run. Nothing was changed. A run deletes at most {FakeSiteHygieneService.Cap} objects. Filter the findings to {FakeSiteHygieneService.Cap} or fewer.", vm.StatusMessage);
    }

    [Fact]
    public async Task PartialResultsKeepEachOutcomeAndOnlyTheDoneRowsLeaveTheList()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        h.Service.Fail = f => f == h.UnusedApp2 ? @"Failed: the provider at \\cm01 refused the delete." : null;
        Select(vm, h.UnusedApp, h.UnusedApp2);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Equal(["Done", "Failed"], vm.LastResults.Select(r => r.OutcomeText));
        Assert.DoesNotContain(vm.FindingRows, r => r.Finding == h.UnusedApp);
        Assert.Contains(vm.FindingRows, r => r.Finding == h.UnusedApp2);
        Assert.Equal(SiteOperationOutcome.PartlySucceeded, h.History.Entries.Single().Outcome);
    }

    [Fact]
    public async Task OnlyTheFinalOutcomeIsAResultSoAFailedReadBackKeepsTheFinding()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        h.Service.ReadBackFails = f => f == h.RefreshType;
        Select(vm, h.RefreshType);
        await vm.RunFixCommand.ExecuteAsync(null);
        var plan = Assert.Single(h.Plans.Shown);
        Assert.False(plan.IsDestructive);
        Assert.Null(plan.ExtraConfirmation);
        Assert.Equal("Failed", Assert.Single(vm.LastResults).OutcomeText);
        Assert.Contains(vm.FindingRows, r => r.Finding == h.RefreshType);
        Assert.False(Assert.Single(h.Service.Applied).DeleteConfirmed);
        Assert.Equal(0, h.History.Entries.Single().Counts!["changed"]);
    }

    [Fact]
    public async Task APlanIsDiscardedWhenTheSelectionOrTheResultChangedDuringTheCheck()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        h.Service.PrepareGate = new TaskCompletionSource();
        Select(vm, h.UnusedApp);
        var run = vm.RunFixCommand.ExecuteAsync(null);
        await WaitUntil(() => h.Service.PrepareCalls == 1);
        Assert.True(vm.IsWriting);
        Assert.False(vm.RunFixCommand.CanExecute(null));
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Equal(1, h.Service.PrepareCalls);
        Select(vm, h.UnusedApp2);
        h.Service.PrepareGate.SetResult();
        await run;
        Assert.Equal(VM.SelectionChanged, vm.StatusMessage);
        Assert.Empty(h.Plans.Shown);

        h.Service.PrepareGate = new TaskCompletionSource();
        var second = vm.RunFixCommand.ExecuteAsync(null);
        await WaitUntil(() => h.Service.PrepareCalls == 2);
        await vm.ScanAsync();
        h.Service.PrepareGate.SetResult();
        await second;
        Assert.Equal(VM.ResultChanged, vm.StatusMessage);
        Assert.Empty(h.Plans.Shown);
        Assert.Empty(h.Service.Applied);
    }

    [Theory]
    [MemberData(nameof(FixKinds))]
    public async Task EveryFixKindHasAPlanFromThePreparedChangeAndOnlyADeleteAsksTheSecondBox(HygieneFixKind kind)
    {
        var h = new SiteHygienePageHarness();
        var findings = new[] { "16777800", "16777801" }.Select(k => Finding("XYZ-01", "Applications", HygieneSeverity.Info, "Object", k, "Object " + k, kind, $"Do-Thing -Id {k}")).ToList();
        var prepared = await h.Service.PrepareAsync(new HygieneChange(kind, findings, false, [], [], null), CancellationToken.None);
        var (plan, problem) = SiteHygieneText.BuildPlan(prepared);
        Assert.Null(problem);
        var delete = kind.ToString().StartsWith("Remove", StringComparison.Ordinal);
        Assert.Equal(delete, plan!.Plan.IsDestructive);
        Assert.Equal(delete, plan.Plan.ExtraConfirmation is not null);
        Assert.Equal(2, plan.Plan.Items.Count);
        Assert.All(plan.Plan.Items, i => Assert.Equal(SiteHygieneLabels.FixLabel(kind), i.Action));
        Assert.Equal("Do-Thing -Id 16777800" + Environment.NewLine + "Do-Thing -Id 16777801", plan.Plan.Body);
        // The service states the one-at-a-time rule in the impact lines; the page adds no second copy.
        Assert.DoesNotContain(plan.Plan.Notes, n => n.Contains("one at a time", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(kind.ToString(), SiteHygieneLabels.FixLabel(kind));
    }

    public static TheoryData<HygieneFixKind> FixKinds()
    {
        var data = new TheoryData<HygieneFixKind>();
        foreach (var kind in Enum.GetValues<HygieneFixKind>().Where(k => k != HygieneFixKind.None)) data.Add(kind);
        return data;
    }

    [Fact]
    public void ThePlanTextComesFromThePreparedChangeAndNotFromTheScanRows()
    {
        var h = new SiteHygienePageHarness();
        var fresh = h.EmptyCollection with { ObjectName = "SHL Empty Collection (renamed)", Evidence = "Read again: 0 members, no deployment.", FixText = "Remove-CMCollection -Id MCM00012 -Force # fresh" };
        var prepared = new HygieneChange(HygieneFixKind.RemoveCollection, [fresh], false, ["Read at Prepare."], [new PreflightCheck("References", "Warn", "One query rule names the collection.")], "fp");
        var (plan, _) = SiteHygieneText.BuildPlan(prepared);
        Assert.Equal("Collection SHL Empty Collection (renamed) (MCM00012)", plan!.Plan.Items[0].Target);
        Assert.Equal("COL-01: Read again: 0 members, no deployment.", plan.Plan.Items[0].Detail);
        Assert.Equal("References: Warn. One query rule names the collection.", plan.Plan.Notes[0]);
        Assert.Equal("Read at Prepare.", plan.Plan.Notes[1]);
        Assert.EndsWith("# fresh", plan.Plan.Body);

        var blocked = prepared with { Checks = [new PreflightCheck("Audit", "Fail", "The audit log cannot be written, so no delete runs.")] };
        Assert.Equal("The plan cannot run. Nothing was changed. The audit log cannot be written, so no delete runs.", SiteHygieneText.BuildPlan(blocked).Problem);
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
    public async Task AFixLeftRunningBlocksTheNextFixAndTakesItsRowsOutWhenItEndsAfterTheOperatorLeft()
    {
        var h = new SiteHygienePageHarness();
        var detaching = new DetachingPlanDialogs();
        h.PlanDialogs = detaching;
        h.Service.InConfirmedRun = null;
        h.Service.ApplyGate = new TaskCompletionSource();
        var vm = await h.ScannedAsync();
        Select(vm, h.UnusedApp);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Equal(VM.BackgroundRunText, vm.StatusMessage);
        Assert.True(vm.IsWriting);
        Assert.False(vm.SuppressCommand.CanExecute(null));
        await vm.OnNavigatedFromAsync();
        h.Service.ApplyGate.SetResult();
        await detaching.Run!;
        await WaitUntil(() => !vm.IsWriting);
        Assert.DoesNotContain(vm.FindingRows, r => r.Finding == h.UnusedApp);
        Assert.Equal("The fix that continued in the background has ended: 1 finding fixed.", vm.StatusMessage);
        await vm.OnNavigatedToAsync();
        Assert.Contains("1 finding fixed since the scan.", vm.SummaryLine);
    }

    [Fact]
    public async Task LeavingThePageDuringAScanKeepsItRunningAndTheReturnShowsItsResultAndTheSummaryLine()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.OpenAsync();
        h.Service.ScanGate = new TaskCompletionSource();
        var scan = vm.ScanAsync();
        await WaitUntil(() => h.Service.ScanCalls == 1);
        await vm.OnNavigatedFromAsync();
        h.Service.ScanGate.SetResult();
        await scan;
        Assert.True(vm.HasResult);
        foreach (var tab in new[] { VM.FindingsTab, VM.RelationsTab, VM.SummaryTab, VM.OptionsTab })
        {
            vm.SelectedTabIndex = tab;
            await vm.OnNavigatedFromAsync();
            await vm.OnNavigatedToAsync();
            Assert.StartsWith("1 error, at least 6 warnings", vm.SummaryLine);
        }
        Assert.Equal(1, h.Service.ScanCalls);
    }

    [Fact]
    public async Task ExportWritesTheRowsTheFiltersShowNamesTheChecksThatDidNotRunAndRefusesWithoutRows()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.OpenAsync();
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(VM.ExportNotScanned, vm.StatusMessage);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.CategoryFilter = "Devices";
        await vm.ExportHtmlCommand.ExecuteAsync(null);
        var (format, report) = Assert.Single(h.Reports.Written);
        Assert.Equal("html", format);
        Assert.Equal("Findings", report.View);
        var row = Assert.Single(report.Rows);
        Assert.Equal("Two records share the SMBIOS GUID 4C4C4544-0042.\nThe newer record is 16777230.", row[6]);
        Assert.Equal("Category: Devices.", report.Filter);
        Assert.Equal(["APP-04: Not run. The content source probe is off.", "DEV-03: Needs the site database. The check reads inventory that only the site database holds."], report.ChecksNotRun);
        Assert.False(report.Truncated);

        vm.SelectedTabIndex = VM.SummaryTab;
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal("Check status", h.Reports.Written[^1].Report.View);
        Assert.Equal(9, h.Reports.Written[^1].Report.Rows.Count);

        vm.SelectedTabIndex = VM.RelationsTab;
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(VM.ExportNoRelations, vm.StatusMessage);
        vm.SelectedTabIndex = VM.OptionsTab;
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(VM.ExportOptions, vm.StatusMessage);
        Assert.Equal("The Options tab has no rows. Select the Findings, Relationships, Live, Summary or History tab to export.", vm.StatusMessage);

        vm.SelectedTabIndex = VM.FindingsTab;
        vm.FindingText = "no such finding";
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(VM.NothingToExport, vm.StatusMessage);
        Assert.Equal(2, h.Reports.Written.Count);

        vm.FindingText = "";
        h.Reports.Fails = true;
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(VM.ReportFailed("CSV"), vm.StatusMessage);
        Assert.Equal(@"C:\Data\SiteHygiene\Reports", vm.LastReportFolder);
        vm.OpenReportsFolderCommand.Execute(null);
        Assert.Equal([@"C:\Data\SiteHygiene\Reports"], h.Dialogs.OpenedFolders);
    }

    [Fact]
    public async Task TheOptionsTabMessageNamesEachTabThatExportsAndOnlyThose()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        foreach (var tab in new[] { VM.FindingsTab, VM.RelationsTab, VM.SummaryTab, VM.OptionsTab, VM.HistoryTab })
        {
            vm.SelectedTabIndex = tab;
            var (_, problem) = vm.ActiveReport();
            Assert.Equal(tab == VM.OptionsTab, problem == VM.ExportOptions);
            Assert.Equal(tab != VM.OptionsTab, VM.ExportTabs.Any(t => t.Index == tab));
        }
        Assert.All(VM.ExportTabs, t => Assert.Contains($" {t.Name}", VM.ExportOptions, StringComparison.Ordinal));
        Assert.Equal("", VM.OrList([]));
        Assert.Equal("Findings", VM.OrList(["Findings"]));
        Assert.Equal("Findings or History", VM.OrList(["Findings", "History"]));
    }

    [Fact]
    public async Task ATruncatedScanIsNamedInTheExportHeader()
    {
        var h = new SiteHygienePageHarness();
        h.Service.Truncated = true;
        var vm = await h.ScannedAsync();
        await vm.ExportCsvCommand.ExecuteAsync(null);
        var report = Assert.Single(h.Reports.Written).Report;
        Assert.True(report.Truncated);
        Assert.StartsWith("The read was truncated: the findings are not complete.", report.Note);
    }

    [Fact]
    public async Task RelationshipsAreReadOnRequestFilteredAndBuiltIntoChainsWithACycleMark()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        Assert.Equal(0, h.Service.RelationCalls);
        Assert.Equal(VM.RelationsNotRead, vm.RelationsSummary);
        await vm.LoadRelationsCommand.ExecuteAsync(null);
        Assert.Equal("5 relationships: 4 supersedence, 1 dependency.", vm.RelationsSummary);
        Assert.Equal(5, vm.EdgeRows.Count);
        vm.EdgeKind = "Depends on";
        var edge = Assert.Single(vm.EdgeRows);
        Assert.Equal("Contoso CRM Client (16777600)", edge.From);
        Assert.Equal("✓ Healthy", edge.Health);
        vm.EdgeKind = VM.AllText;
        vm.EdgeText = "retired";
        Assert.Equal("! Retired", Assert.Single(vm.EdgeRows).Health);
        vm.SelectedEdge = vm.EdgeRows[0];
        vm.EdgeText = "";
        Assert.Equal("! Retired", vm.SelectedEdge!.Health);

        var (supersedence, dependencies) = (vm.TreeRoots[0], vm.TreeRoots[1]);
        Assert.Equal("Supersedence: 2 chains", supersedence.Text);
        Assert.Equal("Dependencies: 1 chain", dependencies.Text);
        var sevenZip = supersedence.Children[0];
        Assert.Equal("7-Zip 26.04 (16777500)", sevenZip.Text);
        Assert.True(Assert.Single(sevenZip.Children).IsPlaceholder);
        sevenZip.IsExpanded = true;
        var older = Assert.Single(sevenZip.Children);
        Assert.Equal("Supersedes 7-Zip 25.01 (16777499), ✓ Healthy", older.Text);
        older.IsExpanded = true;
        Assert.Equal("Supersedes 7-Zip 24.08 (16777498), ! Retired", Assert.Single(older.Children).Text);

        var loop = supersedence.Children[1];
        Assert.Equal("Loop A (16777700)", loop.Text);
        loop.IsExpanded = true;
        loop.Children[0].IsExpanded = true;
        var mark = Assert.Single(loop.Children[0].Children);
        Assert.StartsWith("↻ supersedes Loop A (16777700) again", mark.Text);
        Assert.Empty(mark.Children);
        vm.SelectedTabIndex = VM.RelationsTab;
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(5, Assert.Single(h.Reports.Written, w => w.Report.View == "Relationships").Report.Rows.Count);
        Assert.False(h.Reports.Written[^1].Report.Truncated);
        h.Service.RelationsTruncated = true;
        await vm.LoadRelationsCommand.ExecuteAsync(null);
        Assert.EndsWith("The read was truncated, so the list and the chains are not complete. The site holds more relationships than the read takes.", vm.RelationsSummary);
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.True(h.Reports.Written[^1].Report.Truncated);
    }

    [Fact]
    public void TheTreeOfFiftyThousandEdgesIsBuiltInLinearWorkAndOpensOnlyTheLinesTheOperatorExpands()
    {
        var edges = Enumerable.Range(0, 50_000).Select(i => new RelationEdge($"{i}", $"App {i}", $"{i + 1}", $"App {i + 1}", "Supersedes", "Healthy")).ToList();
        var roots = SiteHygieneTreeText.Build(edges);
        var chain = Assert.Single(roots[0].Children);
        Assert.Equal("App 0 (0)", chain.Text);
        Assert.True(Assert.Single(chain.Children).IsPlaceholder);
        var node = chain;
        for (var i = 0; i < 50; i++)
        {
            node.IsExpanded = true;
            node = Assert.Single(node.Children);
        }
        Assert.Equal("Supersedes App 50 (50), ✓ Healthy", node.Text);
        var ring = SiteHygieneTreeText.Build([new RelationEdge("a", "A", "b", "B", "DependsOn", ""), new RelationEdge("b", "B", "a", "A", "DependsOn", "")]);
        Assert.Equal("Dependencies: 1 chain", ring[1].Text);
        Assert.Equal("Depends on B (b), ? Unknown", RelationChild(ring[1].Children[0]));
    }

    private static string RelationChild(RelationTreeNode node)
    {
        node.IsExpanded = true;
        return node.Children[0].Text;
    }

    [Fact]
    public async Task TheChoicesRoundTripAndAnOlderOrStrangeFileGivesTheDefaults()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.OpenAsync();
        vm.SelectedTabIndex = VM.SummaryTab;
        vm.RelationsTabIndex = VM.TreeTab;
        vm.CategoryFilter = "Devices";
        vm.SeverityFilter = "Warning";
        vm.FindingText = "CLIENT";
        vm.ShowSuppressed = true;
        vm.PauseMilliseconds = 500;
        vm.ProbeContentSources = true;
        vm.AreaChoices.Single(a => a.Id == "Devices").IsChecked = false;
        vm.Sort(VM.FindingsGrid, nameof(FindingGridRow.Severity));
        Assert.True(await vm.SaveAsync());
        var saved = h.Saved!;
        Assert.Equal((VM.SummaryTab, 1, "Devices", "Warning", "CLIENT", true, 500, true), (saved.SelectedTab, saved.RelationsTab, saved.Category, saved.Severity, saved.FindingText, saved.ShowSuppressed, saved.PauseMilliseconds, saved.ProbeContentSources));
        Assert.Equal(17, saved.Areas!.Count);
        Assert.DoesNotContain("Devices", saved.Areas);
        Assert.Equal("Severity|Ascending", saved.Sorts![VM.FindingsGrid]);

        var again = new SiteHygienePageHarness { Saved = saved };
        var vm2 = await again.OpenAsync();
        Assert.Equal((VM.SummaryTab, VM.TreeTab, "Devices", "Warning", 500), (vm2.SelectedTabIndex, vm2.RelationsTabIndex, vm2.CategoryFilter, vm2.SeverityFilter, vm2.PauseMilliseconds));
        Assert.False(vm2.AreaChoices.Single(a => a.Id == "Devices").IsChecked);
        Assert.Equal(("Severity", true), vm2.SortOf(VM.FindingsGrid));

        var odd = new SiteHygienePageHarness
        {
            Saved = new SiteHygieneSettings { SelectedTab = 9, Category = "Nope", Severity = null, PauseMilliseconds = 7, Areas = ["OldArea"], Sorts = new() { ["findings"] = "Bad Column!|Up" }, FindingText = null }
        };
        var vm3 = await odd.OpenAsync();
        Assert.Equal((VM.HistoryTab, VM.AllText, VM.AllText, 0, ""), (vm3.SelectedTabIndex, vm3.CategoryFilter, vm3.SeverityFilter, vm3.PauseMilliseconds, vm3.FindingText));
        Assert.All(vm3.AreaChoices, a => Assert.True(a.IsChecked));
        Assert.Null(vm3.SortOf(VM.FindingsGrid));
    }

    [Fact]
    public async Task CopyPutsTheFixTextOnTheClipboardAndAReportOnlyFindingHasNothingToCopy()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        Select(vm, h.Backup);
        Assert.True(vm.CopyFixTextCommand.CanExecute(null));
        vm.CopyFixTextCommand.Execute(null);
        Assert.Equal(["Set-CMSiteMaintenanceTask -SiteCode MCM -MaintenanceTaskName 'Backup SMS Site Server' -Enabled $true"], h.Dialogs.Clipboard);
        Select(vm, h.Boundary);
        Assert.False(vm.CopyFixTextCommand.CanExecute(null));
        Assert.Equal(SiteHygieneLabels.ReportOnlyReason(HygieneFixKind.None), vm.DetailFixText);
        vm.SetSelectedFindings([]);
        Assert.Equal(VM.NoFindingSelected, vm.DetailTitle);
    }

    [Fact]
    public async Task AScanFailureKeepsTheEarlierResultAndShowsAFixedMessage()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        h.Service.ScanFailure = new InvalidOperationException(@"Get-CMApplication failed at \\cm01\SMS_MCM with password=hunter2");
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(VM.ScanFailed, vm.StatusMessage);
        Assert.Equal(8, vm.FindingRows.Count);
        Assert.DoesNotContain(h.Log.Entries, e => e.Message.Contains("hunter2", StringComparison.Ordinal));
        h.Service.ScanFailure = new HygieneServiceException("The SMS Provider refused the read of the boundaries.");
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal("The SMS Provider refused the read of the boundaries.", vm.StatusMessage);
    }

    [Fact]
    public async Task AStoredResultThatCannotBeSavedSaysTheNextStartShowsNoStoredResult()
    {
        var h = new SiteHygienePageHarness();
        h.CompleteScan();
        h.ScanStore.SaveFails = true;
        var vm = await h.ScannedAsync();
        Assert.EndsWith("The result could not be stored as the last scan, so the next start shows no stored result.", vm.StatusMessage);
        Assert.True(vm.HasResult);
    }

    [Fact]
    public void TheFindingKeyUsesTheObjectKeySoTwoObjectsWithOneNameStayTwoRows()
    {
        var a = Finding("APP-01", "Applications", HygieneSeverity.Warning, "Application", "16777300", "Contoso Fonts", HygieneFixKind.RemoveApplication);
        var b = a with { ObjectKey = "16777301" };
        Assert.NotEqual(SiteHygieneLabels.Key(a), SiteHygieneLabels.Key(b));
        Assert.Equal(SiteHygieneLabels.Key(a), SiteHygieneLabels.Key(a with { ObjectName = "Renamed" }));
        Assert.Equal(SiteHygieneLabels.Key(a), SiteHygieneLabels.Key(a.CheckId.ToLowerInvariant(), a.ObjectKey));
        Assert.Equal(a.Key.ToUpperInvariant(), SiteHygieneLabels.Key(a));
    }

    [Fact]
    public async Task AReportOnlyFixKindDisablesRunFixWithTheReasonAndItsTextStaysCopyable()
    {
        var h = new SiteHygienePageHarness();
        var resume = Finding("SUP-03", "Relationships", HygieneSeverity.Warning, "Application", "ScopeId_1/Application_2", "Contoso Viewer 3", HygieneFixKind.ResumeApplication,
            "Resume-CMApplication -ModelName 'ScopeId_1/Application_2'");
        var guided = Finding("DPT-01", "Distribution Points", HygieneSeverity.Warning, "DistributionPoint", "cm01.contoso.com", "cm01.contoso.com", HygieneFixKind.None)
            with { Guidance = "Add the distribution point to a boundary group in the console." };
        h.Service.Findings.AddRange([resume, guided]);
        var vm = await h.ScannedAsync();
        Assert.Equal("Report only", Row(vm, resume).Fix);
        Assert.Equal("Report only", Row(vm, h.Boundary).Fix);
        Assert.Equal("Delete application", Row(vm, h.UnusedApp).Fix);

        Select(vm, resume);
        Assert.False(vm.RunFixCommand.CanExecute(null));
        Assert.True(vm.HasRunFixHint);
        Assert.Equal(SiteHygieneLabels.ReportOnlyReason(HygieneFixKind.ResumeApplication), vm.RunFixHint);
        Assert.Contains("reinstate application", vm.RunFixHint);
        Assert.Contains("Run fix is not available; the PowerShell text can be copied.", vm.DetailFixNote);
        Assert.Equal("Resume-CMApplication -ModelName 'ScopeId_1/Application_2'", vm.DetailFixText);
        Assert.True(vm.CopyFixTextCommand.CanExecute(null));
        vm.CopyFixTextCommand.Execute(null);
        Assert.Equal(["Resume-CMApplication -ModelName 'ScopeId_1/Application_2'"], h.Dialogs.Clipboard);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Equal(vm.RunFixHint, vm.StatusMessage);

        Select(vm, guided);
        Assert.False(vm.RunFixCommand.CanExecute(null));
        Assert.Equal("Guidance: Add the distribution point to a boundary group in the console.", vm.DetailFixNote);

        Select(vm, h.UnusedApp);
        Assert.True(vm.RunFixCommand.CanExecute(null));
        Assert.False(vm.HasRunFixHint);
        Assert.Equal(0, h.Service.PrepareCalls);
        foreach (var kind in new[] { HygieneFixKind.SetCollectionSchedule, HygieneFixKind.SetBoundaryGroup, HygieneFixKind.ResumeApplication, HygieneFixKind.None })
            Assert.False(SiteHygieneLabels.CanRun(kind));
    }

    [Fact]
    public async Task AMixedSelectionPlansTheOneFixAndNamesWhatWasLeftOutAndTwoFixesNeedTwoRuns()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.ScannedAsync();
        Select(vm, h.UnusedApp, h.EmptyCollection);
        Assert.False(vm.RunFixCommand.CanExecute(null));
        Assert.Equal(VM.MixedKinds([HygieneFixKind.RemoveApplication, HygieneFixKind.RemoveCollection]), vm.RunFixHint);

        Select(vm, h.UnusedApp, h.Boundary);
        Assert.True(vm.RunFixCommand.CanExecute(null));
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Equal([h.UnusedApp], Assert.Single(h.Service.PrepareRequests).Findings);
        Assert.Equal("Done", Assert.Single(vm.LastResults).OutcomeText);
        Assert.EndsWith("1 selected finding had no fix or was suppressed and was not in the plan.", vm.StatusMessage);
    }

    [Fact]
    public async Task InactiveDevicesAreOneRowPerDeviceSoEachHasItsOwnSuppressionAndPlanLine()
    {
        var h = new SiteHygienePageHarness();
        var devices = new[] { ("16777222", "CLIENT02"), ("16777223", "CLIENT03") }.Select(d => Finding("DEV-01", "Devices", HygieneSeverity.Warning, "Device", d.Item1, d.Item2,
            HygieneFixKind.RemoveDevice, $"Remove-CMDevice -ResourceId {d.Item1} -Force", evidence: $"{d.Item2} was last active 176 days ago.")).ToList();
        h.Service.Findings.AddRange(devices);
        var vm = await h.ScannedAsync();
        Assert.Equal(2, vm.FindingRows.Count(r => r.CheckId == "DEV-01"));

        Select(vm, devices[0]);
        await vm.SuppressCommand.ExecuteAsync(null);
        Assert.Equal(("DEV-01", "16777222"), (Assert.Single(h.Suppressions.Items).CheckId, h.Suppressions.Items[0].ObjectKey));
        Assert.Equal("16777223", Assert.Single(vm.FindingRows, r => r.CheckId == "DEV-01").Finding.ObjectKey);

        vm.ShowSuppressed = true;
        await vm.UnsuppressCommand.ExecuteAsync(null);
        Select(vm, devices[0], devices[1]);
        await vm.RunFixCommand.ExecuteAsync(null);
        var plan = Assert.Single(h.Plans.Shown);
        Assert.Equal(["Device CLIENT02 (16777222)", "Device CLIENT03 (16777223)"], plan.Items.Select(i => i.Target));
        Assert.Equal(SiteHygieneText.DeleteConfirmation(2), plan.ExtraConfirmation);
        Assert.Equal(["Done", "Done"], vm.LastResults.Select(r => r.OutcomeText));
    }

    [Fact]
    public async Task ChecksOfAreasThatWereNotScannedAreNotSelectedAndNeverCountAsChecksThatDidNotRun()
    {
        var h = new SiteHygienePageHarness();
        h.Service.Checks.Add(new HygieneCheckStatus(CheckId: "UPD-01", Name: "Update group with high expired-update ratio", Category: "Updates", State: HygieneCheckStates.NotSelected, Findings: 0,
            Reason: "No selected area covers the check."));
        h.Service.Checks.Add(new HygieneCheckStatus(CheckId: "UPD-03", Name: "Automatic deployment rule disabled, stale, or erroring", Category: "Updates", State: HygieneCheckStates.NotSelected,
            Findings: 0, Reason: "No selected area covers the check."));
        var vm = await h.ScannedAsync();
        Assert.Contains("2 checks did not run.", vm.SummaryLine);
        Assert.Contains("2 checks were not in the scanned areas or options.", vm.SummaryLine);
        Assert.DoesNotContain("UPD-01", vm.NotRunText);
        Assert.Equal(("Not selected", "Not known"), (vm.CheckRows.Single(c => c.CheckId == "UPD-01").State, vm.CheckRows.Single(c => c.CheckId == "UPD-01").Findings));
        var updates = vm.CategoryRows.Single(r => r.Category == "Updates");
        Assert.Equal(("Not known", "2"), (updates.Total, updates.NotRun));

        await vm.ExportCsvCommand.ExecuteAsync(null);
        var report = h.Reports.Written[^1].Report;
        Assert.Equal(2, report.ChecksNotRun.Count);
        Assert.Contains("2 checks were not in the scanned areas or options.", report.Note);
        Assert.Equal(h.Service.ScannedUtc, report.ScannedUtc);
    }

    [Fact]
    public async Task APreparedChangeWithSeveralObjectsPerFindingListsEveryObjectAndEachTakesTheOutcomeOfItsFinding()
    {
        var h = new SiteHygienePageHarness();
        h.Service.InConfirmedRun = null;
        var retired = h.RetiredDeployed;
        var targets = new[] { "{A1}", "{A2}" }.Select(id => new HygieneTarget(FindingKey: retired.Key, ObjectType: "Deployment", ObjectKey: id, ObjectName: "Contoso Printer Drivers", Detail: "to All Desktops " + id)).ToList();
        var left = h.UnusedApp with { FixKind = HygieneFixKind.RemoveApplicationDeployment };
        var prepared = new HygieneChange(HygieneFixKind.RemoveApplicationDeployment, [retired], false, ["Delete 2 objects: remove the application deployments. A delete cannot be undone. There is no Undo."],
            [new PreflightCheck("Objects", "Pass", "1 finding holds."), new PreflightCheck("Dropped", "Warn", "1 finding was left out of the plan.")], "fp-targets")
        {
            Targets = targets,
            FixText = "Remove-CMApplicationDeployment -DeploymentId '{A1}' -Force\r\nRemove-CMApplicationDeployment -DeploymentId '{A2}' -Force",
            Dropped = [new HygieneObjectOutcome(left, HygieneResults.Skipped, "Skipped: the finding no longer holds: the application has no deployment now.")]
        };
        var (plan, problem) = SiteHygieneText.BuildPlan(prepared);
        Assert.Null(problem);
        Assert.Equal(["Deployment Contoso Printer Drivers ({A1})", "Deployment Contoso Printer Drivers ({A2})"], plan!.Plan.Items.Select(i => i.Target));
        Assert.Equal("APP-02: to All Desktops {A1}", plan.Plan.Items[0].Detail);
        Assert.Equal(SiteHygieneText.DeleteConfirmation(2), plan.Plan.ExtraConfirmation);
        Assert.Equal(prepared.FixText, plan.Plan.Body);
        Assert.Equal("Dropped: Warn. 1 finding was left out of the plan.", plan.Plan.Notes[0]);
        Assert.Contains("Left out: Application Contoso Fonts 2.1 (16777300). Skipped: the finding no longer holds: the application has no deployment now.", plan.Plan.Notes);

        var dialog = new ChangePlanViewModel(plan.Plan, h.Writer().RunFor(plan)) { DestructiveAcknowledged = true, ExtraAcknowledged = true };
        ScriptedPlanDialogs.Wait(dialog.ConfirmCommand.ExecuteAsync(null));
        Assert.Equal(["Done", "Done"], dialog.Outcome.Result!.Items.Select(r => r.OutcomeText));
        Assert.True(Assert.Single(h.Service.Applied).ChangeConfirmed);
        Assert.Equal([SiteHygieneLabels.Key(retired)], plan.DoneKeys());
    }
}
