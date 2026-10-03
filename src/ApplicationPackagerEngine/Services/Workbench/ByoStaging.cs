using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>The stage manifest one BYO build stages, with the revision and installer it came from.</summary>
public sealed record ByoStagePlan(
    string ApplicationId,
    ByoResolvedSource Resolved,
    DroppedInstallerAnalysis Analysis,
    StageManifest Manifest)
{
    public string InstallerPath => Resolved.InstallerPath;
    public ByoSourceRevision Source => Resolved.Source;
}

/// <summary>
/// A BYO application has no definition and no packager script: it stages from the
/// installer revision persisted with it, through the same ad-hoc stage a dropped
/// installer takes. The name and publisher come from the stored definition and the
/// version from the revision; the remaining values come from the revision's
/// confirmed drop review over a fresh analysis of the stored installer.
/// </summary>
public static class ByoStaging
{
    public static async Task<ByoStagePlan> PlanAsync(
        WorkbenchStore store, DroppedInstallerAnalysisService analyzer, string applicationId,
        string? version = null, bool deepInspect = false, CancellationToken ct = default)
    {
        var resolved = store.ResolveByoSource(applicationId, version);
        var analysis = await analyzer.AnalyzeAsync(resolved.InstallerPath, deepInspect, ct);
        var values = resolved.Source.StageValues;
        var overrides = new DroppedInstallerOverrides
        {
            AppName = resolved.Definition.DisplayName,
            Publisher = resolved.Definition.Publisher,
            SoftwareVersion = resolved.Source.SoftwareVersion,
            InstallArgs = values?.InstallArgs,
            UninstallArgs = values?.UninstallArgs,
            UninstallCommand = values?.UninstallCommand,
            InstallMode = values?.InstallMode
        };
        var manifest = analyzer.BuildStageManifest(analysis, overrides, values?.Confirmed == true);
        ApplyAdHocSource(manifest, analysis);
        return new ByoStagePlan(applicationId, resolved, analysis, manifest);
    }

    /// <summary>The two fields every ad-hoc stage records about the installer it staged.</summary>
    public static void ApplyAdHocSource(StageManifest manifest, DroppedInstallerAnalysis analysis)
    {
        manifest.DetectedEngine = analysis.Engine;
        manifest.AdHocSource = analysis.Path;
    }

    public static Task<StagingResult> StageAsync(
        StagingService staging, ByoStagePlan plan, string downloadRoot,
        Action<string>? onProgress = null, CancellationToken ct = default, StageInputs? inputs = null) =>
        staging.StageDroppedInstallerAsync(plan.Manifest, plan.InstallerPath, downloadRoot, onProgress, ct, inputs);

    /// <summary>
    /// The folder an ad-hoc stage of <paramref name="manifest"/> writes under the
    /// download root of <paramref name="profileId"/>: vendor, application and version,
    /// named the way the ad-hoc stage names them.
    /// </summary>
    public static string StageFolder(StageManifest manifest, string downloadRoot, string? profileId)
    {
        var vendor = MecmTarget.SanitizeFolderNamePublic(manifest.Publisher);
        if (string.IsNullOrWhiteSpace(vendor)) vendor = "Unknown";
        var application = MecmTarget.SanitizeFolderNamePublic(manifest.DisplayName ?? manifest.AppName);
        var version = MecmTarget.SanitizeFolderNamePublic(manifest.SoftwareVersion);
        return Path.Combine(ProfileContentScope.LocalDownloadRoot(downloadRoot, profileId), vendor, application, version);
    }

    /// <summary>The application folder (vendor and application) that holds every staged version of the plan.</summary>
    public static string ApplicationStageRoot(StageManifest manifest, string downloadRoot, string? profileId) =>
        Path.GetDirectoryName(StageFolder(manifest, downloadRoot, profileId))!;

    /// <summary>
    /// Package without Stage consumes the build the last stage left behind: the
    /// manifest in the folder this plan stages into, or null when there is none and
    /// the build stages first. A WSUS target always stages again, because its publish
    /// accepts only a manifest that carries this run's BuildId.
    /// </summary>
    public static string? FindPriorStageManifest(ByoStagePlan plan, string downloadRoot, string? profileId, string? target = null)
    {
        if (target is not null && (string.Equals(target, RunTargets.WsusOnly, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(target, RunTargets.MecmAndWsus, StringComparison.OrdinalIgnoreCase)))
            return null;
        var manifest = Path.Combine(StageFolder(plan.Manifest, downloadRoot, profileId), "stage-manifest.json");
        return File.Exists(manifest) ? manifest : null;
    }
}
