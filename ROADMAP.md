# SRL Packr — Roadmap

**Product:** SRL Packr by Signal Ridge Labs
**Website:** packr.signalridgelabs.com
**Repo:** `c:\projects\application-packager-ui\` (GitHub: `jasonulbright/application-packager-ui`, private)

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

### Chrome JSON — Validated End-to-End on CM01 (2026-03-31)
- Check Latest → Stage → Package: fully working through engine DLL
- CM Application created with registry detection, revision history cleaned

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

### App Conversions by Scraper Type (116 remaining)

**HttpRegex (63 apps)** — scraper done, convert to JSON definitions:
- [ ] 7-Zip
- [ ] Adobe Acrobat Reader DC
- [ ] Anaconda Distribution
- [ ] AnyDesk
- [ ] ASP.NET 8 Server Hosting Bundle
- [ ] CCleaner
- [ ] Citrix Workspace CR
- [ ] Citrix Workspace LTSR
- [ ] CPU-Z
- [ ] CutePDF Writer
- [ ] .NET 8 Desktop Runtime
- [ ] .NET 9 Desktop Runtime
- [ ] .NET 10 Desktop Runtime
- [ ] Microsoft Edge
- [ ] Everything
- [ ] FileZilla Client
- [ ] Mozilla Firefox
- [ ] Foxit PDF Reader
- [ ] GIMP
- [ ] Inkscape
- [ ] KeePass
- [ ] LibreOffice
- [ ] Malwarebytes
- [ ] MSODBCSQL18
- [ ] MSOLEDB
- [ ] MSVC Runtimes
- [ ] Node.js
- [ ] Opera
- [ ] pgAdmin 4
- [ ] PostgreSQL 13
- [ ] PostgreSQL 14
- [ ] PostgreSQL 15
- [ ] PostgreSQL 16
- [ ] PostgreSQL 17
- [ ] Postman
- [ ] Power BI Desktop
- [ ] PuTTY
- [ ] PyCharm Community
- [ ] Python
- [ ] R
- [ ] Slack
- [ ] SoapUI
- [ ] SSMS
- [ ] Sysinternals
- [ ] Tableau Desktop
- [ ] Tableau Prep
- [ ] Tableau Reader
- [ ] Microsoft Teams
- [ ] TeamViewer
- [ ] TeamViewer Host
- [ ] Thunderbird
- [ ] TortoiseGit
- [ ] TortoiseSVN
- [ ] TreeSize Free
- [ ] VLC
- [ ] VMware Workstation
- [ ] Visual Studio 2026
- [ ] Visual Studio 2026 Community
- [ ] VS Code
- [ ] Webex
- [ ] WebView2
- [ ] WinRAR
- [ ] WinSCP
- [ ] Wireshark
- [ ] WizTree
- [ ] XenCenter
- [ ] XenServer VM Tools
- [ ] Zoom

**GitHubApi (20 apps)** — implement `GitHubApiScraper`, then convert:
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

**AdoptiumApi (14 apps)** — implement `AdoptiumApiScraper`, then convert:
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

**GitHubApi — Amazon Corretto (10 apps)** — same scraper as GitHub, different API pattern:
- [ ] Amazon Corretto JDK 8 (x64)
- [ ] Amazon Corretto JDK 8 (x86)
- [ ] Amazon Corretto JDK 11 (x64)
- [ ] Amazon Corretto JDK 11 (x86)
- [ ] Amazon Corretto JDK 17
- [ ] Amazon Corretto JDK 21
- [ ] Amazon Corretto JDK 25

**CdnCab (6 apps)** — implement `CdnCabScraper` (Office CDN + ODT), then convert:
- [ ] M365 Apps for Enterprise (x64)
- [ ] M365 Apps for Enterprise (x86)
- [ ] M365 Project (x64)
- [ ] M365 Project (x86)
- [ ] M365 Visio (x64)
- [ ] M365 Visio (x86)

**Done (1 app)**:
- [x] Google Chrome (HttpRegex, validated end-to-end on CM01)

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
- End-to-end: Chrome JSON validated on CM01

### v3.0
- Service integration tests
- Update server validation
- Multi-environment testing
