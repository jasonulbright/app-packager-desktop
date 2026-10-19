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

/// <summary>The dialogs and folder calls of the Maintenance Windows page, so its view model runs without a window.</summary>
public interface IMaintenanceWindowsPrompts
{
    /// <summary>Shows the New, Edit or template dialog until the operator closes it or a save is reported.</summary>
    void ShowEditor(MaintenanceWindowEditorViewModel editor);

    /// <summary>Shows the Apply template dialog until the operator closes it or an apply is reported.</summary>
    void ShowApplyTemplate(MaintenanceApplyTemplateViewModel dialog);

    Task<bool> ConfirmDeleteTemplateAsync(string name);

    void OpenFolder(string folder);
}

public sealed class MaintenanceWindowsPrompts(IContentDialogService dialogs) : IMaintenanceWindowsPrompts
{
    private static Window? Owner() =>
        DiscoveryPrompt.UsableOwner(Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow);

    private static void ShowModal(Window window)
    {
        var owner = Owner();
        if (owner is not null) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }

    public void ShowEditor(MaintenanceWindowEditorViewModel editor) => ShowModal(new MaintenanceWindowEditorWindow(editor));

    public void ShowApplyTemplate(MaintenanceApplyTemplateViewModel dialog) => ShowModal(new MaintenanceApplyTemplateWindow(dialog));

    public async Task<bool> ConfirmDeleteTemplateAsync(string name)
    {
        var dialog = new ContentDialog
        {
            Title = "Delete template",
            Content = $"Delete the template {name}? Windows made from it stay in ConfigMgr.",
            PrimaryButtonText = "Delete template",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialogs.ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary;
    }

    public void OpenFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")); }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Reads and writes the page choices in settings.json without touching any other value.</summary>
public static class MaintenanceWindowsPreferences
{
    public static MaintenanceWindowsSettings? Load(SettingsService settings) => settings.Load().MaintenanceWindows;

    /// <summary>False when nothing was written; an unreadable settings file is never replaced.</summary>
    public static bool Save(SettingsService settings, MaintenanceWindowsSettings choices)
    {
        try
        {
            var stored = settings.Load();
            if (settings.IsFileUnreadable) return false;
            stored.MaintenanceWindows = choices;
            settings.Save(stored);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}

public static partial class MaintenanceWindowsRegistration
{
    public static IServiceCollection AddMaintenanceWindowsPage(this IServiceCollection services)
    {
        AddTemporaryServices(services);
        services.AddSingleton<IMaintenanceWindowsPrompts, MaintenanceWindowsPrompts>();
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new MaintenanceWindowWriter(
                sp.GetRequiredService<IMaintenanceWindowService>(),
                sp.GetRequiredService<ISiteOperationHistory>(),
                sp.GetRequiredService<IAppLog>(),
                () => PackagerServiceRegistration.SessionConnection(settings).Key);
        });
        services.AddSingleton<MaintenanceWindowsViewModel>();
        services.AddSingleton<MaintenanceWindowsPage>();
        return services;
    }

    // Registers fallback services with TryAdd, so a registered Engine service always wins.
    // Without an implementing file the compiler removes the call.
    static partial void AddTemporaryServices(IServiceCollection services);
}
