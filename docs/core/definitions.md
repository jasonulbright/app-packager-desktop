# Definitions

## What it does

A definition is one JSON file that describes one application. It names the vendor, the place to read the current version, the download address, the install and uninstall commands and the rule that detects the installed application. The App Packager page shows one row for each definition.

Shipped definitions are in the `Definitions` folder beside `ApplicationPackagerUI.exe`. The release ships 323 definitions. You can add your own definitions and you can replace a shipped definition.

This page explains how definitions work. Every field is in [Definition format](../reference/definition-format.md).

## When to use it

- To learn what a row on the App Packager page reads and uses.
- To add an application: see [Add from web](add-app.md), [Add from file](add-from-file.md), or the steps below.
- To change a shipped definition: see [Replace a shipped definition](#replace-a-shipped-definition).
- To find out why **Check Latest** returns an error for a row.

## Requirements

- A text editor, to change a definition file.
- Write access to the user definitions folder. By default it is `%LOCALAPPDATA%\AppPackager\Workbench\definitions`.

## Where definitions are

| Folder | Content |
|---|---|
| `Definitions` beside the executable | The shipped definitions. |
| `Definitions\Icons` beside the executable | The icons of the definitions that have `IconSource` `External`. |
| User definitions folder | Definitions that you add, and copies of shipped definitions that you changed. |

- App Packager Desktop reads every `*.json` file in the two definition folders. It sorts the files by name.
- The property names are not case sensitive.
- A file that App Packager Desktop cannot read as JSON, or a file with an empty `Id`, does not appear in the App Packager grid. The other definitions still load.
- [Add from web](add-app.md) and [Add from file](add-from-file.md) write new files to the user definitions folder. They never overwrite a file.
- The paths with the `APP_PACKAGER_DATA_ROOT` and `APP_PACKAGER_WORKBENCH_ROOT` overrides are in [Data folders](../reference/data-folders.md).

### Which definition wins

The `Id` identifies a definition. The comparison ignores case.

- A user definition with the `Id` of a shipped definition replaces the shipped definition. The row keeps its catalog identity.
- A user definition with a new `Id` is a custom application.
- If two user files have the same `Id`, the file that sorts first by name is used.

App Packager Desktop also finds PS1 packager scripts named `package-*.ps1`. It looks in the folder under Settings, Packagers Folder, and in the `Packagers` folder beside the executable. No PS1 packager ships. A JSON definition replaces a PS1 packager with the same application name. The [Catalog](applications.md) marks these rows with the badge `PS1`.

## What a definition controls

| Action | Fields it reads |
|---|---|
| **Check Latest** | `VersionScraper`, `DisplayVersionOverride` |
| **Check ConfigMgr** | `CMName` |
| **Discover unmanaged software** | `ArpName`, `ArpVersion`, `ArpArch` |
| **Check CVEs** | `Cpe`, `SecurityAdvisoryUrl` |
| **Stage** | `DownloadUrl`, `InstallerFileName`, `InstallerType`, `InstallArgs`, `UninstallArgs`, `UninstallCommand`, `Detection`, the hash and signature fields, and the other install fields |
| The One Click plan and the WSUS publish | `WsusSupport` |
| Install Parameters | `InstallerSettings`, `ReviewBeforeStage` |

## Version sources (scrapers)

The `VersionScraper.Type` field names the scraper. The comparison ignores case. An unknown type gives the status **Error** and the log line `ERROR: No scraper registered for type '<type>'`.

| Type | Definitions | Method | Example |
|---|---|---|---|
| `HttpRegex` | 209 | Gets a page or an API answer and matches the version with a regular expression. | `putty.json` |
| `JsonQuery` | 68 | Reads the version from a JSON API with a path expression. | `notepadplusplus.json` |
| `FileVersion` | 31 | Downloads the installer and reads the version from its properties. | `amazondcv.json` |
| `ChainedHttpRegex` | 7 | Follows a sequence of pages. Each page is matched with a regular expression. | `agentransack.json` |
| `CdnCab` | 6 | Reads the Office CDN version descriptor. | `m365apps-x64.json` |
| `DellCatalog` | 1 | Reads the Dell update catalog. | `dellcommandupdate.json` |
| `HeadProbe` | 1 | Probes the download host for the newest build number. | `rvtools.json` |

How `Pattern` works for each type:

- `HttpRegex`: a regular expression. Group 1 holds the version. The special value `LAST_MODIFIED` reads the Last-Modified header of the page as a date version (`sysinternals.json`). The prefix `REDIR:` matches the expression against the final address after the redirects, not against the page (`azurecli.json`).
- `JsonQuery`: a path. `name` reads a property. `[N]` reads an array item. `[?key=value]` reads the first item where the key equals the value. `[?key!=value]` reads the first item where it does not. `[?k1=v1,k2=v2]` needs all conditions. `[max:field]` reads the item with the highest version in the field. Example: `[?name=studio,version=latest,os=windows].source` in `anypointstudio.json`.
- `FileVersion`: `FileVersion`, `ProductVersion`, `MsiProductVersion` or `MsixManifest`. Any other value reads `ProductVersion`.
- `ChainedHttpRegex`: the value `CHAINED`. The hops hold the expressions. Each hop fetches a page and applies its expression. The capture goes into the address of the next hop as `{capture}`. The named groups `version` and `url` hold the result.
- `CdnCab`: `I640Version` for the 64-bit version or `I320Version` for the 32-bit version. `Url` is the channel `.cab` file of the Office CDN.
- `HeadProbe`: the oldest version that the walk may start from. `Url` holds `{version}`. The walk goes up from this version while the host serves the file.

The scraper first returns the raw version. The `VersionTransform` field then produces the display version. A transform is a chain of steps that you separate with `|`: `strip_prefix:X`, `insert_dot:N`, `replace:X:Y` and `regex_replace:pattern:replacement`. An empty replacement deletes the match. `DisplayVersionOverride` replaces the display version. Its value `{version}` gives the raw version back.

In `DownloadUrl`, `{version}` is the raw version and `{display_version}` is the display version. A `DownloadUrlQuery` finds the address in the same answer. A found address replaces the `DownloadUrl` template. 75 shipped definitions have an empty `DownloadUrl` because the scraper finds the address.

101 of the 323 definitions read `api.github.com`. Without a token, GitHub permits 60 requests each hour. App Packager Desktop takes a token from `APP_PACKAGER_GITHUB_TOKEN`, then `GITHUB_TOKEN`, then `gh auth token`.

## Detection types

The `Detection` field decides when ConfigMgr, Intune or WSUS counts the application as installed.

| Type | Checks | Definitions | Example |
|---|---|---|---|
| `RegistryKeyValue` | A value under a registry key, compared with the version. | 215 | `7zip.json` |
| `File` | The version of a file, or that the file exists. | 84 | `notepadplusplus.json` |
| `RegistryKey` | That a registry key exists. | 10 | `corretto-jdk8-x64.json` |
| `Script` | That a script writes text to the output stream. | 6 | `tableaudesktop.json` |
| `Compound` | Several clauses that you join with `And` or `Or`. | 4 | `nomachine.json` |
| `ArpEntry` | An Add/Remove Programs entry. The stage turns it into a script. | 4 | `aspnethostingbundle10.json` |

## Counts in the shipped catalog

These counts come from the 323 files.

| Item | Count |
|---|---|
| Vendors | 200 |
| `InstallerType` | `EXE` 159, `MSI` 155, `ODT` 6, `MSIX` 2, `ZIP` 1 |
| `InstallerFramework` | `NativeMSI` 149, `InnoSetup` 56, `NSIS` 41, `WixBurn` 24, `NativeEXE` 16, `Unknown` 13, `InstallShield` 9, `ODT` 6, `MSIX` 3, `Squirrel` 3, `ZIP` 2, `AdvancedInstaller` 1 |
| `Architecture` | `x64` 302, `x86` 20, `ARM64` 1 |
| `InstallContext` `User` | 12 (the other definitions use the default `Machine`) |
| `ArpName` pattern / `none` | 299 / 24 |
| `Cpe` | 298 |
| `WsusSupport` `Yes` / `No (...)` | 257 / 66 |
| `IconSource` | `Installer` 240, `None` 35, `External` 34. 14 definitions declare no value, which counts as `None`. |
| `InstallerSettings` | 13 |

## Steps

### Replace a shipped definition

1. Find the shipped file in the `Definitions` folder beside `ApplicationPackagerUI.exe`.
2. Copy the file to the user definitions folder.
3. Do not change the `Id`.
4. Change the copy in a text editor.
5. On the App Packager page, click the grid, then press F5.
6. Check the row and select **Check Latest** to test the version source.

### Add a definition by hand

1. Copy a shipped definition that is similar to your application. Use `putty.json` for an MSI with a version on a web page. Use `notepadplusplus.json` for an EXE with a version in a GitHub release.
2. Save the copy in the user definitions folder.
3. Change `Id` to a new value of your own. Add from web and Add from file use 16 characters.
4. Change `Vendor`, `Application`, `CMName`, `VendorUrl`, the version source, `DownloadUrl`, the install fields and `Detection`.
5. Delete the `Cpe`, `ArpName` and `SecurityAdvisoryUrl` fields, or replace them with values for your application. The copied values describe the other application.
6. On the App Packager page, click the grid, then press F5.

## Limits

- When it loads a file, the App Packager page checks only that the file parses as JSON and that `Id` is not empty. The page shows no message for a file that it skips.
- The version checks and the downloads run through `curl.exe`. A download address must be an absolute http or https address.
- A definition file is not an editable object in App Packager Desktop. Add from web and Add from file write a new file and never change one. To change a file, use a text editor.

## Troubleshooting

| Message | Cause | Fix |
|---|---|---|
| `ERROR: No scraper registered for type '<type>'` | `VersionScraper.Type` is not one of the seven types. | Use a type from the table. |
| `Refused download URL '<url>': it is not an absolute http or https URL.` | A definition holds a download address that is not an absolute http or https address. | Correct the definition. |
| `ERROR: Could not determine latest version for <application>` | The scraper found no version. | Check `Url` and `Pattern`. Use **Test Scrape** on [Add from web](add-app.md) to test the same values. |
| `GitHub API rate limit reached for <url> ...` | The scraper reads `api.github.com` without a token. | Set `APP_PACKAGER_GITHUB_TOKEN` or `GITHUB_TOKEN`, or run `gh auth login`. |
| A definition is missing from the grid. | The file is not valid JSON, has an empty `Id`, or is hidden. | Check the file. Check the [Catalog](applications.md). |

## Links

- [Create applications in Configuration Manager](https://learn.microsoft.com/intune/configmgr/apps/deploy-use/create-applications): Describes how to create an application with deployment types, detection methods and dependencies.
- [Create Windows applications in Configuration Manager](https://learn.microsoft.com/intune/configmgr/apps/get-started/creating-windows-applications): Lists the Windows application types and their options.
- [ProductCode property](https://learn.microsoft.com/windows/win32/msi/productcode): Describes the product code that identifies an installed product.
- [Windows Installer Properties for the Uninstall Registry Key](https://learn.microsoft.com/windows/win32/msi/uninstall-registry-key): Lists the Uninstall registry values that Windows Installer writes.
