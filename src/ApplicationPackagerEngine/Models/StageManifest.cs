using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ApplicationPackagerEngine.Models;

/// <summary>
/// Matches the stage-manifest.json schema v4 from PS1 packagers: v2 fields,
/// v3 FileHashes integrity records, per-deployment-type entries and the
/// operator command override record, and the v4 build identity, timing,
/// execution and plan digest. v2 and v3 manifests deserialize cleanly
/// (v3 fields null, v4 fields at their defaults).
/// </summary>
public sealed class StageManifest
{
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string AppName { get; set; } = "";
    public string? DisplayName { get; set; }
    public string Publisher { get; set; } = "";
    public string SoftwareVersion { get; set; } = "";
    public string InstallerFile { get; set; } = "";
    public string InstallerType { get; set; } = "MSI";
    /// <summary>
    /// Architecture of the installer payload: x86, x64 or ARM64. Distinct from
    /// Detection.Is64Bit, which is the registry or file system view a detection reads —
    /// an x64 product installed per-user detects through the 32-bit view routinely.
    /// A manifest written before this field existed carries none and reads as x64.
    /// </summary>
    public string Architecture { get; set; } = InstallerArchitectures.X64;
    /// <summary>Machine or User install context. Drives deployment type behavior and collection targeting.</summary>
    public string InstallContext { get; set; } = "Machine";
    /// <summary>AllUsers or CurrentUser when the operator chose an install mode for a switchable installer; null otherwise.</summary>
    public string? InstallMode { get; set; }
    public string InstallArgs { get; set; } = "";
    public string UninstallArgs { get; set; } = "";
    public string? ProductCode { get; set; }
    public string[]? RunningProcess { get; set; }
    public StageDetection Detection { get; set; } = new();
    public DateTimeOffset StagedAt { get; set; } = DateTimeOffset.Now;
    public StageFileHash[]? FileHashes { get; set; }
    /// <summary>
    /// File name of the staged application icon inside the content folder.
    /// Null when the definition stages no icon.
    /// </summary>
    public string? Icon { get; set; }
    /// <summary>Full uninstall command for EXE products whose uninstall is not msiexec-based.</summary>
    public string? UninstallCommand { get; set; }
    /// <summary>Installer framework identified at analysis time (drop-to-package).</summary>
    public string? DetectedEngine { get; set; }
    /// <summary>Original path of an ad-hoc dropped installer (drop-to-package provenance).</summary>
    public string? AdHocSource { get; set; }
    public StageDeploymentType[]? DeploymentTypes { get; set; }
    public StageCommandOverrides? CommandOverrides { get; set; }
    /// <summary>Package identity and signer read from an MSIX container at stage time.</summary>
    public StageMsixPackage? MsixPackage { get; set; }

    // Schema v4. Every field below carries its default on an unprofiled build, so a
    // reader never has to tell "unprofiled" apart from "written before v4".

    /// <summary>The build that produced this stage; empty for an unprofiled build.</summary>
    public string BuildId { get; set; } = "";
    /// <summary>The workbench ApplicationId; empty for an unprofiled build.</summary>
    public string ApplicationId { get; set; } = "";
    public string ProfileId { get; set; } = "default";
    public int ProfileRevision { get; set; }
    public StageTiming Timing { get; set; } = new();
    public StageExecution Execution { get; set; } = new();
    /// <summary>Default, or Custom when a profile replaced the detection.</summary>
    public string DetectionSource { get; set; } = "Default";
    /// <summary>Files a profile placed into the stage (icon, source files).</summary>
    public StageCustomAsset[] CustomAssets { get; set; } = [];
    /// <summary>The run-level overrides this build used; they never reach the profile.</summary>
    public JsonObject RunOverrides { get; set; } = [];
    /// <summary>IncludeVersion or NoVersion when a profile chose one; absent otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TitleMode { get; set; }
    /// <summary>The signing outcome as the signing service records it; absent when nothing signed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? ScriptSigning { get; set; }
    /// <summary>SHA-256 over the canonical plan; see <c>StageManifestPlanDigest</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlanDigest { get; set; }
}

/// <summary>Resolved runtime minutes for the build (schema v4).</summary>
public sealed class StageTiming
{
    public int? EstimatedMinutes { get; set; }
    public int? MaximumMinutes { get; set; }
    /// <summary>False when the minutes were recorded but not sent to Intune; absent on an unprofiled build.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IntuneApplied { get; set; }
}

/// <summary>Resolved execution settings for the build (schema v4).</summary>
public sealed class StageExecution
{
    public string? Context { get; set; }
    public string? LogonRequirement { get; set; }
    /// <summary>A boolean from a packager, or the interaction text a profile stores.</summary>
    public JsonNode? UserInteraction { get; set; }
    public string? ScriptHost { get; set; }
}

/// <summary>One file a profile placed into the stage (schema v4).</summary>
public sealed class StageCustomAsset
{
    /// <summary>Icon or SourceFile.</summary>
    public string Category { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string? Asset { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256 { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Size { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Linked { get; set; }
}

/// <summary>
/// MSIX identity recorded at stage time. The install wrapper pins SignerThumbprint
/// and the uninstall/detection scripts key off Name and Version, so packaging never
/// re-opens the container.
/// </summary>
public sealed class StageMsixPackage
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string ProcessorArchitecture { get; set; } = "";
    public string? SignerThumbprint { get; set; }
    public string? SignerSubject { get; set; }
    /// <summary>True when the container installs for every user via Add-AppxProvisionedPackage.</summary>
    public bool Provisioned { get; set; } = true;
}

/// <summary>One staged file's integrity record (schema v3).</summary>
public sealed class StageFileHash
{
    public string RelativePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>
/// One deployment type variant. Entries create most-specific-first; an
/// entry with no Requirements is the unconditional fallback.
/// </summary>
public sealed class StageDeploymentType
{
    public string NameSuffix { get; set; } = "";
    public string? ContentSubpath { get; set; }
    public string? InstallCommandLine { get; set; }
    public string? UninstallCommandLine { get; set; }
    public StageDetection? Detection { get; set; }
    public StageRequirement[]? Requirements { get; set; }
}

/// <summary>A requirement rule spec keyed to a condition template id.</summary>
public sealed class StageRequirement
{
    public string ConditionId { get; set; } = "";
    public object? Value { get; set; }
    public string[]? Cultures { get; set; }
}

/// <summary>Operator command overrides recorded at stage time.</summary>
public sealed class StageCommandOverrides
{
    public string? Install { get; set; }
    public string? Uninstall { get; set; }
}

public sealed class StageDetection
{
    public string Type { get; set; } = "RegistryKeyValue";
    public string? RegistryKeyRelative { get; set; }
    public string? ValueName { get; set; }
    public string? ExpectedValue { get; set; }
    public string? Operator { get; set; }
    public string? PropertyType { get; set; }
    public string? FilePath { get; set; }
    public string? FileName { get; set; }
    public bool Is64Bit { get; set; } = true;
    /// <summary>
    /// Registry hive of a registry detection: "LocalMachine" or "CurrentUser".
    /// Null reads as LocalMachine, which is what every manifest written before
    /// the field existed meant.
    /// </summary>
    public string? Hive { get; set; }

    /// <summary>True when the detection reads the logged-on user's hive.</summary>
    [JsonIgnore]
    public bool IsCurrentUserHive =>
        string.Equals(Hive, "CurrentUser", StringComparison.OrdinalIgnoreCase);
    /// <summary>Script detection language. Only meaningful when Type is Script.</summary>
    public string? ScriptLanguage { get; set; }
    /// <summary>Script detection body. Only meaningful when Type is Script.</summary>
    public string? ScriptText { get; set; }
    /// <summary>Clause connector, "And" or "Or". Only meaningful when Type is Compound.</summary>
    public string? Connector { get; set; }
    /// <summary>Sub-detections. Only meaningful when Type is Compound.</summary>
    public StageDetection[]? Clauses { get; set; }
    /// <summary>
    /// Two contiguous clause-run sizes producing (run1 AND ...) OR (run2 AND ...).
    /// Only meaningful when Type is Compound; overrides Connector.
    /// </summary>
    public int[]? GroupSizes { get; set; }
}
