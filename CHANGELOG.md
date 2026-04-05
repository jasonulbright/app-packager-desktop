# Changelog

All notable changes to SRL Packr are documented here.

## [2.1.0-alpha8] - 2026-04-05

### Engine - Uninstall Wrapper Fixes
- **NSIS uninstall matching** — wrapper now matches ARP entries by application display name (from definition) instead of download filename. Fixes Bitwarden, DBeaver, Opera, TeamViewer Host, Notepad++, WinRAR.
- **MSI uninstall via ProductCode** — wrapper reads ProductCode from stage-manifest.json first, falls back to MSI file path. Fixes Firefox/Thunderbird exit 1605 (MSI wrapper uninstall).
- `GenerateExeWrappers` takes new `applicationName` parameter for ARP matching.

### Definition Fixes (7 apps)
- git.json: InstallerFramework NativeEXE -> InnoSetup
- positron.json: InstallerFramework NativeEXE -> InnoSetup
- winscp.json: InstallerFramework NSIS -> InnoSetup
- vim.json: UninstallArgs `/S` -> `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`
- wireshark.json: InstallerFramework InnoSetup -> NSIS
- audacity.json: UninstallArgs added `/SP-`

### Lab Infrastructure
- CLIENT01 rebuilt: 150GB OS disk (was 60GB), fresh Windows 11 Enterprise
- `Deploy-HomeLab.ps1`: CLIENT01 OSDiskSize configurable in config.psd1, disk expansion step added
- `Deploy-HomeLab.ps1`: re-adds CLIENT01 to lab definition when removed (supports VM-only rebuild)
- Default Switch NIC required for CLIENT01 internet access (not auto-added by AutomatedLab deferred deploy)

## [2.1.0-alpha7] - 2026-04-04

### Engine — Installer Metadata Extraction (Foundation)
- **ArchiveExtractionService** — 7z.exe wrapper: extract, list, extract single file, identify installer framework from archive contents
- **InstallerIdentificationService** — auto-detect NSIS/InnoSetup/WixBurn/MSIX/ZIP from EXE binary via 7z inspection; extract detection-relevant metadata per framework
- **Per-framework manifest readers**: NativeMSI (COM), NSIS (product-info.json + primary EXE), InnoSetup ({app}/ + _is1 key), WiX Burn (inner MSI extraction), MSIX (AppxManifest.xml), ZIP (primary EXE FileVersion)
- **Automated detection derivation** — `BuildDetection` falls back to `InstallerMetadata` when definition's `{ProductCode}` can't be resolved. Eliminates manual detection rule authoring for most installer types.
- **InstallerFramework field** on `AppDefinition` — all 116 definitions populated (NativeMSI, NSIS, InnoSetup, WixBurn, InstallShield, Squirrel, MSIX, ZIP, NativeEXE, ODT)
- **InstallerParameter model** — schema-driven per-app settings with ControlType (Toggle/Dropdown/Text/Number), CommandFragment, Group, Tooltip. Framework for plugin-style installer parameter UI.

### Engine — Deployment Automation
- **MSIX deployment type** in MecmTarget — `Add-CMWindowsAppxDeploymentType` for Teams/Slack. CM handles detection natively from MSIX manifest.
- **Per-user deployment support** — `InstallContext` field (Machine/User) on AppDefinition, flows to `InstallationBehaviorType InstallForUser` on deployment type. LogonRequirementType correctly omitted for user installs per CM docs.
- **Auto-distribute** — `MecmTarget.DistributeAsync()` with `-DistributionPointGroupName`
- **Auto-deploy** — `MecmTarget.DeployAsync()` with configurable purpose, maintenance window override
- **Content share path validation** — pre-check UNC accessibility before packaging
- **SanitizeFolderName** — strips trailing dots, commas, special chars from vendor folder names (prevents CM content path mismatches)

### Engine — Services
- **VersionHistoryService** — SQLite database tracking every operation (CheckLatest, Stage, Package, Distribute, Deploy). Per-app history, latest-per-app view, time range queries.
- **BareStaging** — `IDeploymentTarget` that copies content to network share without creating CM Application. For PSADT prep, Intune prep, manual workflows.
- **PsadtWrapper** — generates Deploy-Application.ps1 with vendor/app metadata, process close list, PSADT best practices. Creates separate [Interactive] CM Application, not second DT.
- **BuildDetection version sanitization** — `+` and `-` converted to `.` for Version-type RegistryKeyValue detection values
- **BuildDetection {version} substitution** — in RegistryKeyValue and RegistryKey key templates

### UI
- **Add New App wizard** — 5-step page (Basic Info → Version Source → Download → Install/Uninstall → Review & Save) with CardExpander sections, test scrape button, ComboBox selectors, tooltips on every field
- **ContentDialogHost** added to MainWindow — enables ContentDialog overlays throughout the app
- **Comment field optional** — Package without comment shows ContentDialog (Continue/Cancel) instead of blocking error
- **NumberBox** for runtime settings — replaces TextBox with spin buttons, min/max, tooltips
- **StepVisibilityConverter** — drives wizard step switching

### Regression Testing — 90 of 116 Signed Off
- Full pipeline validated on CM01 + CLIENT01: Check Latest → Check MECM → Stage → Package → Distribute → Deploy → Install → Detect
- 11 deferred (install/distribution issues), 13 excluded (architecture changes), 2 vendor-gated
- Detection fixes: 42 definitions corrected across 4 validation rounds
- See REGRESSION-STATUS.md for per-app validation table

## [2.1.0-alpha6] - 2026-04-02

### Engine — Detection Fixes
- Disk logging: per-session log files in `%LOCALAPPDATA%\SRLPackr\Logs\`
- Status labels realigned to workflow (Latest retrieved, Staged, Packaged, etc.)
- StatusToBrushConverter default changed from Transparent to Gray

## [2.1.0-alpha5] - 2026-04-02

### App Definitions — 117 of 117 validated
- **Tableau Desktop, Prep Builder, Reader (3)**: HttpRegex version from release notes, `tssoftwareregistered`/`tssoftware` CDN downloads, `replace:.:-` for URL hyphens, `DisplayVersionOverride` for dotted display
- Tableau WAF bypass: direct CDN download URLs work, no Akamai blocking

### Roadmap
- Added v2.2 WebView2 Manual Download Panel spec for vendor-gated apps (Citrix LTSR, VMware Workstation)
- Embedded `CoreWebView2.DownloadStarting` interception for TOS/auth-gated downloads
- `ManualDownload`, `ManualDownloadUrl`, `ManualDownloadHint` definition fields planned

## [2.1.0-alpha4] - 2026-04-02

### Engine — New Scrapers & Features
- **CdnCabScraper** — Office CDN version discovery via CAB/VersionDescriptor.xml extraction. Supports MEC, Current, and SemiAnnual channels with `I640Version`/`I320Version` attributes.
- **ODT staging pipeline** — full M365 staging: download ODT setup.exe, generate download/install/uninstall XML configs, run `setup.exe /download` for offline Office source, generate ODT-specific wrappers.
- **OdtConfig** on `AppDefinition` — ProductIds, UninstallProductId, Channel, ExcludeApps fields for M365 ODT configuration.
- **MsixManifest** pattern in `FileVersionScraper` — extracts `Package.Identity.Version` from AppxManifest.xml inside MSIX packages (Teams).
- **LAST_MODIFIED** pattern in `HttpRegexScraper` — HEAD request extracts Last-Modified header, converts to date-based version `YYYY.M.D` (Sysinternals).
- **ZIP extraction in staging** — downloaded ZIP files are automatically extracted to find the MSI/EXE installer inside. Original ZIP cleaned up after extraction (TeamViewer, paint.net).
- **MecmTarget recursive directory copy** — `Directory.GetFiles` with `SearchOption.AllDirectories` preserves subdirectory structure when copying to network share (M365 Office/Data/).

### App Definitions — 114 of 117 validated end-to-end
- **M365 Apps/Project/Visio x64+x86 (6)**: CdnCab scraper + ODT staging pipeline, full end-to-end
- **MSVC Runtimes x64+x86 (2)**: FileVersion scraper, separate apps, file-based detection on msvcp140.dll
- **CutePDF Writer (1)**: FileVersion scraper
- **Microsoft Teams (1)**: MsixManifest version extraction from MSIX
- **Sysinternals Suite (1)**: LAST_MODIFIED header → date version
- **TeamViewer (1)**: HttpRegex page scrape + ZIP→MSI extraction
- **paint.net (1)**: JsonQuery GitHub API + ZIP→MSI extraction

### Bug Fixes
- MecmTarget only copied top-level files, missing M365 Office/Data/ subdirectories
- TeamViewer EXE installer has no version info — reverted to ZIP/MSI variant with proper ARP detection

## [2.1.0-alpha3] - 2026-04-01

### Engine — New Scrapers & Features
- **JsonQueryScraper** — path-based query language for JSON APIs (`[?key=value].field`, array index, negation, AND conditions). Replaces need for dedicated scrapers per API format.
- **FileVersionScraper** — downloads installer to temp cache, reads EXE FileVersionInfo or MSI ProductVersion. Stage reuses cached download to avoid re-downloading.
- **DownloadUrlQuery** — extracts download URL from the same API/page response in a single request. Supported on both `JsonQueryScraper` and `HttpRegexScraper`. Relative URLs auto-prefixed with scraper base path.
- **NoUserAgent** flag on `VersionScraperConfig` — omits User-Agent header for vendors that block browser UA from server IPs (Adobe).
- **DisplayVersionOverride** on `AppDefinition` — decouples UI display version from URL transform version. Supports `{version}` placeholder for raw version.
- **regex_replace** transform — regex-based version string manipulation (Adoptium LTS suffix, Corretto 5→4 part version).
- **RegistryKey** detection type — key-existence detection with `{version}` substitution in key path (ASP.NET Hosting Bundle).
- **`{version}` substitution in File detection paths** — version-specific install directories (dotnet runtimes).
- **Raw version fallback for File detection** — uses raw (dotted) version for `ExpectedValue` when PropertyType is Version, preventing invalid version format errors.

### Engine — Performance & Reliability
- **Stage version reuse** — Stage passes `knownVersion` from Check Latest to skip redundant API calls (GitHub rate limiting). Re-scrapes only when `VersionTransform` exists and raw version is needed.
- **REDIR: mode optimization** — `--max-filesize 1` replaces `--output NUL` for fast redirect URL resolution without downloading the response body. Accepts exit code 63 as success.
- **InstallerFileName `{display_version}` resolution** — both `{version}` and `{display_version}` placeholders supported in InstallerFileName override.

### UI
- **Status column labels** — workflow-aligned labels: Latest retrieved, Not in MECM, Update available, Up to date, Current newer, Staged, Packaged, Error variants.
- **Status column visibility** — `StatusToBrushConverter` updated: all statuses render visible text. Default fallback changed from `Transparent` to `Gray`.
- **Checkbox behavior** — Check Latest unchecks on error. Check MECM unchecks if version equals or newer. Stage unchecks on error. Package unchecks on both error and success.

### App Definitions — 101 of 117 validated end-to-end
- **JsonQuery batch (13)**: dotnet 8/9/10, ASP.NET 8, Edge, WebView2, Node.js, PostgreSQL 13–17, Python
- **GitHub API batch (19)**: Audacity, Bitwarden, Brave, DBeaver, draw.io, Git, Greenshot, mRemoteNG, Notepad++, Obsidian, Positron, PowerShell 7/LTS, PowerToys, RStudio, ShareX, Vim, WinDirStat, WinMerge
- **Adoptium Temurin (14)**: JDK/JRE 8/11/17/21/25 (x64+x86 variants)
- **Amazon Corretto (7)**: JDK 8/11/17/21/25 (x64+x86 variants)
- **FileVersion (8)**: AnyDesk, Malwarebytes, MSOLEDB 19, Postman, SSMS 22, TeamViewer Host, VS 2026 Enterprise/Community
- **REDIRECT (3)**: Foxit PDF Reader, TortoiseGit, Zoom Workplace
- **Citrix (1)**: Workspace CR (XML catalog regex)
- **DYNAMIC (11)**: Adobe Reader DC, pgAdmin 4, Power BI Desktop, PyCharm, R, TortoiseSVN, VMware Tools, WinSCP, Wireshark, XenCenter, XenServer VM Tools
- **7-Zip** migrated from 7-zip.org HTML to GitHub Releases API

### Bug Fixes
- ASP.NET 8 / dotnet detection: `RegistryKey` type and `{version}` in File paths now handled in `BuildDetection`
- Node.js double-v URL: `v{version}` → `v{display_version}` when `strip_prefix:v` transform active
- Corretto download 403: Stage re-scrapes when `VersionTransform` exists to preserve raw version for URL
- Malwarebytes detection: `PropertyType` changed from `String` to `Version` for `GreaterEquals` operator
- CMName mismatches: dotnet 8/9/10, ASP.NET 8 CMNames updated to match engine app names
- Stale runtime DLLs: `hostfxr.dll`/`coreclr.dll` in deploy folder caused framework-dependent app to search locally instead of `Program Files\dotnet`
- mRemoteNG download 404: asset filename includes build number not in tag; switched to `releases/latest/download/`
- Brave InstallerFileName: `{display_version}` placeholder resolution added

### Deploy Script
- Allowlist-based definition filtering — only tested definitions deployed to CM01
- Stale runtime DLL cleanup guard
- `JSON_QUERY` removed from exclusion filter (definitions now use real scraper configs)

## [2.1.0-alpha2] - 2026-03-31

- 24 apps validated end-to-end on CM01 (HttpRegex scraper)
- VersionTransform: insert_dot, replace, strip_prefix
- InstallerFileName override for fwlink/query-string URLs
- REDIR: pattern prefix for redirect URL extraction
- MSI Manufacturer fallback for invalid characters
- Scraper fetching switched from PS5.1 IWR to curl.exe

## [2.1.0-alpha1] - 2026-03-31

- ApplicationPackagerEngine class library (IVersionScraper, HttpRegexScraper, ScraperRegistry)
- Core services: DownloadService, MsiInspectionService, WrapperGeneratorService, ManifestService, StagingService
- MecmTarget: PowerShell 5.1 subprocess for CM cmdlets
- Chrome JSON definition as proof of concept
- UI routing: JSON apps → engine DLL, PS1 apps → powershell.exe

## [2.0.0-alpha] - 2026-03-30

- Initial WPF Fluent UI with .NET 10, WPF-UI 4.2.0, CommunityToolkit.Mvvm
- Packages page with DataGrid, Check Latest/MECM/Stage/Package operations
- Applications page with vendor-grouped expanders and tri-state checkboxes
- Settings page: Appearance, Organization, MECM Site, DT defaults
- MECM connection test, per-app timestamps, priority auto-check
- Window state persistence, keyboard shortcuts, CSV export
