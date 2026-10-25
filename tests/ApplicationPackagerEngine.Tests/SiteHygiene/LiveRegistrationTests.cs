using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>The Live services in the container, and the Live settings in the settings file.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class LiveRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-shliveservices-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public LiveRegistrationTests()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task TheLiveServicesAreRegisteredOnceOverTheOneSessionWithTheDataFolderFiles()
    {
        await using var services = new ServiceCollection().AddPackagerServices().BuildServiceProvider();
        var live = services.GetRequiredService<ISiteHygieneLiveService>();
        Assert.IsType<SiteHygieneLiveService>(live);
        Assert.Same(live, services.GetRequiredService<ISiteHygieneLiveService>());
        var folder = Path.Combine(AppDataPaths.Root, "SiteHygiene");
        Assert.Equal(Path.Combine(folder, "metrics-history.csv"), services.GetRequiredService<IMetricsHistoryStore>().Path);
        Assert.Same(services.GetRequiredService<MetricsHistoryStore>(), services.GetRequiredService<IMetricsHistoryStore>());
        Assert.Equal(Path.Combine(folder, "alerts.log"), services.GetRequiredService<IAlertLog>().Path);
        Assert.Equal(Path.Combine(folder, "Reports"), services.GetRequiredService<ILiveReportWriter>().Folder);
        Assert.Same(services.GetRequiredService<IAlertEvaluator>(), services.GetRequiredService<IAlertEvaluator>());
        Assert.IsType<LegacyDashboardImport>(services.GetRequiredService<ILegacyDashboardImport>());
        Assert.Same(services.GetRequiredService<IConfigMgrSession>(), services.GetRequiredService<IConfigMgrSession>());
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void AnOlderSettingsFileWithoutLiveValuesLoadsTheDefaultsAndTheDiscoverSqlSettings()
    {
        var path = SettingsService.SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"SiteCode\":\"MCM\",\"ProviderMachine\":\"CM01.contoso.com\",\"DiscoverySqlServer\":\" CM01.contoso.com \",\"DiscoverySqlDatabase\":\"CM_MCM\",\"SiteHygiene\":{\"SelectedTab\":3}}");
        var service = new SettingsService();
        var stored = service.Load();
        Assert.Null(stored.SiteHygieneLive);
        Assert.Equal(3, stored.SiteHygiene!.SelectedTab);
        var live = SiteHygieneLivePreferences.ToLiveSettings(stored);
        Assert.Equal(LiveSettings.Default with { SqlServer = "CM01.contoso.com", SqlDatabase = "CM_MCM" }, live);
        Assert.Equal(new LiveSettingValues(SqlServer: "CM01.contoso.com", TrustSqlCertificate: null, RefreshMinutes: null, InactiveDays: null, AlertsEnabled: null, CompliancePctFloor: null),
            SiteHygieneLivePreferences.Values(stored));
    }

    [Fact]
    public void StoredValuesAreClampedAndTheComplianceSwitchTurnsTheFloorOff()
    {
        var settings = new AppSettings
        {
            SiteHygieneLive = new SiteHygieneLiveSettings { RefreshMinutes = 1, InactiveDays = 99_999, AlertCompliance = false, CompliancePctFloor = 70, AlertDpCritical = false, TrustSqlCertificate = true }
        };
        var live = SiteHygieneLivePreferences.ToLiveSettings(settings);
        Assert.Equal((LiveSettings.MinRefreshMinutes, LiveSettings.MaxInactiveDays, true), (live.RefreshMinutes, live.InactiveDays, live.TrustSqlCertificate));
        Assert.Equal(new AlertSettings(Enabled: true, SiteCritical: true, DpCritical: false, ContentFailed: true, CompliancePctFloor: null), live.Alerts);
        settings.SiteHygieneLive.AlertCompliance = null;
        Assert.Equal(70, SiteHygieneLivePreferences.ToLiveSettings(settings).Alerts.CompliancePctFloor);
    }

    [Fact]
    public void AFillFromTheImportSetsOnlyEmptyValuesAndKeepsThePageChoices()
    {
        var service = new SettingsService();
        var stored = service.Load();
        stored.SiteHygiene = new SiteHygieneSettings { SelectedTab = 2 };
        stored.SiteHygieneLive = new SiteHygieneLiveSettings { RefreshMinutes = 60 };
        service.Save(stored);

        Assert.True(SiteHygieneLivePreferences.Fill(service, new LiveSettingValues(SqlServer: "SQL01", TrustSqlCertificate: true, RefreshMinutes: 15, InactiveDays: 30, AlertsEnabled: false, CompliancePctFloor: 90)));
        var after = new SettingsService().Load();
        Assert.Equal("SQL01", after.DiscoverySqlServer);
        Assert.Equal((true, 60, 30, false, 90), (after.SiteHygieneLive!.TrustSqlCertificate!.Value, after.SiteHygieneLive.RefreshMinutes!.Value, after.SiteHygieneLive.InactiveDays!.Value,
            after.SiteHygieneLive.AlertsEnabled!.Value, after.SiteHygieneLive.CompliancePctFloor!.Value));
        Assert.Equal(2, after.SiteHygiene!.SelectedTab);

        Assert.True(SiteHygieneLivePreferences.Fill(service, new LiveSettingValues(SqlServer: "OTHER", TrustSqlCertificate: false, RefreshMinutes: 5, InactiveDays: 5, AlertsEnabled: true, CompliancePctFloor: 10)));
        var again = new SettingsService().Load();
        Assert.Equal("SQL01", again.DiscoverySqlServer);
        Assert.Equal((60, 30, 90), (again.SiteHygieneLive!.RefreshMinutes!.Value, again.SiteHygieneLive.InactiveDays!.Value, again.SiteHygieneLive.CompliancePctFloor!.Value));
    }
}
