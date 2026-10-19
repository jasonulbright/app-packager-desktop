using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// The services a Stage and a publish run need, without any window, page or dialog
/// type: the application host and the command-line build register the same set.
/// </summary>
public static class PackagerServiceRegistration
{
    public static IServiceCollection AddPackagerServices(this IServiceCollection services)
    {
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ISettingsReader<AppSettings>>(sp => sp.GetRequiredService<SettingsService>());
        services.AddSingleton<IRunSettingsReader<AppSettings>>(sp => sp.GetRequiredService<SettingsService>());
        services.AddSingleton<ApplicationPackagerEngine.Services.ApplicationInventoryService>();
        services.AddSingleton<LegacyInventoryMigration>();
        services.AddSingleton<ApplicationInventoryProjection>();
        services.AddSingleton<PackagerDiscoveryService>();
        services.AddSingleton<PackagerExecutionService>();
        services.AddSingleton<MecmQueryService>();
        services.AddSingleton<IDiscoveryService, ApplicationPackagerEngine.Services.DiscoveryService>();
        services.AddSingleton<PackagerPreferencesService>();
        services.AddSingleton<IntuneDeploymentService>();
        services.AddSingleton<DeploymentConditionsService>();
        services.AddSingleton<WorkbenchApplicationCatalog>();
        services.AddSingleton<ApplicationPackagerEngine.Services.Wsus.IWsusAdminClient>(_ => new ApplicationPackagerEngine.Services.Wsus.WsusAdminClient());
        services.AddSingleton<ApplicationPackagerEngine.Services.Wsus.IWsusPublisher>(_ => new ApplicationPackagerEngine.Services.Wsus.WsusPublishService());
        services.AddSingleton<PublishRunService>();

        services.AddSingleton<ApplicationPackagerEngine.Scrapers.ScraperRegistry>();
        services.AddSingleton<ApplicationPackagerEngine.Services.DownloadService>();
        services.AddSingleton<ApplicationPackagerEngine.Services.MsiInspectionService>();
        services.AddSingleton<ApplicationPackagerEngine.Services.WrapperGeneratorService>();
        services.AddSingleton<ApplicationPackagerEngine.Services.ManifestService>();
        services.AddSingleton<ApplicationPackagerEngine.Services.VersionHistoryService>();
        services.AddSingleton<ApplicationPackagerEngine.Services.ArchiveExtractionService>();
        services.AddSingleton<ApplicationPackagerEngine.Services.InstallerIdentificationService>();
        services.AddSingleton<ApplicationPackagerEngine.Services.IconExtractionService>();
        services.AddSingleton<ApplicationPackagerEngine.Services.StagingService>();
        services.AddSingleton<ApplicationPackagerEngine.Services.MecmTarget>();
        services.AddSingleton<ApplicationPackagerEngine.Services.ConditionTemplateService>();
        services.AddSingleton(sp => new ApplicationPackagerEngine.Services.DroppedInstallerAnalysisService(
            sp.GetRequiredService<ApplicationPackagerEngine.Services.MsiInspectionService>(),
            sp.GetRequiredService<ApplicationPackagerEngine.Services.InstallerIdentificationService>()));

        services.AddSingleton<IAppLog>(_ => new AppLog());
        services.AddSingleton<ISiteOperationHistory>(sp => new SiteOperationHistory(log: sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IConfigMgrSession>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new ConfigMgrSessionHost(() => SessionConnection(settings), sp.GetRequiredService<IAppLog>());
        });

        services.AddSingleton<IInstalledApplicationsService>(_ => new InstalledApplicationsService());
        services.AddSingleton<IDetectionTestService>(_ => new DetectionTestService());
        services.AddSingleton<IDetectionHistoryStore>(sp => new DetectionHistoryStore(log: sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IDetectionReportWriter>(_ => new DetectionReportWriter());

        services.AddSingleton<IScheduleProjector>(_ => new ScheduleProjector());
        services.AddSingleton<IMaintenanceWindowService>(sp => new MaintenanceWindowService(
            sp.GetRequiredService<IConfigMgrSession>(), sp.GetRequiredService<IScheduleProjector>(), sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IMaintenanceTemplateStore>(sp => new MaintenanceTemplateStore(log: sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IMaintenanceReportWriter>(_ => new MaintenanceReportWriter());

        services.AddSingleton<ICollectionService>(sp => new CollectionService(sp.GetRequiredService<IConfigMgrSession>(), sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IQueryTemplateStore>(sp => new QueryTemplateStore(log: sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<ICollectionReportWriter>(_ => new CollectionReportWriter());
        return services;
    }

    /// <summary>The site and provider the ConfigMgr session connects to, read at each command.</summary>
    public static ConfigMgrConnection SessionConnection(SettingsService settings)
    {
        var current = settings.Load();
        return new ConfigMgrConnection(current.SiteCode, current.ProviderMachine);
    }
}
