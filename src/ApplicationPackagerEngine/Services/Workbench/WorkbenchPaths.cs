namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>Data root and shared path helpers for the workbench and its packager children.</summary>
public static class WorkbenchPaths
{
    /// <summary>Names the data root for a child packager process and overrides every other source.</summary>
    public const string DataRootVariable = "APP_PACKAGER_WORKBENCH_ROOT";

    /// <summary>
    /// The per-user default: a Workbench folder beside the desktop's settings.json,
    /// never under the install folder.
    /// </summary>
    public static string DefaultDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppPackager", "Workbench");

    /// <summary>
    /// Resolves the data root: APP_PACKAGER_WORKBENCH_ROOT, then the preference, then
    /// the per-user default. A child process launched with the variable resolves the
    /// same root as the process that launched it.
    /// </summary>
    public static string ResolveDataRoot(string? preference = null, bool create = true)
    {
        var root = Environment.GetEnvironmentVariable(DataRootVariable);
        if (string.IsNullOrWhiteSpace(root)) root = preference;
        if (string.IsNullOrWhiteSpace(root)) root = DefaultDataRoot;
        if (create) Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// The shared installer download cache for any download root. A per-profile
    /// download root (&lt;root&gt;\profiles\&lt;ProfileId&gt;) resolves back to
    /// &lt;root&gt;\_cache, so profiles never fork the cache.
    /// </summary>
    public static string GetSharedDownloadCacheRoot(string downloadRoot, bool create = true)
    {
        var full = Path.GetFullPath(downloadRoot).TrimEnd('\\');
        var parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent)
            && string.Equals(Path.GetFileName(parent), "profiles", StringComparison.OrdinalIgnoreCase))
        {
            full = (Path.GetDirectoryName(parent) ?? parent).TrimEnd('\\');
        }
        var cache = Path.Combine(full, "_cache");
        if (create) Directory.CreateDirectory(cache);
        return cache;
    }

    /// <summary>
    /// The staging download root for one profile: &lt;root&gt;\profiles\&lt;ProfileId&gt;.
    /// The default profile, and an empty root, keep the root unchanged.
    /// </summary>
    public static string GetProfileDownloadRoot(string downloadRoot, string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) || ApplicationPackagerEngine.Models.Workbench.ApplicationIds.IsDefaultProfile(profileId))
            return downloadRoot;
        if (string.IsNullOrWhiteSpace(downloadRoot)) return downloadRoot;
        return Path.Combine(downloadRoot, "profiles", profileId);
    }
}
