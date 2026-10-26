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
                "App Packager Desktop",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            args.Handled = true;
        };

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddNavigationViewPageProvider();

                services.AddSingleton<INavigationService, NavigationService>();
                services.AddSingleton<ISnackbarService, SnackbarService>();
                services.AddSingleton<IContentDialogService, ContentDialogService>();

                services.AddPackagerServices();
                services.AddSidebar();
                services.AddSingleton<IWorkbenchBuildLauncher, WorkbenchBuildLauncher>();
                services.AddSingleton<PublishDialogs>();
                services.AddSingleton<IPublishDialogs>(sp => sp.GetRequiredService<PublishDialogs>());
                services.AddSingleton<IWsusPrompts>(sp => sp.GetRequiredService<PublishDialogs>());
                services.AddSingleton<IDiscoveryPrompt, DiscoveryPrompt>();
                services.AddSingleton<IChangePlanDialogs, ChangePlanDialogs>();

                services.AddSingleton<MainWindow>();
                services.AddSingleton<MainWindowViewModel>();

                // Setup window: transient so a re-open starts from the stored settings.
                services.AddTransient<SetupWindow>();
                services.AddTransient<SetupWindowViewModel>();

                // Workbench window: transient so every open starts from the stored profiles.
                services.AddTransient<WorkbenchWindow>();
                services.AddTransient<WorkbenchWindowViewModel>();

                services.AddSingleton<PackagesPage>();
                services.AddSingleton<PackagesViewModel>();
                services.AddSingleton<ApplicationsPage>();
                services.AddSingleton<ApplicationsViewModel>();
                services.AddSingleton<SettingsPage>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<IPublishActionHost>(sp => sp.GetRequiredService<PackagesViewModel>());
                services.AddSingleton<ISettingsReload>(sp => sp.GetRequiredService<SettingsViewModel>());
                services.AddSingleton<AddAppPage>();
                services.AddSingleton<AddAppViewModel>();
                services.AddDetectionTesterPage();
                services.AddMaintenanceWindowsPage();
                services.AddCollectionCompliancePage();
                services.AddInstallerAnalysisPage();
                services.AddDeploymentHelperPage();
                services.AddDpContentManagerPage();

                services.AddHostedService<ApplicationHostService>();
            })
            .Build();

        await _host.StartAsync();
    }

    /// <summary>The longest wait at exit for the hosted services to stop.</summary>
    private static readonly TimeSpan ExitStopTimeout = TimeSpan.FromSeconds(10);

    // An async OnExit returns at its first await and the process can end before the stop runs.
    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);

        AppShutdown.Complete(_host, ExitStopTimeout, () => Environment.Exit(e.ApplicationExitCode));
    }
}
