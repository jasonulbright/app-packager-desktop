# Packr

**Keeps third-party Windows applications packaged and up to date in ConfigMgr, Intune and WSUS — without hand-building each one.**

Free, MIT-licensed, and free forever.

![Packr](screenshot.png)

## What it does

Packaging an application for enterprise deployment normally means doing the same chore over and over: find the vendor's current version, download the installer, work out the silent install switches, work out how a management system can tell whether the app is already installed, then create the application record by hand. Every app, every update.

Packr does that chore for you. For each application it knows about, it:

1. Checks the vendor for the current version.
2. Downloads that version's installer.
3. Stages the installer and generates install and uninstall scripts.
4. Works out a detection rule — the check that tells ConfigMgr or Intune whether the app is present on a machine — by reading the installer itself.
5. Publishes to the destination you choose: an application in ConfigMgr, a Win32 app in Intune, or a locally published update in WSUS.

You pick applications from a list and press a button. 319 applications ship with it already described, so common software is covered out of the box.

## Who it's for

Windows administrators who maintain third-party software in ConfigMgr (Configuration Manager), Intune or WSUS. You do not need to write scripts to use it — the application definitions are plain data files, and the day-to-day workflow is a normal desktop window with a list and buttons.

If you do want to add your own applications, you can: definitions are single JSON files, there is a guided "Add New App" flow in the application, and you can drop an installer on the Packages page and keep it as an application.

## Quickstart

1. Download the release. The installer (`AppPackagerDesktop-Setup-<version>.exe`) installs for the current user with no elevation and puts a Start Menu shortcut in place; `/S` installs silently. The zip is the same build without an installer: extract it anywhere and run `ApplicationPackagerUI.exe`. Both are self-contained and need no separate .NET runtime.
2. Close the application before installing an update; the installer refuses to replace a running executable.
3. On first launch a short setup window asks which systems you use — ConfigMgr, Intune, WSUS — then asks only for the fields those systems need. Everything it sets is also on the **Settings** page. For ConfigMgr the application tests the connection on launch and shows the result.
4. On the **Packages** page, tick one or more applications and press **Check Latest** to see the current vendor version alongside what your environment already has.
5. Press **Stage** to download and prepare, then **Publish to ConfigMgr**, **Publish to Intune** or **Publish to WSUS**. A button is disabled while its system is not set up; its tooltip names the missing setting.

Nothing is sent anywhere. Version checks go directly from your machine to the vendor.

## Application Workbench

The Workbench is the editor for one application. A definition ships defaults; a profile holds what you change. Each field shows the inherited value and whether your profile overrides it, and **Reset** returns it to the inherited value.

- **Application**: display name, publisher, description, application title, icon.
- **Install & uninstall**: the generated script, the generated script extended with before and after hooks, or a custom script.
- **Detection**: the inherited rule, a typed rule, or a PowerShell detection script with parse diagnostics.
- **Requirements & variants**: requirement rules, variant split, install mode, site condition names.
- **Timing & execution**: run times, install context, logon requirement.
- **Source files**: extra files staged with the installer.
- **Review & build**: the differences from the defaults, compatibility findings, the resolved plan, earlier builds.

**Save** stores the profile and makes it active. **Stage** and **Publish** on the Packages page then build from the active profile. Profiles, builds and drafts are stored under `%LOCALAPPDATA%\AppPackager\Workbench`. An application and its profiles can be exported as a bundle and imported on another computer.

## Script signing

Settings, **Script Signing** signs the scripts a build generates: detection, requirement and deployment scripts. Each category has a Sign switch and a Require switch. Sign is best effort; Require refuses a build or a publish whose scripts in that category do not carry a valid signature. The certificate is selected by thumbprint from the current user or local machine store. With deployment scripts signed, the launchers run without an execution-policy argument, so the endpoint's own policy governs.

## WSUS publishing

**Publish to WSUS** publishes the installer as a locally published update with rules built from the application's detection: the update is offered only where an older version is installed. It needs the WSUS console (or RSAT WSUS tools) on this computer and an account in the WSUS Administrators group. **WSUS Updates** lists the published updates and can approve, decline, expire and remove them and import updates from the Microsoft Update Catalog. Applications that install per user, or whose install does more than run the installer, are refused with a code.

## One Click

One Click checks the tracked applications and stages and publishes the ones with a new version, each to its own destinations. The plan window shows what will run before it runs; **Run** asks nothing after it starts. A destination that refuses an application is remembered and skipped until the version changes. Each run writes a report with a rollback section.

## Command-line build

`AppPackagerBuild.exe` stages or publishes one application through a profile without the window, for scripts and scheduled tasks. It uses the same settings and Workbench data as the window.

```
AppPackagerBuild --application <id> [--profile <name or id>] (--stage | --package | --stage --package) [options]
```

`--target` takes `ContentOnly`, `MECM` (default), `MECMAndIntune`, `IntuneOnly`, `MECMAndWSUS` or `WSUSOnly`. `--on-existing` takes `Skip` (default), `Overwrite` or `Fail`. `--help` lists every option. Nothing prompts: a decision the window would ask for comes from an option or a saved setting.

| Exit code | Meaning |
|---|---|
| 0 | Success |
| 1 | Failure or refusal, or a WSUS approval or decline that failed after a successful publish |
| n | A PS1 packager's own non-zero exit code |

## Relationship to app-packager

Packr is the C# sibling of [app-packager](https://github.com/jasonulbright/app-packager), the PowerShell/WPF packager. Features are proven in app-packager first and then ported here, so app-packager is generally ahead on new capability while Packr is the faster, compiled implementation with the pluggable engine. Both are MIT licensed and free.

## Status

**v2026.1003.16** — 319 application definitions. At parity with app-packager 2026.10.02.0103: Application Workbench with profiles, script signing, WSUS publishing, publish per destination, One Click per destination, bring-your-own installers, bundles and the command-line build, on top of the existing engine (multi-deployment-type applications with requirement rules, Intune Win32 publishing via Graph, MSIX provisioned packages, installer hash and signature verification, script and compound detection, chained scrapers, bootstrapper layouts, drop-to-package analysis).

3,355 C# tests pass and all Release projects build without errors. Lab runs cover ConfigMgr packaging and deployment, WSUS publishing with a client update, and signed builds on a client that enforces AllSigned. Intune publishing is tested against mocked Graph calls only. See [REGRESSION-STATUS.md](REGRESSION-STATUS.md) for per-application verdicts and [CHANGELOG.md](CHANGELOG.md) for the open lab items.

## Stack

- .NET 10, WPF-UI 4.2.0, CommunityToolkit.Mvvm 8.4.2
- ApplicationPackagerEngine class library (scraper plugins, staging pipeline, ConfigMgr, Intune and WSUS targets)
- Windows PowerShell 5.1 subprocess for the ConfigurationManager cmdlets and the WSUS administration API

## Architecture

```
ApplicationPackagerUI (WPF)          ← user interface (WPF-UI 4.2.0 Fluent)
AppPackagerBuild (console)           ← command-line build, same services as the window
  └─ ApplicationPackagerEngine (DLL) ← core logic (portable .NET 10)
       ├─ Scrapers/                  ← IVersionScraper plugins (HttpRegex, JsonQuery, FileVersion, CdnCab)
       ├─ Services/                  ← Download, MSI inspection, 7z extraction, installer ID,
       │                                staging, ConfigMgr packaging, SQLite history
       ├─ Services/Workbench/        ← profiles, run snapshots, stage finalization, build records, bundles
       ├─ Services/Signing/          ← signing policy, certificates, script signing, launcher audit
       ├─ Services/Wsus/             ← WSUS rules, findings, publish, adapter for the WSUS API
       ├─ Services/OneClick/         ← plan, history, reports
       ├─ Models/                    ← AppDefinition, StageManifest, InstallerMetadata, InstallerParameter
       └─ Definitions/              ← JSON app definitions (319 apps, one per file)
```

## Scraper Types

A scraper is the part that finds out what version a vendor currently ships. Each application definition names the one it uses.

| Scraper | Method | Apps |
|---------|--------|------|
| `HttpRegexScraper` | Fetch page/API, pattern-match the version out of the text | 50+ |
| `JsonQueryScraper` | Read the version from a vendor JSON API | 35+ |
| `FileVersionScraper` | Download installer, read EXE/MSI/MSIX properties | 10 |
| `CdnCabScraper` | Office CDN CAB/VersionDescriptor.xml | 6 |
| `WingetScraper` | Winget package manager (stub) | — |

About a third of the definitions read `api.github.com`, which allows 60 requests per hour without a token. Set `GITHUB_TOKEN` or run `gh auth login` to check the whole catalog in one pass.

## JSON App Definition

One file per application. `Pattern` below is a text-matching expression (a regular expression); the guided "Add New App" flow builds it for you if you would rather not write one.

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

## Building a release

```powershell
tools\build-release.ps1
```

Publishes self-contained single-file win-x64 builds of the window and the command-line build, compiles the per-user installer from them when NSIS (`makensis`) is installed, and writes `AppPackagerDesktop-Setup-<version>.exe`, `AppPackagerDesktop-<version>.zip` and `checksums.txt` into `publish\`. `-PublishOnly` and `-PackageOnly` run the two halves separately; `tools\build-installer.ps1` compiles the installer from a stage on its own.

Releases are cut by the Release workflow (Actions, run manually from `main`). It runs the engine tests, publishes, signs the executables with Azure Artifact Signing, compiles the installer from the signed files and signs it too, verifies the signatures, tags `v<version>` from the csproj and publishes the GitHub release with the matching `CHANGELOG.md` section as its notes. Before running it, set `<Version>` in the UI project and give the changelog a section for that version. A version has the form `YYYY.MDD.N`: the year, the month and day as one number without a leading zero (October 3 is `1003`), and the release number, which follows the last release by one. The workflow refuses any other version (`tools\test-release-version.ps1`).

## License

MIT. See [LICENSE](LICENSE).
