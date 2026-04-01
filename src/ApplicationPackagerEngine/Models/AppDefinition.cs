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
    public AcquisitionConfig Acquisition { get; set; } = new();
    public VersionScraperConfig VersionScraper { get; set; } = new();
    public string DownloadUrl { get; set; } = "";
    /// <summary>
    /// Override filename for the downloaded installer. Required when the DownloadUrl
    /// doesn't contain a usable filename (fwlinks, query-string URLs, version-agnostic paths).
    /// </summary>
    public string? InstallerFileName { get; set; }
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

/// <summary>
/// Dual acquisition paths. JSON definitions declare what's available.
/// User preference decides which path to use at runtime.
/// </summary>
public sealed class AcquisitionConfig
{
    /// <summary>Winget package ID (e.g., "Google.Chrome"). Null = winget not available for this app.</summary>
    public string? WingetId { get; set; }

    /// <summary>Whether this app has a winget source available.</summary>
    [JsonIgnore]
    public bool HasWinget => !string.IsNullOrEmpty(WingetId);

    /// <summary>Whether this app has a static URL source available (VersionScraper + DownloadUrl).</summary>
    [JsonIgnore]
    public bool HasStaticUrl => true; // Always true if the definition exists with VersionScraper
}

public sealed class VersionScraperConfig
{
    public string Type { get; set; } = "HttpRegex";
    public string Url { get; set; } = "";
    public string Pattern { get; set; } = "";

    /// <summary>
    /// Optional post-capture version transform. Applied after regex extraction.
    /// Supported transforms (chain with |):
    ///   insert_dot:N  — insert dot at position N ("2600" → "26.00")
    ///   replace:X:Y   — replace X with Y ("4_31" → "4.31")
    ///   strip_prefix:X — remove leading X ("v1.2.3" → "1.2.3")
    /// </summary>
    public string? VersionTransform { get; set; }
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
