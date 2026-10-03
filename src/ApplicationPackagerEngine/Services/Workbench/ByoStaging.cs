using System.Text.Json;
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

/// <summary>The profile revision and signing-policy digest a reused stage must carry to count as current.</summary>
public sealed record ByoStageCurrency(int ProfileRevision, string PolicyDigest);

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
        manifest.ByoSource = new StageByoSource
        {
            ApplicationId = applicationId,
            Revision = resolved.Source.Revision,
            Sha256 = resolved.Source.Sha256
        };
        return new ByoStagePlan(applicationId, resolved, analysis, manifest);
    }

    /// <summary>The two fields every ad-hoc stage records about the installer it staged.</summary>
    public static void ApplyAdHocSource(StageManifest manifest, DroppedInstallerAnalysis analysis)
    {
        manifest.DetectedEngine = analysis.Engine;
        manifest.AdHocSource = analysis.Path;
    }

    /// <summary>
    /// Stages the plan. The staged copy of the installer must hash to the revision's
    /// recorded SHA-256, so a source replaced after the plan verified it never reaches
    /// the manifest.
    /// </summary>
    public static Task<StagingResult> StageAsync(
        StagingService staging, ByoStagePlan plan, string downloadRoot,
        Action<string>? onProgress = null, CancellationToken ct = default, StageInputs? inputs = null) =>
        staging.StageDroppedInstallerAsync(plan.Manifest, plan.InstallerPath, downloadRoot, onProgress, ct, inputs,
            new ExpectedInstaller(plan.Source.Sha256, $"source revision {plan.Source.Revision}"));

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
    /// Package without Stage consumes the build the last stage left behind: the manifest
    /// in the folder this plan stages into, when it was staged from this application's
    /// same source revision and installer hash, under this profile at its current revision
    /// and the current signing policy. Null otherwise, and the build stages first. Two
    /// applications, or two revisions of one version, share that folder, so the folder
    /// alone never identifies the build. A WSUS target always stages again, because its
    /// publish accepts only a manifest that carries this run's BuildId.
    /// </summary>
    public static string? FindPriorStageManifest(ByoStagePlan plan, string downloadRoot, string? profileId, string? target,
        ByoStageCurrency current)
    {
        if (target is not null && (string.Equals(target, RunTargets.WsusOnly, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(target, RunTargets.MecmAndWsus, StringComparison.OrdinalIgnoreCase)))
            return null;
        var path = Path.Combine(StageFolder(plan.Manifest, downloadRoot, profileId), "stage-manifest.json");
        if (!File.Exists(path)) return null;

        StageManifest staged;
        try { staged = new ManifestService().Read(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
        var source = staged.ByoSource;
        if (source is null
            || !string.Equals(source.ApplicationId, plan.ApplicationId, StringComparison.OrdinalIgnoreCase)
            || source.Revision != plan.Source.Revision
            || !string.Equals(source.Sha256, plan.Source.Sha256, StringComparison.OrdinalIgnoreCase))
            return null;
        var profile = string.IsNullOrWhiteSpace(profileId) ? ApplicationIds.DefaultProfileId : profileId;
        if (!string.Equals(staged.ProfileId, profile, StringComparison.OrdinalIgnoreCase)
            || staged.ProfileRevision != current.ProfileRevision)
            return null;
        var digest = WorkbenchJson.Get(staged.ScriptSigning, "PolicyDigest").Text ?? "";
        return string.Equals(digest, current.PolicyDigest ?? "", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    /// <summary>
    /// The stage of one sealed BYO build, found from its build record: the manifest under
    /// the recorded stage root that carries exactly <paramref name="buildId"/>. The current
    /// installer revision is not read, so an older build publishes after its source was
    /// replaced.
    /// </summary>
    public static StagedBuild SealedBuild(WorkbenchStore store, string applicationId, string profileId, string buildId)
    {
        var record = BuildRecords.List(store.DataRoot, applicationId, profileId)
                         .FirstOrDefault(r => string.Equals(WorkbenchJson.Get(r, "BuildId").Text, buildId, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException($"stale build: no sealed build record of {applicationId} carries BuildId '{buildId}'.");
        var stageRoot = WorkbenchJson.Get(record, "StageRoot").Text;
        if (string.IsNullOrWhiteSpace(stageRoot))
            throw new InvalidOperationException($"stale build: the build record of '{buildId}' names no stage root.");
        return BuildRecords.ResolveStageManifestForBuild(buildId, stageRoot);
    }
}
