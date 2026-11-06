# Settings reference

This page lists every setting of App Packager Desktop. Each row gives the label that the window shows, the property name in `settings.json`, the default, and the effect.

The Settings page is described in [Settings page](../core/settings-page.md). The first-run window is described in [Getting started](../getting-started.md).

## How settings are stored

- The file is `settings.json` in the data folder. The default data folder is `%LOCALAPPDATA%\AppPackager`. See [Data folders](data-folders.md).
- The file is indented JSON. The app replaces it atomically.
- A key that is null or absent in the file gives the default on load.
- The Settings page writes the file only when you select `Save Settings`. `Discard changes` drops the edits.
- The `Sidebar` section writes at once. It is not part of `Save Settings`.
- A save from the Settings page sets `SiteCodeSaved`.
- The command-line build reads `settings.json` one time per run. A run with command-line overrides does not write the file.
- When `settings.json` cannot be read, the app uses the defaults. It writes nothing to that file until you save the Settings page. That save first keeps the unreadable file as `settings.json.unreadable-<timestamp>`.

Secrets (the Intune client secret and the NVD API key) are stored protected for the current Windows user (DPAPI, base64). The app never logs them. An empty box on the Settings page keeps the saved value.

## Header of the Settings page

| UI label | Property | Default | Effect |
|---|---|---|---|
| `Dark` / `Light` | `Theme` | `Dark` | The theme applies at once. |
| `Save Settings` | none | none | Writes the edited values to `settings.json`. The window shows "Settings saved." |
| `Discard changes` | none | none | Drops the edits that are not saved. |

## Sidebar

| UI label | Property | Default | Effect |
|---|---|---|---|
| Check box per entry, `Up`, `Down` | `Sidebar.Order`, `Sidebar.Hidden` | null (the default sidebar) | Sets the order and the hidden entries. The change applies at once. `App Packager` is the start page and stays first. An entry moves only inside its group. |
| `Reset sidebar` | `Sidebar` | null | Returns to the default sidebar. |

The default order is, in the first group: App Packager, Add from web, Add from file, Workbench. The group TOOLS follows: Installer Analysis, Detection Tester, Deployment Helper, Pivots and Scripts, Collection and Compliance, Maintenance Windows, DP Content Manager, Site Hygiene. `Settings` is always the last (footer) entry. A saved layout that names an entry which the sidebar no longer has loads without that entry.

## Updates

| UI label | Property | Default | Effect |
|---|---|---|---|
| `Check for a new version when the app starts` | `CheckForUpdatesAtStartup` | `true` | The app calls `api.github.com` once at start and opens the update window when a newer release exists. See [Updates](../core/updates.md). |

## Organization

| UI label | Property | Default | Effect |
|---|---|---|---|
| `Company Name` | `CompanyName` | empty | The app writes it to `packager-preferences.json` in the Packagers folder. Only PS1 packagers use that file. The app reads no other use of this value. When no Packagers folder exists, the value has no effect. |

## Systems in use

| UI label | Property | Default | Effect |
|---|---|---|---|
| `ConfigMgr` | `Systems.ConfigMgr` | on | A system that is not in use keeps its publish buttons disabled on the App Packager page. |
| `Intune` | `Systems.Intune` | off | Same. |
| `WSUS` | `Systems.Wsus` | off | Same. |

A `settings.json` file without `Systems` gets these values on load: ConfigMgr on; Intune on when a tenant ID is stored; WSUS on when a server name is stored.

With ConfigMgr off, the launch skips the ConfigMgr version query, and the CVE run uses an empty site code.

## ConfigMgr Site Settings

| UI label | Property | Default | Effect |
|---|---|---|---|
| `Site Code` | `SiteCode` | `MCM` | The site for every ConfigMgr step. The publish gate checks only that the value is not empty. The default is therefore accepted as a site code until you change it. Discover accepts three letters or digits. |
| (none) | `SiteCodeSaved` | null | Internal flag. A save from the Settings page sets it. |
| `Site Server / SMS Provider Host` | `ProviderMachine` | empty | The host of the generated CMSite drive. Empty means this computer. That works only when this computer is the SMS Provider. The Pivots and Scripts AdminService client needs a host name without `https://` and without a path. |
| `Discovery SQL Server` | `DiscoverySqlServer` | empty | The SQL Server instance of the site database. Empty reads it from the SMS Provider. Windows authentication only. Discover and the Site Hygiene Live views use it. |
| `Discovery Database` | `DiscoverySqlDatabase` | empty | The database name. Empty uses `CM_<site code>`. |
| `Not offered by Discover`, `Clear list` | `DiscoveryDismissed` | empty list | Definition IDs that Discover no longer offers. `Clear list` takes effect on `Save Settings`. |
| (none; set by Discover) | `DiscoveryEnabled` | empty list | Definition IDs that Discover made visible. The first-run import of the hidden list never hides them again. |
| `File Share Root (UNC)` | `FileShareRoot` | `\\fileserver\sccm$` | The root of the content share. Content goes under `Applications\...`. |
| `Content Layout` | `ContentLayout` | `Nested` | `Nested` writes `Applications\Vendor\App\Version`. `Flat` writes `Applications\Vendor-App-Version`. |
| `Download Root (local staging)` | `DownloadRoot` | `%LOCALAPPDATA%\AppPackager\Staging` | The local staging folder. The app refuses a folder that other accounts can write. A root that is still the old default `C:\temp\ap` moves once to the per-user folder, with a notice. When the data root is refused, the default is empty. |
| (none) | `StagingRootMigrated` | null | Internal flag of that move. |
| `Packagers Folder (leave empty for auto-detect)` | `PackagersRoot` | empty | The folder of PS1 packagers. Empty uses the `Packagers` folder beside the executable. |

Used by: [Stage and publish](../core/stage-and-publish.md), [Discover unmanaged software](../core/discover-unmanaged-software.md), [Site Hygiene Live](../tools/site-hygiene-live.md).

The environment variables `SUITE_CM_SITECODE` and `SUITE_CM_PROVIDER` fill the site code (when never saved) and an empty provider in memory. They do not write the file.

## After Package

These settings apply to `Publish to ConfigMgr`. See [Stage and publish](../core/stage-and-publish.md).

| UI label | Property | Default | Effect |
|---|---|---|---|
| `Distribute content after Package` | `ContentDistribution.AutoDistribute` | off | Distributes the new application to the group below. A group that already holds the content counts as done. |
| `Distribution point group` | `ContentDistribution.DpGroupName` | empty (the box shows the hint "All DPs") | The target distribution point group. |
| `Deploy to a test collection after distribution` | `ContentDistribution.DeployToTestCollection` | off | Creates an Available deployment that is effective at once. It runs only after a distribution. An existing deployment counts as done. |
| `Test collection` | `ContentDistribution.TestCollectionName` | empty | The literal collection name. Wildcard matching is off. |
| `Use a user collection` | `ContentDistribution.TestCollectionIsUser` | off | Uses a user collection instead of a device collection. It does not change the install context. |
| `Create the collection if it does not exist` | `ContentDistribution.CreateTestCollectionIfMissing` | off | Creates an empty collection limited to All Users (`SMS00002`) or All Systems (`SMS00001`). |

## One Click Settings

See [One Click](../core/one-click.md).

| UI label | Property | Default | Effect |
|---|---|---|---|
| `Action on update` | `OneClick.Action` | `Report` | Choices: `Report only` (`Report`), `Stage` (`Stage`), `Stage and Publish` (`StageAndPackage`). |
| `Force on launch (ignore cadence)` | `OneClick.ForceOnLaunch` | off | The plan opens with Force set. |
| `Default cadence (days)` | `OneClick.DefaultCadenceDays` | 7 | The number of days that a version check stays fresh. A Report run skips an application checked within its cadence unless Force is set. |
| `Default destinations` (`ConfigMgr`, `WSUS`, `Intune`) | `OneClick.DefaultDestinations` | null | The destinations of a tracked application that has no choice of its own. When null, the planner reads the saved deployment target (see the next row). |
| (none) | `Intune.DeploymentTarget` | `MECM` | Not an editable setting. The app derives the nearest single target from the default destinations on save. Grid Stage for PS1 packagers reads it. |
| `Existing ConfigMgr app` | `OneClick.OnExisting` | `Skip` | Choices: `Skip the application` (`Skip`), `Overwrite it` (`Overwrite`). A run does not ask. |
| `Track` (grid) | `OneClick.TrackedApplications` | empty list | The tracked definition or packager IDs. `Track all`, `Track none` and `Refresh list` change the grid. |
| `Cadence (days)` (grid) | `OneClick.CadenceOverrides` | empty | The cadence per application. |
| `ConfigMgr` / `WSUS` / `Intune` (grid) | `OneClick.Destinations` | empty | The destinations per application. Rows without an entry follow the default. A row with no box checked is skipped. |

## Detected Tools

This section shows the state of three tools. It stores no setting.

| UI label | What it shows |
|---|---|
| `Configuration Manager console` | Found, or the text "Not found. Install the Configuration Manager console; ConfigMgr packaging runs its PowerShell module." |
| `7-Zip command line` | Found, or the text "Not found. Install 7-Zip; installer analysis and archive extraction need 7z.exe." |
| `Win32 Content Prep Tool` and `Download` | Found, or the text "Not downloaded yet. Fetched on first Intune build, or download it now." `Download` fetches the tool. See [Network calls](network.md). |

## Packager Icon Pack and GitHub API

These sections store no setting.

| UI label | Effect |
|---|---|
| `Install from file...` | Installs an icon pack from a local file. |
| `Download icon pack` | Downloads the icon pack release and checks its SHA-256 value. The app stores the icons in the user definitions folder. Bundled icons stay read-only. |
| `Refresh` (GitHub API) | Shows the GitHub API rate limit. |

## Intune

See [Stage and publish](../core/stage-and-publish.md).

| UI label | Property | Default | Effect |
|---|---|---|---|
| `Tenant ID` | `Intune.TenantId` | empty | The Entra tenant. |
| `Client ID` | `Intune.ClientId` | empty | The app registration (client) ID. |
| `Client Secret` | `Intune.ClientSecretProtected` | empty | The app registration secret. Stored protected for the current Windows user. An empty box keeps the saved secret. |

The app registration needs the application permission `DeviceManagementApps.ReadWrite.All`, with admin consent.

## CVE lookup

See [CVE lookup](../core/cve-lookup.md).

| UI label | Property | Default | Effect |
|---|---|---|---|
| `NVD API key` | `Vulnerability.NvdApiKeyProtected` | empty | The NVD key. Stored protected for the current Windows user. Without a saved key, the app uses the `NVD_API_KEY` environment variable. |
| `Remove the saved key on Save` | none | off | Deletes the saved key on `Save Settings`. |
| `Look up CVEs after Check ConfigMgr` | `Vulnerability.LookupAfterCheckCm` | off | Runs the CVE lookup for the same rows after `Check ConfigMgr`. |
| (none) | `Vulnerability.CacheTtlMinutes` | 360 | The number of minutes that a cached lookup answers. A Shodan result is used for 60 minutes. |

## WSUS Publishing

See [WSUS publishing](../core/wsus-publishing.md).

| UI label | Property | Default | Effect |
|---|---|---|---|
| `WSUS server` | `Wsus.ServerName` | empty | Empty means WSUS is not configured. The value must be a host name. |
| `Port` | `Wsus.PortNumber` | 8530 | A value outside 1 to 65535 becomes 8531 with SSL and 8530 without. |
| `Use SSL` | `Wsus.UseSsl` | off | Uses HTTPS to the WSUS administration API (port 8531). |
| `Test connection`, `Cancel` | none | none | Tests the connection to the server. |
| `Create self-signed...`, `Import PFX...`, `Export certificate...` | none | none | Manage the signing certificate for locally published updates. |
| `Classification` | `Wsus.Classification` | `Updates` | One of: Updates, SecurityUpdates, CriticalUpdates, FeaturePacks, ServicePacks, Tools, UpdateRollups. |
| `Approve for group`, `Load groups` | `Wsus.ApprovalGroup` | empty | Empty publishes without an approval. |
| `Decline earlier versions of the same application after publishing` | `Wsus.DeclineSuperseded` | off | Declines the lower-version updates that this product published. Approving an update again reverses the decline. |

## Script Signing

See [Script signing](../core/script-signing.md).

| UI label | Property | Default | Effect |
|---|---|---|---|
| `Sign detection scripts` | `ScriptSigning.SignDetection` | off | Signs the generated detection scripts. |
| `Sign requirement scripts and script global conditions` | `ScriptSigning.SignRequirements` | off | Signs the requirement scripts. |
| `Sign install and uninstall PowerShell scripts` | `ScriptSigning.SignDeployment` | off | Signs the install and uninstall scripts. |
| `Require valid detection signatures` | `ScriptSigning.RequireDetection` | off | The build or publish stops when a script of this category has no valid signature. There is no unsigned fallback. |
| `Require valid requirement signatures` | `ScriptSigning.RequireRequirements` | off | Same, for requirement scripts. |
| `Require valid deployment script signatures` | `ScriptSigning.RequireDeployment` | off | Same, for install and uninstall scripts. |
| `Certificate store` | `ScriptSigning.StoreLocation` | `CurrentUser` | `CurrentUser` or `LocalMachine`. The app reads the certificate from the `My` store of that location. |
| `Certificate`, `Refresh` | `ScriptSigning.CertificateThumbprint` | empty | The 40-hex-character thumbprint. |
| `Timestamp server` | `ScriptSigning.TimestampServer` | empty | An `http://` or `https://` URL. |
| `Timestamp required` | `ScriptSigning.TimestampRequired` | off | A timestamp failure fails the build. |
| `Test signing configuration` | none | none | Tests the signing configuration. |
| (none) | `ScriptSigning.HashAlgorithm` | `SHA256` | The hash of the signature. |

## Deployment Type Defaults

These values go to the ConfigMgr deployment type at `Publish to ConfigMgr`. See [Stage and publish](../core/stage-and-publish.md).

| UI label | Property | Default | Effect |
|---|---|---|---|
| `Estimated Runtime (mins)` | `EstimatedRuntimeMins` | 15 | The estimated install time. |
| `Maximum Runtime (mins)` | `MaximumRuntimeMins` | 30 | The maximum run time. |
| `Fallback DPs` | `ContentFallback` | `Allow` | `Allow - use the site default boundary group` or `Deny - current and neighbor groups only`. |
| `Neighbor/default DP` | `SlowNetworkDeploymentMode` | `Download` | `Download content and install` (`Download`) or `Do not download content` (`DoNothing`). |
| `Include version in application name` | `IncludeVersionInTitle` | off | Creates one ConfigMgr application for each release. A per-application Workbench choice overrides it. The app does not rename existing applications. |
| `Remove version from application name` | `RemoveVersionFromTitle` | off | Strips the release version from a vendor product name. It applies only while `Include version in application name` is off. |

The environment variables `APP_PACKAGER_DT_CONTENT_FALLBACK` and `APP_PACKAGER_DT_SLOW_NETWORK_MODE` pass the two content options to PS1 packagers.

## M365 Apps - Channel

| UI label | Property | Default | Effect |
|---|---|---|---|
| Channel choice | `M365Channel` | `MonthlyEnterprise` | Choices: `Monthly Enterprise Channel` (`MonthlyEnterprise`), `Current Channel` (`Current`), `Semi-Annual Enterprise Channel (kiosk)` (`SemiAnnual`). The update channel for the M365 Apps, Project and Visio packagers. |

## Install Parameters

| UI label | Property | Default | Effect |
|---|---|---|---|
| `Skip the review dialog before staging and use the saved values` | `SkipConfigReview` | off | Stage does not show the review dialog. Applications that have `ReviewBeforeStage` still show it. |
| Parameters of each application | `InstallerSettingValues` | empty | Maps definition ID to parameter key to value. The values pre-fill the review dialog. |

The section lists, at run time, the applications whose definition carries `InstallerSettings`. The shipped definitions with such settings are:

| Definition | Parameters |
|---|---|
| Adobe Acrobat 64-bit (`adobereader.json`) | `Languages` (language list, 26 locale options; `en_US` is always included) |
| DBeaver Community x64 (`dbeaver.json`) | `Install scope` (default System); `Disable AI features` (default off) |
| MariaDB Server x64 (`mariadb-server.json`) | `Service name` (default MariaDB); `TCP port` (default 3306) |
| SQL Server 2022 Express x64 (`sqlserver2022express.json`) | `Instance name` (default SQLEXPRESS); `Sysadmin accounts` (default `BUILTIN\Administrators`) |
| SQL Server Management Studio 22 x64 (`ssms.json`) | `UI mode` (default Quiet); `Install path` (default empty); `Download all packages before installing` (default on); `Do not update the Visual Studio Installer` (default off); `Include recommended components` (default off); `Include optional components` (default off); `Remove out-of-support components` (default on); `Close SSMS if it is running` (default off) |
| TeamViewer Host x64 (`teamviewerhost.json`) | `Assign the device after install` (default off); `Assignment ID` (default empty); `Assignment options` (default `--grant-easy-access`) |
| Windows Admin Center v2 (`windowsadmincenter.json`) | `HTTPS port` (default 443) |
| M365 Apps for Enterprise, M365 Project, M365 Visio (x64 and x86; six definitions) | `DeployMode` (default Managed) and the exclusion switches below |

The six Microsoft 365 definitions share these exclusion switches (group "Excluded apps"):

- Default off: Access, Excel, OneNote, Outlook (classic), Outlook for Windows (new), PowerPoint, Publisher, Word.
- Default on: OneDrive for Business (Groove), Skype for Business (Lync), OneDrive (modern), Teams, Microsoft Search in Bing.

## Local Installer Sources and Packager Files

| UI label | Property | Default | Effect |
|---|---|---|---|
| `Local Installer Sources` (folder per application, `Browse...`) | `LocalSourceFolders` | empty | Maps definition ID to folder. Stage takes the highest file version in the folder. The installer must carry a valid signature. Use it for an installer whose download needs a sign-in. |
| `Packager Files` (file per key, `Browse...`) | `SideFiles` | empty | Maps side-file key to path. Stage copies the file beside the installer. The app never reads it. |

## Citrix Workspace App - Install Parameters

Property: `CitrixSwitches` (default null). The legacy property `Cwa` is read so that an older file keeps its choices. The app drops `Cwa` on the next save. The app also writes the choices to `citrix-workspace-switches.json` for PS1 packagers.

The definitions Current Release and LTSR use these choices. A shaded box or an empty choice is not set. The app then passes no switch, and the installer default applies. `Preview` shows the arguments for Current Release and LTSR.

| Group | Controls |
|---|---|
| Store | `Store Name`, `Store URL (passed as typed)` |
| Installation | `Clean install`, `Include SSON`, `Enable SSON`, `startAppProtection`, `Session pre-launch`, `Self-service mode` |
| Plugins | `Microsoft Teams`, `Zoom`, `Webex`, `uberAgent`, `Skip uberAgent upgrade`, `EPA client`, `Session recording` |
| Components | `Customize (ADDLOCAL)`, `ReceiverInside`, `ICA_Client`, `AM`, `SelfService`, `DesktopViewer`, `WebHelper`, `BCR_Client`, `USB`, `SSON`. The page shows the check boxes of `ICA_Client` and `BCR_Client` as `ICAClient` and `BCRClient`, because WPF reads the underscore as an access key. The component names are `ICA_Client` and `BCR_Client`. |
| Updates, telemetry and store policy | `AutoUpdateCheck` (`(not set)`, `auto`, `manual`, `disabled`); `EnableCEIP`; `EnableTracing`; `ALLOWADDSTORE` (`(not set)`, `S`, `A`, `N`); `ALLOWSAVEPWD` (`(not set)`, `S`, `A`, `N`) |

Each check box has three states: on, off and not set.

## About

| UI label | Effect |
|---|---|
| `Releases` | Opens `https://github.com/jasonulbright/app-packager-desktop/releases` in the browser. |
| Version text | Shows `App Packager Desktop <version>` and "MIT License. Settings are stored under %LOCALAPPDATA%\AppPackager." The text does not follow an `APP_PACKAGER_DATA_ROOT` override. |

The app does not check for a new version. Open `Releases` to see new versions.

## Setup window (first run)

The Setup window opens one time after the main window shows, when `FirstRunCompleted` is not true. A settings file that exists without this flag counts as completed.

| UI label | Property | Effect |
|---|---|---|
| `Systems in use` (`ConfigMgr`, `Intune`, `WSUS`) | `Systems.*` | As in the Systems in use section above. |
| ConfigMgr: `Site Code`, `Site Server / SMS Provider`, `File Share Root`, `Download Root` | `SiteCode`, `ProviderMachine`, `FileShareRoot`, `DownloadRoot` | As in ConfigMgr Site Settings above. |
| Intune: `Tenant ID`, `Client ID`, `Client Secret` | `Intune.*` | As in Intune above. |
| WSUS: `WSUS server`, `Port`, `Use SSL` | `Wsus.*` | As in WSUS Publishing above. |
| `Don't show this again` | `FirstRunCompleted` | Sets the flag to true. |
| `Back`, `Next`, `Skip`, `Save` | none | Move through the window, or save. |

When you select no system, the window says "No system is selected. Save stores that, and the publish buttons stay disabled until a system is selected in Settings."

## Settings without a control

| Property | Default | Effect |
|---|---|---|
| `HiddenApplications` | empty | The IDs hidden in the App Packager grid. The `Save` button of the [Catalog](../core/applications.md) window sets it. |
| `AppStates` | empty | Per application: last checked, latest version, current version, last staged, published per destination, not supported per destination. |
| `WindowLeft`, `WindowTop`, `WindowWidth`, `WindowHeight`, `WindowMaximized`, `PaneOpen` | -1, -1, 1200, 800, off, on | The app saves the window state when the main window closes. The minimum window size is 900 x 600. The supported minimum is 1024 x 720. |
| `FirstRunCompleted` | null | See the Setup window. |
| `DeploymentConditions` | empty | A legacy map per definition. No screen writes it. It applies only when no Workbench profile applies. At start, a migration moves it into Workbench profiles. A hand edit of `settings.json` is the only way to change it. |
| `CommandOverrides` | empty | A legacy map of install and uninstall commands per definition. It has the same rules as `DeploymentConditions`. |

## Choices saved by each page

A page saves its tab and filter choices under its own key in `settings.json`. You set them by using the page. They hold no secret. A null key gives the page defaults.

| Key | Properties and defaults |
|---|---|
| `DetectionTester` | `ClauseType` RegistryKeyValue; `Hive` HKLM; `View` 64-bit; `ShowSystemComponents` off |
| `MaintenanceWindows` | `SelectedTab` 0 (Windows, Coverage, Templates); `TextFilter` empty; `TypeFilter` empty (Any, SoftwareUpdates, TaskSequences); `EnabledFilter` All; `OnlyCollectionsWithWindows` on; `FolderFilter` empty; `CoverageWithoutWindowOnly` off; `DetailTab` 0; `EditorWidth` 0; `EditorHeight` 0 |
| `CollectionCompliance` | `SelectedTab` 0 (Collections, WQL Editor, Templates, Site Queries); `DetailTab` 0; `DetailsHidden` off; `TextFilter`; `TypeFilter` (BuiltIn, Custom); `OnlyEmpty`; `OnlyWithDirectMembers`; `OnlyWithQueryRules`; `FolderFilter`; `TemplateCategory`; `EditorWidth`; `EditorHeight`; `Section` 0 (Collections, Compliance); `Compliance` (`SelectedTab` 0 for Catalog, Live CI/CB, Editor, Auditing; catalog and live filters; `AuditingTab` 0 for Drift, Compliance; `DriftState` Current, Behind, Untagged, Unknown; `OnlyStale`) |
| `InstallerAnalysis` | `SelectedTab` 0 (Overview, MSI Properties, Payload, Inner Installers, File Browser, Registry Settings, Strings); filters per tab; `StringsCategory`; `RecentFiles` (the list of recent paths; the page keeps the last 10) |
| `DeploymentHelper` | `SelectedTab` 0; `ObjectType` 0 (Applications, Packages, Task Sequences, Software Update Groups); `TemplateId` empty; `AuditLogPath` empty (the default is under the data folder; a UNC path is allowed); `RingPlanName` empty; `RingsFolder` empty; `RunsFolder` empty |
| `DpContent` | `SelectedTab` 0 (DPs, Content, Status Issues, History); text and status filters; `Sorts` |
| `PivotsScripts` | `SelectedTab` 0 (Pivot, Script, Site scripts, History, Options); `ResultTab` 0 (Results, Devices, Raw, Pipeline); `LibraryFolder` null (the built-in samples only); `WaitMinutes` 0 (means the default of 5 minutes; the list offers 1, 2, 5, 10, 15, 30 and 60); `TargetKind` (Collection, Device, Pattern, List) |
| `SiteHygiene` | `SelectedTab`; `RelationsTab` 0 (Inventory, Tree); finding and edge filters; `ShowSuppressed` off; `Areas` null or empty (all 18 areas); `PauseMilliseconds` 0 (the list offers 0, 50, 100, 250, 500, 1000); `ProbeContentSources` off; `Sorts`; `LiveView`; `LiveStatus`; `LiveText`; `TrendDays` null (30); `TrendMetric`; `ImportOfferDismissed` off |
| `SiteHygieneLive` | All null until set. `TrustSqlCertificate` off; `RefreshMinutes` 15 (range 5 to 1440); `InactiveDays` 14 (range 1 to 3650); `AlertsEnabled` on; `AlertSiteCritical` on; `AlertDpCritical` on; `AlertContentFailed` on; `AlertCompliance` on; `CompliancePctFloor` 80 |

## Environment variables

| Variable | Effect |
|---|---|
| `APP_PACKAGER_DATA_ROOT` | Moves the data folders to `<value>\Users\<Windows SID>`. See [Data folders](data-folders.md). |
| `APP_PACKAGER_WORKBENCH_ROOT` | Sets the Workbench data root. |
| `NVD_API_KEY` | The NVD key, used when no key is saved. |
| `APP_PACKAGER_GITHUB_TOKEN`, `GITHUB_TOKEN` | A GitHub token for version checks that read `api.github.com`. The app tries them in this order, then `gh auth token`. |
| `SMS_ADMIN_UI_PATH` | Set by the ConfigMgr console. The app uses it to find the console. |
| `SUITE_CM_SITECODE`, `SUITE_CM_PROVIDER` | Fill the site code (when never saved) and an empty provider, in memory only. |
| `APP_PACKAGER_DT_CONTENT_FALLBACK`, `APP_PACKAGER_DT_SLOW_NETWORK_MODE` | Pass the two content options to PS1 packagers. |

## Command-line options and `monitor-config.json`

The command-line build uses the same `settings.json`. The options and the `monitor-config.json` file are in [Command-line build](../core/command-line-build.md).
