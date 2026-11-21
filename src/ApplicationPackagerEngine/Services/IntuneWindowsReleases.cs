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

    /// <summary>The build number of each release in <see cref="Known"/>, in the same order (Microsoft Learn, Windows 10 and Windows 11 release information).</summary>
    private static readonly int[] Builds =
    [
        14393, 15063, 16299, 17134, 17763,
        18362, 18363, 19041, 19042, 19043,
        19044, 19045,
        22000, 22621, 22631, 26100, 26200
    ];

    /// <summary>The position of a release in <see cref="Known"/>, oldest first; -1 when the list does not know it.</summary>
    public static int Rank(string? release)
    {
        for (var i = 0; release is not null && i < Known.Count; i++)
            if (Known[i].Equals(release.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The release whose build number is exactly the build; null when the build is not the build of a known release.</summary>
    public static string? OfBuild(long build)
    {
        if (build < 1 || build > int.MaxValue) return null;
        var index = Array.IndexOf(Builds, (int)build);
        return index < 0 ? null : Known[index];
    }

    /// <summary>
    /// The later of two releases. A release that the list does not know (a newer one that a person typed) is kept as it is,
    /// because the order of an unknown release cannot be judged.
    /// </summary>
    public static string Later(string current, string required)
    {
        var a = Rank(current);
        var b = Rank(required);
        return a < 0 ? current : b > a ? Known[b] : current;
    }

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