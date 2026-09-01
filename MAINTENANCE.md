# Definitions maintenance

Definition upkeep (vendor URLs, scraper patterns, silent switches) is
recurring maintenance, not development work. Scrapers rot on the
vendor's schedule, not ours.

## Cadence

- **Weekly:** scraper sweep — no downloads, no installs:

  ```
  dotnet run --project tools\TestHarness -c Release -- src\ApplicationPackagerEngine\Definitions C:\temp\packr-check --check-only
  ```

  Produces `definition-check-<timestamp>.csv` (App, Result, Version,
  Error). Investigate every FAIL: usually a vendor page change; fix the
  scraper config in the definition, never the frozen test verdicts.

- **Monthly (or after any definition edit):** full harness pass for the
  changed apps only (`--app <name>`), install + detect + uninstall on
  the lab client, verdict recorded in REGRESSION-STATUS.md.

- **Signed-off definitions stay frozen** (REGRESSION-STATUS.md rule):
  a scraper fix that does not change install behavior does not reopen
  the app's verdict; anything touching InstallArgs, Detection, or the
  payload does.

## Piggybacking on app-packager

The PowerShell sibling (C:\projects\app-packager) checks the same
vendors from its own packagers. Its scheduled runs (run with /netonly
credentials against the site) double as early warning: a vendor change
that breaks a PS packager will usually break the matching Packr
definition the same week. When a PS packager gains a fixed URL or
pattern, port the fix to the matching definition here in the same
sitting.

## Known-broken definitions (2026-08-31 sweep + stage probe)

- **obsidian.json** — version scrape OK, download 404 (GitHub release
  asset naming drift; re-derive the asset URL pattern).
- **wiztree.json** — download URL now returns an HTML page (vendor
  added a wall or moved the direct link); needs a new direct URL or
  the app is no longer auto-packagable.
- **pycharm.json** — scraper/download/signature fixed and verified
  2026-08-31 (JetBrains unified installer), but the ARP detection key
  changed; needs a lab install retest before the FULL PASS verdict is
  trusted.
- **filezilla.json / foxitreader.json** — on the harness skip list for
  installer behavior (not download walls); unchanged.
- **tableaudesktop/prep/reader, vmwaretools** — behind vendor
  login/EULA walls; not auto-packagable. Citrix Workspace CR, XenCenter,
  and XenServer VM Tools are NOT walled (verified staged 2026-08-31);
  only Citrix LTSR variants are.
- **slack.json** — vendor gap found 2026-08-31 during the PS port:
  Slack's desktop.latestRelease API advertises an MSI whose S3 object
  does not exist (NoSuchKey for 4.52.153-155); Slack.msix and
  SlackSetup.exe in the same folder are live. Wait for republication or
  switch installer type (MSIX / per-user Squirrel — a deliberate
  decision, not a scraper fix).
- **cpuz.json** — DownloadUrl moved to download.cpuid.com (the
  www.cpuid.com path serves an HTML interstitial with HTTP 200). The
  engine now refuses non-MZ/non-OLE payloads at stage time
  (StagingService.ValidateInstallerMagic), so this failure mode can no
  longer stage silently.
- **citrixworkspacecr.json** — detection key carries a literal
  WOW6432Node segment AND Is64Bit false, which double-redirects when a
  target adds the segment itself; strip the literal segment (found
  during the PS port, port the fix here).

## Catalog-port definitions: known gaps (177 added, 2026-09-01)

The 177 definitions ported from app-packager's catalog waves carry these
schema-vs-PowerShell gaps. The PS packager remains the source of truth
per the PS-first rule.

**Pinned values that rot on the vendor's schedule:**
- `omnissahorizonclient` — download-group code CART27FQ2_WIN_2606 rotates quarterly.
- `agentransack` — tokenized download path not expressible; ZIP payload.
- `rvtools` — pinned to 4.8.1; no probing scraper.
- `mariadb-server` — LTS series pinned to 12.3; bump when a newer LTS ships.
- `pdfstudioviewer` — release year 2026 hardcoded in four fields; annual edit.

**Unverified against a real payload:**
- `windowsadk` / `windowspeaddon` — the `VersionSourceMsi` anchors
  (`Windows Deployment Tools-x86*.msi`, `Windows PE wims
  (DesktopEditions)-x86*.msi`) come from the PS packagers and have not
  been matched against a built layout here; a layout build resolves them
  or fails loudly, since the anchor must match exactly one file. The
  layout itself is ~1.5 GB and needs a download root short enough to keep
  the versioned content path under 120 characters.
- `sqlserver2022express` — wrapper text is unit-verified only; the media
  box (279 MB, HTTP 200) has not been extracted and run here.

**Systematic downgrades (correct today, fragile):**
- PS Script/Compound detection flattened to single registry or file
  clauses (anyburn, bulkrenameutility, codemeterruntime, nomachine,
  nvda, ocenaudio, openshot, picpick, pidgin, teracopy).
- Vendor SHA256 verification dropped (go, remotedesktopmanager,
  teracopy, androidstudio, omnissahorizonclient).
- `openwebstart` — install4j varfile forcing all-users context not
  shippable; silent run may land per-user.
- `geogebra` — conditional REINSTALL=ALL vomus (fixed ProductCode, no
  UpgradeCode) not expressible; upgrades may no-op.
