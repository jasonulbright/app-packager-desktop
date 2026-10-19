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

/// <summary>The dialogs and folder calls of the Collection and Compliance page, so its view model runs without a window.</summary>
public interface ICollectionCompliancePrompts
{
    /// <summary>Shows the New, Edit or Copy dialog until the operator closes it or a save is reported.</summary>
    void ShowCollectionEditor(CollectionEditorViewModel editor);

    /// <summary>Shows the collection picker until the operator closes it or an accept is reported.</summary>
    void ShowPicker(CollectionPickerViewModel picker);

    /// <summary>Shows the text prompt until the operator closes it or an accept is reported.</summary>
    void ShowTextPrompt(TextPromptViewModel prompt);

    void OpenFolder(string folder);
}

public sealed class CollectionCompliancePrompts : ICollectionCompliancePrompts
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

    public void ShowCollectionEditor(CollectionEditorViewModel editor) => ShowModal(new CollectionEditorWindow(editor));

    public void ShowPicker(CollectionPickerViewModel picker) => ShowModal(new CollectionPickerWindow(picker));

    public void ShowTextPrompt(TextPromptViewModel prompt) => ShowModal(new TextPromptWindow(prompt));

    public void OpenFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")); }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Reads and writes the page choices in settings.json without touching any other value.</summary>
public static class CollectionCompliancePreferences
{
    public static CollectionComplianceSettings? Load(SettingsService settings) => settings.Load().CollectionCompliance;

    /// <summary>False when nothing was written; an unreadable settings file is never replaced.</summary>
    public static bool Save(SettingsService settings, CollectionComplianceSettings choices)
    {
        try
        {
            var stored = settings.Load();
            if (settings.IsFileUnreadable) return false;
            stored.CollectionCompliance = choices;
            settings.Save(stored);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}

public static partial class CollectionComplianceRegistration
{
    public static IServiceCollection AddCollectionCompliancePage(this IServiceCollection services)
    {
        AddServiceFallbacks(services);
        services.AddSingleton<ICollectionCompliancePrompts, CollectionCompliancePrompts>();
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new CollectionWriter(
                sp.GetRequiredService<ICollectionService>(),
                sp.GetRequiredService<ISiteOperationHistory>(),
                sp.GetRequiredService<IAppLog>(),
                () => PackagerServiceRegistration.SessionConnection(settings).Key);
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new CollectionCompliancePageServices(
                sp.GetRequiredService<ICollectionService>(),
                sp.GetRequiredService<IQueryTemplateStore>(),
                sp.GetRequiredService<ICollectionReportWriter>(),
                sp.GetRequiredService<ICollectionCompliancePrompts>(),
                sp.GetRequiredService<IChangePlanDialogs>(),
                sp.GetRequiredService<CollectionWriter>(),
                settings.Load,
                () => CollectionCompliancePreferences.Load(settings),
                choices => CollectionCompliancePreferences.Save(settings, choices),
                async () =>
                {
                    var result = await sp.GetRequiredService<SuiteToolLauncher>().LaunchAsync(SuiteTools.CollectionAndCompliance, settings.Load);
                    return result.Outcome == SuiteToolLaunchOutcome.Failed ? result.Message ?? $"{SuiteTools.CollectionAndCompliance.Title} could not start." : null;
                },
                sp.GetRequiredService<IAppLog>());
        });
        services.AddSingleton<CollectionComplianceViewModel>();
        services.AddSingleton<CollectionCompliancePage>();
        return services;
    }

    /// <summary>Registers stand-ins for collection services that are not registered; a no-op once the services exist.</summary>
    static partial void AddServiceFallbacks(IServiceCollection services);
}
