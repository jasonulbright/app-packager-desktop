using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        services.AddSingleton<ICatalogService>(sp => new CatalogService(sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IComplianceService>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new ComplianceService(sp.GetRequiredService<IConfigMgrSession>(), sp.GetRequiredService<ICatalogService>(), new CatalogBuilderRunner(),
                () => SessionConnection(settings), sp.GetRequiredService<IAppLog>());
        });
        services.AddSingleton<IComplianceReportWriter>(_ => new ComplianceReportWriter());

        services.TryAddSingleton<IDeploymentAuditPathProvider>(_ => new DefaultDeploymentAuditPath());
        services.AddSingleton<IDeploymentAuditLog>(sp =>
        {
            var paths = sp.GetRequiredService<IDeploymentAuditPathProvider>();
            return new DeploymentAuditLog(() => paths.AuditLogPath, sp.GetRequiredService<IAppLog>());
        });
        services.AddSingleton<IDeploymentTemplateStore>(sp => new DeploymentTemplateStore(log: sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IDeploymentReportWriter>(_ => new DeploymentReportWriter());
        services.AddSingleton<IDeploymentSigningCheck>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new ProductSigningCheck(sp.GetRequiredService<ApplicationInventoryService>(), () => SessionConnection(settings), () => settings.Load().ScriptSigning, sp.GetRequiredService<IAppLog>());
        });
        services.AddSingleton(sp => new DeploymentService(sp.GetRequiredService<IConfigMgrSession>(), sp.GetRequiredService<ICollectionService>(),
            sp.GetRequiredService<IDeploymentAuditLog>(), sp.GetRequiredService<IDeploymentSigningCheck>(), sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IDeploymentService>(sp => sp.GetRequiredService<DeploymentService>());
        services.AddSingleton<IDeploymentSiteReader>(sp => sp.GetRequiredService<DeploymentService>());
        services.TryAddSingleton<IRingFolderPathProvider>(_ => new DefaultRingFolderPaths());
        services.AddSingleton<IRingService>(sp =>
        {
            var paths = sp.GetRequiredService<IRingFolderPathProvider>();
            var log = sp.GetRequiredService<IAppLog>();
            return new RingService(sp.GetRequiredService<IDeploymentService>(), sp.GetRequiredService<IDeploymentSiteReader>(), sp.GetRequiredService<IDeploymentAuditLog>(),
                new RingPlanStore(() => paths.PlansFolder, log), new RingRunStore(() => paths.RunsFolder, log), log);
        });
        services.AddSingleton<IDpContentAuditLog>(sp => new DpContentAuditLog(log: sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IDpContentReportWriter>(_ => new DpContentReportWriter());
        services.AddSingleton<IDpContentService>(sp => new DpContentService(sp.GetRequiredService<IConfigMgrSession>(), sp.GetRequiredService<IDpContentAuditLog>(), sp.GetRequiredService<IAppLog>()));

        services.AddSingleton<IAdminServiceClient>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new AdminServiceClient(() => settings.Load().ProviderMachine, sp.GetRequiredService<IAppLog>());
        });
        services.AddSingleton<IPivotsAuditLog>(sp => new PivotsAuditLog(log: sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IPivotsSessionStore>(sp => new PivotsSessionStore(log: sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IPivotsReportWriter>(_ => new PivotsReportWriter());
        services.AddSingleton<IPivotsLibrary>(sp => new PivotsLibrary(sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IPivotsScriptsService>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            var admin = sp.GetRequiredService<IAdminServiceClient>();
            return new PivotsScriptsService(sp.GetRequiredService<IConfigMgrSession>(), admin, sp.GetRequiredService<IPivotsAuditLog>(), sp.GetRequiredService<IPivotsSessionStore>(),
                sp.GetRequiredService<IAppLog>(), connection: () => (SessionConnection(settings).NormalizedSiteCode, admin.Provider));
        });

        services.AddSingleton<IHygieneAuditLog>(sp => new HygieneAuditLog(log: sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IHygieneSuppressionStore>(sp => new HygieneSuppressionStore(log: sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IHygieneScanStore>(sp => new HygieneScanStore(log: sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IHygieneReportWriter>(_ => new HygieneReportWriter());
        services.AddSingleton<ISiteHygieneService>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new SiteHygieneService(sp.GetRequiredService<IConfigMgrSession>(), sp.GetRequiredService<IHygieneAuditLog>(), sp.GetRequiredService<IAppLog>(),
                connection: () => SessionConnection(settings));
        });
        services.AddSingleton<ISiteHygieneLiveService>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new SiteHygieneLiveService(sp.GetRequiredService<IConfigMgrSession>(), sp.GetRequiredService<IAppLog>(), connection: () => SessionConnection(settings));
        });
        services.AddSingleton(sp => new MetricsHistoryStore(log: sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<IMetricsHistoryStore>(sp => sp.GetRequiredService<MetricsHistoryStore>());
        services.AddSingleton<IAlertEvaluator>(_ => new AlertEvaluator());
        services.AddSingleton<IAlertLog>(sp => new AlertLog(log: sp.GetRequiredService<IAppLog>()));
        services.AddSingleton<ILiveReportWriter>(_ => new LiveReportWriter());
        services.AddSingleton<ILegacyDashboardImport>(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new LegacyDashboardImport(sp.GetRequiredService<MetricsHistoryStore>(), () => SiteHygieneLivePreferences.Values(settings.Load()),
                values => SiteHygieneLivePreferences.Fill(settings, values), log: sp.GetRequiredService<IAppLog>());
        });

        services.AddSingleton<IAnalysisSandbox>(_ => new AnalysisSandbox());
        services.AddSingleton<IInstallerAnalysisService>(sp => new InstallerAnalysisService(sp.GetRequiredService<IAnalysisSandbox>()));
        services.AddSingleton<IInstallerReportWriter>(_ => new InstallerReportWriter());
        return services;
    }

    /// <summary>The site and provider the ConfigMgr session connects to, read at each command.</summary>
    public static ConfigMgrConnection SessionConnection(SettingsService settings)
    {
        var current = settings.Load();
        return new ConfigMgrConnection(current.SiteCode, current.ProviderMachine);
    }
}
