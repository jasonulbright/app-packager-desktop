using System.Text.Json;
using System.Text.Json.Serialization;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Models;

/// <summary>
/// JSON app definition — one file per application.
/// </summary>
public sealed class AppDefinition
{
    public int Schema { get; set; } = 1;
    /// <summary>Unique identifier for this app definition. Used as primary key for state, visibility, dedup.</summary>
    public string Id { get; set; } = "";
    /// <summary>
    /// Whether WSUS can take the default build: "Yes", or "No (Code)" with the blocking
    /// refusal codes joined by '|'. The One Click plan shows a declared refusal before a
    /// run. Absent declares nothing.
    /// </summary>
    public string? WsusSupport { get; set; }
    public string Vendor { get; set; } = "";
    public string Application { get; set; } = "";
    public string CMName { get; set; } = "";
    /// <summary>
    /// The staged application name; {version} is the staged version. When set, the name
    /// and publisher do not come from metadata read out of an extracted EXE, which only
    /// a machine with 7-Zip can read.
    /// </summary>
    public string? AppName { get; set; }
    /// <summary>The staged publisher when <see cref="AppName"/> is set; Vendor otherwise.</summary>
    public string? Publisher { get; set; }
    public string Architecture { get; set; } = "x64";
    public string VendorUrl { get; set; } = "";
    /// <summary>CPE 2.3 name of the product, in the URI or the formatted form. The CVE lookup needs it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cpe { get; set; }
    /// <summary>
    /// Add/Remove Programs display-name wildcards (PowerShell -like syntax, case-insensitive),
    /// joined by " | ". "none" marks a product without a stable Add/Remove Programs entry, or a
    /// variant that inventory cannot tell apart from the sibling that owns the pattern.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArpName { get; set; }
    /// <summary>Optional wildcard on the Add/Remove Programs version.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArpVersion { get; set; }
    /// <summary>Optional inventory view: x86 is the 32-bit view, x64 the 64-bit view.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArpArch { get; set; }
    /// <summary>The vendor's security advisory page. Only an https URL is shown as a link.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecurityAdvisoryUrl { get; set; }
    public string InstallerType { get; set; } = "MSI"; // MSI, EXE, ODT, ZIP, MSIX
    /// <summary>
    /// Installer framework technology. Drives automated detection derivation and metadata extraction.
    /// Values: NativeMSI, NSIS, InnoSetup, WixBurn, InstallShield, Squirrel, Velopack, MSIX, ZIP, Unknown.
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
    /// <summary>
    /// Post-install configuration appended to install.ps1 behind an installer
    /// exit-code guard. Null for definitions the installer configures fully.
    /// </summary>
    public PostInstallScriptConfig? PostInstallScript { get; set; }
    public DetectionConfig Detection { get; set; } = new();
    public string[] RunningProcess { get; set; } = [];

    /// <summary>
    /// Process names (no .exe) the EXE installer launches on success.
    /// Start-Process -Wait also waits for those descendants, which never exit
    /// under a system-context deployment, so the install wrapper waits for the
    /// installer process alone and then stops these processes.
    /// </summary>
    public string[]? PostInstallKillProcesses { get; set; }

    /// <summary>
    /// Installer exit codes that mean "reboot required". The EXE install and
    /// uninstall wrappers report each as 3010, the code the deployment
    /// agents read as success with a pending restart.
    /// </summary>
    public int[]? RebootExitCodes { get; set; }

    /// <summary>
    /// Installer exit codes that follow a complete install. The EXE install
    /// wrapper reports each as 0; the detection rule still decides whether the
    /// install succeeded.
    /// </summary>
    public int[]? SuccessExitCodes { get; set; }
    public string? Icon { get; set; }
    public string? IconFormat { get; set; }

    /// <summary>
    /// Where the application icon comes from at stage time:
    /// "Installer" extracts it from the staged payload, "External" copies
    /// Definitions\Icons\&lt;definition id&gt;.ico|png, "None" stages no icon.
    /// Absent is None.
    /// </summary>
    public string? IconSource { get; set; }

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

    /// <summary>
    /// Additional architecture-specific payloads that can be staged beside
    /// the base installer. These are acquired only when the operator enables
    /// the Architecture split for a build. The base payload remains the
    /// fallback architecture and each additional payload carries its own
    /// installer commands, detection and content folder.
    /// </summary>
    public AppDefinitionPayloadVariant[]? PayloadVariants { get; set; }

    /// <summary>
    /// Install modes the installer accepts through a command-line switch,
    /// from the set CurrentUser, AllUsers. Declared only where the compiled
    /// script offers both; the operator then chooses System or User per app
    /// and staging takes that branch of the installer.
    /// </summary>
    public string[]? SupportsInstallModes { get; set; }

    /// <summary>
    /// Local installer folder for a product whose vendor download needs a
    /// sign-in, or whose catalog serves one build only. Absent means the
    /// payload always comes from DownloadUrl.
    /// </summary>
    public LocalSourceConfig? LocalSource { get; set; }

    /// <summary>
    /// The analysed installer file of a definition made by Add from file. With no download
    /// source, the stage copies this file, checks its SHA-256 and takes its version; no
    /// version source and no download run.
    /// </summary>
    public LocalInstallerConfig? LocalInstaller { get; set; }

    /// <summary>
    /// Operator-chosen files staged beside the installer, such as a license
    /// key file the setup reads from its own folder. Stage refuses to run
    /// while one is not chosen or no longer exists.
    /// </summary>
    public SideFileConfig[]? SideFiles { get; set; }

    /// <summary>
    /// Current or LTSR: the install arguments are built from the Citrix
    /// Workspace switch settings for that stream instead of InstallArgs.
    /// </summary>
    public string? CitrixSwitchStream { get; set; }

    /// <summary>
    /// ConfigMgr deployment-type post-installation behavior: BasedOnExitCode,
    /// NoAction, ProgramReboot or ForceReboot. Absent leaves the cmdlet default.
    /// </summary>
    public string? PostExecutionBehavior { get; set; }

    /// <summary>
    /// The version the payload must report: the MSI ProductVersion, or the
    /// FileVersion of an EXE. Takes {version}, {version_parts:N} and
    /// {version_part:N}. Guards an unversioned download URL that still serves
    /// an older build than the version source names.
    /// </summary>
    public string? ExpectedPayloadVersion { get; set; }

    /// <summary>
    /// URL of a vendor checksum file listing the payload SHA-256 as
    /// "&lt;hash&gt; [*]&lt;file name&gt;" lines. {download_url} is the resolved
    /// payload URL, {version} the scraped version.
    /// </summary>
    public string? PayloadSha256Url { get; set; }

    /// <summary>
    /// When true, staging refuses a payload for which no SHA256 was found:
    /// a feed that dropped the hash for one release would otherwise stage it
    /// unverified.
    /// </summary>
    public bool RequirePayloadHash { get; set; }

    /// <summary>
    /// When true, the MSI install wrapper expands %NAME% environment
    /// references in InstallArgs on the client, so a per-machine or per-user
    /// folder resolves in the context the deployment runs in.
    /// </summary>
    public bool ExpandInstallArgs { get; set; }

    /// <summary>
    /// When true, a downloaded payload must carry a valid Authenticode
    /// signature. A local-source installer is always checked.
    /// </summary>
    public bool RequireValidSignature { get; set; }

    /// <summary>
    /// The ZIP is the payload: the install wrapper extracts it to a folder
    /// instead of running an installer from inside it.
    /// </summary>
    public ZipPayloadConfig? ZipPayload { get; set; }

    /// <summary>
    /// A secret each target generates for itself, for an installer argument that
    /// would otherwise need a fixed password in the staged content. The install
    /// arguments mark its place with {TargetSecret}.
    /// </summary>
    public TargetSecretConfig? TargetSecret { get; set; }

    /// <summary>True when both modes are declared and an operator choice applies.</summary>
    [JsonIgnore]
    public bool OffersInstallModes =>
        SupportsInstallModes is { Length: >= 2 }
        && SupportsInstallModes.Contains("CurrentUser", StringComparer.OrdinalIgnoreCase)
        && SupportsInstallModes.Contains("AllUsers", StringComparer.OrdinalIgnoreCase);

    /// <summary>Path to the .json file this was loaded from.</summary>
    [JsonIgnore]
    public string SourcePath { get; set; } = "";

    /// <summary>Workbench identity namespace selected by discovery; never stored in the definition file.</summary>
    [JsonIgnore]
    public ApplicationOrigin SourceOrigin { get; set; } = ApplicationOrigin.Catalog;
}

/// <summary>One separately acquired architecture payload in a multi-payload stage.</summary>
public sealed class AppDefinitionPayloadVariant
{
    public string NameSuffix { get; set; } = "";
    public string Architecture { get; set; } = "";
    /// <summary>A single stage-root folder name, such as "arm64".</summary>
    public string ContentSubpath { get; set; } = "";
    public string InstallerType { get; set; } = "EXE";
    public string DownloadUrl { get; set; } = "";
    public string? InstallerFileName { get; set; }
    public string InstallArgs { get; set; } = "";
    public string UninstallArgs { get; set; } = "";
    public string? UninstallCommand { get; set; }
    public DetectionConfig Detection { get; set; } = new();
    /// <summary>Optional fixed SHA-256 pin; when absent, PayloadHashQuery supplies a release-specific digest.</summary>
    public string? PayloadSha256 { get; set; }
    /// <summary>Regex/JsonQuery capture for this exact payload in the shared version feed.</summary>
    public string? PayloadHashQuery { get; set; }
    public bool RequirePayloadHash { get; set; }
    public bool RequireValidSignature { get; set; }
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
    /// Optional capture of the product build number from the same feed the
    /// version came from, for detection that compares a build number the
    /// version does not carry. A regex over the feed text whose named group
    /// "build" — or group 1 — holds the build; {version} is replaced by the
    /// escaped version. It resolves the {FeedBuild} detection token.
    /// </summary>
    public string? BuildQuery { get; set; }

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
    /// HttpRegex only: a URL with a {version} placeholder that names the installer of a
    /// listed version. The Pattern matches are tried from the highest version down, and
    /// the first whose URL answers a one-byte range request with 200 or 206 is the
    /// version. A vendor index can list a release before its files are served.
    /// </summary>
    public string? ServedUrlTemplate { get; set; }

    /// <summary>
    /// HttpRegex only: the number of times curl repeats a page fetch that timed out or
    /// answered 5xx. Zero or absent fetches once.
    /// </summary>
    public int FetchRetries { get; set; }

    /// <summary>
    /// HttpRegex only: the time limit of one page fetch attempt in seconds. Zero or
    /// absent uses the scraper's default.
    /// </summary>
    public int FetchMaxTimeSeconds { get; set; }

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

    /// <summary>
    /// FileVersion only: the oldest build number the Url's {build} placeholder may take.
    /// The HeadProbe walk moves up from it while the host answers 200, and the newest
    /// answering URL names the file whose version resource is read. A floor the host no
    /// longer serves fails the scrape.
    /// </summary>
    public string? ProbeFloor { get; set; }

    /// <summary>
    /// "Name: value" request headers sent with the version request and the
    /// payload download, for vendors whose pages or CDN refuse a request
    /// without browser headers. A User-Agent entry replaces the default one.
    /// </summary>
    public string[]? RequestHeaders { get; set; }
}

/// <summary>Where a local-source installer comes from and how it is named.</summary>
public sealed class LocalSourceConfig
{
    /// <summary>
    /// Required: the payload always comes from the folder and is never
    /// downloaded. Optional: the folder wins when one is set, the download
    /// runs otherwise.
    /// </summary>
    public string Mode { get; set; } = "Optional";

    /// <summary>File-name wildcard the installer must match.</summary>
    public string Filter { get; set; } = "*.exe";

    /// <summary>Wildcards that disqualify a file the filter matched.</summary>
    public string[] Exclude { get; set; } = [];

    /// <summary>
    /// Staged file name per source-name wildcard, first match wins. {version}
    /// is the installer's file version. No match stages under InstallerFileName.
    /// </summary>
    public LocalSourceStagedName[] StagedNames { get; set; } = [];

    [JsonIgnore]
    public bool IsRequired => Mode.Equals("Required", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One installer file on this computer, identified by its SHA-256.</summary>
public sealed class LocalInstallerConfig
{
    /// <summary>The full path of the installer file the analysis read.</summary>
    public string Path { get; set; } = "";

    /// <summary>The SHA-256 of the file in hexadecimal; the stage refuses a file with another hash.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>The reviewed version; it names the staged folder and fills the version tokens.</summary>
    public string Version { get; set; } = "";
}

public sealed class LocalSourceStagedName
{
    public string Match { get; set; } = "";
    public string FileName { get; set; } = "";
}

/// <summary>One operator-chosen file copied beside the installer at stage time.</summary>
public sealed class SideFileConfig
{
    /// <summary>Settings key the chosen path is stored under; shared by every definition naming it.</summary>
    public string Key { get; set; } = "";

    /// <summary>File name the copy takes in the content folder.</summary>
    public string FileName { get; set; } = "";

    /// <summary>What the file is, in lower case, for the settings row and the refusal message.</summary>
    public string Label { get; set; } = "";
}

/// <summary>Where a target keeps the secret it generated.</summary>
public sealed class TargetSecretConfig
{
    /// <summary>Folder under %ProgramData% (letters, digits and hyphens).</summary>
    public string FolderName { get; set; } = "";

    /// <summary>File in that folder that holds the DPAPI-protected secret.</summary>
    public string FileName { get; set; } = "";

    /// <summary>Words that name the secret in the error text (letters, digits and spaces).</summary>
    public string Label { get; set; } = "product";
}

/// <summary>A ZIP extracted on the client as the installed product.</summary>
public sealed class ZipPayloadConfig
{
    /// <summary>
    /// Folder the product occupies after extraction. The ZIP's own top folder
    /// must carry this folder's name, because extraction targets its parent.
    /// </summary>
    public string InstallDir { get; set; } = "";

    /// <summary>Entries the ZIP must hold; staging refuses a ZIP without one.</summary>
    public string[] RequiredEntries { get; set; } = [];

    /// <summary>
    /// True when the ZIP holds the product files at its top level instead of
    /// inside a folder named like InstallDir. The install extracts into
    /// InstallDir itself, and the entries need no top folder.
    /// </summary>
    public bool FlatExtract { get; set; }

    /// <summary>
    /// HKLM subkey that receives the staged version as the string value
    /// "Version" after the extraction. The uninstall removes the key. It lets
    /// a ZIP product, which has no uninstall entry, be detected by version.
    /// </summary>
    public string? VersionRegistryKey { get; set; }

    /// <summary>EXE entry whose icon is the application icon.</summary>
    public string? IconEntry { get; set; }

    /// <summary>
    /// Named tokens read from entry names: each regex runs over every entry
    /// path and group 1 of the last match becomes {zip:Name}. A token whose
    /// regex matches no entry refuses the stage.
    /// </summary>
    public Dictionary<string, string> EntryTokens { get; set; } = [];
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
    /// <summary>
    /// The uninstall command-line fragment, with the same Toggle and {value} rules
    /// as CommandFragment. Null when the parameter does not affect the uninstall.
    /// </summary>
    public string? UninstallFragment { get; set; }
    /// <summary>Group name for organizing parameters into sections in the UI.</summary>
    public string? Group { get; set; }
    /// <summary>Minimum value for Number control type.</summary>
    public double? Minimum { get; set; }
    /// <summary>Maximum value for Number control type.</summary>
    public double? Maximum { get; set; }
    /// <summary>
    /// Substitution table for the chosen value: the value is looked up here before
    /// it replaces {value} in CommandFragment or an unnamed {setting:Key} token.
    /// A value absent from the table substitutes itself.
    /// </summary>
    public Dictionary<string, string>? ValueMap { get; set; }
    /// <summary>
    /// Named substitution tables addressed by {setting:Key:MapName}. Lets one
    /// parameter drive several different strings — install switches, an install
    /// root, an uninstall switch set — without duplicating the definition.
    /// A value absent from the named table substitutes itself.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>>? ValueMaps { get; set; }
    /// <summary>
    /// LocaleList only: the code every non-empty selection carries, whether checked or not.
    /// </summary>
    public string? AlwaysInclude { get; set; }
}

/// <summary>
/// PowerShell appended to install.ps1 and reached only after the installer
/// returns 0. Used for post-install configuration the installer itself cannot
/// express; the script body must be idempotent because it re-runs on reinstall.
/// </summary>
public sealed class PostInstallScriptConfig
{
    /// <summary>InstallerSettings key gating the script. Null runs it unconditionally.</summary>
    public string? WhenSetting { get; set; }
    /// <summary>Value of WhenSetting that enables the script. Default "true".</summary>
    public string WhenValue { get; set; } = "true";
    /// <summary>Script body. {setting:Key} and {setting:Key:MapName} tokens are resolved at stage time.</summary>
    public string Script { get; set; } = "";
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
    /// Value a RegistryKeyValue or File rule compares against, replacing the
    /// one derived from the scraped version. Takes {version},
    /// {version_parts:N}, {version_part:N}, {ProductVersion} and
    /// {HeaderDisplayVersion}.
    /// </summary>
    public string? ExpectedValue { get; set; }

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

    /// <summary>
    /// The product's Add/Remove Programs entry when Type is "ArpEntry". The stage
    /// writes it as the ConfigMgr and Intune script detection and as the
    /// manifest's WsusDetection. Version takes the same tokens as ExpectedValue.
    /// </summary>
    public ArpEntryDetection? ArpEntry { get; set; }
}
