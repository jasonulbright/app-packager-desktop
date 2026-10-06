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
    public static string DefaultDataRoot => Path.Combine(AppDataPaths.Root, "Workbench");

    /// <summary>User-authored JSON definitions and icon overrides alongside Workbench state.</summary>
    public static string UserDefinitionsRoot => Path.Combine(ResolveDataRoot(create: false), "definitions");

    /// <summary>
    /// Resolves the data root: APP_PACKAGER_WORKBENCH_ROOT, then the preference, then
    /// the per-user default. Explicit roots outside AppDataPaths.Root are scoped beneath
    /// Users\&lt;SID&gt;; a child process resolves an already-scoped root idempotently.
    /// </summary>
    public static string ResolveDataRoot(string? preference = null, bool create = true)
    {
        var root = Environment.GetEnvironmentVariable(DataRootVariable);
        if (string.IsNullOrWhiteSpace(root)) root = preference;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = DefaultDataRoot;
        }
        else
        {
            root = Path.GetFullPath(root);
            if (!AppDataPaths.IsWithin(root, AppDataPaths.Root))
                root = AppDataPaths.ScopeRootToCurrentUser(root);
        }
        root = AppDataPaths.EnsureOutsideProgramFiles(root);
        if (create)
        {
            Directory.CreateDirectory(root);
            if (AppDataPaths.IsWithin(root, AppDataPaths.Root))
                AppDataPaths.EnsureDirectoryIsNotReparsePoint(AppDataPaths.Root);
            AppDataPaths.EnsureDirectoryIsNotReparsePoint(root);
        }
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
        ApplicationPackagerEngine.Models.Workbench.ApplicationIds.AssertProfileId(profileId);
        return CombineUnder(downloadRoot, "profiles", profileId);
    }

    /// <summary>
    /// Path.Combine that refuses a result outside <paramref name="parent"/>:
    /// a rooted part discards everything before it and a dot segment climbs
    /// out, so the combined path is checked after it is resolved.
    /// </summary>
    public static string CombineUnder(string parent, params string[] parts)
    {
        var combined = Path.Combine([parent, .. parts]);
        var root = Path.GetFullPath(parent).TrimEnd('\\') + '\\';
        if (!Path.GetFullPath(combined).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The path '{combined}' resolves outside '{parent}'.");
        return combined;
    }
}
