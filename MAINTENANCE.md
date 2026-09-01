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
