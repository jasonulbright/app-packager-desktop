# Data folders and files

This page lists every folder and file that App Packager Desktop writes.

## Roots

All paths are per Windows user.

| Name used on this page | Default location | With `APP_PACKAGER_DATA_ROOT` set |
|---|---|---|
| Data | `%LOCALAPPDATA%\AppPackager` | `<value>\Users\<Windows SID>` |
| Legacy | `%LOCALAPPDATA%\SRLPackr` | `<value>\Users\<Windows SID>\SRLPackr` |

The Legacy folder keeps the name of an earlier version of the product. It holds session logs, the shared inventory and downloaded tools.

Rules for the roots:

- The Data folder and the Workbench folder cannot be under Program Files.
- They cannot be a junction or a symbolic link.
- Each tool refuses a report or template folder that is a junction or a symbolic link.
- An explicitly configured staging folder stays where you set it.

## Settings, cache and reports

| Location | Content | Retention |
|---|---|---|
| `Data\settings.json` | All settings. See [Settings reference](settings.md). During a replace, temporary files `settings.json.*.tmp` and `.bak` exist for a short time. | The app keeps `settings.json.unreadable-<yyyyMMdd-HHmmss>` when it replaces an unreadable file. |
| `Data\condition-templates.json` | Condition templates that the Workbench writes (CPU architecture, language and VPN conditions). | The app writes it only when a value changed. |
| `Data\monitor-config.json` | The configuration of the CVE report run. See [Command-line build](../core/command-line-build.md). | The app writes a missing file with defaults. |
| `Data\Vulnerability\vuln-cache.json` | The CVE cache. | The app deletes entries older than 14 days. A cache entry answers for 360 minutes (60 minutes for a Shodan result). |
| `Data\Vulnerability\kev-catalog.json` | A copy of the CISA KEV catalog. | The app loads the catalog again after 24 hours. |
| `Data\Reports\cve-report-*` (`.html`, `.csv`, `.json`, `.xlsx`, `.md`) | CVE reports. | `--keep-report-days` deletes the reports of that command that are older than n days. The default is 90. 0 keeps all. |

## Staging

| Location | Content | Retention |
|---|---|---|
| `Data\Staging\<Application>-<Arch>\<version>\` (default `DownloadRoot`) | Staged content: the installer, `install.ps1` or `install.bat`, `uninstall.ps1` or `uninstall.bat`, `exitcode.txt`, `stage-manifest.json`. | The code states no retention. |
| `Data\Staging\_cache\` | The shared download cache. | The code states no retention. |
| `Data\FileVersionCache\` | Installers that a `FileVersion` scraper downloaded, so that Stage can reuse them. **Test Scrape** in Add from web with this scraper type leaves its file here. | A file stays until a newer release replaces it or you delete the folder. The app deletes the oldest files when the folder passes 2 GiB. At the first use the app deletes the old `%TEMP%\SRLPackr-FileVersionCache\` folder and copies nothing from it. |
| `Data\Staging\profiles\<ProfileId>\...` | Staged content of a non-default Workbench profile. | The code states no retention. |

## Workbench

| Location | Content |
|---|---|
| `Data\Workbench\applications\<key>\application.json` | Workbench applications. |
| `profiles\<profile>\profile.json` in the application folder | Profiles. |
| `sources\` in the application folder | Revisions of a bring-your-own installer. |
| `.lock` files | Locks beside the stores. |
| `Data\Workbench\runs\` | Run snapshots and the build records `build.json`. |
| `Data\Workbench\drafts\<key>\<profile>.draft.json` | Drafts. |
| `Data\Workbench\definitions\*.json` | User definitions. The icon overrides and the icon pack are stored here too. |
| A path that you choose, with `bundle.json` | Workbench export and import bundles. |

See [Workbench](../core/workbench.md) and [Definitions](../core/definitions.md).

## Tool folders

| Location | Content | Retention |
|---|---|---|
| `Data\DetectionTester\history.json`, `Reports\`, `run\ap-detection-<guid>.ps1` | Detection Tester history, reports, temporary script files. | The history holds 500 entries. The app sweeps script files older than 1 day. |
| `Data\MaintenanceWindows\Templates\*.json`, `Reports\` | Your templates and the reports. | The code states no retention. |
| `Data\Collections\Templates\`, `Reports\`, `run-*` folders | Your query templates (file limit 4 MB), reports, and the folders of compliance builder runs. | The app removes a `run-*` folder after the run. |
| `Data\InstallerAnalysis\Temp\<session>\` (with `session-owner.txt`), `Reports\` | Extracted payload and reports. | The app removes the extracted payload on back, on leave, on close and at the next start. |
| `Data\Deployments\deployment-audit.jsonl` (with a lock file), `Reports\deployment-history*`, `Templates\*.json` | The Deployment Helper audit chain, reports, your templates. | You can set the audit path. A UNC path is allowed. |
| `Data\Deployments\Rings\*.json`, `Rings\runs\<run>.json` (with a lock), `Rings\runs\closed\` | Ring plans and run files. | You can set the folders. UNC paths are allowed. |
| `Data\DpContent\dp-content-audit.jsonl`, `Reports\` | The DP Content Manager audit chain and reports. | The code states no retention. |
| `Data\PivotsScripts\pivots-scripts-audit.jsonl`, `Sessions\`, `Reports\` (CSV and `.about.txt`) | The Pivots and Scripts audit chain, saved runs, reports. A saved run holds no parameter values and no script text. | The code states no retention. A library folder is optional and you choose it. |
| `Data\SiteHygiene\site-hygiene-audit.jsonl`, `suppressions.json`, `last-scan.json`, `alerts.log`, `metrics-history.csv`, `Reports\` (CSV and `.about.txt`) | The Site Hygiene audit chain, suppressions, the last scan, alerts, metrics, reports. | `metrics-history.csv` keeps 180 days. |

## Logs, inventory and tools (Legacy folder)

| Location | Content | Retention |
|---|---|---|
| `Legacy\Logs\packr-yyyyMMdd-HHmmss.log` | The App Packager page operation log, one file for each session. | The app keeps the newest 30. |
| `Legacy\packr.log` | The rolling App Packager log. | The code states no retention. |
| `Legacy\Logs\app-yyyyMMdd-HHmmss-<pid>.log` | The application log of the tools. The log masks values of keys named password, secret, token, apikey and credential. | The app keeps the newest 30. |
| `Legacy\Logs\build-yyyyMMdd-HHmmss-<pid>.log` | The log of a command-line run. `--log-folder` changes the folder. | The code states no retention. |
| `Legacy\Logs\OneClick\one-click-<yyyyMMdd-HHmmss>.md` and `.json` | One Click reports. Each has a rollback section. A name clash adds `-2`, `-3`. | The code states no retention. |
| `Legacy\history.db` | The shared application inventory and operation history (SQLite). The window and the command line share it. | The Operation History window shows the latest 100 operations of the selected application. |
| `Legacy\icons\` | The icon cache. | The code states no retention. |
| `Legacy\Tools\IntuneWinAppUtil.exe` | The downloaded Content Prep Tool. | The code states no retention. |

## Other locations

| Location | Content |
|---|---|
| `<Packagers folder>\packager-preferences.json`, `citrix-workspace-switches.json` | Written for PS1 packagers. |
| `<FileShareRoot>\Applications\<Vendor>\<App>\<Version>\` (layout `Nested`) or `<FileShareRoot>\Applications\<Vendor>-<App>-<Version>\` (layout `Flat`) | The content share for `Publish to ConfigMgr`. A ConfigMgr run writes only the content here. |
| `%TEMP%\ap-cmsession-*.ps1`, `%TEMP%\apkg-hdr-*.txt`, `%TEMP%\appackager-sign-*`, and the WSUS adapter files `request.json` and `result.json` in a temporary folder | Short-lived files. |
| `<DownloadRoot>\AddFromWeb\<8 characters>\` | The installer that step 4 of Add from web downloaded. The app deletes the folder when you save, when a new analysis starts, when an analysis fails and when you select **Done**. |
| The folder where you chose the installer | The installer file of a definition from Add from file. The definition keeps the path, the SHA-256 and the version. The app does not copy or move the file. |
| `%LOCALAPPDATA%\Update Services\SoftwareDistribution.log` | The WSUS administration API wrote this file in the user profile after the WSUS connection test (Settings, WSUS Publishing, Test connection) ran without elevation. The app does not manage this file. |
| A path that you choose | CSV exports from the App Packager grid, the exported certificate (`.cer`), an imported PFX, the update ID text file. The app writes these only through Save and Open dialogs. |

## Related pages

- [Settings reference](settings.md)
- [Network calls](network.md)
