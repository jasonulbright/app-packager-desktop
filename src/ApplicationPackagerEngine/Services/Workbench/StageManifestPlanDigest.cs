using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>
/// SHA-256 over the canonical JSON of a stage manifest's resolved plan. FileHashes
/// covers the payload bytes but not the manifest that records them, so a
/// metadata-only change (commands, detection, timing, signing status) needs this
/// separate seal. FileHashes, PlanDigest and StagedAt are excluded: the first is
/// already covered, the second is the output, and the third changes on every run.
/// The digest is uppercase hex and equals the one a PowerShell packager computes
/// for the same manifest content.
/// </summary>
public static class StageManifestPlanDigest
{
    public static readonly IReadOnlyList<string> ExcludedFields = ["FileHashes", "PlanDigest", "StagedAt"];

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    /// <summary>The digest of a manifest document, such as one read from stage-manifest.json.</summary>
    public static string Compute(JsonObject manifest) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalText(manifest))));

    /// <summary>The digest of the document this manifest serializes to.</summary>
    public static string Compute(StageManifest manifest) =>
        Compute(JsonSerializer.SerializeToNode(manifest, SerializerOptions)!.AsObject());

    /// <summary>The exact text the digest hashes, for diagnosing a digest mismatch.</summary>
    public static string CanonicalText(JsonObject manifest)
    {
        var copy = new JsonObject();
        foreach (var (name, value) in manifest)
        {
            if (ExcludedFields.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            // The PowerShell digest copies each top-level value through an if-expression,
            // which unrolls one array level: an empty top-level array hashes as null,
            // while an empty nested array hashes as an empty object.
            copy[name] = value is JsonArray { Count: 0 } ? null : value?.DeepClone();
        }
        return PowerShellJson.SerializeCanonical(copy);
    }
}
