namespace ApplicationPackagerEngine.Models;

/// <summary>
/// Matches the stage-manifest.json schema v3 from PS1 packagers
/// (AppPackagerCommon 0.0.21): v2 fields plus FileHashes integrity
/// records, per-deployment-type entries, and the operator command
/// override record. v2 manifests deserialize cleanly (new fields null).
/// </summary>
public sealed class StageManifest
{
    public int SchemaVersion { get; set; } = 3;
    public string AppName { get; set; } = "";
    public string? DisplayName { get; set; }
    public string Publisher { get; set; } = "";
    public string SoftwareVersion { get; set; } = "";
    public string InstallerFile { get; set; } = "";
    public string InstallerType { get; set; } = "MSI";
    /// <summary>Machine or User install context. Drives deployment type behavior and collection targeting.</summary>
    public string InstallContext { get; set; } = "Machine";
    public string InstallArgs { get; set; } = "";
    public string UninstallArgs { get; set; } = "";
    public string? ProductCode { get; set; }
    public string[]? RunningProcess { get; set; }
    public StageDetection Detection { get; set; } = new();
    public DateTimeOffset StagedAt { get; set; } = DateTimeOffset.Now;
    public StageFileHash[]? FileHashes { get; set; }
    /// <summary>Full uninstall command for EXE products whose uninstall is not msiexec-based.</summary>
    public string? UninstallCommand { get; set; }
    /// <summary>Installer framework identified at analysis time (drop-to-package).</summary>
    public string? DetectedEngine { get; set; }
    /// <summary>Original path of an ad-hoc dropped installer (drop-to-package provenance).</summary>
    public string? AdHocSource { get; set; }
    public StageDeploymentType[]? DeploymentTypes { get; set; }
    public StageCommandOverrides? CommandOverrides { get; set; }
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
}
