using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;

namespace ApplicationPackagerUI.Services;

public class ApplicationHostService(IServiceProvider serviceProvider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            serviceProvider.GetRequiredService<LegacyInventoryMigration>().Run();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"The saved application inventory could not be moved from settings.json into the local database. The settings file was kept unchanged.\n\n{ex.Message}",
                "Application inventory migration failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            System.Windows.Application.Current.Shutdown();
            return Task.CompletedTask;
        }

        // The resource dictionary starts Dark; the stored theme applies before the first window renders.
        ThemeApplier.Apply(serviceProvider.GetRequiredService<SettingsService>().Load().Theme);

        ShowMainWindow(serviceProvider.GetRequiredService<MainWindow>());

        MigrateLegacyPreferences();
        return Task.CompletedTask;
    }

    /// <summary>Shows the main window on Packages, the start page, whatever the saved sidebar order.</summary>
    public static void ShowMainWindow(MainWindow mainWindow)
    {
        mainWindow.Show();
        mainWindow.GetNavigation().Navigate(typeof(PackagesPage));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Writes one Migrated workbench profile per application that still carries
    /// per-app deployment conditions or command overrides. The pass is idempotent,
    /// leaves the settings untouched, and a failure only leaves the profiles
    /// unmigrated until the next start.
    /// </summary>
    private void MigrateLegacyPreferences()
    {
        var settings = serviceProvider.GetRequiredService<SettingsService>().Load();
        var packagersRoot = serviceProvider.GetRequiredService<PackagerDiscoveryService>().ResolvePackagersRoot();
        _ = Task.Run(() =>
        {
            try
            {
                LegacyPreferenceMigration.Run(WorkbenchStore.Open(), JsonSerializer.SerializeToNode(settings),
                    new WorkbenchDiscoveryRoots
                    {
                        CatalogDefinitionsRoot = IconPackService.BundledDefinitionsDirectory(),
                        CatalogOverridesRoot = AppDataPaths.UserDefinitionsRoot,
                        CustomDefinitionsRoot = AppDataPaths.UserDefinitionsRoot,
                        PackagersRoot = packagersRoot
                    });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or InvalidOperationException or ArgumentException)
            {
                System.Diagnostics.Debug.WriteLine($"Legacy preference migration skipped: {ex.Message}");
            }
        });
    }
}
