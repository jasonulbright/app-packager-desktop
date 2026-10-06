using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ApplicationPackagerEngine.Models.Workbench;

/// <summary>
/// The stored record for one application (application.json). An application with
/// no stored record reads as an unsaved definition on the default profile.
/// </summary>
public sealed class WorkbenchApplicationDefinition
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string ApplicationId { get; set; } = "";
    public string? Origin { get; set; }
    public string? DisplayName { get; set; }
    public string? Publisher { get; set; }
    public string? Description { get; set; }
    public string? ProviderId { get; set; }
    /// <summary>Discovered for a catalog or custom application, Manual for a BYO application.</summary>
    public string? UpdatePolicy { get; set; }
    public string ActiveProfileId { get; set; } = ApplicationIds.DefaultProfileId;
    /// <summary>BYO source revisions; empty for every other origin.</summary>
    public JsonArray Sources { get; set; } = [];

    /// <summary>Fields written by a newer build, kept so a save does not drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>True when the record was read from application.json.</summary>
    [JsonIgnore]
    public bool Persisted { get; set; }
}

/// <summary>One application the workbench can edit, from discovery or from the store.</summary>
public sealed record WorkbenchApplication(
    string ApplicationId,
    string ApplicationKey,
    ApplicationOrigin Origin,
    string DisplayName,
    string? Publisher,
    string? SourcePath,
    bool Runnable,
    string ActiveProfileId,
    string? UpdatePolicy,
    bool Persisted);

/// <summary>Where discovery looks for applications that have no stored record yet.</summary>
public sealed class WorkbenchDiscoveryRoots
{
    /// <summary>The shipped Definitions folder; each JSON file's Id becomes catalog:&lt;Id&gt;.</summary>
    public string? CatalogDefinitionsRoot { get; init; }

    /// <summary>User definition files that replace a shipped definition with the same Id.</summary>
    public string? CatalogOverridesRoot { get; init; }

    /// <summary>The PS1 packagers folder; each package-*.ps1 becomes catalog:&lt;script base name&gt;.</summary>
    public string? PackagersRoot { get; init; }

    /// <summary>The user-writable definitions folder; each JSON file's Id becomes custom:&lt;Id&gt;.</summary>
    public string? CustomDefinitionsRoot { get; init; }
}
