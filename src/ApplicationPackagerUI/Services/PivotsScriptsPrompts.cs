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
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApplicationPackagerUI.Services;

/// <summary>The dialogs, the clipboard and the folder calls of the Pivots and Scripts page, so its view model runs without a window.</summary>
public interface IPivotsDialogs
{
    /// <summary>The TXT or CSV file of device names the operator picked; null when cancelled.</summary>
    string? PickTargetFile();

    /// <summary>The library folder the operator picked; null when cancelled.</summary>
    string? PickFolder(string? current);

    /// <summary>Shows the Parameters dialog until OK or Cancel; the values are on the view model.</summary>
    void ShowParameters(PivotsParametersViewModel parameters);

    /// <summary>The comment the operator typed; null when cancelled.</summary>
    string? AskComment(string title, string prompt);

    void SetClipboard(string text);

    void OpenFolder(string folder);
}

public sealed class PivotsDialogs : IPivotsDialogs
{
    private static Window? Owner() => DiscoveryPrompt.UsableOwner(Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow);

    private static void ShowModal(Window window)
    {
        if (Owner() is { } owner) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }

    public string? PickTargetFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Device lists (*.txt;*.csv)|*.txt;*.csv|All files (*.*)|*.*", CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog(Owner()) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string? current)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Multiselect = false };
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current)) dialog.InitialDirectory = current;
        return dialog.ShowDialog(Owner()) == true ? dialog.FolderName : null;
    }

    public void ShowParameters(PivotsParametersViewModel parameters) => ShowModal(new PivotsParametersWindow(parameters));

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
public static class PivotsScriptsPreferences
{
    private static readonly object FileLock = new();

    public static PivotsScriptsSettings? Load(SettingsService settings) => settings.Load().PivotsScripts;

    /// <summary>False when nothing was written; an unreadable settings file is never replaced.</summary>
    public static bool Save(SettingsService settings, PivotsScriptsSettings choices)
    {
        lock (FileLock)
        {
            try
            {
                var stored = settings.Load();
                if (settings.IsFileUnreadable) return false;
                stored.PivotsScripts = choices;
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

public static class PivotsScriptsRegistration
{
    public const string NotAvailable = "The Pivots and Scripts service is not available in this build.";

    public static IServiceCollection AddPivotsScriptsPage(this IServiceCollection services)
    {
        services.TryAddSingleton<IPivotsScriptsService, UnavailableService>();
        services.TryAddSingleton<IPivotsSessionStore, UnavailableSessions>();
        services.TryAddSingleton<IPivotsAuditLog, UnavailableAuditLog>();
        services.TryAddSingleton<IPivotsReportWriter, UnavailableReports>();
        services.TryAddSingleton<IPivotsLibrary, EmptyLibrary>();
        services.AddSingleton<IPivotsDialogs, PivotsDialogs>();
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new PivotsScriptsWriter(
                sp.GetRequiredService<IPivotsScriptsService>(),
                sp.GetRequiredService<ISiteOperationHistory>(),
                sp.GetRequiredService<IAppLog>(),
                () => PackagerServiceRegistration.SessionConnection(settings).Key);
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new PivotsScriptsPageServices(
                sp.GetRequiredService<IPivotsScriptsService>(),
                sp.GetRequiredService<IPivotsSessionStore>(),
                sp.GetRequiredService<IPivotsAuditLog>(),
                sp.GetRequiredService<IPivotsReportWriter>(),
                sp.GetRequiredService<IPivotsLibrary>(),
                sp.GetRequiredService<IPivotsDialogs>(),
                sp.GetRequiredService<IChangePlanDialogs>(),
                sp.GetRequiredService<PivotsScriptsWriter>(),
                settings.Load,
                () => PivotsScriptsPreferences.Load(settings),
                choices => PivotsScriptsPreferences.Save(settings, choices),
                sp.GetRequiredService<IAppLog>());
        });
        services.AddSingleton<PivotsScriptsViewModel>();
        services.AddSingleton<PivotsScriptsPage>();
        return services;
    }

    private sealed class UnavailableService : IPivotsScriptsService
    {
        public Task<ResolvedTargets> ResolveTargetsAsync(RunTarget target, CancellationToken ct) => throw new PivotsScriptsServiceException(NotAvailable);
        public Task<IReadOnlyList<SiteScript>> LoadScriptsAsync(CancellationToken ct) => throw new PivotsScriptsServiceException(NotAvailable);
        public Task<PivotsChange> PrepareAsync(PivotsChange request, CancellationToken ct) => throw new PivotsScriptsServiceException(NotAvailable);
        public Task<PivotsOutcome> ApplyAsync(PivotsChange change, IProgress<PivotsOutcome> progress, CancellationToken ct) => throw new PivotsScriptsServiceException(NotAvailable);
        public Task<PivotsOutcome> WaitForResultsAsync(string operationId, IProgress<PivotsOutcome> progress, CancellationToken ct) => throw new PivotsScriptsServiceException(NotAvailable);
        public Task<string> TestConnectionAsync(CancellationToken ct) => Task.FromResult(NotAvailable);
    }

    private sealed class UnavailableSessions : IPivotsSessionStore
    {
        public string Save(PivotsSession session) => throw new PivotsScriptsServiceException(NotAvailable);
        public IReadOnlyList<PivotsSessionSummary> List(int max) => [];
        public PivotsSession? Read(string sessionId) => null;
        public string Folder => "";
    }

    private sealed class UnavailableAuditLog : IPivotsAuditLog
    {
        public AuditProbe Probe() => new(false, NotAvailable);
        public AuditWrite Append(PivotsAuditRecord record) => new(false, NotAvailable);
        public IReadOnlyList<PivotsAuditRecord> Read(int max) => [];
        public string Path => "";
    }

    private sealed class UnavailableReports : IPivotsReportWriter
    {
        public string WriteCsv(PivotsReport report) => throw new PivotsScriptsServiceException(NotAvailable);
        public string WriteJson(PivotsReport report) => throw new PivotsScriptsServiceException(NotAvailable);
        public string Folder => "";
    }

    private sealed class EmptyLibrary : IPivotsLibrary
    {
        public IReadOnlyList<PivotsLibraryItem> Load(PivotsLibraryKind kind, string? userFolder) => [];
    }
}
