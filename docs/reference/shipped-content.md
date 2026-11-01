# Shipped content

This page lists the content that ships with App Packager Desktop: application definitions, query templates, catalog entries, templates, ring plans, library items, checks and views. Each count was taken from the files in the source tree. Where a test asserts the count, the test is named.

A count describes the build that you installed. A user file in a data folder can add items to a list. The sections below say where that is possible.

## Summary

| Content | Count | Where it is used |
|---|---|---|
| Application definitions | 319 | Core pages. See [definitions](../core/definitions.md). |
| Scraper types used by the definitions | 7 | Version checks. See [definitions](../core/definitions.md). |
| Collection query templates | 245 | [Collections: queries and templates](../tools/collections-queries.md) |
| Compliance catalog entries | 16 | [Compliance](../tools/compliance.md) |
| Maintenance Windows templates | 5 | [Maintenance Windows](../tools/maintenance-windows.md) |
| Detection Tester templates | 0 | [Detection Tester](../tools/detection-tester.md) |
| Deployment Helper templates | 4 | [Deployment Helper](../tools/deployment-helper.md) |
| Ring plans | 2 | [Deployment rings](../tools/deployment-rings.md) |
| Pivots and Scripts library queries | 7 | [Pivots and Scripts](../tools/pivots-and-scripts.md) |
| Pivots and Scripts library scripts | 4 | [Pivots and Scripts](../tools/pivots-and-scripts.md) |
| Site Hygiene checks | 57 | [Site Hygiene checks](site-hygiene-checks.md) |
| Site Hygiene scan areas | 18 | [Site Hygiene](../tools/site-hygiene.md) |
| Site Hygiene Live views | 6 | [Site Hygiene Live](../tools/site-hygiene-live.md) |
| Shipped site query templates | 0 | [Collections: queries and templates](../tools/collections-queries.md) |

## Application definitions

The folder `src\ApplicationPackagerEngine\Definitions` holds 319 definition files (`*.json`). See [definitions](../core/definitions.md) and [definition format](definition-format.md).

| Property | Count of definitions |
|---|---|
| Version scraper `HttpRegex` | 208 |
| Version scraper `JsonQuery` | 65 |
| Version scraper `FileVersion` | 31 |
| Version scraper `ChainedHttpRegex` | 7 |
| Version scraper `CdnCab` | 6 |
| Version scraper `DellCatalog` | 1 |
| Version scraper `HeadProbe` | 1 |
| `ArpName` with a pattern | 295 |
| `ArpName` set to `none` | 24 |
| With a `Cpe` | 294 |
| That read `api.github.com` | 101 |
| With `InstallerSettings` | 13 |

The 13 definitions with `InstallerSettings`: `adobereader`, `dbeaver`, `mariadb-server`, `sqlserver2022express`, `ssms`, `teamviewerhost`, `windowsadmincenter`, and the six Microsoft 365 definitions `m365apps-x64`, `m365apps-x86`, `m365project-x64`, `m365project-x86`, `m365visio-x64` and `m365visio-x86`. The Settings page lists these definitions at run time.

## Collection query templates

The Templates tab lists 245 shipped templates. They come from two embedded files in `src\ApplicationPackagerEngine\Services\Collections\Templates`:

- `operational-collections.json`: 225 templates without parameters.
- `parameterized-templates.json`: 20 templates. 18 of them have parameters.

The test `QueryTemplateStoreTests` asserts 245, 225, 20 and 18. The `Source` column shows `Shipped` for these templates. The `Category` list holds 26 categories:

| Category | Templates |
|---|---|
| Active Directory | 2 |
| Client Health | 2 |
| Clients | 3 |
| Clients Version | 38 |
| Hardware | 7 |
| Hardware Inventory | 1 |
| Laptops | 4 |
| Microsoft 365 Apps Build Version | 43 |
| Microsoft 365 Apps Channel | 6 |
| Mobile Devices | 10 |
| Network | 1 |
| Office 365 Build Version | 39 |
| Office 365 Channel | 4 |
| Operating System | 1 |
| Others | 2 |
| Project | 5 |
| SCCM Infrastructure | 4 |
| Security | 1 |
| Servers | 11 |
| Software | 3 |
| Software Inventory | 1 |
| System Health | 8 |
| Systems | 4 |
| Visio | 5 |
| Windows Update Agent | 2 |
| Workstations | 38 |

You can add your own templates. See "Your own templates" in [Collections: queries and templates](../tools/collections-queries.md).

The Site Queries tab lists queries that the site holds. The application ships none.

## Compliance catalog

The folder `src\ApplicationPackagerEngine\Services\Compliance\Catalog\Entries` holds 16 entries. The folder `Builders` holds 16 builder scripts. The test `ComplianceRegistrationTests` asserts 16 entries.

All entries have the version `1.0.0`. All entries except one have the status `Draft`. The entry `specexec-mitigations` has the status `Validated`. Every entry takes the parameters `SiteCode` and `SiteServer`. The column "Other parameters" lists the others.

| ID | Display name | Subcategory | Severity | Restart | Other parameters |
|---|---|---|---|---|---|
| `acrobat-alltoolspane-fix` | Acrobat All Tools Pane Fix | Configuration | Warning | Yes | `CollectionName`, `DetectOnly` |
| `anonymous-enumeration-restrict` | Anonymous Enumeration Restrictions | Hardening | Critical | No | `CollectionName`, `DetectOnly` |
| `autorun-autoplay-disable` | Autorun/Autoplay Disable | Compliance | Warning | No | `CollectionName`, `DetectOnly` |
| `do-downloadmode` | Delivery Optimization Download Mode | Configuration | Warning | No | `ClientCollectionName`, `ServerCollectionName`, `ClientDownloadMode`, `ServerDownloadMode`, `DetectOnly` |
| `installer-alwaysinstallelevated-disable` | Windows Installer AlwaysInstallElevated Disable | Compliance | Critical | No | `CollectionName`, `DetectOnly` |
| `llmnr-disable` | LLMNR Disable | Hardening | Warning | No | `CollectionName`, `DetectOnly` |
| `lsa-protection-runasppl` | LSA Protection (RunAsPPL) | Hardening | Critical | Yes | `CollectionName` (no remediation) |
| `ntlm-lmcompatibilitylevel` | NTLM LmCompatibilityLevel | Hardening | Critical | No | `CollectionName`, `DetectOnly` |
| `powershell-scriptblock-logging` | PowerShell Script Block Logging | Compliance | Warning | No | `CollectionName`, `DetectOnly` |
| `rdp-nla-tls-require` | RDP NLA + TLS Required | Compliance | Critical | No | `CollectionName`, `DetectOnly` |
| `smb-signing-require` | SMB Signing Required | Hardening | Critical | No | `CollectionName`, `DetectOnly` |
| `smbv1-disable` | SMBv1 Disable (server) | Compliance | Critical | No | `CollectionName`, `DetectOnly` |
| `specexec-mitigations` | CPU Speculative Execution Mitigations (Intel + AMD, Bundled) | CVE-Remediation | Critical | Yes | `HtEnabledCollectionName`, `HtDisabledCollectionName`, `SkipDeploy`, `DetectOnly` |
| `tls-legacy-disable` | TLS Legacy Disable (SCHANNEL + .NET 4.x) | Compliance | Critical | Yes | `CollectionName`, `DetectOnly` |
| `uac-remote-restriction` | UAC Remote Restriction (LocalAccountTokenFilterPolicy) | Compliance | Critical | No | `CollectionName`, `DetectOnly` |
| `wdigest-disable` | WDigest Credential Caching Disable | Hardening | Critical | No | `CollectionName`, `DetectOnly` |

The entry `specexec-mitigations` needs two collections: "Devices: Hyperthreading Enabled" and "Devices: Hyperthreading Disabled". No shipped builder creates them.

## Maintenance Windows templates

The folder `src\ApplicationPackagerEngine\Services\MaintenanceWindows\Templates` holds 5 templates. The test `MaintenanceTemplateStoreTests` asserts 5.

| Name |
|---|
| Daily After Hours |
| Monthly First Saturday |
| Patch Tuesday + 3 Days |
| Patch Tuesday + 7 Days |
| Weekly Sunday 2 AM |

## Detection Tester

The Detection Tester page has no template feature and ships no template.

## Deployment Helper templates

`DeploymentTemplateStore` writes four templates to the data folder `Deployments\Templates` (`DeploymentTemplateStore.Seeds`). A user template never replaces one of them. The test `DeploymentTemplateStoreTests` asserts 4.

| Name | Purpose | User notification | Hours from available time to deadline |
|---|---|---|---|
| Workstation Pilot | Available | DisplaySoftwareCenterOnly | 0 |
| Workstation Production | Required | DisplayAll | 72 |
| Server Pilot | Available | DisplaySoftwareCenterOnly | 0 |
| Server Production | Required | HideAll | 168 |

## Ring plans

`RingPlanStore` writes two plans to the data folder `Deployments\Rings` on the first list (`RingPlanStore.Seeds`). The test `RingPlanStoreTests` asserts the two names. Offsets are whole days from the plan start. Every ring of a shipped plan has the purpose Required. The plans have no collection set. Set each collection before use.

| Plan | Hold later rings | Ring | Available offset | Deadline offset | Notification | Success threshold |
|---|---|---|---|---|---|---|
| Workstation-Rings | No | QA | 0 | 1 | DisplayAll | none |
| | | Pilot | 2 | 5 | DisplayAll | 90 |
| | | Prod 1 | 7 | 10 | DisplayAll | 95 |
| | | Prod Final | 14 | 17 | DisplayAll | none |
| Server-Rings | Yes | Test | 0 | 2 | HideAll | 95 |
| | | Prod | 7 | 9 | HideAll | none |

## Pivots and Scripts library

The built-in library holds 7 queries and 4 scripts. The test `PivotsFilesTests` asserts both counts and the hash of each item.

| Kind | Name |
|---|---|
| Query | BIOS and serial |
| Query | Free disk |
| Query | Installed software |
| Query | Logged on users |
| Query | Operating system |
| Query | Processes |
| Query | Services |
| Script | Client version |
| Script | Pending reboot |
| Script | Service state |
| Script | Uptime |

## Site Hygiene

- 57 checks, listed with ID, category, severity and title in [Site Hygiene checks](site-hygiene-checks.md) (`HygieneCatalog.Checks`).
- 18 scan areas: Applications; Application relationships and content paths; Content distribution; Packages; Collections; Collection evaluation schedules; Deployments; Devices; Boundaries; Task sequences; Software updates; Software update package content; Distribution points; Compliance and client settings; Drivers; Administrative users; Maintenance windows; Site maintenance (`HygieneAreas.All`).
- 6 refreshed Live views: Deployments, Content, Distribution Points, Client Health, Inactive Devices and Site Health. The Trends view reads the metrics history and is not refreshed (`LiveView`).
