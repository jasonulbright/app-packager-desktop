# Definition format

## What it does

This page lists every field of an application definition: the top-level fields, the version source (`VersionScraper`) and the detection rule (`Detection`). The nested sections are on [Definition format: sections](definition-format-sections.md). How definitions work is on [Definitions](../core/definitions.md).

## When to use it

Use this page when you write or change a definition file by hand, or when you want to know what a field in a shipped file does.

## Requirements

A text editor. The file is JSON with the extension `.json`.

## Rules for the file

- The property names are not case sensitive when App Packager Desktop reads the file. The files that App Packager Desktop writes use the names on this page.
- A field that is absent has the default in the column **Default**. `null` means that no value is set. `[]` means an empty list.
- A file needs an `Id`. A file with an empty `Id` is skipped.
- The shipped files hold the field `Acquisition` with the empty value `{}` (317 files). The definition model has no such field. App Packager Desktop ignores it.
- `Schema` is 1 in all 319 shipped files. No code reads the field.
- The column **Shipped** gives the number of the 319 shipped files that set the field, and an example file. A field that no shipped file sets has no example.

## Top-level fields

### Identity

| Field | Type | Meaning | Default | Shipped |
|---|---|---|---|---|
| `Schema` | number | Version of the file format. | `1` | 319 |
| `Id` | string | Unique identifier. It is the key for stored state, for the show-or-hide list and for the override rule. The shipped and the wizard-made values have 16 characters. | `""` | 319; `"656d16fead444d4e"` in `putty.json` |
| `Vendor` | string | Vendor name. The grid and the content share folders use it. | `""` | 319; `"Simon Tatham"` |
| `Application` | string | Application name with the bitness. | `""` | 319; `"PuTTY (x64)"` |
| `CMName` | string | Name that **Check ConfigMgr** uses to find the application in ConfigMgr. | `""` | 319; `"PuTTY"` |
| `AppName` | string or null | Name of the staged application. `{version}` is the staged version. When set, the name and the publisher do not come from metadata of an extracted EXE. | `null` | 6; `"Microsoft .NET {version} - Windows Server Hosting"` in `aspnethostingbundle10.json` |
| `Publisher` | string or null | Publisher of the staged application when `AppName` is set. Without it the value of `Vendor` is used. | `null` | 6; `"Microsoft Corporation"` |
| `Architecture` | string | Architecture of the installer: `x64`, `x86` or `ARM64`. | `"x64"` | 319 (299 `x64`, 19 `x86`, 1 `ARM64`) |
| `VendorUrl` | string | Web site of the vendor. The context menu entry **Open Vendor URL** opens it. | `""` | 319 |
| `WsusSupport` | string or null | Whether WSUS can take the default build: `Yes`, or `No (Code)` with the blocking refusal codes joined by `\|`. The One Click plan shows a declared refusal before a run. Absent declares nothing. | `null` | 319; `"No (PerUserInstall\|CustomInstall)"` in `githubdesktop.json` |

### Matching and security

| Field | Type | Meaning | Default | Shipped |
|---|---|---|---|---|
| `Cpe` | string or null | CPE 2.3 name of the product, in the URI form or the formatted form. **Check CVEs** needs it. | `null` | 294; `"cpe:2.3:a:7-zip:7-zip:*:*:*:*:*:*:*:*"` |
| `ArpName` | string or null | Display-name wildcards of the Add/Remove Programs entry. PowerShell `-like` syntax, not case sensitive. Join several with ` \| `. `none` marks a product without a stable entry, or a variant that inventory cannot tell from its sibling. | `null` | 319 (24 `none`); `"PuTTY release * (64-bit)"`; `"Amazon DCV Client \| NICE DCV Client"` |
| `ArpVersion` | string or null | Optional wildcard for the Add/Remove Programs version. | `null` | 7; `"11.*"` in `corretto-jdk11-x64.json` |
| `ArpArch` | string or null | Optional inventory view: `x86` is the 32-bit view, `x64` is the 64-bit view. | `null` | 13; `"x64"` |
| `SecurityAdvisoryUrl` | string or null | The security advisory page of the vendor. Only an https address shows as a link. | `null` | 94; `"https://www.anaconda.com/security"` in `anaconda.json` |

### Installer and install behavior

| Field | Type | Meaning | Default | Shipped |
|---|---|---|---|---|
| `InstallerType` | string | `MSI`, `EXE`, `ODT`, `ZIP`, `MSIX` or `MSP`. | `"MSI"` | 319 |
| `InstallerFramework` | string or null | Installer technology. It drives the automatic derivation of detection and of metadata. When empty, the engine tries to identify the installer. Values in the shipped files: `NativeMSI`, `InnoSetup`, `NSIS`, `WixBurn`, `NativeEXE`, `Unknown`, `InstallShield`, `ODT`, `Squirrel`, `MSIX`, `ZIP`. | `null` | 319; `"NSIS"` in `notepadplusplus.json` |
| `InstallContext` | string | `Machine` (Program Files, HKLM, device collections, runs as SYSTEM) or `User` (`%LOCALAPPDATA%`, HKCU, user collections, runs as the logged-on user). For MSIX the manifest decides, and the field only guides the deployment target. | `"Machine"` | 12 (`"User"`); `apppackagersuite.json` |
| `DownloadUrl` | string | Template of the installer address. `{version}` is the raw version. `{display_version}` is the display version. Can be empty when a scraper finds the address. | `""` | 319 (75 empty) |
| `InstallerFileName` | string or null | File name for the downloaded installer. Use it when the address has no usable name. Takes `{version}` and `{display_version}`. Both give the display version. | `null` | 86; `"AcroRdrDCx64-{display_version}.exe"` in `adobereader.json` |
| `InstallArgs` | string | Install arguments. | `""` | 319; `"/qn /norestart"` |
| `UninstallArgs` | string | Uninstall arguments. `{ProductCode}` is the MSI product code. | `""` | 319; `"/x {ProductCode} /qn /norestart"` |
| `UninstallCommand` | string or null | A command for the uninstall wrapper. The wrapper calls it directly and does not search Add/Remove Programs. | `null` | 101; `bitwarden.json` |
| `ExpandInstallArgs` | bool | When `true`, the MSI install wrapper expands `%NAME%` references in `InstallArgs` on the client. | `false` | 6; `ms-openjdk17-msi.json` |
| `CanUninstall` | bool | Whether the application can be uninstalled. | `true` | 2 (`false`); `edge.json` |
| `RunningProcess` | string list | Process names of the application. The names go into the stage manifest. The WSUS check lists them as the finding `RunningProcessNotClosed`. | `[]` | 319 (269 with names); `["putty","pageant","plink"]` |
| `PostInstallKillProcesses` | string list or null | Process names without `.exe`. The EXE installer starts them on success. The install wrapper waits for the installer alone and then stops these processes. | `null` | 3; `["picpick"]` in `picpick.json` |
| `RebootExitCodes` | number list or null | Installer exit codes that mean a restart is needed. The wrappers report each as 3010. | `null` | 1; `[2]` in `dellcommandupdate.json` |
| `SuccessExitCodes` | number list or null | Installer exit codes that follow a complete install. The wrapper reports each as 0. The detection rule still decides. | `null` | 1; `[111111]` in `ccleaner.json` |
| `PostExecutionBehavior` | string or null | Post-installation behavior of the ConfigMgr deployment type: `BasedOnExitCode`, `NoAction`, `ProgramReboot` or `ForceReboot`. Absent leaves the cmdlet default. | `null` | 2; `"ForceReboot"` in `msvcruntimesx64.json` |
| `SupportsInstallModes` | string list or null | Install modes that a switch selects: `CurrentUser` and `AllUsers`. Declare both, or none. The operator then chooses System or User. | `null` | 13; `bitwarden.json` |
| `SupportsVariants` | string list or null | Variant splits that the definition can stage: `Architecture`, `Language`, `Network`. | `null` | 1; `["Architecture"]` in `7zip.json` |
| `CitrixSwitchStream` | string or null | `Current` or `LTSR`. The install arguments come from the Citrix Workspace switch settings of that stream, not from `InstallArgs`. | `null` | 4; `"LTSR"` in `citrixworkspace-ltsr-x64.json` |
| `DisplayVersionOverride` | string or null | Replaces the display version in the UI, in folder names and in detection. `{version}` is the raw version. | `null` | 4; `"{version}"` in `blender.json` |
| `ReviewBeforeStage` | bool | When `true`, **Stage** always shows the **Configure** dialog, whatever the setting **Skip the review dialog before staging and use the saved values** says. | `false` | 2; `mariadb-server.json` |
| `IconSource` | string or null | Source of the application icon: `Installer` extracts it from the staged payload. `External` copies `Definitions\Icons\<Id>.ico` or `.png`. `None` stages no icon. Absent is `None`. | `null` | 305 (240 `Installer`, 34 `External`, 31 `None`) |
| `Icon`, `IconFormat` | string or null | No code reads these fields. | `null` | 0 |

### Download checks

| Field | Type | Meaning | Default | Shipped |
|---|---|---|---|---|
| `PayloadSha256` | string or null | Pinned SHA-256 of the payload. Stage refuses a payload with another hash. Use it only for a fixed address whose content does not change. | `null` | 0 |
| `PayloadSha256Url` | string or null | Address of a vendor checksum file with lines `<hash> [*]<file name>`. `{download_url}` is the payload address. `{version}` is the scraped version. | `null` | 12; `"{download_url}.sha256sum.txt"` in `ms-openjdk17-msi.json` |
| `RequirePayloadHash` | bool | When `true`, Stage refuses a payload for which no SHA-256 was found. | `false` | 3; `7zip.json` |
| `ExpectedPayloadVersion` | string or null | The version that the payload must report: the MSI `ProductVersion`, or the `FileVersion` of an EXE. Takes `{version}`, `{version_parts:N}` and `{version_part:N}`. It guards an address without a version that can serve an older build. | `null` | 3; `"{version}"` in `rtools.json` |
| `RequireValidSignature` | bool | When `true`, a downloaded payload needs a valid Authenticode signature. A local-source installer is always checked. | `false` | 3; `tableaudesktop.json` |

### Nested sections

These fields hold an object or a list. The page [Definition format: sections](definition-format-sections.md) describes them.

| Field | Type | Default | Shipped |
|---|---|---|---|
| `InstallerSettings` | list of install parameters | `null` | 13 |
| `PostInstallScript` | object | `null` | 6 |
| `Odt` | object | `null` | 6 |
| `BootstrapperLayout` | object | `null` | 2 |
| `ExtractSetup` | object | `null` | 1 |
| `LocalSource` | object | `null` | 3 |
| `LocalInstaller` | object | `null` | 0 |
| `SideFiles` | list | `null` | 1 |
| `ZipPayload` | object | `null` | 1 |
| `PayloadVariants` | list | `null` | 1 |

## VersionScraper

`VersionScraper` is an object. It tells **Check Latest** where to read the version.

| Field | Type | Meaning | Default | Shipped |
|---|---|---|---|---|
| `Type` | string | `HttpRegex`, `JsonQuery`, `FileVersion`, `ChainedHttpRegex`, `CdnCab`, `DellCatalog` or `HeadProbe`. | `"HttpRegex"` | 319 |
| `Url` | string | Address to read. `HeadProbe` takes a template with `{version}`. `FileVersion` can take `{build}`. | `""` | 319 |
| `Pattern` | string | The expression or the property name. The meaning depends on `Type`. See [Definitions](../core/definitions.md#version-sources-scrapers). | `""` | 316; `"tag_name"` in `notepadplusplus.json` |
| `VersionTransform` | string or null | Chain of `strip_prefix:X`, `insert_dot:N`, `replace:X:Y` and `regex_replace:pattern:replacement` that you separate with `\|`. | `null` | 72; `"strip_prefix:v"` |
| `DownloadUrlQuery` | string or null | A query for the download address in the same answer. A found address replaces `DownloadUrl`. For `ChainedHttpRegex` and `DellCatalog` the value is ignored and must not be empty. By convention it is `CHAINED`. | `null` | 127; `"CHAINED"` in `agentransack.json` |
| `PayloadHashQuery` | string or null | A capture of the payload SHA-256 from the same answer. `HttpRegex`: an expression whose named group `hash`, or group 1, holds the hash. `JsonQuery`: a path to the hash. | `null` | 6; `7zip.json` |
| `NoUserAgent` | bool | When `true`, the request has no User-Agent header. | `false` | 3; `adobereader.json` |
| `SelectHighestVersion` | bool | `HttpRegex` only. Takes the highest version of all matches, not the first match. | `false` | 54; `dbvisualizer.json` |
| `ServedUrlTemplate` | string or null | `HttpRegex` only. An address with `{version}`. App Packager Desktop tries the matches from the highest version down. The first version whose address answers a one-byte range request with 200 or 206 is the version. | `null` | 1; `libreoffice.json` |
| `FetchRetries` | number | `HttpRegex` only. How many times curl repeats a page fetch that timed out or answered 5xx. 0 fetches once. | `0` | 1; `2` in `smathstudio.json` |
| `FetchMaxTimeSeconds` | number | `HttpRegex` only. Time limit of one fetch attempt, in seconds. 0 uses the default of 25 seconds. | `0` | 1; `20` in `smathstudio.json` |
| `Hops` | list of hops | The hops of `ChainedHttpRegex`. `FileVersion` can also use hops. | `null` | 7; `rtools.json` |
| `MaxVersionHops` | number | Upper bound of the `HeadProbe` walk. 0 uses the default of 12. | `0` | 2; `12` in `rvtools.json` |
| `ProbeFloor` | string or null | `FileVersion` only. The oldest build number of `{build}` in `Url`. The walk goes up from it while the host answers 200. | `null` | 1; `"222"` in `defraggler.json` |
| `RequestHeaders` | string list or null | Lines `Name: value`, sent with the version request and the download. A `User-Agent` entry replaces the default. | `null` | 6; `tableaudesktop.json` |

With a pause of 5 seconds between retries, one `HttpRegex` fetch ends after at most `attempt time x (retries + 1) + 5 x retries + 5` seconds.

### Hops

| Field | Type | Meaning | Default |
|---|---|---|---|
| `Url` | string or null | Address template. `{capture}` is the capture of the previous hop. `{capture0}` to `{captureN}` are earlier captures. `{version}` is the version captured so far. Omit it in the first hop to use `VersionScraper.Url`. A relative value resolves against the previous address. | `null` |
| `Pattern` | string | Expression for the content. The capture that moves on is the named group `capture`, or group 1. The named groups `version` and `url` hold the version and the download address. A later hop replaces an earlier value. | `""` |
| `SelectHighestVersion` | bool | Collects all matches and takes the highest capture. | `false` |

Example (`rtools.json`, two hops):

```json
"Hops": [
  { "Pattern": "href=\"(?<capture>rtools\\d\\d+)/rtools\\.html\"", "SelectHighestVersion": true },
  { "Url": "{capture}/rtools.html", "Pattern": "href=\"(?<url>files/(?<version>rtools\\d\\d+-\\d+-\\d+)\\.exe)\"" }
]
```

## Detection

`Detection` is an object. It defines the rule that tells when the application is installed. The `Type` decides which fields apply.

| Field | Type | Applies to | Meaning | Default |
|---|---|---|---|---|
| `Type` | string | All | `RegistryKeyValue`, `RegistryKey`, `File`, `Script`, `Compound` or `ArpEntry`. | `"RegistryKeyValue"` |
| `RegistryHive` | string or null | Registry | `HKLM`, or `HKCU` (also `HKEY_CURRENT_USER`, `CurrentUser`). Any other value counts as the machine hive. | `"HKLM"` |
| `RegistryKeyTemplate` | string or null | Registry | Key path below the hive. `{ProductCode}` and `{version}` are replaced at stage. | `null` |
| `ValueName` | string or null | `RegistryKeyValue` | Name of the value. | `"DisplayVersion"` |
| `Operator` | string or null | `RegistryKeyValue`, `File` | `IsEquals`, `NotEquals`, `GreaterThan`, `GreaterEquals`, `LessThan`, `LessEquals`, `BeginsWith`, `EndsWith` or `Contains`. | `"IsEquals"` |
| `FilePath` | string or null | `File` | Folder of the file. `{version}` is replaced at stage. | `null` |
| `FileName` | string or null | `File` | Name of the file. | `null` |
| `PropertyType` | string or null | `RegistryKeyValue`, `File` | `Version`, `String`, `Existence` or an integer type (`Integer`, `Int`, `Int32`, `Int64`, `DWORD`, `QWORD`). When absent, stage uses `String` for a registry value and `Version` for a file. | `null` |
| `Is64Bit` | bool | Registry, `File` | `true` reads the 64-bit view. `false` reads the 32-bit view. | `true` |
| `ExpectedValue` | string or null | `RegistryKeyValue`, `File` | The value to compare, instead of the scraped version. Takes `{version}`, `{version_parts:N}`, `{version_part:N}`, `{ProductVersion}` and `{HeaderDisplayVersion}`. | `null` |
| `ScriptLanguage` | string or null | `Script` | `PowerShell`, `VBScript` or `JavaScript`. | `null` (PowerShell) |
| `ScriptText` | string or null | `Script` | Script body. `{version}` is replaced at stage. | `null` |
| `Connector` | string or null | `Compound` | `And` or `Or`. Ignored when `GroupSizes` is set. | `null` (And) |
| `Clauses` | list of detection objects or null | `Compound` | Registry or file clauses. A `Compound` inside a `Compound` is refused. | `null` |
| `GroupSizes` | number list or null | `Compound` | Two sizes of clause runs, larger than 0, that add up to the clause count. The result is `(run1 AND ...) OR (run2 AND ...)`. | `null` |
| `ArpEntry` | object or null | `ArpEntry` | The Add/Remove Programs entry. See below. | `null` |

Shipped use: `RegistryKeyValue` 208, `File` 91, `RegistryKey` 11, `Script` 4, `Compound` 3, `ArpEntry` 2. Operators: `GreaterEquals` 221, `IsEquals` 44, `BeginsWith` 6. Property types: `Version` 221, `Existence` 28, `String` 7.

Example (`nomachine.json`, `Compound`):

```json
"Detection": {
  "Type": "Compound", "Connector": "Or",
  "Clauses": [
    { "Type": "RegistryKeyValue", "RegistryHive": "HKLM", "RegistryKeyTemplate": "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\NoMachine_is1", "ValueName": "DisplayVersion", "PropertyType": "Version", "Operator": "GreaterEquals", "Is64Bit": true },
    { "Type": "RegistryKeyValue", "RegistryHive": "HKLM", "RegistryKeyTemplate": "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\NoMachine_is1", "ValueName": "DisplayVersion", "PropertyType": "Version", "Operator": "GreaterEquals", "Is64Bit": false }
  ]
}
```

### ArpEntry

| Field | Type | Meaning | Default |
|---|---|---|---|
| `Type` | string | Always `ArpEntry`. | `"ArpEntry"` |
| `View` | string | `32`, `64` or `Both`. Required. | `""` |
| `DisplayName` | string or null | Exact display name. | `null` |
| `DisplayNamePrefix` | string or null | Start of the display name. Needed when `DisplayName` is empty. | `null` |
| `DisplayNameSuffix` | string or null | End of the display name. | `null` |
| `Publisher` | string or null | Publisher of the entry. | `null` |
| `WindowsInstaller` | bool | When `true`, the entry is a Windows Installer entry. | `false` |
| `Version` | string | Up to four numbers. `{version}` is replaced at stage. | `""` |

Example (`aspnethostingbundle10.json`): `{ "View": "32", "DisplayNamePrefix": "Microsoft .NET 10.0.", "DisplayNameSuffix": " - Windows Server Hosting", "Publisher": "Microsoft Corporation", "Version": "{version}" }`.

## Limits

- This page lists the fields that the definition model has. A field that the model does not have is ignored.
- A field value is not validated when App Packager Desktop loads the file. An invalid value shows as an error at **Check Latest** or at **Stage**.
- The 25 second and 12 hop defaults are values in the code.

## Troubleshooting

| Message | Cause | Fix |
|---|---|---|
| `The operator '<operator>' is not supported.` | `Operator` holds a value that the detection evaluator does not implement. | Use a value from the table. |
| `The clause type '<type>' is not supported.` | `Detection.Type` is not one of the types that the evaluator implements. | Use a type from the table. |
| `The entry names no DisplayName or DisplayNamePrefix, so the script cannot find the Add/Remove Programs entry.` | `ArpEntry` has no `DisplayName` and no `DisplayNamePrefix`. | Set one of the two. |
| `The entry view '<view>' is not 32, 64 or Both.` | `ArpEntry.View` holds another value. | Use `32`, `64` or `Both`. |

## Links

- [ProductCode property](https://learn.microsoft.com/windows/win32/msi/productcode): Describes the product code that identifies an installed product.
- [Windows Installer Properties for the Uninstall Registry Key](https://learn.microsoft.com/windows/win32/msi/uninstall-registry-key): Lists the Uninstall registry values that Windows Installer writes.
- [Create applications in Configuration Manager](https://learn.microsoft.com/intune/configmgr/apps/deploy-use/create-applications): Describes how to create an application with deployment types, detection methods and dependencies.
- [win32LobAppRule resource type](https://learn.microsoft.com/graph/api/resources/intune-apps-win32lobapprule): Describes the detection and requirement rule types of a Win32 app.
