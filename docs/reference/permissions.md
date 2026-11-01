# Permissions reference

This page lists the ConfigMgr rights and the local rights that each page and step needs.

## Verification status

Every lab run used the account `CONTOSO\labadmin` with the ConfigMgr role Full Administrator. No test used a custom or least-privilege security role. The minimum rights are therefore not verified with a custom role.

The ConfigMgr column below quotes the rights that the code names in its own access messages. The app shows these messages when the SMS Provider denies access. The messages are not a tested minimum.

## Rights per page

| Page or step | ConfigMgr rights (text of the code) | Local and other rights |
|---|---|---|
| [Packages](../core/packages.md): `Publish to ConfigMgr`, `Check ConfigMgr` | The code names no right. On access denied, it says the account needs "a ConfigMgr security role for site <code>". The README says "rights to create applications". | The ConfigMgr console on this computer (the variable `SMS_ADMIN_UI_PATH` and the PowerShell module file). Windows PowerShell 5.1. Write access to `FileShareRoot`. Write access to `DownloadRoot`. |
| [Stage and publish](../core/stage-and-publish.md): `Publish to Intune` | Not applicable. | An Entra app registration with the application permission `DeviceManagementApps.ReadWrite.All`, with admin consent. A client secret. |
| [WSUS publishing](../core/wsus-publishing.md): `Publish to WSUS` | Not applicable. | The WSUS console or the RSAT WSUS tools, at the version of the server. Membership of the WSUS Administrators group on the server. File-share access to the server. Clients must trust the signing certificate and allow the policy for signed updates from an intranet update service location. |
| [Discover unmanaged software](../core/discover-unmanaged-software.md) | Read access through the SMS Provider. | The role `db_datareader` on the database `CM_<site code>`. A domain account on a domain-joined computer. |
| [Site Hygiene Live](../tools/site-hygiene-live.md): Client Health and Inactive Devices views | Read access through the SMS Provider. | The role `db_datareader` on the site database (Windows authentication). |
| [Maintenance Windows](../tools/maintenance-windows.md) | "the Modify Collection Setting permission on the collection" | The ConfigMgr console. |
| [Collection and Compliance](../tools/collection-and-compliance.md): Collections | "rights on collections and queries (Read, Modify, Create and Delete on Collection and Query)" | The ConfigMgr console. |
| [Compliance](../tools/compliance.md) | "Compliance Settings Manager rights (configuration items, baselines and their deployments) and read rights on collections" | The ConfigMgr console. The builder runs in Windows PowerShell 5.1. |
| [Deployment Helper](../tools/deployment-helper.md) and [Rings](../tools/deployment-rings.md) | "deployment rights for the object type, distribute content rights and read rights on collections" | The ConfigMgr console. |
| [DP Content Manager](../tools/dp-content-manager.md) | "read rights on distribution points and content, and rights to distribute and remove content" | The ConfigMgr console. |
| [Pivots and Scripts](../tools/pivots-and-scripts.md): CMPivot | "Run CMPivot on the collection and Read on Inventory Reports" | The ConfigMgr console. The provider certificate must be trusted, and its name must match the provider name in Settings. The clients must have CMPivot enabled. |
| [Pivots and Scripts](../tools/pivots-and-scripts.md): scripts | "Run Script on the collection, and Create, Approve or Delete on SMS Scripts for the script actions" | The ConfigMgr console. The clients must have Run Scripts enabled. |
| [Site Hygiene](../tools/site-hygiene.md): scan and fixes | "read rights on the objects the scan reads, and for a fix the rights to delete or change the object" | The ConfigMgr console. |
| [Detection Tester](../tools/detection-tester.md) | None. | The current user. The page reads HKLM, HKCU and files. A script runs as the account that started the app, not as SYSTEM. |
| [Installer Analysis](../tools/installer-analysis.md) | None. | The current user. Extraction needs 7-Zip 25.01 or newer (optional). |
| [Script signing](../core/script-signing.md) | None. | Access to the private key of the signing certificate. A key on a hardware token can need a PIN and can stop unattended runs. |

## Elevation

No code in the app requires an elevated session. An unelevated run works with the ConfigMgr session (verified in a DP Content Manager lab run).

Explorer drag and drop cannot reach an elevated app. Do not run the app elevated when you use drag and drop to add an installer.

## Related pages

- [Network calls](network.md) lists the hosts and ports that each step uses.
- [Error messages](error-messages.md) lists the access-denied messages and their fixes.

## Links

- [Fundamentals of role-based administration for Configuration Manager](https://learn.microsoft.com/intune/configmgr/core/understand/fundamentals-of-role-based-administration): Explains security roles, security scopes and collections for administrative users.
- [Configure role-based administration for Configuration Manager](https://learn.microsoft.com/intune/configmgr/core/servers/deploy/configure/configure-role-based-administration): Shows how to set up roles, scopes and administrative users.
- [Create and run PowerShell scripts from the Configuration Manager console](https://learn.microsoft.com/intune/configmgr/apps/deploy-use/create-deploy-scripts): Explains script authors, approvers and runners, and how to run a script on a collection.
- [Microsoft Graph permissions reference](https://learn.microsoft.com/graph/permissions-reference): Lists the Graph permissions that each API needs.
