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
    /// Expected SHA256 of the downloaded payload, pinned in the definition.
    /// Staging refuses a payload whose hash differs. Use only for a fixed URL
    /// whose content does not change; a versioned payload takes
    /// VersionScraper.PayloadHashQuery instead.
    /// </summary>
    public string? PayloadSha256 { get; set; }
    /// <summary>
    /// Direct uninstall command template. When set, the uninstall wrapper calls this
    /// directly instead of searching ARP. Use for apps with known uninstaller paths.
    /// Example: "\"C:\\Program Files\\Bitwarden\\Uninstall Bitwarden.exe\" /allusers /S"
    /// </summary>
    public string? UninstallCommand { get; set; }
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
    /// Bootstrapper /layout acquisition. When set, staging downloads the
    /// bootstrapper and runs it to produce an offline layout as the package
    /// content, instead of shipping the bootstrapper alone.
    /// </summary>
    public BootstrapperLayoutConfig? BootstrapperLayout { get; set; }

    /// <summary>
    /// Extract-then-setup wrapper generation. When set, the wrappers extract a
    /// self-extracting payload and drive an inner setup executable.
    /// </summary>
    public ExtractSetupConfig? ExtractSetup { get; set; }

    /// <summary>
    /// Override the display version shown in the UI and used for folder names.
    /// Supports {version} (raw) placeholder. When set, the transformed version is used
    /// only for URL construction, and this value replaces it for display/folders/detection.
    /// Example: "{version}" restores the raw dotted version for display when VersionTransform
    /// strips dots for URL purposes.
    /// </summary>
    public string? DisplayVersionOverride { get; set; }

    /// <summary>
    /// Variant splits this definition can stage, from the set
    /// Architecture, Language, Network. Absent or empty means the app
    /// stages one deployment type only and no split may be selected for it.
    /// </summary>
    public string[]? SupportsVariants { get; set; }

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
    /// For HttpRegex, {version} in the query is replaced by the extracted version so
    /// the URL comes from the same release the version did.
    /// Scrapers that produce the URL themselves (ChainedHttpRegex, DellCatalog) ignore
    /// the value but require a non-empty one, by convention "CHAINED".
    /// </summary>
    public string? DownloadUrlQuery { get; set; }

    /// <summary>
    /// Optional capture of the payload SHA256 from the same feed the version
    /// came from, so the hash and the payload describe one build.
    /// For HttpRegex, a regex whose named group "hash" — or group 1 — holds the
    /// hash. For JsonQuery, a query path resolving to the hash string.
    /// </summary>
    public string? PayloadHashQuery { get; set; }

    /// <summary>
    /// When true, omits the User-Agent header from HTTP requests.
    /// Some vendors (e.g., Adobe) block requests with browser User-Agents from server IPs.
    /// </summary>
    public bool NoUserAgent { get; set; }

    /// <summary>
    /// When true, HttpRegex collects every Pattern match and returns the highest
    /// by version comparison instead of the first match. Guards against vendor
    /// pages that list a back-published maintenance release above the newest one.
    /// </summary>
    public bool SelectHighestVersion { get; set; }

    /// <summary>
    /// Ordered hops for the ChainedHttpRegex scraper. Each hop fetches a URL and
    /// applies a regex; the capture feeds the next hop's URL template.
    /// </summary>
    public List<ScraperHop>? Hops { get; set; }

    /// <summary>
    /// Upper bound on the HeadProbe scraper's version walk. Bounds the request
    /// count if the download host ever answers 200 for every candidate path.
    /// Zero or absent uses the scraper's default.
    /// </summary>
    public int MaxVersionHops { get; set; }
}

/// <summary>
/// One fetch-and-capture step of a ChainedHttpRegex scrape.
/// </summary>
public sealed class ScraperHop
{
    /// <summary>
    /// URL template for this hop. Placeholders: {capture} (previous hop's capture),
    /// {capture0}..{captureN} (any earlier hop's capture), {version} (version captured
    /// so far). Omit on the first hop to use VersionScraperConfig.Url.
    /// A relative value is resolved against the previous hop's URL.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Regex applied to the fetched content. The capture fed forward is the named
    /// group "capture" when present, otherwise group 1. The optional named groups
    /// "version" and "url" record the version and download URL; a later hop's
    /// value replaces an earlier one.
    /// </summary>
    public string Pattern { get; set; } = "";

    /// <summary>
    /// When true, all matches are collected and the one whose capture is the
    /// highest version wins.
    /// </summary>
    public bool SelectHighestVersion { get; set; }
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
/// Offline-layout acquisition for a vendor bootstrapper that can materialize
/// its own payload set (Windows ADK and the Windows PE add-on).
/// </summary>
public sealed class BootstrapperLayoutConfig
{
    /// <summary>
    /// Bootstrapper download URL. Empty falls back to the definition's DownloadUrl.
    /// </summary>
    public string BootstrapperUrl { get; set; } = "";

    /// <summary>
    /// Bootstrapper file name. Empty falls back to the definition's InstallerFileName.
    /// The layout writes a copy of this file at its own root, and the install
    /// wrapper runs that copy so the client consumes the local payload set.
    /// </summary>
    public string BootstrapperFileName { get; set; } = "";

    /// <summary>Switch that requests an offline layout. Takes the target folder as its argument.</summary>
    public string LayoutSwitch { get; set; } = "/layout";

    /// <summary>Switch that suppresses the bootstrapper UI for both layout and install.</summary>
    public string QuietSwitch { get; set; } = "/quiet";

    /// <summary>
    /// Layout-relative path to the MSI whose ProductVersion is the kit version
    /// and whose ProductCode is the ARP key. A '*' or '?' makes it a pattern
    /// matched against the layout, and exactly one file must match.
    /// </summary>
    public string VersionSourceMsi { get; set; } = "";

    /// <summary>Install root passed after the /installpath switch.</summary>
    public string InstallPath { get; set; } = "";

    /// <summary>Feature option ids passed after the /features switch.</summary>
    public string[] Features { get; set; } = [];

    /// <summary>Switch that removes the installed kit. Paired with QuietSwitch.</summary>
    public string UninstallSwitch { get; set; } = "/uninstall";

    /// <summary>
    /// Longest layout root the bundle accepts. Beyond this it refuses with
    /// exit 2002 ("Invalid download path") because a payload path would
    /// approach MAX_PATH.
    /// </summary>
    public int MaxLayoutPathLength { get; set; } = 120;
}

/// <summary>
/// Wrapper generation for a self-extracting payload that wraps an inner setup
/// executable (the SQL Server Express media box).
/// </summary>
public sealed class ExtractSetupConfig
{
    /// <summary>
    /// Argument template the payload is run with to extract itself.
    /// {extractdir} is replaced with the extraction root.
    /// </summary>
    public string ExtractArgs { get; set; } = "/Q /X:{extractdir}";

    /// <summary>
    /// Folder name created at the system drive root for the extraction tree.
    /// Setup rejects a path containing spaces, so this must be spaceless, and
    /// a drive-root parent keeps the inner payload paths short.
    /// </summary>
    public string ExtractDirName { get; set; } = "";

    /// <summary>Path of the inner setup executable, relative to the extraction root.</summary>
    public string InnerSetupPath { get; set; } = "setup.exe";
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

    /// <summary>
    /// Detection script language when Type is "Script": PowerShell (default),
    /// VBScript, or JavaScript.
    /// </summary>
    public string? ScriptLanguage { get; set; }

    /// <summary>
    /// Detection script body when Type is "Script". {version} is substituted
    /// with the staged display version at stage time.
    /// </summary>
    public string? ScriptText { get; set; }

    /// <summary>
    /// Clause connector when Type is "Compound": "And" (default) or "Or".
    /// Ignored when GroupSizes is set, which fixes the expression shape.
    /// </summary>
    public string? Connector { get; set; }

    /// <summary>
    /// Sub-detections when Type is "Compound". Each entry is a single
    /// registry or file clause; nesting a Compound inside a Compound is not
    /// expressible in a CM detection expression and is refused.
    /// </summary>
    public DetectionConfig[]? Clauses { get; set; }

    /// <summary>
    /// Two contiguous clause-run sizes producing (run1 AND ...) OR (run2 AND ...).
    /// Must be exactly two non-zero sizes summing to the clause count.
    /// </summary>
    public int[]? GroupSizes { get; set; }
}
