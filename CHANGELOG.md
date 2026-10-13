# Changelog

## Unreleased

### Added

- Look up the CVEs an update fixes for every checked application.
- Show CVEs, Max CVSS, KEV, In latest and Newest CVE columns in the grid.
- Open a CVE window with fix version, dates and links to six CVE sites.
- Write the CVE report as HTML, CSV, XLSX, Markdown and JSON.
- Run the CVE report from `AppPackagerBuild --cve-report`, with a webhook and a drop folder.
- Add the NVD API key and the lookup-after-Check-ConfigMgr setting.
- Show the stored ConfigMgr version, compare result and CVE count on launch.
- Add a CPE to 294 definitions and a vendor advisory page to 94.
- Show which applications WSUS cannot take in the One Click plan before a run.
- Select a user collection or a device collection for ConfigMgr test deployments.

### Changed

- Report a vendor release with more version parts than ConfigMgr holds as an update.
- Find ConfigMgr titles with the version mid-name or the architecture attached.
- Skip One Click when the vendor reports a version older than the one last seen.
- Skip a destination that holds a newer version than the vendor reports.
- Publish again when the Workbench profile or the signing policy changed since the last publish.
- Build the profile that the Workbench shows when you select Stage or Publish there.
- Accept a build without a script in a category that a Require signing switch names.
- Check the detection script signature again right before sending it to ConfigMgr.
- Read a PS1 packager version from the first output line shaped like a version.
- Keep every grid header readable; the grid scrolls when the columns do not fit.

### Fixed

- Detect UltraVNC and IrfanView by the dotted version the installer registers.
- Accept the CCleaner exit code returned when no desktop session exists.
- Detect CutePDF Writer, Jabra Direct and Windows Admin Center by stable keys.
- Find the newest LibreOffice build the vendor serves.
- Find the UltraVNC uninstaller through its uninstall entry.
- Read Unity Hub installers from the current vendor feed.
- Stage Agent Ransack from its MSI.
- Name the curl exit code when a version page cannot be fetched.
- Suggest WSUS database maintenance when a publish times out.
- Keep an existing Intune app unchanged until its replacement content is committed.
- Check every page when matching an Intune app by name.
- Keep completed ConfigMgr and WSUS results when a later step fails or is cancelled.

### Security

- Remove upload credentials from storage error logs.
- Fix a gap in the script launcher audit.
- Fix four defects in how the CVE lookup handles remote data.

### Documentation

- Document the CVE lookup, its network requests, report locations and the command-line run.
- Document per-user definitions, data-root overrides and operation history.

## [2026.1003.1] - 2026-10-03

Initial release
