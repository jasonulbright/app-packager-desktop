using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerEngine.Services.Supersedence;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.OneClick;

/// <summary>
/// How the One Click run uses the supersedence-only pass (the second Describe of Tests\OneClickSupersedence.Tests.ps1, as behavior of
/// the row runner instead of a read of the source): the pass runs only for a version the history holds as published, pending, unforced
/// and of the same build; it publishes nothing and runs before the stage and the other destinations; only an applied step or a line
/// without an older version ends the pending state; a publish whose step failed is recorded as published and pending; the pass stays
/// out of the other runs. Each test has its own data root and a ConfigMgr site code.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class OneClickSupersedenceRunTests : IDisposable
{
    private const string BuildId = "20261003-120000-abcdef01";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-oneclick-pass-run-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _log = [];

    public OneClickSupersedenceRunTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, SettingsService.LauncherSiteCodeVariable, SettingsService.LauncherProviderVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, Path.Combine(_root, "appdata"));
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, "MCM");
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, "cm01");
        Directory.CreateDirectory(DataRoot);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string DataRoot => Path.Combine(_root, "workbench");

    private sealed class FakeExecution(string root) : IOneClickExecution
    {
        public string Latest { get; set; } = "2.0";
        public RunSnapshot Snapshot { get; } = new() { ApplicationId = "catalog:widget", ProfileId = "default", BuildId = BuildId };
        public Func<string, PackagerExecutionResult> Package { get; set; } = target => new PackagerExecutionResult { StdOut = "Packaged: Widget 2.0" };
        public Func<OneClickSupersedencePassResult> Pass { get; set; } =
            () => OneClickSupersedencePassResult.Of(SupersedenceStepStatus.Applied, "Widget 2.0 supersedes Widget 1.0");
        public List<string> Calls { get; } = [];
        public List<(string Name, string Version)> PassCalls { get; } = [];

        public Task<string?> GetLatestVersionAsync(PackagerInfo row, CancellationToken ct) => Task.FromResult<string?>(Latest);
        public string VersionSource(PackagerInfo row) => "test-feed";
        public PackagerExecutionService.OneClickBuildPlan? ResolveBuildPlan(PackagerInfo row) => new(root, "catalog:widget", "default", 0);
        public string? StagedManifestPath(PackagerInfo row, PackagerExecutionResult stage, RunSnapshot? snapshot) => null;

        public RunSnapshot? CreateRowSnapshot(PackagerInfo row, PackagerExecutionService.OneClickBuildPlan plan, string target, out string? refusal)
        {
            refusal = null;
            return Snapshot;
        }

        public Task<PackagerExecutionResult> StageAsync(PackagerInfo row, Action<string> log, RunSnapshot snapshot, CancellationToken ct)
        {
            Calls.Add("Stage");
            return Task.FromResult(new PackagerExecutionResult { ExitCode = 0 });
        }

        public Task<PackagerExecutionResult> PackageAsync(PackagerInfo row, string comment, Action<string> log, OnExistingBehavior onExisting,
            RunSnapshot snapshot, string target, CancellationToken ct)
        {
            Calls.Add("Package");
            return Task.FromResult(Package(target));
        }

        public Task<WsusStepResult> PublishWsusAsync(PackagerInfo row, PackagerExecutionResult stage, RunSnapshot snapshot, Action<string> log, CancellationToken ct)
        {
            Calls.Add("WSUS");
            return Task.FromResult(new WsusStepResult(WsusStepStatus.Published, "published", [], [], "wsus-1"));
        }

        public Task<OneClickSupersedencePassResult> SupersedencePassAsync(PackagerInfo row, string applicationName, string version, Action<string> log, CancellationToken ct)
        {
            Calls.Add("Pass");
            PassCalls.Add((applicationName, version));
            return Task.FromResult(Pass());
        }
    }

    private OneClickRowRunner Runner(FakeExecution execution)
    {
        var inventory = new ApplicationInventoryService();
        return new OneClickRowRunner(execution, new SettingsService(), inventory, new ApplicationInventoryProjection(inventory), _log.Add);
    }

    private static OneClickPlanItem Item(bool configMgr, bool wsus) => new()
    {
        Key = "widget", Application = "Widget",
        Row = new PackagerInfo { Uid = "widget", Application = "Widget", Script = "widget.json" },
        ConfigMgr = configMgr, Wsus = wsus, Include = true, NotReady = new Dictionary<string, string>()
    };

    private static OneClickRunOptions Publish => new(OneClickActionNames.StageAndPackage, false, OneClickOnExisting.Skip, "");

    private static InventoryEnvironmentIdentity ConfigMgrEnvironment() =>
        ApplicationInventoryProjection.Environments(new SettingsService().Load()).Single(e => e.Destination == "ConfigMgr").Environment;

    /// <summary>The sealed build of 2.0 the run of this row finds current, so a changed build does not force a new publish.</summary>
    private void SealCurrentBuild(string version = "2.0")
    {
        var stage = Directory.CreateDirectory(Path.Combine(_root, "stage-" + Guid.NewGuid().ToString("N"))).FullName;
        Assert.NotNull(BuildRecords.Write(DataRoot, stage, new StageManifest
        {
            ApplicationId = "catalog:widget", BuildId = BuildId, ProfileId = "default", ProfileRevision = 0, SoftwareVersion = version, AppName = "Widget",
            // The run compares the policy digest of the saved signing policy with the one the build carries.
            ScriptSigning = new JsonObject { ["PolicyDigest"] = new SettingsService().Load().ScriptSigning.PolicyDigest }
        }));
    }

    /// <summary>The check and the stage of 2.0 from the day before, and a ConfigMgr publish of it with the given outcome.</summary>
    private void SeedPublish(InventoryOutcome outcome, string? objectId = "Widget 2.0", string? detail = null, InventoryEnvironmentIdentity? environment = null)
    {
        var inventory = new ApplicationInventoryService();
        var identity = new InventoryApplicationIdentity("widget", "catalog:widget", "default", 0);
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        inventory.RecordVendorCheck(new InventoryVendorCheck("widget", "Widget", "2.0", "test-feed", InventoryOutcome.Succeeded, at));
        inventory.RecordStagedBuild(new InventoryStagedBuild(identity, "2.0", BuildId, "build.json", "manifest.json", at));
        inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("widget", identity, environment ?? ConfigMgrEnvironment(), "Publish", "2.0",
            outcome, objectId, at, BuildId, Detail: detail));
    }

    private void SeedPending(string? objectId = "Widget 2.0", InventoryEnvironmentIdentity? environment = null)
    {
        SealCurrentBuild();
        SeedPublish(InventoryOutcome.PublishedSupersedenceNotApplied, objectId, "Published, supersedence not applied: access denied", environment);
    }

    private OneClickPublishRecord? StoredConfigMgr(FakeExecution execution)
    {
        var inventory = new ApplicationInventoryService();
        var row = new PackagerInfo { Uid = "widget", Application = "Widget", Script = "widget.json" };
        return new ApplicationInventoryProjection(inventory).Entry(row, new SettingsService().Load(), execution.ResolveBuildPlan(row))
            .Published.GetValueOrDefault("ConfigMgr");
    }

    [Fact]
    public async Task ThePassRunsForAVersionThatTheHistoryHoldsAsPublishedAndPendingAndPublishesNothing()
    {
        SeedPending();
        var execution = new FakeExecution(DataRoot);
        var item = Item(true, false);

        await Runner(execution).RunAsync(item, Publish, CancellationToken.None);

        Assert.Equal(["Pass"], execution.Calls);
        Assert.Equal(("Widget 2.0", "2.0"), Assert.Single(execution.PassCalls));
        Assert.Equal(OneClickOutcomes.Superseded, item.Outcome);
        Assert.Equal("supersedence applied: Widget 2.0 supersedes Widget 1.0", item.ResultConfigMgr);
        Assert.Equal("Widget 2.0", item.Ids["ConfigMgr"]);
        Assert.Equal("Superseded", item.Step);
        Assert.Contains("ConfigMgr: 2.0 already published, supersedence pending", item.Reason);
        Assert.Equal("Supersedence applied", item.Row!.Status);
    }

    [Fact]
    public async Task ThePassDoesNotRunWithForceAndTheVersionIsPublishedAgain()
    {
        SeedPending();
        var execution = new FakeExecution(DataRoot);

        await Runner(execution).RunAsync(Item(true, false), Publish with { Force = true }, CancellationToken.None);

        Assert.Equal(["Stage", "Package"], execution.Calls);
        Assert.Empty(execution.PassCalls);
        Assert.False(StoredConfigMgr(execution)!.SupersedencePending);
    }

    [Fact]
    public async Task ThePassDoesNotRunForAVersionWhoseStepWasNotPending()
    {
        SealCurrentBuild();
        SeedPublish(InventoryOutcome.Succeeded);
        var execution = new FakeExecution(DataRoot);
        var item = Item(true, false);

        await Runner(execution).RunAsync(item, Publish, CancellationToken.None);

        Assert.Empty(execution.Calls);
        Assert.Equal(OneClickOutcomes.Skipped, item.Outcome);
        Assert.Equal("up to date", item.Reason);
    }

    [Fact]
    public async Task ANewerVersionIsPublishedInFullAndRunsNoPass()
    {
        SeedPending();
        var execution = new FakeExecution(DataRoot) { Latest = "2.1" };

        await Runner(execution).RunAsync(Item(true, false), Publish, CancellationToken.None);

        Assert.Equal(["Stage", "Package"], execution.Calls);
        Assert.Empty(execution.PassCalls);
    }

    [Fact]
    public async Task AChangedBuildOfThePendingVersionIsPublishedAgainAndRunsNoPass()
    {
        // No sealed build of this profile revision and signing policy exists, so the build is stale: a new publish retries the step.
        SeedPublish(InventoryOutcome.PublishedSupersedenceNotApplied, "Widget 2.0", "Published, supersedence not applied: access denied");
        var execution = new FakeExecution(DataRoot);

        await Runner(execution).RunAsync(Item(true, false), Publish, CancellationToken.None);

        Assert.Equal(["Stage", "Package"], execution.Calls);
        Assert.Empty(execution.PassCalls);
    }

    [Fact]
    public async Task APendingRecordOfAnotherSiteIsIgnored()
    {
        SeedPending(environment: InventoryEnvironmentIdentity.ConfigMgr("XYZ", "other01"));
        var execution = new FakeExecution(DataRoot);

        await Runner(execution).RunAsync(Item(true, false), Publish, CancellationToken.None);

        Assert.Equal(["Stage", "Package"], execution.Calls);
        Assert.Empty(execution.PassCalls);
    }

    [Fact]
    public async Task ThePassRunsBeforeTheStageAndThePublishToTheOtherDestinations()
    {
        SeedPending();
        var execution = new FakeExecution(DataRoot);
        var item = Item(true, true);

        await Runner(execution).RunAsync(item, Publish, CancellationToken.None);

        Assert.Equal(["Pass", "Stage", "WSUS"], execution.Calls);
        Assert.Equal(OneClickOutcomes.Published, item.Outcome);
        Assert.Equal("supersedence applied: Widget 2.0 supersedes Widget 1.0", item.ResultConfigMgr);
        Assert.Equal("published wsus-1", item.ResultWsus);
    }

    [Theory]
    [InlineData(SupersedenceStepStatus.Applied, "supersedence applied: ", true, OneClickOutcomes.Superseded)]
    [InlineData(SupersedenceStepStatus.NoOlderVersion, "supersedence: ", true, OneClickOutcomes.Superseded)]
    [InlineData(SupersedenceStepStatus.NotApplied, "supersedence not applied: ", false, OneClickOutcomes.Skipped)]
    [InlineData(SupersedenceStepStatus.NotRun, "supersedence not run: ", false, OneClickOutcomes.Skipped)]
    public async Task OnlyAnAppliedStepOrALineWithoutAnOlderVersionEndsThePendingState(SupersedenceStepStatus status, string cell, bool ends, string outcome)
    {
        SeedPending();
        var execution = new FakeExecution(DataRoot) { Pass = () => OneClickSupersedencePassResult.Of(status, "the message") };
        var item = Item(true, false);

        await Runner(execution).RunAsync(item, Publish, CancellationToken.None);

        var record = StoredConfigMgr(execution)!;
        Assert.Equal(!ends, record.SupersedencePending);
        Assert.Equal(("2.0", "Widget 2.0"), (record.Version, record.Id));
        Assert.Equal(outcome, item.Outcome);
        Assert.Equal(cell + "the message", item.ResultConfigMgr);
        Assert.Equal(["Pass"], execution.Calls);
        if (status == SupersedenceStepStatus.NotApplied)
            Assert.Equal("Published, supersedence not applied: the message", item.Details["ConfigMgr"]);
    }

    [Fact]
    public async Task AModeOfOffKeepsThePendingStateAndTheNextRunPlansThePassAgain()
    {
        SeedPending();
        var execution = new FakeExecution(DataRoot)
        {
            Pass = () => OneClickSupersedencePassResult.Of(SupersedenceStepStatus.NotRun, "the supersedence mode is Off.")
        };

        await Runner(execution).RunAsync(Item(true, false), Publish, CancellationToken.None);
        await Runner(execution).RunAsync(Item(true, false), Publish, CancellationToken.None);

        Assert.Equal(["Pass", "Pass"], execution.Calls);
        Assert.True(StoredConfigMgr(execution)!.SupersedencePending);
    }

    [Fact]
    public async Task APassForTheWrongVersionIsNotAppliedAndKeepsThePendingState()
    {
        SeedPending();
        var execution = new FakeExecution(DataRoot)
        {
            Pass = () => OneClickSupersedencePassResult.Of(SupersedenceStepStatus.NotApplied, "The application 'Widget 2.0' is not on the site. Publish it first.")
        };
        var item = Item(true, false);

        await Runner(execution).RunAsync(item, Publish, CancellationToken.None);

        Assert.True(StoredConfigMgr(execution)!.SupersedencePending);
        Assert.StartsWith("supersedence not applied: The application 'Widget 2.0' is not on the site", item.ResultConfigMgr);
        Assert.Equal(OneClickOutcomes.Skipped, item.Outcome);
        Assert.Equal("Supersedence not applied", item.Row!.Status);
    }

    [Fact]
    public async Task APendingRecordWithoutAnApplicationNameIsNotAppliedAndTheSiteIsNotAsked()
    {
        SeedPending(objectId: null);
        var execution = new FakeExecution(DataRoot);
        var item = Item(true, false);

        await Runner(execution).RunAsync(item, Publish, CancellationToken.None);

        Assert.Empty(execution.PassCalls);
        Assert.Equal("supersedence not applied: the publish record has no application name.", item.ResultConfigMgr);
        Assert.True(StoredConfigMgr(execution)!.SupersedencePending);
    }

    [Fact]
    public async Task APublishWhoseStepFailedIsRecordedAsPublishedAndPendingAndNotAsFailed()
    {
        SealCurrentBuild();
        var execution = new FakeExecution(DataRoot)
        {
            Package = _ => new PackagerExecutionResult
            {
                StdOut = "Packaged: Widget 2.0", ConfigMgrOutcome = InventoryOutcome.PublishedSupersedenceNotApplied,
                ConfigMgrDetail = "Published, supersedence not applied: access denied"
            }
        };
        var item = Item(true, false);

        await Runner(execution).RunAsync(item, Publish, CancellationToken.None);

        Assert.Equal(OneClickOutcomes.Published, item.Outcome);
        Assert.Equal("published Widget 2.0, supersedence not applied", item.ResultConfigMgr);
        Assert.Equal("Published, supersedence not applied: access denied", item.Details["ConfigMgr"]);
        Assert.Contains("ConfigMgr: supersedence not applied, the next run applies it", item.Reason);
        var record = StoredConfigMgr(execution)!;
        Assert.True(record.SupersedencePending);
        Assert.Equal(("2.0", "Widget 2.0", "access denied"), (record.Version, record.Id, record.SupersedenceReason));
    }

    [Fact]
    public async Task TheNextRunAfterAFailedStepRunsThePassAndDoesNotPublishTheVersionAgainUntilItApplies()
    {
        SealCurrentBuild();
        var execution = new FakeExecution(DataRoot)
        {
            Package = _ => new PackagerExecutionResult
            {
                StdOut = "Packaged: Widget 2.0", ConfigMgrOutcome = InventoryOutcome.PublishedSupersedenceNotApplied,
                ConfigMgrDetail = "Published, supersedence not applied: access denied"
            },
            Pass = () => OneClickSupersedencePassResult.Of(SupersedenceStepStatus.NotApplied, "access denied")
        };
        await Runner(execution).RunAsync(Item(true, false), Publish, CancellationToken.None);

        var second = Item(true, false);
        await Runner(execution).RunAsync(second, Publish, CancellationToken.None);
        Assert.Equal(["Stage", "Package", "Pass"], execution.Calls);
        Assert.Equal(OneClickOutcomes.Skipped, second.Outcome);

        execution.Pass = () => OneClickSupersedencePassResult.Of(SupersedenceStepStatus.Applied, "Widget 2.0 supersedes Widget 1.0");
        var third = Item(true, false);
        await Runner(execution).RunAsync(third, Publish, CancellationToken.None);
        Assert.Equal(["Stage", "Package", "Pass", "Pass"], execution.Calls);
        Assert.Equal(OneClickOutcomes.Superseded, third.Outcome);

        // Ended: the next run finds the version published and has nothing to do.
        var fourth = Item(true, false);
        await Runner(execution).RunAsync(fourth, Publish, CancellationToken.None);
        Assert.Equal(4, execution.Calls.Count);
        Assert.Equal("ConfigMgr: 2.0 already published", fourth.Reason);
    }

    [Theory]
    [InlineData(OneClickActionNames.Report)]
    [InlineData(OneClickActionNames.Stage)]
    public async Task ThePassStaysOutOfTheRunsThatDoNotPublish(string action)
    {
        SeedPending();
        var execution = new FakeExecution(DataRoot);

        await Runner(execution).RunAsync(Item(true, false), new OneClickRunOptions(action, false, OneClickOnExisting.Skip, ""), CancellationToken.None);

        Assert.Empty(execution.PassCalls);
        Assert.True(StoredConfigMgr(execution)!.SupersedencePending);
    }

    [Fact]
    public void TheProjectionReadsThePendingStateAndTheReasonFromTheInventory()
    {
        SeedPending();

        var record = StoredConfigMgr(new FakeExecution(DataRoot))!;

        Assert.True(record.SupersedencePending);
        Assert.Equal("access denied", record.SupersedenceReason);
        Assert.Equal("Widget 2.0", record.Id);
    }

    // ---- the plan window ----

    [Fact]
    public async Task ThePlanWindowShowsThePassAndTheRunNamesTheSupersededRows()
    {
        var published = new Dictionary<string, OneClickPublishRecord>();
        OneClickHistory.SetPublished(published, new Dictionary<string, OneClickRefusalRecord>(), "ConfigMgr", "2.0", "Widget 2.0", DateTimeOffset.Now,
            supersedenceNotApplied: true, supersedenceReason: "access denied");
        var runner = new PassRunner();
        var vm = new OneClickPlanViewModel(new OneClickPlanContext
        {
            Apps = [(new OneClickApp("widget", "Widget", "V", "2.0"), (PackagerInfo?)new PackagerInfo { Uid = "widget", Application = "Widget" })],
            Settings = () => OneClickPlannerTests.Settings("MECM", ["widget"]),
            History = new Dictionary<string, OneClickHistoryEntry> { ["widget"] = new() { Published = published } },
            Action = OneClickActionNames.StageAndPackage,
            ReportFolder = Path.Combine(_root, "reports")
        }, runner);

        var row = vm.Rows.Single();
        Assert.Equal("Check, Supersedence", row.Planned);
        Assert.EndsWith(", 1 supersedence only", vm.SummaryText);

        vm.Force = true;
        Assert.Equal("Check, Stage, Publish to ConfigMgr", row.Planned);
        vm.Force = false;
        Assert.Equal("Check, Supersedence", row.Planned);

        await vm.RunCommand.ExecuteAsync(null);

        Assert.StartsWith("Complete: 0 published, 0 failed, 1 superseded. Report: one-click-", vm.SummaryText);
        var md = File.ReadAllText(vm.ReportPath);
        Assert.Contains("| Supersedence applied | 1 |", md);
        Assert.Contains("| Widget | 2.0 | Superseded | supersedence applied: Widget 2.0 supersedes Widget 1.0 |", md);
    }

    private sealed class PassRunner : IOneClickRunner
    {
        public Task<bool> RunAsync(IReadOnlyList<OneClickPlanItem> rows, OneClickRunOptions options, CancellationToken ct)
        {
            foreach (var r in rows)
            {
                r.SetResult("ConfigMgr", "supersedence applied: Widget 2.0 supersedes Widget 1.0", "Widget 2.0");
                r.Outcome = OneClickOutcomes.Superseded;
            }
            return Task.FromResult(false);
        }
    }
}
