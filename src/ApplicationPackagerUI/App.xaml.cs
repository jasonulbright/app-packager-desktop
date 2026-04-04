using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using System.Windows;
using Wpf.Ui.DependencyInjection;

namespace ApplicationPackagerUI;

public partial class App : Application
{
    private IHost? _host;

    public static IServiceProvider Services =>
        ((App)Current)._host!.Services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(
                $"Error: {args.Exception.Message}\n\n{args.Exception.StackTrace?[..Math.Min(500, args.Exception.StackTrace?.Length ?? 0)]}",
                "Application Packager",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            args.Handled = true;
        };

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddNavigationViewPageProvider();

                // WPF-UI services
                services.AddSingleton<INavigationService, NavigationService>();
                services.AddSingleton<ISnackbarService, SnackbarService>();
                services.AddSingleton<IContentDialogService, ContentDialogService>();

                // App services
                services.AddSingleton<SettingsService>();
                services.AddSingleton<PackagerDiscoveryService>();
                services.AddSingleton<PackagerExecutionService>();
                services.AddSingleton<MecmQueryService>();
                services.AddSingleton<PackagerPreferencesService>();

                // Engine services (v2.1)
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

                // Windows
                services.AddSingleton<MainWindow>();
                services.AddSingleton<MainWindowViewModel>();

                // Pages
                services.AddSingleton<PackagesPage>();
                services.AddSingleton<PackagesViewModel>();
                services.AddSingleton<ApplicationsPage>();
                services.AddSingleton<ApplicationsViewModel>();
                services.AddSingleton<SettingsPage>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<AddAppPage>();
                services.AddSingleton<AddAppViewModel>();

                // Hosted service
                services.AddHostedService<ApplicationHostService>();
            })
            .Build();

        await _host.StartAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
