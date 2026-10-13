using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Compares the version a site or the history holds (A) with the version a vendor
/// reports (B). A build or pre-release suffix after '+' or '-' and a trailing channel
/// word ("140.17.0esr") are dropped, a fifth number is dropped, and a bare number such
/// as "31" reads as 31.0. Every part B carries is compared and a part A lacks counts as
/// zero, so "8.9.8.1" after "8.9.8" is an update. Parts A carries beyond B are not
/// compared: an MSI that appends an internal build number ("26.2.2.2") equals the
/// vendor's "26.2.2".
/// </summary>
public static partial class VersionCompare
{
    [GeneratedRegex(@"[+-].*$")]
    private static partial Regex Suffix();

    [GeneratedRegex(@"[A-Za-z]+$")]
    private static partial Regex ChannelWord();

    [GeneratedRegex(@"^((\d+\.){3}\d+)\..*$")]
    private static partial Regex BeyondFourParts();

    [GeneratedRegex(@"^(\d+)$")]
    private static partial Regex BareNumber();

    /// <summary>A version string System.Version can parse, or the input unchanged when it is not one.</summary>
    public static string Normalize(string? value)
    {
        var text = Suffix().Replace((value ?? "").Trim(), "");
        text = ChannelWord().Replace(text, "");
        text = BeyondFourParts().Replace(text, "$1");
        return BareNumber().Replace(text, "$1.0");
    }

    /// <summary>-1, 0 or 1. Either side unparsable compares as equal.</summary>
    public static int Compare(string? a, string? b) => Compare(a, b, strict: false);

    /// <summary>
    /// -1, 0 or 1 over all four parts of both sides, for two versions from the same
    /// vendor feed. Either side unparsable compares as equal.
    /// </summary>
    public static int CompareStrict(string? a, string? b) => Compare(a, b, strict: true);

    private static int Compare(string? a, string? b, bool strict)
    {
        if (!Version.TryParse(Normalize(a), out var va) || !Version.TryParse(Normalize(b), out var vb)) return 0;

        var count = strict ? 4 : vb.Revision >= 0 ? 4 : vb.Build >= 0 ? 3 : 2;
        int[] aParts = [va.Major, va.Minor, Math.Max(va.Build, 0), Math.Max(va.Revision, 0)];
        int[] bParts = [vb.Major, vb.Minor, Math.Max(vb.Build, 0), Math.Max(vb.Revision, 0)];
        for (var i = 0; i < count; i++)
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
