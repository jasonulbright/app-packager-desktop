using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApplicationPackagerEngine.Models;

/// <summary>
/// JSON app definition — one file per application.
/// Replaces the per-app PS1 packager script.
/// </summary>
public sealed class AppDefinition
{
    public int Schema { get; set; } = 1;
    /// <summary>Unique identifier for this app definition. Used as primary key for state, visibility, dedup.</summary>
    public string Id { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Application { get; set; } = "";
    public string CMName { get; set; } = "";
    public string Architecture { get; set; } = "x64";
    public string VendorUrl { get; set; } = "";
    public string InstallerType { get; set; } = "MSI"; // MSI, EXE, ODT
    public VersionScraperConfig VersionScraper { get; set; } = new();
    public string DownloadUrl { get; set; } = "";
    public string InstallArgs { get; set; } = "";
    public string UninstallArgs { get; set; } = "";
    public DetectionConfig Detection { get; set; } = new();
    public string[] RunningProcess { get; set; } = [];
    public string? Icon { get; set; }
    public string? IconFormat { get; set; }

    /// <summary>Path to the .json file this was loaded from.</summary>
    [JsonIgnore]
    public string SourcePath { get; set; } = "";
}

public sealed class VersionScraperConfig
{
    public string Type { get; set; } = "HttpRegex";
    public string Url { get; set; } = "";
    public string Pattern { get; set; } = "";
}

public sealed class DetectionConfig
{
    public string Type { get; set; } = "RegistryKeyValue";
    public string? RegistryHive { get; set; } = "HKLM";
    public string? RegistryKeyTemplate { get; set; }
    public string? ValueName { get; set; } = "DisplayVersion";
    public string? Operator { get; set; } = "IsEquals";
    public string? FilePath { get; set; }
    public string? FileName { get; set; }
    public string? PropertyType { get; set; }
    public bool Is64Bit { get; set; } = true;
}
