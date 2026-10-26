# App Packager Desktop

[![CI](https://github.com/jasonulbright/app-packager-desktop/actions/workflows/ci.yml/badge.svg?branch=main&event=push)](https://github.com/jasonulbright/app-packager-desktop/actions/workflows/ci.yml)
[![Release workflow](https://img.shields.io/github/actions/workflow/status/jasonulbright/app-packager-desktop/release.yml?label=release%20workflow)](https://github.com/jasonulbright/app-packager-desktop/actions/workflows/release.yml)
[![Latest release](https://img.shields.io/github/v/release/jasonulbright/app-packager-desktop?label=release)](https://github.com/jasonulbright/app-packager-desktop/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/jasonulbright/app-packager-desktop/total?label=downloads)](https://github.com/jasonulbright/app-packager-desktop/releases)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D4)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](#build-from-source)
[![License](https://img.shields.io/github/license/jasonulbright/app-packager-desktop)](LICENSE)

App Packager Desktop packages third-party Windows applications and publishes them to ConfigMgr, Intune and WSUS. It is a Windows desktop application. It is free and MIT licensed.

![The Packages page with the version, ConfigMgr, status and CVE columns filled for checked applications](docs/images/packages.png)

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
- For the suite tool entries in the sidebar: the App Packager Suite, installed for the current user.

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

## Sidebar

The sidebar on the left lists the pages and tools. **Packages** is the main page. The application always starts on it.

![The sidebar with the Packages page, the Application Workbench and the eight suite tools](docs/images/sidebar.png)

You control the sidebar:

- **Reorder:** drag an entry to a new place in the list. Press Alt+Up or Alt+Down on a selected entry to move it with the keyboard. Press Escape during a drag to cancel it.
- **Hide:** right-click an entry and select **Hide**. The entry leaves the sidebar. Its settings and data stay.
- **Show again:** right-click the empty area of the sidebar, or any entry, and select **Show tool**. Then select a hidden entry. It returns to the place it had.
- **Reset:** right-click and select **Reset sidebar** to restore the default order and show every entry.
- **Settings:** the Sidebar section of the **Settings** page lists every entry with a check box and Up and Down buttons. It makes the same changes without the mouse.

Packages cannot be hidden or moved, and no entry can be dropped above it. **Settings** stays at the bottom. The order and the hidden entries are saved for the current Windows user.

The eight entries after the Application Workbench are Detection Tester, Maintenance Windows, Collection and Compliance, Installer Analysis, Deployment Helper, DP Content Manager, Pivots and Scripts and Site Hygiene. All eight are native pages in App Packager Desktop; see [Detection Tester](#detection-tester), [Maintenance Windows](#maintenance-windows), [Collection and Compliance](#collection-and-compliance), [Installer Analysis](#installer-analysis), [Deployment Helper](#deployment-helper), [DP Content Manager](#dp-content-manager), [Pivots and Scripts](#pivots-and-scripts) and [Site Hygiene](#site-hygiene). The sidebar starts no suite tool.

- App Packager Desktop finds the suite in this order: the `InstallLocation` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\AppPackagerSuite`, then `%LOCALAPPDATA%\AppPackagerSuite`.
- To find a tool, App Packager Desktop first uses `ToolsRoot` from the `suite.settings.json` file of the suite, when that value is set. It then uses the tool folders of the suite installation.
- If the suite or a tool is not installed, a message names what is missing and where App Packager Desktop looked.
- A second click on a tool that is open brings its window to the front.
- App Packager Desktop passes the ConfigMgr site code and provider to the tool in the environment variables `SUITE_CM_SITECODE` and `SUITE_CM_PROVIDER`. It passes the site code you saved. When you did not save one, it passes only a site code that the environment already holds from the suite launcher. It passes the provider only when it is set. A tool uses these values only when its own settings are empty.
- If a tool stops right after it starts with a non-zero exit code, a message names the tool and its exit code.

## Detection Tester

The **Detection Tester** page writes ConfigMgr detection clauses and tests them on this computer. It makes no ConfigMgr connection and no network request.

![The Detection Tester page with a passed registry clause and the history of earlier tests](docs/images/detection-tester.png)

The **Detection** tab:

- **Clause types:** a registry value, a registry key, a file and a script. A compound clause (And, Or) appears after you import one.
- **Registry:** choose the hive (HKLM or HKCU) and the registry view (64-bit or 32-bit). The key path accepts a prefix such as `HKLM\`. A prefix that names another hive than the clause is an error.
- **File:** the rule checks that the file exists or compares its version. A 32-bit rule reads the 32-bit folders.
- **Script:** the script runs in a child Windows PowerShell, as the current user, not as SYSTEM, with a limit of 30 seconds. **Cancel** stops it. The script counts as detected when it prints output. The result shows the output, the error output and the exit code. ConfigMgr runs a detection script as SYSTEM and also reads the exit code, so a script can give another result in ConfigMgr. A script that leaves a child process running does not keep the page waiting, and the child process ends with the script.
- **Negative test:** a clause that is not detected passes.
- **Import:** **Import stage-manifest.json** loads the detection of a staged build, with compound clauses. Nothing runs until you press **Run**. A manifest without a detection shows a message and changes no input.
- **History:** every test adds a row. **Use selected** fills the inputs from a row. **Export CSV** and **Export HTML** write a report. **Clear history** asks first. History keeps the clause text, including script text, on this computer. A CSV cell that starts with `=`, `+`, `-` or `@` is written as text, and the HTML report encodes all text.

![The Installed Applications tab with the list, the filter and the detail panel](docs/images/detection-installed.png)

The **Installed Applications** tab lists the applications from the three uninstall registry locations (HKLM 64-bit, HKLM 32-bit and HKCU). It has a filter, a detail panel and **Copy details**. **Show system components** adds the entries that Windows marks as system components. **Use for detection** fills a clause for the selected application.

The history and the reports are in the `DetectionTester` folder of the data folder (see [Data and network](#data-and-network)). A history file that cannot be read stays in place until the next save. A folder that is a junction or a symbolic link is refused.

## Maintenance Windows

The **Maintenance Windows** page shows, creates, edits and removes the maintenance windows of the ConfigMgr device collections. It connects to the SMS Provider through the ConfigMgr session of the application. It makes no other network request. Every change shows a plan first. Nothing changes until you read the plan, select the confirmation box and select the action button.

![The Windows tab with eight windows, a selected window and its properties](docs/images/apd7-mw-list.png)

The **Windows** tab:

- **List:** one row for each window. The filters are the text filter, the window type, the state (enabled or disabled) and the collection folder. **Only collections with windows** hides the collections that have none. The detail panel shows the properties of the selected window and its next five occurrences. The panel shows the description of a window only when a person wrote it.
- **Schedule kinds:** one time, daily, weekly, monthly by date, monthly by weekday and Patch Tuesday with an offset. Window types are all deployments, software updates and task sequences. The page reads each schedule from the stored ConfigMgr schedule and shows the same schedule text as ConfigMgr.
- **New Window, Edit:** the editor shows the schedule text and the next five occurrences while you type. Edit keeps the ID, the enabled state and the description of the window. The editor shows the description and does not change it.

![The Edit dialog with a description, a monthly schedule and the next five occurrences](docs/images/apd7-mw-editor.png)

- **Rename:** the page creates the window with the new name first. It removes the old window only after the new window exists. If the create fails, the old window stays unchanged.
- **Toggle Enabled:** enables or disables the selected windows.
- **Remove, Undo:** Remove deletes the exact windows that you selected, not other windows with the same name. **Undo** reverses the last run. Undo of a removal creates the window again with the same values and a new ID. A run that stops part way keeps the Undo data of the changes it made.
- **Not stored by ConfigMgr:** ConfigMgr stores no end date for a window. A window with an end date is refused. A window with a schedule that the page cannot represent is read-only: you can enable or disable it, and you cannot edit, rename or remove it. A window with a description is refused when the ConfigMgr module cannot write a description. The message names the reason.
- **Stale plans:** the page checks the window again when it writes. A window that another person changed after the plan was shown is skipped, and the result says so. ConfigMgr has no conditional write, so a change made in the short time between this check and the write can still be lost.
- **Export CSV, Export HTML:** write a report of the shown windows. A CSV cell that starts with `=`, `+`, `-` or `@` is written as text. The HTML report encodes all text.

![The plan dialog for a removal](docs/images/apd7-mw-plan.png)

The **Coverage** tab lists every device collection with its member count and its number of windows. A green mark means the collection has a window. An orange mark means it has none. **Only collections without a window** shows the gaps. Select collections and **Apply template** to give them a window.

![The Coverage tab with the collections that have and do not have a window](docs/images/apd7-mw-coverage.png)

The **Templates** tab lists five shipped templates and your own templates. **New Template**, **Edit** and **Delete** change your own templates only. A shipped template cannot be changed or deleted. **Apply** opens the Apply template dialog.

![The Templates tab with the five shipped templates](docs/images/apd7-mw-templates.png)

In the Apply template dialog, choose the template and the collections. You choose what happens when a collection already has a window with the template name: skip it, or create the window with a new name, for example `Name (2)`. The plan lists every collection and marks each skipped collection. **Cancel** during a run stops the run. The changes already made stay, and **Undo** reverses them.

![The Apply template dialog with two collections selected](docs/images/apd7-mw-apply-template.png)

The page follows the light and the dark theme. You can create a window with the keyboard only.

![The Windows tab in the dark theme](docs/images/apd7-mw-dark.png)

Your templates and the exported reports are in the `MaintenanceWindows` folder of the data folder (see [Data and network](#data-and-network)). The page saves the selected tab and the filters for the current Windows user. A folder or a template file that is a junction or a symbolic link is refused. Each completed run adds one line to the operation history. The line holds counts and no window names.

## Collection and Compliance

The **Collection and Compliance** page shows and changes the device collections of the ConfigMgr site, their membership rules, query templates and saved site queries. It connects to the SMS Provider through the ConfigMgr session of the application and makes no other network request. Every change shows a plan first. Nothing changes until you read the plan, select the confirmation box and select the action button. The page has two sections, chosen with the switch at the top right: **Collections** and **Compliance**. The sections below describe Collections first and Compliance after it.

![The Collections tab with the folder column and the detail panel](docs/images/apd8-cc-collections.png)

The **Collections** tab:

- **List:** one row for each device collection, with members, limiting collection, refresh type, type (built-in or custom), rules and folder. The filters are the text filter, the folder, **Empty**, **With direct members** and **With query rules**. A folder that has sub-folders shows as `Folder (and sub-folders)` and covers everything below it. **Details** shows or hides the detail panel. Its tabs are Properties, Direct Members (paged), Query Rules, Include Rules and Exclude Rules.
- **Open by ID:** loads one collection without loading the list. **Refresh** loads the whole list. **Connect Site** tests the connection and loads no inventory. After Open by ID, changes that need the whole list ask you to select Refresh first.
- **New, Copy, Edit:** the dialog sets the name, the comment, the refresh type, the refresh interval and the limiting collection. The interval is 1 to 59 minutes, 1 to 23 hours or 1 to 31 days. A new collection with no interval refreshes every 7 days. An existing collection keeps its schedule when you leave the interval empty. The page reads the stored schedule back after the write and reports a failure when ConfigMgr stored another schedule.

![The Edit dialog with the refresh interval and the limiting collection](docs/images/apd8-cc-editor.png)

- **Evaluate:** starts a membership update. It cannot be undone.
- **Remove:** a built-in collection cannot be removed. A collection that limits another collection, or that another collection includes or excludes, cannot be removed. The plan shows the member count, the deployments, the maintenance windows and the references, read from the site when the plan opens. ConfigMgr removes the deployments and the maintenance windows with the collection. If a count or a property changes after the plan opened, the collection is not removed. **Undo** creates the collection again with its properties and rules. The new collection has a new ID. Deployments and maintenance windows are not restored.

![The plan dialog for removing a collection with its impact](docs/images/apd8-cc-remove-plan.png)

- **Undo:** reverses the last run. A run that stops part way keeps the Undo data of the changes it made. A write that may have changed ConfigMgr but whose result is unknown gives no Undo, and the message says to check ConfigMgr.
- **Stale plans:** the page checks the collection again when it writes. ConfigMgr has no conditional write, so a change made in the short time between this check and the write can still be lost.
- **Export CSV, Export HTML:** write a report of the shown collections. A CSV cell that starts with `=`, `+`, `-` or `@` is written as text. The HTML report encodes all text.

The **WQL Editor** tab changes the query rules of one collection.

![The WQL Editor tab with a query rule and the preview](docs/images/apd8-cc-wql.png)

- **Validate, Preview:** the page accepts one `select` statement. It refuses a comment, a semicolon and a second statement before the query reaches the provider. Preview shows the match count and the first rows in the order of the select list. Cancel stops it. A preview stops reading after 10,000 rows and says "or more".
- **Add Rule, Update Rule, Remove Rule:** each shows a plan with the query. Update adds the new rule first, checks it, and then removes the old rule. The collection never has fewer rules than before. When a step fails, the old rule stays and the message names the rules that exist.
- **Rule names:** ConfigMgr stores `*`, `?`, `[` and `]` in a rule name. The page accepts them and matches names exactly.

The **Templates** tab lists 245 query templates, with a category filter.

![The Templates tab with a parameterized template and the final query](docs/images/apd8-cc-templates.png)

A parameterized template shows an input for each value and the final query before you use it. A value inside quotes is escaped with a backslash. A value outside quotes must be a whole number. A value for a `like` comparison cannot contain `%`, `_` or `[`. **Copy to WQL Editor** loads the final query. **Apply to Collections** adds it as a query rule to the collections you select. The plan marks a collection that already has a rule with that name, and skips it. Undo removes the rules that the run added.

The **Site Queries** tab lists the saved queries of the site: **Refresh**, **Load to Editor**, **Save as New**, **Update Saved** and **Delete**. Each write shows a plan. Undo of a delete creates the query again with a new ID.

![The Site Queries tab with the query text](docs/images/apd8-cc-queries.png)

The page follows the light and the dark theme.

![The Collections tab in the dark theme](docs/images/apd8-cc-dark.png)

The exported reports are in the `Collections` folder of the data folder (see [Data and network](#data-and-network)). The page saves the selected tab and the filters for the current Windows user. A folder that is a junction or a symbolic link is refused. Each completed run adds one line to the operation history. The line holds counts and no names or queries.

### Compliance

The **Compliance** section manages configuration items and baselines of the site. It has four tabs: Catalog, Live CI/CB, Editor and Auditing. It needs a console role with Compliance Settings Manager rights. Every change shows a plan first, as in the Collections section.

![The Catalog tab with 16 entries and the details of one entry](docs/images/apd9-comp-catalog.png)

The **Catalog** tab lists the 16 catalog entries that ship with the application. Each entry creates configuration items and baselines for one hardening, compliance or configuration setting. The filters are the text filter, the CVE or plugin ID, and the lists for subcategory, severity, status and operating system. The details show the description, the ID and version, the operating systems, the restart flag, the registry values and the objects that the entry creates.

![The Deploy dialog for the entry SMB Signing Required](docs/images/apd9-comp-deploy.png)

- **Deploy:** the dialog shows one input for each parameter of the entry. The site code and the site server come from your settings. **DetectOnly** is on by default: clients report compliance and change nothing. A collection name cannot contain `*`, `?`, `[` or `]`, because ConfigMgr reads them as wildcards.
- **Plan:** the plan names the configuration items and the baselines that the entry creates, the collections, their member counts and the mode. A deploy is refused while a configuration item or a baseline with the same name exists, because a second run would make duplicates. A missing collection stops the plan with a message.
- **Remediation:** a deploy with remediation changes registry values on every device of the collection. The plan lists the registry paths and the number of devices, and the action button stays off until you select the second confirmation box. Enable Remediation, Redeploy, Reassign, the Undo of such changes and a setting that remediates need the same box.
- **Run:** the entry runs from a private copy in a new folder of the data folder. The application checks the file against its recorded SHA-256 hash right before the run and removes the folder after the run. The run has a time limit, and **Cancel** ends the builder and every process that it started. The plan dialog shows each step while the builder runs. After the builder, the application adds the drift tag `[ccm:<ID>@<version>]` to the baseline description one time.
- **Undo:** Undo removes the deployments, the baselines and the configuration items that the run created, and only these. An object that existed before the run is never removed. An object that something else uses, for example a deployment that another administrator made during the run, stays, and the message names it. After a Cancel, a baseline can appear a few seconds late. The result then says so, and Undo can be used again later.

![The plan dialog for a deploy with remediation and its confirmation box](docs/images/apd9-comp-plan.png)

The **Live CI/CB** tab lists the configuration items, the baselines and the deployments of the selected baseline. The counts of the baselines show compliant, non-compliant and error devices. The page reads these lists in a fixed number of queries and makes no query for each row.

![The Live CI/CB tab with the deployments of a baseline](docs/images/apd9-comp-live.png)

- **Enable remediation, Disable remediation:** change the mode of the selected deployments. The page reads the stored mode back after the write.
- **Redeploy:** creates the deployment again. It removes the old deployment only after the new one passes the read back. If the new deployment differs or the write fails, both deployments stay and the message says so.
- **Reassign:** creates the deployment on another collection first, and removes the old one after the read back.
- **Delete:** removes the selected deployments. The plan shows their compliance counts. Undo creates them again with a new ID. This also works for a deployment on a built-in collection.

The **Editor** tab shows the settings of one configuration item. **Add**, **Edit** and **Remove** change a registry value setting or a script setting.

![The Edit setting dialog for a registry setting](docs/images/apd9-comp-editor.png)

- **Plan:** the plan shows the old and the new values. ConfigMgr gives the configuration item a new revision with each change, and clients evaluate the new revision. A change of the data type, the rule or the value adds the new setting first and removes the old setting after it. If a step fails, both settings stay and the message names them.
- **Rules of ConfigMgr:** a DateTime setting with remediation is not stored, so the dialog refuses it. A Boolean value works for script settings only. A StringArray value works for registry settings only. ConfigMgr allows two settings with the same name, and a removal by name removes all of them, so the page uses the setting ID.
- **Undo:** Undo sets the old values back. A recreated setting has a new ID.

The **Auditing** tab has two lists. **Drift** compares the version in the drift tag of each baseline with the catalog version: Current, Behind or Untagged. A tag that cannot be read shows as Unknown.

![The Drift list with the states Current, Behind and Untagged](docs/images/apd9-comp-drift.png)

**Compliance** shows the compliant, non-compliant, error and unknown counts of each deployment and its last evaluation. A count that ConfigMgr did not report shows `unknown`, not 0. A deployment that no client has reported on shows `never` and Stale. **Export CSV** and **Export HTML** write all rows of both lists, without the filters. **Open folder** opens the reports folder.

![The Compliance list with counts, last evaluation and freshness](docs/images/apd9-comp-compliance.png)

The page follows the light and the dark theme.

![The Live CI/CB tab in the dark theme](docs/images/apd9-comp-dark.png)

The files of a catalog run are in the `Collections` folder of the data folder (see [Data and network](#data-and-network)). Each completed run adds one line to the operation history. The line holds counts and no names or script text.
## Installer Analysis

The **Installer Analysis** page reads a Windows installer on this computer and reports the data that you need to package it. It makes no ConfigMgr connection and no network request, and it needs no elevated rights. It never starts the installer or any file taken out of it. Installers are untrusted input: every reader has limits for size, entry count, depth and time, and a damaged or hostile file gives a plain message.

![The Overview tab for an Inno Setup installer](docs/images/apd10-ia-overview.png)

Drop one file on the page, type a path and press Enter, or select **Open**. A folder, several files and a shortcut give a plain message. The page reads these installer types: NSIS, Inno Setup, InstallShield, WiX Burn, 7-Zip and WinRAR self-extracting archives, BitRock, Advanced Installer, MSI, MSP, MSIX and APPX, `.intunewin`, NuGet packages, PSAppDeployToolkit, Squirrel and Velopack.

The **Overview** tab shows these items:

- **Source facts:** type, architecture, size, SHA-256 and the Authenticode signature with its signer. The page does not fetch certificates from the network, so a file whose intermediate certificate is not cached can show NotTrusted.
- **Deployment fields:** display name, version, publisher, silent install command, silent uninstall command and the uninstall registry key. The **Confidence** column says **Confirmed** when the page read the value from the installer. It says **Predicted** when the page worked it out, for example when the install folder is chosen at run time. Check a Predicted value before you use it in a detection method.
- **Install modes:** one row for each mode (per machine and per user) with its switch, folder, uninstall command, registry key, hive, view and context. The registry view is the 32-bit or 64-bit view that the installer writes to.
- **MSI properties, package metadata and notes:** the notes say plainly what the page could not read, for example an encrypted Inno Setup header or an NSIS header that uses bzip2.
- **Post-patch target:** for a file that holds a base MSI and an MSP, the page shows the product code, version and registry key that the patched product writes.

A value is only as good as the installer allows. The page checked its predictions against real installs in the lab (an NSIS, an Inno Setup, a BitRock and several MSI installers). An MSI without an `ALLUSERS` property writes its Add/Remove Programs key under `HKLM`, but Windows Installer registers the product for the installing account only. The page says so.

![The MSI Properties tab](docs/images/apd10-ia-msi-properties.png)

The other tabs:

- **MSI Properties:** the full Property table, with a filter.
- **Payload:** every file in the installer. **Analyze selected** opens an inner installer. **Extract to temp**, **Copy name**, **Copy full path** and **SHA-256** act on the selected row. The SHA-256 of a large file is computed only when you ask, and **Cancel** stops it. **Extract payload** writes the files into a new `<file name> payload` folder inside a folder that you choose. A link in an archive is listed as a link and is never created.
- **Inner Installers:** the installers inside the payload. **Analyze selected** drills into one. The breadcrumb and **Back** return to the outer file. The depth limit is 5.

![The Inner Installers tab with the breadcrumb](docs/images/apd10-ia-inner.png)

- **File Browser:** the payload as folders, with a filter.
- **Registry Settings:** the registry values that the installer writes. They come from the MSI Registry table, the NSIS script and the Inno Setup header. Each row shows the origin, action, hive, key, name, value, type, view, section and condition.

![The Registry Settings tab for an NSIS installer](docs/images/apd10-ia-registry.png)

- **Strings:** the readable text in the file (ASCII and UTF-16), with a category and a filter. The page stops at a fixed number of strings.

![The Payload tab with the SHA-256 box](docs/images/apd10-ia-payload.png)

Export and copy:

- **Copy summary** and **Copy JSON** put the analysis on the clipboard. The JSON is a ConfigMgr digest with detection hints for the installer type. The key material of an `.intunewin` file is never shown or exported.
- **Export CSV** and **Export HTML** write a report to the `InstallerAnalysis` folder of the data folder. A CSV cell that starts with `=`, `+`, `-`, `@`, a tab, a carriage return or a line feed is written as text. The HTML report encodes all text. If 7-Zip is missing, the HTML report says that the payload was not listed. If a list was cancelled or failed, the export is refused and the status says why. **Open folder** opens the reports folder.

The page uses 7-Zip, when it is installed, to list and extract archives. The page does not extract with a 7-Zip older than 25.01, because older versions can write outside the target folder. Analysis and listing still work.

Extracted files go to a private folder under the data folder, one folder for each analysis level. The page removes these folders when you go back a level, when you leave the page, when the application closes, and at the next start. The page refuses entry names that leave the folder (`..\`, absolute paths, device names, alternate data streams) and any link in the target path. A limit stops an archive with too many entries, too much data or an extreme compression ratio before the page writes it. **Recent files** keeps the last 10 paths for the current Windows user; **Clear recent** removes them. A path is never read until you open it. The page keeps no other analysis data on disk.

![The Overview tab in the dark theme](docs/images/apd10-ia-dark.png)
## Deployment Helper

The **Deployment Helper** page creates one deployment of an application, a package, a task sequence or a software update group to a device collection. A Required deployment is enforced on every device of its collection, and the clients then try to install the software, so every step is checked and every change shows a plan first. Nothing changes until you read the plan, select the confirmation box and select the action button. The page connects to the SMS Provider through the ConfigMgr session of the application and makes no other network request. The **Rings** tab deploys one object to a series of collections, see [Rings](#rings).

![The Deploy tab after Validate, with the checks and the result of an Undo](docs/images/apd11-dh-deploy.png)

The **Deploy** tab:

- **Object and collection:** select the type (Applications, Packages, Task Sequences or Software Update Groups), then the object, and for a package the program. Select a device collection. A collection whose ID starts with `SMS` (a built-in collection) cannot be chosen. User collections are not offered. Names are matched exactly: a `*` or `[` in a name is a plain character.
- **Purpose and times:** Available or Required, the available time, the deadline and the time basis (local time or UTC). The deadline must be later than the available time. A deadline in the past is blocked. An Available time in the past needs an extra confirmation. `Available` with `Hide in Software Center and all notifications` is blocked, because the deployment would do nothing visible.
- **Options:** each type shows its own options. For an Available deployment, ConfigMgr ignores the rerun, maintenance window, metered connection and restart settings, and the page marks those inputs. The slow network option `RunProgramFromDistributionPoint` is refused, because ConfigMgr rejects it.
- **Distribution points:** the list shows the status of each DP group. **Distribute content** shows a plan and cannot be undone: the content stays on the distribution points.
- **Templates:** **Apply template** fills the purpose, notification, time basis, options and deadline offset. A template holds no object or collection. Four templates ship with the page: Workstation Pilot, Workstation Production, Server Pilot and Server Production. **Save as template** keeps your own.
- **Validate:** the page runs these checks: the object exists, the content is distributed, the collection is a device collection, the collection is not built in, no deployment of this object to this collection exists, the schedule and options are valid, the audit log can be written, and the script-signing rule. A check with Fail or Unknown blocks the deployment. ConfigMgr refuses a second application deployment to the same collection. It allows a second package, task sequence or update group deployment, so the page checks for these itself.
- **Deploy:** the plan lists the object, the collection with its member count now, the purpose, the times, the options and the result of every check.

![The plan for a Required deployment with its second confirmation](docs/images/apd11-dh-plan-required.png)

- **Required deployments:** the plan names the number of devices and the deadline, and the action button stays off until you select the extra box. The service refuses a Required deployment without this confirmation, whatever the page sends.
- **Script signing:** for an application that this product packaged, a Required deployment follows the signing policy of the product. The page finds the application by its CI ID, and by its name when no CI ID is stored. An older packaged application that was renamed in the console before the page first listed it can pass as an application that the product did not package.
- **Distribute and create:** with checked DP groups and **Distribute first**, one plan distributes the content and creates the deployment. The distribution is requested first. The deployment is created when the content check passes. A box on the plan lets you create the deployment at once while the content still distributes.

![A plan that distributes the content and creates the deployment](docs/images/apd11-dh-distribute-create.png)

- **Stale plans:** the service keeps the state that it read when the plan opened: the object revision, the content references and the member count. If the object or its content changed, or the member count of a Required collection grew, the page skips the deployment and asks you to validate again. A decrease of the member count is allowed.
- **Stored values:** the page reads the deployment back after the create and compares it with the plan. A difference fails the result and names the values. The page sends the UTC flag with the available time only: a UTC package or task sequence deployment then has no expiry.
- **Undo:** Undo removes the deployment that this run created, found by its deployment ID. It asks one more question first. Software that clients already installed stays installed. A change that was not attempted starts with "Skipped" in the result.

The **History** tab lists the audit log with filters for the object type and the event. **Export CSV** and **Export HTML** write the rows to the `Deployments` folder of the data folder (see [Data and network](#data-and-network)). A CSV cell that starts with `=`, `+`, `-`, `@`, a tab, a carriage return or a line feed is written as text, and the HTML report encodes all text. The page shows the newest 2000 records and says so when the log holds more. **Remove deployment** opens the plan for the selected record.

![The History tab with the audit log](docs/images/apd11-dh-history.png)

- **Audit log:** every create, distribution and removal adds one record to an append-only file (`deployment-audit.jsonl`). A deployment is created only when the file can be written. The page never rewrites or shortens the file. Each record ends with a hash that covers its fields and the previous record. The **Verified** column shows whether a record passes this chain.
- **Undo from History:** it works only for a verified record. The page also checks the record against the site: the creator account, the creation time within 5 minutes, the stored values and, for an application, the object revision. If a check fails, the page refuses and says why. A record that was edited by hand, or that comes after an edited record, shows "Not verified" and cannot be undone from History. Remove such a deployment in the console. Undo from History checks the audit record against the site. It cannot stop a process of the same account that forges both.
- **Creation proof:** applications and update groups carry the creation time and the account. Packages and task sequences have neither, so the page reads status message 30006 of the site. If the site no longer holds that message, Undo from History is refused.
- **Two deployments at once:** a lock file next to the audit log stops two sessions of this product from creating the same deployment at the same time. The site cannot stop another administrator. If a second deployment of the same object appears during the create, the result is Partial with an Undo for the one made here. If the page cannot tell which deployment it made, the result names both IDs with their creators and times and offers no Undo.

![The History tab in the dark theme](docs/images/apd11-dh-dark.png)

The **Options** tab sets the audit log path and shows whether the log can be written. A share path is allowed. It also lists the templates, with **New**, **Edit** and **Delete** for your own.

![The Options tab with the audit log and the templates](docs/images/apd11-dh-options.png)

The page saves the selected tab, type and template for the current Windows user. Each completed run adds one line to the operation history, with counts only.
### Rings

The **Rings** tab deploys one object to any number of collections, from 1 up to 100. A plan lists the rings in order. Each ring names a device collection, a purpose, an available offset and a deadline offset in days from the plan start, and the options of the object type. A run turns one object and one plan into ordinary deployments, one for each ring. ConfigMgr does not know that they form a sequence. The page keeps that in a run file. You create the collections yourself, for example ten 10 % collections with a membership query on the resource ID, plus a pilot and a final ring.

![The Rings tab with a 13-ring plan expanded](docs/images/apd12-rings-new-run.png)

- **Plans:** the shipped plans are examples (Workstation-Rings with four rings and Server-Rings with two). They name no collection, so they cannot run until you set one for each ring. **New**, **Edit**, **Copy** and **Delete** change your own plans. A plan is a JSON file, and the plan editor keeps every option of a ring that you do not change. A plan file can be shared with the Deployment Helper tool of the App Packager Suite (a separate program), because both use the same file format.
- **Plan editor:** add, remove, duplicate and move rings. **Add rings from collections** makes one ring for each collection that you select in the picker, sorted by name with numbers in number order (Wave 2 before Wave 10), or in the order of the picker. **Fill offsets** sets the first available offset, the days between rings and the days to each deadline, and you can still change every ring. The plan checks these rules: rings do not share a collection, a built-in collection (ID starts with `SMS`) is refused, a Required deadline is later than the available time and later than the deadline of the previous Required ring, and a deadline in the past is refused. A plan over 100 rings is refused.
- **New run:** select the object, the plan and the plan start, and **Expand**. The preview shows each ring with its collection, member count, purpose and times. **Set collection** changes the collection of a ring for this run only. **Validate** runs the checks of the single deployment page for every ring and the rules of the plan.
- **All at once or hold:** by default the run creates every ring now with its own future dates, and ConfigMgr enforces the schedule. With **Hold later rings**, the run creates only the first ring. **Promote** creates the next ring after you look at the live counts of the previous ring. Nothing is promoted by itself.
- **Create run:** one plan lists every deployment that the run creates, with the options of each ring, held rings included. Required rings need the extra box: it names the number of Required rings and the number of devices in total. The service refuses each Required ring without it.

![The plan editor with a plan of 13 rings](docs/images/apd12-rings-plan-editor.png)

![The plan of a run with Required rings and its second confirmation](docs/images/apd12-rings-plan-required.png)

- **If a ring fails:** a run stops at the first ring that is not fully created. The rings before it stay, the rings after it are not tried, and the result says so. **Undo run** removes the rings that were created.
- **Open runs:** each run shows its rings with the status Created, Creating, Held, Removed or Cancelled, and the live counts from the site: targeted, success, error, in progress and unknown. A count that the site has not summarized shows `unknown`. **Refresh and reconcile** reads the counts and checks the runs against the site.

![Open runs with live counts and a held run](docs/images/apd12-rings-open-runs.png)

- **Promote:** the previous ring must be created. The page checks that the object is still the same, runs the checks of the deployment page again, and re-reads the live counts. If a ring has a success threshold, the success rate of the previous ring must reach it. A ring with no counts keeps the gate shut, and the page names the reason. If the deadline of the ring has passed, the plan offers to move that ring and every later held ring by the same time, so the ring keeps its time from available to deadline. A Required ring needs its own extra box.
- **Undo run:** it removes only the deployments that this run created. The page checks each deployment against the audit log, the site and the run file, as Undo from History does. A deployment that fails a check is kept, and the plan names it and the reason. Held rings are cancelled. A closed run that still has created rings can be undone from **Show closed runs**. A deployment made by another run, by the Deploy tab or by hand is never removed.
- **Close run:** closes a run without removing anything. A finished run closes itself at the next Refresh and reconcile.
- **Crash and stale state:** a ring is saved as Creating before the page creates it. After a crash, **Refresh and reconcile** adopts exactly one deployment that matches the ring and can be proven to belong to this run. If two match, the ring stays blocked. If none appears within 5 minutes, the ring returns to Held. **Abandon ring** lets you return a ring in Creating to Held yourself, after a plan that says a deployment may exist. A ring is marked Removed only when ConfigMgr no longer has the deployment, never because a summary row is missing.
- **Run files and locks:** the plans and run files are in the `Deployments\Rings` folder of the data folder. The **Options** tab sets both folders, and a share path is allowed so that a team can share runs. **Check** shows whether the folders are writable and a lock can be created. Promote, Undo run, Close and Reconcile take a lock file next to the run file and read the run file again under the lock. If another session holds the lock, the page names the lock file and its owner. A lock older than one hour is reported as stale and is never removed: another session may still be running, so check before you remove it by hand. A run file that cannot be read shows "Unreadable ring N", and Promote and Undo run stay off for it.
- **History:** every ring create, Promote, Undo, Close, cancelled ring and Abandon adds a record to the audit log, with the plan name, ring number, ring name and run ID. History shows these columns and a Run filter.

Limits: the lock only protects sessions that share the same runs folder, and the site cannot stop another administrator from creating a deployment of the same object. Undo from History checks the audit record against the site, and it cannot stop a process of the same account that forges both. A ring that the page cannot prove to belong to a run is kept, not removed.
## DP Content Manager

The **DP Content Manager** page shows which content is on which distribution point, finds content that has a problem, and changes the content of distribution points (DPs). It reads the site through the SMS Provider. It writes only after a plan that you confirm.

![The DPs tab with the detail of one distribution point](docs/images/apd13-dpcm-dps.png)

- **DPs:** one row for each distribution point, with the number of content objects, how many are installed, in progress and failed, the percent that is OK, whether it is a pull DP, and the size. Select a DP to see its content.
- **Content:** one row for each application, package, software update package, boot image, operating system image, operating system upgrade package, driver package and task sequence. An application shows its application name. Select a content object to see its status on each DP. The Deployments column counts the deployments that use the content.
- **Status Issues:** every pair of a DP and a content object that is not installed. A pair with an error message in its status shows as Failed, whatever its state number.
- **History:** every write that the page made. See [History and audit](#history-and-audit).
- **Filters, sort and export:** each tab filters by text and by state, and sorts by any column. **Export CSV** and **Export HTML** write the rows that you see, with the view, the time, the row count and the filter in the file. A formula in a cell is made safe in the CSV.

![The Content tab with the status of one content object on each DP](docs/images/apd13-dpcm-content.png)

![The Status Issues tab with two failed pairs](docs/images/apd13-dpcm-issues.png)

### Actions

- **Validate** asks the site to verify the content on the selected pairs.
- **Redistribute selected DPs** and **Redistribute selected** copy the content again to the selected pairs. **Redistribute failed** does this for all failed pairs of the view.
- **Remove from DP** removes the content from the selected DPs. **Remove failures** removes the failed pairs. With no pair selected, no pair is ticked.
- A list of the pairs opens first. You untick the pairs that must not change. Only the ticked pairs go to the plan.
- **Undo last change** copies the content of the last removal back to the DPs. It restores the pairs that were installed. It does not restore task sequence pairs. The removal plan says how many pairs Undo does not restore.

![The plan of a removal with the three confirmation boxes](docs/images/apd13-dpcm-plan-remove.png)

### Plan and checks

Each action shows a plan before it writes. The plan lists every pair, the content that has no DP left after the change, the deployments that use the content, and the pairs that the service left out because the site changed since you looked. A removal needs three boxes. The first confirms the removal. The second names the number of pairs. The third confirms that you accept the loss of the last installed copy of a content object. The service enforces the boxes. A removal without them is refused, whatever the page sends.

- **Last copy:** the service reads the copies on other DPs again before each part of a removal. If another process removed the last other copy during the run, that part and the later parts are skipped.
- **Stale pairs:** the service reads the state of each pair again before it writes. A pair that the site no longer lists is skipped, with the reason.
- **Size of a run:** the service works in parts of 25 pairs. A run takes at most 500 pairs.
- **Audit first:** a removal writes a Started record to the audit log before it removes anything. If the log cannot be written, the removal does not start.

### Results

The site processes the requests one after another and later. The page therefore shows two results.

- **Requested:** the site accepted the request. The read-back did not yet show the new state.
- **Confirmed:** the read-back showed the new state. The page reads the site every 5 seconds for 60 seconds.
- **Unchanged:** the site accepted the request, but the state did not change. This happens, for example, when you distribute again right after a removal. The site returns OK and does nothing. Try again later.
- **Failed** and **Skipped:** the site refused the request, or the page did not try the pair. The result text says why.

![The result panel after a redistribute](docs/images/apd13-dpcm-results.png)

### History and audit

Every write adds records to `dp-content-audit.jsonl` in the `DpContent` folder of the data folder: Started, Finished and ReadBack, with the pairs, the user and the result. The records form a hash chain. **History** shows the records, newest first, and marks each one **Verified** or **Not verified**. A record that was edited by hand, or a record after a broken line, shows Not verified. History reads the audit log only; it makes no call to the site.

![The History tab with the records of a removal](docs/images/apd13-dpcm-history.png)

The page works in light and dark themes.

![The Content tab in the dark theme](docs/images/apd13-dpcm-dark.png)

### Limits

- The site processes requests later. A Requested result is not a Confirmed result.
- Undo of a removal copies the content again. This can take a long time for large content.
- The audit chain finds edits to single records. A process that runs under the same account can write a new chain that has no break. The chain does not stop that.
- A load shows at most 100,000 pairs. If the site has more, all totals show as not known and the page says so.
- The deployment counts cover the kinds of deployment that the plan names.
- The page makes no network call except to the SMS Provider through the ConfigMgr session.
## Pivots and Scripts

The **Pivots and Scripts** page runs a CMPivot query or an approved ConfigMgr script on client devices and shows what each device returns. **A run cannot be undone.** The clients run the code. Read [Limits](#limits-1) before you use the page on production devices.

![The Pivot tab with a query, a target and the results of a run](docs/images/apd14-ps-pivot.png)

- **Targets:** a collection (ID or exact name), one device, a name pattern with `*`, or a list that you type or import from a TXT or CSV file. **Resolve** shows the number of devices, the names that match nothing and a warning when the target is a built-in collection. A name with a line break or a control character is not imported, and the status line names it.
- **Pivot:** a query editor with a starter list and a library of sample queries. In the editor, Tab types a tab. Press Esc and then Tab, or Ctrl+Tab, to leave the editor. **Run query** opens a plan.
- **Script:** pick a script that the site has approved. The page reads the parameter definitions of the script and shows them in the Parameters dialog. A value must fit the type of the parameter (text or whole number). **Run script** opens a plan.
- **Site scripts:** the scripts on the site with their state. **Check** tests the syntax. **Submit** creates a script and its parameter definitions. **Approve**, **Deny** and **Remove** each open a plan. The page never approves a script by itself, and the site setting that blocks approval by the author is not changed.
- **Results:** the tabs Results, Devices, Raw and Pipeline. A device that did not answer shows as No response or Timed out, never as a result. **Export CSV** and **Export JSON** write the rows that you see. **Copy as PowerShell** copies a snippet that works in Windows PowerShell 5.1 and in PowerShell 7. **Selection to Pivot** and **Selection to Script** use the selected devices as the next target.

![The Script tab with the approved scripts and their parameters](docs/images/apd14-ps-script.png)

![The results of a run on one device](docs/images/apd14-ps-results.png)

### Plan and checks

Each run shows a plan before anything is sent. The plan names the target and the number of devices. For a script it also shows the script text, the site hash and the SHA-256 of the text, and the parameter values. A script run needs two boxes: one for the script text and one for the number of devices. A target that reaches every device of the site, for example All Systems or a pattern `*`, needs a third box. The service enforces the boxes. A run without them is refused.

![The plan of a script run with the parameters and the confirmation boxes](docs/images/apd14-ps-plan-script.png)

- **The script is pinned:** the run goes to the planned hash. The client runs that text, or it returns no result. After the send, the page reads the script again. If the script changed during the send, the run is marked Partial and the message says so.
- **The targets are pinned:** every run goes to the fixed list of devices that the plan shows. A device that joins a collection after the check does not receive the code. If the collection has more members than the plan counted, the run is refused.
- **Size of a run:** a run takes at most 2,000 devices. A plan is valid for 30 minutes, and then you prepare it again.
- **Audit first:** the page writes a Started record to the audit log and saves the run before it sends. If the log cannot be written, nothing is sent. Each run ends with one Finished record. A run that the service refuses writes a Refused record.
- **Waiting:** the page reads the results for the wait time of **Options** (30 seconds to 60 minutes). **Stop waiting** ends the wait on the page only. **Wait again** reads the results again, also after you restart the app. If you close the app during a wait, the run continues on the clients, and the record says that the results were not read.

![The Site scripts tab](docs/images/apd14-ps-sitescripts.png)

### History and audit

**History** lists the saved runs. A saved run holds the targets, the query or the script identity, the parameter names and the raw responses. It does not hold parameter values or script text. The audit log is a hash chain, as for the other pages. History marks each record **Verified** or **Not verified**. The application log holds no script text, no query text and no parameter value.

![The History tab with saved runs and the audit records](docs/images/apd14-ps-history.png)

The page works in light and dark themes.

![The Script tab in the dark theme](docs/images/apd14-ps-dark.png)

### Limits

- A run cannot be undone. A script that you stop waiting for still runs on the clients that received it.
- A script that ends with `exit 3` reads as a success. ConfigMgr reports exit code 0 for it. Only a script that stops with an error reads as failed. Every script plan says this.
- The provider checks only that the script is approved at the moment of the call. If an administrator changes the text and approves it again before the call, the client still runs the planned text or returns no result. A change between the last read and the call is reported after the send and not prevented. An approval or a denial that races with an edit is reported as Partial, and the page does not undo it.
- The page needs the right to run CMPivot on the collection, or to run scripts. The site refuses the call otherwise, and the page shows the right that is missing. The client must have CMPivot and Run Scripts enabled in its client settings. The provider certificate must be trusted, and you must connect with the full name of the provider.
- The audit chain finds edits to single records. A process that runs under the same account can write a new chain without a break.
- The limit of 2,000 devices is a set limit and not a measured limit. The lab had four clients.
- The page makes no network call except to the SMS Provider.
## Site Hygiene

The **Site Hygiene** page scans a ConfigMgr site for clutter and drift, shows each finding with its evidence, and can fix a finding. It also provides live site views, trends, alerts, metrics history and dashboard import. **A fix deletes or changes objects on the site. Most deletes cannot be undone.** Read [Limits](#limits-2) before you fix anything on a production site.

![The Findings tab with the findings of a scan and the fix text of the selected finding](docs/images/apd15-sh-findings.png)

- **Scan:** the scan reads the site through the SMS Provider. It runs 57 checks in areas that you choose in **Options**, and you can cancel the scan. The scan changes nothing. A check that did not run, because the read failed, was cut or was not selected, never reports "no findings": the Summary names it and its reason.
- **Findings:** each finding has a check ID, a category, a severity (Error, Warning or Info), the object, the evidence and a recommendation. You can filter by text, category and severity. The **New** column marks the findings that the last stored scan of the same site and provider did not have. The first visit shows that stored scan, marked as stored, without a call to the site.
- **Suppress:** **Suppress** hides a finding with your comment. **Unsuppress** shows it again. A suppression holds the check ID, the object key, the site and the provider. **Show suppressed** lists the hidden findings.
- **Relationships:** **Read relationships** reads every supersedence and dependency between applications. **Inventory** lists the edges with their health. **Tree** shows the chains, marks cycles, and works from the keyboard.
- **Summary:** the counts by category and severity, and the status of every check. A count shows "At least N", or "Not known" for zero, when a check of that category and severity did not run.
- **History:** every fix that the page made, from its own audit log. See [History and audit](#history-and-audit-2).
- **Export CSV** and **Export HTML** write the rows that you see. The file names the filter, the suppressed findings that are hidden, and the checks that did not run. The CSV has the column row first, and the notes go to a separate `.about.txt` file, so `Import-Csv` reads it.

![The Summary tab with the counts by category and severity](docs/images/apd15-sh-summary.png)

![The Relationships tab with the supersedence and dependency edges](docs/images/apd15-sh-relationships.png)

### Fixes

A fix is a typed operation with exact object identifiers. The page never runs script text. The plan shows the equivalent PowerShell as read-only text that you can copy, and a test proves that this text is what the service runs.

- **Fix kinds:** the page can remove applications, application deployments, application revision history, packages, boot images, driver packages, collections, devices, auto deployment rules and maintenance windows. It can also run an auto deployment rule and change the refresh type of a collection, an application and a site maintenance task. A finding whose fix the page cannot prove safe shows **Report only** in the Fix column, with the reason. You then fix it yourself in the console. Run fix is disabled for it.
- **Plan:** **Run fix** opens a list of the objects, where you can untick objects. The plan shows the PowerShell text, the impact lines and the checks of the prepared change. A delete needs a second box that names the number of objects and says that the delete cannot be undone. The service enforces the box.
- **The finding must still hold:** the service reads the object again on fresh data when it prepares the plan, and again immediately before each write. If the finding no longer holds, for example an application that got a deployment since the scan, the object is skipped with the reason.
- **References are checked in the same command as the delete:** before a delete, the same site command reads what uses the object. A collection is refused when a deployment, an auto deployment rule, a client settings deployment, a collection rule or an administrative scope uses it. An application is refused when a task sequence uses it. A driver package is refused while a task sequence has an Auto Apply Drivers step. A device with a client heartbeat inside the inactivity window is never deleted.
- **Size of a run:** a run takes at most 100 deletes or 200 changes. A plan is valid for 15 minutes and can be used once. It is tied to the site and the provider: if the connection changes before you confirm, the run is refused.
- **Audit first:** the page writes a Started record before the first change. If the log cannot be written, nothing is changed. Each run ends with one Finished record. If that record cannot be written, the result says so and names the log.
- **Results:** each object gets its own result: Done, Unchanged, Failed or Skipped. Done means that the read-back showed the change. If the write ran but the read-back failed, the result is Failed and says so. **Cancel** finishes the object in progress and skips the rest.

![The plan of a fix with the PowerShell text, the notes and the confirmation boxes](docs/images/apd15-sh-plan-fix.png)

### History and audit

**History** lists the runs with the time, the action, the result, the object counts, the site, the provider and the user. A run is marked **Verified** or **Not verified** from the hash chain of the audit log. The detail shows the result of each object. History reads the local file and makes no call to the site.

![The History tab with the records of two fix runs](docs/images/apd15-sh-history.png)

The page works in light and dark themes.

![The Findings tab in the dark theme after a fix run](docs/images/apd15-sh-dark.png)

### Live views

The six Live views read current site data. Deployments, Content, Distribution Points and Site Health read through the SMS Provider. Client Health and Inactive Devices read the site database with Windows authentication and read-only access. Both database views use the shared **Discovery SQL Server** and **Discovery SQL Database** settings. When no SQL server is found, Options says **SQL is not set up**.

One refresh makes eight SMS Provider queries in four session calls and two SQL statements. The refresh timer starts after the first successful refresh. It supports intervals from 5 minutes to 24 hours, can be paused, stops when you leave the page, never overlaps refreshes, and backs off after repeated failures. Failed or cut reads show **Not known**, never zero.

![Deployments Live view](docs/images/apd16-sh2-deployments.png)

![Content Live view](docs/images/apd16-sh2-content.png)

![Distribution Points Live view](docs/images/apd16-sh2-dps.png)

![Client Health Live view](docs/images/apd16-sh2-clients.png)

![Site Health Live view](docs/images/apd16-sh2-sitehealth.png)

![Site Hygiene Options](docs/images/apd16-sh2-options.png)

![Live views in the dark theme](docs/images/apd16-sh2-dark.png)

### Trends

WPF shapes draw the chart, with a table of the same points below it. Missing points stay visible as gaps. The chart downsamples data above its point cap.

![Site Hygiene Trends](docs/images/apd16-sh2-trends.png)

### Alerts

Alerts cover critical site components or systems, critical distribution points, failed distribution point and content pairs, and deployments below a compliance floor. The first evaluation in a session sets a baseline and does not alert. An alert fires when a complete reading crosses into breach. It can fire again after a complete read shows recovery and a later reading crosses into breach. Alerts appear in the in-app list and banner and in `SiteHygiene\alerts.log`. Windows toast notifications are not used.

![Site Hygiene alerts](docs/images/apd16-sh2-alerts.png)

### Metrics history

The app stores a CSV at `SiteHygiene\metrics-history.csv` in the data folder. Each completed refresh adds one row. The file retains 180 days. A failed view writes empty cells. You can read the file with `Import-Csv`.

### Dashboard import

Import can read a retired Health Dashboard folder or the suite's own history and preferences. It shows a plan and requires one confirmation. It fills only empty settings and never moves or deletes the old files. A second import changes nothing. The importer refuses corrupt files and files without known columns.

![Site Hygiene dashboard import](docs/images/apd16-sh2-import.png)

### Limits

- A delete cannot be undone. Restore from a backup of the site if you removed the wrong object.
- A scan is a snapshot. The fix reads the object again, but the site can change between that read and the write. ConfigMgr has no conditional write.
- The site updates some flags late. An application still reads as deployed, and still counts a dependent task sequence, for some minutes after the last deployment or the task sequence is removed. The page then skips or refuses the delete and says why.
- The site does not list the task sequence steps of a boot image that is used only for media or PXE. A boot image plan says so.
- The task sequences read is capped. When it is cut, the checks that need it do not run.
- The content source probe (check APP-04) is off by default, because it reaches file servers. With it off, APP-04 is not selected.
- The audit chain finds edits to single records. A process that runs under the same account can write a new chain without a break.
- The Live views read the SMS Provider and, for two views, the site database. The content source probe also reaches file servers when it is switched on.
- The lab had one distribution point and four clients. Scale is covered by stub tests only.
- Alerts fire only while the app runs. Polling stops when you leave the page.
- The database views need read rights on the site database.
- The Content view was empty in the lab and was tested with fixtures.
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

![The Packages grid scrolled to the CVEs, Max CVSS, KEV, In latest and Newest CVE columns](docs/images/packages-cve.png)

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
