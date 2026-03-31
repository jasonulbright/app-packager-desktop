using System.Text.Json;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Reads and writes stage-manifest.json files (schema v2).
/// Compatible with manifests produced by PS1 packagers.
/// </summary>
public sealed class ManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public void Write(string path, StageManifest manifest)
    {
        manifest.StagedAt = DateTimeOffset.Now;
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(path, json, System.Text.Encoding.UTF8);
    }

    public StageManifest Read(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Stage manifest not found", path);

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<StageManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize stage manifest");
    }
}
