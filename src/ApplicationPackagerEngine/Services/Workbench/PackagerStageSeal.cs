using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>
/// Seals the build record of a PS1 packager stage that ran with a run snapshot. The child
/// writes its own stage manifest and, when its AppPackagerCommon module carries stage
/// finalization, its own record; this covers a child that wrote the manifest and no
/// record. The record follows <see cref="BuildRecords.Write"/>: a manifest that does not
/// carry the snapshot's BuildId is a build the snapshot was never applied to and gets none.
/// </summary>
public static partial class PackagerStageSeal
{
    [GeneratedRegex(@"Wrote stage manifest\s*:\s*(?<path>.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex ManifestLine();

    /// <summary>The manifest path the child logged last, or null when it logged none.</summary>
    public static string? ManifestPathFromOutput(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        var matches = ManifestLine().Matches(output);
        return matches.Count == 0 ? null : matches[^1].Groups["path"].Value;
    }

    /// <summary>
    /// Seals the record for <paramref name="snapshot"/> from the manifest the child logged,
    /// else from the manifest under <paramref name="searchRoot"/> that carries the BuildId.
    /// Returns the record, or null with the reason none was sealed.
    /// </summary>
    public static JsonObject? Seal(RunSnapshot snapshot, string? loggedManifestPath, string? searchRoot, out string message)
    {
        var dataRoot = string.IsNullOrWhiteSpace(snapshot.DataRoot) ? WorkbenchPaths.ResolveDataRoot() : snapshot.DataRoot;
        var profileId = string.IsNullOrWhiteSpace(snapshot.ProfileId) ? ApplicationIds.DefaultProfileId : snapshot.ProfileId;
        var recordPath = Path.Combine(BuildRecords.GetBuildFolder(dataRoot, snapshot.ApplicationId, profileId, snapshot.BuildId), BuildRecords.FileName);
        if (File.Exists(recordPath))
        {
            message = $"Build record sealed by the packager: {recordPath}";
            return WorkbenchJson.Read(recordPath) as JsonObject;
        }

        StagedBuild? build = null;
        if (!string.IsNullOrWhiteSpace(loggedManifestPath) && File.Exists(loggedManifestPath))
        {
            var manifest = new ManifestService().Read(loggedManifestPath);
            if (!string.Equals(manifest.BuildId, snapshot.BuildId, StringComparison.OrdinalIgnoreCase))
            {
                message = $"No build record sealed: {loggedManifestPath} carries no BuildId {snapshot.BuildId}. " +
                          "The packager's AppPackagerCommon module did not apply the run snapshot, so this stage is not a build of the profile.";
                return null;
            }
            build = new StagedBuild(loggedManifestPath, Path.GetDirectoryName(Path.GetFullPath(loggedManifestPath))!, manifest);
        }
        else if (!string.IsNullOrWhiteSpace(searchRoot))
        {
            try { build = BuildRecords.ResolveStageManifestForBuild(snapshot.BuildId, searchRoot); }
            catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or IOException or JsonException)
            {
                message = $"No build record sealed: {ex.Message}";
                return null;
            }
        }

        if (build is null)
        {
            message = "No build record sealed: the packager logged no stage manifest and no download root was given to search.";
            return null;
        }
        var record = BuildRecords.Write(dataRoot, build.StageRoot, build.Manifest);
        message = record is null
            ? $"No build record sealed: {build.ManifestPath} carries no ApplicationId."
            : $"Build record sealed          : {WorkbenchJson.Get(record, "Path").Text}";
        return record;
    }
}
