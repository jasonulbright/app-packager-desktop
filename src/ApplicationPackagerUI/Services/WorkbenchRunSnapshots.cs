using System.IO;
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

    /// <summary>
    /// The version of the sealed build that the snapshot names, read from its stage manifest, or why it cannot be read. The publish review of a
    /// Workbench run names this version, because the run publishes that build and not the latest version of the grid row.
    /// </summary>
    public static (string Version, string? Failure) SealedVersion(RunSnapshot snapshot)
    {
        try
        {
            var build = ByoStaging.SealedBuild(WorkbenchStore.Open(snapshot.DataRoot), snapshot.ApplicationId, snapshot.ProfileId, snapshot.BuildId);
            return (build.Manifest.SoftwareVersion ?? "", null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        {
            return ("", $"The sealed build {snapshot.BuildId} cannot be read: {ex.Message}");
        }
    }

    /// <summary>
    /// The version and BuildId of the stage at a manifest path, for a run that publishes that path (a bring-your-own build). With an expected
    /// BuildId, the manifest must still carry it: a later stage into the same folder is another build. A manifest without a BuildId cannot be
    /// told from another one, so it is a failure too.
    /// </summary>
    public static (string Version, string BuildId, string? Failure) StageAt(string manifestPath, string? expectedBuildId)
    {
        try
        {
            var manifest = new ManifestService().Read(manifestPath);
            var buildId = manifest.BuildId ?? "";
            if (buildId.Length == 0) return ("", "", $"The stage manifest {manifestPath} carries no BuildId. Stage the application again.");
            if (!string.IsNullOrWhiteSpace(expectedBuildId) && !string.Equals(buildId, expectedBuildId, StringComparison.OrdinalIgnoreCase))
                return ("", "", $"The build {expectedBuildId} is gone: {manifestPath} now carries BuildId {buildId}.");
            return (manifest.SoftwareVersion ?? "", buildId, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        {
            return ("", "", $"The stage manifest {manifestPath} cannot be read: {ex.Message}");
        }
    }

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
