# Application Packager — Roadmap

## v2.0 — C# WPF Fluent UI (ALPHA)

**Repo:** `c:\projects\application-packager-ui\` (GitHub: `jasonulbright/application-packager-ui`, private)

### Stack
- .NET 10, WPF-UI 4.2.0, CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.Hosting 10.0.5
- FluentWindow + Mica backdrop + NavigationView + MVVM + DI

### Features
- **Packages page**: ui:DataGrid, Check Latest/MECM/Stage/Package dropdown (MECM/PSADT/Intune targets), search/filter, live log streaming, batch progress bar, cancel support, keyboard shortcuts (Ctrl+A/F5/Esc/Ctrl+E), CSV export
- **Applications page**: vendor-grouped collapsible expanders, tri-state checkboxes, JSON/PS1 type badges, save refreshes Packages grid
- **Settings page**: Appearance, Organization, MECM Site, DT defaults, Acquisition Method (Static URL/Winget), M365 Channel, CWA params
- Per-app timestamps, priority-based auto-check on launch (browsers first)
- Window state persistence, persistent log file (`%LOCALAPPDATA%\AppPackager\apppackager.log`)
- Preferences sync to PS1 JSON files (CompanyName, CWA settings)
- First-launch migration from old PS1 GUI preferences
- Pinned top rows (CanContentScroll=False), native DataGrid scroll
- Random opaque UIDs on all packagers (primary key for exclusion, state, dedup — never displayed)

### Known Issues
- Row checkbox turns white after Check Latest/Stage operations (WPF DataGrid selection foreground inheritance)

### Remaining for Stable
- Real-world validation: Package phase against live MECM environment

---

## v2.1 — Engine DLL + JSON App Definitions (IN PROGRESS)

### Problem
117 PS1 packager scripts, ~90% duplicated logic. With winget, the addressable catalog is 500-600+ apps — PS1-per-app doesn't scale.

### Engine DLL (ApplicationPackagerEngine)
Proprietary class library. Plugin architecture via reflection discovery.

| Interface | Purpose | Implementations |
|-----------|---------|-----------------|
| `IVersionScraper` | Latest version detection | `HttpRegexScraper` (done), `CdnCabScraper` (M365), `GitHubApiScraper`, `VendorApiScraper`, `WingetScraper` (stub) |
| `IDeploymentTarget` | Create app in MDM system | `MecmTarget`, `BareStaging` |
| `IWrapperGenerator` | Install/uninstall scripts | `BareWrapper` (done), `PsadtWrapper` |

Core services (done): DownloadService (HttpClient), MsiInspectionService (COM), WrapperGeneratorService, ManifestService, StagingService orchestrator.

### JSON App Definitions
One file per app, replaces PS1 scripts. Community-contributable.
```json
{
  "Schema": 1,
  "Id": "a3f0c8e12d4b7961",
  "Vendor": "Google",
  "Application": "Google Chrome",
  "Acquisition": { "WingetId": "Google.Chrome" },
  "VersionScraper": { "Type": "HttpRegex", "Url": "...", "Pattern": "..." },
  "DownloadUrl": "https://dl.google.com/...",
  "Detection": { "Type": "RegistryKeyValue", "..." : "..." }
}
```

### Acquisition: Static URL vs Winget
- **Static URL** (default): engine scrapes vendor page + regex. Works everywhere.
- **Winget** (optional): `winget show/download`. Requires winget + repository access (Store GPO). Many orgs block Store — winget is never a requirement, always optional.
- User preference in Settings. Per-app: if WingetId exists and preference is Winget, try winget first, fall back to static URL.

### PSADT Integration
Packaging-time option: "Also create interactive (PSADT) application". Creates a **separate CM Application** (not a second DT) to avoid poisoning TS compatibility. Silent app = Required/TS, Interactive app = Available/Software Center.

### Per-App Settings Auto-Generation
JSON `Settings` block describes UI fields (text, dropdown, checkbox). Settings page renders them dynamically — no XAML per app.

### Migration Tooling
Utility to convert `package-*.ps1` into JSON definitions (parse headers + extract variables).

### App Icons
Base64-encoded PNG in JSON definition. Engine decodes → caches to `%LOCALAPPDATA%\AppPackager\icons\`. 32x32, ~1-3KB. Winget wizard auto-captures from vendor.

### "Add New App" Wizard
1. Search via winget or manual URL entry
2. Download + introspect installer (MSI tables, no install needed)
3. Auto-generate `definition.json`
4. User reviews/tweaks, saves

### Version History & App Detail View
- Every version check appends `{ timestamp, version }` per app
- App detail pane: version history graph, vendor links, definition editor, current state
- Local history in SQLite/JSON; optionally synced to product website for community tracking

### Progress (2026-03-31)
- Phase 1 COMPLETE: Engine scaffold, Chrome JSON validated end-to-end
- JSON definitions suppress matching PS1 scripts
- Acquisition routing (Static URL / Winget preference) wired
- WingetScraper stub registered (returns null, falls back to static)

### Remaining
- Convert remaining 116 PS1 scripts to JSON definitions
- Implement remaining scraper types (CdnCab, GitHubApi, VendorApi)
- Implement WingetScraper (functional)
- `IDeploymentTarget` + `MecmTarget` (Package phase in C#)
- `PsadtWrapper` implementation
- "Add New App" wizard UI
- Per-app settings auto-generation from JSON schema
- App icons in DataGrid

---

## v3.0 — Service Architecture, Distribution, Advanced Features

### Service Architecture (UI + Agent Split)

| Component | Identity | Responsibility |
|-----------|----------|---------------|
| **UI** (admin console) | Interactive user | Configuration, monitoring, manual operations |
| **Service/Agent** | SYSTEM or service account | Auto-check, auto-stage, auto-package, auto-import |
| **Engine DLL** | Called by both | Core logic |

- Shared config in `%PROGRAMDATA%\AppPackager\`
- UI state in `%LOCALAPPDATA%\AppPackager\`
- Per-machine install (Program Files), Windows Service (delayed auto-start)
- PS1 sync files deprecated after full JSON conversion

### Agent Capabilities
- Auto-check: periodic version scraping on schedule
- Auto-stage: stage automatically when new version detected
- Auto-package: create MECM/Intune application automatically
- Auto-import: distribute content, deploy to target collections
- Notifications: email, Teams webhook, dashboard alert

### Distribution
- **Formats**: MSI, setup EXE, ZIP, portable, source via GitHub
- **Code signing**: all executables and DLLs signed
- **Auto-update**: dedicated domain, three channels (definitions, engine DLL, app exe)
- **Website**: product page, documentation, changelog, screenshots

### Additional Plugin Interfaces
- `IDownloader` — auth tokens (GitHub private releases), vendor APIs, multi-file (ODT), gated downloads
- `IPostAction` — webhook, Teams notification, email, CMDB update, trigger downstream deployment

### Advanced Features
- App icon with transparent PNG source
- First-run wizard
- Full sandbox pipeline (Windows Sandbox / Hyper-V) for EXE introspection
- Community definition repository (public JSON, private engine)
- Intune deployment target (.intunewin + Graph API)

---

## Testing Strategy

### v2.0
- Build validation: `dotnet build` zero warnings, zero errors
- Manual integration testing against real MECM environment

### v2.1
- Unit tests for engine DLL (mocked HTTP, version parsing, detection clauses)
- JSON schema validation
- Integration tests: end-to-end scrape → download → stage → verify manifest

### v3.0
- Service integration tests (auto-check, auto-stage pipelines)
- Update server validation
- Multi-environment deployment testing
