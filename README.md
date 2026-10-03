# Packr

Packr packages third-party Windows applications and publishes them to ConfigMgr, Intune and WSUS. It is a Windows desktop application. It is free and MIT licensed.

![The Packages page](docs/images/packages.png)

## What it does

For each application in its catalog, Packr does these steps:

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
- For Intune: an app registration with a client secret. Packr downloads the Microsoft Win32 Content Prep Tool when it is necessary.
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

## Application Workbench

The Workbench is the editor for one application. A definition supplies the defaults. A profile holds your changes. Each field shows the inherited value and shows if the profile overrides it. **Reset** returns a field to the inherited value.

![The Application Workbench](docs/images/workbench.png)

- **Application**: display name, publisher, description, application title, icon.
- **Install & uninstall**: the generated script, the generated script with a before hook and an after hook, or a custom script.
- **Detection**: the inherited rule, a rule that you enter, or a PowerShell detection script with parse diagnostics.
- **Requirements & variants**: requirement rules, variant split, install mode.
- **Timing & execution**: run times, install context, logon requirement.
- **Source files**: more files that are staged with the installer.
- **Review & build**: the differences from the defaults, the compatibility findings, the plan, the earlier builds.

**Save** stores the profile and makes it active. **Stage** and **Publish** on the Packages page then build from the active profile. You can export an application and its profiles as a bundle and import the bundle on a different computer.

## Bring your own installer

Drop an installer on the Packages page. Packr analyses it and shows the installer type, the silent arguments and a detection rule. **Save as Application** keeps it as an application with numbered installer revisions.

## Script signing

**Settings**, **Script Signing** signs the scripts that a build generates: detection scripts, requirement scripts and deployment scripts. Each category has a Sign switch and a Require switch. With Sign, Packr signs the scripts when it can. With Require, Packr refuses a build or a publish when a script in that category has no valid signature. You select the certificate by thumbprint from the current user store or the local machine store. When deployment scripts are signed, the launchers run without an execution-policy argument, and the policy of the endpoint applies.

## WSUS publishing

**Publish to WSUS** publishes the installer as a locally published update. The rules of the update come from the detection of the application: WSUS offers the update only to a computer that has an older version. **WSUS Updates** lists the published updates. It can approve, decline, expire and remove an update, and it can import an update from the Microsoft Update Catalog.

Packr refuses an application that installs for the signed-in user, and an application whose install script does more than run the installer. The refusal gives a code and the reason.

## One Click

One Click checks the applications that you track. It stages and publishes each application that has a new version, to the destinations of that application. The plan window shows the work before it starts. After you select **Run**, One Click asks no more questions. When a destination refuses an application, One Click remembers the refusal until the version changes. Each run writes a report with a rollback section.

![The One Click plan](docs/images/one-click.png)

## Command-line build

`AppPackagerBuild.exe` stages or publishes one application through a profile, without the window. Use it in scripts and scheduled tasks. It uses the same settings and the same Workbench data as the window.

```
AppPackagerBuild --application <id> [--profile <name or id>] (--stage | --package | --stage --package) [options]
```

`--target` takes `ContentOnly`, `MECM` (default), `MECMAndIntune`, `IntuneOnly`, `MECMAndWSUS` or `WSUSOnly`. `--on-existing` takes `Skip` (default), `Overwrite` or `Fail`. `--help` lists all options. The command does not prompt: an option or a saved setting supplies each decision.

| Exit code | Meaning |
|---|---|
| 0 | Success |
| 1 | Failure or refusal, or a WSUS approval or decline that failed after a successful publish |
| n | The non-zero exit code of a PS1 packager |

## Application definitions

A definition is one JSON file in the `Definitions` folder. The **Add New App** page writes a definition for you.

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

- Settings are in `%LOCALAPPDATA%\AppPackager`. Workbench profiles, builds and drafts are in `%LOCALAPPDATA%\AppPackager\Workbench`. The environment variable `APP_PACKAGER_DATA_ROOT` moves both.
- Packr has no telemetry. It connects to vendor sites for version checks and downloads, to GitHub for the icon pack and for definitions that read GitHub releases, and to the systems that you set up.
- The Intune client secret is stored encrypted for the current Windows user.

## Validation

ConfigMgr packaging and deployment, WSUS publishing with a client update, and signed builds on a client that enforces AllSigned are validated on a lab site with client computers. Intune publishing is tested against simulated Graph responses only.

## Relationship to app-packager

Packr is the C# counterpart of [app-packager](https://github.com/jasonulbright/app-packager), the PowerShell packager. A feature is released in app-packager first and is then ported to Packr. The two products are MIT licensed.

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

The Release workflow makes a release. Run it manually from `main`. It runs the tests, publishes and signs the executables, compiles and signs the installer, creates the tag `v<version>` and publishes the GitHub release. Before you run it, set `<Version>` in the UI project and add a section for that version to `CHANGELOG.md`. A version has the form `YYYY.MDD.N`: the year, then the month and day as one number without a leading zero (October 3 is `1003`), then the release number, which is the number of the last release plus one. `tools\test-release-version.ps1` checks the version.

## License

MIT. See [LICENSE](LICENSE).
