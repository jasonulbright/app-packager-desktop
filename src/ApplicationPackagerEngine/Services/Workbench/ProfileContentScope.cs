using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>
/// The profile-scoped suffix for network content version folders. Two profiles of
/// one application at one vendor version write to &lt;version&gt;-&lt;suffix&gt; instead of
/// sharing &lt;version&gt;. The default profile has no suffix and keeps today's paths.
/// </summary>
public sealed partial record ProfileContentScope(string ProfileId, string Name, string Suffix)
{
    public static readonly ProfileContentScope Default = new(ApplicationIds.DefaultProfileId, "", "");

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeCharacters();

    /// <summary>
    /// Reads the scope from a run snapshot file. A missing or unreadable snapshot,
    /// or one on the default profile, is the default scope.
    /// </summary>
    public static ProfileContentScope FromSnapshot(string? snapshotPath)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath) || !File.Exists(snapshotPath)) return Default;
        try
        {
            return FromSnapshotNode(WorkbenchJson.Read(snapshotPath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return Default;
        }
    }

    public static ProfileContentScope FromSnapshot(RunSnapshot snapshot) =>
        FromValues(snapshot.ProfileId, WorkbenchJson.AsText(snapshot.Profile["Name"]));

    private static ProfileContentScope FromSnapshotNode(System.Text.Json.Nodes.JsonNode? node) =>
        FromValues(WorkbenchJson.Get(node, "ProfileId").Text, WorkbenchJson.Get(node, "Profile.Name").Text);

    /// <summary>
    /// The suffix is the profile name reduced to [A-Za-z0-9._-] with trailing dots and
    /// spaces removed, then a hyphen and the first eight characters of the reduced
    /// profile id; a name that reduces to nothing gives the id fragment alone. Names
    /// that reduce to the same text (Pilot A and PilotA) would otherwise share one
    /// network folder, and a publish of one would mirror over the other's content.
    /// </summary>
    public static ProfileContentScope FromValues(string? profileId, string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileId) || ApplicationIds.IsDefaultProfile(profileId)) return Default;
        var name = profileName ?? "";
        var idFragment = UnsafeCharacters().Replace(profileId, "");
        if (idFragment.Length > 8) idFragment = idFragment[..8];
        var reducedName = UnsafeCharacters().Replace(name, "").TrimEnd('.', ' ');
        var suffix = string.IsNullOrWhiteSpace(reducedName) ? idFragment : $"{reducedName}-{idFragment}";
        return new ProfileContentScope(profileId, name, suffix);
    }

    /// <summary>
    /// The scope a staged build was finalized under, read from the build's own run
    /// snapshot (runs\&lt;BuildId&gt;.json) so the network folder matches the profile
    /// name at stage time. A build without a profile is the default scope; a
    /// snapshot that is gone falls back to the profile id.
    /// </summary>
    public static ProfileContentScope ForManifest(Models.StageManifest manifest, string? dataRoot = null)
    {
        if (string.IsNullOrWhiteSpace(manifest.BuildId) || ApplicationIds.IsDefaultProfile(manifest.ProfileId)) return Default;
        if (ApplicationIds.IsPlainFolderName(manifest.BuildId))
        {
            var root = string.IsNullOrWhiteSpace(dataRoot) ? WorkbenchPaths.ResolveDataRoot(create: false) : dataRoot;
            var path = Path.Combine(root, "runs", manifest.BuildId + ".json");
            if (File.Exists(path))
            {
                var scope = FromSnapshot(path);
                if (string.Equals(scope.ProfileId, manifest.ProfileId, StringComparison.OrdinalIgnoreCase)) return scope;
            }
        }
        return FromValues(manifest.ProfileId, null);
    }

    /// <summary>
    /// The local staging root for a profile: &lt;root&gt;\profiles\&lt;ProfileId&gt;, or the
    /// root unchanged for the default profile. Network folders take the version
    /// suffix instead (see <see cref="ScopedVersionFolder(string)"/>).
    /// </summary>
    public static string LocalDownloadRoot(string downloadRoot, string? profileId) =>
        WorkbenchPaths.GetProfileDownloadRoot(downloadRoot, profileId);

    /// <summary>The version folder name for a suffix: the version alone, or &lt;version&gt;-&lt;suffix&gt;.</summary>
    public static string ScopedVersionFolder(string version, string? suffix) =>
        string.IsNullOrWhiteSpace(suffix) ? version : $"{version}-{suffix}";

    public string ScopedVersionFolder(string version) => ScopedVersionFolder(version, Suffix);
}
