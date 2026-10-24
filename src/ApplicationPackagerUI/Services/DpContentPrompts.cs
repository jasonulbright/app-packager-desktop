using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerUI.Services;

/// <summary>The dialogs and folder calls of the DP Content Manager page, so its view model runs without a window.</summary>
public interface IDpContentDialogs
{
    /// <summary>Shows the pairs of an action until the operator continues or cancels; the result is on the view model.</summary>
    void ShowPairs(DpContentPairsViewModel pairs);

    void OpenFolder(string folder);
}

public sealed class DpContentDialogs : IDpContentDialogs
{
    public void ShowPairs(DpContentPairsViewModel pairs)
    {
        var window = new DpContentPairsWindow(pairs);
        var owner = DiscoveryPrompt.UsableOwner(Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow);
        if (owner is not null) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }

    public void OpenFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")); }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Reads and writes the page choices in settings.json without touching any other value.</summary>
public static class DpContentPreferences
{
    private static readonly object FileLock = new();

    public static DpContentSettings? Load(SettingsService settings) => settings.Load().DpContent;

    /// <summary>False when nothing was written; an unreadable settings file is never replaced.</summary>
    public static bool Save(SettingsService settings, DpContentSettings choices)
    {
        lock (FileLock)
        {
            try
            {
                var stored = settings.Load();
                if (settings.IsFileUnreadable) return false;
                stored.DpContent = choices;
                settings.Save(stored);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return false;
            }
        }
    }
}

public static partial class DpContentRegistration
{
    public static IServiceCollection AddDpContentManagerPage(this IServiceCollection services)
    {
        AddServiceFallbacks(services);
        services.AddSingleton<IDpContentDialogs, DpContentDialogs>();
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new DpContentWriter(
                sp.GetRequiredService<IDpContentService>(),
                sp.GetRequiredService<ISiteOperationHistory>(),
                sp.GetRequiredService<IAppLog>(),
                () => PackagerServiceRegistration.SessionConnection(settings).Key);
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new DpContentPageServices(
                sp.GetRequiredService<IDpContentService>(),
                sp.GetRequiredService<IDpContentAuditLog>(),
                sp.GetRequiredService<IDpContentReportWriter>(),
                sp.GetRequiredService<IDpContentDialogs>(),
                sp.GetRequiredService<IChangePlanDialogs>(),
                sp.GetRequiredService<DpContentWriter>(),
                settings.Load,
                () => DpContentPreferences.Load(settings),
                choices => DpContentPreferences.Save(settings, choices),
                sp.GetRequiredService<IAppLog>());
        });
        services.AddSingleton<DpContentManagerViewModel>();
        services.AddSingleton<DpContentManagerPage>();
        return services;
    }

    /// <summary>Registers stand-ins for DP content services that are not registered; a no-op once the services exist.</summary>
    static partial void AddServiceFallbacks(IServiceCollection services);
}
