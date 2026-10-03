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

  Run the sweep with a GitHub token available. About a third of the
  definitions scrape `api.github.com`, whose anonymous budget is 60
  requests per hour; a token raises it to 5000 and keeps the sweep off
  rate-limit failures that look like vendor breakage. The engine picks up
  `PACKR_GITHUB_TOKEN`, then `GITHUB_TOKEN`, then `gh auth token`, so an
  authenticated `gh` needs no further setup.

- **Monthly (or after any definition edit):** full harness pass for the
  changed apps only (`--app <name>`), install + detect + uninstall on
  the lab client, verdict recorded in REGRESSION-STATUS.md.

- **Signed-off definitions stay frozen** (REGRESSION-STATUS.md rule):
  a scraper fix that does not change install behavior does not reopen
  the app's verdict; anything touching InstallArgs, Detection, or the
  payload does.

- **Isolated UI runs:** set `APP_PACKAGER_DATA_ROOT` to a scratch folder to move `settings.json`, `condition-templates.json` and the default Workbench root out of `%LOCALAPPDATA%\AppPackager`.

## Piggybacking on app-packager

The PowerShell sibling (C:\projects\app-packager) checks the same
vendors from its own packagers. Its scheduled runs (run with /netonly
credentials against the site) double as early warning: a vendor change
that breaks a PS packager will usually break the matching Packr
definition the same week. When a PS packager gains a fixed URL or
pattern, port the fix to the matching definition here in the same
sitting.

## Known-broken definitions (2026-08-31 sweep + stage probe; updated 2026-09-04)

Resolved 2026-09-04 (stage-verified): obsidian (newest release carrying a
Windows installer wins; the release tagged latest can be mobile-only),
wiztree (file host is diskanalyzer.com/files, which redirects to the
vendor CDN), slack (MSIX provisioned for all users, lab-verified), cpuz and
citrixworkspacecr (fixed 2026-08-31). Also verified in the lab the same
day (install, detect, uninstall, clean): audacity (MSI), defraggler (builds
page + file resource), gpg4win (64-bit view), kdiff3 (32-bit view; uninstall
`/S` alone, `_?=` exits -1), netbeans (new), rstudio (/allusers, 64-bit
view), sharex (x64 asset). Verdicts in REGRESSION-STATUS.md.

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

## Catalog-port definitions: known gaps (177 added, 2026-09-01)

The 177 definitions ported from app-packager's catalog waves carry these
schema-vs-PowerShell gaps. The PS packager remains the source of truth
per the PS-first rule.

**Pinned values that rot on the vendor's schedule:**
- `pdfstudioviewer` — release year hardcoded in `InstallArgs`,
  `UninstallCommand`, `Detection.FilePath`, `Detection.FileName`, and
  `RunningProcess`; annual edit. The scraped `ProductVersion` already
  carries the year as its major part, but no token derives a version part,
  and `InstallArgs` and `RunningProcess` get no substitution at all — the
  fix is a `{version_major}` token in the staging substitution set, not a
  scraper change. The year was 2026 against a published `ProductVersion` of
  2024.0.1, so every one of those five fields pointed at a directory the
  install would not create; corrected to 2024 on 2026-09-01. An annual
  hand-edit that nothing checks is the failure mode here — the token is
  worth doing.

Resolved 2026-09-01 (all four live-verified):
- `omnissahorizonclient` — chained hops resolve the download group from the
  Customer Connect product listing; verified 8.19.0 and a
  download3.omnissa.com URL answering 200.
- `mariadb-server` — chained hops take the highest Stable + LTS series from
  the REST index, then its newest Windows point release; verified 12.3.3.
- `agentransack` — a chained hop captures the tokenized, protocol-relative
  href off the download page; verified 3562 and a 200 on the ZIP. The ZIP
  payload is not a gap: staging already extracts a ZIP and finds the MSI
  inside.
- `rvtools` — the new `HeadProbe` scraper walks the download host from a
  4.8.1 floor; verified 4.8.1 as still current.

**Unverified against a real payload:**
- `windowsadk` / `windowspeaddon` — the `VersionSourceMsi` anchors
  (`Windows Deployment Tools-x86*.msi`, `Windows PE wims
  (DesktopEditions)-x86*.msi`) come from the PS packagers and have not
  been matched against a built layout here; a layout build resolves them
  or fails loudly, since the anchor must match exactly one file. The
  layout itself is ~1.5 GB and needs a download root short enough to keep
  the versioned content path under 120 characters.
- `sqlserver2022express` — wrapper text is unit-verified only; the media
  box (279 MB, HTTP 200) has not been extracted and run here. The
  `InstanceName` setting now drives the install switch, the uninstall
  switch, and the detection key through `{setting:InstanceName}`. The
  `MSSQL16.` prefix on that key stays fixed: it encodes the product major
  version, not the instance, so it tracks the definition rather than the
  setting.

**Systematic downgrades (correct today, fragile):**
- Vendor SHA256 verification dropped (androidstudio,
  omnissahorizonclient). The schema now expresses both a pinned
  `PayloadSha256` and a feed-sourced `VersionScraper.PayloadHashQuery`.
  androidstudio is outstanding only because its feed has not been matched
  to a capture yet. omnissahorizonclient needs more than a capture:
  `sha256checksum` sits in the second chained hop's response, while
  `ResolveFeedHashAsync` re-reads `VersionScraper.Url`, which is now the
  first hop. Hash capture has to reach the last hop of a chained scrape
  before that one can be restored.
- `openwebstart` — install4j varfile forcing all-users context not
  shippable; silent run may land per-user.
- `geogebra` — conditional REINSTALL=ALL vomus (fixed ProductCode, no
  UpgradeCode) not expressible; upgrades may no-op.
