using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The checks a value read from an installer passes before it is placed in a proposed command
/// line, and the check of a finished command-prompt command line.
/// </summary>
public static class ProposedCommand
{
    private static readonly Regex ProductCodePattern = new(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$", RegexOptions.CultureInvariant);

    // NuGet and Chocolatey package ids: letters, digits, dot, dash and underscore.
    private static readonly Regex NuGetIdPattern = new(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}$", RegexOptions.CultureInvariant);

    // A semantic or legacy four-part version with optional prerelease and build labels.
    private static readonly Regex NuGetVersionPattern = new(@"^[0-9]{1,9}(\.[0-9]{1,9}){0,3}(-[0-9A-Za-z][0-9A-Za-z.-]{0,63})?(\+[0-9A-Za-z][0-9A-Za-z.-]{0,63})?$", RegexOptions.CultureInvariant);

    // The AppX manifest schema type ST_PackageName.
    private static readonly Regex MsixNamePattern = new(@"^[-.A-Za-z0-9]{3,50}$", RegexOptions.CultureInvariant);

    public static bool IsProductCode(string value) => ProductCodePattern.IsMatch(value);

    public static bool IsNuGetId(string value) => NuGetIdPattern.IsMatch(value);

    public static bool IsNuGetVersion(string value) => NuGetVersionPattern.IsMatch(value);

    public static bool IsMsixName(string value) => MsixNamePattern.IsMatch(value);

    /// <summary>
    /// True when a command-prompt command line holds no control character, balanced double quotes
    /// and none of the operators &amp; | &lt; &gt; ^ ; ` outside quotes, so it runs exactly one program.
    /// </summary>
    public static bool IsSafeCommandLine(string command)
    {
        var quoted = false;
        foreach (var c in command)
        {
            if (c < 0x20) return false;
            if (c == '"') { quoted = !quoted; continue; }
            if (!quoted && c is '&' or '|' or '<' or '>' or '^' or ';' or '`') return false;
        }
        return !quoted;
    }
}
