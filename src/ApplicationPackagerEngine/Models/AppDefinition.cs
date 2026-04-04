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
    public string InstallerType { get; set; } = "MSI"; // MSI, EXE, ODT, ZIP, MSIX
    /// <summary>
    /// Installer framework technology. Drives automated detection derivation and metadata extraction.
    /// Values: NativeMSI, NSIS, InnoSetup, WixBurn, InstallShield, Squirrel, MSIX, ZIP, Unknown.
    /// When null/empty, the engine will attempt auto-identification via ArchiveExtractionService.
    /// </summary>
    public string? InstallerFramework { get; set; }
    /// <summary>
    /// Installation context: "Machine" (default) or "User".
    /// Machine: installs to Program Files, HKLM registry, deploys to device collections, runs as SYSTEM.
    /// User: installs to %LOCALAPPDATA%, HKCU registry, deploys to user collections, runs as logged-on user.
    /// MSIX apps: context is determined by the MSIX manifest, this field is advisory for deployment targeting.
    /// </summary>
    public string InstallContext { get; set; } = "Machine";
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
    /// <summary>
    /// When true, always show the installer settings review dialog before staging,
    /// regardless of the global "Skip config review" setting. Use for apps where
    /// configuration matters (PostgreSQL port/password, VS workloads, ODT products).
    /// </summary>
    public bool ReviewBeforeStage { get; set; }
    /// <summary>
    /// Whether the app can be uninstalled. Default true. Set false for apps like Microsoft Edge
    /// that Windows prevents from being removed. Test runner skips uninstall phase when false.
    /// </summary>
    public bool CanUninstall { get; set; } = true;
    /// <summary>
    /// Per-app configurable installer parameters. Schema-driven — the UI renders controls
    /// dynamically based on this array. Each parameter defines a switch/flag that can be
    /// toggled, selected, or edited by the user, with tooltips for guidance.
    /// The engine substitutes parameter values into InstallArgs at staging time.
    /// </summary>
    public InstallerParameter[]? InstallerSettings { get; set; }
    public DetectionConfig Detection { get; set; } = new();
    public string[] RunningProcess { get; set; } = [];
    public string? Icon { get; set; }
    public string? IconFormat { get; set; }

    /// <summary>
    /// ODT configuration for M365 apps. Only used when InstallerType is "ODT".
    /// </summary>
    public OdtConfig? Odt { get; set; }

    /// <summary>
    /// Override the display version shown in the UI and used for folder names.
    /// Supports {version} (raw) placeholder. When set, the transformed version is used
    /// only for URL construction, and this value replaces it for display/folders/detection.
    /// Example: "{version}" restores the raw dotted version for display when VersionTransform
    /// strips dots for URL purposes.
    /// </summary>
    public string? DisplayVersionOverride { get; set; }

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

    /// <summary>
    /// Optional JsonQuery pattern to extract the download URL from the same API response.
    /// When set, the scraper extracts both version and download URL in one request.
    /// The extracted URL replaces the DownloadUrl template entirely.
    /// </summary>
    public string? DownloadUrlQuery { get; set; }

    /// <summary>
    /// When true, omits the User-Agent header from HTTP requests.
    /// Some vendors (e.g., Adobe) block requests with browser User-Agents from server IPs.
    /// </summary>
    public bool NoUserAgent { get; set; }
}

/// <summary>
/// ODT-specific configuration for M365 apps.
/// </summary>
public sealed class OdtConfig
{
    /// <summary>Product IDs for the Add element (e.g., "O365ProPlusRetail", "ProjectProRetail").</summary>
    public string[] ProductIds { get; set; } = [];
    /// <summary>Product ID for the uninstall Remove element (first product only).</summary>
    public string? UninstallProductId { get; set; }
    /// <summary>ODT channel name: MonthlyEnterprise, Current, SemiAnnual.</summary>
    public string Channel { get; set; } = "MonthlyEnterprise";
    /// <summary>Apps to exclude (e.g., Groove, Lync, OneDrive, Teams, Bing).</summary>
    public string[] ExcludeApps { get; set; } = ["Groove", "Lync", "OneDrive", "Teams", "Bing"];
    /// <summary>
    /// Offline: full ODT /download with offline source (large, self-contained).
    /// Online: thin install.xml only, downloads at install time (small package, requires internet).
    /// </summary>
    public string DeploymentMode { get; set; } = "Offline";
    /// <summary>Update channel for auto-updates after install. Same as Channel if not specified.</summary>
    public string? UpdateChannel { get; set; }
    /// <summary>Whether to enable Office auto-updates. Default true.</summary>
    public bool EnableUpdates { get; set; } = true;
    /// <summary>Accept EULA silently.</summary>
    public bool AcceptEula { get; set; } = true;
    /// <summary>Pin icons to taskbar.</summary>
    public bool PinToTaskbar { get; set; } = false;
    /// <summary>Shared computer licensing (for RDS/VDI).</summary>
    public bool SharedComputerLicensing { get; set; } = false;
}

/// <summary>
/// A configurable installer parameter that the UI renders as a control (toggle, dropdown, textbox, number).
/// The engine resolves these into install/uninstall command-line arguments at staging time.
/// Each parameter maps to a specific switch or flag in the installer command line.
/// </summary>
public sealed class InstallerParameter
{
    /// <summary>Unique key for this parameter (e.g., "AddToPath", "ServerPort", "Workload_ManagedDesktop").</summary>
    public string Key { get; set; } = "";
    /// <summary>Display label shown in the UI settings panel.</summary>
    public string Label { get; set; } = "";
    /// <summary>Tooltip text explaining what this parameter does.</summary>
    public string? Tooltip { get; set; }
    /// <summary>Control type: Toggle, Dropdown, Text, Number.</summary>
    public string ControlType { get; set; } = "Toggle";
    /// <summary>Default value (bool string for Toggle, string for Dropdown/Text, number string for Number).</summary>
    public string DefaultValue { get; set; } = "false";
    /// <summary>Available options for Dropdown control type.</summary>
    public string[]? Options { get; set; }
    /// <summary>
    /// The command-line fragment to append when enabled/set.
    /// For Toggle: appended when true, omitted when false.
    /// For Dropdown/Text/Number: {value} placeholder is replaced with the selected value.
    /// Example: "/D={value}" or "PrependPath={value}" or "--serverport {value}"
    /// </summary>
    public string? CommandFragment { get; set; }
    /// <summary>Group name for organizing parameters into sections in the UI.</summary>
    public string? Group { get; set; }
    /// <summary>Minimum value for Number control type.</summary>
    public double? Minimum { get; set; }
    /// <summary>Maximum value for Number control type.</summary>
    public double? Maximum { get; set; }
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
