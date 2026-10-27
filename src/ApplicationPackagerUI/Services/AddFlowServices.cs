using System.Windows;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.AddFromInstaller;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    public string? PickInstaller()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open an installer file",
            Filter = AddFileFormats.DialogFilter,
            CheckFileExists = true,
            DereferenceLinks = false
        };
        return dialog.ShowDialog(DiscoveryPrompt.UsableOwner(Application.Current?.MainWindow)) == true ? dialog.FileName : null;
    }
}

/// <summary>Stands in until the analysis service is registered; every call fails with a plain message.</summary>
public sealed class UnavailableAddFromInstallerService : IAddFromInstallerService
{
    private static Exception Unavailable() => new InvalidOperationException("The installer analysis service is not available in this build.");

    public Task<AddAnalysisResult> AnalyzeAsync(string installerPath, IProgress<string> progress, CancellationToken ct) => Task.FromException<AddAnalysisResult>(Unavailable());

    public Task<AddSaveResult> SaveAsync(AddAnalysisResult reviewed, AddSaveOptions options, CancellationToken ct) => Task.FromException<AddSaveResult>(Unavailable());

    public Task<string> FetchInstallerAsync(DownloadRequest request, IProgress<string> progress, CancellationToken ct) => Task.FromException<string>(Unavailable());
}

public static class AddFlowRegistration
{
    /// <summary>The two add pages, their result panels, the drop route and the analysis service when none is registered yet.</summary>
    public static IServiceCollection AddAddFlows(this IServiceCollection services)
    {
        services.TryAddSingleton<IAddFromInstallerService, UnavailableAddFromInstallerService>();
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
