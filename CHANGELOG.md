# Changelog

## [Unreleased]

### Changed

- Use one fixed app palette in the light and the dark theme instead of the Windows accent. The accent, the status colors, tertiary text and the control and card borders now pass WCAG 2.1 AA (text 4.5 to 1, borders and focus rings 3 to 1). The status column colors follow the theme.
- Use a solid window surface in both themes instead of the Mica material, so the contrast of text and borders does not depend on the wallpaper.
- Retake the guide images that show the App Packager page, the Settings page, the Discover prompt and the About section. Add images of the Convert from ConfigMgr window and the update window.
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
