using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerUI.Services;

/// <summary>The dialogs and folder calls of the Compliance section, so its view models run without a window.</summary>
public interface IComplianceDialogs
{
    /// <summary>Shows the deploy form until the operator closes it or a deploy is confirmed.</summary>
    void ShowDeploy(CatalogDeployViewModel deploy);

    /// <summary>Shows the setting dialog until the operator closes it or a save is confirmed.</summary>
    void ShowSettingEditor(CiSettingEditorViewModel editor);

    /// <summary>Shows the collection picker until the operator closes it or an accept is reported.</summary>
    void ShowPicker(CollectionPickerViewModel picker);

    void OpenFolder(string folder);
}

public sealed class ComplianceDialogs : IComplianceDialogs
{
    private static void ShowModal(Window window)
    {
        var owner = DiscoveryPrompt.UsableOwner(Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow);
        if (owner is not null) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }

    public void ShowDeploy(CatalogDeployViewModel deploy) => ShowModal(new CatalogDeployWindow(deploy));

    public void ShowSettingEditor(CiSettingEditorViewModel editor) => ShowModal(new CiSettingEditorWindow(editor));

    public void ShowPicker(CollectionPickerViewModel picker) => ShowModal(new CollectionPickerWindow(picker));

    public void OpenFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")); }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) { }
    }
}

public static partial class ComplianceRegistration
{
    public static IServiceCollection AddCompliancePage(this IServiceCollection services)
    {
        AddServiceFallbacks(services);
        services.AddSingleton<IComplianceDialogs, ComplianceDialogs>();
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new ComplianceWriter(
                sp.GetRequiredService<IComplianceService>(),
                sp.GetRequiredService<ISiteOperationHistory>(),
                sp.GetRequiredService<IAppLog>(),
                () => PackagerServiceRegistration.SessionConnection(settings).Key);
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            return new CompliancePageServices(
                sp.GetRequiredService<IComplianceService>(),
                sp.GetRequiredService<ICatalogService>(),
                sp.GetRequiredService<ICollectionService>(),
                sp.GetRequiredService<IComplianceReportWriter>(),
                sp.GetRequiredService<IComplianceDialogs>(),
                sp.GetRequiredService<IChangePlanDialogs>(),
                sp.GetRequiredService<ComplianceWriter>(),
                settings.Load,
                () => CollectionCompliancePreferences.LoadCompliance(settings),
                choices => CollectionCompliancePreferences.SaveCompliance(settings, choices),
                sp.GetRequiredService<IAppLog>());
        });
        services.AddSingleton<ComplianceViewModel>();
        return services;
    }

    /// <summary>Registers stand-ins for compliance services that are not registered; a no-op once the services exist.</summary>
    static partial void AddServiceFallbacks(IServiceCollection services);
}
