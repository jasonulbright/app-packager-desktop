# Site Hygiene checks and scan areas

The Site Hygiene page runs 57 checks in 18 scan areas. This page lists every check and every area, as the code defines them. For the steps of a scan and a fix, see [Site Hygiene](../tools/site-hygiene.md).

## When to use this page

- Use it to learn what a check finds before you scan.
- Use it to see which findings the page can fix and which are report only.
- Use it to choose scan areas on the **Options** tab.

## How to read the tables

- **Severity** is the severity in the catalog: Error, Warning or Info. A few checks give another severity for some findings. The text in the **Finds** column names these cases.
- **Fix** is **Yes** when the page can fix the finding. It is **No** when the **Fix** column of the page shows **Report only**. The finding then shows guidance for the ConfigMgr console.
- **Fix kind** is the label that the **Fix** column of the page shows.
- The numbers in the **Finds** column are thresholds in the code. They are fixed. The page has no control to change them.

| Threshold | Value | Used by |
|---|---|---|
| Unused application age | 30 days | APP-01 |
| Incremental evaluation ceiling | 200 collections | COL-03 |
| Slow collection evaluation | 5000 ms | COL-04 |
| Include/exclude chain depth | more than 3 levels | COL-07 |
| Full evaluation hot spot | 10 collections in one start hour | COL-09 |
| Content in progress | 2 days | CNT-02 |
| Deadline grace | 7 days | DPL-02 |
| Failure rate | 20 percent | DPL-02 |
| Available deployment age | 30 days | DPL-03 |
| Inactive device | 90 days | DEV-01 |
| Expired update rate | 30 percent | UPD-01 |
| Updates in one group | more than 1000 | UPD-04 |
| Stale deployment rule | 45 days | UPD-03 |
| Content source probe | 3 seconds for each path, at most 200 paths | APP-04 |

## The 57 checks

| ID | Category | Severity | Title | Finds | Fix | Fix kind |
|---|---|---|---|---|---|---|
| APP-01 | Applications | Warning | Application with no deployments or references | An application with no deployment, no supersedence relationship, no dependency on it, no task sequence use, and older than 30 days. | Yes | Delete application |
| APP-02 | Applications | Error | Retired application still deployed | A retired application that still has deployments. | Yes | Delete deployment |
| APP-03 | Applications | Warning | Superseded application still deployed | A superseded application, not retired, whose own deployments are active. | Yes | Delete deployment |
| APP-04 | Applications | Warning | Deployment type content source missing or unreachable | A content source path of the latest deployment type that this computer cannot reach. Info when the path did not answer in 3 seconds. Runs only when the content source probe is on. The check does not run when a path cannot be probed safely or within the limit of 200 paths. | No | Report only |
| APP-05 | Applications | Info | Application with old revisions | An application with one or more old revisions. | Yes | Delete old revisions |
| PKG-01 | Packages | Warning | Package with no programs and no references | A standard package with no program, no deployment and no task sequence reference. Packages named Configuration Manager Client are skipped. | Yes | Delete package |
| CNT-01 | Content | Warning | Content failed on one or more distribution points | Content with an error on at least one targeted distribution point. | No | Report only |
| CNT-02 | Content | Info | Content distribution in progress beyond threshold | Content with distribution in progress and no error, last updated more than 2 days ago. | No | Report only |
| CNT-03 | Content | Error | Deployed content on no distribution point | Content with source files, an active deployment, and no targeted distribution point. | No | Report only |
| SUP-01 | Relationships | Error | Supersedence referencing a deleted application | A supersedence that points to an application that no longer exists. | No | Report only |
| SUP-02 | Relationships | Error | Circular supersedence chain | A supersedence edge that is part of a loop. | No | Report only |
| SUP-03 | Relationships | Warning | Superseding application disabled | A supersedence whose superseding application is disabled. | No | Report only |
| SUP-04 | Relationships | Warning | Supersedence target retired or expired | A supersedence whose superseded application is retired. | No | Report only |
| DEP-01 | Relationships | Error | Dependency referencing a deleted application | A dependency that points to an application that no longer exists. | No | Report only |
| DEP-02 | Relationships | Error | Circular dependency | A dependency edge that is part of a loop. | No | Report only |
| DEP-03 | Relationships | Warning | Dependency target disabled | A dependency on a disabled application. | No | Report only |
| DEP-04 | Relationships | Warning | Dependency target retired or expired | A dependency on a retired application. | No | Report only |
| DEP-05 | Relationships | Info | Dependency target reports no packaged content | A dependency on an application that reports no content. A script deployment type can be valid without content. | No | Report only |
| REL-01 | Relationships | Info | Application relationships without manufacturer metadata | An application in a supersedence or dependency relationship with an empty Manufacturer. | No | Report only |
| DEV-01 | Devices | Warning | Inactive devices beyond threshold | A client record last active more than 90 days ago, with no heartbeat inside that window. Info when a discovery cleanup task is enabled. | Yes | Delete device |
| DEV-02 | Devices | Warning | Duplicate device records | Records that share a name, or records with different names that share an SMBIOS GUID. | No | Report only |
| DEV-03 | Devices | Info | Clients below the newest client version | One finding for the site: clients that run a version below the newest version seen. | No | Report only |
| BND-01 | Boundaries | Warning | Boundary in no boundary group | A boundary that belongs to no boundary group. | No | Report only |
| BND-02 | Boundaries | Warning | Boundary group with no site systems | A boundary group that references no site system. | No | Report only |
| BND-03 | Boundaries | Info | Overlapping IP-range boundaries | Two IP-range boundaries that overlap. | No | Report only |
| TSQ-01 | Task Sequences | Error | Task sequence referencing deleted content | A task sequence with a reference to an ID that no longer exists. | No | Report only |
| TSQ-02 | Task Sequences | Warning | Boot image or driver package referenced by nothing | A custom boot image or a driver package that no task sequence references. Default boot images are skipped. | Yes, with a condition | Delete boot image; Delete driver package |
| TSQ-03 | Task Sequences | Warning | Application in a task sequence without the task sequence install setting | An application that a task sequence installs and that lacks the setting "Allow this application to be installed from the Install Application task sequence action without being deployed". | Yes | Change application |
| TSQ-04 | Task Sequences | Error | Application in a task sequence with content on no distribution point | An application that a task sequence installs, with source files and no targeted distribution point. | No | Report only |
| UPD-01 | Updates | Warning | Update group with high expired-update ratio | A software update group in which 30 percent or more of the updates are expired. | No | Report only |
| UPD-03 | Updates | Warning | Automatic deployment rule disabled, stale, or erroring | Error: the last run ended with an error code. Info: the rule is disabled. Warning: the rule is enabled and last ran more than 45 days ago. | Yes, for Info and Warning | Delete deployment rule (disabled); Run deployment rule (stale) |
| UPD-04 | Updates | Warning | Update group over the per-deployment update limit | A software update group with more than 1000 updates. | No | Report only |
| UPD-05 | Updates | Info | Deployment package holding expired update content | A deployment package with content that belongs to expired updates. | No | Report only |
| MNT-01 | Site | Info | Recommended maintenance tasks disabled | A disabled task from this list: Delete Aged Inventory History, Delete Aged Discovery Data, Delete Obsolete Client Discovery Data, Delete Aged Client Operations, Delete Aged Log Data. | Yes | Change maintenance task |
| MNT-02 | Site | Warning | Site backup task disabled | The task Backup SMS Site Server (or Backup Site Server) is disabled. | Yes | Change maintenance task |
| COL-01 | Collections | Info | Empty collection nothing references | An empty custom collection with no deployment, no include/exclude reference, no use as a limiting collection and no variable. | Yes, with a condition | Delete collection |
| COL-02 | Collections | Warning | Deployment targeting an empty collection | A deployment to a collection with zero members. | No | Report only |
| COL-03 | Collections | Warning | Incremental-evaluation collection count over ceiling | One finding for the site when more than 200 collections use incremental evaluation. | No | Report only |
| COL-04 | Collections | Warning | Collection evaluation run time over threshold | A collection whose last full or incremental evaluation took longer than 5.0 seconds. | No | Report only |
| COL-05 | Collections | Warning | Sub-daily full evaluation on an incremental collection | A collection with incremental updates and a full evaluation every few minutes or hours. | Yes | Change refresh type |
| COL-06 | Collections | Info | Scheduled evaluation on a direct-rule-only collection | A scheduled collection with direct rules only, no include or exclude rule, and a limiting collection that is not incremental. | Yes | Change refresh type |
| COL-07 | Collections | Warning | Include/exclude reference chain over depth threshold | A collection with an include or exclude chain deeper than 3 levels. | No | Report only |
| COL-08 | Collections | Error | Circular collection reference | A limiting, include or exclude reference that is part of a loop. | No | Report only |
| COL-09 | Collections | Info | Full-update start-time hot spot | 10 or more collections that start their scheduled full evaluation in the same hour. | No | Report only |
| DPL-01 | Deployments | Info | Deployment past its expiration time | An application deployment with an expiration time in the past. | Yes | Delete deployment |
| DPL-02 | Deployments | Error | Required deployment past deadline with high failures | A Required deployment whose deadline passed at least 7 days ago and with 20 percent or more of the targeted clients in error. | No | Report only |
| COL-10 | Collections | Info | Expired one-time maintenance window | A one-time maintenance window that ended more than one day ago. | Yes | Delete maintenance window |
| DPL-03 | Deployments | Info | Available deployment with no takers | An Available deployment created at least 30 days ago, with targeted clients, no success and nothing in progress. | No | Report only |
| DPL-04 | Deployments | Warning | Required deployment to a built-in all-resources collection | A Required deployment to All Systems, All Users, All Desktop and Server Clients or a similar built-in collection. | No | Report only |
| DPL-05 | Deployments | Warning | Deployment of a disabled task sequence or program | A deployment of a task sequence or a program that is disabled. | No | Report only |
| DPT-01 | Distribution Points | Warning | Distribution point in no boundary group | A distribution point that is a site system of no boundary group. | No | Report only |
| DPT-02 | Distribution Points | Info | Distribution point group with no members | A distribution point group with no member. | No | Report only |
| CFG-01 | Compliance | Info | Configuration baseline deployed nowhere | A user-defined baseline with no deployment that no other baseline uses. | No | Report only |
| CFG-02 | Compliance | Info | Configuration item in no baseline | A user-defined configuration item that no baseline references. | No | Report only |
| CFG-03 | Compliance | Info | Custom client settings deployed to no collection | A custom client settings object with no deployment. | No | Report only |
| DRV-01 | Drivers | Info | Driver in no driver package or boot image | A driver in the catalog that is in no driver package and no boot image. | No | Report only |
| SEC-01 | Security | Warning | Administrative user with a deleted directory account | An administrative user whose Active Directory account the site reports as deleted. | No | Report only |

The catalog holds 16 checks with a fix on this page and 41 report-only checks. The catalog has no check UPD-02. The order of the rows follows the catalog, except that UPD-04 and UPD-05 follow UPD-03.

## Conditions of a fix

A finding with a fix can still show **Report only**. The scan reads these cases and the page then gives the reason:

- COL-01: a collection that an automatic deployment rule, a client settings deployment, or a deployment of an application, a software update, a baseline or a policy targets.
- TSQ-02 for a driver package: a task sequence has an Auto Apply Drivers step, or the steps of the task sequences could not be read.
- UPD-03 in the Error case: the page does not fix a rule that ended with an error.

Each fix kind is a typed operation with exact object identifiers. The page re-reads the object on fresh data when it prepares the plan, and again immediately before each write. If the finding no longer holds, the page skips the object and gives the reason. The plan shows the equivalent PowerShell as read-only text.

| Fix kind | Command in the plan text | Checks | The page skips the object when |
|---|---|---|---|
| Delete application | `Remove-CMApplication` | APP-01 | The application has a deployment now; the site reports it as deployed; it is in a supersedence; another application depends on it; a task sequence uses it; it is younger than the age threshold; the task sequence steps could not be read. |
| Delete deployment | `Remove-CMApplicationDeployment` | APP-02, APP-03, DPL-01 | The application is not retired (APP-02); it is not superseded or it is retired (APP-03); the deployment has no past expiration time (DPL-01). |
| Delete old revisions | `Remove-CMApplicationRevisionHistory` | APP-05 | None of the listed old revisions exists now. |
| Delete package | `Remove-CMPackage` | PKG-01 | The package is a Configuration Manager client package; has a program or a deployment; a task sequence uses it. |
| Delete collection | `Remove-CMCollection` | COL-01 | The collection is built in; has members; has a deployment; is included, excluded or used as a limit; has variables or maintenance windows; or an administrative user, rule or client settings deployment uses it. |
| Delete device | `Remove-CMDevice` | DEV-01 | The record has no client; the client was active, or sent a heartbeat, inside the inactivity window. |
| Delete boot image | `Remove-CMBootImage` | TSQ-02 | The boot image is a default boot image; a task sequence references it. |
| Delete driver package | `Remove-CMDriverPackage` | TSQ-02 | A task sequence references it; a task sequence has an Auto Apply Drivers step. |
| Delete deployment rule | `Remove-CMSoftwareUpdateAutoDeploymentRule` | UPD-03 | The rule is enabled; its last run ended with an error. |
| Delete maintenance window | a write to `SMS_CollectionSettings.ServiceWindows` | COL-10 | The window recurs; it has not ended a day ago. |
| Change refresh type | `Set-CMCollection` | COL-05 (Continuous), COL-06 (Manual) | The collection no longer matches the case. COL-06 also skips a collection with include or exclude rules or an incremental limiting collection. |
| Change application | `Set-CMApplication` | TSQ-03 | The application has the setting now; no task sequence installs it. The change makes a new revision. |
| Change maintenance task | `Set-CMSiteMaintenanceTask` | MNT-01, MNT-02 | The task is enabled now. MNT-01 enables only the recommended cleanup tasks. MNT-02 enables only the site backup task. |
| Run deployment rule | `Invoke-CMSoftwareUpdateAutoDeploymentRule` | UPD-03 | The rule is disabled; its last run ended with an error; it ran recently. |

The first ten fix kinds delete objects. A delete needs the second confirmation box. The service enforces the box.

## The 18 scan areas

The **Options** tab lists the areas. Each area is checked by default. A check runs when at least one area that covers it is selected.

| Area ID | Title in **Options** | Checks |
|---|---|---|
| Applications | Applications | APP-01, APP-02, APP-03, APP-05 |
| AppRelationships | Application relationships and content paths | SUP-01 to SUP-04, DEP-01 to DEP-05, REL-01, APP-04 |
| Content | Content distribution | CNT-01, CNT-02, CNT-03 |
| Packages | Packages | PKG-01 |
| Collections | Collections | COL-01, COL-02, COL-03, COL-04, COL-07, COL-08 |
| CollectionSchedules | Collection evaluation schedules | COL-05, COL-06, COL-09, COL-07, COL-08 |
| Deployments | Deployments | DPL-01 to DPL-05 |
| Devices | Devices | DEV-01, DEV-02, DEV-03 |
| Boundaries | Boundaries | BND-01, BND-02, BND-03 |
| TaskSequences | Task sequences | TSQ-01 to TSQ-04 |
| Updates | Software updates | UPD-01, UPD-03, UPD-04 |
| UpdateContent | Software update package content | UPD-05 |
| DistributionPoints | Distribution points | DPT-01, DPT-02 |
| Compliance | Compliance and client settings | CFG-01, CFG-02, CFG-03 |
| Drivers | Drivers | DRV-01 |
| Security | Administrative users | SEC-01 |
| MaintenanceWindows | Maintenance windows | COL-10 |
| Site | Site maintenance | MNT-01, MNT-02 |

COL-07 and COL-08 run when either **Collections** or **Collection evaluation schedules** is selected.

## States of a check

The **Summary** tab shows one state for each check.

| State | Meaning |
|---|---|
| **Ran** | The check ran. The finding count is exact. |
| **Not selected** | No selected area covers the check, or an option leaves it out. APP-04 shows this state when the content source probe is off. |
| **Not run** | A read that the check needs failed or was cut. The check never reports "no findings" in this state. |
| **Failed** | The check stopped with an error. Its findings are missing. |
| **Needs the site database** | The page can show this state. No check of the catalog uses it. |

A read is cut at 200,000 rows. A read that costs one provider call for each object stops at 2000 objects. The reads of this kind are the collection details (COL-05, COL-06, COL-09), the maintenance windows (COL-10), the application definitions that a task sequence installs (TSQ-03) and the task sequence steps (APP-01, PKG-01, TSQ-01 to TSQ-04, and the driver package fix). A check whose read was cut does not run. A scan that you limit to some areas does not become the stored last scan for the **New** column.

## Limits

- Dates and times are compared with the clock of this computer. The provider returns its times without conversion.
- A fix deletes or changes objects on the site. Most deletes cannot be undone.
- The lab ran the page on one site (CM01). No lab record states which of the 57 checks found an object on that site.

## Troubleshooting

| Message | Cause | Fix |
|---|---|---|
| The check did not run: ... Running it over incomplete data would report findings that are not true. | A read that the check needs failed or was cut. | Read the reason in the **Summary** tab. Scan fewer areas, or fix the read. |
| The content source probe is off. Switch it on in Options to probe each path over the network. | APP-04 needs the probe. | Check the probe box on the **Options** tab. |
| The scan did not cover the area ... | The area is not selected. | Select the area on the **Options** tab. |

## Links

- [Reference for maintenance tasks in Configuration Manager](https://learn.microsoft.com/intune/configmgr/core/servers/manage/reference-for-maintenance-tasks): Lists each maintenance task with its settings.
- [Use the status system in Configuration Manager](https://learn.microsoft.com/intune/configmgr/core/servers/manage/use-status-system): Explains the status system and the status messages.
- [Client health checks](https://learn.microsoft.com/intune/configmgr/core/clients/manage/client-health-checks): Lists the checks of the client health evaluation.
- [Revise and supersede applications in Configuration Manager](https://learn.microsoft.com/intune/configmgr/apps/deploy-use/revise-and-supersede-applications): Explains application revisions and supersedence relationships.
- [SMS_Application Server WMI Class](https://learn.microsoft.com/intune/configmgr/develop/reference/apps/sms_application-server-wmi-class): Describes the SMS_Application class.
- [SMS_DistributionPointInfo server WMI class](https://learn.microsoft.com/intune/configmgr/develop/reference/core/servers/configure/sms_distributionpointinfo-server-wmi-class): Describes the SMS_DistributionPointInfo class.
- [Plan for the SMS Provider](https://learn.microsoft.com/intune/configmgr/core/plan-design/hierarchy/plan-for-the-sms-provider): Explains the SMS Provider role.
