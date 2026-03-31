# Application Packager — Roadmap

## v2.0 — C# WPF Fluent UI Conversion (ALPHA — v2.0.0-alpha tagged 2026-03-30)

**Repo:** `c:\projects\application-packager-ui\` (GitHub: `jasonulbright/application-packager-ui`, private)
**Plan file:** `C:\Users\jason\.claude\plans\glowing-sauteeing-sphinx.md`

### Stack
- .NET 10, WPF-UI 4.2.0, CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.Hosting 10.0.5
- Template patterns from ProjectDashboard (`c:\projects\projectdashboard\`)
- FluentWindow + Mica backdrop + NavigationView + MVVM + DI

### Navigation
- **Packages** (home) — Administrative Comments field, action buttons (Check Latest, Check MECM, Stage, Package dropdown with MECM/PSADT/Intune targets), ui:DataGrid with status color coding, log output area with live streaming, status bar
- **Settings** (footer) — all configuration in one scrollable sectioned page (Discord/Slack style)

### Settings Page Sections (top to bottom, separated by dividers)
1. **Save button + title row** (always visible at top)
2. **Appearance** — Dark/Light theme toggle
3. **Organization** — Company Name
4. **MECM Site Settings** — Site Code, File Share Root, Download Root, Packagers Folder
5. **Deployment Type Defaults** — Estimated/Maximum runtime
6. **Application Selector** — vendor-grouped, collapsible TreeView with checkboxes (controls which apps appear in Packages grid)
7. **Per-app settings** — only shown for enabled apps that require config:
   - M365 Channel Selector (MEC/CC/SAEC)
   - Citrix Workspace App Install Params (Store, Install Options, Plugins, Policies)
   - Future apps: license keys, server URLs, ports, etc.

### v2.0 Improvements Over PS1 GUI
- **Cancel support** — `CancellationTokenSource` with `Process.Kill(entireProcessTree: true)`. The PS1 GUI had no cancel mechanism.
- **Proper async** — replaces `[System.Windows.Forms.Application]::DoEvents()` with async/await and dispatcher threading. UI stays responsive during long operations.
- **Preferences migration** — first-launch one-time import from `AppPackager.preferences.json` (old PS1 GUI) so existing users don't re-enter settings.
- **Packagers folder auto-detection** — resolution chain: Settings override > co-located `Packagers/` > `C:\projects\applicationpackager\Packagers` (dev default) > prompt user in Settings.

### PS1 GUI Deprecation
`start-apppackager.ps1` will be deprecated once v2.0 reaches feature parity and is validated in production. It will remain in the `application-packager` repo as a reference but will not receive new features. The PS1 packager scripts themselves (`package-*.ps1`, `AppPackagerCommon.psm1`) are NOT deprecated — they continue to be the execution engine for v2.0.

### Progress (2026-03-30)
- Phase 1 COMPLETE: Project skeleton, FluentWindow, NavigationView, DI wiring
- Phase 2 COMPLETE: Settings page — Organization, MECM Site, DT defaults, M365 Channel, CWA params, Dark/Light theme
- Phase 3 COMPLETE: PackagerDiscoveryService, ui:DataGrid, select-all checkbox, debug column toggle, status color coding
- Phase 4 COMPLETE: PackagerExecutionService, MecmQueryService, Check Latest + Check MECM with real process execution
- Phase 5 COMPLETE: Stage + Package with live stdout streaming, log file writing, stderr capture
- Phase 6 COMPLETE: Context menu, Package dropdown (MECM/PSADT/Intune), window state persistence
- Phase 7 COMPLETE: Applications page — own nav item, vendor-grouped collapsible expanders, tri-state checkboxes, Show All/Hide All, Save refreshes Packages grid
- **v2.0.0-alpha tagged and released 2026-03-30**

### Remaining for v2.0 stable
- Header select-all checkbox contrast mismatch on dark theme
- App icon needs transparent PNG source (white background on dark mode)
- Preferences migration from old PS1 GUI (first-launch import)
- Real-world validation: Check Latest / Stage / Package against live MECM environment

---

## v2.1 — Engine DLL + JSON App Definitions (HIGH PRIORITY, next after v2.0)

### Problem
117 packager scripts today, each ~400 lines, ~90% duplicated logic. Only the variables differ (vendor, URLs, version regex, installer type, detection method, install args). Adding a new app means cloning and modifying a full script. With winget integration, the addressable catalog expands to 500-600+ enterprise-relevant apps — the PS1-per-app model doesn't scale.

### Architecture

**JSON app definitions** (one file per app, public/community-contributable):
```json
{
  "Vendor": "Google",
  "Application": "Chrome",
  "Architecture": "x64",
  "InstallerType": "MSI",
  "VersionScraper": {
    "Type": "HttpRegex",
    "Url": "https://chromiumdash.appspot.com/...",
    "Pattern": "\"version\":\"(\\d+\\.\\d+\\.\\d+\\.\\d+)\""
  },
  "DownloadUrl": "https://dl.google.com/dl/chrome/install/googlechromestandaloneenterprise64.msi",
  "Detection": { "Type": "RegistryKeyValue", "..." : "..." },
  "InstallArgs": "/qn /norestart",
  "UninstallArgs": "/x {ProductCode} /qn",
  "Settings": {
    "Channel": { "Type": "ComboBox", "Label": "Update Channel", "Options": ["Stable", "Beta"], "Default": "Stable" }
  }
}
```

**Engine DLL** (proprietary black box):
- Generalized scraper types: `HttpRegex` (most apps), `CdnCabParse` (M365), `GitHubApi`, `VendorApi`, etc.
- Most apps = scrape a single page with a regex. Same pattern, different URL and regex.
- New scraper type needed? Implement the `IVersionScraper` interface, register it. JSON just references which type to use.
- Scraper type registry is plugin-style, NOT a hardcoded switch. Types implement `IVersionScraper` and are discovered via reflection or explicit registration at startup. Adding a new scraper type = add a class, it's automatically available to JSON definitions. No modification to existing code required.
- Encapsulates: version check, download, staging, wrapper generation, manifest, MECM app creation, detection clauses.
- Progress callbacks, cancellation, structured results — called directly by UI (no stdout parsing).
- Replaces `AppPackagerCommon.psm1` functionality in C#.

**Per-app settings auto-generation:**
- JSON definitions include an optional `Settings` block describing UI fields (text, dropdown, checkbox, etc.)
- The Settings page dynamically renders per-app config sections from this schema — no XAML changes needed per app.
- Replaces the hardcoded M365 Channel / CWA sections with data-driven UI.

**Legacy PS1 support retained:**
- Existing `package-*.ps1` scripts continue to work alongside JSON-defined apps.
- UI handles both: JSON apps go through DLL, PS1 apps shell out to PowerShell.
- Gradual migration: convert one app at a time from PS1 to JSON.

**Migration tooling:**
- Utility to convert existing `package-*.ps1` into a JSON definition — parses header metadata + extracts variables, URLs, detection config. Accelerates the 26-script migration.

**Third-party contributions:**
- Anyone can add app support by writing a JSON file.
- Contributors don't need engine source — define *what*, DLL handles *how*.
- Power users who need new scraper types can contribute to the DLL.

### Plugin Interfaces

| Interface | Purpose | Built-in Implementations |
|-----------|---------|-------------------------|
| `IVersionScraper` | Determine latest available version | `HttpRegex` (most apps), `CdnCabParse` (M365/ODT), `GitHubApi`, `VendorApi` |
| `IDeploymentTarget` | Create application in MDM/deployment system | `MecmTarget` (CM cmdlets), `BareStaging` (versioned folder + manifest only) |
| `IWrapperGenerator` | Generate install/uninstall scripts | `BareWrapper` (bat/ps1), `PsadtWrapper` (Deploy-Application.ps1) |

All plugin-style via reflection discovery. Add a class implementing the interface, it's automatically available. No hardcoded switches.

**Future interfaces (v3.0):**
- `IDownloader` — auth tokens (GitHub private releases), vendor APIs (JetBrains), multi-file (ODT), gated downloads
- `IPostAction` — webhook, Teams notification, email, CMDB update, trigger downstream deployment

### PSADT (PowerShell App Deployment Toolkit) Integration

Packaging-time option: **"Also create interactive (PSADT) application"**

Creates a **second, separate CM Application** alongside the silent one:

| Application | Install Command | DT Logon Requirement | Use Case |
|-------------|----------------|---------------------|----------|
| `Chrome - 136.x (x64)` | `install.bat` | `WhetherOrNotUserIsLoggedOn` | Required deployments, task sequences |
| `Chrome - 136.x (x64) [Interactive]` | `Deploy-Application.exe` | `WhetherOrNotUserIsLoggedOn` | Available in Software Center (user-facing dialogs) |

**Why two separate applications, NOT two DTs on one app:**
A deployment type with `OnlyWhenUserIsLoggedOn` can prevent the entire application from appearing in the Task Sequence application picker. MECM evaluates DT eligibility at the app level — a single user-interactive DT poisons TS compatibility even if a higher-priority silent DT exists. Two separate applications avoids this entirely.

**PSADT wrapper auto-population from manifest:**
- App name, version, vendor from JSON definition
- `Show-InstallationWelcome -CloseApps` populated from `RunningProcess` array
- Install/uninstall commands from manifest
- Deferral settings configurable in Settings page or per-app JSON
- PSADT toolkit vendored into engine (per dependency philosophy)

Both apps share the same content source. Silent goes to Required/TS collections, Interactive goes to Available/self-service collections. Completely independent deployments.

### Winget Integration & App Discovery

**Dual acquisition paths** per app — JSON definition supports both:
```json
{
  "Acquisition": {
    "WingetId": "Notepad++.Notepad++",
    "StaticUrl": "https://github.com/notepad-plus-plus/...",
    "Preferred": "Winget"
  }
}
```

| Path | Pipeline | Requires |
|------|----------|----------|
| **Winget** | `winget download --id <id>` — versioning, download, and hash validation handled by winget. No version scraper, no URL maintenance, no regex. Near-zero pipeline. | Store/winget GPO enabled |
| **Static URL** | Engine downloads from vendor CDN, scrapes version via `IVersionScraper`. Current behavior. | Internet access, version scraper config |

Per-app choice: winget for commodity apps (Chrome, Firefox, 7-Zip), static URL for niche/internal apps not in winget. Falls back automatically if winget is unavailable (air-gapped, GPO-locked).

**"Add New App" wizard** (search bar in Packages page):
1. User types app name → engine queries `winget search`
2. User picks from results → engine runs `winget show <id>` for metadata (vendor, version, installer URL, type, license)
3. Engine runs `winget download <id>` to temp staging
4. Engine introspects the downloaded installer:
   - **MSI**: read ProductCode, UpgradeCode, version, install path from MSI tables (no install needed)
   - **EXE**: detect installer framework (NSIS, Inno, InstallShield), known silent switches
   - **MSIX**: read package manifest
5. Engine auto-generates `definition.json` with all discovered fields pre-populated
6. User reviews/tweaks in Settings, saves

**v2.1 scope**: Winget search/show/download + MSI table inspection (covers ~60% of apps with zero manual config).
**v3.0 scope**: Full sandbox pipeline (Windows Sandbox or Hyper-V checkpoint) for EXE introspection — install, diff ARP + filesystem, capture detection data automatically.

### App Icons

Icons are base64-encoded directly in the JSON definition — self-contained, no external URL dependency, works offline and air-gapped:

```json
{
  "Vendor": "Google",
  "Application": "Chrome",
  "Icon": "iVBORw0KGgoAAAANSUhEUgAAACAAAA...",
  "IconFormat": "png"
}
```

Engine on startup: decode base64 → write to `%LOCALAPPDATA%\AppPackager\icons\<app>.png` → DataGrid binds to the cached file path. Icons are 32x32 PNG (~1-3KB base64), keeping JSON files human-readable.

For the winget "Add New App" wizard: `winget show` returns the app's icon URL → engine downloads → resizes to 32x32 → base64 encodes into the generated JSON automatically. Zero manual icon work.

Icons sourced from vendor installers/pages — ships with the product, no copyright issue.

### Key Benefits
- One JSON per app replaces one 400-line PS1 per app.
- Adding a new app via winget = 2-minute guided flow (was 2 hours of PS1 cloning).
- Dual acquisition: winget (near-zero pipeline) or static URL (current behavior). Per-app choice.
- Deep UI integration: progress, cancel, structured results (no stdout parsing).
- Community-scalable: separate public definitions from proprietary engine.
- Per-app settings auto-generated from JSON schema — zero XAML per app.
- PSADT interactive apps without breaking TS compatibility.

---

## Distribution & Product

### Packaging Formats
- **MSI** — enterprise deployment via MECM/GPO
- **Setup EXE** — standard installer for manual installs
- **ZIP** — portable/xcopy deployment (no install required)
- **Portable** — single-directory, no installer, no registry writes
- **Source code** — via GitHub (public repo for definitions, private repo for engine/UI)

### Code Signing
- All executables and DLLs signed with a code signing certificate.
- Engine DLL signed separately — validates authenticity of the black box component.

### Auto-Update
- Dedicated domain for update infrastructure (TBD).
- Three update channels:
  - **App definitions** — JSON files updated independently. Check for new/updated definitions on launch or on-demand.
  - **Engine DLL** — updated when new scraper types or engine features ship. Versioned, signed.
  - **Application executable** — full app updates. Version check against update server.
- Update checks are non-blocking — app launches immediately, updates download in background.
- Definitions can be updated without updating the app or engine.

### Website & Documentation
- Product website on the dedicated domain: download page, documentation, changelog, screenshots.
- Documentation covers: installation, first-run setup, adding custom app definitions, MECM integration guide.
- No forum initially — GitHub Issues for bug reports and feature requests.
- GitHub Discussions or Discord considered for community later.

### Repository Strategy
- **`application-packager-ui`** (PRIVATE) — C# UI + engine DLL source. Contains proprietary code.
- **`application-packager`** (PUBLIC) — PS1 packager scripts, shared module, legacy GUI. Remains public for community reference.
- **App definitions** — TBD: either a public repo of JSON files, or bundled with the app and updated via the update server.

---

## Version History & App Detail View (v2.1+)

### Version History Database
Every version check records a timestamped entry per app: `{ timestamp, version }`. Over time this builds a version history database showing release cadence and update trends per application. Data could be:
- **Local**: SQLite or JSON append log in `%LOCALAPPDATA%\AppPackager\history\`
- **Centralized**: synced to the product website for community-wide version tracking with graphs

### App Detail Pane
Clicking an app name in the Packages grid opens a detail view showing:
- **Version history graph** — release timeline, update frequency trends
- **Vendor links** — vendor page, support page, release notes URL, download page
- **App definition editor** — modify/customize variables, scraper regex, install args, detection config
- **Current state** — last checked, latest version, MECM version, staging status

This is the "per-app dashboard" — beautiful data presentation that keeps admins engaged and informed. In v2.1 with JSON definitions, the detail pane becomes the primary way to customize app behavior without editing files.

### Scope Decision (TBD)
The version history database may be better suited for the product website rather than the local app — community-wide version tracking has more value than per-admin local data. Local app shows individual check history; website aggregates across all users. To be discussed.

---

## Testing Strategy

### v2.0
- **Build validation** — `dotnet build` must produce zero warnings, zero errors.
- **Manual integration testing** — launch app, configure settings, discover packagers, run all 4 operations against real MECM environment.
- **Process execution tests** — validate argument construction matches PS1 GUI exactly (same powershell.exe invocation patterns).

### v2.1
- **Unit tests** for engine DLL — each scraper type tested against known page content (mocked HTTP). Version parsing, download URL construction, detection clause generation.
- **JSON schema validation** — validate app definitions against a JSON schema before loading. Report errors for malformed definitions.
- **Integration tests** — end-to-end: load JSON definition, scrape version, download, stage, verify manifest. Requires network access but no MECM.
