using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ApplicationPackagerEngine.Models.Workbench;

/// <summary>
/// The immutable per-build snapshot (runs\&lt;BuildId&gt;.json). It freezes the profile,
/// every asset path resolved at snapshot time, the run overrides, the signing policy
/// and the target. Run overrides live only here and in the build record; they are
/// never written back into the profile. Property names match the file a PS1 packager
/// child reads through APP_PACKAGER_RUN_SNAPSHOT.
/// </summary>
public sealed class RunSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string BuildId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string ApplicationId { get; set; } = "";
    public string ProfileId { get; set; } = ApplicationIds.DefaultProfileId;
    public int ProfileRevision { get; set; }
    public JsonObject Profile { get; set; } = [];
    /// <summary>Asset id, or "linked:" plus the provenance path, to the file this build reads.</summary>
    public Dictionary<string, string> AssetPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public JsonObject RunOverrides { get; set; } = [];
    /// <summary>The signing policy as given; the signing service owns its shape.</summary>
    public JsonNode? SigningPolicy { get; set; }
    public string Target { get; set; } = RunTargets.Mecm;
    public string? DataRoot { get; set; }
    public string? DownloadRoot { get; set; }
    public string? PackagerScriptPath { get; set; }

    /// <summary>The snapshot file this object was written to or read from.</summary>
    [JsonIgnore]
    public string? Path { get; set; }
}

/// <summary>The targets a run snapshot records.</summary>
public static class RunTargets
{
    public const string ContentOnly = "ContentOnly";
    public const string Mecm = "MECM";
    public const string MecmAndIntune = "MECMAndIntune";
    public const string IntuneOnly = "IntuneOnly";
    public const string MecmAndWsus = "MECMAndWSUS";
    public const string WsusOnly = "WSUSOnly";

    public static readonly IReadOnlyList<string> All =
        [ContentOnly, Mecm, MecmAndIntune, IntuneOnly, MecmAndWsus, WsusOnly];

    /// <summary>The canonical spelling of a known target, or null for an unknown one.</summary>
    public static string? Normalize(string? target) =>
        All.FirstOrDefault(t => string.Equals(t, target, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Inputs for one run snapshot.</summary>
public sealed class RunSnapshotRequest
{
    public required string ApplicationId { get; init; }
    public string ProfileId { get; init; } = ApplicationIds.DefaultProfileId;
    public string Target { get; init; } = RunTargets.Mecm;
    /// <summary>Sparse run-level overrides keyed by effective-settings field name.</summary>
    public JsonObject? RunOverrides { get; init; }
    public JsonNode? SigningPolicy { get; init; }
    public string? PackagerScriptPath { get; init; }
    public string? DownloadRoot { get; init; }
    /// <summary>Null or empty creates a new build id.</summary>
    public string? BuildId { get; init; }
}

/// <summary>One file copied into managed profile storage, or recorded by provenance only when linked.</summary>
public sealed record ProfileAsset(
    string AssetId,
    string FileName,
    string Sha256,
    long Size,
    string Provenance,
    bool Linked,
    string Path,
    string AddedAt);
