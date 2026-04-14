# SRL Packr — Roadmap

**Product:** SRL Packr by Signal Ridge Labs
**Website:** packr.signalridgelabs.com
**Repo:** `c:\projects\packr\` (GitHub: `jasonulbright/packr`, private)

---

## v2.0 — WPF Fluent UI (COMPLETE)

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
| `IVersionScraper` | Latest version detection | `HttpRegexScraper` (done), `JsonQueryScraper` (done), `FileVersionScraper` (done), `CdnCabScraper` (done), `WingetScraper` (stub) |
| `IDeploymentTarget` | Create app in MDM system | `MecmTarget` (done), `BareStaging` |
| `IWrapperGenerator` | Install/uninstall scripts | `BareWrapper` (done), `PsadtWrapper` |

### Core Services

| Service | Dependency | Status |
|---------|-----------|--------|
| `DownloadService` | curl.exe | Done |
| `MsiInspectionService` | COM (WindowsInstaller) | Done |
| `ArchiveExtractionService` | 7z.exe | **TODO** — required for installer manifest extraction (NSIS, Burn, InnoSetup, ZIP) |
| `InstallerIdentificationService` | 7z.exe | **TODO** — detect installer framework from EXE binary (NSIS, InnoSetup, WiX Burn, InstallShield) |
| `InstallerMetadataService` | ArchiveExtractionService + framework readers | **TODO** — auto-derive detection rules from installer manifests |
| `WrapperGeneratorService` | — | Done |
| `ManifestService` | — | Done |
| `StagingService` | all above | Done |
| `MecmTarget` | powershell.exe 5.1 (CM cmdlets) | Done |

### InstallerFramework — Installer Type Identification & Metadata Extraction

**`InstallerFramework` field on AppDefinition** — identifies the installer technology. Required for automated detection derivation.

Values: `NativeMSI`, `NSIS`, `InnoSetup`, `WixBurn`, `InstallShield`, `Squirrel`, `MSIX`, `ZIP`, `Unknown`

**Auto-identification** (when field is not set): Extract EXE with 7z → inspect payload structure:
- `$PLUGINSDIR/` → NSIS
- `{app}/` + `unins000.exe` → InnoSetup
- `*.msi` + `BootstrapperCore.dll` → WiX Burn
- `AppxManifest.xml` → MSIX
- Top-level `*.msi` only → wrapped MSI (self-extracting)

**Per-framework manifest readers:**

| Framework | Extraction | Metadata Source | Detection Output |
|-----------|-----------|-----------------|-----------------|
| NativeMSI | COM (no extraction) | MSI tables: ProductCode, ProductVersion, Manufacturer | RegistryKeyValue with ProductCode + DisplayVersion |
| NSIS | 7z | `product-info.json` (JetBrains), payload scan | RegistryKey existence or File detection |
| InnoSetup | `innoextract` (vendor) | Compiled script: `[Setup]` AppName, AppVersion, DefaultDirName, `[Registry]` section | RegistryKey with InnoSetup `_is1` key pattern |
| WiX Burn | 7z | Inner `.msi` → MSI tables | RegistryKeyValue with inner MSI ProductCode |
| MSIX | 7z or ZipFile | `AppxManifest.xml`: Identity Name, Version, Publisher | Native MSIX detection (CM handles automatically) |
| Squirrel | 7z | `*.nuspec` inside RELEASES file | Per-user File detection or user ARP key |
| ZIP | ZipFile | File inventory: pick primary EXE, read FileVersion | File existence/version detection |

**Goal:** Given an installer, produce the correct detection rule without manual authoring or trial-and-error deployment.

### MSIX Deployment Type Support

`MecmTarget` must support creating native MSIX deployment types via `Add-CMMSixDeploymentType` (or equivalent CM cmdlet). CM handles provisioning, detection, and per-user registration natively for MSIX — no custom detection scripts needed.

Blocked apps: Microsoft Teams, Slack

### Per-User Deployment Support

Some apps (Obsidian, Postman, Zoom EXE) only install per-user. The engine must support:
- User-targeted deployments (deploy to user collections, not device collections)
- User-context detection (ARP in `HKCU` or `%LOCALAPPDATA%` file paths)
- Extended hardware inventory via MOF edits for detecting user-installed apps
- Alternatively: force machine-wide install via `/D=` (NSIS), `/ALLUSERS` (InnoSetup), or MSI variant when available

Blocked apps: Obsidian (NSIS per-user), Postman (Squirrel per-user), Zoom EXE (per-user, MSI variant available)

### Auto-Distribute After Package

After `MecmTarget.PackageAsync` succeeds, optionally trigger `Start-CMContentDistribution -DistributionPointGroupName`. Configurable per-operation or globally in Settings. Eliminates manual content distribution step.

### Auto-Deploy After Distribute

After content distribution succeeds, optionally create deployment to a configured test collection with configurable purpose (Required/Available) and maintenance window overrides. Eliminates manual deployment creation step.

### Dployr Pipeline Export

After staging or packaging, optionally generate a **deployment pipeline JSON** that Dployr can consume. This bridges Packr (packaging) to Dployr (deployment operations).

- Pipeline JSON contains: AppName, SoftwareVersion, ContentPath, Detection, InstallArgs, UninstallArgs, Ring target, source manifest path
- Single-app export: per-app action in Packages grid context menu
- Batch export: multi-select apps → "Export to Dployr" button
- Export location: configurable folder (Dployr watches this folder for incoming JSON)
- Schema is NOT finalized — it will be defined during Dployr development once the full deployment workflow is understood. See `c:\projects\dployr\ROADMAP.md`.

### Content Share Path Validation

Before `MecmTarget` copies content to the network share, validate that the target path is accessible and creatable. Fail fast with a clear error message instead of creating CM Applications that reference non-existent content.

Includes: vendor name sanitization (`SanitizeFolderName` — done 2026-04-03), UNC path accessibility check, disk space check.

### Version History Database (SQLite)

Local SQLite database tracking every Check Latest / Stage / Package operation.
- Schema: AppId, Version, Timestamp, Operation, Status, Duration, ErrorMessage, DetectionRule
- App detail pane: click a row in the grid to see full version history, stage/package logs
- Export to CSV/JSON for reporting
- Powers "what changed since last check" diff view
- Tracks which detection rule was applied to each packaged version (audit trail)

Pulled forward from v2.3 — essential for regression tracking and operational visibility.

### Comment Field on Package

Make the comment/description field optional on Package. Show OK/Cancel prompt if empty rather than requiring input. Current behavior blocks the workflow.

### Detection Conformance Sweep

Programmatic validation that all 116 definitions produce correct detection rules via the new `InstallerIdentificationService` pipeline. For each definition:
1. Load cached installer (already downloaded from staging)
2. Run `InstallerIdentificationService.ExtractMetadataAsync` to derive detection
3. Compare metadata-derived detection against the hardcoded detection in the JSON
4. **Match** → remove hardcoded detection fields, let engine derive at staging time
5. **Mismatch** → flag for investigation, log both expected and derived values

Goal: eliminate all hardcoded detection paths/keys from definitions. The engine derives detection from the installer itself. Only exceptions: apps with no extractable metadata (online installers, ODT) or apps where the vendor's ARP registration is non-standard and unpredictable.

Exceptions (require manual detection config):
- M365 Apps/Project/Visio — ODT-based, no installer to inspect
- SSMS — WiX Burn bootstrapper with complex multi-MSI bundle
- VS Enterprise/Community — online installer, no local payload

### Online vs Offline Packaging Options

For apps with large or online-delivered payloads, provide user options:
- **M365 Apps**: Online (thin installer, downloads at install time) vs Offline (full ODT `/download` source). ODT XML configuration integrated into Settings with per-product customization (channel, excluded apps, update behavior). Settings UI with tooltips explaining each option, similar to Citrix Workspace App (CWA) parameter settings.
- **SSMS / VS**: Online bootstrapper (small EXE, downloads components) vs Offline layout (`--layout` flag). User selects workloads/components in Settings.
- **ODT XML Builder**: Integrated M365 configuration UI — select products, channel, architecture, excluded apps, update settings. Generates install.xml and uninstall.xml. Preview XML before staging.
- **Installer Parameter Settings**: Per-definition configurable install/uninstall arguments with tooltips describing each switch. Similar to CWA Settings page pattern — grouped parameters with descriptions, defaults, and validation.

### Pre-Stage Config Review Dialog (DONE)

Before staging, if an app has `InstallerSettings` defined, a `ContentDialog` shows the `InstallerSettingsPanel` with all configurable parameters (toggles, dropdowns, text, numbers with tooltips). User reviews/adjusts and clicks Continue or Cancel.

- Per-definition: `"ReviewBeforeStage": true` — always prompt (PostgreSQL port, VS workloads)
- Global: `SkipConfigReview` in Settings — skip for apps that don't force it
- Apps with no `InstallerSettings` — skip silently

### .NET 8 Split (DONE)

Split `.NET 8 Desktop Runtime (x86+x64)` into separate x86 and x64 definitions. 117 definitions total.

### Local Definition Test Runner

Validates install, detection, and uninstall locally on bare metal without CM dependency. Faster than lab VM round-trips.

**Two test tracks based on `InstallContext`:**

| Track | Context | Method | Apps |
|-------|---------|--------|------|
| **User** | Standard user, no elevation | Direct launch | Per-user apps (Obsidian, Postman, etc.) |
| **Machine** | SYSTEM | `psexec -s` | All machine-install apps (browsers, runtimes, tools, etc.) |

No crossover — user apps never tested as SYSTEM, machine apps never tested as standard user.

**Test pipeline per app:**
1. Run install.ps1 (user context or SYSTEM via psexec)
2. Evaluate detection rule against local registry/filesystem
3. Run uninstall.ps1
4. Verify detection returns false (clean removal)
5. Log results to SQLite version history

Uses `DetectionTester` module functions for detection evaluation. `psexec -s` for SYSTEM context. Results feed into REGRESSION-STATUS.md uninstall column.

**Test Harness** (`tools/TestHarness/`): Console app referencing the engine DLL directly. Stages and tests all definitions headlessly — no GUI needed.
- Run: `dotnet run -- [definitionsDir] [downloadRoot]`
- Output: CSV results file + SQLite version history entries
- Captures full ARP baseline before testing, validates no ARP changes after testing on physical hardware
- .NET 10 testing deferred to last app (avoid breaking the test runtime)

### ARP-Driven Application Naming (replaces double-bitness fix)

CM Application Name and Software Version are populated directly from ARP registry data extracted from the installer via `InstallerMetadata`:
- **Software Center display name** = ARP `DisplayName` verbatim (e.g., "7-Zip 26.00 (x64 edition)")
- **Software Version** = ARP `DisplayVersion` verbatim (e.g., "26.00.00.0")
- No string construction, no templates, no bitness appending
- Eliminates double-bitness at the root — nothing to double because we're not constructing the name
- Fallback to `def.Application` + `def.Architecture` only when metadata extraction fails (online installers, ODT, vendor-gated)

This ensures Software Center shows exactly what Programs and Features shows.

### CM01 Lab Validation — Per-App Pipeline Status

Full pipeline: Check Latest → Check MECM → Stage → Package → Distribute → Deploy → Install → Detect → Uninstall

See [REGRESSION-STATUS.md](REGRESSION-STATUS.md) for the complete per-app validation table.

**Summary (2026-04-10):**
- **94 FULL PASS** — Install + Detect + Uninstall + Clean verified
- **6 needs retest** — definition fixes applied (CCleaner ARP key, WinMerge/WizTree framework tag), awaiting CLIENT01 retest
- **17 excluded/deferred** — per-user only, MSIX needed, CAPTCHA-gated download, SYSTEM hang, PUP, hypervisor, too large
- See [REGRESSION-STATUS.md](REGRESSION-STATUS.md) for detailed per-app status
- **Retest automation:** `tools/Retest-FailedApps.ps1` — psexec-driven 4-phase pipeline against CLIENT01, CSV results output

**Engine fixes applied during validation:**
- `BuildDetection` sanitizes `+`/`-` to `.` for Version-type RegistryKeyValue detection
- `BuildDetection` substitutes `{version}` in RegistryKeyValue and RegistryKey key templates
- `SanitizeFolderName` in MecmTarget — strips trailing dots, commas, special chars from vendor folder names
- VersionTransform working: insert_dot, replace, strip_prefix, regex_replace
- InstallerFileName override working: fwlink URLs, query-string URLs, version-agnostic URLs
- MSI Manufacturer fallback: invalid chars falls back to def.Vendor
- Scraper fetching via curl.exe (handles 308 redirects, WAF, JS-encrypted pages)
- `REDIR:` pattern prefix for redirect URL extraction
- `NoUserAgent` flag, `DisplayVersionOverride`, `DownloadUrlQuery`
- `regex_replace` transform for Adoptium LTS suffix stripping and Corretto version formats
- `FileVersionScraper` — downloads installer to temp cache, reads EXE/MSI/MSIX version properties
- `--max-filesize 1` in REDIR mode for fast redirect resolution without downloading body
- NSIS uninstall wrapper matches by application display name (not download filename) — fixes Bitwarden, DBeaver, Opera, TeamViewer Host, Notepad++, WinRAR
- MSI uninstall reads ProductCode from stage-manifest.json — fixes Firefox/Thunderbird exit 1605
- 7 definition InstallerFramework/UninstallArgs corrections (git, positron, winscp, vim, wireshark, audacity, dbeaver)

### JSON App Definitions
One file per app. Community-contributable. `InstallerFramework` field added to schema (2026-04-04, all 116 definitions populated).

### Acquisition: Static URL vs Winget
- Static URL (default): works everywhere
- Winget (optional): never a requirement, always falls back to static URL

### PSADT Integration
Separate CM Application (not second DT) to preserve TS compatibility.
- Creates a SECOND separate CM Application (`[Interactive]` suffix), NOT a second DT on the same app
- Reason: a user-interactive DT poisons TS compatibility for the entire app in MECM
- Silent app = Required/TS deployments. Interactive app = Available/Software Center self-service

### Remaining v2.1 Work — Priority Order

**Foundation (build first):**
1. `ArchiveExtractionService` — 7z wrapper in the engine
2. `InstallerFramework` field + `InstallerIdentificationService` — auto-detect framework from EXE
3. Per-framework manifest readers (NSIS, InnoSetup, WiX Burn, MSIX, Squirrel, ZIP)
4. `InstallerMetadataService` — automated detection derivation from manifest data
5. MSIX deployment type in `MecmTarget`
6. Per-user deployment support

**Framing (builds on foundation):**
7. Auto-distribute after Package
8. Auto-deploy after distribute
9. Content share path validation
10. Version history database (SQLite)
11. `BareStaging` — `IDeploymentTarget` for staging without CM Application
12. `PsadtWrapper` — PSADT Deploy-Application.ps1 wrapper generation

**Finishing:**
13. "Add New App" wizard — uses InstallerFramework, 7z extraction, metadata reading
14. Comment field optional (OK/Cancel prompt)
15. .NET 8 x86/x64 split
16. Double-bitness cosmetic fix
17. Per-app installer parameter settings — configurable install/uninstall arguments with tooltips per switch (SSMS workloads, VS components, ODT products/channel, CWA params, PostgreSQL port/password, etc.)
18. Detection conformance sweep — validate all definitions against engine-derived detection
19. Online vs offline packaging options — M365 ODT XML builder, SSMS/VS layout mode, integrated configuration UI
20. Per-app settings auto-generation from JSON schema
21. Migration tooling (PS1 → JSON converter)
22. Deprecate PS1 sync files once all apps validated on JSON definitions

---

### CLI Entry Point (packr.exe headless mode)

Expose engine operations via command-line without launching the WPF GUI. Enables scripted workflows, CI/CD integration, and automated regression testing.

**Commands:**
```
packr stage <app-name|app-id> [--definitions-dir <path>] [--output-dir <path>]
packr test <staged-content-dir> [--client <hostname>] [--user <domain\user>] [--pass <password>]
packr check-latest [<app-name|app-id>] [--json]
packr list [--json] [--filter <query>]
```

- `stage` — runs the full staging pipeline (scrape → download → inspect → wrappers → manifest) headlessly
- `test` — runs DefinitionTestRunner's 4-phase pipeline (install → detect → uninstall → clean) against a local or remote machine via psexec
- `check-latest` — scrapes vendor for latest version, outputs to stdout
- `list` — lists all definitions with current status

**Implementation:** Second `<OutputType>Exe</OutputType>` project (`PackrCli`) referencing the engine DLL. Lightweight — no WPF dependency. Uses `System.CommandLine` for arg parsing.

**Value:** Eliminates GUI dependency for regression testing and automated packaging. The `Retest-FailedApps.ps1` script becomes `packr test` with built-in credential handling.

---

## v2.2 — WebView2 Manual Download + App Icons

### WebView2 Manual Download Panel
For apps where the download is gated behind TOS acceptance or vendor authentication
(Citrix Workspace LTSR, VMware Workstation Pro, and any future gated vendors).

**`ManualDownload` flag** on `AppDefinition` — when true, Stage opens an embedded
WebView2 panel instead of downloading via curl.

**Flow:**
1. Check Latest works normally (version from API/page scrape)
2. Stage detects `ManualDownload: true`
3. Opens embedded WebView2 panel navigated to the vendor download page
4. User accepts TOS / authenticates within the panel
5. `CoreWebView2.DownloadStarting` event intercepts the browser download
6. Download is redirected into the staging content folder automatically
7. Once file lands, staging resumes (inspect, wrappers, manifest, detection)
8. WebView2 panel closes, Package proceeds normally

**Per-definition config:**
```json
{
  "ManualDownload": true,
  "ManualDownloadUrl": "https://www.citrix.com/downloads/...",
  "ManualDownloadHint": "Accept the license agreement, then click Download"
}
```

### App Icons
- `Icon` + `IconFormat` fields on AppDefinition — base64-encoded PNG in JSON (32x32, ~1-3KB)
- Engine decodes → caches to `%LOCALAPPDATA%\SRLPackr\icons\<appId>.png` → DataGrid binds to cached file
- Works offline/air-gapped (no external URL fetch)
- Displayed in DataGrid Application column and app detail pane
- Optionally set on MECM Application via `-IconLocationFile`
- "Add New App" wizard auto-captures: download icon URL → resize 32x32 → base64 encode into JSON

### First-Run Wizard
- Guides user through initial configuration: MECM site code, file share path, download root, M365 channel, company name
- Detects CM module availability and tests connection
- Creates `packager-preferences.json` and app settings on completion

### Vendor-Gated Apps — Resolved by WebView2 Panel (2 apps)
Check Latest works today. Stage blocked by download authentication. WebView2 panel completes the pipeline.
- [ ] Citrix Workspace LTSR — TOS acceptance required, download gated behind `__gda__` token
- [ ] VMware Workstation — Broadcom Support Portal requires authentication

---

## v2.3 — Intune + Notifications

### Intune Deployment Target
New `IDeploymentTarget` implementation alongside `MecmTarget`.
- `IntuneTarget` — wraps installer as `.intunewin` using `IntuneWinAppUtil.exe`
- Uploads via Microsoft Graph API (`deviceAppManagement/mobileApps`)
- Creates Win32 app with detection rules, install/uninstall commands, requirements
- Uses same staged content — no re-download needed
- UI dropdown: MECM / Intune / Both (per-operation or global setting)

### Post-Action Notifications (`IPostAction`)
Fires after Stage or Package completes (success or failure).
- `WebhookPostAction` — HTTP POST to Teams incoming webhook, Slack webhook, or custom URL
- `EmailPostAction` — SMTP notification with summary (app, version, status)
- Configurable per-app or globally in Settings
- Template-based message body with `{app}`, `{version}`, `{status}`, `{timestamp}` placeholders

### Community Definition Repository
- Public GitHub repo of curated JSON definitions (separate from the product repo)
- In-app sync: Settings → Definitions → "Update from community repository"
- Definitions are signed (SHA256 hash in a manifest) to prevent tampering
- User can override community definitions with local customizations
- Contribution workflow: fork → add/edit definition → PR → review → merge

---

## v3.0 — Service Architecture + Distribution

### Service Architecture (UI + Agent Split)

| Component | Identity | Responsibility |
|-----------|----------|---------------|
| **UI** (admin console) | Interactive user | Configuration, monitoring, manual operations, WebView2 downloads |
| **Service/Agent** | SYSTEM or service account | Scheduled auto-check, auto-stage, auto-package, auto-import |
| **Engine DLL** | Called by both | Core logic (scrapers, staging, packaging) |

- Shared config: `%PROGRAMDATA%\SRLPackr\`
- UI state: `%LOCALAPPDATA%\SRLPackr\`
- Per-machine install (`Program Files`), Windows Service (delayed auto-start)
- Agent runs on schedule even when no user is logged in
- UI launched on demand, connects to agent via named pipe or localhost API for status/control

### Agent Capabilities
- **Scheduled operations**: auto-check, auto-stage, auto-package, auto-import on configurable cron schedule
- **Deployment-ring automation**: user-defined phased deployment pipeline
  - Define N staging collections (minimum 3 recommended: Pilot → UAT → Production)
  - Each ring has a collection name, deployment action (Available/Required), and schedule offset (e.g., Pilot=0 days, UAT=+3 days, Production=+7 days)
  - After Package, agent creates deployments to each ring on the defined schedule
  - Similar to MECM ADR phased deployment, but for third-party apps
  - Dashboard shows deployment status per ring per app
- **Notifications**: email, Teams webhook, dashboard alert (via `IPostAction` from v2.3)
- **Conflict resolution**: if UI and Agent both try to operate on the same app, agent defers to UI

### Distribution & Packaging
- **Formats**: MSI installer, setup EXE (Burn/WiX), ZIP portable, source via GitHub
- **Code signing**:
  - Settings page: select signing certificate from file (PFX) or Windows certificate store
  - Signs the published EXE/MSI and optionally the engine DLL
  - Definition signing: SHA256 hash manifest for community definitions (tamper detection)
- **Auto-update**: check packr.signalridgelabs.com for new versions of definitions, engine DLL, and app EXE
- **Product website**: packr.signalridgelabs.com — downloads, documentation, definition browser

### Additional Plugins
- `IDownloader` — extensible download strategies: auth tokens, vendor-specific APIs, multi-file orchestration, resume/retry with progress. Replaces raw curl for complex scenarios.
- `IPostAction` — (introduced in v2.3, extended in v3.0 with agent-aware scheduling)

### Advanced Features
- Sandbox pipeline for EXE introspection (Windows Sandbox or Hyper-V isolated environment — run installer, capture ARP registry changes, auto-derive detection rules for frameworks that resist static analysis)
- Centralized version tracking on product website (aggregated telemetry from opt-in instances)
- Multi-tenant support (multiple MECM sites / Intune tenants from one UI)

### Packr Web — Server-Side Engine + API

The engine core (scrapers, staging, metadata extraction) is pure .NET with no Windows dependencies. .NET 10 runs on Windows, Linux, and macOS. This enables a server-side architecture:

**Architecture:**

| Component | Runtime | Purpose |
|-----------|---------|---------|
| **Packr Web API** | ASP.NET on Linux container | Serves definitions, version data, scraper results via REST API |
| **Engine Core** | .NET 10 (portable) | Scrapers, metadata extraction, version checking — shared with Desktop |
| **MecmTarget** | Windows-only | CM PowerShell cmdlets — stays on Packr Desktop, not on web server |
| **Packr Desktop** | WPF-UI, Windows | Full packaging workflow with CM integration |
| **Trackr** | Next.js on Vercel | Public version tracking — consumes Packr Web API for version data |

**Key decisions:**
- **No JS/TS scraper rewrite needed.** The .NET engine runs server-side natively. One codebase serves Desktop, Web API, and Trackr.
- **curl.exe replacement required for Linux.** Replace with `Fluent.Client` (`lepoco/fluent`) or native `HttpClient` with equivalent redirect/header/WAF handling. Fluent.Client provides chainable API with clean syntax for complex HTTP scenarios.
- **Definitions as SaaS.** Packr Desktop pulls definitions from `packr.signalridgelabs.com` on demand. Not bundled locally. This IS the SaaS value proposition — configs come from SRL.
- **On-prem container for enterprise.** Docker image with the engine + web console. OS-agnostic admin interface (Linux admin manages Windows MECM environment via browser). Container facilitates MECM commands on the site server via network logon.

**Packr Web API endpoints:**
- `GET /api/v1/definitions` — list all app definitions (metadata only)
- `GET /api/v1/definitions/{id}` — full definition JSON
- `GET /api/v1/versions/latest` — latest version for all apps (cached, refreshed on schedule)
- `GET /api/v1/versions/{id}/history` — version history for an app
- `POST /api/v1/scrape/{id}` — trigger on-demand version check for an app

### Licensing & Subscription Model

Packr Desktop scrapes vendors locally (free, offline, no dependency). Subscription adds cloud-hosted version data and definitions as a service. No license key files — API token validates against `packr.signalridgelabs.com`.

**Trackr as the version data backbone:**
- Trackr already scrapes 115+ apps on a schedule → `GET /api/v1/versions/latest`
- Packr Desktop (subscription) calls Trackr API instead of 117 individual vendor scrapes — one HTTP call
- Faster, more reliable, less vendor WAF/throttle exposure for enterprise users
- Trackr is free/public for version alerts; Packr subscription gets priority data + definitions

**Tiers:**

| Tier | Scraping | Definitions | Features | Price |
|------|----------|-------------|----------|-------|
| **Free** | Local (engine DLL) | Bundled/community | Full pipeline, offline/air-gapped | Free |
| **Pro** | Cloud (Trackr API) | Cloud-hosted from packr.srl.com | Pre-scraped versions, auto-stage, priority def updates | Subscription |
| **Enterprise** | Cloud + on-prem fallback | Cloud + custom private defs | Packr Web container, deployment rings, multi-site, audit logging | Subscription |

**What moves to the cloud (Pro/Enterprise):**
- Version scraping (Trackr API replaces local scraper calls)
- Definition hosting (pull on demand from `packr.signalridgelabs.com`)
- Definition updates (new apps, updated URLs/patterns pushed from SRL)

**What stays local (all tiers):**
- Engine DLL (staging, packaging, metadata extraction, detection derivation)
- MECM integration (MecmTarget, PowerShell cmdlets, CM connection)
- Content (installers, wrappers, content share)
- Install/uninstall execution
- SQLite version history

**Subscription validation:**
- API token issued per-org on subscription
- Packr Desktop sends token with each Trackr API / definition pull request
- Token validates against `packr.signalridgelabs.com/api/v1/auth/validate`
- Graceful degradation: if subscription lapses, falls back to local scraping (free tier behavior)
- No phone-home for offline/air-gapped installs — free tier always works without internet

### Testing — Built Alongside, Not After
- **Browser E2E test suite** (Playwright) for Packr Web console — written as each page is built
- **API integration tests** for every Packr Web API endpoint
- **Test plan** is a deliverable at each milestone, shipped with the feature
- Desktop app: existing Pester + engine unit tests. Web: Playwright + API integration tests.

**Security surface (v4.0+ vision, needs extensive planning):**
- Mutual TLS between container and MECM site server
- Role-based access for web console
- Audit logging for all packaging operations
- Credential vault integration (no stored passwords)
- Zero standing privileges for MECM admin actions

---

## Testing Strategy

### v2.0
- Build validation: zero warnings, zero errors
- Automated checkbox pixel analysis
- Manual integration testing against home lab

### v2.1
- Unit tests for engine DLL
- JSON schema validation
- Per-app pipeline validation: Check Latest → Check MECM → Stage → Package → Distribute → Deploy → Install → Detect → Uninstall
- `tools/Retest-FailedApps.ps1` — automated psexec regression testing against CLIENT01
- AutoTest (AHK v2) GUI wiring tests — see `c:\projects\autotest` Packr suite (v0.3)
- See [REGRESSION-STATUS.md](REGRESSION-STATUS.md) for per-app status

### v2.2–v2.3
- WebView2 download interception tests (mock TOS page)
- Intune Graph API integration tests (test tenant)
- SQLite version history CRUD tests
- Notification delivery tests (webhook, email)

### v3.0
- Service lifecycle tests (install, start, stop, upgrade)
- Agent scheduling tests (cron trigger, conflict resolution)
- Deployment-ring pipeline tests (phased deployment timing)
- Update server validation
- Multi-environment testing (multiple MECM sites)
