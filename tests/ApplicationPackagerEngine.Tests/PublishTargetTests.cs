using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// What a publish packages and where: only the stage of the latest version, a profiled
/// build only while its record is current, the button's target for a dropped installer,
/// and the stored target recorded by a grid Stage's snapshot and PS1 bridge.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PublishTargetTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();
    private readonly Dictionary<string, string?> _saved = [];
    private readonly string _downloadRoot;

    public PublishTargetTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, WorkbenchPaths.DataRootVariable,
                     SettingsService.LauncherSiteCodeVariable, SettingsService.LauncherProviderVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _t.NewFolder("appdata"));
        Environment.SetEnvironmentVariable(WorkbenchPaths.DataRootVariable, _t.Store.DataRoot);
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, null);
        _downloadRoot = _t.NewFolder("downloads");
        StoreTarget(DeploymentTargets.Mecm);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        _t.Dispose();
    }

    private void StoreTarget(string target)
    {
        var settings = new SettingsService();
        var s = settings.Load();
        s.DownloadRoot = _downloadRoot;
        s.Intune.DeploymentTarget = target;
        settings.SaveOperatorEdit(s);
    }

    private static PackagerExecutionService Service(ApplicationInventoryService? inventory = null)
    {
        var settings = new SettingsService();
        return new PackagerExecutionService(settings, ProfileStageFixture.Service(),
            new IntuneDeploymentService(settings), new DeploymentConditionsService(settings), inventory);
    }

    private PackagerInfo Row(LoopbackFileServer server)
    {
        var path = Path.Combine(_t.NewFolder("defs"), "widget.json");
        File.WriteAllText(path, JsonSerializer.Serialize(ProfileStageFixture.Definition(server)));
        return new PackagerInfo { Uid = "widget", Application = "Widget", IsJsonDefined = true, DefinitionPath = path, LatestVersion = "1.2.3" };
    }

    [Fact]
    public async Task AnOlderStagedVersionIsNeitherReusedNorPublishedAsTheLatest()
    {
        using var server = ProfileStageFixture.Server();
        var row = Row(server);
        var service = Service();
        Assert.Equal(0, (await service.StageAsync(row)).ExitCode);
        Assert.NotNull(service.ExistingStageManifest(row));

        row.LatestVersion = "1.3.0";

        Assert.Null(service.ExistingStageManifest(row));
        var publish = await service.PackageAsync(row, "", targetOverride: PublishRunTargets.IntuneOnly);
        Assert.Equal(1, publish.ExitCode);
        Assert.Contains("Run Stage for Widget 1.3.0 first", publish.StdErr);
    }

    [Fact]
    public async Task AProfiledBuildIsReusedOnlyWhileItsBuildRecordIsCurrent()
    {
        using var server = ProfileStageFixture.Server();
        var profile = _t.NewProfile(ProfileStageFixture.ApplicationId, "Pilot Ring");
        var row = Row(server);
        var service = Service();
        var stage = await service.StageAsync(row);
        Assert.Equal(0, stage.ExitCode);

        Assert.Equal(stage.OutLogPath, service.ExistingStageManifest(row));

        var edit = _t.Store.GetProfile(ProfileStageFixture.ApplicationId, profile.ProfileId).Clone();
        edit.Set("Install.Command", JsonValue.Create("changed.cmd"));
        _t.Store.SaveProfile(edit);

        Assert.Null(service.ExistingStageManifest(row));
    }

    [Fact]
    public async Task SharedExecutionRecordsTheSealedBuildAndAnIntuneRefusalForItsTenant()
    {
        var profile = _t.NewProfile(ProfileStageFixture.ApplicationId, "Pilot Ring");
        using var server = ProfileStageFixture.Server();
        var inventory = new ApplicationInventoryService();
        var row = Row(server);
        var service = Service(inventory);

        var stage = await service.StageAsync(row);
        Assert.Equal(0, stage.ExitCode);
        var manifest = new ManifestService().Read(stage.OutLogPath!);
        var reopenedInventory = new ApplicationInventoryService();
        var package = Assert.IsType<InventoryPackageState>(reopenedInventory.GetPackageState(ProfileStageFixture.ApplicationId, profile.ProfileId));
        Assert.Equal(manifest.BuildId, package.BuildId);
        Assert.Equal(stage.OutLogPath, package.ManifestPath);
        Assert.True(File.Exists(package.BuildRecordPath));

        var settings = new SettingsService();
        var appSettings = settings.Load();
        appSettings.Intune.TenantId = "tenant-a";
        settings.SaveOperatorEdit(appSettings);
        var intuneEnvironment = InventoryEnvironmentIdentity.Intune("tenant-a");
        inventory.RecordDestinationOutcome(new InventoryDestinationOutcome(row.Uid, null, intuneEnvironment,
            "Publish", "1.0", InventoryOutcome.Succeeded, "old-app", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var publish = await service.PackageAsync(row, "", targetOverride: PublishRunTargets.IntuneOnly);

        Assert.NotEqual(0, publish.ExitCode);
        var destination = Assert.IsType<InventoryDestinationState>(reopenedInventory.GetDestinationState(
            row.Uid, intuneEnvironment, "Publish"));
        Assert.Equal(InventoryOutcome.Refused, destination.LastAttemptOutcome);
        Assert.Equal("1.0", destination.LastSuccessVersion);
        Assert.Contains(reopenedInventory.GetEvents(row.Uid, 20), item =>
            item.Kind == "Publish" && item.Outcome == InventoryOutcome.Refused && item.BuildId == manifest.BuildId);

        appSettings.Intune.TenantId = "tenant-b";
        settings.SaveOperatorEdit(appSettings);
        var secondPublish = await service.PackageAsync(row, "", targetOverride: PublishRunTargets.IntuneOnly);

        Assert.NotEqual(0, secondPublish.ExitCode);
        var afterTenantB = new ApplicationInventoryService();
        var tenantBDestination = Assert.IsType<InventoryDestinationState>(afterTenantB.GetDestinationState(
            row.Uid, InventoryEnvironmentIdentity.Intune("tenant-b"), "Publish"));
        var tenantADestination = Assert.IsType<InventoryDestinationState>(afterTenantB.GetDestinationState(
            row.Uid, intuneEnvironment, "Publish"));
        Assert.Equal(InventoryOutcome.Refused, tenantBDestination.LastAttemptOutcome);
        Assert.Null(tenantBDestination.LastSuccessVersion);
        Assert.Equal("1.0", tenantADestination.LastSuccessVersion);
        Assert.Equal(InventoryOutcome.Refused, tenantADestination.LastAttemptOutcome);

        var tenantBProjection = new ApplicationInventoryProjection(afterTenantB).Entry(row, appSettings);
        Assert.DoesNotContain("Intune", tenantBProjection.Published.Keys);
        Assert.Contains("Intune", tenantBProjection.NotSupported.Keys);
    }

    [Fact]
    public async Task ACancelledSharedStageIsRetainedAsACancelledOperation()
    {
        using var server = ProfileStageFixture.Server();
        var row = Row(server);
        var inventory = new ApplicationInventoryService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(inventory).StageAsync(row, ct: cancellation.Token));

        var operation = Assert.Single(new ApplicationInventoryService().GetOperations(row.Uid));
        Assert.Equal("Stage", operation.Kind);
        Assert.Equal(InventoryOutcome.Cancelled, operation.Outcome);
        Assert.Equal("Stage cancelled.", operation.Detail);
        Assert.NotNull(operation.FinishedAt);
    }

    [Fact]
    public async Task ACancelledSharedPackageRetainsItsOperationAndDestinationAttempt()
    {
        var settings = new SettingsService();
        var appSettings = settings.Load();
        appSettings.Intune.TenantId = "cancel-tenant";
        settings.SaveOperatorEdit(appSettings);
        var inventory = new ApplicationInventoryService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(inventory).PackageAsync(new PackagerInfo { Uid = "cancel-package", Application = "Widget" }, "",
                ct: cancellation.Token, targetOverride: PublishRunTargets.IntuneOnly));

        var reopenedInventory = new ApplicationInventoryService();
        var operation = Assert.Single(reopenedInventory.GetOperations("cancel-package"));
        Assert.Equal(InventoryOutcome.Cancelled, operation.Outcome);
        Assert.Equal("Package cancelled.", operation.Detail);
        var destination = Assert.IsType<InventoryDestinationState>(reopenedInventory.GetDestinationState(
            "cancel-package", InventoryEnvironmentIdentity.Intune("cancel-tenant"), "Publish"));
        Assert.Equal(InventoryOutcome.Cancelled, destination.LastAttemptOutcome);
        Assert.Null(destination.LastSuccessVersion);
    }

    [Fact]
    public async Task ADroppedInstallerRowPublishesToTheButtonTargetAndNotTheStoredOne()
    {
        using var server = ProfileStageFixture.Server();
        var stage = await Service().StageAsync(Row(server));
        var manifestPath = stage.OutLogPath!;
        var content = Path.GetDirectoryName(manifestPath)!;
        var manifest = new ManifestService().Read(manifestPath);
        var intunewin = IntuneWinArtifact.PathFor(manifest, content);
        File.WriteAllText(intunewin, "x");
        File.SetLastWriteTimeUtc(manifestPath, DateTime.UtcNow.AddMinutes(-5));
        var dropped = new PackagerInfo { Uid = "drop", Application = "Widget", StagedManifestPath = manifestPath };

        var result = await Service().PackageAsync(dropped, "", targetOverride: PublishRunTargets.IntuneOnly);

        Assert.StartsWith("Staged content ready", result.StdOut);
        Assert.Contains("credentials not configured", result.IntunePublishNote);

        var direct = await Service().PackageStagedManifestAsync(manifestPath, "", target: PublishRunTargets.IntuneOnly);
        Assert.StartsWith("Staged content ready", direct.StdOut);

        var wsus = await Service().PackageAsync(dropped, "", targetOverride: PublishRunTargets.WsusOnly);
        Assert.Equal(1, wsus.ExitCode);
        Assert.Contains("Publish to WSUS", wsus.StdErr);
    }

    [Fact]
    public async Task AGridStageRecordsTheStoredWsusTargetAndARunTargetOverridesIt()
    {
        StoreTarget(DeploymentTargets.WsusOnly);
        using var server = ProfileStageFixture.Server();
        _t.NewProfile(ProfileStageFixture.ApplicationId, "Pilot Ring");
        var service = Service();

        var grid = SnapshotOf(await service.StageAsync(Row(server)));
        var publishStage = SnapshotOf(await service.StageAsync(Row(server), runTarget: PublishRunTargets.IntuneOnly));

        Assert.Equal(DeploymentTargets.WsusOnly, grid.Target);
        Assert.Equal(DeploymentTargets.IntuneOnly, publishStage.Target);
    }

    [Fact]
    public async Task APs1PackagerBridgeRecordsTheStoredWsusTarget()
    {
        StoreTarget(DeploymentTargets.MecmAndWsus);
        var script = Path.Combine(_t.NewFolder("packagers"), "package-widgetps.ps1");
        File.WriteAllText(script, "exit 0");
        var row = new PackagerInfo { Uid = "widgetps", Application = "Widget PS", Script = "package-widgetps.ps1", FullPath = script };

        await Service().StageAsync(row);

        var snapshot = Assert.Single(Directory.GetFiles(_t.Store.RunsRoot, "*.json"));
        Assert.Equal(DeploymentTargets.MecmAndWsus, (string?)JsonNode.Parse(File.ReadAllText(snapshot))!["Target"]);
    }

    private RunSnapshot SnapshotOf(PackagerExecutionResult stage)
    {
        var buildId = new ManifestService().Read(stage.OutLogPath!).BuildId;
        return WorkbenchStore.ReadRunSnapshot(Path.Combine(_t.Store.RunsRoot, buildId + ".json"))!;
    }
}
