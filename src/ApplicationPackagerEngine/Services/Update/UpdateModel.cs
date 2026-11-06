using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.Update;

/// <summary>One file of a GitHub release.</summary>
public sealed record UpdateAsset(string Name, string Url, long Size);

/// <summary>The latest release as the update check needs it.</summary>
public sealed record UpdateRelease(string Tag, string Version, string PageUrl, string Notes, IReadOnlyList<UpdateAsset> Assets);

public enum UpdateStatus
{
    UpToDate,
    Available,
    Offline,
    RateLimited,
    Failed
}

/// <summary>The answer of one update check. Only Available carries a release.</summary>
public sealed record UpdateCheck(UpdateStatus Status, string Current, UpdateRelease? Release, string Message, DateTimeOffset? RetryAfter = null);

/// <summary>The two files an update needs: the Setup exe and the checksum list.</summary>
public sealed record UpdatePackage(UpdateAsset Setup, UpdateAsset Checksums);

/// <summary>The signature facts of a file, as read from Windows. Status is the Authenticode status name.</summary>
public sealed record SignatureInfo(string Status, string Subject, bool HasTimestamp);

/// <summary>An installer that passed the checksum and signature checks.</summary>
public sealed record VerifiedUpdate(string Path, string Version, string Sha256, string Subject);

/// <summary>The update was refused: a file is missing, changed or not signed as expected.</summary>
public sealed class UpdateRefusedException(string message) : Exception(message);

/// <summary>Version rules for the release tags (v2026.1103.3) and the running app.</summary>
public static partial class UpdateVersion
{
    [GeneratedRegex(@"^v?(\d+(?:\.\d+){1,3})$")]
    private static partial Regex TagPattern();

    /// <summary>'v2026.1103.3' gives '2026.1103.3'. Anything else, a pre-release tag too, gives null.</summary>
    public static string? FromTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var match = TagPattern().Match(tag.Trim());
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// The running version without build metadata: '2026.1103.3+abc' gives '2026.1103.3'.
    /// A value that is not a version gives null.
    /// </summary>
    public static string? FromRunning(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return null;
        var core = informational.Split('+', '-')[0].Trim();
        return FromTag(core);
    }

    /// <summary>
    /// -1, 0 or 1. The parts compare as numbers, so '2026.1103.3' equals '2026.1103.3.0'. A side that is
    /// not a version counts as the lowest; two such sides are equal.
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        var left = Parts(a);
        var right = Parts(b);
        if (left is null && right is null) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var x = i < left.Length ? left[i] : 0;
            var y = i < right.Length ? right[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }

    private static long[]? Parts(string? version)
    {
        var core = FromTag(version);
        if (core is null) return null;
        var parts = core.Split('.');
        var numbers = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!long.TryParse(parts[i], out numbers[i])) return null;
        return numbers;
    }
}

/// <summary>Chooses the two release files an update needs, and only from this repository's release downloads.</summary>
public static class UpdateAssetSelector
{
    public const string ChecksumsName = "checksums.txt";

    public static string SetupName(string version) => $"AppPackagerDesktop-Setup-{version}.exe";

    /// <summary>
    /// The Setup exe and checksums.txt of the release, matched by exact name. A file whose address is not an
    /// https download of the repository's releases does not count.
    /// </summary>
    public static bool TrySelect(UpdateRelease release, string repo, out UpdatePackage? package, out string reason)
    {
        package = null;
        var setup = Find(release, SetupName(release.Version), repo);
        var sums = Find(release, ChecksumsName, repo);
        if (setup is null) { reason = $"The release {release.Tag} has no {SetupName(release.Version)}."; return false; }
        if (sums is null) { reason = $"The release {release.Tag} has no {ChecksumsName}."; return false; }
        package = new UpdatePackage(setup, sums);
        reason = "";
        return true;
    }

    private static UpdateAsset? Find(UpdateRelease release, string name, string repo)
    {
        var prefix = $"https://github.com/{repo}/releases/download/";
        var matches = release.Assets
            .Where(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase) && a.Url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}

/// <summary>Reads the SHA-256 list of a release (sha256sum format: hash, space, optional star, file name).</summary>
public static partial class UpdateChecksums
{
    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?(.+?)\s*$")]
    private static partial Regex LinePattern();

    /// <summary>The lower-case hash of the file. Null when the list has no entry or two different entries for it.</summary>
    public static string? Find(string text, string fileName)
    {
        string? found = null;
        foreach (var line in text.Split('\n'))
        {
            var match = LinePattern().Match(line.TrimEnd('\r'));
            if (!match.Success || !string.Equals(match.Groups[2].Value, fileName, StringComparison.OrdinalIgnoreCase)) continue;
            var hash = match.Groups[1].Value.ToLowerInvariant();
            if (found is not null && found != hash) return null;
            found = hash;
        }
        return found;
    }

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

/// <summary>The signature rule of an installer: Valid, with a timestamp, and the expected common name as signer.</summary>
public static class UpdateSignaturePolicy
{
    public const string SignerCommonName = "Jason Ulbright";

    private const string CommonNameOid = "2.5.4.3";

    /// <summary>Null when the signature passes; otherwise the reason it is refused.</summary>
    public static string? Evaluate(SignatureInfo? info, string expectedCommonName = SignerCommonName)
    {
        if (info is null) return "The signature could not be read.";
        if (!string.Equals(info.Status, "Valid", StringComparison.OrdinalIgnoreCase)) return $"The signature status is {info.Status}, not Valid.";
        if (!info.HasTimestamp) return "The signature has no timestamp.";
        if (!HasCommonName(info.Subject, expectedCommonName)) return $"The signer is '{info.Subject}', not CN={expectedCommonName}.";
        return null;
    }

    private static bool HasCommonName(string subject, string expected)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;
        try
        {
            foreach (var rdn in new X500DistinguishedName(subject).EnumerateRelativeDistinguishedNames())
            {
                if (rdn.HasMultipleElements) continue;
                if (rdn.GetSingleElementType().Value == CommonNameOid &&
                    string.Equals(rdn.GetSingleElementValue(), expected, StringComparison.Ordinal))
                    return true;
            }
        }
        catch (CryptographicException)
        {
            return false;
        }
        return false;
    }
}
