using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Compares a site version with a vendor version. A build or pre-release suffix after
/// '+' or '-' is dropped, a bare number such as "31" reads as 31.0, and only the parts
/// both sides carry are compared: an MSI that appends an internal build number
/// ("26.2.2.2") equals the vendor's "26.2.2".
/// </summary>
public static partial class VersionCompare
{
    [GeneratedRegex(@"[+-].*$")]
    private static partial Regex Suffix();

    [GeneratedRegex(@"^(\d+)$")]
    private static partial Regex BareNumber();

    /// <summary>A version string System.Version can parse, or the input unchanged when it is not one.</summary>
    public static string Normalize(string? value) =>
        BareNumber().Replace(Suffix().Replace((value ?? "").Trim(), ""), "$1.0");

    /// <summary>-1, 0 or 1. Either side unparsable compares as equal.</summary>
    public static int Compare(string? a, string? b)
    {
        if (!Version.TryParse(Normalize(a), out var va) || !Version.TryParse(Normalize(b), out var vb)) return 0;

        static int Count(Version v) => v.Revision >= 0 ? 4 : v.Build >= 0 ? 3 : 2;
        var common = Math.Min(Count(va), Count(vb));
        int[] aParts = [va.Major, va.Minor, Math.Max(va.Build, 0), Math.Max(va.Revision, 0)];
        int[] bParts = [vb.Major, vb.Minor, Math.Max(vb.Build, 0), Math.Max(vb.Revision, 0)];
        for (var i = 0; i < common; i++)
        {
            if (aParts[i] < bParts[i]) return -1;
            if (aParts[i] > bParts[i]) return 1;
        }
        return 0;
    }

    /// <summary>True when System.Version parses the value once a bare number is padded.</summary>
    public static bool TryParse(string? value, out Version version) =>
        Version.TryParse(BareNumber().Replace((value ?? "").Trim(), "$1.0"), out version!);
}
