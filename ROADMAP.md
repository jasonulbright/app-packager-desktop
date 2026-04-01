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
| `IVersionScraper` | Latest version detection | `HttpRegexScraper` (done), `WingetScraper` (stub), `CdnCabScraper`, `GitHubApiScraper`, `VendorApiScraper` |
| `IDeploymentTarget` | Create app in MDM system | `MecmTarget` (done), `BareStaging` |
| `IWrapperGenerator` | Install/uninstall scripts | `BareWrapper` (done), `PsadtWrapper` |

Core services: DownloadService (curl.exe), MsiInspectionService (COM), WrapperGeneratorService, ManifestService, StagingService, MecmTarget (powershell.exe 5.1 for CM cmdlets).

### CM01 Lab Validation (2026-03-31)
- 24 apps fully validated end-to-end: Check Latest → Stage → Package → MECM Application
- VersionTransform working: insert_dot (7-Zip, WinRAR), replace (WizTree)
- InstallerFileName override working: fwlink URLs (MSODBCSQL18), query-string URLs (Thunderbird), version-agnostic URLs (VS Code, CCleaner)
- MSI Manufacturer fallback: invalid chars in Manufacturer field (Thunderbird `*`) falls back to def.Vendor
- Detection: RegistryKeyValue (MSI apps with ProductCode), File (EXE apps), PropertyType Version for GreaterEquals operator
- Scraper fetching switched from PS5.1 IWR to curl.exe (handles 308 redirects, WAF, JS-encrypted pages)
- `REDIR:` pattern prefix for redirect URL extraction (Slack)
- `{display_version}` placeholder for post-transform version in download URLs (Inkscape)
- GitLab tags API for Inkscape (inkscape.org blocks automated requests)
- 5 definitions deferred: Tableau x3 (Akamai WAF), TortoiseGit (URL version mismatch), VMware (manual)

### JSON App Definitions
One file per app. Community-contributable.

### Acquisition: Static URL vs Winget
- Static URL (default): works everywhere
- Winget (optional): never a requirement, always falls back to static URL

### PSADT Integration
Separate CM Application (not second DT) to preserve TS compatibility.

### Remaining
- Implement remaining scraper types, then convert apps by category
- BareStaging, PsadtWrapper
- "Add New App" wizard
- Per-app settings auto-generation from JSON schema
- App icons (base64 in JSON)
- Migration tooling (PS1 → JSON converter)

### App Conversions — Status (117 apps)

#### HttpRegex — Fully Validated on CM01 (24 apps)
Check Latest + Stage + Package all working end-to-end in MECM.
- [x] 7-Zip
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

#### HttpRegex — Needs JSON_QUERY Scraper (13 apps)
These scrape a JSON API endpoint and extract a field. Need `JsonQueryScraper`.
- [ ] ASP.NET 8 Server Hosting Bundle — dotnet releases-index JSON
- [ ] .NET 8 Desktop Runtime — dotnet releases-index JSON
- [ ] .NET 9 Desktop Runtime — dotnet releases-index JSON
- [ ] .NET 10 Desktop Runtime — dotnet releases-index JSON
- [ ] Microsoft Edge — JSON version endpoint
- [ ] Node.js — nodejs.org/dist/index.json, filter `lts != false`
- [ ] PostgreSQL 13 — postgresql.org JSON API, filter `cycle==13`
- [ ] PostgreSQL 14 — postgresql.org JSON API, filter `cycle==14`
- [ ] PostgreSQL 15 — postgresql.org JSON API, filter `cycle==15`
- [ ] PostgreSQL 16 — postgresql.org JSON API, filter `cycle==16`
- [ ] PostgreSQL 17 — postgresql.org JSON API, filter `cycle==17`
- [ ] Python — python.org JSON API
- [ ] WebView2 — JSON version endpoint (same pattern as Edge)

#### HttpRegex — Needs FILE_VERSION Scraper (11 apps)
Version is only available by downloading the installer and reading file/MSI properties.
Need `FileVersionScraper` (download → inspect → extract version).
- [ ] AnyDesk — EXE FileVersionInfo
- [ ] CutePDF Writer — download + inspect
- [ ] Malwarebytes — download + inspect
- [ ] MSOLEDB — MSI ProductVersion after download
- [ ] MSVC Runtimes — download + inspect
- [ ] Postman — EXE FileVersionInfo
- [ ] SSMS — bootstrapper ProductVersion (use aka.ms/ssms/22/release/vs_SSMS.exe)
- [ ] TeamViewer — MSI ProductVersion from ZIP
- [ ] TeamViewer Host — EXE FileVersionInfo
- [ ] Visual Studio 2026 — bootstrapper FileVersion
- [ ] Visual Studio 2026 Community — bootstrapper FileVersion

#### HttpRegex — Needs REDIRECT Scraper (3 apps)
Version extracted from HTTP redirect Location header or final URL.
- [ ] Foxit PDF Reader — version from redirect Location header `/win/([\d.]+)/`
- [ ] TortoiseGit — directory version (2.18.0.0) differs from file version (2.18.0.1) in download URL; single `{version}` placeholder can't handle both
- [ ] Zoom — version from redirect URL `/client/(\d+\.\d+\.\d+(?:\.\d+)?)/`

#### HttpRegex — Needs XML_QUERY Scraper (2 apps)
Scrape Citrix receiver catalog XML.
- [ ] Citrix Workspace CR — Citrix catalog XML, Current Release stream
- [ ] Citrix Workspace LTSR — Citrix catalog XML, LTSR stream

#### Needs DYNAMIC URL/Detection Construction (10 apps)
Download URL or detection registry key varies per version in ways that
require custom logic beyond simple `{version}` substitution.
- [ ] Adobe Acrobat Reader DC — version-specific download URL construction
- [ ] pgAdmin 4 — detection registry key is `pgAdmin 4v{MAJOR}_is1` (major version changes key name)
- [ ] Power BI Desktop — download URL extracted from page, not static
- [ ] PyCharm Community — JetBrains API response, dynamic download URL
- [ ] R — R-hub API response, dynamic download URL
- [ ] TortoiseSVN — SourceForge with SVN version embedded in filename
- [ ] WinSCP — ARP registry key determined after install
- [ ] Wireshark — ARP registry key determined after install
- [ ] XenCenter — MSI ProductVersion from download, dynamic URL
- [ ] XenServer VM Tools — MSI ProductVersion from download, dynamic URL

#### Needs Special Handling (6 apps)
- [ ] Microsoft Teams — MSIX_MANIFEST: version from AppxManifest.xml inside MSIX package
- [ ] Sysinternals — LAST_MODIFIED: version derived from HTTP Last-Modified header date
- [ ] Tableau Desktop — Akamai WAF blocks all automated requests from both dev and lab networks
- [ ] Tableau Prep Builder — same Tableau WAF issue
- [ ] Tableau Reader — same Tableau WAF issue
- [ ] VMware Workstation — MANUAL: Broadcom Support Portal requires authentication, no automation possible

#### GitHubApi (20 apps)
Implement `GitHubApiScraper` (GitHub Releases API), then convert.
- [ ] Audacity
- [ ] Bitwarden Desktop
- [ ] Brave Browser
- [ ] DBeaver Community
- [ ] draw.io
- [ ] Git for Windows
- [ ] Greenshot
- [ ] mRemoteNG
- [ ] Notepad++
- [ ] Obsidian
- [ ] paint.net
- [ ] Positron
- [ ] PowerShell 7
- [ ] PowerShell 7 LTS
- [ ] PowerToys
- [ ] RStudio Desktop
- [ ] ShareX
- [ ] Vim
- [ ] VMware Tools
- [ ] WinDirStat
- [ ] WinMerge

#### AdoptiumApi (14 apps)
Implement `AdoptiumApiScraper`, then convert.
- [ ] Eclipse Temurin JDK 8 (x64)
- [ ] Eclipse Temurin JDK 8 (x86)
- [ ] Eclipse Temurin JDK 11 (x64)
- [ ] Eclipse Temurin JDK 11 (x86)
- [ ] Eclipse Temurin JDK 17
- [ ] Eclipse Temurin JDK 21
- [ ] Eclipse Temurin JDK 25
- [ ] Eclipse Temurin JRE 8 (x64)
- [ ] Eclipse Temurin JRE 8 (x86)
- [ ] Eclipse Temurin JRE 11 (x64)
- [ ] Eclipse Temurin JRE 11 (x86)
- [ ] Eclipse Temurin JRE 17
- [ ] Eclipse Temurin JRE 21
- [ ] Eclipse Temurin JRE 25

#### GitHubApi — Amazon Corretto (7 apps)
Same scraper as GitHub, different API pattern.
- [ ] Amazon Corretto JDK 8 (x64)
- [ ] Amazon Corretto JDK 8 (x86)
- [ ] Amazon Corretto JDK 11 (x64)
- [ ] Amazon Corretto JDK 11 (x86)
- [ ] Amazon Corretto JDK 17
- [ ] Amazon Corretto JDK 21
- [ ] Amazon Corretto JDK 25

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
- Auto-check, auto-stage, auto-package, auto-import
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
