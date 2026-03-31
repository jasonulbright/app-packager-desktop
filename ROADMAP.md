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

### Remaining for Stable
- Real-world validation: full pipeline against production MECM environment

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
- Convert remaining apps to JSON definitions
- Remaining scraper types (CdnCab, GitHubApi, VendorApi, Winget)
- BareStaging, PsadtWrapper
- "Add New App" wizard
- Per-app settings auto-generation from JSON schema
- App icons (base64 in JSON)
- Migration tooling (PS1 → JSON converter)

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
