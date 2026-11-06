using System.Diagnostics;
using System.Reflection;
using System.Windows;
using ApplicationPackagerEngine.Services.Update;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// Runs the startup update check and opens the update window. The startup check runs in the background,
/// stays quiet unless a newer release exists, and never blocks the app.
/// </summary>
public sealed class UpdateCoordinator(SettingsService settingsService, UpdateService service)
{
    private UpdateWindow? _window;

    /// <summary>The running version from the assembly, for example 2026.1103.3. '0' when it cannot be read.</summary>
    public static string CurrentVersion { get; } = UpdateVersion.FromRunning(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) ?? "0";

    private static bool Launch(string path)
    {
        try
        {
            return Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }) is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private UpdateWindow CreateWindow(UpdateViewModel viewModel, Window? owner)
    {
        var window = new UpdateWindow(viewModel);
        if (owner is { IsVisible: true }) window.Owner = owner;
        window.Closed += (_, _) => _window = null;
        _window = window;
        return window;
    }

    /// <summary>Opens the update window and checks at once, or brings the open window to the front.</summary>
    public void ShowAndCheck(Window? owner)
    {
        if (_window is { IsVisible: true })
        {
            _window.Activate();
            return;
        }
        var viewModel = new UpdateViewModel(service, CurrentVersion, Launch, () => Application.Current.Shutdown());
        CreateWindow(viewModel, owner).Show();
        _ = viewModel.CheckCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// The check at startup. A failure, an offline machine and a rate limit stay silent: the manual
    /// check reports them. A newer release opens the update window once.
    /// </summary>
    public async Task RunStartupCheckAsync(Window owner)
    {
        UpdateCheck check;
        try
        {
            if (!settingsService.Load().CheckForUpdatesAtStartup) return;
            check = await service.CheckAsync(CurrentVersion).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Fire and forget from the main window: a failure here must not reach the user or end the app.
            Trace.WriteLine($"The startup update check failed: {ex.Message}");
            return;
        }
        if (check.Status != UpdateStatus.Available) return;
        if (_window is { IsVisible: true }) return;
        var viewModel = new UpdateViewModel(service, CurrentVersion, Launch, () => Application.Current.Shutdown());
        viewModel.Show(check);
        CreateWindow(viewModel, owner).Show();
    }
}
