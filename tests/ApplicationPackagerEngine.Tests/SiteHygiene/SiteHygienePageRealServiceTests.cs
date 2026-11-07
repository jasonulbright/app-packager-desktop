using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using static ApplicationPackagerEngine.Tests.SiteHygiene.SiteHygieneFixTests;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>
/// The page, its plan and its writer over the real Site Hygiene service, the real stores and the real report
/// writer, with the stub site running the real script. Every refusal of the service reaches the page as a plain message.
/// </summary>
internal sealed class SiteHygieneRealPage : IDisposable
{
    public SiteHygieneRealPage(JsonObject? state = null, int? maxDeletes = null)
    {
        Site = new SiteHygieneHarness(state);
        Service = maxDeletes is { } cap
            ? new SiteHygieneService(Site.Site, Site.Audit, delay: (_, _) => Task.CompletedTask, utcNow: () => Site.Clock) { MaxDeletes = cap }
            : Site.Service;
        Suppressions = new HygieneSuppressionStore(Path.Combine(Site.Folder, HygieneSuppressionStore.FileName));
        ScanStore = new HygieneScanStore(Path.Combine(Site.Folder, HygieneScanStore.FileName));
        Reports = new HygieneReportWriter(Path.Combine(Site.Folder, "Reports"));
        Writer = new SiteHygieneWriter(Service, History, Log, () => "MCM");
    }

    public SiteHygieneHarness Site { get; }
    public SiteHygieneService Service { get; }
    public HygieneSuppressionStore Suppressions { get; }
    public HygieneScanStore ScanStore { get; }
    public HygieneReportWriter Reports { get; }
    public SiteHygieneWriter Writer { get; }
    public FakeHygieneDialogs Dialogs { get; } = new();
    public ScriptedPlanDialogs Plans { get; } = new();
    public RecordingSiteHistory History { get; } = new();
    public RecordingMaintenanceLog Log { get; } = new();

    /// <summary>Runs before the plan window confirms: the site changes between Prepare and Apply.</summary>
    public Action? BeforeConfirm;

    /// <summary>The settings name the connection of the harness, which the service reads at each call, as the desktop does.</summary>
    public SiteHygieneViewModel Create() => new(new SiteHygienePageServices(Service, Suppressions, ScanStore, Reports, Dialogs, new Hook(this), Writer,
        () => new AppSettings { SiteCode = Site.Connection.SiteCode ?? "", ProviderMachine = Site.Connection.ProviderMachine ?? "" }, () => null, _ => true, Log) { FilterDelay = TimeSpan.Zero });

    /// <summary>Opens the page and scans only the given areas of the stub site.</summary>
    public async Task<SiteHygieneViewModel> ScannedAsync(params string[] areas)
    {
        var vm = Create();
        await vm.OnNavigatedToAsync();
        foreach (var area in vm.AreaChoices) area.IsChecked = areas.Contains(area.Id);
        await vm.ScanAsync();
        Assert.True(vm.HasResult, vm.StatusMessage);
        return vm;
    }

    public static FindingGridRow Row(SiteHygieneViewModel vm, string checkId, string objectKey) =>
        vm.FindingRows.Single(r => r.CheckId == checkId && r.Finding.ObjectKey == objectKey);

    public void Dispose() => Site.Dispose();

    private sealed class Hook(SiteHygieneRealPage page) : IChangePlanDialogs
    {
        public ChangePlanOutcome Show(ChangePlan plan, ChangePlanRun run, ChangePlanRun? undo = null)
        {
            page.BeforeConfirm?.Invoke();
            return page.Plans.Show(plan, run, undo);
        }
    }
}

public sealed class SiteHygienePageRealServiceTests
{
    private const string EmptyPackage = "MCM0010E";
    private const string OtherPackage = "MCM00001";

    private static HygieneFinding Package(string id) => Finding(f => f.CheckId == "PKG-01" && f.ObjectKey == id);

    /// <summary>The plan of the page for findings the real service prepared; the plan text comes from that change.</summary>
    private static async Task<SiteHygienePlan> PlanAsync(SiteHygieneRealPage p, HygieneFixKind kind, params HygieneFinding[] findings)
    {
        var prepared = await PrepareAsync(p.Site, kind, findings);
        var (plan, problem) = SiteHygieneText.BuildPlan(prepared);
        Assert.True(plan is not null, problem);
        return plan!;
    }

    private static ChangePlanViewModel Confirm(SiteHygienePlan plan, ChangePlanRun run)
    {
        var dialog = new ChangePlanViewModel(plan.Plan, run) { DestructiveAcknowledged = true, ExtraAcknowledged = true, SecondAcknowledged = true };
        ScriptedPlanDialogs.Wait(dialog.ConfirmCommand.ExecuteAsync(null));
        return dialog;
    }

    private static void AssertPlain(string message)
    {
        Assert.DoesNotContain("SECRET", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", message, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", message, StringComparison.Ordinal);
    }

    [WindowFact]
    public async Task ADeleteWithEveryBoxRunsTheTypedFixOfThePlanAndTheRowLeavesTheList()
    {
        using var p = new SiteHygieneRealPage();
        var vm = await p.ScannedAsync("Packages");
        vm.SetSelectedFindings([SiteHygieneRealPage.Row(vm, "PKG-01", EmptyPackage)]);
        Assert.True(vm.RunFixCommand.CanExecute(null));
        await vm.RunFixCommand.ExecuteAsync(null);

        var plan = Assert.Single(p.Plans.Shown);
        Assert.True(plan.IsDestructive);
        Assert.Equal(SiteHygieneText.DeleteConfirmation(1), plan.ExtraConfirmation);
        Assert.Contains(EmptyPackage, Assert.Single(plan.Items).Target);
        Assert.Contains(plan.Notes, n => n.Contains("A delete cannot be undone", StringComparison.Ordinal));
        Assert.Equal(plan.Body!.Split("\r\n"), p.Site.Site.Commands);

        var result = Assert.Single(vm.LastResults);
        Assert.Equal("Done", result.OutcomeText);
        Assert.Equal("Deleted. The site no longer lists the object.", result.Message);
        Assert.DoesNotContain(vm.FindingRows, r => r.CheckId == "PKG-01" && r.Finding.ObjectKey == EmptyPackage);
        Assert.Contains("1 finding fixed since the scan.", vm.SummaryLine);
        Assert.Equal(1, p.History.Entries.Single().Counts!["changed"]);
        Assert.Equal(["Started", "Finished"], p.Site.Audit.Read(10).Select(a => a.Phase));
        Assert.DoesNotContain(p.Log.Entries, e => e.Message.Contains("SHL-", StringComparison.Ordinal) || e.Message.Contains(EmptyPackage, StringComparison.Ordinal));
    }

    [WindowTheory]
    [InlineData(HygieneFixKind.RemovePackage)]
    [InlineData(HygieneFixKind.SetCollectionRefreshType)]
    public async Task ThePlanNotesStateTheOneAtATimeAndReadBackRuleOnce(HygieneFixKind kind)
    {
        using var p = new SiteHygieneRealPage();
        var finding = kind == HygieneFixKind.RemovePackage ? Package(EmptyPackage) : Finding(f => f.FixKind == kind);
        var plan = await PlanAsync(p, kind, finding);
        var notes = plan.Plan.Notes;
        Assert.Single(notes, n => n.Contains("one at a time", StringComparison.OrdinalIgnoreCase) || n.Contains("one object at a time", StringComparison.OrdinalIgnoreCase));
        Assert.Single(notes, n => n.Contains("read back", StringComparison.OrdinalIgnoreCase) || n.Contains("reads each one back", StringComparison.OrdinalIgnoreCase));
        var before = SiteHygieneLabels.IsDelete(kind) ? "Before each delete the page reads the object again" : "Before each change the page reads the object again";
        Assert.Single(notes, n => n.Contains(before, StringComparison.Ordinal));
    }

    [WindowTheory]
    [InlineData(HygieneFixKind.RemovePackage, "PKG-01", EmptyPackage)]
    [InlineData(HygieneFixKind.RemoveCollection, "COL-01", "MCM00215")]
    public async Task ADeletePlanStatesTheCheckBeforeEachDeleteOnce(HygieneFixKind kind, string checkId, string key)
    {
        using var p = new SiteHygieneRealPage();
        var plan = await PlanAsync(p, kind, Finding(f => f.CheckId == checkId && f.ObjectKey == key));
        var before = Assert.Single(plan.Plan.Notes, n => n.Contains("Before each delete", StringComparison.Ordinal));
        Assert.Contains("the finding no longer holds", before, StringComparison.Ordinal);
        if (kind == HygieneFixKind.RemoveCollection)
        {
            foreach (var use in new[] { "a deployment", "a collection rule or setting", "an automatic deployment rule", "a client settings deployment", "an administrative user" })
                Assert.Contains(use, before, StringComparison.Ordinal);
        }
    }

    [WindowFact]
    public async Task APlanWithoutTheSecondBoxReachesTheRealServiceUnconfirmedAndEveryObjectIsSkippedWithAPlainMessage()
    {
        using var p = new SiteHygieneRealPage();
        var plan = await PlanAsync(p, HygieneFixKind.RemovePackage, Package(EmptyPackage));
        var unconfirmed = new SiteHygienePlan(plan.Action, plan.Change, new ChangePlan(plan.Plan.Title, plan.Plan.ActionName, plan.Plan.Items), plan.Entries);
        var row = Assert.Single(Confirm(unconfirmed, p.Writer.RunFor(unconfirmed)).Outcome.Result!.Items);
        Assert.True(row.Skipped);
        Assert.Equal("Skipped: a delete needs the second confirmation. Nothing was deleted.", row.Message);
        Assert.Empty(p.Site.Site.Commands);
        Assert.Empty(p.Site.Audit.Read(10));
        Assert.Equal(SiteOperationOutcome.Skipped, p.History.Entries.Single().Outcome);
    }

    [WindowFact]
    public async Task AFindingThatStopsHoldingBetweenThePlanAndTheConfirmIsSkippedOnThePage()
    {
        using var p = new SiteHygieneRealPage();
        var vm = await p.ScannedAsync("Packages");
        p.BeforeConfirm = () => p.Site.Site.Class("SMS_TaskSequencePackageReference_All").Add(new JsonObject { ["PackageID"] = "MCM00008", ["ObjectID"] = EmptyPackage });
        vm.SetSelectedFindings([SiteHygieneRealPage.Row(vm, "PKG-01", EmptyPackage)]);
        await vm.RunFixCommand.ExecuteAsync(null);
        var result = Assert.Single(vm.LastResults);
        Assert.Equal("Skipped", result.OutcomeText);
        Assert.Equal("Skipped: the finding no longer holds: 1 task sequence uses the package now.", result.Message);
        Assert.Contains(vm.FindingRows, r => r.Finding.ObjectKey == EmptyPackage);
        Assert.Empty(p.Site.Site.Commands);
    }

    [WindowFact]
    public async Task AFindingThatStoppedHoldingBeforeThePlanMakesNoPlanAndThePageNamesTheObjectAndTheReason()
    {
        using var p = new SiteHygieneRealPage();
        var vm = await p.ScannedAsync("Packages");
        p.Site.Site.Class("SMS_TaskSequencePackageReference_All").Add(new JsonObject { ["PackageID"] = "MCM00008", ["ObjectID"] = EmptyPackage });
        vm.SetSelectedFindings([SiteHygieneRealPage.Row(vm, "PKG-01", EmptyPackage)]);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Empty(p.Plans.Shown);
        Assert.StartsWith("The plan cannot run. Nothing was changed. No finding of the plan holds now.", vm.StatusMessage);
        Assert.Contains($"Left out: Package SHL-Pkg-Empty ({EmptyPackage}). Skipped: the finding no longer holds: 1 task sequence uses the package now.", vm.StatusMessage);
        AssertPlain(vm.StatusMessage);
    }

    [WindowFact]
    public async Task ADeleteOverTheCapIsRefusedOnThePageWithTheMessageOfTheService()
    {
        using var p = new SiteHygieneRealPage(maxDeletes: 1);
        var vm = await p.ScannedAsync("Packages");
        vm.SetSelectedFindings([SiteHygieneRealPage.Row(vm, "PKG-01", EmptyPackage), SiteHygieneRealPage.Row(vm, "PKG-01", OtherPackage)]);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Single(p.Dialogs.Shown);
        Assert.Empty(p.Plans.Shown);
        Assert.Equal("The plan cannot run. Nothing was changed. The plan holds 2 findings. One run deletes at most 1 object. Filter the findings and run them in parts. The site was not read.", vm.StatusMessage);
    }

    [WindowFact]
    public async Task AStalePlanIsRefusedByTheRealServiceAndEveryLineSaysSo()
    {
        using var p = new SiteHygieneRealPage();
        var plan = await PlanAsync(p, HygieneFixKind.RemovePackage, Package(EmptyPackage));
        p.Site.Clock += SiteHygieneService.MaxPlanAge + TimeSpan.FromMinutes(1);
        var result = Confirm(plan, p.Writer.RunFor(plan)).Outcome.Result!;
        var row = Assert.Single(result.Items);
        Assert.True(row.Skipped);
        Assert.Equal("Skipped: the plan is older than 15 minutes. Prepare it again on fresh data.", row.Message);
        Assert.Equal("1 skipped.", result.Headline);
        Assert.Empty(p.Site.Site.Commands);
    }

    [WindowFact]
    public async Task AConfirmedPlanSentTwiceRunsOnceAndTheSecondSendIsSkipped()
    {
        using var p = new SiteHygieneRealPage();
        var plan = await PlanAsync(p, HygieneFixKind.RemovePackage, Package(EmptyPackage));
        Assert.Equal("Done", Assert.Single(Confirm(plan, p.Writer.RunFor(plan)).Outcome.Result!.Items).OutcomeText);
        var again = Assert.Single(Confirm(plan, p.Writer.RunFor(plan)).Outcome.Result!.Items);
        Assert.True(again.Skipped);
        Assert.Equal("Skipped: the plan was not prepared by this session, or it already ran. Prepare the plan again.", again.Message);
        Assert.Single(p.Site.Site.Commands);
        Assert.Equal([SiteOperationOutcome.Succeeded, SiteOperationOutcome.Skipped], p.History.Entries.Select(e => e.Outcome));
    }

    [WindowFact]
    public async Task AWriteWhoseReadBackShowsNoChangeIsUnchangedAndNeverCountsAsDone()
    {
        var state = SiteHygieneFixtures.StubState();
        var rule = state["Classes"]!["SMS_AutoDeployment"]!.AsArray().Single()!.AsObject();
        rule["AutoDeploymentEnabled"] = "True";
        rule["LastRunTime"] = "2026-08-01T00:00:00";
        state["Options"]!["AdrNoRun"] = true;
        using var p = new SiteHygieneRealPage(state);
        var finding = HygieneChecks.F("UPD-03", "ADR", "1", "SHL-ADR-Disabled", "e", "r", HygieneFixKind.RunAutoDeploymentRule,
            HygieneChecks.Params((HygieneFixParameters.Id, "1"), (HygieneFixParameters.Days, "45")));
        var plan = await PlanAsync(p, HygieneFixKind.RunAutoDeploymentRule, finding);
        Assert.False(plan.Plan.IsDestructive);
        var result = Confirm(plan, p.Writer.RunFor(plan)).Outcome.Result!;
        var row = Assert.Single(result.Items);
        Assert.Equal("Unchanged", row.OutcomeText);
        Assert.False(row.Succeeded);
        Assert.Contains("no new run time within 60 seconds", row.Message);
        Assert.Empty(plan.DoneKeys());
        var entry = p.History.Entries.Single();
        Assert.Equal(SiteOperationOutcome.Failed, entry.Outcome);
        Assert.Equal((0, 0, 1), (entry.Counts!["changed"], entry.Counts["failed"], entry.Counts["unchanged"]));
    }

    [WindowFact]
    public async Task AnApplicationTheSiteStillReportsAsDeployedIsLeftOutWithTheReasonOfTheLag()
    {
        using var p = new SiteHygieneRealPage();
        var vm = await p.ScannedAsync("Applications");
        var row = vm.FindingRows.First(r => r.CheckId == "APP-01" && r.ObjectName == "CMTrace Open");
        foreach (var app in p.Site.Site.Class("SMS_Application").OfType<JsonObject>().Where(a => (string?)a["ModelName"] == row.Finding.ObjectKey)) app["IsDeployed"] = "True";
        vm.SetSelectedFindings([row]);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Empty(p.Plans.Shown);
        Assert.Contains("Skipped: the finding no longer holds: the site still reports the application as deployed. The site updates this flag some time after a deployment is removed; scan again later.", vm.StatusMessage);
        Assert.Empty(p.Site.Site.Commands);
    }

    [WindowFact]
    public async Task AScanOfSomeAreasCountsTheChecksOfTheOtherAreasAsNotSelectedNotAsNotRun()
    {
        using var p = new SiteHygieneRealPage();
        var vm = await p.ScannedAsync("Packages");
        var result = vm.Result!;
        var notSelected = result.Checks.Count(c => c.State == HygieneCheckStates.NotSelected);
        Assert.True(notSelected > 0);
        Assert.Contains($"{notSelected} checks were not in the scanned areas or options.", vm.SummaryLine);
        Assert.Equal(result.ChecksNotRun, vm.CheckRows.Count(c => SiteHygieneLabels.IsNotRun(c.Check)));
        Assert.DoesNotContain(vm.CheckRows.Where(c => SiteHygieneLabels.WasNotSelected(c.Check)), c => vm.NotRunText.Contains(c.CheckId + " ", StringComparison.Ordinal));
        Assert.All(vm.CheckRows.Where(c => SiteHygieneLabels.WasNotSelected(c.Check)), c => Assert.Equal(("Not selected", "Not known"), (c.State, c.Findings)));
        Assert.False(File.Exists(p.ScanStore.Path));
    }

    [WindowFact]
    public async Task AnInactiveDeviceIsOneFindingPerDeviceAndItsDeleteRunsThroughThePage()
    {
        var state = SiteHygieneFixtures.StubState();
        var device = state["Classes"]!["SMS_CM_RES_COLL_SMS00001"]!.AsArray().Single(d => (string?)d!["Name"] == "CLIENT02")!.AsObject();
        device["LastActiveTime"] = "2026-05-01T00:00:00";
        state["Classes"]!["SMS_R_System"]!.AsArray().Single(d => (string?)d!["ResourceId"] == "16777222")!.AsObject()["AgentTime"] =
            new JsonArray("2026-05-01T00:00:00", "2026-05-01T00:00:00", "2026-05-01T00:00:00");
        using var p = new SiteHygieneRealPage(state);
        var vm = await p.ScannedAsync("Devices");
        var row = Assert.Single(vm.FindingRows, r => r.CheckId == "DEV-01");
        Assert.Equal(("16777222", "Delete device"), (row.Finding.ObjectKey, row.Fix));
        vm.SetSelectedFindings([row]);
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Equal(SiteHygieneText.DeleteConfirmation(1), Assert.Single(p.Plans.Shown).ExtraConfirmation);
        Assert.Equal("Done", Assert.Single(vm.LastResults).OutcomeText);
        Assert.Equal(["Remove-CMDevice -ResourceId 16777222 -Force"], p.Site.Site.Commands);
    }

    [WindowFact]
    public async Task TheRealStoresKeepASuppressionByKeyAndTheNextFullScanMarksOnlyUnseenFindingsNew()
    {
        using var p = new SiteHygieneRealPage();
        var vm = await p.ScannedAsync([.. HygieneAreas.All.Select(a => a.Id)]);
        Assert.True(File.Exists(p.ScanStore.Path));
        Assert.DoesNotContain(vm.FindingRows, r => r.Finding.IsNew);
        var target = SiteHygieneRealPage.Row(vm, "PKG-01", EmptyPackage);
        vm.SetSelectedFindings([target]);
        await vm.SuppressCommand.ExecuteAsync(null);
        Assert.Equal(target.Finding.Key, Assert.Single(p.Suppressions.Load()).Key);

        p.ScanStore.Save(vm.Result! with { Findings = vm.Result!.Findings.Where(f => f.ObjectKey != OtherPackage).ToList() });
        await vm.ScanAsync();
        Assert.Equal([OtherPackage], vm.FindingRows.Where(r => r.Finding.IsNew).Select(r => r.Finding.ObjectKey));
        Assert.DoesNotContain(vm.FindingRows, r => r.Finding.ObjectKey == EmptyPackage);
        Assert.Contains("1 suppressed.", vm.SummaryLine);

        await vm.ExportCsvCommand.ExecuteAsync(null);
        var file = Assert.Single(Directory.GetFiles(p.Reports.Folder, "*.csv"));
        Assert.StartsWith("\"Severity\",\"Check\",", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Contains("Site Hygiene: Findings.", File.ReadAllText(HygieneReportWriter.AboutPath(file)), StringComparison.Ordinal);
    }

    [WindowFact]
    public async Task EachDeploymentOfAFindingShowsItsOwnResultAndTheFindingStaysWhileOneRemains()
    {
        using var p = new SiteHygieneRealPage(SiteHygieneRunBindingTests.RetiredWithTwoDeployments());
        var vm = await p.ScannedAsync("Applications");
        var row = vm.FindingRows.Single(r => r.CheckId == "APP-02" && r.Finding.ObjectName == "SHL-App-Retired");
        p.Site.Site.BeforeRun = (q, site) => { if ((string?)q["Operation"] == "Apply" && site.Commands.Any()) site.Fail("cmd:Remove-CMApplicationDeployment"); };
        vm.SetSelectedFindings([row]);
        await vm.RunFixCommand.ExecuteAsync(null);
        var plan = Assert.Single(p.Plans.Shown);
        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(["Done", "Failed"], vm.LastResults.Select(r => r.OutcomeText));
        Assert.Equal("Deleted. The site no longer lists the object.", vm.LastResults[0].Message);
        Assert.Equal("ConfigMgr reported an error. The log has the kind of failure.", vm.LastResults[1].Message);
        Assert.Equal(plan.Items.Select(i => i.Target), vm.LastResults.Select(r => r.Item.Target));
        Assert.Contains(vm.FindingRows, r => r.Key == row.Key);
        Assert.EndsWith(": 1 done, 1 failed.", vm.ResultSummary);
    }

    [WindowFact]
    public async Task ASettingsChangeToAnotherProviderBetweenThePlanAndTheConfirmChangesNothing()
    {
        using var p = new SiteHygieneRealPage();
        var vm = await p.ScannedAsync("Packages");
        p.BeforeConfirm = () => p.Site.Connection = new ApplicationPackagerEngine.Services.ConfigMgrSession.ConfigMgrConnection("MCM", "CM02");
        vm.SetSelectedFindings([SiteHygieneRealPage.Row(vm, "PKG-01", EmptyPackage)]);
        await vm.RunFixCommand.ExecuteAsync(null);
        var result = Assert.Single(vm.LastResults);
        Assert.Equal("Skipped", result.OutcomeText);
        Assert.StartsWith("Skipped: the connection changed since the plan", result.Message);
        Assert.Empty(p.Site.Site.Commands);
        Assert.Contains(vm.FindingRows, r => r.Finding.ObjectKey == EmptyPackage);
    }

    [WindowFact]
    public async Task AResultOfAnotherProviderOfTheSameSiteCodeIsNotFixed()
    {
        using var p = new SiteHygieneRealPage();
        var vm = await p.ScannedAsync("Packages");
        p.Site.Connection = new ApplicationPackagerEngine.Services.ConfigMgrSession.ConfigMgrConnection("MCM", "CM02");
        vm.SetSelectedFindings([SiteHygieneRealPage.Row(vm, "PKG-01", EmptyPackage)]);
        var runs = p.Site.Site.Runs.Count;
        await vm.RunFixCommand.ExecuteAsync(null);
        Assert.Empty(p.Plans.Shown);
        Assert.Equal(runs, p.Site.Site.Runs.Count);
        Assert.Equal("The result shown is of site MCM on CM01, and the settings name site MCM on CM02. Nothing was changed. Select Scan to read the site of the settings.", vm.StatusMessage);
    }
}
