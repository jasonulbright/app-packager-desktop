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
    /// spaces removed; a name that reduces to nothing falls back to the first eight
    /// characters of the reduced profile id.
    /// </summary>
    public static ProfileContentScope FromValues(string? profileId, string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileId) || ApplicationIds.IsDefaultProfile(profileId)) return Default;
        var name = profileName ?? "";
        var suffix = UnsafeCharacters().Replace(name, "").TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(suffix))
        {
            suffix = UnsafeCharacters().Replace(profileId, "");
            if (suffix.Length > 8) suffix = suffix[..8];
        }
        return new ProfileContentScope(profileId, name, suffix);
    }

    /// <summary>The version folder name for a suffix: the version alone, or &lt;version&gt;-&lt;suffix&gt;.</summary>
    public static string ScopedVersionFolder(string version, string? suffix) =>
        string.IsNullOrWhiteSpace(suffix) ? version : $"{version}-{suffix}";

    public string ScopedVersionFolder(string version) => ScopedVersionFolder(version, Suffix);
}
