using System.Windows;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerUI.Services;

/// <summary>Sends a file drop to the Add from file page.</summary>
public interface IAddFileRoute
{
    void Route(IReadOnlyList<string> paths);
}

/// <summary>Shows the Add from file page and starts its analysis of the dropped file.</summary>
public sealed class AddFileRoute(AddFileViewModel addFile, Action<Type> navigate) : IAddFileRoute
{
    public void Route(IReadOnlyList<string> paths)
    {
        navigate(typeof(AddFilePage));
        addFile.DropFiles(paths);
    }
}

/// <summary>The result panel actions on the App Packager grid and the Workbench window.</summary>
public sealed class AddResultActions(IServiceProvider services) : IAddResultActions
{
    public Task SelectRowAsync(string definitionId) =>
        services.GetRequiredService<PackagesViewModel>().SelectDefinitionAsync(definitionId);

    public void OpenWorkbench(string definitionId) =>
        services.GetRequiredService<MainWindow>().OpenWorkbench(WorkbenchApplicationCatalog.ApplicationIdForDefinition(definitionId));

    public Task<string> StageAsync(string definitionId) =>
        services.GetRequiredService<PackagesViewModel>().StageDefinitionAsync(definitionId);
}

public sealed class AddFilePrompts : IAddFilePrompts
{
    public string? PickInstaller(string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open an installer file",
            Filter = filter,
            CheckFileExists = true,
            DereferenceLinks = false
        };
        return dialog.ShowDialog(DiscoveryPrompt.UsableOwner(Application.Current?.MainWindow)) == true ? dialog.FileName : null;
    }
}

public static class AddFlowRegistration
{
    /// <summary>The two add pages, their result panels and the drop route. The analysis service is registered with the packager services.</summary>
    public static IServiceCollection AddAddFlows(this IServiceCollection services)
    {
        services.AddSingleton<IAddResultActions, AddResultActions>();
        services.AddSingleton<IAddFilePrompts, AddFilePrompts>();
        services.AddTransient<AddResultViewModel>();
        services.AddSingleton<AddFileViewModel>();
        services.AddSingleton<AddFilePage>();
        services.AddSingleton<AddAppViewModel>();
        services.AddSingleton<AddAppPage>();
        services.AddSingleton<IAddFileRoute>(sp => new AddFileRoute(
            addFile: sp.GetRequiredService<AddFileViewModel>(),
            navigate: page => sp.GetRequiredService<INavigationService>().Navigate(page)));
        return services;
    }
}
