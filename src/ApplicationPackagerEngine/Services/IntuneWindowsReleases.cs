using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The minimum Windows release of an Intune Win32 app (<c>minimumSupportedWindowsRelease</c>).
/// The list is what the Settings page offers. Graph takes the value as a string, and the
/// reference gives only an example (<c>Windows11_23H2</c>), so the names in the list are
/// not checked against a tenant.
/// </summary>
public static partial class IntuneWindowsReleases
{
    /// <summary>The release that every Intune app got before the setting existed.</summary>
    public const string Default = "Windows10_21H2";

    /// <summary>The releases that the Settings page offers, oldest first.</summary>
    public static readonly IReadOnlyList<string> Known =
    [
        "Windows10_1607", "Windows10_1703", "Windows10_1709", "Windows10_1803", "Windows10_1809",
        "Windows10_1903", "Windows10_1909", "Windows10_2004", "Windows10_20H2", "Windows10_21H1",
        "Windows10_21H2", "Windows10_22H2",
        "Windows11_21H2", "Windows11_22H2", "Windows11_23H2", "Windows11_24H2", "Windows11_25H2"
    ];

    /// <summary>True for the shape Windows10_21H2 or Windows11_23H2: it keeps a stray value out of the app body.</summary>
    public static bool IsWellFormed(string? value) => value is not null && Shape().IsMatch(value);

    /// <summary>
    /// The value to send. An empty or malformed value gives the default. A value that the list
    /// knows gets its canonical spelling. A well-formed value that the list does not know goes
    /// through, so that a newer release is possible before the list has it; Graph judges it.
    /// </summary>
    public static string Resolve(string? stored)
    {
        var value = stored?.Trim();
        if (!IsWellFormed(value)) return Default;
        return Known.FirstOrDefault(k => k.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? value!;
    }

    [GeneratedRegex(@"^Windows(10|11)_[0-9A-Za-z]{2,8}$", RegexOptions.IgnoreCase)]
    private static partial Regex Shape();
}