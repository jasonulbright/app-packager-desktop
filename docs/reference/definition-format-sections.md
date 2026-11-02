# Definition format: sections

## What it does

This page lists the nested sections of an application definition: `InstallerSettings`, `PostInstallScript`, `Odt`, `BootstrapperLayout`, `ExtractSetup`, `LocalSource`, `LocalInstaller`, `SideFiles`, `ZipPayload` and `PayloadVariants`. The top-level fields, `VersionScraper` and `Detection` are on [Definition format](definition-format.md).

## When to use it

Use this page when a definition needs install parameters, a post-install script or a special acquisition method, and when you want to know what such a section in a shipped file does.

## Requirements

A text editor. Read [Definition format](definition-format.md) first.

## InstallerSettings

`InstallerSettings` is a list of install parameters. The **Configure** dialog and the section Install Parameters in Settings show one control for each parameter. At stage, the engine puts the values into the install arguments.

| Field | Type | Meaning | Default |
|---|---|---|---|
| `Key` | string | Unique key of the parameter, for example `Port`. | `""` |
| `Label` | string | Text of the control. | `""` |
| `Tooltip` | string or null | Help text of the control. | `null` |
| `ControlType` | string | `Toggle`, `Dropdown`, `Text`, `Number` or `LocaleList`. | `"Toggle"` |
| `DefaultValue` | string | Start value. `"true"` or `"false"` for a `Toggle`. A number as text for a `Number`. | `"false"` |
| `Options` | string list or null | Choices of a `Dropdown` or a `LocaleList`. | `null` |
| `CommandFragment` | string or null | Text that goes into the install command. A `Toggle` adds it when on. The other types replace `{value}` with the value. Example: `"PORT={value}"`. | `null` |
| `UninstallFragment` | string or null | The same for the uninstall command. | `null` |
| `Group` | string or null | Section name in the dialog. | `null` |
| `Minimum`, `Maximum` | number or null | Limits of a `Number`. | `null` |
| `ValueMap` | object or null | A table. The chosen value is looked up here before it replaces `{value}`. A value that is not in the table stands for itself. | `null` |
| `ValueMaps` | object or null | Named tables. `{setting:Key:MapName}` reads one. A value that is not in the table stands for itself. | `null` |
| `AlwaysInclude` | string or null | `LocaleList` only. A code that every non-empty choice carries, checked or not. | `null` |

The token `{setting:Key}` reads the value of the parameter `Key`. `{setting:Key:MapName}` reads it through the table `MapName` of `ValueMaps`. The tokens work in `PostInstallScript.Script` and in these fields of `Detection`: `RegistryKeyTemplate`, `FilePath`, `FileName`, `ExpectedValue`, `ScriptText`, the clauses and the `ArpEntry` texts.

Settings, Install Parameters stores your values for each application. The check box **Skip the review dialog before staging and use the saved values** controls the **Configure** dialog. `ReviewBeforeStage` overrides the check box.

Example (`mariadb-server.json`):

```json
{ "Key": "Port", "Label": "TCP port", "ControlType": "Number", "DefaultValue": "3306",
  "Group": "Server", "Minimum": 1, "Maximum": 65535, "CommandFragment": "PORT={value}" }
```

### Install parameters in shipped definitions

13 shipped definitions have install parameters. They have 103 parameters: 86 `Toggle`, 8 `Dropdown`, 6 `Text`, 2 `Number` and 1 `LocaleList`.

| File | Application | Parameters (label, control, default, group) |
|---|---|---|
| `adobereader.json` | Adobe Acrobat (64-bit) | Languages, `LocaleList`, empty, Language. 26 locale choices. `AlwaysInclude` is `en_US`. |
| `dbeaver.json` | DBeaver Community (x64) | Install scope, `Dropdown` (`System`, `User`), `System`, Install. Disable AI features, `Toggle`, `false`, Install. |
| `mariadb-server.json` | MariaDB Server (x64) | Service name, `Text`, `MariaDB`, Server. TCP port, `Number`, `3306`, Server. |
| `sqlserver2022express.json` | SQL Server 2022 Express (x64) | Instance name, `Text`, `SQLEXPRESS`, Instance. Sysadmin accounts, `Text`, `BUILTIN\Administrators`, Instance. |
| `ssms.json` | SQL Server Management Studio 22 (x64) | UI mode, `Dropdown` (`Quiet`, `Passive`), `Quiet`, Install behavior. Install path, `Text`, empty, Install behavior. Download all packages before installing, `Toggle`, `true`, Install behavior. Do not update the Visual Studio Installer, `Toggle`, `false`, Install behavior. Close SSMS if it is running, `Toggle`, `false`, Install behavior. Include recommended components, `Toggle`, `false`, Components. Include optional components, `Toggle`, `false`, Components. Remove out-of-support components, `Toggle`, `true`, Components. |
| `teamviewerhost.json` | TeamViewer Host (x64) | Assign the device after install, `Toggle`, `false`, Assignment. Assignment ID, `Text`, empty, Assignment. Assignment options, `Text`, `--grant-easy-access`, Assignment. |
| `windowsadmincenter.json` | Windows Admin Center (v2) | HTTPS port, `Number`, `443`, no group. |
| `m365apps-x64.json`, `m365apps-x86.json`, `m365project-x64.json`, `m365project-x86.json`, `m365visio-x64.json`, `m365visio-x86.json` | Microsoft 365 Apps, Project and Visio | The same 14 parameters in each file. Deploy mode, `Dropdown` (`Managed`, `Online`), `Managed`, Deployment. Then 13 `Toggle` parameters in the group Excluded apps. |

The 13 toggles of the group Excluded apps are: Access, Excel, OneNote, Outlook (classic), Outlook for Windows (new), PowerPoint, Publisher and Word (default `false`), and OneDrive for Business (Groove), Skype for Business (Lync), OneDrive (modern), Teams and Microsoft Search in Bing (default `true`).

## PostInstallScript

PowerShell that App Packager Desktop appends to `install.ps1`. It runs only after the installer returns 0. The script body must give the same result when it runs again, because it runs again on a reinstall.

| Field | Type | Meaning | Default |
|---|---|---|---|
| `WhenSetting` | string or null | The `Key` of an install parameter that gates the script. `null` runs the script every time. | `null` |
| `WhenValue` | string | The value of `WhenSetting` that enables the script. | `"true"` |
| `Script` | string | The script body. `{setting:Key}` and `{setting:Key:MapName}` are replaced at stage. | `""` |

Shipped in 6 files. Example: `teamviewerhost.json` runs `TeamViewer.exe assignment` when the parameter `AssignDevice` is `true`.

## Odt

For Microsoft 365 definitions with `InstallerType` `ODT`. It sets the Office Deployment Tool configuration. Shipped in 6 files: Apps, Project and Visio in x64 and x86.

| Field | Type | Meaning | Default |
|---|---|---|---|
| `ProductIds` | string list | Product IDs of the `Add` element, for example `O365ProPlusRetail`. | `[]` |
| `UninstallProductId` | string or null | Product ID of the `Remove` element (first product only). | `null` |
| `Channel` | string | Channel: `MonthlyEnterprise`, `Current` or `SemiAnnual`. | `"MonthlyEnterprise"` |
| `ExcludeApps` | string list | Applications to exclude. | `["Groove","Lync","OneDrive","Teams","Bing"]` |
| `DeploymentMode` | string | `Offline`: the full ODT download with an offline source. `Online`: only `install.xml`, and the client downloads at install time. | `"Offline"` |
| `UpdateChannel` | string or null | Update channel after the install. `null` uses `Channel`. | `null` |
| `EnableUpdates` | bool | Turns on Office updates. | `true` |
| `AcceptEula` | bool | Accepts the license terms without a prompt. | `true` |
| `PinToTaskbar` | bool | Pins the icons to the taskbar. | `false` |
| `SharedComputerLicensing` | bool | Shared computer licensing for RDS and VDI. | `false` |

Example (`m365apps-x64.json`): `{ "ProductIds": ["O365ProPlusRetail"], "UninstallProductId": "O365ProPlusRetail", "Channel": "MonthlyEnterprise", "ExcludeApps": ["Groove","Lync","OneDrive","Teams","Bing"] }`.

## BootstrapperLayout

For a vendor bootstrapper that builds an offline layout of its payload (Windows ADK and the Windows PE add-on). Staging downloads the bootstrapper and runs it to make the layout. The layout becomes the package content. Shipped in `windowsadk.json` and `windowspeaddon.json`.

| Field | Type | Meaning | Default |
|---|---|---|---|
| `BootstrapperUrl` | string | Address of the bootstrapper. Empty uses `DownloadUrl`. | `""` |
| `BootstrapperFileName` | string | File name of the bootstrapper. Empty uses `InstallerFileName`. The layout holds a copy of the file at its root. The install wrapper runs this copy. | `""` |
| `LayoutSwitch` | string | Switch that asks for an offline layout. It takes the target folder. | `"/layout"` |
| `QuietSwitch` | string | Switch that hides the bootstrapper UI for the layout and the install. | `"/quiet"` |
| `VersionSourceMsi` | string | Path of the MSI in the layout. Its `ProductVersion` is the version of the kit and its `ProductCode` is the Add/Remove Programs key. A `*` or `?` makes it a pattern. Exactly one file must match. | `""` |
| `InstallPath` | string | Install root. It follows the switch `/installpath`. | `""` |
| `Features` | string list | Feature option IDs. They follow the switch `/features`. | `[]` |
| `UninstallSwitch` | string | Switch that removes the kit. It goes with `QuietSwitch`. | `"/uninstall"` |
| `MaxLayoutPathLength` | number | Longest layout root that the bundle accepts. A longer root gives exit code 2002 (`Invalid download path`). | `120` |

## ExtractSetup

For a self-extracting payload that wraps an inner setup program (the SQL Server Express media box). The wrappers extract the payload and run the inner setup. Shipped in `sqlserver2022express.json`.

| Field | Type | Meaning | Default |
|---|---|---|---|
| `ExtractArgs` | string | Arguments that make the payload extract itself. `{extractdir}` is the extraction root. | `"/Q /X:{extractdir}"` |
| `ExtractDirName` | string | Folder name at the root of the system drive. It must not contain spaces, because setup rejects a path with spaces. | `""` |
| `InnerSetupPath` | string | Path of the inner setup, relative to the extraction root. | `"setup.exe"` |

## LocalSource

For a product whose download needs a sign-in or whose catalog serves one build only. The installer comes from a folder that you set under Settings, Local Installer Sources. A valid signature is required. Shipped in the three `citrixworkspace-ltsr-*.json` files.

| Field | Type | Meaning | Default |
|---|---|---|---|
| `Mode` | string | `Required`: the payload always comes from the folder and is never downloaded. `Optional`: the folder wins when you set one. Otherwise the download runs. | `"Optional"` |
| `Filter` | string | File name wildcard that the installer must match. | `"*.exe"` |
| `Exclude` | string list | Wildcards that disqualify a file that the filter matched. | `[]` |
| `StagedNames` | list | Staged file name for each source name. The first match wins. Each item has `Match` (a wildcard) and `FileName`. `{version}` is the file version of the installer. When nothing matches, the file stages as `InstallerFileName`. | `[]` |

Example (`citrixworkspace-ltsr-x64.json`): `{ "Mode": "Required", "Filter": "CitrixWorkspace*_x64*.exe", "Exclude": [], "StagedNames": [ { "Match": "*FullInstaller*", "FileName": "CitrixWorkspaceFullInstaller-LTSR-x64-{version}.exe" } ] }`.

## LocalInstaller

A definition that [Add from file](../core/add-from-file.md) saves has this section. The definition has no download address and no version source. The installer is a file on this computer. The stage copies it and checks its SHA-256. No shipped file has this section.

| Field | Type | Meaning | Default |
|---|---|---|---|
| `Path` | string | The full path of the installer file that the analysis read. | `""` |
| `Sha256` | string | The SHA-256 of the file in hexadecimal. The stage refuses a file with another hash. | `""` |
| `Version` | string | The version that you reviewed. It names the staged folder and fills the version tokens. **Check Latest** shows it. | `""` |

A definition stages from this section only when it has no `DownloadUrl`, no `VersionScraper.Url` and no `VersionScraper.DownloadUrlQuery`, and no `LocalSource`.

## SideFiles

A list of files that you choose and that Stage copies beside the installer, for example a license key file. Stage refuses to run while a file is not chosen or does not exist. You choose the file under Settings, Packager Files. Shipped in `beyondcompare5.json`.

| Field | Type | Meaning | Default |
|---|---|---|---|
| `Key` | string | Settings key that stores the chosen path. Every definition with the same key shares the path. | `""` |
| `FileName` | string | File name of the copy in the content folder. | `""` |
| `Label` | string | Name of the file in lower case, for the settings row and for the refusal message. | `""` |

Example: `{ "Key": "BeyondCompareKeyFile", "FileName": "BC5Key.txt", "Label": "license key file" }`.

## ZipPayload

For a ZIP that is the product. The install wrapper extracts the ZIP to a folder and does not run an installer from the ZIP. Shipped in `anypointstudio.json`.

| Field | Type | Meaning | Default |
|---|---|---|---|
| `InstallDir` | string | Folder that the product occupies after extraction. The top folder of the ZIP must have the name of this folder, because extraction targets the parent. | `""` |
| `RequiredEntries` | string list | Entries that the ZIP must hold. Stage refuses a ZIP without one. | `[]` |
| `IconEntry` | string or null | EXE entry whose icon is the application icon. | `null` |
| `EntryTokens` | object | Named tokens. Each expression runs on every entry path. Group 1 of the last match becomes `{zip:Name}`. A token without a match refuses the stage. | `{}` |

## PayloadVariants

A list of extra payloads for other architectures. Staging acquires them only when the operator turns on the Architecture split for a build. The base payload stays the fallback. Shipped in `7zip.json`, with one ARM64 payload.

| Field | Type | Meaning | Default |
|---|---|---|---|
| `NameSuffix` | string | Suffix of the deployment type name. | `""` |
| `Architecture` | string | Architecture of the payload. | `""` |
| `ContentSubpath` | string | One folder name in the stage root, for example `arm64`. | `""` |
| `InstallerType` | string | Installer type of the payload. | `"EXE"` |
| `DownloadUrl` | string | Address of the payload. | `""` |
| `InstallerFileName` | string or null | File name of the payload. | `null` |
| `InstallArgs`, `UninstallArgs` | string | Arguments of the payload. | `""` |
| `UninstallCommand` | string or null | Uninstall command of the payload. | `null` |
| `Detection` | object | Detection rule of the payload. | default detection |
| `PayloadSha256` | string or null | Fixed SHA-256. When absent, `PayloadHashQuery` supplies a digest for the release. | `null` |
| `PayloadHashQuery` | string or null | Expression that captures the digest of this payload in the shared version answer. | `null` |
| `RequirePayloadHash` | bool | Refuses the payload without a verified hash. | `false` |
| `RequireValidSignature` | bool | Requires a valid Authenticode signature. | `false` |

The stage accepts these payloads under these conditions only:

- The base installer has the architecture `x64`.
- The extra payload is the ARM64 deployment type: `Architecture` and `NameSuffix` are both `ARM64`.
- `InstallerType` is `EXE`.
- `ContentSubpath` has 1 to 64 characters. It starts with a letter or a digit. It holds letters, digits, `.`, `_` and `-`.
- `DownloadUrl` resolves to an https address.
- `RequirePayloadHash` is `true`, and `PayloadSha256` or `PayloadHashQuery` is set.

## Limits

- The two pages cover every field of the definition model. A field in a file that the model does not have is ignored.
- The `ControlType` list in the code comment names `Toggle`, `Dropdown`, `Text` and `Number`. The shipped file `adobereader.json` also uses `LocaleList`.

## Troubleshooting

| Message | Cause | Fix |
|---|---|---|
| `Optional architecture payloads currently require an x64 base installer.` | The base installer is not `x64`. | Set `Architecture` to `x64`. |
| `The additional architecture payload must be the ARM64 deployment type.` | `Architecture` or `NameSuffix` of the payload is not `ARM64`. | Set both to `ARM64`. |
| `Optional ARM64 architecture payloads currently support EXE installers only.` | `InstallerType` of the payload is not `EXE`. | Set it to `EXE`. |
| `An optional architecture payload requires an HTTPS download URL and a mandatory SHA256 verification source.` | The payload has no https address, no hash source, or `RequirePayloadHash` is `false`. | Set an https `DownloadUrl`, a hash source and `RequirePayloadHash` `true`. |
| `Payload variant content subpath '<name>' must be one plain folder name.` | `ContentSubpath` has a character that is not allowed. | Use one plain folder name. |
