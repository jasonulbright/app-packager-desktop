# Changelog

## [2.2.0-alpha6] - 2026-09-07

### Fixed

- Fixed install paths with spaces for HashTools, HeidiSQL and PSPad.
- Fixed the TeamViewer Host version read without 7-Zip.
- Fixed repeated full downloads when checking versions from the installer.
- Fixed Intune detection for prefix, suffix and substring matches.
- Fixed Intune detection operators other than equals and greater-or-equal.

## [2.2.0-alpha5] - 2026-09-05

### Added

- Added Install Parameters to Settings, saved per application.
- Added M365 deploy mode and excluded apps parameters.
- Added SSMS install parameters.
- Added TeamViewer Host device assignment parameters.

### Fixed

- Fixed M365 channel setting not applied to the definitions.

## [2.2.0-alpha4] - 2026-09-05

### Added

- Added Content Layout (nested or flat) to Settings.
- Added auto-distribute to a DP group after Package.
- Added a test collection deployment after distribution.
- Added Create .intunewin during Package for MECM-only targets.
- Added One Click tracking with cadence and action on update.
- Added a One Click button to the Packages page.
- Added detected Console, 7-Zip and Content Prep Tool rows to Settings.
- Added About with the version and a releases link.

### Fixed

- Fixed the empty Packages list when no PS1 packagers folder exists.
- Fixed the Deployment Conditions Application column width.
- Fixed SpectraPDF install params.

## [2.2.0-alpha3] - 2026-09-05

### Added

- Added a per-user installer.
- Added Install for System or User to Typora.

### Fixed

- Fixed Typora install path.

## [2.2.0-alpha2] - 2026-09-04

### Added

- Build and publish releases from the release workflow.
- Add Apache NetBeans (catalog at 295).
- Add 177 definitions from the catalog waves (117 to 294).
- Install the icon pack from a local or UNC zip.
- Download the icon pack from Settings and apply icons to MECM and Intune applications.
- Extract full-size icons from installers and MSI packages.
- Tag the icon source on 277 definitions.
- Choose Skip, Overwrite or Fail when the application already exists at the same version.
- Show a per-run conflict dialog with an apply-to-all choice.
- Set the DBeaver install scope and AI opt-out from installer settings.
- Substitute installer settings through value maps and post-install scripts.
- Verify the installer SHA256 against a pinned or feed-published digest.
- Declare script and compound detection rules in definitions.
- Authenticate GitHub API calls with PACKR_GITHUB_TOKEN, GITHUB_TOKEN or the GitHub CLI.
- Show GitHub API authentication and remaining quota in Settings.
- Add the HeadProbe, chained and Dell catalog scrapers.
- Select the highest version among page matches and JSON array elements.
- Stage MSIX packages as provisioned packages with a pinned publisher.
- Build offline layouts for the Windows ADK and the Windows PE add-on.
- Generate extract-then-setup wrappers for the SQL Server Express media box.
- Open a first-run setup window for the deployment target and its settings.
- Resolve a FileVersion scrape through page hops.
- Re-download a cached installer once when its checksum fails.
- Retry a bootstrapper layout while another installation holds the Windows Installer mutex.
- Added NSIS and Inno Setup compiled-script analysis for drops.
- Added Install for System or User to the drop preview.
- Added an Install for column to Deployment Conditions.
- Added Install for System or User to eleven definitions.

### Changed

- Name the release zip AppPackagerDesktop-<version>.zip.
- Stage Slack as an MSIX provisioned for all users.
- Switch Audacity 4.0 to its MSI installer with ProductCode detection.
- Accept ShareX's per-architecture setup asset.
- Read the Defraggler build from the builds page and the version from the installer.
- Install RStudio for all users.
- Take the newest Obsidian release that carries a Windows installer.
- Download WizTree from the vendor's own host.
- Unpin the download sources of eight definitions that carried release tokens.
- Move 46 definitions to highest-version selection.
- Enable the Check MECM and Package actions from the deployment target.
- License under MIT and describe the application as free software.
- Rewrite the README for administrators.

### Fixed

- Run only the product's own uninstaller by matching its Add/Remove Programs entry.
- Fixed uninstall wrappers for 17 definitions with plain command-line uninstall commands.
- Keep the definition's name, version and publisher when installer metadata names a bundled component.
- Fixed Gpg4win detection view.
- Fixed KDiff3 detection view and uninstall params.
- Fixed RStudio detection view.
- Start Intune-only runs without a ConfigMgr site code.
- Resolve origin-relative download links against the scraper's origin.
- Honor NoUserAgent in JSON feeds.
- Pick the newest Liberica JDK 21 release.
- Correct the PDF Studio Viewer install path.

## [2.2.0-alpha1] - 2026-08-31

### Added

- Split applications into deployment types by architecture, OS language or network.
- Override install and uninstall commands per application.
- Publish Win32 applications to Intune.
- Analyze a dropped installer and propose a definition.
- Set the deployment target, Intune tenant and site server in Settings.
- Edit deployment conditions per application.

### Changed

- Pass the pre-stage configuration review values to staging.
- Accept a configured site server instead of the local machine.
- Update the SQLite library.

## [2.1.0-alpha8] - 2026-04-05

### Fixed

- Match NSIS uninstallers by display name instead of download file name.
- Uninstall MSI packages by ProductCode.
- Correct the installer framework of Git, Positron, WinSCP and Wireshark.
- Correct the uninstall switches of Vim and Audacity.

## [2.1.0-alpha7] - 2026-04-04

### Added

- Identify NSIS, Inno Setup, WiX Burn, MSIX and ZIP installers and read their metadata.
- Derive detection rules from installer metadata when no ProductCode exists.
- Record the installer framework on every definition.
- Define per-application installer settings with toggles, dropdowns, text and numbers.
- Create MSIX deployment types.
- Deploy per-user applications with the user install behavior.
- Distribute content and deploy applications after packaging.
- Validate the content share before packaging.
- Record every operation in a version history database.
- Copy content to a share without creating an application.
- Generate PSADT deployment scripts.
- Add a five-step Add New App wizard.

### Changed

- Sanitize vendor folder names for content paths.
- Convert plus and dash version separators for version comparisons.
- Make the package comment optional.
- Enter runtime settings with number boxes.

## [2.1.0-alpha6] - 2026-04-02

### Added

- Write per-session log files.

### Changed

- Align status labels with the workflow and show every status in the grid.

## [2.1.0-alpha5] - 2026-04-02

### Changed

- Download Tableau Desktop, Prep Builder and Reader from the vendor CDN.

## [2.1.0-alpha4] - 2026-04-02

### Added

- Discover Microsoft 365 versions from the Office CDN.
- Stage Microsoft 365 applications through the Office Deployment Tool.
- Read MSIX package versions from the manifest.
- Read versions from the Last-Modified header.
- Extract installers from downloaded ZIP files.

### Fixed

- Copy Microsoft 365 subdirectories to the content share.
- Detect TeamViewer through its MSI variant.

## [2.1.0-alpha3] - 2026-04-01

### Added

- Query JSON APIs for versions.
- Read versions from downloaded installers.
- Extract the download URL from the version response.
- Omit the User-Agent header for vendors that block it.
- Override the displayed version.
- Transform versions with regular expressions.
- Detect by registry key existence.
- Substitute the version in file detection paths.

### Changed

- Reuse the checked version when staging.
- Resolve redirects without downloading the body.
- Uncheck rows after errors and after packaging.
- Read 7-Zip releases from GitHub.

### Fixed

- Detect ASP.NET Core 8 and .NET runtimes.
- Build Node.js and Brave download URLs.
- Download Corretto with the raw version.
- Compare Malwarebytes versions as versions.
- Match the .NET and ASP.NET application names.
- Stop stale runtime files shadowing the installed runtime.
- Download mRemoteNG from the latest-release path.

## [2.1.0-alpha2] - 2026-03-31

### Added

- Transform versions with insert_dot, replace and strip_prefix.
- Override the installer file name for link-style URLs.
- Extract versions from redirect URLs.

### Changed

- Fetch pages with curl.
- Fall back to the definition's vendor when the MSI manufacturer is unusable.

## [2.1.0-alpha1] - 2026-03-31

### Added

- Add the engine library with scrapers, staging and MECM packaging.
- Route JSON definitions through the engine and PowerShell packagers through PowerShell.

## [2.0.0-alpha] - 2026-03-30

### Added

- Add the Fluent desktop UI with Packages, Applications and Settings pages.
- Check versions, check MECM, stage and package from the grid.
- Test the MECM connection, persist window state and export CSV.
