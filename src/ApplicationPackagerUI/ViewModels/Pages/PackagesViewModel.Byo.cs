using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Controls;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>Bring-your-own applications: keeping a drop as an application, and its Workbench stage and publish.</summary>
public partial class PackagesViewModel
{
    /// <summary>
    /// Persists the reviewed drop as a BYO application. No download script is written and
    /// nothing is staged or deployed; the installer becomes source revision 1.
    /// </summary>
    private async Task SaveDroppedInstallerAsApplicationAsync(DroppedInstallerAnalysis analysis, DropReviewPanel panel)
    {
        var problem = panel.Validate();
        if (problem is not null)
        {
            AddLogLine($"Drop rejected: {problem}");
            _snackbarService.Show("Missing Value", problem,
                Wpf.Ui.Controls.ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(3));
            return;
        }

        var overrides = panel.ReadOverrides();
        var stageValues = ByoStageValues.From(overrides, panel.IsConfirmed);
        ByoSourceUpdate saved;
        try
        {
            saved = await Task.Run(() => WorkbenchStore.Open().SaveByoApplication(
                analysis.Path, ByoInstallerIdentity.From(analysis), overrides.AppName, overrides.Publisher,
                overrides.SoftwareVersion, stageValues));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            AddLogLine($"Save as Application failed: {ex.Message}");
            _snackbarService.Show("Save as Application", ex.Message,
                Wpf.Ui.Controls.ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24), TimeSpan.FromSeconds(5));
            return;
        }

        AddLogLine($"Saved as application {saved.ApplicationId}. It appears in the workbench with manual updates; nothing was staged or deployed.");
        var answer = await _contentDialogService.ShowAsync(new ContentDialog
        {
            Title = "Saved",
            Content = $"\"{overrides.AppName}\" is saved as an application. Open it in the Workbench now?",
            PrimaryButtonText = "Yes",
            CloseButtonText = "No",
            DefaultButton = ContentDialogButton.Primary
        }, CancellationToken.None);
        if (answer == ContentDialogResult.Primary
            && System.Windows.Application.Current?.MainWindow is Views.Windows.MainWindow main)
        {
            main.OpenWorkbench(saved.ApplicationId);
        }
    }

    /// <summary>
    /// Stages a BYO application from its newest installer revision through the ad-hoc
    /// stage, under the run snapshot the Workbench created, and lists the staged result
    /// in the grid so Package continues from it.
    /// </summary>
    public async Task RunByoStageAsync(string applicationId, RunSnapshot snapshot)
    {
        if (IsOperationRunning)
        {
            AddLogLine($"Workbench run skipped: another operation is running. {applicationId} was not staged.");
            return;
        }
        var settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.DownloadRoot))
        {
            AddLogLine("Stage: DownloadRoot is not configured. Open Settings.");
            return;
        }

        IsOperationRunning = true;
        try
        {
            var profileName = WorkbenchJson.AsText(snapshot.Profile["Name"]) ?? snapshot.ProfileId;
            var displayName = WorkbenchStore.Open(snapshot.DataRoot).GetApplicationDefinition(applicationId).DisplayName ?? applicationId;
            AddLogLine($"Workbench Stage: {displayName} ({applicationId}, profile {profileName}, revision {snapshot.ProfileRevision}, build {snapshot.BuildId})");
            await StageByoCoreAsync(applicationId, snapshot, settings);
        }
        finally
        {
            IsOperationRunning = false;
        }
    }

    /// <summary>
    /// Publishes a BYO application. A selected build packages the stage its build record
    /// names, without reading the current installer revision; without one, Package reuses
    /// the manifest the last stage of this revision left when it is still current, and
    /// stages first otherwise.
    /// </summary>
    public async Task RunByoPackageAsync(string applicationId, string target, RunSnapshot? buildSnapshot, Func<RunSnapshot> newSnapshot)
    {
        if (IsOperationRunning)
        {
            AddLogLine($"Workbench run skipped: another operation is running. {applicationId} was not packaged.");
            return;
        }
        var settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.DownloadRoot))
        {
            AddLogLine("Package: DownloadRoot is not configured. Open Settings.");
            return;
        }

        PackagerInfo? row = null;
        RunSnapshot? snapshot = buildSnapshot;
        IsOperationRunning = true;
        try
        {
            var store = WorkbenchStore.Open();
            if (buildSnapshot is not null)
            {
                var sealedBuild = ByoStaging.SealedBuild(store, applicationId, buildSnapshot.ProfileId, buildSnapshot.BuildId);
                AddLogLine($"Workbench Package: {sealedBuild.Manifest.AppName} build {buildSnapshot.BuildId}");
                row = AddByoRow(Services.WorkbenchRunSnapshots.ByoRow(sealedBuild.Manifest, sealedBuild.ManifestPath, applicationId));
            }
            else
            {
                var plan = await ByoStaging.PlanAsync(store, _dropAnalysisService, applicationId, deepInspect: true);
                var profileId = store.GetApplicationDefinition(applicationId).ActiveProfileId;
                var current = new ByoStageCurrency(store.GetProfile(applicationId, profileId).Revision, settings.ScriptSigning.PolicyDigest);
                var manifestPath = ByoStaging.FindPriorStageManifest(plan, settings.DownloadRoot, profileId, target, current);
                if (manifestPath is not null)
                {
                    AddLogLine($"Workbench Package: {plan.Manifest.AppName} reuses the prior stage {manifestPath}");
                    row = AddByoRow(plan.Manifest, plan.Analysis, manifestPath, applicationId);
                }
                else
                {
                    snapshot = newSnapshot();
                    row = await StageByoCoreAsync(applicationId, snapshot, settings);
                    if (row is null) return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            AddLogLine($"Package refused: {ex.Message}");
            StatusText = "Package refused.";
            return;
        }
        finally
        {
            IsOperationRunning = false;
        }
        if (target == PublishRunTargets.WsusOnly) await PublishWsusRunAsync([row!], snapshot);
        else await PackageAsync([row!], target, snapshot);
    }

    private async Task<PackagerInfo?> StageByoCoreAsync(string applicationId, RunSnapshot snapshot, AppSettings settings)
    {
        ByoStagePlan plan;
        try
        {
            plan = await ByoStaging.PlanAsync(WorkbenchStore.Open(snapshot.DataRoot), _dropAnalysisService, applicationId, deepInspect: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            AddLogLine($"Stage refused: {ex.Message}");
            StatusText = "Stage refused.";
            return null;
        }

        StatusText = $"Staging bring-your-own application: {plan.Manifest.AppName}";
        AddLogLine($"Stage: {plan.Manifest.AppName} {plan.Manifest.SoftwareVersion} (source revision {plan.Source.Revision}, {plan.Source.FileName})");
        var inputs = new StageInputs { RunSnapshot = snapshot, SigningPolicy = settings.ScriptSigning };
        var pendingRow = Services.WorkbenchRunSnapshots.ByoRow(plan.Manifest, plan.Analysis, "", applicationId);
        var result = await ByoStaging.StageAsync(_stagingService, plan, settings.DownloadRoot, line => AddLogLine($"  {line}"), CancellationToken.None, inputs);
        if (!result.Success)
        {
            AddLogLine($"Stage FAILED: {result.ErrorMessage}");
            StatusText = "Stage failed.";
            _executionService.RecordExternalStage(pendingRow,
                new PackagerExecutionResult { ExitCode = 1, StdErr = result.ErrorMessage ?? "Stage failed." }, snapshot, AddLogLine);
            return null;
        }

        AddLogLine($"Staged: {result.ContentPath}");
        var row = AddByoRow(plan.Manifest, plan.Analysis, result.ManifestPath, applicationId);
        _executionService.RecordExternalStage(row,
            new PackagerExecutionResult { ExitCode = 0, StdOut = $"Staged to: {result.ContentPath}", OutLogPath = result.ManifestPath },
            snapshot, AddLogLine);
        StatusText = $"Staged {plan.Manifest.AppName}. Select the row and Package to continue.";
        return row;
    }

    /// <summary>Lists one staged BYO build in the grid as a normal run row; an earlier row of the same application is replaced.</summary>
    private PackagerInfo AddByoRow(StageManifest manifest, DroppedInstallerAnalysis analysis, string manifestPath, string applicationId) =>
        AddByoRow(Services.WorkbenchRunSnapshots.ByoRow(manifest, analysis, manifestPath, applicationId));

    private PackagerInfo AddByoRow(PackagerInfo row)
    {
        foreach (var existing in Packagers.Where(p => p.Uid == row.Uid).ToList()) Packagers.Remove(existing);
        Packagers.Add(row);
        ApplyFilter();
        return row;
    }
}
