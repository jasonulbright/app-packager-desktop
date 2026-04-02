# Changelog

All notable changes to SRL Packr are documented here.

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
