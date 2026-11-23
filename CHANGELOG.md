# Changelog

## [2026.1123.58] - 2026-11-23

### Added

- Export the settings of a site to one zip file with a SHA-256 for each entry.
- Import a site file on another computer into a site or as a new site.
- Keep every secret out of the file and refuse a file that fails a check.
- Read and write the same site file as the PowerShell app.

## [2026.1123.57] - 2026-11-23

### Added

- Import settings from another site: choose a source site, tick the settings, preview, then import.
- Keep the value of the active site on a conflict, or replace it per item with a verified backup.
- Never copy the site code, the records of the source site or any secret.
- Record each import in imported-from.json with the source, the date and the file hashes.

### Changed

- Use one glyph per action: Refresh, the export formats, the imports and the Packages menu.
- Show the glyph of the matching page action on the primary buttons of nine dialogs.
- Retook the guide images of the screens whose icons changed.

### Internal

- Add IconConsistencyTests, which fails when one action label uses two glyphs.

## [2026.1122.56] - 2026-11-22

### Added

- Add a site switcher to the footer: switch between sites, add a site by site code and SMS Provider, and refuse a switch while a run is active.
- Keep the settings and the data of each site apart: the site settings, the Check ConfigMgr result, the application inventory, ring plans and runs, Site Hygiene, DP Content, Pivots and Scripts runs, and the reports of Maintenance Windows and Collections.
- Ask "same site, other provider" or "different environment" when a site code is already in use.
- Copy the data of the saved site into its site folder at the first start, check each copy by SHA-256 and keep the old files.

### Changed

- Record the site with every row of the operation history.
- Change the Site Code box of Settings into a site switch.
- Retook 30 guide images of the tool pages from the real app on a lab client.
- Retook the Application Settings image so it shows the Installer type column.

### Internal

- Add SiteDataPaths, the paths and names of the per-site data layout, with shared fixtures pinned by SHA-256.

## [2026.1122.55] - 2026-11-22

### Added

- Installer type (EXE or MSI) per application, for an application whose vendor ships both: 7-Zip (MSI by default, EXE with /S), Notepad++ (EXE by default, MSI with /qn /norestart) and TeamViewer Host (MSI from Local Installer Sources by default; EXE downloaded from TeamViewer, installed with /S). Settings, Application Settings has the column Installer type (with the bulk bar), and the Workbench profile has the field Installer type (custom, inherited, reset). Each type carries its own download, silent switches and detection. TeamViewer documents silent install for the MSI only; the /S switch of the EXE is the NSIS silent switch and is not in the TeamViewer documentation.

## [2026.1121.54] - 2026-11-21

### Added

- Settings, Application Settings has the columns Free disk space (MB), Memory (MB) and OS build (minimum). Each writes one requirement operation of the Workbench profile of the application (an empty value removes it; a value that is not a whole number above zero is refused). The deployment type gets a requirement with the global condition Disk space (New-CMRequirementRuleFreeDiskSpaceValue, system drive, GreaterEquals), the global condition Total physical memory (GreaterEquals), or the new WQL condition APKG - Operating System Build on the version of the operating system (GreaterEquals, 10.0 and the build). Intune gets the three as the app properties minimumFreeDiskSpaceInMB, minimumMemoryInMB and minimumSupportedWindowsRelease (a build that is not the build of a release becomes a PowerShell requirement rule). A requirement that has no Intune form is named by the blocking finding, and its drop can be accepted as before.
- The condition templates have the fields Operator, ValueFormat and IntuneProperty, and the rule type FreeDiskSpace. A condition-templates.json that was written before these templates existed gets the built-in templates that it lacks.
- Check ConfigMgr writes CheckConfigMgrResult.csv and .json to the SRLPackr data folder after each run; Export Check Results saves it (CSV or JSON, all or only updates available). Deployment Helper's Update rollout reads it.
- Settings, Application Settings has the columns Distribution point group and Test collection. A package run reads the value of the application first and the setting of the Post Packaging page as the fallback. The column Detection shows whether the Workbench profile holds its own detection rule (read only).

### Changed

- The migration of the saved conditions into the Migrated profile keeps every requirement operation that it did not write. Before, it replaced the whole list.

## [2026.1121.53] - 2026-11-21

### Added

- Settings, Application Settings has the columns Estimated runtime, Maximum runtime and Reboot after install. They write the Workbench profile of the application (an empty value or Inherit removes the field; a value is a custom override). An application that uses another profile of your choice shows the cell greyed out and the tooltip names the profile.

## [2026.1120.52] - 2026-11-20

### Added

- Settings, Application Settings is one grid with the settings of each application (it replaces the supersedence grid and brings back the per-application view of the Deployment Conditions page): Package source (x64 only, ARM64 only, or both with one deployment type each, for an application that has an architecture choice), Install scope (System or User, for an installer that supports both), Supersedence, Version in title (Include version or No version, locked to Include version while supersedence is on) and Version rule (Minimum or Exact). Every column is the value that the package run reads: the keys Supersedence.Mode, Supersedence.TitleOverrides and DeploymentConditions. Stored values of earlier versions still show their choice.
- The grid also has the columns Network, Network split, Install command, Uninstall command, In One Click, Check interval and Publish to ConfigMgr, Intune and WSUS (the column chooser Columns shows them). A setting that does not apply to an application is greyed out with the reason in its tooltip. A bulk bar sets one column for the checked, the filtered (search box) or all applications, and None: reset all returns every application to its defaults. Check all, Check none and Refresh list manage the list.

### Changed

- The page Settings, Supersedence is now Settings, Application Settings. The buttons Follow the default, Off, Replace, Replace and uninstall, and None: Off for all are replaced by the bulk bar.
- Save Settings brings the Migrated Workbench profiles up to date with the saved conditions, so a package run in the same session reads the choices of the grid.

## [2026.1120.51] - 2026-11-20

### Changed

- The lab capture scripts select the sidebar entries by name, the capture script of the supersedence guide drives the grid in Settings, and the guide images that showed the old toolbar and sidebar are retaken.
- Settings, Supersedence names the grid as the place for the mode of an application (the text named the Supersedence button of the toolbar, which no longer exists).

## [2026.1120.50] - 2026-11-20

### Added

- Site Hygiene can fix APP-07: the fix Remove retired application rules removes the supersedence rules and dependencies that a retired application holds toward active applications. The site ignores a change of a retired application, so the fix reinstates the application, removes the rule and retires it again (it does not run for a retired application that has a deployment). The plan has the check box Also delete the old revisions this fix creates.
- Site Hygiene can fix APP-08: the fix Delete superseded application removes the supersedence rule of the replacement, then deletes the application when no deployment, dependency, task sequence or stale reference row refers to it. The delete needs the second confirmation. The check boxes of SUP-04 and DEP-04 work the same way.
- Site Hygiene has a Show blockers button. It opens a window for the application of the selected finding with every deployment, task sequence (also nested), stale reference row, dependency and supersedence rule that blocks the delete of the application. The list comes from the last scan and makes no site call.
- Site Hygiene can fix SUP-01: the fix Remove supersedence removes the rules that point at a deleted application, after the plan and the confirmation. The plan has the check box Also delete the old revisions this fix creates (on at the start), so that check APP-05 does not report the revision that the change makes. The cmdlets `Set-CMApplicationSupersedence -RemoveSupersedence` and `Remove-CMDeploymentTypeSupersedence` need the deleted application, so the fix edits the definition of the superseding application with the serializer of the console. It refuses when the serializer is not the one of the console, does not write when the edited document differs from the original in anything but the removed rules, reads the result back, and writes no file of the removed data.
- Supersedence is a setting of each application and is Off until you turn it on. The Supersedence button on the Packages page turns it on or off for the checked applications, for all applications or for none. With it on, the version is part of the application title and that title option is locked. A package run finds the older version by title and publisher (the query of Check ConfigMgr), and a window asks Yes, Skip or No before the supersedence is applied; Remember keeps the answer for the rest of the run. On the command line, --yes is Yes and no --yes is Skip.
- Settings, Supersedence has a grid of all applications with a Supersedence column (Follow the default, Off, Replace, Replace and uninstall) and the buttons for the checked applications, for all applications and for none. The changes are written on Save Settings.
- The App Packager toolbar is one row in this order, with a separator between the groups: One Click | Check Latest, Check ConfigMgr, Check CVEs | Stage, Publish All | Publish to ConfigMgr, Publish to Intune, Publish to WSUS | CVE Report, Ops History. The row shows the labels where they fit and the icons only in a narrower window. Publish All is new: it publishes the checked applications to every system that is on in Systems in Use, and it is off when none is on.
- Discover unmanaged software, the Supersedence button and WSUS Updates left the toolbar. Settings, ConfigMgr Site Settings has the buttons Discover not selected and Discover all; WSUS Updates is an entry of the sidebar, under Add from file; it opens the window that approves, declines, expires and removes the published updates. After the first-run setup the program asks if it must run Discover not selected to help you pick the applicable products.
- Supersedence also works for Intune. With it on for an application and Intune as target, a package run finds the older version in the tenant by display name and publisher, asks the same Yes, Skip or No before the publish, and after the publish creates the supersedence relation (type update for Replace, type replace for Replace and uninstall). A relation that fails leaves the app published and is reported as "Published, supersedence not applied". Graph v1.0 has no write for a relation, so this one call uses the beta endpoint, which can change without notice; every other Intune call stays on v1.0. Not proven against a tenant.
- Add the definition Power BI Desktop (optimized for Power BI Report Server). It is the application that report authors use to create reports for a Power BI Report Server, and it installs next to Power BI Desktop. The version comes from the change log of Power BI Report Server on Microsoft Learn and the installer from the Download Center page of Power BI Report Server. Staging stops when the installer reports another version than the change log, and the download needs a valid Microsoft signature. NVD has no CPE for this edition, so the CVE columns show CPE missing.
- A definition can carry extra files for an MSIX (`ExtraFiles`): a ZIP of dependency packages and a license file. Stage downloads each file, checks its SHA-256, extracts the dependency packages of the named architecture folders, and the install wrapper provisions the package with the dependencies of the device processor and the license. A definition can also carry `StageNotes`, lines that the stage log shows. An imported feed cannot carry extra files.
- Add the App Installer (WinGet) definition (bundle, dependency packages and license of the winget-cli release, all checked against the GitHub digests) and the PowerShell 7 (MSIX) definition (the Microsoft msixbundle; its stage notes name the limits of the MSIX package: single user, no remoting, no all-users scope). Both are in parity with the PowerShell app.
- Settings, Systems in Use: the check boxes for ConfigMgr, Intune and WSUS now set the stored deployment target. Every combination has a name, for example IntuneAndWSUS. No box checked means nothing is published.
- The command line follows the saved Systems in Use when you give no --target. A target that is empty or not valid never means ConfigMgr.
- After Check Latest and Check ConfigMgr the log gets one line with the count of the products that need an update, for example "Check ConfigMgr finished: 2 of 315 products need an update; 2 selected." A product whose check failed is named in the line. A cancelled check writes no such line.

### Changed

- The Detection Tester and the Discover texts say Select Run and devices, as the ConfigMgr lexicon asks.
- Guide images: the images of the App Packager page, its dialogs and windows, the sidebar context menu and the Settings pages show the current toolbar and the current Settings tree (lab run on CLIENT01, 1920 x 1032, light theme; the dark theme was captured too). The lab capture scripts `Capture-PackagesPage.ps1`, `Capture-PackagesDialogs.ps1` and `Capture-WindowChecks.ps1` are new. The captions of the Settings images name the page that they show.
- The version rule (Minimum or Exact) applies to the detection of the application that is packaged only. The older applications are never changed.
- Publishing into an existing application is unchanged.
- TeamViewer Host installs the MSI instead of the EXE. TeamViewer gives the MSI only in the Management Console, so you put it in the folder of the application under Settings, Local Installer Sources (file `TeamViewer_Host*.msi`). The version is the ProductVersion of that file, and detection reads the Add/Remove Programs entry. The settings API token (APITOKEN), Custom config ID (CUSTOMCONFIGID), Assignment ID (ASSIGNMENTID), Assignment options (ASSIGNMENTOPTIONS) and Remove the desktop shortcut (DESKTOPSHORTCUTS=0) are MSI properties on the install command line; the post-install assignment step is gone.
- TortoiseSVN takes its x64 MSI from the folder under Settings, Local Installer Sources (file `TortoiseSVN-*-x64-*.msi`). The vendor site now blocks scripted requests with a bot protection, and the application does not try to get around it. The version is the ProductVersion of the file (three parts); an unsigned file is staged and the log says so.
- Check Latest for these two applications reports the version of the MSI in the folder. With no folder or no matching file it fails with a message that names the folder and the file to put there. A definition field `LocalSource.VersionFromFile` and the scraper type `LocalFile` mark such a definition; `LocalSource.Note` and `LocalSource.RequireSignature` are new.
- Tableau Desktop, Reader and Prep Builder download from the esdalt path first and try the tssoftware path when the first one fails for any reason: a refused or missing file (403, 404, another HTTP error), a connection failure, or a body that is no installer (definition field `FallbackDownloadUrl`). The log says which address was used and why the first was skipped; the stage fails only when both fail, and the message lists both reasons. Prep uses `esdalt/tableau_prep/<release>/TableauPrep-<release with hyphens>.exe`.
- Removed the definition `powershell7lts.json`: it followed PowerShell 7.4, whose support ended on 10 Nov 2026 (Microsoft Learn, PowerShell Support Lifecycle). `PowerShell 7 (x64)` (`powershell7.json`) is the LTS MSI now: it takes the newest release of the 7.6 LTS line that has the MSI and never another line, installs with USE_MU=1 and ENABLE_MU=1 so that devices update through Microsoft Update, requires a valid signature, and recognises a PowerShell 7 MSI install of any line in Discover. A setting that named the removed definition moves to `PowerShell 7 (x64)` when the settings load, and the settings notice says so; a command-line request for `powershell7lts` stops with "This definition was removed: PowerShell 7.4 is out of support. Use PowerShell 7 LTS." The PowerShell 7 definitions name their release channel in the stage notes, and the MSIX and Devolutions definitions in their names (PowerShell 7 Stable (MSIX, per user); PowerShell 7 (Devolutions, third party)).
- The MSIX uninstall wrapper no longer fails when the deprovision is refused or a user copy cannot be removed. It tries both steps and judges the result by the provisioning entry that is left. A package that a user updated from the Store (App Installer on a server) refuses the deprovision with 0x80073CFA, which made the uninstall fail although the removal for all users took the entry away. A lookup of the provisioning entry that itself fails still fails the uninstall.

### Removed

- The buttons Apply supersedence, Rewrite old versions and Restore Old Versions, the context menu item Use as old application and its window, the option --rewrite-old-versions, the supersedence-only pass of One Click, and the identity marker that a ConfigMgr publish wrote into the keywords. A marker that an earlier release wrote stays on the application; the program neither reads nor changes it. Applications that an earlier rewrite changed stay as they are.

### Fixed

- Fix the Site Hygiene check SUP-01 (a supersedence that points at a deleted application). The site removes the relation row of a supersedence when the superseded application is deleted, so the page found nothing. The page now reads the supersedence rules from the application definitions.
- Supersedence: the older version is never one of another architecture ((x86) against (x64)), and a retired or unreadable highest older version now means "no older version" instead of the next lower version. The vendor compare ignores case, punctuation and closing legal suffixes (Bitwarden Inc and Bitwarden, Inc. are the same vendor) and an application without a vendor no longer matches a named vendor, as in the PowerShell app. A Skip or Fail run no longer asks about the supersedence of an application that the site already holds. A confirmation window that fails counts as Skip, with one log line, and the package goes on. Settings, Supersedence tells you after Save when Replace and uninstall is set while the ConfigMgr version rule is Minimum (every package run refuses that combination). The version lock of the title in the Workbench follows a change of the setting made while the window is open.
- A download whose host name does not resolve now says so: "the vendor download host does not resolve", with the host name. The cause is the vendor (an outage or a removed download host), for example the Jabra Direct download host. Other download failures keep the general text.
- The red Cancel button of the App Packager toolbar has the same height, line and spacing as the other buttons while a run is active, also when the toolbar wraps. The toolbar shows its labels while they fit in four lines, so the grid keeps its rows.
- Settings: the list of pages no longer needs a horizontal scroll bar. The list is 250 px wide at the start, and a handle between the list and the page changes its width.
- Fix the icon pack update that failed with a file in use error while the Packages page showed the icons. The page no longer keeps an icon file open, and it shows the new icons after the update without a restart.
- A cancelled run names itself and says nothing about finishing. Check ConfigMgr, Check Latest and Check CVEs log "Check ConfigMgr cancelled: N of M products checked." (and the same for the other two), and One Click logs "One Click cancelled: N of M products done." The wording is the wording of the PowerShell app.

## [2026.1118.49] - 2026-11-18

### Added

- Add the targets IntuneAndWSUS and ConfigMgrAndIntuneAndWSUS. Now every combination of ConfigMgr, Intune and WSUS has a name in Settings and in the command line (`--target`). A run publishes only to the destinations of its target.
- Add the Bruno and PowerShell 7 (Devolutions) definitions. Bruno installs the x64 MSI and checks the file against the SHA-256 digest that GitHub lists. PowerShell 7 (Devolutions) installs the Microsoft-payload MSI that Devolutions publishes, requires a valid signature and a digest, and detects on the file version of `pwsh.exe`.

### Changed

- The ring plan editor shows the full label of the check box for the deployment option settings on one line. The guide images of the ConfigMgr, Intune and Citrix Workspace App pages of Settings and of the ring plan editor show the current window text.
- The guide, the window text and the command line help now say that ConfigMgr, Intune and WSUS are independent and each is optional. Publishing to Intune or WSUS needs no ConfigMgr site, console or file share.
- Every run can be cancelled. The Cancel button shows while a check, stage, package, publish, CVE or One Click run is active. It stops the download or child process that is running within seconds, keeps the products that finished and your selection, and shows "Cancelled: N of M products done." in the status line and the log. A second click does nothing, and closing the window also stops the running child processes.
- Space NVD requests 6 seconds apart without an API key, so the lookup stays within the NVD limit.
- Share the NVD request limit between the app, the command line and the PowerShell App Packager.
- Wait for the time in the NVD Retry-After header, up to 120 seconds, before a retry.
- Try an NVD timeout or network error again, like a 429 or 503 answer.
- Show a product as not checked, never clean, when NVD does not answer.
- End a Check CVEs run with one line that counts the products that were not checked.
- Show the wait for the NVD request limit and the lookups left in the status line.
- The guide now uses Configuration Manager terms and Microsoft Learn style. For example, it says View and Review instead of Read.
- Window text and messages use Configuration Manager terms: for example device, software update group and SMS Provider.
- The deployment target is now named ConfigMgr, ConfigMgrAndIntune or ConfigMgrAndWSUS in Settings, the command line and the guide. The earlier names still work.
- The collection query templates show the category ConfigMgr Infrastructure instead of SCCM Infrastructure.

### Fixed

- A deployment target that is empty or not valid no longer means ConfigMgr. An empty value takes your saved default destinations, or ConfigMgr with a notice if you saved none. A value that is not valid publishes to no destination, and the log lists the valid names.

## [2026.1118.48] - 2026-11-18

### Added

- Add Use as old application to the grid menu. It links an older version.
- Add a supersedence pass to One Click that retries a failed supersedence step.

### Fixed

- Fix the App Packager page failing to open on a PC without 7-Zip.
- Remove the ConfigMgr cmdlet wildcard warning from the Apply supersedence log.
- Show Same title, not Not older, as the state of a duplicate title in Old Application.
- Old Application window: applications with the same title are listed with the reason and cannot be linked.
- Settings, Import from App Packager (PowerShell): the Intune keys now have a row each with a reason. Publish to Intune is derived from the deployment target (an older file with only the toggle writes MECMAndIntune), the protected client secret is skipped as a DPAPI secret, and the IntuneWinAppUtil path of the other computer is skipped because APD downloads its own copy.
- Settings, Import from App Packager (PowerShell): the SQL server, SQL instance and SQL port of the PowerShell file become one SQL server name in APD (server\instance,port).
- Settings, Import from App Packager (PowerShell): the minimum Windows release and the default cadence days of the PowerShell file are imported.
- Settings, Import from App Packager (PowerShell): the architecture tag, the update check at startup and the user test collection of the PowerShell file are imported.
- Settings, Import from App Packager (PowerShell): the supersedence mode, the two version rules and the review switch are imported.

## [2026.1116.47] - 2026-11-16

### Added

- Settings, Folders: a staging folder that other accounts can write to gets a warning when you set it. The warning names the folder and the accounts and says staging is blocked for security. The stage result and the run status show the same text. Create a safe folder makes the folder in your data folder. Allow this folder anyway asks you to accept the risk for that folder; the allowance is stored with the date and your Windows user and ends when the folder or its access list changes.
- Settings, General: Import from App Packager (PowerShell). It reads AppPackager.preferences.json, shows a preview (differs, same, skipped, unknown) and writes only after you accept. Credentials are never imported and the PowerShell file is not changed.

### Changed

- Icon pack: pack 0012 gives an icon to every packager. A test fails when a shipped packager has none.

- Settings: the page is a tree of Title Case pages (General, Connections, Packaging, Applications, Updates and CVEs, Security, Tools, About), one compact page for each group, in the order of the PowerShell Options window. Each page fits one screen at 1920 x 1032. Field titles are bold, the headings are smaller, and every setting keeps its place.

## [2026.1116.46] - 2026-11-16

### Changed

- Packages grid: each row shows the icon of the icon pack, or a gray box when the pack has none. The tooltip names the icon.
- Icon pack: the app downloads the icon pack by itself when none is installed, and installs a newer pack release at startup. A test lists the definitions that the pack has no icon for.

### Fixed

- Titles: a name that already has the architecture, for example 7-Zip (x64), no longer gets a second (x64).
- Titles: a split run of 7-Zip (x64 and ARM64) is named 7-Zip. A single run keeps the installer name, as the PowerShell app does.

### Added

- Settings: Add the architecture to application names. Clear it to keep names without (x64), (x86) or (ARM64).

## [2026.1116.45] - 2026-11-16

### Changed

- Buttons: every button is one compact size, 32 px high.
- Toolbars: the buttons of a group wrap, so nothing clips at the right edge.
- App Packager toolbar: it shows the labels, and shows only icons when the labels do not fit.
- Icon buttons: all icon-only buttons are 32 px squares with a tooltip.
- Grids and lists: rows are 28 to 32 px high, so ten rows fit in a 1920 x 1032 window.
- Settings: single-line inputs are 32 px high, with widths that fit the type of field.
- Settings: the page title and its icon are 14 pt.
- Tabs: the selected tab is not bold. The accent underline marks it.

### Added

- Import From URL: unblock one definition that has scripts or commands, after you read the full text and SHA-256.
- Import From URL: Check for Update on a stored feed shows a diff. Nothing is written until you confirm.
- Import From URL: authenticated feeds with a key in a header, or with Basic. The secret is in Windows Credential Manager.
- Import From URL: more access methods for a feed: Windows integrated authentication, a client certificate, an OAuth 2 token and a signed expiring address.
- Import From URL: a credential for version checks and checksum files on the bound host.
- Import From URL: an imported definition runs with no ambient credential, and curl accepts HTTPS only.
- Import From URL: downloads from an authenticated host use curl. The credential goes only to the bound host.
- Guide images: a capture run saves the pages of the app in both themes at 1920 x 1032. A test checks that every guide image has a capture step.
- Guide images: the SettingsScroll scenario retakes the 11 Settings images one screen at a time. The LivePages scenario retakes 10 pages that read the lab site when they open.

## [2026.1115.44] - 2026-11-15

### Notes

- The Intune changes of the last releases are tested against a fake Graph service only. They are not verified against a tenant.
- Nothing of this release ran on a machine of the lab yet: the lab checks of Import From URL (unsigned and signed documents), of Rewrite old versions and Restore Old Versions, of the new installer options and of the new footer and text weights are listed in the test documentation and are not run. The screenshots of the guide are not retaken for the new footer, the bold buttons and the Settings headings.
- A package that you staged before this release keeps its old install.xml (Microsoft 365) and its old install arguments until you stage it again.

### Added

- Settings, GitHub API: a **GitHub token** box. The token is stored encrypted for the current Windows user (never as plain text in `settings.json`) and is used by the version checks and the command-line build for `api.github.com` after the two environment variables (`APP_PACKAGER_GITHUB_TOKEN`, `GITHUB_TOKEN`) and before `gh auth login`. An empty box keeps the saved token; **Remove the saved token on Save** deletes it. The status row names the source ("Settings").
- Main window: a footer, lower right, shows the site code and the site server: "Site P01", a middle dot and "primary.contoso.test"; "Site not set" when both are empty; "Site not set", a middle dot and the server without a site code; "Site P01", a middle dot and "server not set" without a server. It has one font (Segoe UI), size (12, the smallest size of the type scale of this app) and style in both themes (only the color follows the theme), as in every ConfigMgr tool of the suite. It follows a save in Options or in the Setup window at once, and shows the site that the app uses, launcher-supplied values included. The site code and the site server are edited in Options and in the Setup window only; a test fails on an editable site field in any other window.
- Citrix Workspace (Settings, install parameters): `/forceinstall`, a fixed client name (`ENABLE_DYNAMIC_CLIENT_NAME=No` and `CLIENT_NAME`), `/SkipSRAgentUpgrade` with Session recording (Current Release), `AutoUpdateStream` and `AURolloutPriority` (only with `AutoUpdateCheck`), the HDX switches `ALLOW_BIDIRCONTENTREDIRECTION`, `FORCE_LAA` and `ALLOW_CLIENTHOSTEDAPPSURL`, `LEGACYFTAICONS`, `DisableSetting`, and the component `CitrixEnterpriseBrowser` for LTSR. Each is written only when set, so a package with none of them has the install command that it had; a value outside the documented set is skipped with a warning, as for the other switches. The Preview shows each result.
- Microsoft 365: the install options that the code wrote as fixed lines are settings of the six definitions now. Languages (ODT language IDs separated by commas; the pattern is checked; before, only en-us), Let ConfigMgr manage Office updates (`OfficeMgmtCOM`), Office updates (off writes `Updates Enabled=FALSE`), Close running Office apps (`FORCEAPPSHUTDOWN`), Remove installed Office MSI versions (`RemoveMSI`) and Device-based licensing. The defaults are the values of the fixed lines, so a package that you stage without changing them has the same install.xml (with `DeviceBasedLicensing` 0). An imported configuration.xml is not offered yet.
- Installer options that the audit of installer options listed as missing, all off or empty by default (the install command is unchanged until you choose one):
  - Adobe Acrobat Reader: Accept the license agreement (`EULA_ACCEPT=YES`), No desktop shortcut (`DISABLEDESKTOPSHORTCUT=1`), Block the Chrome extension (`ENABLE_CHROMEEXT=0`) and No browser plug-in (`DISABLE_BROWSER_INTEGRATION=YES`). `UPDATE_MODE` is not offered: the meaning of its values is not verified.
  - Beyond Compare 5: Turn off remote profiles (`/DisableRemoteProfiles`) and Do not save passwords (`/DisableSavedPasswords`), version 5.2 and later.
  - SQL Server Management Studio: one product language (`--addProductLang`, a code such as `de-de`; another text stops the stage) and Delete the package cache after the install (`--nocache`).
- A setting of a definition can have a **ValuePattern**, a regular expression that its value must match; a value that does not match stops the stage with a message. See the definition format.
- Packages page: the button **Restore Old Versions** restores the earlier rewrites of the checked application's line from the log on its own, with its plan and confirmation, beside Rewrite old versions.
- Import From URL: a document can be signed. Enter a **pinned publisher** (the subject and the issuer of the code-signing certificate separated by `|`, or `key:` and the SHA-256 of its public key; not the thumbprint, so a renewal of the certificate does not break the pin). The program fetches the detached signature (`.p7s` at the address of the document with `.p7s` added, on the same host) and checks it for the exact bytes, the code-signing usage of the certificate, its chain to a trusted root with the online revocation check of Windows, and the signer against the pin. The state is then **Signed by pinned publisher** and needs no confirmation of the state "not verified". An expired certificate is accepted only with a checked RFC 3161 time stamp (its token belongs to this signature, its authority is trusted and has the time-stamping usage); any other time stamp is ignored. A failure stops the import and never falls back to "not verified". A signature does not unblock code in a definition.

### Changed

- Settings: every text box, password box and drop-down list has one minimum height (40 px) from one style of the page, and the two theme choices read "Dark Theme" and "Light Theme", as the theme toggle of the suite does. A test checks that no input of the page sets its own height.
- Text weight (the rule of the suite): the text of every button is bold in both themes (one style for the app, `ButtonText.xaml`, and every page style of a button is based on it). Bold elsewhere is for the title of a field only: the headings of the pages, cards and Settings, the summaries, the results, the status texts and the values are no longer bold (also the sidebar group captions), and the field labels of Settings and the parameter names of the Pivots dialog are bold. The link buttons (the CVE counts and links) are bold too. A test fails when a button turns its text normal, when a button style has no bold weight and no BasedOn, and when a text or a text style other than a field title is bold.
- Settings: the section **After Package** is **Post Packaging**. The headings are in Title Case (the rule of the suite: each word starts with a capital letter, except and, or, in, of, to, the, a, for, on, at and with): Systems in Use, CVE Lookup, Signing Certificate, Require Valid Signatures, Signing Identity, Updates, Telemetry and Store Policy, HDX Features and Display; the dashes of "M365 Apps - Channel" and "Citrix Workspace App - Install Parameters" are hyphens. A test checks every heading of the page.

### Fixed

- Rewrite old versions: **Undo**, the restore of the window and `--rewrite-old-versions restore` restore only the rewrites of the line of the checked application. Before, they took every entry of the log, so a restore for one line also tried the clauses of other lines and reported them as failures. The log entries now carry the line (the identity marker); an entry from before has none and stays open to every line (it is left, without a failure, when its application is not in this line).
- Rewrite old versions: the plan is read again after the confirmation and before the first write. When the clauses to rewrite are not the ones that were shown (the site changed while the window was open), nothing is written and every row says so.

## [2026.1115.43] - 2026-11-15

### Notes

- The Intune changes of the last releases are tested against a fake Graph service only. They are not verified against a tenant.
- Import From URL is tested with a fake HTTP handler and rendered in both themes. Its lab check (a feed from the lab web server, pinned and not pinned, and one staged imported definition on CLIENT01) is listed in the test documentation and is not run yet. Only the anonymous import is built: signed documents, the unblock of a script or a command, the update of a feed and authenticated feeds are later steps.
- The buttons Apply supersedence and Rewrite old versions and the command line recorder of the last release still have no lab check on the test site (CM01).


### Fixed

- Microsoft 365 install.xml: `MigrateArch="TRUE"` is no longer written for every package (with it, an x86 package changed an installed 64-bit Office to 32-bit), and shared computer activation is no longer forced on (`SharedComputerLicensing` is 1 only for shared computers such as RDS and VDI). Both are now settings of the six Microsoft 365 definitions, **Migrate architecture** and **Shared computer activation**, off by default. A package that you staged before keeps its old install.xml until you stage it again.
- Microsoft 365: the ExcludeApp ID `Bing` is gone (it is not a documented ID and had no effect), with its toggle.
- SQL Server Management Studio: `--includeRecommended` and `--includeOptional` act on added workloads, and the definition passed none. The definition now has a toggle for each of the five workloads (`--add <ID>`), and the two include switches are passed only while a workload is on (the new `RequiresAnyOf` of a setting). A saved IncludeRecommended or IncludeOptional with no workload no longer passes the switch; the audit notes that --includeRecommended also sets "add recommended components on update", so that side effect is gone for such a saved choice.
- Citrix Workspace: the tooltip of startAppProtection said that it installs App Protection. Since version 2212 the installer always installs it, and the switch starts it.

### Added

- Packages page: the button **Import From URL** opens a window that imports the definitions of a vendor feed: the HTTPS address, an optional pinned SHA-256, Fetch (and Cancel Fetch), a full preview (source, trust state, every definition with its hosts, payload hash source, status and full text, and the findings), the vendor name and the Feed Id, and the confirmation of the state "Definition document NOT verified". A definition that carries code is blocked. A shipped Id is skipped unless you choose to replace it. The list **Imported feeds** has **Remove Feed**. Fetch and Cancel write nothing. A definition with another Id but the stage folder of another definition (Application and Architecture) is skipped, so that it cannot share a stage with it. See [Import vendor definitions](docs/core/import-vendor-definitions.md).
- Imported definitions load: the new origin **Imported** (`imported:<Id>`) is read by all three loaders (the catalog loader, the Workbench discovery and the command-line build). An imported definition sits between the shipped and the user ones: a user definition with the same Id wins, a shipped Id is replaced only when the import was confirmed for that Id, and of two feeds with the same Id the earlier import keeps it. The Packages grid shows a column **Source** (only while a row is imported) with "Imported: vendor (Definition document NOT verified)" from the feed folder and its record, never from the definition file. An imported definition runs under its own identity (`imported:<Id>`), so it never takes the profile, the legacy preferences or the WSUS and Intune identity of a shipped application that it replaced; a file whose Id inside differs from its name is dropped by every loader; Ids with dots stay separate applications. At start (and before a command-line build) the program repairs a feed swap that stopped between its two renames, and one folder that cannot be repaired does not stop the others.

### Changed

- A stage whose manifest carries the identity of another application is refused at publish (an imported definition can have the Application of a shipped one); an older stage without an identity is accepted. The bootstrapper-layout and the ODT stage check the version folder as the main stage does.
- The folder of a stage is built by one helper (`StageFolder`) at every call site (the stage, the publish, the build of a package, the Workbench picker). A name that comes from a definition and would leave the download root (a dot segment, a rooted path, a drive colon, a device name) becomes a plain name, and the result is checked to stay under the root. A name that is safe keeps the folder that it always had, so existing stages are found: a test checks every shipped definition. This closes the way for definitions from a vendor feed, which are not trusted. The installer file name made from the application name has no separator either.

## [2026.1114.42] - 2026-11-14

### Notes

- The Intune changes of this and the last release are tested against a fake Graph service only. They are not verified against a tenant.
- The buttons Apply supersedence and Rewrite old versions and the command line recorder are tested without a ConfigMgr site. Their lab check on the test site (CM01) is listed in the test documentation and is not run yet.

### Added

- Command line: `--record-installed-value <value> --version <version>` records the version that the installed product shows, and `--accept-exact-block --version <version>` accepts the block of the Exact guard. Both write the settings file and nothing else. A recorded value lets `--rewrite-old-versions` and the Exact guard take effect without editing the settings file by hand.
- Packages page: the button **Apply supersedence** runs the lookup and the supersedence step for each checked application, with its stage and without a new publish. It needs a ConfigMgr site and the mode in Options (or the override of the title); the application needs the identity marker. The step reads the existing relations first, so a repeat adds only the missing ones. A success also replaces the state "Published, supersedence not applied" in the inventory.
- Packages page: the button **Rewrite old versions** shows the plan of the clauses that the rewrite would change for the checked application, with the blocked and left clauses and the reason of each, and asks for the confirmation before it writes. Undo restores the old operator from the log `supersedence-rewrite.jsonl`. It is the same plan, runner and log as `--rewrite-old-versions`.

### Changed

- Internal: every PowerShell script of the repository and every text file that a script or a test reads is plain ASCII (the box-drawing lines of `tools/RemoteTestHarness/RemoteTestHarness.ps1` are `=` now; the 26 definition files and 5 fixture files lost their UTF-8 byte-order mark; the Unicode characters of one plan-digest fixture are `XXXX` escapes with the same value). A test fails when such a file holds a byte-order mark or a character above 127.
- Internal: two timing tests depend much less on the speed of the machine. `SettingsDurabilityTests` makes the settings file appear at a chosen read attempt (a seam, `SettingsService.ReadAttemptHook`) instead of after a sleep, and `SiteHygieneLiveTests.ATickWhileARefreshRunsStartsNoSecondRefresh` waits for the timer to report the tick (a seam, `TickHandled`) before it releases the refresh. The cause of the second one: the test waited 100 ms for the timer to look at the running refresh, and on a slow runner the refresh ended first, so the tick rightly started a new one.
- WQL Editor (Collection and Compliance): the rule box and the rule name box have one height (40 px); before, the combo box was 1 px higher than the text box. A layout test now measures the tab in both themes at two window sizes (label gap, block gaps, box alignment, button rows, query box size, no cut-off button).

- Internal: the engine of the import of vendor definitions from a URL (fetch, strict validation, the block of code, the pinned digest, the preview rules and the storage of a third definition folder). The program does not import a feed yet; the window and the command come in the next changes.

## [2026.1111.41] - 2026-11-12

### Notes

- This release carries the publish-review work of 2026-11-11 and 2026-11-12. The Intune changes in it are tested against a fake Graph service only. They are not verified against a tenant.
- The two Visual C++ runtime definitions set a forced restart, so the first new version that is published after this release restarts the devices (Intune now matches ConfigMgr).

### Added

- Command line: `--rewrite-old-versions plan|apply|restore` rewrites the version clause of the old applications of a product line from `GreaterEquals` to `IsEquals` (and back), so the uninstall through supersedence is reported as successful. `plan` only reads the site. `apply` and `restore` need `--yes`. An application with a required deployment is blocked, and a clause is rewritten only when a recorded installed value of that version equals it (an administrator adds the value to the settings file; no recorder exists yet). The log `supersedence-rewrite.jsonl` is the way back.
- Supersedence chain check: before a publish with the supersedence mode on, the program reads the detection of the older application of the line and writes warnings to the log when the uninstall through supersedence would be reported as failed or the chain is inconsistent (Exact against Minimum). It never blocks.
- Packagers for `.NET 9 Desktop Runtime (x86)`, `.NET 11 Desktop Runtime (x64)`, `ASP.NET 9 Server Hosting Bundle` and `ASP.NET 11 Server Hosting Bundle`. .NET 11.0.0 is a stable release, so no preview channel is used. .NET 9 reached the end of support on 2026-11-10, so the two .NET 9 packagers carry its last release. The catalog has 323 definitions. The Intune publish of these packagers is not verified against a tenant.
- Supersedence: with the mode `Replace` or `Replace and uninstall`, a ConfigMgr publish sets the supersedence relation from the new application to the older application of the same product line. The older application is found by the identity marker, with a lower version and another title; the deployment types are paired one to one or by name suffix. `Replace and uninstall` also sets the uninstall option. The older application is not changed. A step that fails does not fail the run: the result is the separate state `Published, supersedence not applied` with the reason. The Intune side has no supersedence.
- Settings, Supersedence: the version rule for ConfigMgr and for Intune (`Minimum` or `Exact`) and the supersedence mode (`Off`, `Replace` or `Replace and uninstall`), and the command line options `--version-rule` and `--supersedence`. `Exact` writes `IsEquals` to the version clause of the detection, only when a value read from the installed product equals the expected value. `Replace and uninstall` with `Minimum` is refused before anything is created. The shipped values are `Minimum` and `Off`, so the published detection does not change. This release writes no supersedence relation.
- A ConfigMgr publish writes an identity marker into the keywords of a new or overwritten application that has no keywords of its own. A later release uses it to find the older versions of a product line. The program never rewrites a keyword that you set; when keywords exist, the result carries a warning. The Software Center shows keywords to users, so the marker is visible there.
- At the start of a publish, the Packages page writes what the publish does and does not do for each target to the log (ConfigMgr, Intune and WSUS buttons), and the Intune review window shows it when it opens. ConfigMgr does not supersede, Intune does not create or change assignments, WSUS makes no first install and approves only for the group in Settings.
- The Intune review window has the button **Accept dropped requirements and continue**. It stores the accepted drop of each requirement that has no Intune rule in `Intune.AcceptedRequirementDrops`, so no one edits the settings file by hand.

### Changed

- A ConfigMgr publish starts one Windows PowerShell process for the content distribution and the test deployment, not two. The ConfigMgr module loads once for both. The messages of a failed step are the same as before.

### Fixed

- The packager `PowerShell 7 (x64)` stages again. PowerShell 7.7.0 ships no MSI for Windows, so the address that the packager built from the latest release answered 404. The packager now takes the highest release that carries the x64 MSI (7.6.10 today).
- An Intune update keeps the description of the existing app when the run has no comment. Before, it wrote the name of the application over a description that an administrator typed in the console. A ConfigMgr replace already kept it.
- The program no longer builds a `.intunewin` file because the Content Prep Tool is on the computer. A stage and a ConfigMgr-only run build no bundle and copy nothing besides the content to the file share. Only an Intune publish builds the bundle.
- The install return codes, the reboot policy and the working directories that a Workbench profile stores now take effect. The return codes are added to the Intune return code table and to the exit codes of the WSUS update; the reboot policy sets the reboot behavior of the ConfigMgr deployment type and the restart behavior of the Intune app; the working directories reach the ConfigMgr deployment type. A mistake in the list stops the stage with the entry named. The uninstall return codes and the uninstall reboot policy stay recorded only, and the Workbench says so.

## [2026.1111.40] - 2026-11-12

### Fixed

- A dropped installer or a bring-your-own installer that is published from its stage now follows the title settings (**Include version in application name**, **Remove version from application name**), as a definition does. Before, its ConfigMgr application and its Intune display name always carried the name of the stage. A dropped installer that was published earlier under the name of its stage is published as a new ConfigMgr application and a new Intune app when a title setting changes its name; the earlier ones are not renamed or removed.

## [2026.1111.39] - 2026-11-11

### Fixed

- A ConfigMgr publish that created the application but could not apply its icon (the site refused it, or the file was not in the content folder) now records that as a warning in the history of the run. Before, the run read as a plain success. The run still counts as a success.

## [2026.1111.38] - 2026-11-11

### Fixed

- A test of the metrics history no longer fails at random on a loaded disk. It was a timing fault of the test: a store that waits more than 5 seconds for the lock file does not record that refresh, by design. The store now takes an optional lock wait (the default stays 5 seconds), so that the test can wait longer.

## [2026.1111.37] - 2026-11-11

### Removed

- The native MSIX deployment type of ConfigMgr is no longer created. It failed on a content path on a network share and did not honor the behavior for an existing application. An MSIX is published as a script deployment type, as every other product is (the stage already wraps it and gives it a detection). A stage from an older release that asks for the native type is refused with the message to stage the application again.

## [2026.1111.36] - 2026-11-11

### Fixed

- The Intune publish of an app that was converted from ConfigMgr now sends all four hardware minimums of the Graph resource (memory, processors, CPU speed, free disk space). Before, it sent only memory and processors. A minimum that the plan does not carry is sent as null, so a repeat publish clears an old one, also one that was set by hand in the Intune admin center. The importer still derives only memory and processors from ConfigMgr rules.

## [2026.1111.35] - 2026-11-11

### Fixed

- The Intune publish no longer fails at the first throttled or busy answer of Microsoft Graph or of the storage service. It waits and sends the request again (up to 5 times, with the wait that the service asks for), asks for a new token when the token expires during a long upload, and renews the upload address each time the storage service refuses it as expired, so that a large upload is not lost at its last block. A request that creates something is not sent again after a failure that may have run it.

## [2026.1111.34] - 2026-11-11

### Changed

- A repeat publish to Intune of the same package no longer uploads the payload again. The notes of the app now carry a second line with the digest of the package; when the digest is the same and the app has content, only the app is updated. The first publish of an app that an earlier release created uploads once and records the digest.

## [2026.1111.33] - 2026-11-11

### Fixed

- The restart behavior of an Intune app now follows the post-execution behavior of the deployment type: no specific action suppresses a restart, the program may restart, and force restarts the device. Before, every app used the return codes, so a forced restart had no effect. The two Visual C++ runtime definitions set a forced restart, so they now restart devices on Intune as they do on ConfigMgr.

## [2026.1111.32] - 2026-11-11

### Fixed

- The requirement rules of an application now go to Intune as PowerShell requirement rules (CPU architecture, operating system language, VPN connected, and any condition of the same kind). Before, the Intune app had no gating and could install on devices that ConfigMgr skips. A rule that has no Intune form blocks the Intune publish, unless its drop is accepted for that application in the setting `Intune.AcceptedRequirementDrops`.

## [2026.1111.31] - 2026-11-11

### Added

- Settings has a new setting, **Minimum Windows release**, in the Intune section. Every Intune app that is published gets it. The default is Windows10_21H2, which every app got before.

## [2026.1111.30] - 2026-11-11

### Fixed

- A registry value detection with an integer property type now compares as an integer in the Intune rule, and a version property compares as a version also when it is one number. Before, both compared as text, so 10 was lower than 9. A value that is not an integer for an integer property is refused with the reason, and a registry value detection on existence checks that the value exists.

## [2026.1111.29] - 2026-11-11

### Fixed

- A file detection on the size or on a date of a file now keeps its meaning in the Intune rule. Before, it was published as "file exists". A size must be a whole number of megabytes with the operator "at least" or "less than" (the Intune rule rounds the size down), and a date needs a UTC offset and whole seconds. A value that cannot keep its meaning is refused with the reason.

## [2026.1111.28] - 2026-11-11

### Changed

- The result of a WSUS publish now says that a WSUS update applies only where the detection finds an older version of the application, so WSUS cannot give a first install of it. The status of a WSUS-only row reads "Published (updates only)" or "Already on the server (updates only)". The result message, the log and the One Click report carry the same note.

## [2026.1110.27] - 2026-11-10

### Fixed

- The install wrapper of an EXE definition that lists processes to stop (Picpick, HashTools, Windows Admin Center) now reports the exit code of an installer that ends at once. Before, such an installer could be reported as a success (exit 0) when it failed.

## [2026.1110.26] - 2026-11-10

### Fixed

- Reading the files inside an installer (Add from file and Installer Analysis) no longer fails at random when 7-Zip ends before it is assigned to its job object, which can happen with a small archive on a busy computer. The page showed "could not be listed" or the message about a job that could not be used.

## [2026.1110.25] - 2026-11-10

### Fixed

- A WSUS publish of a version that is already on the server now checks that the update matches your stage. Before, it reported the old update as current, and a corrected install command, rule, return code or file never reached the server. Now the publish fails with the remedy (expire the update in WSUS Updates, then publish again) and changes nothing. An update published by an earlier release is reported as before, and the log says that it was not compared.

## [2026.1110.24] - 2026-11-10

### Fixed

- A WSUS update now carries the exit codes of the definition in its return codes: a reboot code as a success with a restart, a success code as a success. Before, WSUS knew only 0, 1707, 3010 and 1641 and recorded a successful install with another code as failed. This changes Dell Command Update, whose installer reports 2 for a restart. A stage made before this release takes the codes from the definition when it is published. An update that is already on the WSUS server keeps its old return codes: expire it in the WSUS Updates window and publish again.

## [2026.1110.23] - 2026-11-10

### Fixed

- A ConfigMgr publish now refuses a maximum run time outside 15 to 720 minutes, and the operators Between, OneOf and NoneOf, before it touches the site. Before, the site refused them in the middle of the run.
- A registry value detection with the property type Existence now publishes to ConfigMgr. Before, the publish refused it although the format page allows it.
- A replace of a ConfigMgr application now writes the Software Center name of the manifest, as a new application does. Before, the old name stayed. An empty run comment still leaves an existing description alone.

## [2026.1109.22] - 2026-11-09

### Fixed

- A ConfigMgr run now fails, with the reason, when the content distribution or the test deployment after the application fails. Before, the failure was only in the log and the run reported success.
- The inventory records the ConfigMgr destination of such a run as Partial, with the reason. In a command-line build with ConfigMgr and Intune, the run stops at the failed ConfigMgr destination, as it does for any failed destination.

## [2026.1109.21] - 2026-11-09

### Changed

- A ConfigMgr publish with OnExisting=Skip that finds the title on the site now says in the log which version the site holds and that the new version was not published.

## [2026.1109.20] - 2026-11-09

### Fixed

- A ConfigMgr publish that the site refuses in a deployment-type step no longer leaves an application with no deployment types. An application that the run created is removed. For an existing application that is replaced, the staging deployment types are removed.

## [2026.1109.19] - 2026-11-09

### Fixed

- Content distribution after a replace of a ConfigMgr application no longer fails when the site says the content is already distributed. A DP group that does not exist is now refused with a clear message.

## [2026.1109.18] - 2026-11-09

### Fixed

- Check the signature of a downloaded update again just before the installer starts. The update does not start if the file or its signature changed since the first check.

### Changed

- Add the license text of NSIS to the licenses folder and name it in the third-party notices. This was already in the previous package.

## [2026.1109.17] - 2026-11-09

### Changed

- Add tests that show a cancelled Intune publish stops at each step that waits. The program code did not change.

## [2026.1109.16] - 2026-11-09

### Changed

- Download the Intune content prep tool from a fixed release and check its SHA-256.

## [2026.1108.15] - 2026-11-08

### Changed

- Log "NOT verified" when a download has no SHA-256, and list which definitions verify theirs.

## [2026.1108.14] - 2026-11-08

### Fixed

- Correct the definition counts in the documentation.

## [2026.1108.13] - 2026-11-08

### Changed

- Refuse to release a commit that the CI workflow has not passed.

## [2026.1108.12] - 2026-11-08

### Fixed

- End a One Click run with a report when the run stops on an error.

## [2026.1108.11] - 2026-11-08

### Fixed

- Tell the operator when the Workbench cannot read a profile and shows the default.

## [2026.1108.10] - 2026-11-08

### Fixed

- Refuse a definition that names two stage flows, before any download.

## [2026.1108.9] - 2026-11-08

### Fixed

- Make up to three signing attempts when the timestamp server does not answer.

## [2026.1108.8] - 2026-11-08

### Fixed

- Keep the shared download cache whole when a hash mismatch forces a new download.
- Run detection after an installer that returns exit 1641 in the definition test runner.

## [2026.1107.7] - 2026-11-07

### Fixed

- Keep a FileVersion download from failing when two runs of one URL finish together.
- Sign the uninstaller that the installer writes, so Windows Defender keeps it.

## [2026.1107.6] - 2026-11-07

### Changed

- Ship the application without the .NET runtime; it needs the Desktop Runtime 10.0.14 (x64).
- Install the Desktop Runtime from Microsoft in the installer when it is missing.
- Make the installer and the update download smaller.

### Fixed

- Keep the earlier stage when a re-stage of the same version fails.
- Keep the FileVersion download cache whole; check the installer copied from it.
- Keep writes blocked after a force-closed plan run on Collection and Compliance and Maintenance Windows.
- End a hanging `gh auth token` read at its 10 second limit.
- Keep the results of earlier apps when a Convert from ConfigMgr publish fails.
- Keep the Setup and Settings entries when the settings file cannot be written; Catalog says "Not saved".
- Keep the WSUS Updates window locked while a write to the server works.

## [2026.1106.5] - 2026-11-06

### Changed

- Use one fixed app palette in the light and the dark theme instead of the Windows accent. The accent, the status colors, tertiary text and the control and card borders now pass WCAG 2.1 AA (text 4.5 to 1, borders and focus rings 3 to 1). The status column colors follow the theme.
- Use a solid window surface in both themes instead of the Mica material, so the contrast of text and borders does not depend on the wallpaper.
- Show each page title in a header band with an icon, color the section headings of Settings stronger, and show the status of a package by an icon as well as by color.
- Group the toolbars of Installer Analysis and Collection and Compliance so they wrap by group, and add headings to the query and script libraries of Pivots and Scripts.
- Use one type scale (12, 14, 16, 20, 28 px) and a 4 px spacing grid in every window and page. A test checks both.
- Give the page header band padding and an 8 px gap, align its description with the title, group the App Packager toolbar so it wraps by group, and trim page top margins to 8 px. Shorten the Site Hygiene description.
- Fail the build when a brush pair of a page falls below WCAG 2.1 AA (text 4.5 to 1, borders and focus rings 3 to 1). Disabled text is exempt.
- Add a test that no style removes the keyboard focus visual of a control.
- Retake the guide images that show the App Packager page, the Settings page, the Discover prompt, the About section, the sidebar and the update window with the new palette and page header. Add images of the Convert from ConfigMgr window and the update window.
- Describe Discover unmanaged software as it works now: names and versions only, no client count and no Clients column. Add the page for Convert from ConfigMgr.

### Fixed

- Show the whole check box in the Discover unmanaged software list. The default cell clipped its right side.
- Drop the second "Discovered N apps." line from the Discover prompt. It repeated the title.
- Write "1 of 1 deployment type converts" in the Convert from ConfigMgr preview, and show hint text in its empty plan and log boxes.
- Name only the signer (`Jason Ulbright`) in the update window after a download. It showed the city, state and country of the certificate too.

## [2026.1106.4] - 2026-11-06

### Added

- Add the engine of the ConfigMgr to Intune conversion: it reads the stored document of an application, plans one Intune Win32 app per deployment type with detection, requirement and variant rules, and stages content, manifests and a report. The engine also reads the application from the site, builds the .intunewin files and publishes the apps. The Convert from ConfigMgr button on the App Packager page opens a window that previews the plan, converts, and optionally builds and publishes.
- Add an update check. At start and from **Settings**, **Check for updates**, the app looks up the latest GitHub release without a sign-in. The update window shows the installed and the new version and the release notes link. **Download** checks the SHA-256 value of `checksums.txt` and the Authenticode signature (valid, timestamped, `CN=Jason Ulbright`); **Install and close** starts the installer only on that click. A setting turns the startup check off.

### Changed

- Make Discover unmanaged software read application names and versions only. It no longer reads client keys, client counts, publishers or inventory age. The prompt has no Clients column and the status line shows only the number of apps found.
- Copy the installer at save time in Add from file. The definition keeps a verified copy in the installers folder beside the definitions folder.
- Keep the FileVersion scraper cache in the per-user data folder instead of the Windows temp folder.
- Delete the oldest cached installers when the FileVersion cache passes 2 GiB.
- Detect 18 packagers by installed version, not by the presence of a file or a key. The packagers are AIMP, Anaconda, Android Studio, AWS Session Manager plugin, Azure Storage Explorer, CPU-Z, CutePDF Writer, Cyberduck, DAX Studio, Greenshot, HWMonitor, PDF Studio Viewer, pgAdmin 4, Positron, PSPad, Rainmeter, Sysinternals Suite and Vim.
- Add the detection tokens `{BundleId}`, `{FeedBuild}` and `{version_numeric}`, the scraper field `BuildQuery`, and the ZIP payload fields `FlatExtract` and `VersionRegistryKey`.

### Fixed

- Install Vim, pgAdmin 4 and AIMP with the switches the PowerShell app proved: `/S /allusers` for Vim, `/ALLUSERS` for pgAdmin 4, `/AUTO /SILENT` for AIMP.
- Uninstall Vim through the UninstallString of its entry, Anaconda in place with its folder deleted after, and the AWS Session Manager plugin from its bundle cache copy.
- Count exit code 1223 as success for the Android Studio install, and read its newest release from the feed again. The feed names new installers without the version.
- Check and download the current 64-bit Zoom Workplace release. The latest URLs served the older 32-bit build without `archType=x64`.
- Generate the PostgreSQL 13 to 17 superuser password and the MariaDB root password on each target. The definitions carried one fixed password for every target. The password is stored DPAPI-protected in a folder only SYSTEM and Administrators can read.
- Download CutePDF Writer over HTTPS and require a valid vendor signature.
- Detect the .NET Desktop runtime by its own framework folder (`PresentationFramework.dll`) instead of the host resolver.
- Treat a rebuilt GIMP installer (`gimp-3.0.8-setup-2.exe`) as a distinct release. The scraper read only the base version and downloaded the first build.
- Keep the files a profile places under the `scripts` folder when a build is signed, and sign them with the deployment files. Signing removed the whole folder. It now replaces only `detect.ps1` and the `requirements` folder inside it.
- Apply `TimestampRequired` to a preserved third-party signature. A signature without a timestamp counter-signature now fails the check.
- Decline earlier WSUS updates only after the replacement is approved. A failed approval followed by a decline left computers with no update.
- Stop a WSUS adapter child that runs longer than 30 minutes and report it as a failure. The child had no time limit.
- Fail the MSIX install and uninstall wrappers when an AppX cmdlet fails. They exited 0 after a non-terminating error.
- Extract the Sysinternals Suite ZIP in the install wrapper. The wrapper started the ZIP as a program before.
- Read the ProductCode of an Advanced Installer or InstallShield EXE that wraps an MSI. The analysis extracts the MSI in the sandbox and reads its Property table.

## [2026.1103.3] - 2026-11-03

### Added

- Add a user guide with 90 pages and 123 screenshots.
- Add a short README with the product overview.
- Add web and file entry points and a Catalog window to App Packager.

### Changed

- Group applications in the sidebar of App Packager.
- Show status text in readable colors in every status column.
- Keep at least three whole rows visible in grids at 1024x720.

### Fixed

- Repair the keyboard shortcuts and the conflict dialog.
- Repair the focus ring of the change-plan grid.
- Show clear empty-state messages in Site Hygiene Live.
- Correct the Citrix labels, the About path and the signing hint.
- Correct the WSUS connection hint and the secure-connection wording.

## [2026.1026.2] - 2026-10-26

### Added

- Integrate eight native administration pages into the desktop.
- Test installed-application detection methods and save exportable results.
- Schedule maintenance windows and inspect coverage.
- Create collections and review compliance configurations.
- Analyze installer payloads offline with bounded extraction.
- Plan deployments and promote guarded rings after plan checks pass.
- Manage distribution point content with audited recovery.
- Run approved scripts and client queries through confirmed plans.
- Inspect site health through live views, trends, alerts, and history.
- Import legacy dashboards through a reviewed settings plan.

### Changed

- Keep site reads read-only and show incomplete results as unknown.
- Record confirmed changes with ownership checks and audit history.

### Removed

- Remove the external suite requirement for integrated workflows.

## [2026.1017.1] - 2026-10-17

Initial release
