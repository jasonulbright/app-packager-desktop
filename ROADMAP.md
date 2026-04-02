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
| `IVersionScraper` | Latest version detection | `HttpRegexScraper` (done), `JsonQueryScraper` (done), `FileVersionScraper` (done), `CdnCabScraper` (done), `WingetScraper` (stub) |
| `IDeploymentTarget` | Create app in MDM system | `MecmTarget` (done), `BareStaging` |
| `IWrapperGenerator` | Install/uninstall scripts | `BareWrapper` (done), `PsadtWrapper` |

Core services: DownloadService (curl.exe), MsiInspectionService (COM), WrapperGeneratorService, ManifestService, StagingService, MecmTarget (powershell.exe 5.1 for CM cmdlets).

### CM01 Lab Validation
- 114 apps fully validated end-to-end: Check Latest → Check MECM → Stage → Package → MECM Application
- Initial 24 HttpRegex apps validated 2026-03-31
- JsonQuery batch (13 apps) validated 2026-04-01
- GitHub API batch (19 apps) validated 2026-04-01
- Adoptium + Corretto batch (21 apps) validated 2026-04-01
- FileVersion batch (8 apps) validated 2026-04-01
- REDIRECT batch (3 apps) validated 2026-04-01
- Citrix CR validated 2026-04-01
- DYNAMIC batch (10 apps) validated 2026-04-01
- MSVC Runtimes (2 apps), M365 Apps/Project/Visio (6 apps), CutePDF, Teams, Sysinternals, TeamViewer, paint.net validated 2026-04-02
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

### App Conversion Status: 115 of 117 Fully Automated
All 117 apps have JSON definitions. 115 are fully automated end-to-end (Check Latest → Check MECM → Stage → Package). 2 require manual download (Citrix LTSR, VMware Workstation) — resolved in v2.2.

| Scraper | Apps | Method |
|---------|------|--------|
| HttpRegex | 50+ | Page/API scrape + regex |
| JsonQuery | 35+ | JSON API path queries |
| FileVersion | 10 | Download + EXE/MSI/MSIX inspection |
| CdnCab | 6 | Office CDN CAB + VersionDescriptor.xml |
| LAST_MODIFIED | 1 | HTTP header date |

### Remaining v2.1 Work
- **BareStaging** — `IDeploymentTarget` that stages content without creating a CM Application
- **PsadtWrapper** — `IWrapperGenerator` that produces Deploy-Application.ps1 (PSADT) wrappers
  - Creates a SECOND separate CM Application (`[Interactive]` suffix), NOT a second DT on the same app
  - Reason: a user-interactive DT poisons TS compatibility for the entire app in MECM
  - Silent app = Required/TS deployments. Interactive app = Available/Software Center self-service
- **"Add New App" wizard** — guided workflow to create a new JSON definition
  - Winget path: `winget search` → pick → download → introspect installer → auto-generate definition
  - Manual path: enter vendor/URL/regex, test scrape, configure detection
  - MSI table inspection (no install needed) for ProductCode, version, detection data (~60% of apps)
- Per-app settings auto-generation from JSON schema
- Migration tooling (PS1 → JSON converter)
- Deprecate PS1 sync files once all apps validated on JSON definitions

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

**Why WebView2:** Already a dependency (WPF-UI). The `DownloadStarting` API
provides filename, URL, and allows redirecting the save path — no temp file
juggling. Handles cookies, TOS gates, CAPTCHA, and auth portals transparently
because the user interacts with a real browser context.

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

## v2.3 — Intune + Notifications + Version History

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

### Version History Database
- Local SQLite database tracking every Check Latest / Stage / Package operation
- Schema: AppId, Version, Timestamp, Operation, Status, Duration, ErrorMessage
- App detail pane: click a row in the grid to see full version history, stage/package logs, changelog
- Export to CSV/JSON for reporting
- Powers "what changed since last check" diff view

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
- **Auto-distribute**: trigger MECM content distribution to DPs after package creation
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
- Sandbox pipeline for EXE introspection (Windows Sandbox or Hyper-V isolated environment — run installer, capture ARP registry changes, auto-derive detection rules)
- Centralized version tracking on product website (aggregated telemetry from opt-in instances)
- Multi-tenant support (multiple MECM sites / Intune tenants from one UI)

---

## Testing Strategy

### v2.0
- Build validation: zero warnings, zero errors
- Automated checkbox pixel analysis
- Manual integration testing against home lab

### v2.1
- Unit tests for engine DLL
- JSON schema validation
- End-to-end: 115 of 117 apps validated on CM01 (Check Latest + Check MECM + Stage + Package)

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
