namespace ApplicationPackagerEngine.Models;

/// <summary>
/// Folder shapes under &lt;share&gt;\Applications. Nested is Vendor\App\Version;
/// Flat is one level named Vendor-App-Version. The token is stored, not display text.
/// </summary>
public static class ContentLayouts
{
    public const string Nested = "Nested";
    public const string Flat = "Flat";

    /// <summary>Maps an unknown or absent stored value onto Nested.</summary>
    public static string Normalize(string? value) =>
        string.Equals(value, Flat, StringComparison.OrdinalIgnoreCase) ? Flat : Nested;

    public static bool IsFlat(string? value) => Normalize(value) == Flat;

    /// <summary>
    /// The content version folder for one application: the parent of this folder is
    /// where a .intunewin built beside the content is stored under either layout.
    /// </summary>
    public static string ContentPath(string fileServerPath, string layout, string vendorFolder, string appFolder, string version) =>
        IsFlat(layout)
            ? Path.Combine(fileServerPath, "Applications", TrimSegment($"{vendorFolder}-{appFolder}-{version}"))
            : Path.Combine(fileServerPath, "Applications", TrimSegment(vendorFolder), TrimSegment(appFolder), version);

    /// <summary>
    /// A folder segment without trailing dots and spaces. Windows strips them when it
    /// creates a directory, but the site resolves the stored path through \\?\UNC\,
    /// which does not, and the deployment type then distributes as 0x80070003.
    /// </summary>
    public static string TrimSegment(string segment) => segment.TrimEnd('.', ' ');
}
