# Changelog

## [Unreleased]

## [2026.1109.23] - 2026-11-09

### Fixed

- A ConfigMgr publish now refuses a maximum run time outside 15 to 720 minutes, and the operators Between, OneOf and NoneOf, before it touches the site. Before, the site refused them in the middle of the run.
- A registry value detection with the property type Existence now publishes to ConfigMgr. Before, the publish refused it although the format page allows it.

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
