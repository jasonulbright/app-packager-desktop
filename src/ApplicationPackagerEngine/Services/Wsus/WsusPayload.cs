using System.Security.Cryptography;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Wsus;

/// <summary>A payload path or byte check that refuses the publish.</summary>
public sealed class WsusPayloadException(string message) : Exception(message);

/// <summary>
/// The staged files a WSUS package carries, their location inside the
/// content folder, and the check that they are still the staged bytes.
/// </summary>
public static class WsusPayload
{
    /// <summary>
    /// The files every stage writes beside the installer for the other
    /// destinations; none of them belongs in a WSUS payload.
    /// </summary>
    public static readonly IReadOnlyList<string> GeneratedContentNames =
    [
        "install.bat", "install.ps1", "uninstall.bat", "uninstall.ps1",
        "install-generated.ps1", "uninstall-generated.ps1",
        "install-before.ps1", "install-after.ps1", "uninstall-before.ps1", "uninstall-after.ps1",
        "stage-manifest.json", "staged-version.txt", "app-icon.ico", "app-icon.png"
    ];

    /// <summary>The folder that holds the generated detection and requirement scripts.</summary>
    public const string GeneratedContentFolder = "scripts";

    /// <summary>A locally published update is one signed cab, and a cab cannot exceed this size.</summary>
    public const int CabHardLimitMegabytes = 2047;

    /// <summary>
    /// Every file the stage recorded except the generated ones, with the
    /// installer first. An installer reads companion files its arguments do
    /// not name (setup.ini, an external MSI cab, a transform), so the recorded
    /// stage content is the payload. Paths use backslashes and are relative to
    /// the content folder.
    /// </summary>
    public static IReadOnlyList<string> Files(StageManifest manifest)
    {
        var icon = manifest.Icon ?? "";
        var files = new List<string> { manifest.InstallerFile ?? "" };
        foreach (var entry in manifest.FileHashes ?? [])
        {
            if (entry is null) continue;
            var relative = Normalize(entry.RelativePath);
            if (string.IsNullOrWhiteSpace(relative)) continue;
            if (GeneratedContentNames.Contains(relative, StringComparer.OrdinalIgnoreCase)
                || (icon.Length > 0 && relative.Equals(icon, StringComparison.OrdinalIgnoreCase))) continue;
            if (relative.StartsWith(GeneratedContentFolder + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            if (!files.Contains(relative, StringComparer.OrdinalIgnoreCase)) files.Add(relative);
        }
        return files;
    }

    /// <summary>
    /// The full path of a recorded file. A rooted, drive-relative, stream, or
    /// parent-relative path would copy a file from elsewhere on this computer
    /// into a signed update, so it is refused.
    /// </summary>
    public static string ResolvePath(string contentFolder, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains(':') || Path.IsPathRooted(relativePath))
            throw new WsusPayloadException($"The staged file path '{relativePath}' is not a path inside the content folder.");
        var root = Path.GetFullPath(contentFolder).TrimEnd('\\');
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            throw new WsusPayloadException($"The staged file path '{relativePath}' points outside the content folder.");
        return full;
    }

    /// <summary>
    /// Refuses unless every payload file's SHA-256 equals the one the stage
    /// recorded: WSUS signs whatever it is handed.
    /// </summary>
    public static void AssertIntegrity(StageManifest manifest, string contentFolder, IReadOnlyList<string> files)
    {
        var recorded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.FileHashes ?? [])
        {
            if (entry is null) continue;
            recorded[Normalize(entry.RelativePath)] = entry.Sha256 ?? "";
        }
        if (recorded.Count == 0)
            throw new WsusPayloadException("The stage manifest records no file hashes; re-run the Stage phase before publishing to WSUS.");
        foreach (var file in files)
        {
            if (!recorded.TryGetValue(file, out var expected) || string.IsNullOrEmpty(expected))
                throw new WsusPayloadException($"The stage manifest records no hash for '{file}'; re-run the Stage phase.");
            using var stream = File.OpenRead(ResolvePath(contentFolder, file));
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(expected.ToUpperInvariant(), StringComparison.Ordinal))
                throw new WsusPayloadException($"'{file}' changed after it was staged (SHA-256 mismatch); re-run the Stage phase.");
        }
    }

    /// <summary>The total size of the payload files that exist, in megabytes.</summary>
    public static double Megabytes(string contentFolder, IEnumerable<string> files)
    {
        long bytes = 0;
        foreach (var file in files)
        {
            string path;
            try { path = ResolvePath(contentFolder, file); }
            catch (WsusPayloadException) { continue; }
            if (File.Exists(path)) bytes += new FileInfo(path).Length;
        }
        return bytes / (1024.0 * 1024.0);
    }

    private static string Normalize(string? relativePath) =>
        (relativePath ?? "").Replace('/', '\\').TrimStart('\\');
}
