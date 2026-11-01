# Glossary

This page defines the terms that App Packager Desktop uses. Each term has one meaning in all pages.

## What it does

The page explains the words of the user interface and of the documentation. The terms are in alphabetical order.

## When to use it

Use this page when a word on another page is not clear.

## Requirements

None.

## Terms

**Application (ConfigMgr).** The object that App Packager Desktop creates in ConfigMgr when you select **Publish to ConfigMgr**. It has deployment types.

**ARP.** Add/Remove Programs. A definition names its ARP entry with the field `ArpName`. Discover unmanaged software uses this entry to match an application.

**Audit chain.** A hash chain in an append-only audit file. Each record ends with a hash that covers its fields and the previous record. See **Verified**.

**Baseline.** A ConfigMgr configuration baseline. The Compliance section of **Collection and Compliance** creates and lists baselines.

**Bring your own (BYO).** An application that you create from an installer that you drop on the **Packages** page. **Save as Application** keeps the installer as an application with numbered installer revisions.

**Build.** The record of one stage run of an application and profile. The **Review & build** section of the Application Workbench lists earlier builds as **Prior builds**.

**Cadence.** The number of days that a version check stays fresh in One Click. **Default cadence (days)** in **Settings** sets the default.

**Classification.** The WSUS classification of each update that App Packager Desktop publishes. The default is **Updates**.

**Collection.** A ConfigMgr group of devices or users. A device collection holds devices. A user collection holds users. A built-in collection has an ID that starts with `SMS`. The Deployment Helper blocks deployments to built-in collections.

**Configuration item (CI).** A ConfigMgr object that holds one or more settings. A baseline groups configuration items.

**Content Prep Tool.** The Microsoft Win32 Content Prep Tool, `IntuneWinAppUtil.exe`. It builds the `.intunewin` file. **Settings**, **Detected Tools** shows if it is on this computer.

**CPE.** A product name for the CVE lookup. A definition names its product with the field `Cpe`.

**CVE.** A published vulnerability record. **Check CVEs** looks up the CVEs between the ConfigMgr version and the latest version of an application.

**CVSS.** The severity score of a CVE. The column **Max CVSS** shows the highest score.

**Data folder.** The folder `%LOCALAPPDATA%\AppPackager`. It holds `settings.json`, the Workbench data and the default staging folder.

**Definition.** One JSON file that describes one application: its vendor, its version scraper, its download, its install arguments and its detection. The catalog has 319 definitions. You can add your own definitions.

**Deployment type.** A part of a ConfigMgr application. App Packager Desktop creates the deployment types when it publishes to ConfigMgr.

**Destination.** A system that receives a published application: ConfigMgr, WSUS or Intune. **Default destinations** in **One Click Settings** sets the destinations of a tracked application.

**Distribution point (DP).** A ConfigMgr server that holds content for clients. The DP Content Manager page lists distribution points and their content.

**DP group.** A named set of distribution points. **Distribution point group** in **Settings** names the group that receives content after Package.

**Drift tag.** The text `[ccm:<ID>@<version>]` that App Packager Desktop adds to the description of a baseline. The **Drift** list compares it with the catalog version.

**EPSS.** A score from FIRST. The column **Max EPSS** and the CVE window show it.

**KEV.** The CISA catalog of known exploited vulnerabilities. The column **KEV** shows if a CVE is in it.

**Locally published update.** A WSUS update that App Packager Desktop publishes from an installer. WSUS offers it only to a computer that has an older version of the application.

**Maintenance window.** A ConfigMgr schedule for a collection. The **Maintenance Windows** page creates and lists them.

**NVD.** The NIST National Vulnerability Database. The CVE lookup asks the NVD API first. **NVD API key** in **Settings** is optional.

**One Click.** A run that checks the applications that you track, then stages and publishes each application that has a new version. After you select **Run**, One Click asks no more questions. See [One Click](../core/one-click.md).

**Packager.** One row of the **Packages** grid. A packager is a JSON definition (badge `JSON`) or a PS1 packager script (badge `PS1`).

**Plan.** In most tools, a plan is the list of changes that a tool shows before it writes to the site. You confirm the plan or cancel it. In the Rings tab, a plan is the ordered list of rings for a deployment.

**Profile.** A saved set of changes to one application in the Application Workbench. A definition supplies the defaults. A profile holds your changes.

**Publish.** Send a staged application to a destination.

**Ring.** One step of a Rings plan. A ring names a device collection, a purpose, and the offsets for the available time and the deadline. A plan has 1 to 100 rings.

**Run (rings).** A run turns one object and one ring plan into ordinary deployments, one for each ring. A run file keeps the sequence.

**Site code.** The three-character code of a ConfigMgr site. **Site Code** in **Settings** sets it.

**SMS Provider.** The ConfigMgr server role that the application connects to. **Site Server / SMS Provider Host** in **Settings** names the host.

**Stage.** Download the installer, check it, and write the content and the install scripts to the staging folder. The folder is `<Download Root>\<application name without spaces>-<architecture>\<version>`.

**Verified.** A mark in a History list. **Verified** means the record passes the audit chain. **Not verified** means a record was edited, or follows a broken line.

**Workbench.** The Application Workbench. It is the editor for one application, with profiles, bundles and builds. See [Workbench](../core/workbench.md).

**WSUS.** Windows Server Update Services. **Publish to WSUS** publishes an installer as a locally published update.

**`.intunewin`.** The package file that the Content Prep Tool builds for a Win32 app in Intune.

## Limits

The glossary defines terms. Settings and limits are in [settings reference](settings.md).

## Troubleshooting

See [Troubleshooting](../troubleshooting.md) for messages.
