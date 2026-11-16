using System.Diagnostics;
using System.Reflection;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// Keeps the packager icon pack on the machine without a click: at startup, in the background, it installs the pack
/// when none is installed and installs a newer release when there is one. A failure or an offline machine stays
/// silent (the Settings page has the manual download and reports the outcome); the app never waits for it.
/// </summary>
public static class IconPackStartup
{
    public static async Task RunAsync()
    {
        try
        {
            var settings = new SettingsService().Load();
            var userFolder = IconPackService.UserDefinitionsDirectory();
            var status = IconPackService.ReadStatus(userFolder);
            // A pack that is installed is refreshed only while the check for updates at startup is on.
            if (status.Installed && !settings.CheckForUpdatesAtStartup) return;
            var result = await Task.Run(() => new IconPackService().InstallAsync(
                userFolder, Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
                definitionSourceDirectories: [userFolder, IconPackService.BundledDefinitionsDirectory()],
                onlyWhenNewer: true)).ConfigureAwait(false);
            Trace.WriteLine($"Icon pack at startup: {result.Message}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"The icon pack check at startup failed: {ex.Message}");
        }
    }
}
