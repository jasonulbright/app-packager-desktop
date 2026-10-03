using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Reads and writes stage-manifest.json files (schema v4).
/// Compatible with manifests produced by PS1 packagers; v2 manifests
/// read cleanly and simply carry no hash list, and a manifest newer
/// than v4 is refused.
/// </summary>
public sealed class ManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Writes the manifest with a recursive FileHashes record of every
    /// file under its folder (the manifest itself excluded), then
    /// verifies the record it just wrote so a stage never completes
    /// with an unreadable or shifting payload.
    /// </summary>
    public void Write(string path, StageManifest manifest)
    {
        manifest.StagedAt = DateTimeOffset.Now;
        manifest.SchemaVersion = StageManifest.CurrentSchemaVersion;
        var root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var manifestName = Path.GetFileName(path);
        manifest.FileHashes = ComputeFileHashes(root, manifestName);
        manifest.PlanDigest = StageManifestPlanDigest.Compute(manifest);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(path, json, System.Text.Encoding.UTF8);

        var verification = VerifyFileHashes(root, manifest.FileHashes, manifestName);
        if (verification.Count > 0)
            throw new InvalidOperationException(
                $"Stage integrity verification failed: {string.Join("; ", verification)}");
    }

    /// <summary>Ordered integrity records for every file under root.</summary>
    public static StageFileHash[] ComputeFileHashes(string root, string excludeName)
    {
        var rootFull = Path.GetFullPath(root);
        return Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories)
            .Where(f => !string.Equals(Path.GetFileName(f), excludeName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f =>
            {
                using var stream = File.OpenRead(f);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                return new StageFileHash
                {
                    RelativePath = Path.GetRelativePath(rootFull, f),
                    Sha256 = hash,
                    Size = new FileInfo(f).Length
                };
            })
            .ToArray();
    }

    /// <summary>
    /// Compares a folder tree to expected hash records. Returns an empty
    /// list on pass, otherwise one message per mismatch/missing/extra
    /// file. A null/empty expected list passes (pre-v3 manifest).
    /// </summary>
    public static List<string> VerifyFileHashes(string root, StageFileHash[]? expected, string excludeName)
    {
        var problems = new List<string>();
        if (expected is null || expected.Length == 0) return problems;

        var actual = ComputeFileHashes(root, excludeName)
            .ToDictionary(h => h.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var exp in expected)
        {
            if (!actual.TryGetValue(exp.RelativePath, out var act))
                problems.Add($"missing: {exp.RelativePath}");
            else if (!string.Equals(act.Sha256, exp.Sha256, StringComparison.OrdinalIgnoreCase))
                problems.Add($"hash mismatch: {exp.RelativePath}");
        }
        var expectedSet = expected.Select(h => h.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        problems.AddRange(actual.Keys.Where(k => !expectedSet.Contains(k)).Select(k => $"unexpected file: {k}"));
        return problems;
    }

    public StageManifest Read(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Stage manifest not found", path);

        var json = File.ReadAllText(path);
        var manifest = JsonSerializer.Deserialize<StageManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize stage manifest");

        // Fields a newer build added would be dropped here, and a dropped detection
        // or signing field publishes the wrong thing.
        if (manifest.SchemaVersion > StageManifest.CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"Stage manifest schema version {manifest.SchemaVersion} is newer than this AppPackager understands " +
                $"(maximum {StageManifest.CurrentSchemaVersion}): {path}. Update AppPackager, or re-run the Stage phase with this version.");
        return manifest;
    }
}
