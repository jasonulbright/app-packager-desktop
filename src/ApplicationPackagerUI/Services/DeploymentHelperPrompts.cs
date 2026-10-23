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
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace ApplicationPackagerUI.Services;

/// <summary>The dialogs and folder calls of the Deployment Helper page, so its view models run without a window.</summary>
public interface IDeploymentHelperDialogs
{
    /// <summary>Shows the text prompt until the operator closes it or an accept is reported.</summary>
    void ShowTextPrompt(TextPromptViewModel prompt);

    /// <summary>Shows the template editor until the operator closes it or a save is reported.</summary>
    void ShowTemplateEditor(DeploymentTemplateEditorViewModel editor);

    Task<bool> ConfirmDeleteTemplateAsync(string name);

    /// <summary>Asks the operator to confirm the removal that <paramref name="text"/> names; false when declined or closed.</summary>
    Task<bool> ConfirmRemoveDeploymentAsync(string text);

    void OpenFolder(string folder);

    /// <summary>Shows the ring plan editor until the operator closes it or a save is reported.</summary>
    void ShowRingPlanEditor(RingPlanEditorViewModel editor);

    /// <summary>Asks the operator for one device collection; null when the dialog was closed without a pick.</summary>
    CollectionRow? PickCollection(string title, IReadOnlyList<CollectionRow> collections, string? currentId);

    Task<bool> ConfirmDeleteRingPlanAsync(string name);

    /// <summary>Asks the operator to check device collections; the checked ones in the order shown, empty when the dialog was closed.</summary>
    IReadOnlyList<CollectionRow> PickCollections(string title, IReadOnlyList<CollectionRow> collections);
}

public sealed class DeploymentHelperDialogs(IContentDialogService dialogs) : IDeploymentHelperDialogs
{
    private static void ShowModal(Window window)
    {
        var owner = DiscoveryPrompt.UsableOwner(Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow);
        if (owner is not null) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }

    public void ShowTextPrompt(TextPromptViewModel prompt) => ShowModal(new TextPromptWindow(prompt));

    public void ShowTemplateEditor(DeploymentTemplateEditorViewModel editor) => ShowModal(new DeploymentTemplateEditorWindow(editor));

    public async Task<bool> ConfirmDeleteTemplateAsync(string name)
    {
        var dialog = new ContentDialog
        {
            Title = "Delete template",
            Content = $"Delete the template {name}? Deployments made with it stay in ConfigMgr.",
            PrimaryButtonText = "Delete template",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialogs.ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary;
    }

    /// <summary>
    /// A modal window owned by the change plan window: that window is modal, so a content dialog
    /// of the main window cannot take input while it is open.
    /// </summary>
    public Task<bool> ConfirmRemoveDeploymentAsync(string text)
    {
        var owner = RemovalConfirmWindow.OwnerFor(Application.Current?.Windows.OfType<Window>() ?? [], Application.Current?.MainWindow);
        return Task.FromResult(RemovalConfirmWindow.Ask(owner, text));
    }

    public void OpenFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")); }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) { }
    }

    public void ShowRingPlanEditor(RingPlanEditorViewModel editor) => ShowModal(new RingPlanEditorWindow(editor));

    public CollectionRow? PickCollection(string title, IReadOnlyList<CollectionRow> collections, string? currentId)
    {
        var picker = new RingCollectionPickerViewModel(title, collections, currentId);
        ShowModal(new RingCollectionPickerWindow(picker));
        return picker.Result;
    }

    public IReadOnlyList<CollectionRow> PickCollections(string title, IReadOnlyList<CollectionRow> collections)
    {
        var picker = new RingCollectionPickerViewModel(title, collections, null, multiSelect: true);
        ShowModal(new RingCollectionPickerWindow(picker));
        return picker.Results;
    }

    public async Task<bool> ConfirmDeleteRingPlanAsync(string name)
    {
        var dialog = new ContentDialog
        {
            Title = "Delete ring plan",
            Content = $"Delete the ring plan {name}? Runs made with it keep their run files and their deployments.",
            PrimaryButtonText = "Delete plan",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialogs.ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary;
    }
}

/// <summary>The ring folders of the page settings, read at every call; an empty setting selects the default under the data root.</summary>
public sealed class SettingsRingFolders(Func<AppSettings> load) : IRingFolderPathProvider
{
    public string? PlansFolder => load().DeploymentHelper?.RingsFolder?.Trim() is { Length: > 0 } path ? path : null;
    public string? RunsFolder => load().DeploymentHelper?.RunsFolder?.Trim() is { Length: > 0 } path ? path : null;
}

/// <summary>Reads and writes the page choices in settings.json without touching any other value.</summary>
public static class DeploymentHelperPreferences
{
    private static readonly object FileLock = new();

    public static DeploymentHelperSettings? Load(SettingsService settings) => settings.Load().DeploymentHelper;

    /// <summary>The audit log path the settings name; empty for the default.</summary>
    public static string AuditLogPath(SettingsService settings) => settings.Load().DeploymentHelper?.AuditLogPath?.Trim() ?? "";

    /// <summary>False when nothing was written; an unreadable settings file is never replaced.</summary>
    public static bool Save(SettingsService settings, DeploymentHelperSettings choices)
    {
        lock (FileLock)
        {
            try
            {
                var stored = settings.Load();
                if (settings.IsFileUnreadable) return false;
                stored.DeploymentHelper = choices;
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

/// <summary>The audit log path of the page settings, read at every call; an empty setting selects the default under the data root.</summary>
public sealed class SettingsDeploymentAuditPath(Func<AppSettings> load) : IDeploymentAuditPathProvider
{
    public string? AuditLogPath => load().DeploymentHelper?.AuditLogPath?.Trim() is { Length: > 0 } path ? path : null;
}

public static partial class DeploymentHelperRegistration
{
    public static IServiceCollection AddDeploymentHelperPage(this IServiceCollection services)
    {
        AddServiceFallbacks(services);
        services.Replace(ServiceDescriptor.Singleton<IDeploymentAuditPathProvider>(sp => new SettingsDeploymentAuditPath(sp.GetRequiredService<SettingsService>().Load)));
        services.AddSingleton<IDeploymentHelperDialogs, DeploymentHelperDialogs>();
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new DeploymentWriter(
                sp.GetRequiredService<IDeploymentService>(),
                sp.GetRequiredService<ISiteOperationHistory>(),
                sp.GetRequiredService<IAppLog>(),
                () => PackagerServiceRegistration.SessionConnection(settings).Key,
                sp.GetRequiredService<IDeploymentHelperDialogs>().ConfirmRemoveDeploymentAsync);
        });
        services.Replace(ServiceDescriptor.Singleton<IRingFolderPathProvider>(sp => new SettingsRingFolders(sp.GetRequiredService<SettingsService>().Load)));
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new RingWriter(
                sp.GetRequiredService<IRingService>(),
                sp.GetRequiredService<ISiteOperationHistory>(),
                sp.GetRequiredService<IAppLog>(),
                () => PackagerServiceRegistration.SessionConnection(settings).Key,
                sp.GetRequiredService<IDeploymentHelperDialogs>().ConfirmRemoveDeploymentAsync);
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new DeploymentPageServices(
                sp.GetRequiredService<IDeploymentService>(),
                sp.GetRequiredService<ICollectionService>(),
                sp.GetRequiredService<IDeploymentAuditLog>(),
                sp.GetRequiredService<IDeploymentTemplateStore>(),
                sp.GetRequiredService<IDeploymentReportWriter>(),
                sp.GetRequiredService<IDeploymentHelperDialogs>(),
                sp.GetRequiredService<IChangePlanDialogs>(),
                sp.GetRequiredService<DeploymentWriter>(),
                settings.Load,
                () => DeploymentHelperPreferences.Load(settings),
                choices => DeploymentHelperPreferences.Save(settings, choices),
                sp.GetRequiredService<IRingService>(),
                sp.GetRequiredService<RingWriter>(),
                sp.GetRequiredService<IAppLog>(),
                TimeProvider.System);
        });
        services.AddSingleton<DeploymentHelperViewModel>();
        services.AddSingleton<DeploymentHelperPage>();
        return services;
    }

    /// <summary>Registers stand-ins for deployment services that are not registered; a no-op once the services exist.</summary>
    static partial void AddServiceFallbacks(IServiceCollection services);
}
