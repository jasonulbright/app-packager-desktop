using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Signing;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// The run snapshot a Workbench or command-line Stage builds from: the profile frozen,
/// the signing certificate resolved for the configured policy, and the profile's own
/// download root. A policy the certificate cannot meet throws and nothing is written.
/// </summary>
public static class WorkbenchRunSnapshots
{
    public static RunSnapshot Create(WorkbenchStore store, AppSettings settings, string applicationId, string profileId,
        string target, string? packagerScriptPath, JsonObject? runOverrides = null) =>
        store.CreateRunSnapshot(new RunSnapshotRequest
        {
            ApplicationId = applicationId,
            ProfileId = profileId,
            Target = target,
            RunOverrides = runOverrides,
            SigningPolicy = JsonSerializer.SerializeToNode(settings.ScriptSigning),
            PackagerScriptPath = packagerScriptPath,
            DownloadRoot = WorkbenchPaths.GetProfileDownloadRoot(settings.DownloadRoot, profileId)
        }, new ScriptSigningCertificateGate());

    /// <summary>The run row of one staged BYO build: Package reads its stage manifest directly.</summary>
    public static PackagerInfo ByoRow(StageManifest manifest, DroppedInstallerAnalysis analysis, string manifestPath, string applicationId) =>
        ByoRow(manifest, manifestPath, applicationId, analysis.FileName, analysis.Path, analysis.Engine);

    /// <summary>The run row of one sealed BYO build, from its staged manifest alone.</summary>
    public static PackagerInfo ByoRow(StageManifest manifest, string manifestPath, string applicationId) =>
        ByoRow(manifest, manifestPath, applicationId, manifest.InstallerFile, manifest.AdHocSource ?? manifestPath, manifest.DetectedEngine ?? "");

    private static PackagerInfo ByoRow(StageManifest manifest, string manifestPath, string applicationId,
        string installerName, string installerPath, string engine) => new()
    {
        Uid = "byo-" + ApplicationIds.ToKey(applicationId),
        Vendor = manifest.Publisher,
        Application = manifest.AppName,
        CMName = manifest.AppName,
        Script = installerName,
        FullPath = installerPath,
        Description = $"Bring your own installer: {manifest.InstallerType} ({engine})",
        StagedManifestPath = manifestPath,
        LatestVersion = manifest.SoftwareVersion,
        Status = "Staged",
        IsSelected = true
    };
}
