using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Where the .intunewin of a staged content folder lives, and whether an
/// existing one still matches the stage. The file sits beside the content
/// folder as &lt;AppName&gt;-&lt;SoftwareVersion&gt;.intunewin.
/// </summary>
public static class IntuneWinArtifact
{
    public static string FileName(StageManifest manifest)
    {
        var raw = $"{manifest.AppName}-{manifest.SoftwareVersion}";
        foreach (var c in Path.GetInvalidFileNameChars())
            raw = raw.Replace(c, '_');
        return raw.Replace(' ', '_') + ".intunewin";
    }

    /// <summary>The folder the content prep tool writes to: the parent of the staged content folder.</summary>
    public static string OutputFolder(string stagedContentPath) =>
        Path.GetDirectoryName(stagedContentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        ?? throw new InvalidOperationException($"Staged content folder has no parent: {stagedContentPath}");

    public static string PathFor(StageManifest manifest, string stagedContentPath) =>
        Path.Combine(OutputFolder(stagedContentPath), FileName(manifest));

    /// <summary>
    /// Where a ConfigMgr run stores the .intunewin on the share, beside the network content
    /// version folder and never inside it. Nested: &lt;Vendor&gt;\&lt;App&gt;\&lt;AppName&gt;-&lt;Version&gt;.intunewin,
    /// in the application's own folder. Flat: &lt;Vendor-App-Version&gt;.intunewin beside the
    /// folder of the same name, because the parent there is the Applications folder every
    /// application shares. Null when the content path has no parent.
    /// </summary>
    public static string? NetworkPathFor(StageManifest manifest, string networkContentPath, string? contentLayout)
    {
        var folder = networkContentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(folder);
        if (string.IsNullOrEmpty(parent)) return null;
        return ContentLayouts.IsFlat(contentLayout)
            ? Path.Combine(parent, Path.GetFileName(folder) + ".intunewin")
            : Path.Combine(parent, FileName(manifest));
    }

    /// <summary>
    /// The .intunewin of this stage when one exists and was written after the
    /// stage manifest; null otherwise. An older file belongs to an earlier
    /// stage of the same version and is rebuilt.
    /// </summary>
    public static string? Reusable(StageManifest manifest, string stagedContentPath)
    {
        var path = PathFor(manifest, stagedContentPath);
        var manifestPath = Path.Combine(stagedContentPath, "stage-manifest.json");
        if (!File.Exists(path) || !File.Exists(manifestPath)) return null;
        return File.GetLastWriteTimeUtc(path) > File.GetLastWriteTimeUtc(manifestPath) ? path : null;
    }
}
