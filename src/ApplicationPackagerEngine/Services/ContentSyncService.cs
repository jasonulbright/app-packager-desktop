using System.Security.Cryptography;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>Relative paths touched by one synchronization, by disposition.</summary>
public sealed class ContentSyncResult
{
    public List<string> Copied { get; } = [];
    public List<string> Refreshed { get; } = [];
    public List<string> Unchanged { get; } = [];

    /// <summary>True when the manifest carried no FileHashes, so hashes came from the source.</summary>
    public bool UsedSourceHashes { get; init; }
}

/// <summary>
/// Mirrors a local stage onto the content share. Presence at the destination is not
/// proof of currency: re-staging the same SoftwareVersion after editing a wrapper
/// produces a file of the same name and a different body, so an existing destination
/// file is compared by SHA-256 and overwritten on mismatch. Identical files are left
/// alone, which keeps a repackage off the wire for payloads that did not change.
/// </summary>
public static class ContentSyncService
{
    public const string ManifestFileName = "stage-manifest.json";

    /// <summary>SHA-256 of one file as an uppercase hex string.</summary>
    public static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Verifies a tree against a manifest's integrity record. An empty list is a pass;
    /// a manifest with no FileHashes is a pre-v3 stage and passes without checking.
    /// </summary>
    public static List<string> Verify(string root, StageManifest? manifest) =>
        ManifestService.VerifyFileHashes(root, manifest?.FileHashes, ManifestFileName);

    /// <summary>
    /// Copies files the destination lacks and refreshes those whose content differs.
    /// The expected hash for a path comes from the manifest when it carries one and
    /// from the local file otherwise, so a pre-v3 stage still refreshes correctly.
    /// The root stage-manifest.json is never copied; a manifest nested deeper is
    /// ordinary content.
    /// </summary>
    public static ContentSyncResult Sync(
        string localContentPath,
        string networkContentPath,
        StageManifest? manifest,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var localRoot = Path.GetFullPath(localContentPath);
        if (!Directory.Exists(localRoot))
            throw new DirectoryNotFoundException($"Staged content not found: {localRoot}");

        var expected = BuildExpectedMap(manifest);
        var result = new ContentSyncResult { UsedSourceHashes = expected.Count == 0 };

        Directory.CreateDirectory(networkContentPath);

        var files = Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(localRoot, file);
            if (relativePath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            var dest = Path.Combine(networkContentPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            if (!File.Exists(dest))
            {
                File.Copy(file, dest);
                onProgress?.Invoke($"Copied to network: {relativePath}");
                result.Copied.Add(relativePath);
                continue;
            }

            var want = expected.TryGetValue(relativePath, out var recorded) && !string.IsNullOrWhiteSpace(recorded)
                ? recorded
                : FileSha256(file);

            if (string.Equals(FileSha256(dest), want, StringComparison.OrdinalIgnoreCase))
            {
                onProgress?.Invoke($"Already on network: {relativePath}");
                result.Unchanged.Add(relativePath);
            }
            else
            {
                File.Copy(file, dest, overwrite: true);
                onProgress?.Invoke($"Refreshed on network: {relativePath}");
                result.Refreshed.Add(relativePath);
            }
        }

        return result;
    }

    /// <summary>
    /// The files under <paramref name="root"/> that the manifest does not list, with the
    /// same exclusion <see cref="Verify"/> applies; empty for a manifest without
    /// FileHashes or a folder that does not exist. A sync never removes them, so the
    /// verification after the sync would refuse them after the share was written.
    /// </summary>
    public static List<string> UnexpectedFiles(string root, StageManifest? manifest)
    {
        if (manifest?.FileHashes is not { Length: > 0 } || !Directory.Exists(root)) return [];
        var expected = BuildExpectedMap(manifest);
        var rootFull = Path.GetFullPath(root);
        return Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories)
            .Where(f => !string.Equals(Path.GetFileName(f), ManifestFileName, StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(rootFull, f))
            .Where(relative => !expected.ContainsKey(relative))
            .OrderBy(relative => relative, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, string> BuildExpectedMap(StageManifest? manifest)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest?.FileHashes ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry?.RelativePath)) continue;
            map[entry.RelativePath.TrimStart('\\', '/').Replace('/', '\\')] = entry.Sha256 ?? "";
        }
        return map;
    }
}
