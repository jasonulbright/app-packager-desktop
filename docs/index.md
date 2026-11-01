# App Packager Desktop documentation

App Packager Desktop packages third-party Windows applications and publishes them to ConfigMgr, Intune and WSUS. This page links every page of the documentation.

## Start

- [Getting started](getting-started.md): install, the Setup window, connect to a site, package one application.

## Core

The core is the packaging function of the application.

- [Packages](core/packages.md): the grid, the columns, the buttons, the context menu, Check Latest, Check ConfigMgr and Operation history.
- [Packages grid](core/packages-grid.md): the columns, the status values, the context menu and the keyboard in detail.
- [Stage and publish](core/stage-and-publish.md): Stage, Publish to ConfigMgr, Publish to Intune and the targets.
- [Publish to ConfigMgr](core/publish-to-configmgr.md): the steps, the content layout and the refusals.
- [Publish to Intune](core/publish-to-intune.md): the steps, the targets and the outcomes.
- [Applications](core/applications.md): show and hide applications in the Packages grid.
- [Add App](core/add-app.md): the five-step wizard that writes a new definition, and Test Scrape.
- [Definitions](core/definitions.md): shipped and user definitions, scrapers and detection types.
- [Application Workbench](core/workbench.md): sections, profiles, bundles and builds.
- [Bring your own installer](core/bring-your-own-installer.md): drop an installer, review it, and save it as an application.
- [Script signing](core/script-signing.md): sign and require signatures for generated scripts.
- [WSUS publishing](core/wsus-publishing.md): Publish to WSUS, refusal codes, WSUS Updates and Catalog Import.
- [WSUS Updates and Catalog Import](core/wsus-updates.md): the update list, the catalog import and the certificate trust.
- [One Click](core/one-click.md): settings, the plan window, the run and the report.
- [Discover unmanaged software](core/discover-unmanaged-software.md): the prompt, the matching and the SQL access.
- [CVE lookup](core/cve-lookup.md): Check CVEs, the CVE window, CVE Report and the NVD key.
- [Command-line build](core/command-line-build.md): `AppPackagerBuild`, the CVE report run and the exit codes.
- [Settings page](core/settings-page.md): every section of the Settings page.
- [Deployment behavior](deployment-behavior.md): the test deployment after Package and the Intune outcomes.

## Tools

The tools are the entries of the sidebar after the Application Workbench.

- [Detection Tester](tools/detection-tester.md)
- [Maintenance Windows](tools/maintenance-windows.md)
- [Collection and Compliance](tools/collection-and-compliance.md)
- [Collections: queries and templates](tools/collections-queries.md)
- [Compliance](tools/compliance.md)
- [Compliance: editor and auditing](tools/compliance-editor-auditing.md): the Editor and Auditing tabs.
- [Installer Analysis](tools/installer-analysis.md)
- [Deployment Helper](tools/deployment-helper.md)
- [Deployment Helper: History and Options](tools/deployment-helper-history-options.md): the History and Options tabs.
- [Deployment rings](tools/deployment-rings.md)
- [Deployment rings: plans](tools/deployment-ring-plans.md): ring plans and the plan editor.
- [DP Content Manager](tools/dp-content-manager.md)
- [Pivots and Scripts](tools/pivots-and-scripts.md)
- [Pivots and Scripts: site scripts](tools/pivots-site-scripts.md)
- [Site Hygiene](tools/site-hygiene.md)
- [Site Hygiene: live views](tools/site-hygiene-live.md)

## Reference

- [Settings](reference/settings.md): every setting with its default and where it is stored.
- [Permissions](reference/permissions.md): the rights that each page needs.
- [Network](reference/network.md): every outbound call.
- [Data folders](reference/data-folders.md): every folder and file that the application writes.
- [Error messages](reference/error-messages.md): message, cause and fix.
- [Keyboard](reference/keyboard.md): shortcuts, dialog keys and access keys.
- [Glossary](reference/glossary.md): the terms of the application.
- [Definition format](reference/definition-format.md): every field of a definition.
- [Definition format: sections](reference/definition-format-sections.md): the nested sections of a definition.
- [Site Hygiene checks](reference/site-hygiene-checks.md): the checks and the scan areas.
- [Shipped content](reference/shipped-content.md): the templates, plans, catalog entries and library files that ship with the application.

## External references

- [External references](external-references.md): Microsoft Learn pages and other official sources for each feature.

## Troubleshooting

- [Troubleshooting](troubleshooting.md): the most common messages with cause and fix.
