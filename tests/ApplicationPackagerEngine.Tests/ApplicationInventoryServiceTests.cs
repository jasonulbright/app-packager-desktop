using System.Data.SQLite;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

public sealed class ApplicationInventoryServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-inventory-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly ApplicationInventoryService _inventory;

    public ApplicationInventoryServiceTests()
    {
        Directory.CreateDirectory(_folder);
        _dbPath = Path.Combine(_folder, "history.db");
        _inventory = new ApplicationInventoryService(_dbPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void FailedVendorCheckKeepsTheLastSuccessfulVersionAndRecordsTheAttempt()
    {
        var first = DateTimeOffset.Parse("2026-10-04T10:00:00Z");
        var second = first.AddMinutes(3);
        _inventory.RecordVendorCheck(new InventoryVendorCheck("vendor:app", "App", "2.1", "GitHubRelease", InventoryOutcome.Succeeded, first));
        _inventory.RecordVendorCheck(new InventoryVendorCheck("vendor:app", "App", null, "GitHubRelease", InventoryOutcome.Failed, second, Detail: "rate limited"));

        var state = Assert.IsType<InventoryVendorState>(_inventory.GetVendorState("vendor:app"));
        Assert.Equal("2.1", state.LatestVersion);
        Assert.Equal("GitHubRelease", state.LatestSource);
        Assert.Equal(first, state.LastSucceededAt);
        Assert.Equal(second, state.LastAttemptAt);
        Assert.Equal(InventoryOutcome.Failed, state.LastAttemptOutcome);
        Assert.Equal("rate limited", state.LastAttemptDetail);
    }

    [Fact]
    public void AnOlderDatabaseGetsTheConfigMgrIdentityColumnsAndKeepsItsRecords()
    {
        var path = Path.Combine(_folder, "older.db");
        using (var connection = new SQLiteConnection($"Data Source={path};Version=3;Pooling=False;"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE destination_inventory (packager_uid TEXT NOT NULL, destination TEXT NOT NULL, environment_key TEXT NOT NULL, action TEXT NOT NULL,
                    application_id TEXT, profile_id TEXT, last_attempt_version TEXT, last_attempt_outcome TEXT NOT NULL, last_attempt_object_id TEXT, last_attempt_at TEXT,
                    last_success_version TEXT, last_success_object_id TEXT, last_success_at TEXT, last_success_build_id TEXT, last_attempt_detail TEXT,
                    PRIMARY KEY(packager_uid, destination, environment_key, action));
                INSERT INTO destination_inventory VALUES('app', 'ConfigMgr', 'MCM|CM01', 'Publish', NULL, NULL, '1.0', 'Succeeded', 'Tool', '2026-10-01T00:00:00.0000000+00:00',
                    '1.0', 'Tool', '2026-10-01T00:00:00.0000000+00:00', 'b1', NULL);
                """;
            create.ExecuteNonQuery();
        }

        var inventory = new ApplicationInventoryService(path);
        _ = new ApplicationInventoryService(path);

        var env = new InventoryEnvironmentIdentity("ConfigMgr", "MCM|CM01");
        var row = Assert.IsType<InventoryDestinationState>(inventory.GetDestinationState("app", env, "Publish"));
        Assert.Equal("Tool", row.LastSuccessObjectId);
        Assert.Null(row.ConfigMgrCiId);
        Assert.Null(row.ConfigMgrModelName);
        Assert.True(inventory.LinkConfigMgrApplication("app", env, "Publish", "Tool", "16780662", "ScopeId_X/Application_Y"));
        Assert.False(inventory.LinkConfigMgrApplication("app", env, "Publish", "Tool", "16780665", "ScopeId_X/Application_Z"));
        Assert.Equal(("16780662", "ScopeId_X/Application_Y"), (inventory.GetDestinationState("app", env, "Publish")!.ConfigMgrCiId, inventory.GetDestinationState("app", env, "Publish")!.ConfigMgrModelName));
    }

    [Fact]
    public void APublishRecordsTheCiIdAndARepublishUnderAnotherNameDropsTheOldLink()
    {
        var env = new InventoryEnvironmentIdentity("ConfigMgr", "MCM|CM01");
        var at = DateTimeOffset.Parse("2026-10-04T10:00:00Z");
        _inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("app", null, env, "Publish", "1.0", InventoryOutcome.Succeeded, "Tool", at, ConfigMgrCiId: "16780662"));
        _inventory.LinkConfigMgrApplication("app", env, "Publish", "Tool", "16780662", "M1");

        _inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("app", null, env, "Publish", "1.0", InventoryOutcome.Failed, "Tool", at.AddMinutes(1), ConfigMgrCiId: "999"));
        Assert.Equal(("16780662", "M1"), Pair());
        _inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("app", null, env, "Publish", "1.1", InventoryOutcome.Succeeded, "Tool", at.AddMinutes(2)));
        Assert.Equal(("16780662", "M1"), Pair());
        _inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("app", null, env, "Publish", "1.2", InventoryOutcome.Succeeded, "Tool", at.AddMinutes(3), ConfigMgrCiId: "16780700"));
        Assert.Equal(("16780700", (string?)null), Pair());
        _inventory.LinkConfigMgrApplication("app", env, "Publish", "Tool", "16780700", "M1");
        _inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("app", null, env, "Publish", "2.0", InventoryOutcome.Succeeded, "Tool 2", at.AddMinutes(4)));
        Assert.Equal(((string?)null, (string?)null), Pair());

        (string?, string?) Pair()
        {
            var row = _inventory.GetDestinationState("app", env, "Publish")!;
            return (row.ConfigMgrCiId, row.ConfigMgrModelName);
        }
    }

    [Fact]
    public void LateCompletionsDoNotReplaceNewerFacts()
    {
        var older = DateTimeOffset.Parse("2026-10-04T10:00:00Z");
        var newer = older.AddMinutes(1);
        _inventory.RecordVendorCheck(new InventoryVendorCheck("app", "App", "2.0", "source", InventoryOutcome.Succeeded, newer));
        _inventory.RecordVendorCheck(new InventoryVendorCheck("app", "App", "1.0", "source", InventoryOutcome.Succeeded, older));
        var vendor = Assert.IsType<InventoryVendorState>(_inventory.GetVendorState("app"));
        Assert.Equal("2.0", vendor.LatestVersion);
        Assert.Equal(newer, vendor.LastAttemptAt);

        var env = new InventoryEnvironmentIdentity("Intune", "tenant-a");
        _inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("app", null, env, "Publish", "2.0",
            InventoryOutcome.Succeeded, "new-object", newer));
        _inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("app", null, env, "Publish", "1.0",
            InventoryOutcome.Failed, null, older));
        var destination = Assert.IsType<InventoryDestinationState>(_inventory.GetDestinationState("app", env, "Publish"));
        Assert.Equal("2.0", destination.LastAttemptVersion);
        Assert.Equal("2.0", destination.LastSuccessVersion);
    }

    [Fact]
    public void EnvironmentObservationsAndDestinationResultsAreScopedToTheirEnvironment()
    {
        var identity = new InventoryApplicationIdentity("app", "catalog:app", "default", 3);
        var siteA = new InventoryEnvironmentIdentity("ConfigMgr", "ABC|CM01");
        var siteB = new InventoryEnvironmentIdentity("ConfigMgr", "DEF|CM02");
        var now = DateTimeOffset.Parse("2026-10-04T10:00:00Z");

        _inventory.RecordEnvironmentObservation(new InventoryEnvironmentObservation("app", identity, siteA, "1.0", "ConfigMgr", InventoryOutcome.Succeeded, now));
        _inventory.RecordEnvironmentObservation(new InventoryEnvironmentObservation("app", identity, siteA, null, "ConfigMgr", InventoryOutcome.Failed, now.AddMinutes(1), Detail: "offline"));
        _inventory.RecordEnvironmentObservation(new InventoryEnvironmentObservation("app", identity, siteB, null, "ConfigMgr", InventoryOutcome.NotFound, now.AddMinutes(2)));

        var observedA = Assert.IsType<InventoryEnvironmentState>(_inventory.GetEnvironmentState("app", siteA));
        var observedB = Assert.IsType<InventoryEnvironmentState>(_inventory.GetEnvironmentState("app", siteB));
        Assert.Equal("1.0", observedA.LatestObservedVersion);
        Assert.Equal(InventoryOutcome.Failed, observedA.LastAttemptOutcome);
        Assert.Null(observedB.LatestObservedVersion);
        Assert.Equal(InventoryOutcome.NotFound, observedB.LastAttemptOutcome);

        _inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("app", identity, siteA, "Publish", "2.0",
            InventoryOutcome.Succeeded, "cm-app-7", now, "build-1"));
        _inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("app", identity, siteA, "Publish", "3.0",
            InventoryOutcome.Failed, null, now.AddMinutes(3), "build-2", Detail: "content share unavailable"));
        _inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("app", identity, siteB, "Publish", "2.0",
            InventoryOutcome.AlreadyPresent, "cm-app-8", now.AddMinutes(4), "build-1"));

        var publishedA = Assert.IsType<InventoryDestinationState>(_inventory.GetDestinationState("app", siteA, "Publish"));
        var publishedB = Assert.IsType<InventoryDestinationState>(_inventory.GetDestinationState("app", siteB, "Publish"));
        Assert.Equal("3.0", publishedA.LastAttemptVersion);
        Assert.Equal(InventoryOutcome.Failed, publishedA.LastAttemptOutcome);
        Assert.Equal("2.0", publishedA.LastSuccessVersion);
        Assert.Equal("cm-app-7", publishedA.LastSuccessObjectId);
        Assert.Equal("2.0", publishedB.LastSuccessVersion);
        Assert.Equal("cm-app-8", publishedB.LastSuccessObjectId);
    }

    [Fact]
    public void EnvironmentFactoriesProduceStableDestinationKeys()
    {
        Assert.Equal(InventoryEnvironmentIdentity.ConfigMgr(" abc ", "cm01"), InventoryEnvironmentIdentity.ConfigMgr("ABC", "CM01 "));
        Assert.Equal(InventoryEnvironmentIdentity.Intune("tenant-a"), InventoryEnvironmentIdentity.Intune(" TENANT-A "));
        Assert.NotEqual(InventoryEnvironmentIdentity.Wsus(new() { ServerName = "wsus01", PortNumber = 8530 }),
            InventoryEnvironmentIdentity.Wsus(new() { ServerName = "WSUS01", PortNumber = 8531, UseSsl = true }));
        Assert.Equal("unknown", InventoryEnvironmentIdentity.Intune("").Key);
    }

    [Fact]
    public void OneClickProjectionReadsOnlyTheConfiguredEnvironmentFacts()
    {
        var at = DateTimeOffset.Parse("2026-10-04T10:00:00Z");
        var row = new PackagerInfo { Uid = "app", Application = "App", IsJsonDefined = true };
        var settings = new AppSettings
        {
            SiteCode = "ABC",
            ProviderMachine = "CM01",
            Intune = new IntuneSettings { TenantId = "tenant-a" },
            Wsus = new WsusSettings { ServerName = "wsus01", PortNumber = 8530 }
        };
        var identity = new InventoryApplicationIdentity("app", "catalog:app", "default", 2);
        var cfg = InventoryEnvironmentIdentity.ConfigMgr(settings.SiteCode, settings.ProviderMachine);
        _inventory.RecordVendorCheck(new InventoryVendorCheck("app", "App", "2.0", "feed", InventoryOutcome.Succeeded, at));
        _inventory.RecordStagedBuild(new InventoryStagedBuild(identity, "2.0", "build-2", "build.json", "manifest.json", at));
        _inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("app", identity with { ProfileId = null }, cfg,
            "Publish", "1.9", InventoryOutcome.Succeeded, "cm-app", at));
        _inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("app", identity with { ProfileId = null },
            InventoryEnvironmentIdentity.Unknown("WSUS"), "Publish", "2.0", InventoryOutcome.Refused, null, at,
            Detail: "legacy refusal with unknown server"));

        var entry = new ApplicationInventoryProjection(_inventory).Entry(row, settings,
            new PackagerExecutionService.OneClickBuildPlan("root", "catalog:app", "default", 2));

        Assert.Equal("2.0", entry.LastKnownVersion);
        Assert.Equal(at.LocalDateTime, entry.LastChecked);
        Assert.Equal(at.LocalDateTime, entry.LastStaged);
        Assert.Equal("1.9", entry.PublishedVersion(OneClickDestinations.ConfigMgr));
        Assert.Empty(entry.NotSupported);
    }

    [Fact]
    public void StagedBuildInventoryReferencesTheExactBuildAndManifestPerProfile()
    {
        var now = DateTimeOffset.Parse("2026-10-04T11:00:00Z");
        _inventory.RecordStagedBuild(new InventoryStagedBuild(
            new InventoryApplicationIdentity("app", "catalog:app", "default", 4), "5.2", "build-a",
            @"C:\Workbench\build.json", @"C:\Stage\stage-manifest.json", now));
        _inventory.RecordStagedBuild(new InventoryStagedBuild(
            new InventoryApplicationIdentity("app", "catalog:app", "pilot", 8), "5.2-pilot", "build-b",
            @"C:\Workbench\pilot\build.json", @"C:\Stage\pilot\stage-manifest.json", now.AddMinutes(1)));

        var production = Assert.IsType<InventoryPackageState>(_inventory.GetPackageState("catalog:app", "default"));
        var pilot = Assert.IsType<InventoryPackageState>(_inventory.GetPackageState("catalog:app", "pilot"));
        Assert.Equal(("5.2", "build-a", @"C:\Stage\stage-manifest.json"), (production.Version, production.BuildId, production.ManifestPath));
        Assert.Equal(("5.2-pilot", "build-b", @"C:\Stage\pilot\stage-manifest.json"), (pilot.Version, pilot.BuildId, pilot.ManifestPath));
    }

    [Fact]
    public void OperationAndEventsSupportRestartRecoveryAndReports()
    {
        var at = DateTimeOffset.Parse("2026-10-04T12:00:00Z");
        var identity = new InventoryApplicationIdentity("app", "catalog:app", "default", 2);
        const string operationId = "run-123";
        _inventory.BeginOperation(new InventoryOperation(operationId, "Publish", "app", identity, "ConfigMgr", at));
        Assert.Equal(operationId, Assert.Single(_inventory.GetUnfinishedOperations()).OperationId);

        _inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("app", identity,
            InventoryEnvironmentIdentity.ConfigMgr("ABC", "CM01"), "Publish", "3.0", InventoryOutcome.Succeeded,
            "cm-app-id", at.AddMinutes(1), "build-8", operationId));
        var runEvents = Assert.Single(_inventory.GetEventsForOperation(operationId));
        Assert.Equal(("3.0", "build-8", "cm-app-id"), (runEvents.Version, runEvents.BuildId, runEvents.ObjectId));
        Assert.Equal(operationId, Assert.Single(_inventory.GetEvents("app")).OperationId);

        _inventory.CompleteOperation(operationId, InventoryOutcome.Succeeded, at.AddMinutes(2));
        Assert.Empty(_inventory.GetUnfinishedOperations());
        Assert.Equal(InventoryOutcome.Succeeded, _inventory.GetOperation(operationId)?.Outcome);
        Assert.Equal(operationId, Assert.Single(_inventory.GetOperations("app")).OperationId);
    }

    [Fact]
    public void LegacyImportIsAtomicRepeatableAndMarksUnknownEnvironmentOwnership()
    {
        var checkedAt = DateTimeOffset.Parse("2026-10-04T09:00:00Z");
        var publishedAt = checkedAt.AddMinutes(10);
        using (var history = new VersionHistoryService(_dbPath))
            history.Record(new HistoryEntry { AppId = "legacy", AppName = "Legacy", Version = "4.0", Operation = "Stage", Status = "Success" });

        var legacy = new LegacyInventoryAppState("legacy", "4.1", checkedAt, "4.0", checkedAt.AddMinutes(5),
        [
            new("WSUS", "4.0", "wsus-guid", publishedAt, InventoryOutcome.Succeeded),
            new("WSUS", "4.1", null, publishedAt.AddMinutes(1), InventoryOutcome.Refused, "not supported")
        ]);
        Assert.True(_inventory.ImportLegacyAppStates([legacy], publishedAt.AddMinutes(2)));

        _inventory.RecordVendorCheck(new InventoryVendorCheck("legacy", "Legacy", "4.2", "definition", InventoryOutcome.Succeeded, publishedAt.AddMinutes(3)));
        Assert.False(_inventory.ImportLegacyAppStates([legacy], publishedAt.AddMinutes(4)));

        var vendor = Assert.IsType<InventoryVendorState>(_inventory.GetVendorState("legacy"));
        Assert.Equal("4.2", vendor.LatestVersion);
        var environment = Assert.IsType<InventoryEnvironmentState>(_inventory.GetEnvironmentState("legacy", InventoryEnvironmentIdentity.Unknown("ConfigMgr")));
        Assert.Equal("4.0", environment.LatestObservedVersion);
        Assert.Equal(InventoryOutcome.LegacyImported, environment.LastAttemptOutcome);
        var destination = Assert.IsType<InventoryDestinationState>(_inventory.GetDestinationState("legacy", InventoryEnvironmentIdentity.Unknown("WSUS"), "Publish"));
        Assert.Equal("4.0", destination.LastSuccessVersion);
        Assert.Equal("wsus-guid", destination.LastSuccessObjectId);

        var importedEvents = _inventory.GetEvents("legacy", 20);
        Assert.Equal(2, importedEvents.Count(item => item.Kind == "LegacyDestinationOutcome"));
        Assert.Single(importedEvents, item => item.Kind == "LegacyStage" && item.BuildId is null);

        using var preservedHistory = new VersionHistoryService(_dbPath);
        Assert.Single(preservedHistory.GetHistory("legacy"));
    }

    [Fact]
    public async Task IndependentWritersSerializeWithoutDroppingEvents()
    {
        const int writers = 4;
        const int writesPerWriter = 15;
        var services = Enumerable.Range(0, writers).Select(_ => new ApplicationInventoryService(_dbPath)).ToArray();
        var writes = services.Select((service, writer) => Task.Run(() =>
        {
            for (var index = 0; index < writesPerWriter; index++)
            {
                service.RecordVendorCheck(new InventoryVendorCheck("shared", "Shared", $"{writer}.{index}", "test",
                    InventoryOutcome.Succeeded, DateTimeOffset.UtcNow.AddTicks(writer * writesPerWriter + index)));
            }
        }));

        await Task.WhenAll(writes);

        using var connection = new SQLiteConnection($"Data Source={_dbPath};Version=3;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM inventory_events WHERE packager_uid = 'shared' AND kind = 'VendorCheck'";
        Assert.Equal(writers * writesPerWriter, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public async Task ConcurrentLegacyImportHasOneWinner()
    {
        var other = new ApplicationInventoryService(_dbPath);
        var states = new[] { new LegacyInventoryAppState("legacy", "1.0", DateTimeOffset.UtcNow, "", null, []) };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var imports = new[] { _inventory, other }.Select(service => Task.Run(() =>
        {
            started.Task.GetAwaiter().GetResult();
            return service.ImportLegacyAppStates(states, DateTimeOffset.UtcNow);
        })).ToArray();
        started.SetResult();

        var results = await Task.WhenAll(imports);
        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);
        Assert.Single(_inventory.GetEvents("legacy", 10), item => item.Kind == "LegacyVendorState");
    }
}
