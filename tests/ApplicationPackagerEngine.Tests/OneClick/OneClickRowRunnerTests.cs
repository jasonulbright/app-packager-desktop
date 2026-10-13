using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.OneClick;

/// <summary>
/// The per-row One Click run through an execution seam: one snapshot for Stage and every
/// publish, a per-row refusal for a snapshot that cannot be created, history only for a
/// real publish, refusal memory only for a real refusal, a finished publish kept on
/// cancel, the plan's not-ready destinations skipped, and a row restaged only for a stale
/// build left unpublished. Each test has its own data root.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class OneClickRowRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-oneclick-row-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _log = [];

    public OneClickRowRunnerTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, SettingsService.LauncherSiteCodeVariable, SettingsService.LauncherProviderVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, Path.Combine(_root, "appdata"));
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, null);
        Directory.CreateDirectory(Path.Combine(_root, "workbench"));
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class FakeExecution(string root) : IOneClickExecution
    {
        public string Latest { get; set; } = "2.0";
        public RunSnapshot? Snapshot { get; set; } = new() { ApplicationId = "catalog:widget", ProfileId = "default", BuildId = "20261003-120000-abcdef01" };
        public string? SnapshotRefusal { get; set; }
        public string? SnapshotTarget { get; private set; }
        public int StageExit { get; set; }
        public Func<string, PackagerExecutionResult> Package { get; set; } = target => new PackagerExecutionResult
        {
            StdOut = target == PublishRunTargets.ConfigMgr ? "Packaged: Widget 2.0" : "Staged content ready",
            IntunePublishNote = target == PublishRunTargets.IntuneOnly ? "published (app id intune-1)" : ""
        };
        public WsusStepResult Wsus { get; set; } = new(WsusStepStatus.Published, "published", [], [], "wsus-1");
        public Action<string>? OnPublish { get; set; }
        public List<(string Step, RunSnapshot? Snapshot, string Target)> Calls { get; } = [];
        public List<OnExistingBehavior> OnExisting { get; } = [];

        public Task<string?> GetLatestVersionAsync(PackagerInfo row, CancellationToken ct) => Task.FromResult<string?>(Latest);

        public string VersionSource(PackagerInfo row) => "test-feed";

        public PackagerExecutionService.OneClickBuildPlan? ResolveBuildPlan(PackagerInfo row) => new(root, "catalog:widget", "default", 0);

        public string? StagedManifestPath(PackagerInfo row, PackagerExecutionResult stage, RunSnapshot? snapshot) => null;

        public RunSnapshot? CreateRowSnapshot(PackagerInfo row, PackagerExecutionService.OneClickBuildPlan plan, string target, out string? refusal)
        {
            SnapshotTarget = target;
            refusal = SnapshotRefusal;
            return SnapshotRefusal is null ? Snapshot : null;
        }

        public Task<PackagerExecutionResult> StageAsync(PackagerInfo row, Action<string> log, RunSnapshot snapshot, CancellationToken ct)
        {
            Calls.Add(("Stage", snapshot, ""));
            return Task.FromResult(new PackagerExecutionResult { ExitCode = StageExit, StdErr = StageExit == 0 ? "" : "stage broke" });
        }

        public Task<PackagerExecutionResult> PackageAsync(PackagerInfo row, string comment, Action<string> log, OnExistingBehavior onExisting,
            RunSnapshot snapshot, string target, CancellationToken ct)
        {
            Calls.Add(("Package", snapshot, target));
            OnExisting.Add(onExisting);
            OnPublish?.Invoke(target);
            return Task.FromResult(Package(target));
        }

        public Task<WsusStepResult> PublishWsusAsync(PackagerInfo row, PackagerExecutionResult stage, RunSnapshot snapshot, Action<string> log, CancellationToken ct)
        {
            Calls.Add(("WSUS", snapshot, PublishRunTargets.WsusOnly));
            OnPublish?.Invoke(PublishRunTargets.WsusOnly);
            return Task.FromResult(Wsus);
        }
    }

    private OneClickRowRunner Runner(FakeExecution execution)
    {
        var inventory = new ApplicationInventoryService();
        return new OneClickRowRunner(execution, new SettingsService(), inventory, new ApplicationInventoryProjection(inventory), _log.Add);
    }

    private static OneClickPlanItem Item(bool configMgr, bool wsus, bool intune, IReadOnlyDictionary<string, string>? notReady = null) => new()
    {
        Key = "widget",
        Application = "Widget",
        Row = new PackagerInfo { Uid = "widget", Application = "Widget", Script = "widget.json" },
        ConfigMgr = configMgr,
        Wsus = wsus,
        Intune = intune,
        Include = true,
        NotReady = notReady ?? new Dictionary<string, string>()
    };

    private static OneClickRunOptions Publish => new(OneClickActionNames.StageAndPackage, false, OneClickOnExisting.Skip, "");

    private static AppCheckState Stored()
    {
        var inventory = new ApplicationInventoryService();
        var settings = new SettingsService().Load();
        var state = new AppCheckState();
        if (inventory.GetVendorState("widget") is { } vendor)
        {
            state.LatestVersion = vendor.LatestVersion ?? "";
            state.LastChecked = vendor.LastSucceededAt?.LocalDateTime;
        }
        foreach (var (destination, environment) in ApplicationInventoryProjection.Environments(settings))
        {
            var published = inventory.GetDestinationState("widget", environment, "Publish");
            if (!string.IsNullOrWhiteSpace(published?.LastSuccessVersion))
                state.Published[destination] = new OneClickPublishRecord
                {
                    Version = published.LastSuccessVersion,
                    Id = published.LastSuccessObjectId ?? "",
                    At = OneClickHistory.Stamp(published.LastSuccessAt ?? DateTimeOffset.MinValue)
                };
            if (inventory.GetLatestRefusal("widget", environment, "Publish") is { } refusal)
                state.NotSupported[destination] = new OneClickRefusalRecord
                {
                    Version = refusal.Version,
                    Reason = refusal.Detail ?? "",
                    At = OneClickHistory.Stamp(refusal.At ?? DateTimeOffset.MinValue)
                };
        }
        return state;
    }

    [Fact]
    public async Task OneSnapshotReachesTheStageAndEveryDestination()
    {
        var execution = new FakeExecution(Path.Combine(_root, "workbench"));
        var item = Item(true, true, true);

        await Runner(execution).RunAsync(item, Publish, CancellationToken.None);

        Assert.Equal(["Stage", "Package", "WSUS", "Package"], execution.Calls.Select(c => c.Step));
        Assert.All(execution.Calls, c => Assert.Same(execution.Snapshot, c.Snapshot));
        Assert.Equal([PublishRunTargets.ConfigMgr, PublishRunTargets.WsusOnly, PublishRunTargets.IntuneOnly], execution.Calls.Skip(1).Select(c => c.Target));
        Assert.Equal(OneClickOutcomes.Published, item.Outcome);
        Assert.Equal(["ConfigMgr", "Intune", "WSUS"], Stored().Published.Keys.Order());
    }

    [Fact]
    public async Task ARowForWsusAndIntunePublishesToBoth()
    {
        var execution = new FakeExecution(Path.Combine(_root, "workbench"));
        var item = Item(false, true, true);

        await Runner(execution).RunAsync(item, Publish, CancellationToken.None);

        Assert.Equal(["Stage", "WSUS", "Package"], execution.Calls.Select(c => c.Step));
        Assert.Equal(PublishRunTargets.IntuneOnly, execution.Calls[2].Target);
        Assert.Equal(RunTargets.WsusOnly, execution.SnapshotTarget);
        Assert.Equal("published intune-1", item.ResultIntune);
    }

    [Fact]
    public async Task ASnapshotThatCannotBeCreatedRefusesTheRowAndNothingStages()
    {
        var execution = new FakeExecution(Path.Combine(_root, "workbench")) { SnapshotRefusal = "SigningCertificateUnavailable: no certificate" };
        var item = Item(true, false, false);

        await Runner(execution).RunAsync(item, Publish, CancellationToken.None);

        Assert.Empty(execution.Calls);
        Assert.Equal(OneClickOutcomes.Failed, item.Outcome);
        Assert.Equal("run snapshot refused: SigningCertificateUnavailable: no certificate", item.Reason);
    }

    [Fact]
    public async Task AFailedDestinationIsNotRecordedAsPublished()
    {
        var execution = new FakeExecution(Path.Combine(_root, "workbench"))
        {
            Package = _ => new PackagerExecutionResult { ExitCode = 1, StdErr = "site unreachable" }
        };
        var item = Item(true, true, false);

        await Runner(execution).RunAsync(item, Publish, CancellationToken.None);

        Assert.Equal("failed: exit code 1", item.ResultConfigMgr);
        Assert.Equal(["WSUS"], Stored().Published.Keys);
        Assert.Equal(OneClickOutcomes.Failed, item.Outcome);
    }

    [Fact]
    public async Task ARowThatPublishesToOneDestinationAndFailsAnotherIsRecordedAsPartial()
    {
        var execution = new FakeExecution(Path.Combine(_root, "workbench"))
        {
            Wsus = new(WsusStepStatus.Failed, "server unreachable", [], [])
        };
        var item = Item(true, true, false);
        var inventory = new ApplicationInventoryService();
        var runner = new OneClickRowRunner(execution, new SettingsService(), inventory,
            new ApplicationInventoryProjection(inventory), _log.Add);

        await runner.RunAsync(item, Publish, CancellationToken.None);

        var check = Assert.Single(inventory.GetEvents("widget", 20), record => record.Kind == "VendorCheck");
        var operation = Assert.IsType<InventoryOperationState>(inventory.GetOperation(check.OperationId!));
        Assert.Equal(InventoryOutcome.Partial, operation.Outcome);
        Assert.Equal("published Widget 2.0", item.ResultConfigMgr);
        Assert.StartsWith("failed:", item.ResultWsus);
    }

    [Fact]
    public async Task RefusalMemoryIsWrittenOnlyForARealRefusal()
    {
        var failing = new FakeExecution(Path.Combine(_root, "workbench")) { Wsus = new(WsusStepStatus.Failed, "server unreachable", [], []) };
        await Runner(failing).RunAsync(Item(false, true, false), Publish, CancellationToken.None);
        Assert.Empty(Stored().NotSupported);

        var refusing = new FakeExecution(Path.Combine(_root, "workbench"))
        {
            Latest = "3.0",
            Wsus = new(WsusStepStatus.Refused, "not supported: CustomInstall. text", ["CustomInstall"], [])
        };
        var item = Item(false, true, false);
        await Runner(refusing).RunAsync(item, Publish, CancellationToken.None);
        Assert.Equal("3.0", Stored().NotSupported["WSUS"].Version);
        Assert.Equal(OneClickOutcomes.NotSupported, item.Outcome);
    }

    [Fact]
    public async Task ACancelAfterAPublishKeepsItsResultAndHistoryAndTheReasonSaysWhatWasPublished()
    {
        using var cts = new CancellationTokenSource();
        var execution = new FakeExecution(Path.Combine(_root, "workbench"))
        {
            OnPublish = target => { if (target == PublishRunTargets.ConfigMgr) cts.Cancel(); }
        };
        var item = Item(true, true, false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner(execution).RunAsync(item, Publish, cts.Token));

        Assert.Equal(["Stage", "Package"], execution.Calls.Select(c => c.Step));
        Assert.Equal("published Widget 2.0", item.ResultConfigMgr);
        Assert.Equal("2.0", Stored().Published["ConfigMgr"].Version);
        Assert.Equal(OneClickOutcomes.Published, item.Outcome);
        Assert.Equal("run canceled after publishing to ConfigMgr", item.Reason);
    }

    [Fact]
    public async Task ADestinationThePlanMarkedNotReadyIsSkippedWithThePlansReason()
    {
        var execution = new FakeExecution(Path.Combine(_root, "workbench"));
        var item = Item(true, true, false, new Dictionary<string, string> { ["ConfigMgr"] = "ConfigMgr console not detected" });

        await Runner(execution).RunAsync(item, Publish, CancellationToken.None);

        Assert.Equal(["Stage", "WSUS"], execution.Calls.Select(c => c.Step));
        Assert.Equal("skipped: ConfigMgr console not detected", item.ResultConfigMgr);
        Assert.Contains("ConfigMgr: ConfigMgr console not detected", item.Reason);
        Assert.DoesNotContain("ConfigMgr", Stored().Published.Keys);
    }

    [Fact]
    public async Task ForceDoesNotPublishToANotReadyDestination()
    {
        var execution = new FakeExecution(Path.Combine(_root, "workbench"));
        var item = Item(true, false, false, new Dictionary<string, string> { ["ConfigMgr"] = "site code not set" });

        await Runner(execution).RunAsync(item, Publish with { Force = true }, CancellationToken.None);

        Assert.DoesNotContain(execution.Calls, c => c.Step == "Package");
    }

    /// <summary>The check and the stage of 2.0 from the day before, and optionally its publish to one destination.</summary>
    private static void SeedEarlierRun(string? publishedTo = null)
    {
        var inventory = new ApplicationInventoryService();
        var settings = new SettingsService().Load();
        var identity = new InventoryApplicationIdentity("widget", "catalog:widget", "default", 0);
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        inventory.RecordVendorCheck(new InventoryVendorCheck("widget", "Widget", "2.0", "test-feed", InventoryOutcome.Succeeded, at));
        inventory.RecordStagedBuild(new InventoryStagedBuild(identity, "2.0", "old-build", "build.json", "manifest.json", at));
        if (publishedTo is null) return;
        var environment = ApplicationInventoryProjection.Environments(settings).First(e => e.Destination == publishedTo).Environment;
        inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("widget", identity, environment, "Publish", "2.0",
            InventoryOutcome.Succeeded, "Widget 2.0", at, "old-build"));
    }

    [Fact]
    public async Task ARowRestagedOnlyForAStaleBuildIsNotRepublishedToASiteThatCarriesTheVersionWithoutAPublishRecord()
    {
        SeedEarlierRun();
        var execution = new FakeExecution(Path.Combine(_root, "workbench"));
        var item = Item(true, false, false);
        item.Row!.CurrentVersion = "2.0";

        await Runner(execution).RunAsync(item, Publish, CancellationToken.None);

        Assert.Equal(["Stage"], execution.Calls.Select(c => c.Step));
        Assert.Equal(OneClickOutcomes.Staged, item.Outcome);
    }

    [Fact]
    public async Task AStaleBuildIsPublishedAgainToADestinationThatHasAPublishRecord()
    {
        // No sealed build of this profile revision and signing policy exists, so the build is stale.
        SeedEarlierRun(publishedTo: "ConfigMgr");
        var execution = new FakeExecution(Path.Combine(_root, "workbench"));
        var item = Item(true, false, false);

        await Runner(execution).RunAsync(item, Publish, CancellationToken.None);

        Assert.Equal(["Stage", "Package"], execution.Calls.Select(c => c.Step));
        Assert.Equal(OneClickOutcomes.Published, item.Outcome);
    }

    [Fact]
    public async Task WithoutForceASiteVersionNewerThanTheVendorVersionIsNotPublishedOver()
    {
        var execution = new FakeExecution(Path.Combine(_root, "workbench")) { Latest = "6.5.7" };
        var item = Item(true, false, false);
        item.Row!.CurrentVersion = "99.0";

        await Runner(execution).RunAsync(item, Publish, CancellationToken.None);

        Assert.Equal(["Stage"], execution.Calls.Select(c => c.Step));
        Assert.Equal("skipped: site version 99.0 is newer than 6.5.7", item.ResultConfigMgr);
        Assert.Equal("ConfigMgr: site version 99.0 is newer than 6.5.7", item.Reason);
        Assert.Equal(OneClickOutcomes.Staged, item.Outcome);
        Assert.DoesNotContain("ConfigMgr", Stored().Published.Keys);
    }

    [Fact]
    public async Task ForcePublishesOverASiteVersionNewerThanTheVendorVersion()
    {
        var execution = new FakeExecution(Path.Combine(_root, "workbench")) { Latest = "6.5.7" };
        var item = Item(true, false, false);
        item.Row!.CurrentVersion = "99.0";

        await Runner(execution).RunAsync(item, Publish with { Force = true }, CancellationToken.None);

        Assert.Equal(["Stage", "Package"], execution.Calls.Select(c => c.Step));
    }

    [Fact]
    public async Task AChangedBuildOfAPublishedVersionReplacesTheApplicationUnderTheOverwritePolicy()
    {
        SeedEarlierRun(publishedTo: "ConfigMgr");
        var execution = new FakeExecution(Path.Combine(_root, "workbench"));
        var item = Item(true, false, false);

        await Runner(execution).RunAsync(item, Publish with { OnExisting = OneClickOnExisting.Overwrite }, CancellationToken.None);

        Assert.Equal(["Stage", "Package"], execution.Calls.Select(c => c.Step));
        Assert.Equal([OnExistingBehavior.Overwrite], execution.OnExisting);
        Assert.Equal("published Widget 2.0", item.ResultConfigMgr);
        Assert.Equal(OneClickOutcomes.Published, item.Outcome);
    }

    [Fact]
    public async Task AChangedBuildOfAPublishedVersionIsSkippedUnderTheSkipPolicyAndTheRecordStays()
    {
        SeedEarlierRun(publishedTo: "ConfigMgr");
        var execution = new FakeExecution(Path.Combine(_root, "workbench"))
        {
            Package = _ => new PackagerExecutionResult
            {
                StdOut = "Packaged: Widget 2.0", Conflict = new ExistingApplicationConflict("Widget 2.0", "2.0", "2.0")
            }
        };
        var item = Item(true, false, false);

        await Runner(execution).RunAsync(item, Publish, CancellationToken.None);

        Assert.Equal(["Stage", "Package"], execution.Calls.Select(c => c.Step));
        Assert.Equal([OnExistingBehavior.Skip], execution.OnExisting);
        Assert.Equal("skipped: application Widget 2.0 already exists", item.ResultConfigMgr);
        Assert.Equal("ConfigMgr: application exists, policy Skip", item.Reason);
        Assert.Equal(OneClickOutcomes.Skipped, item.Outcome);
        Assert.Equal("2.0", Stored().Published["ConfigMgr"].Version);
    }

    private static OneClickPlanItem ItemWithCveCells(out PackagerInfo row)
    {
        row = new PackagerInfo
        {
            Uid = "widget", Application = "Widget", Script = "widget.json", Cpe = "cpe:2.3:a:v:widget:*:*:*:*:*:*:*:*",
            CurrentVersion = "1.0", LatestVersion = "1.5"
        };
        CveRowState.Preload(row, new ApplicationPackagerEngine.Services.Vulnerability.VulnerabilitySummary
        {
            QueriedAt = "2026-10-11T12:00:00Z", From = "1.0", To = "1.5", Source = "NVD", Count = 3, KevCount = 1,
            FixedInLatestCount = 1, FixedEarlierCount = 2, FixedUnknownCount = 0
        });
        Assert.Equal("3", row.CveCount);
        return new OneClickPlanItem
        {
            Key = "widget", Application = "Widget", Row = row, ConfigMgr = true, Include = true, NotReady = new Dictionary<string, string>()
        };
    }

    private static OneClickRunOptions Report => new(OneClickActionNames.Report, false, OneClickOnExisting.Skip, "");

    [Fact]
    public async Task ANewVendorVersionClearsTheCveCellsThatDescribedTheOldOne()
    {
        var execution = new FakeExecution(Path.Combine(_root, "workbench")) { Latest = "2.0" };
        var item = ItemWithCveCells(out var row);

        await Runner(execution).RunAsync(item, Report, CancellationToken.None);

        Assert.Equal("2.0", row.LatestVersion);
        Assert.Equal(("", "", ""), (row.CveCount, row.CveKev, row.CveFixed));
        Assert.Equal("Update available", row.Status);
    }

    [Fact]
    public async Task AnUnchangedVendorVersionKeepsTheCveCellsAndTheCountOnTheStatus()
    {
        var execution = new FakeExecution(Path.Combine(_root, "workbench")) { Latest = "1.5" };
        var item = ItemWithCveCells(out var row);

        await Runner(execution).RunAsync(item, Report, CancellationToken.None);

        Assert.Equal("3", row.CveCount);
        Assert.Equal("Update available (3 CVEs, 1 in KEV)", row.Status);
    }

    [Fact]
    public async Task AVendorVersionOlderThanTheOneLastSeenIsSkippedAndTheNewerVersionStaysOnRecord()
    {
        SeedEarlierRun();
        var execution = new FakeExecution(Path.Combine(_root, "workbench")) { Latest = "1.9" };
        var item = Item(true, true, false);

        await Runner(execution).RunAsync(item, Publish, CancellationToken.None);

        Assert.Empty(execution.Calls);
        Assert.Equal(OneClickOutcomes.Skipped, item.Outcome);
        Assert.Equal("vendor reports 1.9, older than 2.0 seen before", item.Reason);
        Assert.Equal("Vendor older than last seen", item.Row!.Status);
        Assert.Equal("2.0", Stored().LatestVersion);
    }

    [Fact]
    public async Task ForceActsOnAVendorVersionOlderThanTheOneLastSeen()
    {
        SeedEarlierRun();
        var execution = new FakeExecution(Path.Combine(_root, "workbench")) { Latest = "1.9" };
        var item = Item(true, false, false);

        await Runner(execution).RunAsync(item, Publish with { Force = true }, CancellationToken.None);

        Assert.Equal(["Stage", "Package"], execution.Calls.Select(c => c.Step));
        Assert.Equal("1.9", Stored().LatestVersion);
    }

    [Fact]
    public void ThePlanCarriesTheNotReadyDestinationsOfARowThatWaitsForTheCheck()
    {
        var settings = new OneClickPlanSettings
        {
            Tracked = ["widget"],
            DefaultDestinations = new DestinationSet(true, true, false),
            Readiness = new Dictionary<string, OneClickReadiness>
            {
                ["ConfigMgr"] = new(false, "ConfigMgr console not detected"),
                ["WSUS"] = OneClickReadiness.Yes
            }
        };

        var row = Assert.Single(OneClickPlanner.Plan([new OneClickApp("widget", "Widget", "Contoso")], settings,
            new Dictionary<string, OneClickHistoryEntry>(), OneClickActionNames.StageAndPackage, false, DateTime.Now));

        Assert.Equal("ConfigMgr console not detected", row.NotReady["ConfigMgr"]);
        Assert.Equal(["WSUS"], row.PublishTo);
    }
}
