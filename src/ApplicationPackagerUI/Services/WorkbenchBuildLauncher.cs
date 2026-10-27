using System.IO;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Signing;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;

namespace ApplicationPackagerUI.Services;

public enum WorkbenchBuildOperation
{
    Stage,
    Package
}

/// <summary>
/// One run requested from the Workbench footer. Target is a <see cref="RunTargets"/> value;
/// Stage carries the configured deployment target. Package names the sealed build it
/// consumes through <paramref name="BuildId"/>.
/// </summary>
public sealed record WorkbenchBuildRequest(
    string ApplicationId, string ProfileId, WorkbenchBuildOperation Operation, string Target, string? BuildId = null);

/// <summary>Why a run did not start, with the dialog title and text the Workbench shows.</summary>
public sealed record WorkbenchLaunchRefusal(string Title, string Message);

/// <summary>Starts a Stage or a publish for the Workbench's application.</summary>
public interface IWorkbenchBuildLauncher
{
    /// <summary>
    /// Resolves the run snapshot for the request, calls <paramref name="beforeRun"/> once it
    /// exists, and starts the run in the background. A refusal starts nothing.
    /// </summary>
    WorkbenchLaunchRefusal? Launch(WorkbenchBuildRequest request, Action beforeRun);
}

/// <summary>
/// Runs a Workbench Stage or publish through the App Packager page, so the run lands the same
/// log, status and history a grid run does. Stage writes a new run snapshot through
/// <see cref="WorkbenchStore.CreateRunSnapshot"/>; Package reads the snapshot of the
/// selected sealed build, so it packages exactly the stage that carries that BuildId.
/// </summary>
public sealed class WorkbenchBuildLauncher(
    SettingsService settingsService,
    PackagesViewModel packagesViewModel,
    INavigationService navigationService) : IWorkbenchBuildLauncher
{
    public const string NoSealedBuildMessage =
        "No sealed build exists for this profile. Stage it first; Package consumes an exact build id, not the newest manifest on disk.";

    public WorkbenchLaunchRefusal? Launch(WorkbenchBuildRequest request, Action beforeRun)
    {
        var package = request.Operation == WorkbenchBuildOperation.Package;
        var title = package ? "Publish" : "Stage";
        if (ApplicationIds.IsByo(request.ApplicationId)) return LaunchByo(request, beforeRun, package, title);
        var row = packagesViewModel.Packagers.FirstOrDefault(p =>
            string.Equals(WorkbenchApplicationCatalog.ApplicationIdFor(p), request.ApplicationId, StringComparison.OrdinalIgnoreCase));
        if (row is null)
            return new WorkbenchLaunchRefusal(title,
                "This application is not in the App Packager grid, so it cannot run through the grid pipeline.");
        if (packagesViewModel.IsOperationRunning)
            return new WorkbenchLaunchRefusal(title,
                "Another operation is running on the App Packager page. Wait for it to finish, then run this again.");
        if (package && string.IsNullOrWhiteSpace(request.BuildId))
            return new WorkbenchLaunchRefusal(title, NoSealedBuildMessage);

        var settings = settingsService.Load();
        var store = WorkbenchStore.Open();
        RunSnapshot snapshot;
        try
        {
            snapshot = package
                ? WorkbenchStore.ReadRunSnapshot(Path.Combine(store.RunsRoot, request.BuildId + ".json"))
                  ?? throw new FileNotFoundException($"Run snapshot for build '{request.BuildId}' was not found.")
                : WorkbenchRunSnapshots.Create(store, settings, request.ApplicationId, request.ProfileId, request.Target,
                    row.IsJsonDefined ? row.DefinitionPath : row.FullPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException
                                   or UnauthorizedAccessException or JsonException)
        {
            return new WorkbenchLaunchRefusal(title, "Run snapshot not available: " + ex.Message);
        }

        beforeRun();
        navigationService.Navigate(typeof(PackagesPage));
        _ = packagesViewModel.RunWorkbenchBuildAsync(row, package, package ? request.Target : null, snapshot);
        return null;
    }

    /// <summary>
    /// A BYO application has no grid row and no packager script: Stage stages its newest
    /// installer revision through the ad-hoc stage under a new run snapshot; Package uses
    /// the selected build, else the prior stage, else stages first.
    /// </summary>
    private WorkbenchLaunchRefusal? LaunchByo(WorkbenchBuildRequest request, Action beforeRun, bool package, string title)
    {
        if (packagesViewModel.IsOperationRunning)
            return new WorkbenchLaunchRefusal(title,
                "Another operation is running on the App Packager page. Wait for it to finish, then run this again.");
        var settings = settingsService.Load();
        var store = WorkbenchStore.Open();
        RunSnapshot NewSnapshot() =>
            WorkbenchRunSnapshots.Create(store, settings, request.ApplicationId, request.ProfileId, request.Target, packagerScriptPath: null);

        RunSnapshot? snapshot;
        try
        {
            if (ByoNeedsCurrentSource(request)) store.ResolveByoSource(request.ApplicationId);
            snapshot = package
                ? string.IsNullOrWhiteSpace(request.BuildId)
                    ? null
                    : WorkbenchStore.ReadRunSnapshot(Path.Combine(store.RunsRoot, request.BuildId + ".json"))
                      ?? throw new FileNotFoundException($"Run snapshot for build '{request.BuildId}' was not found.")
                : NewSnapshot();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException
                                   or UnauthorizedAccessException or JsonException)
        {
            return new WorkbenchLaunchRefusal(title, ex is InvalidOperationException or FileNotFoundException
                ? ex.Message
                : "Run snapshot not available: " + ex.Message);
        }

        beforeRun();
        navigationService.Navigate(typeof(PackagesPage));
        _ = package
            ? packagesViewModel.RunByoPackageAsync(request.ApplicationId, request.Target, snapshot, NewSnapshot)
            : packagesViewModel.RunByoStageAsync(request.ApplicationId, snapshot!);
        return null;
    }

    /// <summary>
    /// True when the BYO run can stage, so it needs the current installer revision: Stage,
    /// and Package without a selected build. A selected sealed build publishes from its
    /// build record and stage alone.
    /// </summary>
    public static bool ByoNeedsCurrentSource(WorkbenchBuildRequest request) =>
        request.Operation == WorkbenchBuildOperation.Stage || string.IsNullOrWhiteSpace(request.BuildId);

    /// <summary>The run target a footer button stands for; Stage records the configured deployment target.</summary>
    public static string TargetFor(WorkbenchBuildOperation operation, string? publishTarget, string configuredTarget) =>
        operation == WorkbenchBuildOperation.Stage || string.IsNullOrWhiteSpace(publishTarget)
            ? DeploymentTargets.Normalize(configuredTarget)
            : publishTarget;
}
