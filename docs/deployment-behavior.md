# Deployment behavior

Updated October 9, 2026. These notes describe the source tree, including unreleased changes.

## ConfigMgr test deployments

In Settings, enable automatic distribution, select a distribution point group, and enable deployment to a test collection. Enter the exact collection name. **Use a user collection** selects user collections; the default selects device collections. This choice does not change the application's installation behavior or detection hive.

APD resolves that name within the selected collection type, with wildcard matching disabled, and uses the resolved collection ID for the deployment. Duplicate names and lookup errors stop the test-deployment step. With creation disabled, a missing collection is reported as skipped. With creation enabled, APD creates an empty collection limited to All Users (`SMS00002`) or All Systems (`SMS00001`), as appropriate. The operator adds membership in ConfigMgr; APD does not add users or devices.

The deployment is Available, effective immediately. A successful distribution request means that ConfigMgr accepted content targeting; APD does not wait for distribution-point transfer completion or confirm client installation. Optional distribution and test-deployment messages belong to their own steps and do not establish endpoint success. One Click plans and reports include the selected collection type.

Microsoft references: [resolve a collection by type and literal name](https://learn.microsoft.com/en-us/powershell/module/configurationmanager/get-cmcollection?view=sccm-ps), [create a user collection](https://learn.microsoft.com/en-us/powershell/module/configurationmanager/new-cmusercollection?view=sccm-ps), [create an application deployment](https://learn.microsoft.com/en-us/powershell/module/configurationmanager/new-cmapplicationdeployment?view=sccm-ps).

## Intune replacement publication

APD matches an application/profile identity tag first. Its fallback can adopt one untagged Win32 app with the exact display name, after reading all result pages; ambiguity refuses the publication.

For an existing app, APD uploads the encrypted payload and waits for successful content commit before sending a single app update containing the replacement metadata and `committedContentVersion`. An upload failure, commit failure or cancellation before that update leaves the existing app's metadata and active content version untouched. Creating a new app still requires an app object before content can be uploaded; a failed first publication can leave that unpublished object or an unused content version for inspection. A connection failure during the final update leaves the server outcome uncertain and requires checking Intune.

Storage-error messages include the endpoint path and HTTP status, excluding SAS query credentials and response bodies that might echo them. APD does not create Intune assignments.

Microsoft references: [Graph paging](https://learn.microsoft.com/en-us/graph/paging), [update a Win32 app](https://learn.microsoft.com/en-us/graph/api/intune-apps-win32lobapp-update?view=graph-rest-1.0), [content-file state](https://learn.microsoft.com/en-us/graph/api/resources/intune-apps-mobileappcontentfile?view=graph-rest-1.0).

## WSUS results after publication

Once WSUS has accepted a package, a subsequent visibility lookup failure is reported as a warning on that successful publication. Approval and decline results remain separate. In a batch action, each update retains its result even when another update cannot be looked up; later updates are still attempted.

The CLI returns exit code 1 when a successful WSUS publication has warnings. Inspect its structured WSUS result and package ID before retrying. Publishing an update does not prove approval or client installation. Client trust and policy prerequisites still apply; see [Microsoft's certificate guidance](https://learn.microsoft.com/en-us/intune/configmgr/sum/tools/updates-publisher-security).

## Validation

The October 9 changes were tested on Windows with .NET 10: the full Release suite passed 3,451 tests. Coverage includes simulated Graph requests and generated PowerShell running under Windows PowerShell 5.1 with stand-in ConfigMgr and WSUS operations. These tests do not replace live server, client, or desktop UI acceptance. Earlier lab validation remains documented separately; this review did not deploy software or modify live management systems.
