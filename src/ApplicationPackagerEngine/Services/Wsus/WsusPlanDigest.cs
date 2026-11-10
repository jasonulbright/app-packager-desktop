using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.Wsus;

/// <summary>
/// A WSUS update cannot change after it is published. Each update that App Packager creates carries a line with a short digest
/// of what it does: the command line, the installer, the rules, the return codes and the hashes of the files. A repeat publish
/// of the same version compares the digest of its stage with the line of the update on the server.
/// </summary>
public static partial class WsusPlanDigest
{
    private const string Marker = "AppPackager plan: ";

    [GeneratedRegex(@"(?m)^AppPackager plan: (?<digest>[0-9a-f]{16})[ \t]*\r?$")]
    private static partial Regex LinePattern();

    /// <summary>The first 16 hex characters of the SHA-256 of the inputs, each on its own line so that no value runs into the next.</summary>
    public static string Compute(string commandLine, string installerType, string installerFile, string isInstalled, string isInstallable,
        IEnumerable<(int Code, string Result, bool Reboot)> returnCodes, IEnumerable<(string Path, string Hash)> payloadFiles)
    {
        var text = string.Join("\n",
            "command:" + commandLine,
            "type:" + installerType,
            "file:" + installerFile,
            "installed:" + isInstalled,
            "installable:" + isInstallable,
            "codes:" + string.Join(",", returnCodes.Select(c => $"{c.Code}={c.Result}/{(c.Reboot ? "reboot" : "none")}")),
            "files:" + string.Join(",", payloadFiles.Select(f => $"{f.Path}={f.Hash}")));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    [GeneratedRegex(@"(?m)^# SIG # Begin signature block[ \t]*\r?$")]
    private static partial Regex SignatureBeginLine();

    /// <summary>
    /// The hash of a PowerShell script that does not depend on its signature. App Packager signs its scripts again at each staging, and
    /// a timestamped signature has other bytes each time, so the hash of the file would change although the script did not. The text
    /// before the signature block is hashed, without trailing white space; an edit of the script body changes it.
    /// </summary>
    public static string ScriptHash(string text)
    {
        // The signature block is the last thing in the file, and its first line starts a line. A script that names the marker in its
        // own text (inside a string, for example) is cut at the last line that is the marker, not at the first mention.
        var matches = SignatureBeginLine().Matches(text);
        var body = (matches.Count > 0 ? text[..matches[^1].Index] : text).TrimEnd();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
    }

    /// <summary>True for the files whose hash is taken without the signature: the scripts and modules that App Packager signs.</summary>
    public static bool IsSignedScript(string path) =>
        path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".psm1", StringComparison.OrdinalIgnoreCase);

    /// <summary>The description line for a digest.</summary>
    public static string Line(string digest) => Marker + digest;

    /// <summary>The digest in a description, or null when the update carries none (it was published by an earlier release).</summary>
    public static string? Parse(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var match = LinePattern().Match(description);
        return match.Success ? match.Groups["digest"].Value : null;
    }
}