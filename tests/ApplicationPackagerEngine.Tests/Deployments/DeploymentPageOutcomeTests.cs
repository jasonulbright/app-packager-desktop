using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>
/// The page over the real service and the stub site: the History Undo built from the audit record,
/// the prepared state on its way to the service, stale plans, cut lists, the history report note
/// and the plain outcome text of every refusal.
/// </summary>
public sealed class DeploymentPageOutcomeTests
{
    private static string ListOf(DeploymentObjectType type) => type switch
    {
        DeploymentObjectType.Application => "AppAssignments",
        DeploymentObjectType.SoftwareUpdateGroup => "UgAssignments",
        _ => "Adverts"
    };

    private static string KeyOf(DeploymentObjectType type) => type is DeploymentObjectType.Application or DeploymentObjectType.SoftwareUpdateGroup ? "AssignmentUniqueID" : "AdvertisementID";

    private static void Tomorrow(DeploymentFormViewModel form)
    {
        form.AvailableDate = new DateTime(2026, 10, 21);
        form.AvailableTimeText = "09:00";
    }

    private static async Task<DeploymentHistoryViewModel> HistoryAsync(DeploymentHelperViewModel page)
    {
        page.SelectedTabIndex = DeploymentHelperViewModel.HistoryTab;
        await page.CurrentTab.EnsureLoadedAsync();
        return page.History;
    }

    // ---- History Undo ---------------------------------------------------------------------------

    public static TheoryData<DeploymentObjectType, string, bool> HistoryCases => new()
    {
        { DeploymentObjectType.Application, "16779607", false },
        { DeploymentObjectType.Package, "MCM0001B", false },
        { DeploymentObjectType.TaskSequence, "MCM00030", false },
        { DeploymentObjectType.SoftwareUpdateGroup, "16778893", false },
        { DeploymentObjectType.Application, "16779607", true },
        { DeploymentObjectType.SoftwareUpdateGroup, "16778893", true }
    };

    [Theory]
    [MemberData(nameof(HistoryCases))]
    public async Task TheHistoryUndoPreparesAndRemovesTheDeploymentOfEachType(DeploymentObjectType type, string id, bool utc)
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, type, id);
        Tomorrow(form);
        form.UtcBasis = utc;
        await form.DeployCommand.ExecuteAsync(null);
        Assert.True(page.Shared.LastResults.Single().Succeeded, page.Shared.LastResults.Single().Message);
        var created = (string)h.Engine.Stored(ListOf(type))[KeyOf(type)]!;

        var history = await HistoryAsync(page);
        history.SelectedRow = history.Rows.Single(r => r.Event == "Create");
        await history.RemoveDeploymentCommand.ExecuteAsync(null);

        var removal = Assert.Single(h.Service.Applied.Last());
        Assert.Same(h.Service.Prepared.Last(), removal);
        Assert.Equal((DeploymentChangeKind.RemoveDeployment, type, created, utc), (removal.Kind, removal.Existing!.Type, removal.Existing.DeploymentId, removal.Existing.UtcBasis));
        Assert.Empty(h.Engine.Site.List(ListOf(type)));
        var undo = h.Engine.Audit.Read(10).Last();
        Assert.Equal(("Undo", "Success", created), (undo.Event, undo.Result, undo.Undoes));
    }

    [Fact]
    public async Task TheHistoryUndoOfAPackageRemovesOnlyTheDeploymentOfTheProgramItNames()
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Package, "MCM0001B");
        Assert.Equal(["CMCO-CANARY-Run", "CMCO-CANARY-RunAvailable"], form.Programs);
        await form.DeployCommand.ExecuteAsync(null);
        await ServicePageHarness.WaitAsync(() => !form.IsLoadingDetails && form.Programs.Count == 2);
        form.SelectedProgram = form.Programs[1];
        await form.DeployCommand.ExecuteAsync(null);
        Assert.True(page.Shared.LastResults.Single().Succeeded, page.Shared.LastResults.Single().Message);
        var adverts = h.Engine.Site.List("Adverts");
        Assert.Equal(2, adverts.Count);
        var second = (string)adverts.Single(a => (string)a!["ProgramName"]! == "CMCO-CANARY-RunAvailable")!["AdvertisementID"]!;

        var history = await HistoryAsync(page);
        history.SelectedRow = history.Rows.Single(r => r.Record.ProgramName == "CMCO-CANARY-RunAvailable");
        await history.RemoveDeploymentCommand.ExecuteAsync(null);

        var removal = Assert.Single(h.Service.Applied.Last());
        Assert.Equal((second, "CMCO-CANARY-RunAvailable"), (removal.Existing!.DeploymentId, removal.Existing.Program));
        Assert.Equal("CMCO-CANARY-Run", (string)Assert.Single(h.Engine.Site.List("Adverts"))!["ProgramName"]!);
    }

    [Fact]
    public async Task AHistoryRecordWithoutTheValuesTheRemovalComparesIsRefusedWithoutAPlan()
    {
        var h = new DeploymentPageHarness();
        h.Audit.Records.Add(h.Record("Create", DeploymentObjectType.Application, "7-Zip", "LAB00010", "{0A1B2C3D-0000-4000-8000-000000000002}") with { TimeBasedOn = "" });
        var page = await h.OpenAsync(DeploymentHelperViewModel.HistoryTab);
        page.History.SelectedRow = page.History.Rows.Single();

        await page.History.RemoveDeploymentCommand.ExecuteAsync(null);

        Assert.Equal(DeploymentHistoryViewModel.NotRemovable, page.Shared.StatusMessage);
        Assert.Empty(h.Plans.Shown);
        Assert.Equal(0, h.Service.PrepareCalls);
    }

    // ---- The prepared state ---------------------------------------------------------------------

    private static JsonObject WithPackageWithoutContent()
    {
        var state = StubDeploymentSite.Lab();
        state["Packages"]!.AsArray().Add(JsonNode.Parse("""{ "PackageID": "MCM0001C", "Name": "DH Package Without Content", "Version": "1", "Programs": [ "Run" ], "Content": { "Targeted": 0, "Success": 0 } }"""));
        return state;
    }

    [Fact]
    public async Task ACombinedRunThatDistributesAndSkipsTheCreateSaysSoAndCountsTheDistribution()
    {
        using var h = new ServicePageHarness(WithPackageWithoutContent());
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Package, "MCM0001C");
        form.DpGroups.Single(g => g.Name == "All DPs").IsChecked = true;
        form.DistributeFirst = true;

        await form.DeployCommand.ExecuteAsync(null);

        Assert.Empty(h.Engine.Site.List("Adverts"));
        var row = Assert.Single(page.Shared.LastResults);
        Assert.Equal("Changed", row.OutcomeText);
        Assert.StartsWith(DeploymentWriter.DistributionOnlyText, row.Message);
        Assert.Contains("1 changed, 0 failed. " + DeploymentWriter.DistributionOnlyText, page.Shared.ResultSummary);
        Assert.False(page.Shared.HasUndo);
        Assert.Equal(1, h.History.Entries.Single().Counts["changed"]);
    }

    [Fact]
    public async Task TheHistoryUndoOfAnUpdateGroupCarriesTheUpdateCountOfTheGroupList()
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.SoftwareUpdateGroup, "16778893");
        var count = form.SelectedObject!.Object.UpdateCount;
        Assert.True(count > 0);
        Tomorrow(form);
        await form.DeployCommand.ExecuteAsync(null);

        var history = await HistoryAsync(page);
        history.SelectedRow = history.Rows.Single(r => r.Event == "Create");
        await history.RemoveDeploymentCommand.ExecuteAsync(null);

        var removal = Assert.Single(h.Service.Applied.Last());
        Assert.Equal(count, removal.Object!.UpdateCount);
    }

    [Fact]
    public async Task TheCombinedPlanAndThePageUndoSendThePreparedChangesWithTheirPreparedState()
    {
        using var h = new ServicePageHarness(WithPackageWithoutContent());
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Package, "MCM0001C");
        form.DpGroups.Single(g => g.Name == "All DPs").IsChecked = true;
        form.DistributeFirst = true;
        h.Plans.ChooseOption = true;

        await form.DeployCommand.ExecuteAsync(null);

        var prepared = Assert.Single(h.Service.Prepared);
        var sent = Assert.Single(Assert.Single(h.Service.Applied));
        Assert.NotNull(prepared.Prepared);
        Assert.Same(prepared.Prepared, sent.Prepared);
        Assert.Same(prepared.Checks, sent.Checks);
        Assert.Equal(prepared.Object, sent.Object);
        Assert.Equal(prepared.Options, sent.Options);
        Assert.True(page.Shared.LastResults.Single().Succeeded, page.Shared.LastResults.Single().Message);

        await page.Shared.UndoLastCommand.ExecuteAsync(null);

        var removal = Assert.Single(h.Service.Applied.Last());
        Assert.Same(h.Service.Prepared.Last(), removal);
        Assert.Empty(h.Engine.Site.List("Adverts"));
    }

    [Fact]
    public async Task TheDialogUndoRemovesTheDeploymentTheCreateReported()
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.TaskSequence, "MCM00030");
        h.Plans.RunUndo = true;
        string? reported = null;
        h.Service.OnOutcome = o => reported ??= o.UndoChange?.CreatedId;

        await form.DeployCommand.ExecuteAsync(null);

        var create = Assert.Single(h.Service.Applied[0]);
        Assert.Same(Assert.Single(h.Service.Prepared).Prepared, create.Prepared);
        var removal = Assert.Single(h.Service.Applied[1]);
        Assert.Equal(reported, removal.Existing!.DeploymentId);
        Assert.Empty(h.Engine.Site.List("Adverts"));
    }

    // ---- Stale plans ----------------------------------------------------------------------------

    private static void AssertStalePlanShown(ServicePageHarness h, DeploymentHelperViewModel page, DeploymentFormViewModel form, string objectId, string stale)
    {
        var row = Assert.Single(page.Shared.LastResults);
        Assert.True(row.Skipped);
        Assert.StartsWith("Skipped: the plan is stale, refresh the plan: " + stale, row.Message);
        Assert.Equal(0, h.Engine.CreateCalls);
        Assert.False(page.Shared.HasUndo);
        Assert.Empty(form.Checks);
        Assert.Equal(DeploymentFormViewModel.StalePlanText, form.ValidationMessage);
        Assert.Equal(DeploymentFormViewModel.StalePlanText, form.FormMessage);
        Assert.Equal(objectId, form.SelectedObject!.Object.Id);
        Assert.Equal("MCM00098", form.SelectedCollection!.CollectionId);
        Assert.True(form.ValidateCommand.CanExecute(null));
    }

    [Fact]
    public async Task ANewRevisionBetweenPrepareAndApplyIsSkippedAsStaleAndTheFormAsksForValidateAgain()
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Application, "16779607");
        h.Service.BeforeApply = _ => h.Engine.Site.List("Applications")[0]!["CIVersion"] = 2;

        await form.DeployCommand.ExecuteAsync(null);

        Assert.Same(Assert.Single(h.Service.Prepared).Prepared, Assert.Single(Assert.Single(h.Service.Applied)).Prepared);
        AssertStalePlanShown(h, page, form, "16779607", "application 7-Zip (x64 edition) - 26.03 (16779607) changed since the plan was shown");

        h.Service.BeforeApply = null;
        await form.ValidateCommand.ExecuteAsync(null);
        Assert.NotEmpty(form.Checks);
        await form.DeployCommand.ExecuteAsync(null);
        Assert.True(page.Shared.LastResults.Single().Succeeded, page.Shared.LastResults.Single().Message);
        Assert.Equal(1, h.Engine.CreateCalls);
    }

    [Fact]
    public async Task ARequiredCollectionThatGrewBetweenPrepareAndApplyIsSkippedAsStale()
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Package, "MCM0001B");
        ServicePageHarness.SetRequired(form);
        h.Service.BeforeApply = _ => h.Engine.Site.List("Collections")[0]!["MemberCount"] = 5;

        await form.DeployCommand.ExecuteAsync(null);

        AssertStalePlanShown(h, page, form, "MCM0001B", "the collection has 5 members now and had 0 when the plan was shown");
    }

    // ---- Cut lists and the history note ---------------------------------------------------------

    [Fact]
    public async Task TheNoteOfACutObjectListIsShownUnderTheListAndGoesWithTheType()
    {
        var state = StubDeploymentSite.Lab();
        state["Packages"]!.AsArray().Add(JsonNode.Parse("""{ "PackageID": "MCM0001C", "Name": "Second", "Version": "1", "Programs": [ "Run" ], "Content": { "Targeted": 1, "Success": 1 } }"""));
        using var h = new ServicePageHarness(state, maxRows: 1);
        var page = await h.OpenAsync();
        var form = page.Deploy;
        Assert.Equal("The application list shows the first 1 of 2 rows. Use the console for the rest.", form.ObjectListNote);
        Assert.True(form.HasObjectListNote);

        form.TypeIndex = (int)DeploymentObjectType.TaskSequence;
        await ServicePageHarness.WaitAsync(() => !form.IsLoading && form.Objects.Count == 1);
        Assert.Equal("", form.ObjectListNote);
        Assert.False(form.HasObjectListNote);

        form.TypeIndex = (int)DeploymentObjectType.Package;
        await ServicePageHarness.WaitAsync(() => !form.IsLoading && form.Objects.Any(o => o.Object.Type == DeploymentObjectType.Package));
        Assert.Equal("The package list shows the first 1 of 2 rows. Use the console for the rest.", form.ObjectListNote);
    }

    [Fact]
    public async Task TheHistoryReportCarriesTheCapNoteOnlyWhenTheListWasCapped()
    {
        var h = new DeploymentPageHarness();
        h.Audit.Records.Add(h.Record("Create", DeploymentObjectType.Application, "7-Zip", "LAB00010", "{0A1B2C3D-0000-4000-8000-000000000003}"));
        var page = await h.OpenAsync(DeploymentHelperViewModel.HistoryTab);
        await page.History.ExportCsvCommand.ExecuteAsync(null);
        await page.History.ExportHtmlCommand.ExecuteAsync(null);
        Assert.Equal([null, null], h.Reports.Notes);

        for (var i = 1; i < DeploymentHistoryViewModel.MaxRecords; i++)
            h.Audit.Records.Add(h.Record("Distribute", DeploymentObjectType.Application, "7-Zip", "LAB00010", "", when: new DateTime(2026, 10, 1).AddMinutes(i)));
        await page.History.RefreshCommand.ExecuteAsync(null);
        Assert.True(page.History.IsCapped);
        await page.History.ExportCsvCommand.ExecuteAsync(null);
        await page.History.ExportHtmlCommand.ExecuteAsync(null);
        Assert.Equal([DeploymentHistoryViewModel.CappedNote, DeploymentHistoryViewModel.CappedNote], h.Reports.Notes.Skip(2));
    }

    // ---- Plain outcome text ---------------------------------------------------------------------

    /// <summary>The run counts the change as skipped, shows the service text unchanged and offers no Undo.</summary>
    private static void AssertSkipped(ServicePageHarness h, DeploymentHelperViewModel page, List<DeploymentOutcome> outcomes, string expected, bool exact = false)
    {
        var message = Assert.Single(outcomes).Message;
        if (exact) Assert.Equal(expected, message);
        else Assert.StartsWith(expected, message);
        var row = Assert.Single(page.Shared.LastResults);
        Assert.Equal(("Skipped", message), (row.OutcomeText, row.Message));
        var result = h.Plans.LastViewModel!.Outcome.Result!;
        Assert.Equal((0, 0, 1), (result.SucceededCount, result.FailedCount, result.SkippedCount));
        Assert.Equal(message, h.Plans.LastViewModel.ResultRows.Single().Message);
        Assert.DoesNotContain(ChangePlanViewModel.FailedText, h.Plans.LastViewModel.ResultText);
        Assert.DoesNotContain(ChangePlanViewModel.FailedText, page.Shared.ResultSummary);
        var entry = h.History.Entries.Last();
        Assert.Equal((0, 0, 1), (entry.Counts["changed"], entry.Counts["failed"], entry.Counts["skipped"]));
        Assert.False(page.Shared.HasUndo);
        Assert.Equal(0, h.Engine.CreateCalls);
    }

    /// <summary>The run counts the change as failed, not changed, shows the service text unchanged and offers no Undo.</summary>
    private static void AssertFailedWithoutChange(ServicePageHarness h, DeploymentHelperViewModel page, List<DeploymentOutcome> outcomes, string expected)
    {
        var message = Assert.Single(outcomes).Message;
        Assert.StartsWith(expected, message);
        var row = Assert.Single(page.Shared.LastResults);
        Assert.Equal(("Failed", message, false), (row.OutcomeText, row.Message, row.PartialChange));
        var result = h.Plans.LastViewModel!.Outcome.Result!;
        Assert.Equal((0, 1, 0), (result.SucceededCount, result.FailedCount, result.SkippedCount));
        Assert.DoesNotContain(ChangePlanViewModel.FailedText, page.Shared.ResultSummary);
        var entry = h.History.Entries.Last();
        Assert.Equal((0, 1, 0), (entry.Counts["changed"], entry.Counts["failed"], entry.Counts["skipped"]));
        Assert.False(page.Shared.HasUndo);
    }

    private static List<DeploymentOutcome> Record(ServicePageHarness h)
    {
        var outcomes = new List<DeploymentOutcome>();
        h.Service.OnOutcome = o => { lock (outcomes) outcomes.Add(o); };
        return outcomes;
    }

    [Fact]
    public async Task AStalePlanIsShownAsTheSkippedTextOfTheService()
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.SoftwareUpdateGroup, "16778893");
        h.Service.BeforeApply = _ => h.Engine.Site.List("Groups")[0]!["CIVersion"] = 3;
        var outcomes = Record(h);

        await form.DeployCommand.ExecuteAsync(null);

        AssertSkipped(h, page, outcomes, "Skipped: the plan is stale, refresh the plan: ");
    }

    [Fact]
    public async Task CreateNowWithoutADistributionInProgressIsShownAsTheSkippedTextOfTheService()
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Package, "MCM0001B");
        form.DpGroups.Single(g => g.Name == "All DPs").IsChecked = true;
        form.DistributeFirst = true;
        h.Plans.ChooseOption = true;
        var outcomes = Record(h);

        await form.DeployCommand.ExecuteAsync(null);

        Assert.True(Assert.Single(Assert.Single(h.Service.Applied)).CreateNowContentDistributing);
        AssertSkipped(h, page, outcomes, DeploymentService.SkippedPrefix + DeploymentService.CreateNowWithoutDistributionMessage, exact: true);
        Assert.DoesNotContain(h.Engine.Site.Log, l => l.StartsWith("Start-CMContentDistribution", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASigningRefusalAtApplyIsShownAsTheSkippedTextOfTheService()
    {
        using var h = new ServicePageHarness(signing: new LaterSigning(DeploymentHarness.App.Name));
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Application, "16779607");
        ServicePageHarness.SetRequired(form);
        var outcomes = Record(h);

        await form.DeployCommand.ExecuteAsync(null);

        AssertSkipped(h, page, outcomes, "Skipped: refused " + DeploymentHarness.App.Name, exact: true);
    }

    [Fact]
    public async Task ANameThatIsNotUniqueAtApplyIsShownAsTheSkippedTextOfTheService()
    {
        using var h = new ServicePageHarness(signing: new LaterSigning("nothing packaged"));
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Application, "16779607");
        ServicePageHarness.SetRequired(form);
        h.Service.BeforeApply = _ => h.Engine.Site.List("Applications")[1]!["LocalizedDisplayName"] = DeploymentHarness.App.Name;
        var outcomes = Record(h);

        await form.DeployCommand.ExecuteAsync(null);

        AssertSkipped(h, page, outcomes, $"Skipped: 2 applications on the site are named {DeploymentHarness.App.Name}, so the script-signing rule cannot tell which one");
    }

    [Fact]
    public async Task AnUnclearCreateIsShownAsTheTextOfTheServiceWithoutUndo()
    {
        using var h = new ServicePageHarness();
        h.Engine.Site.SetOption("CreateTwice:Package", true);
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Package, "MCM0001B");
        var outcomes = Record(h);

        await form.DeployCommand.ExecuteAsync(null);

        AssertFailedWithoutChange(h, page, outcomes, "The result of the create of package CMCO-CANARY-Package");
        Assert.Contains("2 new deployments appeared", page.Shared.LastResults.Single().Message);
        Assert.Equal(2, h.Engine.Site.List("Adverts").Count);
    }

    [Fact]
    public async Task ADistributionThatCouldNotBeLoggedIsShownAsTheTextOfTheServiceWithoutUndo()
    {
        var state = WithPackageWithoutContent();
        using var h = new ServicePageHarness(state, failAppend: n => n == 0 ? new IOException("full") : null);
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Package, "MCM0001C");
        form.DpGroups.Single(g => g.Name == "All DPs").IsChecked = true;
        form.DistributeFirst = true;
        var outcomes = Record(h);

        await form.DeployCommand.ExecuteAsync(null);

        AssertFailedWithoutChange(h, page, outcomes, "The content distribution may have been requested but could not be logged, so the deployment was not created.");
        Assert.Equal(0, h.Engine.CreateCalls);
    }

    [Fact]
    public async Task ASecondDeploymentOfAnotherAdminIsReportedAndTheUndoRemovesOnlyOurs()
    {
        using var h = new ServicePageHarness();
        h.Engine.Site.SetOption("ForeignOnReadBack:Package", true);
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Package, "MCM0001B");
        var outcomes = Record(h);

        await form.DeployCommand.ExecuteAsync(null);

        var outcome = Assert.Single(outcomes);
        var ours = outcome.UndoChange!.CreatedId!;
        var foreign = h.Engine.Site.List("Adverts").Select(a => (string)a!["AdvertisementID"]!).Single(id => id != ours);
        var row = Assert.Single(page.Shared.LastResults);
        Assert.Equal(("Failed", true, outcome.Message), (row.OutcomeText, row.PartialChange, row.Message));
        Assert.Contains($"A second deployment of the same object to this collection exists (made by someone else at the same time): {foreign}. The one made here is {ours}; Undo removes only that one.", row.Message);
        Assert.Equal(0, h.History.Entries.Last().Counts["skipped"]);
        Assert.Equal(ours, Assert.Single(page.Shared.LastPlan!.UndoChanges()).Undo.Existing!.DeploymentId);

        await page.Shared.UndoLastCommand.ExecuteAsync(null);

        Assert.Equal(ours, Assert.Single(h.Service.Applied.Last()).Existing!.DeploymentId);
        Assert.Equal(foreign, (string)Assert.Single(h.Engine.Site.List("Adverts"))!["AdvertisementID"]!);
        Assert.False(page.Shared.HasUndo);
    }

    /// <summary>A signing check that allows the first Required deployment it is asked about and refuses <paramref name="packaged"/> after that.</summary>
    private sealed class LaterSigning(string packaged) : IDeploymentSigningCheck
    {
        private int _calls;
        public IReadOnlySet<string> PackagedApplications() => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { packaged };
        public string? RequiredDeploymentRefusal(string applicationName) =>
            Interlocked.Increment(ref _calls) > 1 && string.Equals(applicationName, packaged, StringComparison.OrdinalIgnoreCase) ? "refused " + applicationName : null;
    }
}
