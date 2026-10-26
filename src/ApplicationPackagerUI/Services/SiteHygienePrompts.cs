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

/// <summary>The dialogs, the clipboard and the folder calls of the Site Hygiene page, so its view model runs without a window.</summary>
public interface ISiteHygieneDialogs
{
    /// <summary>Shows the findings of a fix until the operator continues or cancels; the result is on the view model.</summary>
    void ShowFindings(HygieneFindingsPickerViewModel findings);

    /// <summary>The comment the operator typed; null when cancelled.</summary>
    string? AskComment(string title, string prompt);

    void SetClipboard(string text);

    void OpenFolder(string folder);
}

public sealed class SiteHygieneDialogs : ISiteHygieneDialogs
{
    private static void ShowModal(Window window)
    {
        var owner = DiscoveryPrompt.UsableOwner(Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow);
        if (owner is not null) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Helpers.OpenToolTips.Close();
        window.ShowDialog();
    }

    public void ShowFindings(HygieneFindingsPickerViewModel findings) => ShowModal(new HygieneFindingsPickerWindow(findings));

    public string? AskComment(string title, string prompt)
    {
        var dialog = new PivotsCommentViewModel(title, prompt);
        ShowModal(new PivotsCommentWindow(dialog));
        return dialog.Result;
    }

    public void SetClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    public void OpenFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")); }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Reads and writes the page choices in settings.json without touching any other value.</summary>
public static class SiteHygienePreferences
{
    private static readonly object FileLock = new();

    public static SiteHygieneSettings? Load(SettingsService settings) => settings.Load().SiteHygiene;

    /// <summary>False when nothing was written; an unreadable settings file is never replaced.</summary>
    public static bool Save(SettingsService settings, SiteHygieneSettings choices)
    {
        lock (FileLock)
        {
            try
            {
                var stored = settings.Load();
                if (settings.IsFileUnreadable) return false;
                stored.SiteHygiene = choices;
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

/// <summary>The page, its view model and its writer; the service, the stores and the report writer come from <see cref="PackagerServiceRegistration"/>.</summary>
public static class SiteHygieneRegistration
{
    public static IServiceCollection AddSiteHygienePage(this IServiceCollection services)
    {
        services.AddSingleton<ISiteHygieneDialogs, SiteHygieneDialogs>();
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new SiteHygieneWriter(
                sp.GetRequiredService<ISiteHygieneService>(),
                sp.GetRequiredService<ISiteOperationHistory>(),
                sp.GetRequiredService<IAppLog>(),
                () => PackagerServiceRegistration.SessionConnection(settings).Key);
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            var launcher = sp.GetRequiredService<SuiteToolLauncher>();
            return new SiteHygienePageServices(
                sp.GetRequiredService<ISiteHygieneService>(),
                sp.GetRequiredService<IHygieneSuppressionStore>(),
                sp.GetRequiredService<IHygieneScanStore>(),
                sp.GetRequiredService<IHygieneReportWriter>(),
                sp.GetRequiredService<ISiteHygieneDialogs>(),
                sp.GetRequiredService<IChangePlanDialogs>(),
                sp.GetRequiredService<SiteHygieneWriter>(),
                settings.Load,
                () => SiteHygienePreferences.Load(settings),
                choices => SiteHygienePreferences.Save(settings, choices),
                () => launcher.LaunchAsync(SuiteTools.SiteHygiene, settings.Load),
                sp.GetRequiredService<IAppLog>())
            {
                AuditLog = sp.GetRequiredService<IHygieneAuditLog>()
            };
        });
        services.AddSingleton<SiteHygieneViewModel>();
        services.AddSingleton<SiteHygienePage>();
        return services;
    }
}
