using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>
/// Reads the version of an Intune Win32 app from its display name. Graph v1.0 has no display version on a Win32 app, and the title of
/// a package that supersedence applies to carries the version (the version in the title is locked on for it).
/// </summary>
public static partial class IntuneTitleVersion
{
    [GeneratedRegex(@"^\s*-?\s*v?(\d+(\.\d+)*)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex VersionAfterTitle();

    /// <summary>The version that follows the base title in the display name, or null when the name does not start with the base title or no version follows.</summary>
    public static string? Read(string displayName, string baseTitle)
    {
        var name = (displayName ?? "").Trim();
        var title = (baseTitle ?? "").Trim();
        if (title.Length == 0 || !name.StartsWith(title, StringComparison.OrdinalIgnoreCase)) return null;
        var match = VersionAfterTitle().Match(name[title.Length..]);
        return match.Success ? match.Groups[1].Value : null;
    }
}
