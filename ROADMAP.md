# SRL Packr — Roadmap

**Product:** SRL Packr by Signal Ridge Labs
**Website:** packr.signalridgelabs.com
**Repo:** `c:\projects\packr\` (GitHub: `jasonulbright/packr`, private)

---

## v2.0 — WPF Fluent UI (ALPHA)

### Stack
- .NET 10, WPF-UI 4.2.0, CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.Hosting 10.0.5
- FluentWindow + Mica backdrop + NavigationView + MVVM + DI

### Features
- **Packages page**: ui:DataGrid, Check Latest/MECM/Stage/Package dropdown (MECM/PSADT/Intune targets), search/filter, live log streaming, batch progress bar, cancel support, keyboard shortcuts (Ctrl+A/F5/Esc/Ctrl+E), CSV export
- **Applications page**: vendor-grouped collapsible expanders, tri-state checkboxes, JSON/PS1 type badges, save refreshes Packages grid
- **Settings page**: Appearance, Organization, MECM Site, DT defaults, Acquisition Method (Static URL/Winget), M365 Channel, CWA params
- MECM connection test on launch with status indicator
- Per-app timestamps, priority-based auto-check on launch (browsers first)
- Window state persistence, persistent log file
- Pinned top rows, native DataGrid scroll
- Random opaque UIDs on all packagers (primary key, never displayed)

### Status: STABLE
- Real-world validation complete: Chrome JSON end-to-end on CM01 lab (2026-03-31)

---

## v2.1 — Engine DLL + JSON App Definitions (IN PROGRESS)

### Engine DLL (ApplicationPackagerEngine)
Proprietary class library. Plugin architecture via reflection discovery.

| Interface | Purpose | Implementations |
|-----------|---------|-----------------|
| `IVersionScraper` | Latest version detection | `HttpRegexScraper` (done), `JsonQueryScraper` (done), `WingetScraper` (stub), `CdnCabScraper`, `VendorApiScraper` |
| `IDeploymentTarget` | Create app in MDM system | `MecmTarget` (done), `BareStaging` |
| `IWrapperGenerator` | Install/uninstall scripts | `BareWrapper` (done), `PsadtWrapper` |

Core services: DownloadService (curl.exe), MsiInspectionService (COM), WrapperGeneratorService, ManifestService, StagingService, MecmTarget (powershell.exe 5.1 for CM cmdlets).

### CM01 Lab Validation
- 101 apps fully validated end-to-end: Check Latest → Check MECM → Stage → Package → MECM Application
- Initial 24 HttpRegex apps validated 2026-03-31
- JsonQuery batch (13 apps) validated 2026-04-01
- GitHub API batch (19 apps) validated 2026-04-01
- Adoptium + Corretto batch (21 apps) validated 2026-04-01
- FileVersion batch (8 apps) validated 2026-04-01
- REDIRECT batch (3 apps) validated 2026-04-01
- Citrix CR validated 2026-04-01
- DYNAMIC batch (10 apps) validated 2026-04-01
- VersionTransform working: insert_dot (7-Zip, WinRAR), replace (WizTree)
- InstallerFileName override working: fwlink URLs (MSODBCSQL18), query-string URLs (Thunderbird), version-agnostic URLs (VS Code, CCleaner)
- MSI Manufacturer fallback: invalid chars in Manufacturer field (Thunderbird `*`) falls back to def.Vendor
- Detection: RegistryKeyValue (MSI apps with ProductCode), File (EXE apps), PropertyType Version for GreaterEquals operator
- Scraper fetching switched from PS5.1 IWR to curl.exe (handles 308 redirects, WAF, JS-encrypted pages)
- `REDIR:` pattern prefix for redirect URL extraction (Slack)
- `{display_version}` placeholder for post-transform version in download URLs (Inkscape)
- GitLab tags API for Inkscape (inkscape.org blocks automated requests)
- `NoUserAgent` flag on VersionScraperConfig — Adobe blocks browser UA from server IPs
- `DisplayVersionOverride` on AppDefinition — decouples UI display version from URL transform version
- `DownloadUrlQuery` on HttpRegexScraper — extracts download URL from same page scrape (Power BI, TortoiseSVN, XenCenter, VMware Tools)
- `regex_replace` transform for Adoptium LTS suffix stripping and Corretto 5→4 part version
- `FileVersionScraper` — downloads installer to temp cache, reads EXE/MSI version properties
- `--max-filesize 1` in REDIR mode for fast redirect resolution without downloading body

### JSON App Definitions
One file per app. Community-contributable.

### Acquisition: Static URL vs Winget
- Static URL (default): works everywhere
- Winget (optional): never a requirement, always falls back to static URL

### PSADT Integration
Separate CM Application (not second DT) to preserve TS compatibility.

### Remaining v2.1 Work
- CdnCabScraper for M365 Apps/Project/Visio (6 apps)
- Staging pipeline: ZIP extraction (paint.net, TeamViewer), multi-file (CutePDF, MSVC)
- Sysinternals (Last-Modified header → date version), Teams (MSIX manifest)
- BareStaging, PsadtWrapper
- "Add New App" wizard
- Per-app settings auto-generation from JSON schema
- App icons (base64 in JSON)
- Migration tooling (PS1 → JSON converter)

### App Conversions — Status (117 apps)

#### Fully Validated on CM01 (78 apps)
Check Latest + Check MECM + Stage + Package all working end-to-end.

**HttpRegex — original batch (24 apps)**
- [x] 7-Zip (migrated to GitHub Releases API)
- [x] Anaconda Distribution
- [x] CCleaner
- [x] Cisco Webex
- [x] CPU-Z
- [x] Everything
- [x] FileZilla Client
- [x] GIMP
- [x] Google Chrome
- [x] Inkscape (via GitLab tags API + `{display_version}`)
- [x] KeePass
- [x] LibreOffice
- [x] MSODBCSQL18
- [x] Mozilla Firefox
- [x] Mozilla Thunderbird
- [x] Opera
- [x] PuTTY
- [x] Slack (via `REDIR:` redirect URL extraction)
- [x] SoapUI
- [x] TreeSize Free
- [x] Visual Studio Code
- [x] VLC
- [x] WinRAR
- [x] WizTree

**JsonQuery — JSON API batch (13 apps)**
- [x] ASP.NET 8 Server Hosting Bundle — `releases-index[?channel-version=8.0].latest-runtime`
- [x] .NET 8 Desktop Runtime — `releases-index[?channel-version=8.0].latest-runtime`
- [x] .NET 9 Desktop Runtime — `releases-index[?channel-version=9.0].latest-runtime`
- [x] .NET 10 Desktop Runtime — `releases-index[?channel-version=10.0].latest-runtime`
- [x] Microsoft Edge — `[?Product=Stable].Releases[?Architecture=x64,Platform=Windows].ProductVersion`
- [x] Microsoft Edge WebView2 Runtime — same as Edge
- [x] Node.js LTS — `[?lts!=false].version` + `strip_prefix:v`
- [x] PostgreSQL 13–17 (5 apps) — `[?cycle=N].latest`
- [x] Python — `[0].latest`

**GitHub API batch (19 apps)**
Used existing `HttpRegexScraper` and `JsonQueryScraper` against GitHub Releases API — no dedicated `GitHubApiScraper` needed.
- [x] Audacity — `strip_prefix:Audacity-`
- [x] Bitwarden Desktop — HttpRegex `desktop-v` filter on multi-client repo
- [x] Brave Browser — standalone silent installer
- [x] DBeaver Community — bare version tag (no prefix)
- [x] draw.io Desktop — MSI installer
- [x] Git for Windows — version from asset filename (tag has `.windows.N` suffix)
- [x] Greenshot — version from asset filename `INSTALLER-X.Y.Z-RELEASE.exe`
- [x] mRemoteNG — version from asset URL, `releases/latest/download/`
- [x] Notepad++
- [x] Obsidian
- [x] Positron — Posit CDN download, calver tag
- [x] PowerShell 7
- [x] PowerShell 7 LTS — HttpRegex filters `v7.4.x` from release list
- [x] PowerToys
- [x] RStudio Desktop — tags API, Posit CDN download, `replace:+:-`
- [x] ShareX
- [x] Vim
- [x] WinDirStat — `strip_prefix:release/v`, MSI
- [x] WinMerge

**Adoptium Temurin — JsonQuery + DownloadUrlQuery (14 apps)**
Single API call extracts both version (`[0].version.semver`) and download URL (`[0].binary.installer.link`).
`regex_replace` transform strips `.0.LTS` suffix.
- [x] Eclipse Temurin JDK 8 (x64), JDK 8 (x86)
- [x] Eclipse Temurin JDK 11 (x64), JDK 11 (x86)
- [x] Eclipse Temurin JDK 17, JDK 21, JDK 25
- [x] Eclipse Temurin JRE 8 (x64), JRE 8 (x86)
- [x] Eclipse Temurin JRE 11 (x64), JRE 11 (x86)
- [x] Eclipse Temurin JRE 17, JRE 21, JRE 25

**Amazon Corretto — JsonQuery + regex_replace (7 apps)**
GitHub tag for version (5-part → 4-part via `regex_replace`), Corretto CDN for download (raw 5-part tag).
- [x] Amazon Corretto JDK 8 (x64), JDK 8 (x86)
- [x] Amazon Corretto JDK 11 (x64), JDK 11 (x86)
- [x] Amazon Corretto JDK 17, JDK 21, JDK 25

**FileVersion — download + inspect (8 apps)**
`FileVersionScraper` downloads installer to temp cache, reads EXE FileVersionInfo or MSI ProductVersion.
Stage reuses cached download.
- [x] AnyDesk — EXE FileVersion
- [x] Malwarebytes — EXE ProductVersion
- [x] Microsoft OLE DB Driver 19 — MSI ProductVersion (fwlink redirect)
- [x] Postman — EXE ProductVersion
- [x] SQL Server Management Studio 22 — bootstrapper ProductVersion
- [x] TeamViewer Host — EXE ProductVersion
- [x] Visual Studio 2026 Enterprise — bootstrapper FileVersion
- [x] Visual Studio 2026 Community — bootstrapper FileVersion

**REDIRECT — version from HTTP redirect URL (3 apps)**
Used existing `REDIR:` mode in HttpRegexScraper. `--max-filesize 1` for fast redirect resolution.
- [x] Foxit PDF Reader — `REDIR:` + version from CDN path
- [x] TortoiseGit — full URL path as raw version, `regex_replace` for display version
- [x] Zoom Workplace — `REDIR:` + version from CDN path

**Citrix XML catalog (1 app)**
HttpRegex against catalog XML. No dedicated XML scraper needed.
- [x] Citrix Workspace CR — `<Stream>Current</Stream><Version>` regex, static CDN download

**DYNAMIC URL/Detection — HttpRegex + DownloadUrlQuery (10 apps)**
Version and/or download URL extracted from page scrape or API. `DownloadUrlQuery` added to `HttpRegexScraper` for two-value extraction. Relative URLs auto-prefixed with scraper base path.
- [x] pgAdmin 4 — version from download page
- [x] Power BI Desktop — version + URL from MS download page
- [x] PyCharm Community — JetBrains API
- [x] R for Windows — r-hub API (JsonQuery + DownloadUrlQuery)
- [x] TortoiseSVN — version + SourceForge URL from page
- [x] VMware Tools — version + filename from CDN directory listing
- [x] WinSCP — version from page, SourceForge download
- [x] Wireshark — version from page, constructed URL
- [x] XenCenter — version + URL from XenServer downloads page
- [x] XenServer VM Tools — version + URL from XenServer downloads page

**Adobe — NoUserAgent workaround (1 app)**
Adobe blocks browser User-Agent from server IPs. `NoUserAgent` flag added to `VersionScraperConfig`. Version display uses dots-stripped format; detection uses raw dotted version.
- [x] Adobe Acrobat Reader DC — `NoUserAgent: true`, `replace:.:` for URL construction

#### Remaining — Needs Engine Changes or Manual Handling (16 apps)

**Needs Staging Pipeline Changes (3 apps)** — multi-file, ZIP extraction, or compound detection.
- [ ] CutePDF Writer — two files (converter + writer), custom install wrapper
- [ ] MSVC Runtimes — dual-arch x86+x64, compound registry detection
- [ ] TeamViewer — ZIP containing MSI, needs unzip in staging

**Needs Special Handling (7 apps)** — blocked by vendor restrictions or need non-standard processing.
- [ ] Citrix Workspace LTSR — TOS acceptance required, download gated behind `__gda__` token
- [ ] paint.net — MSI inside ZIP, needs staging unzip support
- [ ] Microsoft Teams — MSIX_MANIFEST: version from AppxManifest.xml inside MSIX package
- [ ] Sysinternals — LAST_MODIFIED: version derived from HTTP Last-Modified header date
- [ ] Tableau Desktop — Akamai WAF blocks automated requests
- [ ] Tableau Prep Builder — same Tableau WAF issue
- [ ] Tableau Reader — same Tableau WAF issue
- [ ] VMware Workstation — MANUAL: Broadcom Support Portal requires authentication

#### CdnCab (6 apps)
Implement `CdnCabScraper` (Office CDN + ODT), then convert.
- [ ] M365 Apps for Enterprise (x64)
- [ ] M365 Apps for Enterprise (x86)
- [ ] M365 Project (x64)
- [ ] M365 Project (x86)
- [ ] M365 Visio (x64)
- [ ] M365 Visio (x86)

---

## v3.0 — Service Architecture, Distribution

### Service Architecture (UI + Agent Split)

| Component | Identity | Responsibility |
|-----------|----------|---------------|
| **UI** (admin console) | Interactive user | Configuration, monitoring, manual operations |
| **Service/Agent** | SYSTEM or service account | Auto-check, auto-stage, auto-package, auto-import |
| **Engine DLL** | Called by both | Core logic |

- Shared config: `%PROGRAMDATA%\SRLPackr\`
- UI state: `%LOCALAPPDATA%\SRLPackr\`
- Per-machine install, Windows Service

### Agent Capabilities
- Auto-check, auto-stage, auto-package, auto-import + optional auto-distribute + optional auto deployment-ring deployment creations (user defined setting, pick your staging collections - 3 are a minimum recommended but user can add more collection names and define deployment dates (similar to mecm adr creation scheduling))
- Notifications: email, Teams webhook, dashboard alert

### Distribution
- Formats: MSI, setup EXE, ZIP, portable, source via GitHub
- Code signing
- Auto-update via packr.signalridgelabs.com (definitions, engine DLL, app exe)
- Product website: packr.signalridgelabs.com

### Additional Plugins
- `IDownloader` — auth tokens, vendor APIs, multi-file, gated downloads
- `IPostAction` — webhook, Teams, email, CMDB update

### Advanced Features
- App icon with transparent PNG
- First-run wizard
- Sandbox pipeline for EXE introspection
- Community definition repository
- Intune deployment target (.intunewin + Graph API)
- Version history database + app detail pane
- Centralized version tracking on product website

---

## Testing Strategy

### v2.0
- Build validation: zero warnings, zero errors
- Automated checkbox pixel analysis
- Manual integration testing against home lab

### v2.1
- Unit tests for engine DLL
- JSON schema validation
- End-to-end: 24 apps validated on CM01 (Check Latest + Stage + Package + MECM Application creation)

### v3.0
- Service integration tests
- Update server validation
- Multi-environment testing
