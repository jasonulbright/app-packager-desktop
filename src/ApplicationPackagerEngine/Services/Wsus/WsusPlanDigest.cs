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
        IEnumerable<(int Code, string Result, bool Reboot)> returnCodes, IEnumerable<string> payloadHashes)
    {
        var text = string.Join("\n",
            "command:" + commandLine,
            "type:" + installerType,
            "file:" + installerFile,
            "installed:" + isInstalled,
            "installable:" + isInstallable,
            "codes:" + string.Join(",", returnCodes.Select(c => $"{c.Code}={c.Result}/{(c.Reboot ? "reboot" : "none")}")),
            "files:" + string.Join(",", payloadHashes));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

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