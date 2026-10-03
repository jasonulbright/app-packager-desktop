namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>
/// The icon a profile inherits when it sets none, resolved the way Stage publishes one:
/// the icon pack entry named for the packager first, then the newest staged build's
/// app-icon.
/// </summary>
public static class WorkbenchInheritedIcon
{
    private static readonly string[] IconExtensions = [".ico", ".png"];

    /// <summary>
    /// The inherited icon file, or an empty string. Only an exact base-name match in the
    /// pack counts, .ico before .png. An application whose icon source is None takes a
    /// pack entry but never a staged icon. The stage search runs only under
    /// <paramref name="stageSearchRoot"/>, the application's own staging folder; without
    /// one the download root is never walked.
    /// </summary>
    public static string Resolve(string? packIconsDirectory, string? packBaseName, string? iconSource, string? stageSearchRoot)
    {
        if (string.IsNullOrWhiteSpace(packBaseName)) return "";

        if (!string.IsNullOrWhiteSpace(packIconsDirectory) && Directory.Exists(packIconsDirectory))
        {
            foreach (var extension in IconExtensions)
            {
                var candidate = Path.Combine(packIconsDirectory, packBaseName + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }

        if (string.Equals(ApplicationPackagerEngine.Services.IconExtractionService.NormalizeIconSource(iconSource), "None", StringComparison.Ordinal))
            return "";
        if (string.IsNullOrWhiteSpace(stageSearchRoot) || !Directory.Exists(stageSearchRoot)) return "";

        string? newest;
        try
        {
            newest = Directory.GetFiles(stageSearchRoot, "stage-manifest.json", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
        if (newest is null) return "";

        var stageFolder = Path.GetDirectoryName(newest)!;
        foreach (var extension in IconExtensions)
        {
            var staged = Path.Combine(stageFolder, "app-icon" + extension);
            if (File.Exists(staged)) return staged;
        }
        return "";
    }

    /// <summary>The Icon line of the Application page for an inherited icon.</summary>
    public static string InheritedStateText(string resolvedPath) => string.IsNullOrEmpty(resolvedPath)
        ? "Inherited from the packager. No staged icon or icon pack entry was found."
        : "Inherited from the packager: " + resolvedPath;
}
