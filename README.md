# App Packager Desktop

[![CI](https://github.com/jasonulbright/app-packager-desktop/actions/workflows/ci.yml/badge.svg?branch=main&event=push)](https://github.com/jasonulbright/app-packager-desktop/actions/workflows/ci.yml)
[![Release workflow](https://img.shields.io/github/actions/workflow/status/jasonulbright/app-packager-desktop/release.yml?label=release%20workflow)](https://github.com/jasonulbright/app-packager-desktop/actions/workflows/release.yml)
[![Latest release](https://img.shields.io/github/v/release/jasonulbright/app-packager-desktop?label=release)](https://github.com/jasonulbright/app-packager-desktop/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/jasonulbright/app-packager-desktop/total?label=downloads)](https://github.com/jasonulbright/app-packager-desktop/releases)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D4)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](#build-from-source)
[![License](https://img.shields.io/github/license/jasonulbright/app-packager-desktop)](LICENSE)

App Packager Desktop packages third-party Windows applications and publishes them to ConfigMgr, Intune and WSUS. It is a Windows desktop application. It is free and MIT licensed.

![The Packages page](docs/images/packages.png)

## What it does

For each application in its catalog, App Packager Desktop does these steps:

1. It reads the current version from the vendor.
2. It downloads the installer of that version.
3. It stages the installer and writes the install and uninstall scripts.
4. It builds a detection rule from the installer.
5. It publishes to the destination you select: an application in ConfigMgr, a Win32 app in Intune, or a locally published update in WSUS.

The catalog has 319 application definitions. A definition is one JSON file. You can add your own definitions.

## Requirements

- Windows 10, Windows 11 or Windows Server, 64-bit.
- Windows PowerShell 5.1.
- For ConfigMgr: the Configuration Manager console on this computer and an account with rights to create applications.
- For WSUS: the WSUS console or the RSAT WSUS tools on this computer, at the version of the server, and an account in the WSUS Administrators group.
- For Intune: an app registration with a client secret. App Packager Desktop downloads the Microsoft Win32 Content Prep Tool when it is necessary.
- 7-Zip, for applications whose installer is an archive.

The release is self-contained. A separate .NET runtime is not necessary.

## Install

1. Download the release.
   - The installer `AppPackagerDesktop-Setup-<version>.exe` installs for the current user without elevation and adds a Start Menu shortcut. `/S` installs silently.
   - The zip `AppPackagerDesktop-<version>.zip` is the same build without an installer. Extract it and run `ApplicationPackagerUI.exe`.
2. Close the application before you install an update. The installer does not replace an executable that is in use.

## First use

1. On the first start, the Setup window asks which systems you use: ConfigMgr, Intune, WSUS. It then asks only for the settings that those systems need. All settings are also on the **Settings** page.
2. On the **Packages** page, select one or more applications. Select **Check Latest** to read the current vendor version.
3. Select **Stage** to download and prepare the content.
4. Select **Publish to ConfigMgr**, **Publish to Intune** or **Publish to WSUS**. A button is disabled while its system is not set up. Its tooltip names the setting that is missing.

In **Settings**, ConfigMgr can request content distribution and then create an Available deployment to a test collection. Select **Use a user collection** to target users; leaving it cleared targets devices. Collection type is independent of the application's install context. If you enable collection creation, the new collection starts empty; add pilot members in ConfigMgr. See [deployment behavior](docs/deployment-behavior.md) for outcomes and validation limits.

## Application Workbench

The Workbench is the editor for one application. A definition supplies the defaults. A profile holds your changes. Each field shows the inherited value and shows if the profile overrides it. **Reset** returns a field to the inherited value.

![The Application Workbench review and build page](docs/images/workbench.png)

- **Application**: display name, publisher, description, application title, icon.
- **Install & uninstall**: the generated script, the generated script with a before hook and an after hook, or a custom script.
- **Detection**: the inherited rule, a rule that you enter, or a PowerShell detection script with parse diagnostics.
- **Requirements & variants**: requirement rules, variant split, install mode.
- **Timing & execution**: run times, install context, logon requirement.
- **Source files**: more files that are staged with the installer.
- **Review & build**: the differences from the defaults, the compatibility findings, the plan, the earlier builds.

**Save** stores the profile and makes it active. **Stage** and **Publish** on the Packages page then build from the active profile. You can export an application and its profiles as a bundle and import the bundle on a different computer.

## Bring your own installer

Drop an installer on the Packages page. App Packager Desktop analyzes it and shows the installer type, the silent arguments and a detection rule. **Save as Application** keeps it as an application with numbered installer revisions.

## Script signing

**Settings**, **Script Signing** signs the scripts that a build generates: detection scripts, requirement scripts and deployment scripts. Each category has a Sign switch and a Require switch. With Sign, App Packager Desktop signs the scripts when it can. With Require, App Packager Desktop refuses a build or a publish when a script in that category has no valid signature. You select the certificate by thumbprint from the current user store or the local machine store. When deployment scripts are signed, the launchers run without an execution-policy argument, and the policy of the endpoint applies.

## WSUS publishing

**Publish to WSUS** publishes the installer as a locally published update. The rules of the update come from the detection of the application: WSUS offers the update only to a computer that has an older version. **WSUS Updates** lists the published updates. It can approve, decline, expire and remove an update, and it can import an update from the Microsoft Update Catalog.

App Packager Desktop refuses an application that installs for the signed-in user, and an application whose install script does more than run the installer. The refusal gives a code and the reason.

## One Click

One Click checks the applications that you track. It stages and publishes each application that has a new version, to the destinations of that application. The plan window shows the work before it starts. After you select **Run**, One Click asks no more questions. When a destination refuses an application, One Click remembers the refusal until the version changes. Each run writes a report with a rollback section.

The plan shows `WSUS: not supported` for an application whose definition declares that WSUS cannot take it. One Click does not publish to a destination that holds a newer version than the vendor reports. One Click publishes again when the Workbench profile or the signing policy changed after the last publish.

## Discover unmanaged software

**Discover unmanaged software** finds hidden applications that your clients run and that ConfigMgr does not manage. It reads the Add/Remove Programs inventory in the site database. A hidden application is offered when at least one client reports it and the site has no ConfigMgr application for it.

The prompt reads "Discovered N apps. Enable console view?". The checklist shows each application and the number of clients that report it.

![The Discover unmanaged software prompt with the application, vendor and client count of each discovered application](docs/images/discover.png)

- **Enable selected** and **Enable all** show the applications in the Packages grid.
- **Not now** closes the prompt and changes nothing.
- **Don't ask again for selected** stops Discover from offering the checked applications. With no application checked, the button changes nothing. **Settings** shows the list as **Not offered by Discover**. **Clear list** empties it when you select **Save**.

How the match works:

- A definition names its Add/Remove Programs entry with `ArpName`: one or more wildcard patterns, separated by ` | `. The value `none` means that the application has no stable entry. The optional `ArpVersion` and `ArpArch` fields narrow the match by version and by architecture. 295 of 319 definitions carry a pattern.
- Some applications share one name with a sibling: Citrix Workspace CR and LTSR, QGIS and QGIS LTR, NVIDIA GeForce and RTX Enterprise, and the Microsoft 365 Apps x64 and x86 editions with Project and Visio. Discover cannot tell which variant a client runs. It offers one application of each group and never offers its siblings. Enabling an application only shows its row. Check ConfigMgr then shows its real state.
- An application counts as managed when a current ConfigMgr application title starts with its ConfigMgr name and the rest is a version or a detail in parentheses. A title with another installer method (EXE or MSI) or another architecture does not count. Check ConfigMgr uses the same rule.

Requirements and limits:

- Run App Packager Desktop as a domain account on a domain-joined computer. A computer outside the domain cannot sign in to the database, and Discover shows a plain message.
- The account needs `db_datareader` on `CM_<site code>`. Discover uses Windows authentication only.
- Discover reads the SQL Server name and the database name from the SMS Provider, over DCOM first and WinRM second. **Settings**, **Discovery SQL Server** and **Discovery Database** replace both values. With a site code missing, enter both fields.
- Discover reads inventory that clients have reported. It shows the age of the newest inventory and the number of clients. When the inventory has no rows, it reports "no inventory data". Hardware inventory must collect the Add/Remove Programs classes.
- The database views bypass ConfigMgr role-based access. The scan counts all clients, whatever scope your console role has. Discover shows counts only. It never shows or stores a client name.
- A scan stops after 600 seconds, when you select **Cancel**, and when you close the main window.

## CVE lookup

**Check CVEs** looks up the CVEs that an update fixes. It applies to each checked application whose ConfigMgr version is older than the vendor version. A definition names its product with a `Cpe` value; 294 definitions have one.

- The grid shows the count, the highest CVSS score, the CISA KEV flag, the count that the latest release fixes, and the date of the newest CVE.
- Select a count to open the CVE window. It lists each CVE with its fix version, published date, modified date, and links to NVD, CVE.org, EUVD, OpenCVE, cvedetails and OSV.
- **CVE Report** writes the checked applications as HTML, CSV, XLSX, Markdown and JSON.
- **Settings** holds the NVD API key and the switch **Look up CVEs after Check ConfigMgr**. A key is optional. Without a key, NVD permits 5 requests in 30 seconds; with a key, 50.

The data comes from NVD. When NVD does not answer, the lookup uses Shodan CVEDB and marks the result. EPSS scores come from FIRST. A lookup that fails shows an error; it does not show a count of 0.

## Command-line build

`AppPackagerBuild.exe` stages or publishes one application through a profile, without the window. Use it in scripts and scheduled tasks. It uses the same settings and the same Workbench data as the window.

```
AppPackagerBuild --application <id> [--profile <name or id>] (--stage | --package | --stage --package) [options]
```

`--target` takes `ContentOnly`, `MECM` (default), `MECMAndIntune`, `IntuneOnly`, `MECMAndWSUS` or `WSUSOnly`. `--on-existing` takes `Skip` (default), `Overwrite` or `Fail`. `--help` lists all options. The command does not prompt: an option or a saved setting supplies each decision.

| Exit code | Meaning |
|---|---|
| 0 | Success |
| 1 | Failure or refusal, or a WSUS warning after a successful publish |
| n | The non-zero exit code of a PS1 packager |

### CVE report run

`AppPackagerBuild --cve-report` checks the vendor version, reads the ConfigMgr version, looks up the CVEs and writes the reports, without the window. Use it in a scheduled task.

```
AppPackagerBuild --cve-report [--apps <id,id,...>] [--report-folder <path>] [--report-formats <list>] [options]
```

| Option | Function |
|---|---|
| `--apps` | Limits the run to these application ids |
| `--skip-latest`, `--skip-mecm`, `--skip-cve` | Omits the vendor check, the ConfigMgr query or the CVE lookup. The report then shows stored versions and cache results, and marks them |
| `--report-folder`, `--report-formats` | Report location and formats (`Html`, `Csv`, `Json`, `Xlsx`, `Markdown`) |
| `--drop-folder` | Copies the reports to a second folder |
| `--webhook-url`, `--notify-always` | Posts the run summary as JSON to an https address when an update exists, or after each run |
| `--keep-report-days` | Deletes reports of this command that are older (default 90; 0 keeps all) |
| `--config` | Path of `monitor-config.json`, which holds the report and notification defaults |

The exit code is 1 when a vendor check, a ConfigMgr query or a CVE lookup failed. The NVD API key comes from Settings or from the `NVD_API_KEY` environment variable.

## Application definitions

A definition is one JSON file. Shipped definitions are in the installation's `Definitions` folder. **Add New App** and **Save as definition** write your definitions into the per-user Workbench `definitions` folder, by default `%LOCALAPPDATA%\AppPackager\Workbench\definitions`. A user definition with the same `Id` overrides the shipped definition.

```json
{
  "Schema": 1,
  "Id": "unique-opaque-id",
  "Vendor": "Google",
  "Application": "Google Chrome",
  "InstallerType": "MSI",
  "VersionScraper": {
    "Type": "HttpRegex",
    "Url": "https://api.example.com/versions",
    "Pattern": "\"version\":\"(\\d+\\.\\d+\\.\\d+\\.\\d+)\""
  },
  "DownloadUrl": "https://example.com/installer-{version}.msi",
  "Detection": {
    "Type": "RegistryKeyValue",
    "RegistryKeyTemplate": "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{ProductCode}"
  }
}
```

The scraper reads the current version from the vendor. Each definition names its scraper type.

| Scraper type | Method |
|---|---|
| `HttpRegex` | Gets a page or an API response and matches the version with a regular expression |
| `ChainedHttpRegex` | Follows a sequence of pages, each matched with a regular expression |
| `JsonQuery` | Reads the version from a JSON API |
| `FileVersion` | Downloads the installer and reads the version from its properties |
| `HeadProbe` | Probes the download host for the newest build number |
| `CdnCab` | Reads the Office CDN version descriptor |
| `DellCatalog` | Reads the Dell update catalog |

About one third of the definitions read `api.github.com`. Without a token, GitHub permits 60 requests each hour. Set `GITHUB_TOKEN` or run `gh auth login` to check the full catalog in one pass.

## Data and network

- Settings are in `%LOCALAPPDATA%\AppPackager`. Workbench profiles, builds and drafts are in `%LOCALAPPDATA%\AppPackager\Workbench`. Staging defaults to `%LOCALAPPDATA%\AppPackager\Staging`. An `APP_PACKAGER_DATA_ROOT` override places settings and these default data folders under `<override>\Users\<Windows SID>`; an explicitly configured staging folder stays configured.
- Session logs, downloaded tools and the shared application inventory (`history.db`) normally live under `%LOCALAPPDATA%\SRLPackr`. With a data-root override they live under `<override>\Users\<Windows SID>\SRLPackr`. **Operation History** on the Packages page shows the selected application's latest 100 operations.
- App Packager Desktop has no telemetry. It connects to vendor sites for version checks and downloads, to GitHub for the icon pack and for definitions that read GitHub releases, and to the systems that you set up.
- The Intune client secret and the NVD API key are stored encrypted for the current Windows user.
- CVE data: the cache is `Vulnerability\vuln-cache.json` and the KEV copy is `Vulnerability\kev-catalog.json` under the settings folder. Reports are in `Reports`. A cache entry is used for 360 minutes, or 60 minutes for a Shodan result. Entries older than 14 days are deleted.

The CVE lookup sends these requests. The window or `AppPackagerBuild.exe` sends them over https; no other process does.

| Host | Request | When |
|---|---|---|
| `services.nvd.nist.gov` | CVE query for one product and version range | One query for each application with an update and no current cache entry |
| `cvedb.shodan.io` | CVE query for one product version | Only when the NVD query failed |
| `api.first.org` | EPSS scores for the CVE ids found | One request for each 100 ids |
| `www.cisa.gov` | Known Exploited Vulnerabilities catalog | One time in 24 hours |
| The webhook host you set | Run summary | `--cve-report` only |

Discover unmanaged software sends no request to the internet. It connects to the SMS Provider (CIM over DCOM, then WinRM) and to the site database (SQL Server, Windows authentication, read-only). It reads these views: `v_GS_ADD_REMOVE_PROGRAMS`, `v_GS_ADD_REMOVE_PROGRAMS_64`, `v_R_System`, `v_Applications` and `v_ConfigurationItems`. The settings file stores the two lists (`DiscoveryDismissed`, `DiscoveryEnabled`) and the two optional override fields. It stores no credential.

A link in the CVE window opens in your browser. App Packager Desktop sends no request to the linked site. The log shows each request address. It does not show the API key, and it shows only the host of the webhook address.

## Validation

ConfigMgr packaging and deployment, WSUS publishing with a client update, and signed builds on a client that enforces AllSigned are validated on a lab site with client computers. Intune publishing is tested against simulated Graph responses only.

The CVE lookup, the CVE reports, the command-line CVE report run, the ConfigMgr title lookup and the One Click skip and republish rules are validated on the lab site. The user-collection option and the publication failure handling have automated tests only; they are not validated against live ConfigMgr, Intune or WSUS.

## Relationship to app-packager

App Packager Desktop is the C# counterpart of [app-packager](https://github.com/jasonulbright/app-packager), the PowerShell packager. The two products are MIT licensed.

## Build from source

Prerequisites: the .NET 10 SDK. NSIS (`makensis`) is necessary only for the installer.

```powershell
dotnet test tests\ApplicationPackagerEngine.Tests\ApplicationPackagerEngine.Tests.csproj -c Release
tools\build-release.ps1
```

`tools\build-release.ps1` publishes self-contained single-file win-x64 builds of the window and of the command-line build. It compiles the installer when NSIS is installed. It writes `AppPackagerDesktop-Setup-<version>.exe`, `AppPackagerDesktop-<version>.zip` and `checksums.txt` into `publish\`.

| Project | Content |
|---|---|
| `src\ApplicationPackagerUI` | The window (WPF, WPF-UI, CommunityToolkit.Mvvm) |
| `src\ApplicationPackagerBuild` | The command-line build; it uses the same services as the window |
| `src\ApplicationPackagerEngine` | Scrapers, staging, the ConfigMgr, Intune and WSUS targets, Workbench, signing, One Click, and the definitions |
| `tests\ApplicationPackagerEngine.Tests` | The tests |

The Release workflow makes a release. Run it manually from `main`. It runs the tests, publishes and signs the executables, compiles and signs the installer, creates the tag `v<version>` and publishes the GitHub release. Before you run it, set `<Version>` in the UI project and add a section for that version to `CHANGELOG.md`. A version has the form `YYYY.MDD.N`: the year, then the month and day as one number without a leading zero (October 3 is `1003`), then the release number, which is the number of the last release plus one. `tools\test-release-version.ps1` checks the version. [RELEASING.md](RELEASING.md) gives the full procedure.

## License

MIT. See [LICENSE](LICENSE).
