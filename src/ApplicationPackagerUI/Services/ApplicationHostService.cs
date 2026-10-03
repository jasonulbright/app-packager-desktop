using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;

namespace ApplicationPackagerUI.Services;

public class ApplicationHostService(IServiceProvider serviceProvider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The resource dictionary starts Dark; the stored theme applies before the first window renders.
        if (Enum.TryParse<Wpf.Ui.Appearance.ApplicationTheme>(serviceProvider.GetRequiredService<SettingsService>().Load().Theme, out var theme)
            && theme is Wpf.Ui.Appearance.ApplicationTheme.Light or Wpf.Ui.Appearance.ApplicationTheme.Dark)
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme);

        var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();

        var nav = mainWindow.GetNavigation();
        nav.Navigate(typeof(PackagesPage));

        MigrateLegacyPreferences();
        return Task.CompletedTask;
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
                        CatalogDefinitionsRoot = Path.Combine(AppContext.BaseDirectory, "Definitions"),
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
