namespace ApplicationPackagerEngine.Models;

/// <summary>
/// Matches the existing stage-manifest.json schema v2 from PS1 packagers.
/// </summary>
public sealed class StageManifest
{
    public int SchemaVersion { get; set; } = 2;
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
