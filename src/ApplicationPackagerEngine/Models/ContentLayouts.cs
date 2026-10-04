namespace ApplicationPackagerEngine.Models;

/// <summary>
/// Folder shapes under &lt;share&gt;\Applications. Nested is Vendor\App\Version;
/// Flat is one level named Vendor-App-Version. The token is stored, not display text.
/// </summary>
public static class ContentLayouts
{
    public const string Nested = "Nested";
    public const string Flat = "Flat";

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"
    };

    /// <summary>Maps an unknown or absent stored value onto Nested.</summary>
    public static string Normalize(string? value) =>
        string.Equals(value, Flat, StringComparison.OrdinalIgnoreCase) ? Flat : Nested;

    public static bool IsFlat(string? value) => Normalize(value) == Flat;

    /// <summary>
    /// The content version folder for one application: the parent of this folder is
    /// where a .intunewin built beside the content is stored under either layout.
    /// </summary>
    public static string ContentPath(string fileServerPath, string layout, string vendorFolder, string appFolder, string version)
    {
        AssertSegment(vendorFolder, nameof(vendorFolder));
        AssertSegment(appFolder, nameof(appFolder));
        AssertSegment(version, nameof(version));

        var applicationsRoot = Path.GetFullPath(Path.Combine(fileServerPath, "Applications"));
        var candidate = IsFlat(layout)
            ? Path.Combine(applicationsRoot, TrimSegment($"{vendorFolder}-{appFolder}-{version}"))
            : Path.Combine(applicationsRoot, TrimSegment(vendorFolder), TrimSegment(appFolder), TrimSegment(version));
        var fullPath = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(applicationsRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == "." || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("The content path must remain under the configured Applications folder.");
        return fullPath;
    }

    /// <summary>
    /// A folder segment without trailing dots and spaces. Windows strips them when it
    /// creates a directory, but the site resolves the stored path through \\?\UNC\,
    /// which does not, and the deployment type then distributes as 0x80070003.
    /// </summary>
    public static string TrimSegment(string segment)
    {
        AssertSegment(segment, nameof(segment));
        var trimmed = segment.TrimEnd('.', ' ');
        return trimmed;
    }

    private static void AssertSegment(string segment, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(segment)
            || Path.IsPathRooted(segment)
            || segment.IndexOfAny(['\\', '/']) >= 0
            || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Content folder names must be non-empty, plain Windows folder names.", parameterName);

        var normalized = segment.TrimEnd('.', ' ');
        if (normalized.Length == 0)
            throw new ArgumentException("Content folder names cannot consist only of dots and spaces.", parameterName);
        var stem = normalized.Split('.')[0].TrimEnd(' ');
        if (DeviceNames.Contains(stem))
            throw new ArgumentException($"Content folder name '{segment}' is a reserved Windows device name.", parameterName);
    }
}
