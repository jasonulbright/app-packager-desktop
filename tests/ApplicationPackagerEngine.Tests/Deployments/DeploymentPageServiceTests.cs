using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>The clock of the lab site: 12:00 UTC, the clients' local time UTC-4.</summary>
internal sealed class LabClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(DeploymentHarness.UtcNow);
    public override TimeZoneInfo LocalTimeZone => DeploymentHarness.Local;
}

/// <summary>The real service between the writer and the stub site; it records what the writer sends and can act after each outcome.</summary>
internal sealed class HookedDeploymentService(IDeploymentService inner) : IDeploymentService
{
    public List<IReadOnlyList<DeploymentChange>> Applied { get; } = [];
    public List<DeploymentChange> Prepared { get; } = [];
    public Action<DeploymentOutcome>? OnOutcome;

    /// <summary>Runs before the changes reach the service; a test can change the site between Prepare and Apply here.</summary>
    public Action<IReadOnlyList<DeploymentChange>>? BeforeApply;

    public Task<IReadOnlyList<DeployableObject>> ListObjectsAsync(DeploymentObjectType type, CancellationToken ct) => inner.ListObjectsAsync(type, ct);
    public Task<IReadOnlyList<string>> ListProgramsAsync(string packageId, CancellationToken ct) => inner.ListProgramsAsync(packageId, ct);
    public Task<IReadOnlyList<DeploymentCollection>> ListCollectionsAsync(CancellationToken ct) => inner.ListCollectionsAsync(ct);
    public Task<IReadOnlyList<DpGroupInfo>> ListDpGroupsAsync(DeployableObject obj, CancellationToken ct) => inner.ListDpGroupsAsync(obj, ct);
    public Task<IReadOnlyList<DeploymentRecord>> ListDeploymentsAsync(DeployableObject obj, CancellationToken ct) => inner.ListDeploymentsAsync(obj, ct);

    public async Task<DeploymentChange> PrepareAsync(DeploymentChange request, CancellationToken ct)
    {
        var prepared = await inner.PrepareAsync(request, ct);
        lock (Prepared) Prepared.Add(prepared);
        return prepared;
    }

    public Task<IReadOnlyList<DeploymentOutcome>> ApplyAsync(IReadOnlyList<DeploymentChange> changes, IProgress<DeploymentOutcome> progress, CancellationToken ct)
    {
        lock (Applied) Applied.Add(changes);
        BeforeApply?.Invoke(changes);
        return inner.ApplyAsync(changes, new Hook(progress, this), ct);
    }

    private sealed class Hook(IProgress<DeploymentOutcome> progress, HookedDeploymentService owner) : IProgress<DeploymentOutcome>
    {
        public void Report(DeploymentOutcome value)
        {
            progress.Report(value);
            owner.OnOutcome?.Invoke(value);
        }
    }
}

/// <summary>
/// The Deployment Helper page over the real DeploymentService, the stub site and a real audit log
/// whose path is the page setting, read at every call as the application registers it.
/// </summary>
internal sealed class ServicePageHarness : IDisposable
{
    public readonly string Folder = Path.Combine(Path.GetTempPath(), "apkg-dhpage-" + Guid.NewGuid().ToString("N"));

    public ServicePageHarness(JsonObject? state = null, IDeploymentSigningCheck? signing = null, int maxRows = 1000, Func<int, Exception?>? failAppend = null)
    {
        Settings = new AppSettings
        {
            SiteCode = "LAB",
            ProviderMachine = "cm01.lab.local",
            DeploymentHelper = new DeploymentHelperSettings { AuditLogPath = FirstPath }
        };
        Paths = new SettingsDeploymentAuditPath(() => Settings);
        Engine = new DeploymentHarness(state, signing, () => Paths.AuditLogPath, maxRows, failAppend);
        Service = new HookedDeploymentService(Engine.Service);
        Writer = new DeploymentWriter(Service, History, Log, () => "LAB", _ => Task.FromResult(true));
    }

    public AppSettings Settings { get; }
    public SettingsDeploymentAuditPath Paths { get; }
    public DeploymentHarness Engine { get; }
    public HookedDeploymentService Service { get; }
    public DeploymentWriter Writer { get; }
    public ScriptedPlanDialogs Plans { get; } = new();
    public RecordingSiteHistory History { get; } = new();
    public RecordingMaintenanceLog Log { get; } = new();
    public FakeDeploymentTemplates Templates { get; } = new();
    public LabClock Clock { get; } = new();
    public string FirstPath => Path.Combine(Folder, "first", "deployment-audit.jsonl");

    public DeploymentPageServices Services() => new(
        Deployments: Service, Collections: Engine.Collections, AuditLog: Engine.Audit, Templates: Templates, Reports: new FakeDeploymentReports(),
        Dialogs: new FakeDeploymentDialogs(), Plans: Plans, Writer: Writer, LoadSettings: () => Settings, LoadPreferences: () => Settings.DeploymentHelper,
        SavePreferences: s => { Settings.DeploymentHelper = s; return true; }, Rings: new UnavailableRingService(),
        RingWriter: new RingWriter(new UnavailableRingService(), History, Log, () => "LAB", _ => Task.FromResult(true)), Log: Log, Clock: Clock);

    public async Task<DeploymentHelperViewModel> OpenAsync()
    {
        var page = new DeploymentHelperViewModel(Services());
        await page.OnNavigatedToAsync();
        return page;
    }

    /// <summary>Picks the object and the collection and waits for the programs and DP groups of the object.</summary>
    public static async Task<DeploymentFormViewModel> PickAsync(DeploymentHelperViewModel page, DeploymentObjectType type, string objectId, string collectionId = "MCM00098")
    {
        var form = page.Deploy;
        if (form.Type != type)
        {
            form.TypeIndex = (int)type;
            await WaitAsync(() => !form.IsLoading && form.Objects.Any(o => o.Object.Type == type));
        }
        form.SelectedObject = form.Objects.Single(o => o.Object.Id == objectId);
        form.SelectedCollection = form.Collections.Single(c => c.CollectionId == collectionId);
        await WaitAsync(() => !form.IsLoadingDetails);
        if (type == DeploymentObjectType.Package) form.SelectedProgram = form.Programs[0];
        return form;
    }

    public static void SetRequired(DeploymentFormViewModel form)
    {
        form.IsRequired = true;
        form.DeadlineDate = new DateTime(2026, 10, 22);
        form.DeadlineTimeText = "17:30";
    }

    public static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not met.");
            await Task.Delay(10);
        }
    }

    /// <summary>The records of the audit log file at <paramref name="path"/>.</summary>
    public static IReadOnlyList<DeploymentAuditRecord> RecordsAt(string path) => File.Exists(path) ? new DeploymentAuditLog(() => path).Read(100) : [];

    public void Dispose()
    {
        Engine.Dispose();
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { }
    }
}

public sealed class DeploymentPageServiceTests
{
    private static readonly string[] Labels = ["Object exists", "Content distributed", "Collection is a device collection", "Collection is not built in",
        "No duplicate deployment", "Schedule and options", "Audit log", "Script signing"];

    // ---- Checks ---------------------------------------------------------------------------------

    [Fact]
    public async Task ValidateListsEveryCheckOfTheServiceWithItsLabelAndMessageAndShowsAWarning()
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.SoftwareUpdateGroup, "16778893");

        await form.ValidateCommand.ExecuteAsync(null);

        Assert.Equal(Labels, form.Checks.Select(c => c.Check));
        Assert.All(form.Checks, c => Assert.False(string.IsNullOrWhiteSpace(c.Message)));
        Assert.Equal("Warn", form.Checks.Single(c => c.Name == "ContentDistributed").Result);
        Assert.Equal("No check blocks the deployment. 1 check has a warning: read its message.", form.ValidationMessage);
        Assert.Equal(0, h.Engine.CreateCalls);
    }

    [Fact]
    public async Task AFailedCheckOutsideTheFiveBlocksTheDeployWithItsPlainMessage()
    {
        const string refusal = "The script-signing policy requires signed scripts, and the record of this application has none.";
        using var h = new ServicePageHarness(signing: new DeploymentHarness.FakeSigning(new HashSet<string> { DeploymentHarness.App.Name }, refusal));
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Application, "16779607");
        ServicePageHarness.SetRequired(form);

        await form.DeployCommand.ExecuteAsync(null);

        Assert.Empty(h.Plans.Shown);
        Assert.Equal($"The deployment cannot be created: Script signing: {refusal}", form.FormMessage);
        Assert.Contains(form.Checks, c => c.Name == "Signing" && c.Result == "Fail" && c.Message == refusal);
        Assert.Equal(0, h.Engine.CreateCalls);
    }

    [Fact]
    public async Task AStaleCollectionComesBackAsAFailedCheckAndBlocksThePlan()
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Application, "16779607");
        var collections = h.Engine.Site.List("Collections");
        collections.Remove(collections.Single(c => (string)c!["CollectionID"]! == "MCM00098"));

        await form.DeployCommand.ExecuteAsync(null);

        Assert.Empty(h.Plans.Shown);
        Assert.Contains("Collection is a device collection: Collection MCM00098 does not exist.", form.FormMessage);
    }

    [Fact]
    public async Task ARefusedRequestShowsTheServiceMessageAndNoExceptionText()
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        var request = new DeploymentChange(Kind: DeploymentChangeKind.Distribute, Object: DeploymentHarness.UpdateGroup, Collection: null, Options: null,
            DpGroups: ["All DPs"], CreateNowContentDistributing: false, RequiredConfirmed: false, BackdatedAcknowledged: false, Existing: null,
            Checks: [], Impact: [], CreatedId: null);

        var run = await page.Shared.RunPlanAsync([request], prepared => DeploymentText.BuildDeployPlan(prepared, h.Clock));

        Assert.Null(run);
        Assert.Equal("The content of a software update group is in deployment packages. Distribute the deployment packages in the console.", page.Shared.StatusMessage);
        Assert.Empty(h.Plans.Shown);
    }

    [Fact]
    public async Task UndoAndRemoveAreBlockedWhileTheDeploymentDiffersFromTheCapturedValues()
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Application, "16779607");
        ServicePageHarness.SetRequired(form);
        await form.DeployCommand.ExecuteAsync(null);
        Assert.True(page.Shared.LastResults.Single().Succeeded, page.Shared.LastResults.Single().Message);
        var stored = h.Engine.Stored("AppAssignments");
        stored["EnforcementDeadline"] = "2026-10-24T17:30:00";
        h.Plans.Shown.Clear();

        await page.Shared.UndoLastCommand.ExecuteAsync(null);
        Assert.Empty(h.Plans.Shown);
        Assert.StartsWith("The deployment cannot be removed: Deployment unchanged: The deployment changed since it was captured (deadline)", page.Shared.StatusMessage);

        page.SelectedTabIndex = DeploymentHelperViewModel.HistoryTab;
        await page.CurrentTab.EnsureLoadedAsync();
        page.History.SelectedRow = page.History.Rows.Single(r => r.Event == "Create");
        await page.History.RemoveDeploymentCommand.ExecuteAsync(null);
        Assert.Empty(h.Plans.Shown);
        Assert.StartsWith($"Undo from History is refused: Deployment {stored["AssignmentUniqueID"]} changed since it was captured (deadline), so it is not removed.", page.Shared.StatusMessage);
        Assert.Single(h.Engine.Site.List("AppAssignments"));

        stored = h.Engine.Stored("AppAssignments");
        stored["EnforcementDeadline"] = "2026-10-22T17:30:00";
        await page.History.RemoveDeploymentCommand.ExecuteAsync(null);
        Assert.Single(h.Plans.Shown);
        Assert.Empty(h.Engine.Site.List("AppAssignments"));
        var undo = h.Engine.Audit.Read(10).Last();
        Assert.Equal(("Undo", "Success"), (undo.Event, undo.Result));
    }

    // ---- Audit log path -------------------------------------------------------------------------

    [Fact]
    public async Task AnAuditLogPathSavedInOptionsIsWhereTheNextCreateWritesAndItsProbeIsShown()
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        page.SelectedTabIndex = DeploymentHelperViewModel.OptionsTab;
        await page.CurrentTab.EnsureLoadedAsync();
        var options = page.Options;
        Assert.Equal(h.FirstPath, options.AuditLogPath);
        Assert.Equal(h.FirstPath, options.AuditLogInUse);

        var second = Path.Combine(h.Folder, "second", "deployment-audit.jsonl");
        options.AuditLogPath = second;
        await options.SavePathCommand.ExecuteAsync(null);
        Assert.Equal(second, h.Settings.DeploymentHelper!.AuditLogPath);
        Assert.Equal(second, h.Engine.Audit.Path);
        Assert.Equal(second, options.AuditLogInUse);
        Assert.True(options.ProbeWritable);
        Assert.Equal("The audit log can be written. A deployment can be created.", options.ProbeText);

        page.SelectedTabIndex = DeploymentHelperViewModel.DeployTab;
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Application, "16779607");
        await form.DeployCommand.ExecuteAsync(null);
        Assert.True(page.Shared.LastResults.Single().Succeeded, page.Shared.LastResults.Single().Message);
        Assert.Empty(ServicePageHarness.RecordsAt(h.FirstPath));
        Assert.Equal("Create", ServicePageHarness.RecordsAt(second).Single().Event);

        var blocker = Path.Combine(h.Folder, "blocked");
        File.WriteAllText(blocker, "a file where the folder of the log would be");
        options.AuditLogPath = Path.Combine(blocker, "deployment-audit.jsonl");
        await options.SavePathCommand.ExecuteAsync(null);
        Assert.False(options.ProbeWritable);
        Assert.StartsWith("The audit log cannot be written, so no deployment can be created.", options.ProbeText);
        Assert.Equal(Path.Combine(blocker, "deployment-audit.jsonl"), options.AuditLogInUse);
    }

    [Fact]
    public void AnEmptyPageSettingSelectsTheDefaultAuditLog()
    {
        var settings = new AppSettings();
        var paths = new SettingsDeploymentAuditPath(() => settings);
        Assert.Null(paths.AuditLogPath);
        settings.DeploymentHelper = new DeploymentHelperSettings { AuditLogPath = "   " };
        Assert.Null(paths.AuditLogPath);
        settings.DeploymentHelper = new DeploymentHelperSettings { AuditLogPath = @" \\fs01\deploy$\audit.jsonl " };
        Assert.Equal(@"\\fs01\deploy$\audit.jsonl", paths.AuditLogPath);
        Assert.Equal(DeploymentAuditLog.DefaultPath(), new DeploymentAuditLog(() => new SettingsDeploymentAuditPath(() => new AppSettings()).AuditLogPath).Path);
    }

    // ---- Creates and the second confirmation ----------------------------------------------------

    [Theory]
    [InlineData(DeploymentObjectType.Application, "16779607", "AppAssignments", "AssignmentUniqueID")]
    [InlineData(DeploymentObjectType.Package, "MCM0001B", "Adverts", "AdvertisementID")]
    [InlineData(DeploymentObjectType.TaskSequence, "MCM00030", "Adverts", "AdvertisementID")]
    [InlineData(DeploymentObjectType.SoftwareUpdateGroup, "16778893", "UgAssignments", "AssignmentUniqueID")]
    public async Task TheDeployTabCreatesEachObjectTypeThroughTheRealService(DeploymentObjectType type, string id, string list, string key)
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, type, id);

        await form.DeployCommand.ExecuteAsync(null);

        var row = Assert.Single(page.Shared.LastResults);
        Assert.True(row.Succeeded, row.Message);
        Assert.Contains("The stored values equal the plan.", row.Message);
        var deploymentId = (string)h.Engine.Stored(list)[key]!;
        var record = h.Engine.Audit.Read(10).Single();
        Assert.Equal(("Create", "Success", deploymentId), (record.Event, record.Result, record.DeploymentUniqueID));
        Assert.Equal(deploymentId, page.Shared.LastPlan!.UndoChanges().Single().Undo.Existing!.DeploymentId);
    }

    [Fact]
    public async Task ARequiredCreateRunsOnlyWithTheSecondBoxAndTheServiceRefusesItWithout()
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Application, "16779607");
        ServicePageHarness.SetRequired(form);
        h.Plans.AcknowledgeExtra = false;

        await form.DeployCommand.ExecuteAsync(null);
        Assert.StartsWith("I understand that this Required deployment installs", h.Plans.Shown.Single().ExtraConfirmation);
        Assert.Empty(h.Service.Applied);

        var prepared = await h.Engine.PrepareAsync(DeploymentHarness.Create(DeploymentHarness.App, DeploymentHarness.Options(DeploymentObjectType.Application, "Required")));
        var plan = new DeploymentPlan("Create deployment", "Deploy", "Create deployment", [DeploymentPlanEntry.Write(new ChangePlanItem("Create deployment", "7-Zip", null), prepared)]);
        var dialog = new ChangePlanViewModel(plan.Plan, h.Writer.RunFor(plan), h.Writer.UndoFor(plan));
        await dialog.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentWriter.NotAcknowledgedText, dialog.ResultRows.Single().Message);
        Assert.Empty(h.Service.Applied);

        var refused = (await h.Engine.ApplyAsync(prepared)).Single();
        Assert.Equal("Skipped: " + DeploymentService.RequiredNotConfirmedMessage, refused.Message);
        Assert.Equal(0, h.Engine.CreateCalls);

        h.Plans.AcknowledgeExtra = true;
        await form.DeployCommand.ExecuteAsync(null);
        var sent = Assert.Single(Assert.Single(h.Service.Applied));
        Assert.True(sent.RequiredConfirmed);
        Assert.False(sent.BackdatedAcknowledged);
        Assert.True(page.Shared.LastResults.Single().Succeeded, page.Shared.LastResults.Single().Message);
        Assert.Equal(0, (int)h.Engine.Stored("AppAssignments")["OfferTypeID"]!);
    }

    [Fact]
    public async Task TheWriterSendsOnlyTheAcknowledgementTheBoxNamedAndNoneWithAnUndo()
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Package, "MCM0001B");
        form.AvailableTimeText = "07:00";
        h.Plans.RunUndo = true;

        await form.DeployCommand.ExecuteAsync(null);

        Assert.DoesNotContain("Required", h.Plans.Shown.Single().ExtraConfirmation);
        var create = Assert.Single(h.Service.Applied[0]);
        Assert.Equal((false, true, false), (create.RequiredConfirmed, create.BackdatedAcknowledged, create.CreateNowContentDistributing));
        var undo = Assert.Single(h.Service.Applied[1]);
        Assert.Equal(DeploymentChangeKind.RemoveDeployment, undo.Kind);
        Assert.Equal((false, false, false), (undo.RequiredConfirmed, undo.BackdatedAcknowledged, undo.CreateNowContentDistributing));
        Assert.Empty(h.Engine.Site.List("Adverts"));
        Assert.Equal(["Create", "Undo"], h.Engine.Audit.Read(10).Select(r => r.Event));
    }

    // ---- Undo -----------------------------------------------------------------------------------

    [Fact]
    public async Task TheUndoOfThePageRemovesTheCreatedDeploymentThroughItsOwnPlan()
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.SoftwareUpdateGroup, "16778893");
        ServicePageHarness.SetRequired(form);
        await form.DeployCommand.ExecuteAsync(null);
        var id = (string)h.Engine.Stored("UgAssignments")["AssignmentUniqueID"]!;

        await page.Shared.UndoLastCommand.ExecuteAsync(null);

        var plan = h.Plans.Shown[1];
        Assert.True(plan.IsDestructive);
        Assert.Null(plan.ExtraConfirmation);
        Assert.Contains(id, plan.Items.Single().Detail);
        Assert.Empty(h.Engine.Site.List("UgAssignments"));
        var removal = Assert.Single(h.Service.Applied[1]);
        Assert.False(removal.RequiredConfirmed);
        Assert.Equal(id, h.Engine.Audit.Read(10).Last().Undoes);
        Assert.False(page.Shared.HasUndo);
    }

    // ---- Distribute and create ------------------------------------------------------------------

    private static JsonObject WithPackageWithoutContent()
    {
        var state = StubDeploymentSite.Lab();
        state["Packages"]!.AsArray().Add(JsonNode.Parse("""{ "PackageID": "MCM0001C", "Name": "DH Package Without Content", "Version": "1", "Programs": [ "Run" ], "Content": { "Targeted": 0, "Success": 0 } }"""));
        return state;
    }

    [Fact]
    public async Task ACombinedPlanDistributesFirstAndCreatesNowOnlyWithThePlanBox()
    {
        using var h = new ServicePageHarness(WithPackageWithoutContent());
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Package, "MCM0001C");
        form.DpGroups.Single(g => g.Name == "All DPs").IsChecked = true;
        form.DistributeFirst = true;
        h.Plans.ChooseOption = true;

        await form.DeployCommand.ExecuteAsync(null);

        var plan = h.Plans.Shown.Single();
        Assert.Equal(DeploymentText.CreateNowChoice, plan.OptionalChoice);
        Assert.Equal("Distribute and create deployment", plan.Items.Single().Action);
        var sent = Assert.Single(Assert.Single(h.Service.Applied));
        Assert.True(sent.CreateNowContentDistributing);
        Assert.Equal(["All DPs"], sent.DpGroups);
        var log = h.Engine.Site.Log;
        Assert.True(log.IndexOf("Start-CMContentDistribution:All DPs") < log.FindIndex(l => l.StartsWith("New-CMPackageDeployment", StringComparison.Ordinal)));
        Assert.True(page.Shared.LastResults.Single().Succeeded, page.Shared.LastResults.Single().Message);
        Assert.Equal(["Distribute", "Create"], h.Engine.Audit.Read(10).Select(r => r.Event));
    }

    [Fact]
    public async Task WithoutThePlanBoxACombinedPlanDistributesAndSkipsTheCreateUntilTheContentIsThere()
    {
        using var h = new ServicePageHarness(WithPackageWithoutContent());
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Package, "MCM0001C");
        form.DpGroups.Single(g => g.Name == "All DPs").IsChecked = true;
        form.DistributeFirst = true;

        await form.DeployCommand.ExecuteAsync(null);

        Assert.False(Assert.Single(Assert.Single(h.Service.Applied)).CreateNowContentDistributing);
        Assert.Contains("Start-CMContentDistribution:All DPs", h.Engine.Site.Log);
        Assert.Equal(0, h.Engine.CreateCalls);
        var row = page.Shared.LastResults.Single();
        Assert.False(row.Skipped);
        Assert.StartsWith(DeploymentWriter.DistributionOnlyText, row.Message);
        Assert.False(page.Shared.HasUndo);
    }

    // ---- Failures after the write, partial runs and cancel --------------------------------------

    [Fact]
    public async Task AnErrorAfterTheCreateKeepsTheUndoOfTheDeploymentItMade()
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.Package, "MCM0001B");
        h.Engine.Site.Fail("after-create:Package");

        await form.DeployCommand.ExecuteAsync(null);

        var row = page.Shared.LastResults.Single();
        Assert.False(row.Succeeded);
        Assert.True(row.PartialChange);
        Assert.DoesNotContain("SECRET-DETAIL", row.Message);
        var undo = page.Shared.LastPlan!.UndoChanges().Single().Undo;
        Assert.Equal(DeploymentChangeKind.RemoveDeployment, undo.Kind);
        Assert.Equal((string)h.Engine.Stored("Adverts")["AdvertisementID"]!, undo.Existing!.DeploymentId);

        await page.Shared.UndoLastCommand.ExecuteAsync(null);
        Assert.Empty(h.Engine.Site.List("Adverts"));
    }

    [Fact]
    public async Task AnUncertainCreateIsClaimedWithItsUndoOnlyWhenTheDeploymentIsProvablyItsOwn()
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.SoftwareUpdateGroup, "16778893");
        h.Engine.Site.AfterRun = p => (string?)p["Operation"] == "Create" ? new ConfigMgrSessionException(ConfigMgrFailure.Timeout, "timeout") : null;

        await form.DeployCommand.ExecuteAsync(null);

        Assert.True(page.Shared.LastResults.Single().PartialChange);
        Assert.Equal((string)h.Engine.Stored("UgAssignments")["AssignmentUniqueID"]!, page.Shared.LastPlan!.UndoChanges().Single().Undo.Existing!.DeploymentId);
    }

    [Fact]
    public async Task AnUncertainCreateNextToAnotherAdminsDeploymentOffersNoUndo()
    {
        using var h = new ServicePageHarness();
        var page = await h.OpenAsync();
        var form = await ServicePageHarness.PickAsync(page, DeploymentObjectType.TaskSequence, "MCM00030");
        h.Engine.Site.AfterRun = p => (string?)p["Operation"] == "Create" ? new ConfigMgrSessionException(ConfigMgrFailure.ProcessDied, "died") : null;
        h.Engine.Site.BeforeRun = (p, site) =>
        {
            if ((string?)p["Operation"] == "Create")
                site.List("Adverts").Add(JsonNode.Parse("""{ "AdvertisementID": "MCM20AAA", "PackageID": "MCM00030", "ProgramName": "*", "CollectionID": "MCM00098", "OfferType": 2, "PresentTime": "2026-10-20T08:05:00", "PresentTimeIsGMT": false, "ExpirationTimeEnabled": false, "AssignedScheduleEnabled": false, "AssignedSchedule": [], "AdvertFlags": 8978432, "RemoteClientFlags": 34896 }"""));
        };

        await form.DeployCommand.ExecuteAsync(null);

        var row = page.Shared.LastResults.Single();
        Assert.False(row.Succeeded);
        Assert.False(row.PartialChange);
        Assert.StartsWith("The result of the create", row.Message);
        Assert.False(page.Shared.HasUndo);
        Assert.DoesNotContain(h.Plans.LastViewModel!.Outcome.Result!.Items, i => i.PartialChange);
    }

    private static async Task<DeploymentPlan> PlanOfAsync(ServicePageHarness h, params DeploymentChange[] requests)
    {
        var prepared = new List<DeploymentChange>();
        foreach (var request in requests) prepared.Add(await h.Engine.PrepareAsync(request));
        return new DeploymentPlan("Create deployment", "Deploy three objects", "Create deployments",
            prepared.Select((c, i) => DeploymentPlanEntry.Write(new ChangePlanItem("Create deployment", $"object {i}", null), c)));
    }

    [Fact]
    public async Task APartialRunKeepsTheResultsAndTheUndoOfTheChangesThatRan()
    {
        using var h = new ServicePageHarness();
        var plan = await PlanOfAsync(h,
            DeploymentHarness.Create(DeploymentHarness.Package, DeploymentHarness.Options(DeploymentObjectType.Package)),
            DeploymentHarness.Create(DeploymentHarness.AppWithoutContent, DeploymentHarness.Options(DeploymentObjectType.Application)),
            DeploymentHarness.Create(DeploymentHarness.UpdateGroup, DeploymentHarness.Options(DeploymentObjectType.SoftwareUpdateGroup)));
        var dialog = new ChangePlanViewModel(plan.Plan, h.Writer.RunFor(plan), h.Writer.UndoFor(plan));

        await dialog.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(["Changed", "Skipped", "Changed"], dialog.ResultRows.Select(r => r.OutcomeText));
        Assert.StartsWith("Skipped", dialog.ResultRows[1].Message);
        Assert.Equal(2, plan.UndoChanges().Count);
        Assert.Equal(SiteOperationOutcome.Succeeded, h.History.Entries.Single().Outcome);

        await dialog.UndoCommand.ExecuteAsync(null);
        Assert.Empty(h.Engine.Site.List("Adverts"));
        Assert.Empty(h.Engine.Site.List("UgAssignments"));
    }

    [Fact]
    public async Task ACancelKeepsTheDoneCreateWithItsUndoAndSkipsTheRest()
    {
        using var h = new ServicePageHarness();
        var plan = await PlanOfAsync(h,
            DeploymentHarness.Create(DeploymentHarness.Package, DeploymentHarness.Options(DeploymentObjectType.Package)),
            DeploymentHarness.Create(DeploymentHarness.TaskSequence, DeploymentHarness.Options(DeploymentObjectType.TaskSequence)));
        var dialog = new ChangePlanViewModel(plan.Plan, h.Writer.RunFor(plan), h.Writer.UndoFor(plan));
        h.Service.OnOutcome = _ => dialog.RequestCancel();

        await dialog.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(["Changed", "Skipped"], dialog.ResultRows.Select(r => r.OutcomeText));
        Assert.Equal("Skipped: the run was cancelled before this change.", dialog.ResultRows[1].Message);
        Assert.Equal(ChangePlanStop.Cancelled, dialog.Outcome.Result!.Stop);
        Assert.Equal(1, h.Engine.CreateCalls);
        Assert.Single(plan.UndoChanges());
        Assert.True(dialog.UndoCommand.CanExecute(null));
    }
}
