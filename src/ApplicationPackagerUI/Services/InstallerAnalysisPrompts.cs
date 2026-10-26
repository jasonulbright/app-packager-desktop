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
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\""))?.Dispose(); }
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

/// <summary>
/// Removes at start the sessions whose owner process has ended (and old sessions without an owner
/// file) and stale 7-Zip list files in the Temp folder, and every session of this process at exit.
/// </summary>
/// <param name="tempFolder">The folder swept for stale 7-Zip list files; the default is the user's Temp folder.</param>
public sealed class InstallerAnalysisSandboxLifetime(IAnalysisSandbox sandbox, IAppLog log, Func<int, long, bool>? isAlive = null, string? tempFolder = null) : IHostedService
{
    /// <summary>A session without an owner file and younger than this can be one another instance is creating.</summary>
    public static readonly TimeSpan StartupCleanAge = TimeSpan.FromHours(1);

    private readonly Func<int, long, bool> _isAlive = isAlive ?? InstallerAnalysisSessions.IsAlive;
    private Task _clean = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _clean = Task.Run(() =>
        {
            Guard("The old analysis sessions could not be removed",
                () => InstallerAnalysisSessions.CleanAbandoned(sandbox, _isAlive, StartupCleanAge));
            Guard("The old 7-Zip list files could not be removed",
                () => InstallerAnalysisTempFiles.RemoveStaleListFiles(tempFolder ?? Path.GetTempPath(), InstallerAnalysisTempFiles.StaleListFileAge));
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _clean.ConfigureAwait(false);
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
            var sandbox = sp.GetRequiredService<IAnalysisSandbox>();
            return new InstallerAnalysisPageServices(
                Analysis: sp.GetRequiredService<IInstallerAnalysisService>(),
                Sandbox: sandbox,
                Reports: sp.GetRequiredService<IInstallerReportWriter>(),
                Prompts: sp.GetRequiredService<IInstallerAnalysisPrompts>(),
                LoadPreferences: () => InstallerAnalysisPreferences.Load(settings),
                SavePreferences: choices => InstallerAnalysisPreferences.Save(settings, choices),
                Log: sp.GetRequiredService<IAppLog>(),
                Limits: AnalysisLimits.Default,
                SandboxRoot: () => sandbox.Root is { Length: > 0 } root ? root : InstallerAnalysisFolders.Temp());
        });
        services.AddSingleton<InstallerAnalysisViewModel>();
        services.AddSingleton<InstallerAnalysisPage>();
        services.AddHostedService(sp => new InstallerAnalysisSandboxLifetime(sp.GetRequiredService<IAnalysisSandbox>(), sp.GetRequiredService<IAppLog>()));
        return services;
    }

    /// <summary>Registers stand-ins for analysis services that are not registered; a no-op once the services exist.</summary>
    static partial void AddServiceFallbacks(IServiceCollection services);
}
