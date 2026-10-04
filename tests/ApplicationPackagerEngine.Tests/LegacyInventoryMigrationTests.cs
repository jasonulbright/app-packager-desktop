using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class LegacyInventoryMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-inventory-migration-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved = [];

    public LegacyInventoryMigrationTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, SettingsService.LauncherSiteCodeVariable, SettingsService.LauncherProviderVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, null);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void SettingsStateMovesToInventoryBeforeTheJsonCopyIsCleared()
    {
        var settingsService = new SettingsService();
        var settings = settingsService.Load();
        var checkedAt = new DateTime(2026, 10, 4, 8, 0, 0, DateTimeKind.Utc);
        var publishedAt = DateTimeOffset.Parse("2026-10-04T08:10:00Z");
        var state = new AppCheckState
        {
            LastChecked = checkedAt,
            LatestVersion = "4.1",
            CurrentVersion = "4.0",
            LastStaged = checkedAt.AddMinutes(5)
        };
        OneClickHistory.SetPublished(state.Published, state.NotSupported, "ConfigMgr", "4.0", "cm-app-5", publishedAt);
        OneClickHistory.SetNotSupported(state.NotSupported, "WSUS", "4.1", "not supported: CustomInstall", publishedAt.AddMinutes(1));
        settings.AppStates["legacy-app"] = state;
        settingsService.SaveOperatorEdit(settings);

        var inventory = new ApplicationInventoryService();
        var migration = new LegacyInventoryMigration(settingsService, inventory);
        Assert.True(migration.Run());

        var reloaded = new SettingsService().Load();
        Assert.Empty(reloaded.AppStates);
        Assert.False(migration.Run());
        Assert.Empty(new SettingsService().Load().AppStates);

        var vendor = Assert.IsType<InventoryVendorState>(inventory.GetVendorState("legacy-app"));
        Assert.Equal("4.1", vendor.LatestVersion);
        var oldEnvironment = Assert.IsType<InventoryEnvironmentState>(inventory.GetEnvironmentState(
            "legacy-app", InventoryEnvironmentIdentity.Unknown("ConfigMgr")));
        Assert.Equal("4.0", oldEnvironment.LatestObservedVersion);
        var published = Assert.IsType<InventoryDestinationState>(inventory.GetDestinationState(
            "legacy-app", InventoryEnvironmentIdentity.Unknown("ConfigMgr"), "Publish"));
        Assert.Equal("cm-app-5", published.LastSuccessObjectId);
        var refused = Assert.Single(inventory.GetEvents("legacy-app", 20), item =>
            item.Kind == "LegacyDestinationOutcome" && item.Destination == "WSUS" && item.Outcome == InventoryOutcome.Refused);
        Assert.Equal("4.1", refused.Version);
        Assert.Equal("unknown", refused.EnvironmentKey);
        var lastRefusal = Assert.IsType<InventoryRefusalState>(inventory.GetLatestRefusal(
            "legacy-app", InventoryEnvironmentIdentity.Unknown("WSUS"), "Publish"));
        Assert.Equal("4.1", lastRefusal.Version);
        Assert.Equal("not supported: CustomInstall", lastRefusal.Detail);
    }
}
