# Error messages

This page lists the messages that App Packager Desktop shows, grouped by area. Each row gives the message, where it appears, the cause, and the fix.

- `<x>` marks a value that the app inserts.
- Where the table says "The code names no fix", the app gives no fix. Read the message text for the cause.
- For the most common messages, see also [Troubleshooting](../troubleshooting.md).

Areas:

1. [Command line](#command-line)
2. [Publish gates and Packages page](#publish-gates-and-packages-page)
3. [Settings and data folders](#settings-and-data-folders)
4. [ConfigMgr connection](#configmgr-connection)
5. [Discover](#discover)
6. [WSUS](#wsus)
7. [Deployment Helper](#deployment-helper)
8. [DP Content Manager](#dp-content-manager)
9. [Pivots and Scripts](#pivots-and-scripts)
10. [Site Hygiene](#site-hygiene)
11. [Collections, Maintenance Windows and Compliance](#collections-maintenance-windows-and-compliance)
12. [Installer Analysis](#installer-analysis)
13. [Add App and Workbench](#add-app-and-workbench)
14. [Network, CVE, download and signing](#network-cve-download-and-signing)
15. [Other message families](#other-message-families)

## Command line

These messages come from `AppPackagerBuild.exe`. See [Command-line build](../core/command-line-build.md). Exit code 1 follows a refusal.

| Message | Cause | Fix |
|---|---|---|
| Unexpected argument '<arg>'. Run AppPackagerBuild --help for usage. | An argument does not start with `--`, or it is only `--`. | Use `--name value`. Run `--help`. |
| Option '--x' takes no value, or =true or =false. | A switch got a value other than true or false. | Give no value, `=true` or `=false`. |
| Option '--x' is given more than once. | The same option appears twice. | Give each option one time. |
| Unknown option '--x'. Run AppPackagerBuild --help for usage. | The name is in no option list. | Run `--help`. |
| Option '--x' needs a value. | A valued option is last, or another option follows it. | Add a value. |
| Option '--x' was given an empty value; pass a value or leave the option out. | A path, root, server or site option is empty. An unset variable in a scheduled task can cause this. | Pass a value, or leave the option out. |
| --application is required. Run AppPackagerBuild --help for usage. | A build has no `--application`. | Add `--application`. |
| Unknown target 'x'; expected ContentOnly, MECM, MECMAndIntune, IntuneOnly, MECMAndWSUS, WSUSOnly. | The `--target` value is not in the list. | Use a listed target. |
| Unknown --on-existing value 'x'; expected Skip, Overwrite or Fail. | The value is not in the list. | Use a listed value. |
| Option '--wsus-port' takes a port number from 1 to 65535. | The value is not a number from 1 to 65535. | Give a valid port. |
| Specify --stage, --package, or both. | The command has neither phase switch. | Add a switch. |
| A MECMAndWSUS package publishes the build staged in the same run; pass --stage together with --package. | `--package` without `--stage` for the target MECMAndWSUS. | Pass both. |
| Option '--x' takes a whole number of minutes. | `--estimated-minutes` or `--maximum-minutes` is not an integer. | Give a whole number. |
| Option '--x' does not apply to --cve-report. | A build-only option is used with `--cve-report`. | Remove the option. |
| Option '--x' applies to --cve-report only. | A CVE-only option is used in a build. | Remove the option. |
| Unknown report format 'x'; expected Html, Csv, Json, Xlsx, Markdown. | `--report-formats` has an unknown name. | Use the listed names. |
| Option '--webhook-url' takes an https URL. | The URL is not https. | Use https. |
| The monitor configuration is not valid JSON: <path>: <detail> | `monitor-config.json` cannot be parsed. | The code names no fix. Correct the file. |
| DownloadRoot is not configured: pass --download-root or set it in Settings. | No staging root exists. | Pass `--download-root`, or set it in Settings. |
| --version does not apply to staging a definition: a definition stages its latest version. Use --version with --package alone to publish the sealed build of that version. | `--version` with `--stage` on a definition. | Do as the message says. |
| <app> stages from a local installer folder, and no folder exists for it: pass --local-source-folder <folder>, or set the folder in Settings. | The definition has a local source and no folder is set. | Do as the message says. |
| No definition or packager script for application '<id>' under <searched>. | The application ID is unknown. | The code names no fix. Check the ID. |
| No sealed build of version '<v>' exists for this profile. Stage it first; Package consumes an exact build id, not the newest manifest on disk. | `--package --version` with no sealed build. | Stage the version first. |
| No WSUS server: pass --wsus-server or set the server in Settings, WSUS Publishing. | A WSUS target has no server. | Do as the message says. |
| WSUS publishing is unavailable: <reason> | The WSUS administration API was not found. | The code names no fix. The reason text comes from the probe. |
| --estimated-minutes and --maximum-minutes do not apply to build <id>: its sealed manifest records ... Stage with them, or leave them out. | Run overrides were given for a package of a sealed build. | Do as the message says. |
| Package refused: <reason> | A package run was refused. | The code names no fix. Read the reason. |

## Publish gates and Packages page

See [Packages](../core/packages.md) and [Stage and publish](../core/stage-and-publish.md).

| Message | Where it appears | Cause | Fix |
|---|---|---|---|
| Publish requires Intune credentials. Open Settings, Intune, to configure Tenant ID, Client ID, and Client Secret. | Packages page, at the start of a run | An Intune publish runs without credentials. | Do as the message says. |
| Publish requires a WSUS server. Open Settings, WSUS Publishing, to configure it. | Packages page, at the start of a run | A WSUS publish runs without a server. | Do as the message says. |
| Publish to ConfigMgr requires the ConfigMgr Console. Not detected on this workstation. | Packages page, at the start of a run | The variable `SMS_ADMIN_UI_PATH` or the module file is missing. | Install the console. See Detected Tools in Settings. |
| SiteCode is required. Open Settings to configure. | Packages page, at the start of a run | The site code is empty. | Do as the message says. |
| File Share Root is required. Open Settings to configure. | Packages page, at the start of a run | The share root is empty. | Do as the message says. |
| Needs the ConfigMgr console on this computer. | Tooltip of `Check ConfigMgr` and `Publish to ConfigMgr` | The console was not found. | Install the console. |
| <System> is not in use. Select it under Systems in use in Settings. | Tooltip or message of a disabled button | The system is off in Systems in use. | Select the system in Settings. |
| Intune credentials are required. Open Settings and set Tenant ID, Client ID, and Client Secret. | Packages page | A target publishes to Intune without credentials. | Do as the message says. |
| No packagers selected. | Packages page log | A button was pressed with no checked row. | Check at least one row. |
| SiteCode is required. Open Settings. | Packages page log, `Check ConfigMgr` | The site code is empty. | Do as the message says. |
| Could not connect to site <code>. Check Settings and ensure the CM console is available. | Packages page log | The connection test at launch failed. A log line `ConfigMgr: Connection failed - <reason>` gives the reason. | Do as the message says. |
| Drop ignored: N files dropped, one installer at a time. | Packages page log | You dropped more than one file. | Drop one file. |
| Drop ignored: <file> is not an .msi or .exe. | Packages page log | You dropped a file with another extension. | Drop an `.msi` or `.exe` file. |
| Stage: DownloadRoot is not configured. Open Settings. | Packages page log | A dropped installer was staged with no root. | Do as the message says. |
| Not found in ConfigMgr | Status column | The site has no application with that title. | None. This is a status, not a failure. |
| Application name is required. | Drop review panel | The field is empty. | Fill the field. |
| Version is required. | Drop review panel | The field is empty. | Fill the field. |
| Install args are required for a non-MSI installer. | Drop review panel | A non-MSI drop has no install arguments. | Fill the field. |
| Package refused: <reason> | Packages page, bring-your-own run | The run was refused. | The code names no fix. Read the reason. |
| Skipped <app>: no installer source folder chosen. | Packages page log | You cancelled the folder prompt. The prompt says: "Choose the folder that holds the <app> installer. Its download requires a sign-in, so it stages from a local copy." | Choose a folder. |

## Settings and data folders

| Message | Where it appears | Cause | Fix |
|---|---|---|---|
| Settings file could not be read: <path> (<reason>). Defaults are in use and nothing is written to that file until Settings is saved; that save first keeps the unreadable file as settings.json.unreadable-<timestamp>. | At start | `settings.json` is not valid or not readable. | Save the Settings page. The old file is kept. |
| The staging folder changed from C:\temp\ap to <path>: other accounts can write under the old folder. Content staged earlier stays in the old folder; stage again before the next publish. | At start | The app moved the old default staging root. | Stage again. |
| These settings carry run overrides for this process and are not written. | Command line | A save was tried in a run that has overrides. | None. This is by design. |
| Staging folder '<f>' can be changed by other accounts (<list>). Use a folder that only your account and administrators can write, such as '<default>'. | Stage | The access list of the staging root is too wide. | Do as the message says. |
| APD user data cannot be stored under Program Files. | At start | The data root or the Workbench root is under Program Files. | Choose another root. |
| The user data folder cannot be a junction or symbolic link: <path> | At start | The data folder is a reparse point. | Use a normal folder. |
| The sidebar layout could not be saved. The change lasts until the application closes. | Main window | The app could not write the sidebar choice. | The code names no fix. |

## ConfigMgr connection

| Message | Cause | Fix |
|---|---|---|
| The ConfigurationManager PowerShell module was not found. Install the Configuration Manager console on this computer. | The module file is missing. | Do as the message says. |
| The SMS Provider <p> denied access. The account needs a ConfigMgr security role for site <code>. | The provider denied access. A variant of the text exists for this computer. | Do as the message says. |
| The CMSite drive for site <code> on <provider> could not connect. Check the site code and the SMS Provider in Settings, ConfigMgr Site Settings. | The drive did not connect. | Do as the message says. |
| The ConfigMgr command <name> did not finish within N seconds. The session was restarted. | The command time limit passed. The default limit is 5 minutes. | The code names no fix. |
| The session did not connect to site <code> on <provider> within N seconds. | The connect limit passed. The default limit is 2 minutes. | The code names no fix. |

## Discover

See [Discover unmanaged software](../core/discover-unmanaged-software.md).

| Message | Cause | Fix |
|---|---|---|
| A site code or SQL overrides are required. Set the site code, or the SQL server and database, in Settings, ConfigMgr Site Settings. | No site code and no SQL override. | Do as the message says. |
| The site code <x> is not three letters or digits. Correct it in Settings, ConfigMgr Site Settings. | The site code is not valid. | Do as the message says. |
| The SQL Server for site <x> is not known (<reason>). Enter the SQL Server name in Settings, ConfigMgr Site Settings. | The provider did not return the site definition, or it has no definition for the code, or it has no SQL Server name. | Do as the message says. |
| Windows PowerShell could not start. Discover needs Windows PowerShell 5.1 (powershell.exe). | `powershell.exe` is missing. | Do as the message says. |
| SQL Server on <s> denied the Windows login. The account needs the db_datareader role on <db>. | The SQL login was denied. | Do as the message says. |
| Windows authentication to SQL Server <s> failed. Run the app as a domain account on a domain-joined computer, and check the SQL Server service principal name. | Windows authentication failed. | Do as the message says. |
| Cannot reach SQL Server <s>. Check the server name, the network path and the SQL Server firewall port. | No connection. | Do as the message says. |
| The scan stopped after N seconds. | The scan time limit passed. The limit is 600 seconds. | The code names no fix. |
| SQL Server on <s> received an anonymous login. Windows credentials do not pass through a second remoting hop without Kerberos delegation. Run the scan from a session that holds your own logon. | Second-hop credentials. | Do as the message says. |

## WSUS

See [WSUS publishing](../core/wsus-publishing.md).

| Message | Cause | Fix |
|---|---|---|
| Connecting to WSUS <server>:<port> failed: <message>. <hint> | The connection failed. The hint says "The WSUS console on this computer must match the server version." or gives a TLS hint. | Follow the hint. |
| The account must be a member of the WSUS Administrators group on the server. | The server answered "not a WSUS Administrator" or "Unauthorized". | Do as the message says. |
| This computer must trust the WSUS signing certificate: import it into the Local Computer Trusted Publishers store, and into Trusted Root Certification Authorities when it is self-signed. Options, WSUS Publishing, Export certificate saves it. | The server answered "Verification of file signature failed". | Do as the message says. In the window, the button is `Export certificate...` in Settings, WSUS Publishing. |
| The UpdateServicesPackages or WSUSContent share on the server is missing, not shared, or not writable. | The server answered "CreateDirectory failed". | Check the share. |
| WSUS rejected the package: <server text> <hint> | The server refused the publish. Other hints cover signing, a SQL timeout, too many categories, the console version and the cab size. | Follow the hint. |
| This application installs for the signed-in user. WSUS installs as SYSTEM, so a per-user installer would land in the wrong profile. | Refusal code PerUserInstall. | None. The app does not publish this application to WSUS. |
| This build installs through a custom command or script. WSUS runs the vendor installer with its silent arguments, so the customization would not reach the client. | Refusal code CustomInstall. | None. The app does not publish it. |
| This application has deployment-type variants. A WSUS update carries one installer, so the variant set cannot be published as one update. | Refusal code MultipleDeploymentTypes. | None. The app does not publish it. |
| The installer payload is N MB. A locally published update is one signed cab, and a cab cannot exceed 2047 MB. | Refusal code PayloadTooLarge. | The code names no fix. |
| WSUS server name '<x>' is not a host name. | The server name is malformed. | Correct the name. |

## Deployment Helper

See [Deployment Helper](../tools/deployment-helper.md) and [Deployment rings](../tools/deployment-rings.md).

| Message | Cause | Fix |
|---|---|---|
| A Required deployment installs on every device of the collection and needs its own confirmation, so nothing was created. | A Required deployment without the extra confirmation box. | Select the box. |
| Collection <id> is a built-in collection. Deployments to built-in collections are blocked. | The collection ID starts with `SMS`. | Choose another collection. |
| <object> is now deployed to collection <id> (deployment ID <x>), so no duplicate was created. | The same object is already deployed to the collection. | None. The app created nothing. |
| the audit log cannot be written, so the deployment was not created. <probe text> | The audit file is not writable. | Fix the audit path. Use `Check` on the Options tab. |
| another session of this product is creating a deployment of the same object to this collection now, or the lock file next to the audit log cannot be made. Nothing was created. Try again when that session finishes. | A target lock exists. | Do as the message says. |
| The change has no captured state, so it was not applied. Validate again. | The plan has no captured state. | Validate again. |

## DP Content Manager

See [DP Content Manager](../tools/dp-content-manager.md).

| Message | Cause | Fix |
|---|---|---|
| a removal needs the second confirmation. Nothing was removed. | A removal without the confirmation boxes. | Select the boxes. |
| N content would have no distribution point with the content installed (<names>). Select the box that accepts this. Nothing was removed. | The removal takes the last installed copy. | Do as the message says. |
| the audit log cannot be written, so nothing was removed. <probe text> | The audit file is not writable. | Fix the audit path. |

## Pivots and Scripts

See [Pivots and Scripts](../tools/pivots-and-scripts.md).

| Message | Cause | Fix |
|---|---|---|
| The administration service returned more data than the page reads. Narrow the query. | The answer is larger than 64 MB. | Narrow the query. |
| The SMS Provider name in Settings is not a host name. Enter the provider name without https:// or a path, normally the FQDN that its certificate carries. | `ProviderMachine` is malformed. | Do as the message says. |
| the plan was used already: a plan runs once. Prepare the plan again. | The plan was reused. | Prepare the plan again. |
| the plan is older than N minutes. The site can have changed since. Prepare the plan again. Nothing was sent. | The plan is older than 30 minutes. | Prepare the plan again. |
| Skipped: the script text on the site changed after the plan was made ... | The script hash changed. | Prepare the plan again. |

## Site Hygiene

See [Site Hygiene](../tools/site-hygiene.md) and [Site Hygiene Live](../tools/site-hygiene-live.md).

| Message | Cause | Fix |
|---|---|---|
| the plan is older than 15 minutes. Prepare it again on fresh data. | A fix plan is older than 15 minutes. | Prepare the plan again. |
| SQL is not set up. Enter the SQL Server of the site database in Options, or set the site code and SMS Provider so the SQL Server can be found. | No SQL Server is known. | Do as the message says. |
| The ConfigMgr session is connected to another site or SMS Provider than the settings name. Refresh again. | The settings changed after the connect. | Refresh again. |
| The SMS Provider did not answer the search for the site database in time. Refresh again, or enter the SQL Server in Options. | The search limit of 120 seconds passed. | Do as the message says. |

## Collections, Maintenance Windows and Compliance

See [Collection and Compliance](../tools/collection-and-compliance.md), [Collections and queries](../tools/collections-queries.md), [Maintenance Windows](../tools/maintenance-windows.md) and [Compliance](../tools/compliance.md).

| Message | Cause | Fix |
|---|---|---|
| Only a select query can be used here. The query must start with select. | The WQL text is not a select query. | Start with `select`. |
| The query can hold only one statement. Remove the semicolon. | The WQL text has a semicolon. | Remove it. |
| The query cannot hold a comment. Remove -- and the text after it. | The WQL text has a comment. A variant exists for `/*`. | Remove the comment. |
| Collection <c> has more than one window named <w>, so the window to change is not certain. Refresh the list and select the window again. | Two windows have the same name. | Do as the message says. |
| On a second run a builder creates a second configuration item and baseline with the same names ... so the page does not run it while objects with these names exist. Remove or rename them first. | The catalog entry is already deployed. | Do as the message says. |
| Remediation changes client settings and needs its own confirmation, so nothing was changed. | The remediation box is not selected. | Select the box. |
| The extracted builder does not match the shipped catalog, so it was not started. | A SHA-256 mismatch. | The code names no fix. |
| The change has no captured state, so it was not applied. Refresh the list and try again. | The change was not prepared. | Refresh the list and try again. |

## Installer Analysis

See [Installer Analysis](../tools/installer-analysis.md).

| Message | Cause | Fix |
|---|---|---|
| The path is a folder; open one installer file. | You gave a folder. | Open a file. |
| The file is larger than the analysis limit of <size>. | The file is over the limit of 8 GiB. | The code names no fix. |
| The file could not be analyzed; it may be damaged or not an installer. | The read failed. A second text, "The analysis stopped at the time limit.", covers the time limit. | The code names no fix. |
| 7-Zip <v> (<path>) is older than <min>; extraction is refused. Install 7-Zip <min> or newer. | The 7-Zip version is too old or cannot be read. | Install 7-Zip 25.01 or newer. |

## Add App and Workbench

| Message | Where it appears | Cause | Fix |
|---|---|---|---|
| Vendor and Application Name are required. | Add App, `Save` | The fields are empty. | Fill them. |
| A user definition named <file> already exists. Existing definitions are never overwritten. | Add App, `Save` | The file name collides. | Use another application name. |
| URL and Pattern are required. | Add App, `Test Scrape` | The fields are empty. | Fill them. |
| No version found. Check URL and Pattern. | Add App, `Test Scrape` | The scrape returned nothing. | Do as the message says. |
| No applications were found. Load packagers or save a bring-your-own installer first. | Workbench | The Workbench opened with an empty list. | Do as the message says. |

## Network, CVE, download and signing

| Message | Cause | Fix |
|---|---|---|
| Request refused: not an https URL: <url> | A non-https request in the CVE path. | The code names no fix. |
| WARNING: Webhook URL is not https; not sent: <host> | The webhook in the configuration is not https. | Use https. |
| The timestamp server URL must begin with http:// or https://. | The timestamp URL is not valid. | Correct it. |
| Refused download URL '<url>': it is not an absolute http or https URL. | A definition has a bad URL. | Correct the definition. |
| GitHub API rate limit reached for <url> (HTTP <status>, <anonymous requests or token source>); it resets at <time>. Set APP_PACKAGER_GITHUB_TOKEN or GITHUB_TOKEN, or run gh auth login, to raise the limit; Settings shows the current budget. | Anonymous requests are limited to 60 each hour. | Do as the message says. |

## Other message families

These messages are fixed text in the code. They are not repeated above.

- A tool refuses a reports folder that is a junction or a symbolic link. Pivots and Scripts, Site Hygiene, DP Content Manager and the compliance builder have a message of this kind.
- Pivots and Scripts results and sending: the page has fixed texts for a failed wait, a stopped run, a partial result, nothing to export, an export that does not fit the view, and no results yet. The service has texts for a sent script, a running script and a script that changed during the send.
- WSUS hints: console version, TLS, and database timeout.
- Detection Tester: fixed texts in the Detection Tester contract.
- Inno Setup payload and 7-Zip version-read texts in Installer Analysis.
