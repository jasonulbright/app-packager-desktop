using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ApplicationPackagerUI.Services;

/// <summary>The file and folder dialogs, clipboard and folder calls of the Installer Analysis page, so its view model runs without a window.</summary>
public interface IInstallerAnalysisPrompts
{
    /// <summary>The chosen installer, or null when the user cancels.</summary>
    string? PickInstaller();

    /// <summary>The chosen folder, or null when the user cancels.</summary>
    string? PickFolder(string title);

    /// <summary>False when another process holds the clipboard.</summary>
    bool CopyText(string text);

    /// <summary>Opens the folder in Explorer. Nothing in the folder is started.</summary>
    void OpenFolder(string folder);
}

public sealed class InstallerAnalysisPrompts : IInstallerAnalysisPrompts
{
    private static Window? Owner() => DiscoveryPrompt.UsableOwner(Application.Current?.MainWindow);

    public string? PickInstaller()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open an installer",
            Filter = "Installers (*.exe;*.msi;*.msp;*.msix;*.msixbundle;*.appx;*.appxbundle;*.intunewin;*.nupkg;*.zip)|*.exe;*.msi;*.msp;*.msix;*.msixbundle;*.appx;*.appxbundle;*.intunewin;*.nupkg;*.zip|All files (*.*)|*.*",
            CheckFileExists = true,
            DereferenceLinks = false
        };
        return dialog.ShowDialog(Owner()) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dialog.ShowDialog(Owner()) == true ? dialog.FolderName : null;
    }

    public bool CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    public void OpenFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")); }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Reads and writes the page choices in settings.json without touching any other value.</summary>
public static class InstallerAnalysisPreferences
{
    public static InstallerAnalysisSettings? Load(SettingsService settings) => settings.Load().InstallerAnalysis;

    /// <summary>False when nothing was written; an unreadable settings file is never replaced.</summary>
    public static bool Save(SettingsService settings, InstallerAnalysisSettings choices)
    {
        try
        {
            var stored = settings.Load();
            if (settings.IsFileUnreadable) return false;
            stored.InstallerAnalysis = choices;
            settings.Save(stored);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>Removes the analysis sessions an earlier run left behind at start, and every session at exit.</summary>
public sealed class InstallerAnalysisSandboxLifetime(IAnalysisSandbox sandbox, IAppLog log) : IHostedService
{
    /// <summary>A session younger than this can belong to another running instance of the desktop.</summary>
    public static readonly TimeSpan StartupCleanAge = TimeSpan.FromHours(1);

    private Task _clean = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _clean = Task.Run(() => Guard("The old analysis sessions could not be removed", () => sandbox.CleanOldSessions(StartupCleanAge)), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _clean;
        Guard("The analysis sessions could not be removed at exit", sandbox.ReleaseAll);
    }

    private void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            log.Warning(InstallerAnalysisViewModel.LogArea, $"{what} ({ex.GetType().Name}).");
        }
    }
}

public static partial class InstallerAnalysisRegistration
{
    public static IServiceCollection AddInstallerAnalysisPage(this IServiceCollection services)
    {
        AddServiceFallbacks(services);
        services.AddSingleton<IInstallerAnalysisPrompts, InstallerAnalysisPrompts>();
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new InstallerAnalysisPageServices(
                sp.GetRequiredService<IInstallerAnalysisService>(),
                sp.GetRequiredService<IAnalysisSandbox>(),
                sp.GetRequiredService<IInstallerReportWriter>(),
                sp.GetRequiredService<IInstallerAnalysisPrompts>(),
                () => InstallerAnalysisPreferences.Load(settings),
                choices => InstallerAnalysisPreferences.Save(settings, choices),
                sp.GetRequiredService<IAppLog>(),
                InstallerAnalysisViewModel.DefaultLimits,
                () => Path.Combine(AppDataPaths.Root, "InstallerAnalysis", "Temp"));
        });
        services.AddSingleton<InstallerAnalysisViewModel>();
        services.AddSingleton<InstallerAnalysisPage>();
        services.AddHostedService<InstallerAnalysisSandboxLifetime>();
        return services;
    }

    /// <summary>Registers stand-ins for analysis services that are not registered; a no-op once the services exist.</summary>
    static partial void AddServiceFallbacks(IServiceCollection services);
}
