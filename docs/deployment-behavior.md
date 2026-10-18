# Deployment behavior

## ConfigMgr test deployments

In **Settings**, enable automatic distribution, select a distribution point group, and enable deployment to a test collection. Enter the exact collection name. **Use a user collection** selects user collections. When it is cleared, App Packager Desktop uses device collections. This choice does not change the install context of the application or the hive of its detection rule.

App Packager Desktop finds the collection by its literal name in the selected collection type. Wildcard matching is off. The deployment uses the ID of the collection that it finds.

- A duplicate name or a lookup error stops the test-deployment step.
- When collection creation is off, a missing collection is reported as skipped.
- When collection creation is on, App Packager Desktop creates an empty collection. A user collection is limited to All Users (`SMS00002`). A device collection is limited to All Systems (`SMS00001`).
- App Packager Desktop does not add users or devices to a collection. Add the members in ConfigMgr.

The deployment is Available and effective immediately. A successful distribution request means that ConfigMgr accepted the content. App Packager Desktop does not wait for the transfer to the distribution points and does not confirm the installation on clients. The distribution step and the test-deployment step report their own messages. These messages do not prove success on an endpoint. One Click plans and reports show the selected collection type.

Microsoft references: [resolve a collection by type and literal name](https://learn.microsoft.com/en-us/powershell/module/configurationmanager/get-cmcollection?view=sccm-ps), [create a user collection](https://learn.microsoft.com/en-us/powershell/module/configurationmanager/new-cmusercollection?view=sccm-ps), [create an application deployment](https://learn.microsoft.com/en-us/powershell/module/configurationmanager/new-cmapplicationdeployment?view=sccm-ps).

## Intune replacement publication

App Packager Desktop first looks for the Win32 app that carries the identity tag of the application and profile. When no app carries the tag, it reads all result pages and adopts one untagged Win32 app with the exact display name. When more than one app matches, it refuses the publication.

For an existing app, App Packager Desktop uploads the encrypted content and waits until the content commit succeeds. Then it sends one app update with the new metadata and `committedContentVersion`.

- An upload failure, a commit failure or a cancel before that update does not change the metadata or the active content version of the existing app.
- A new app needs an app object before the content upload. A failed first publication can leave that unpublished app object, or an unused content version, in Intune.
- A connection failure during the final update leaves the result on the server unknown. Check the app in Intune.

A storage error message shows the endpoint path and the HTTP status. It does not show the SAS query credentials or the response body. App Packager Desktop does not create Intune assignments.

Microsoft references: [Graph paging](https://learn.microsoft.com/en-us/graph/paging), [update a Win32 app](https://learn.microsoft.com/en-us/graph/api/intune-apps-win32lobapp-update?view=graph-rest-1.0), [content-file state](https://learn.microsoft.com/en-us/graph/api/resources/intune-apps-mobileappcontentfile?view=graph-rest-1.0).

## WSUS results after publication

When WSUS has accepted a package and the lookup that follows fails, the publication is successful with a warning. Approval results and decline results are reported separately. In a batch action, each update keeps its own result when the lookup of another update fails. App Packager Desktop continues with the remaining updates.

`AppPackagerBuild.exe` returns exit code 1 when a successful WSUS publication has a warning. Read the WSUS result and the package ID in its output before you try again. A published update is not proof of approval or of installation on a client. The client must trust the signing certificate of the update, and the WSUS client policy must allow locally published updates. See [Microsoft's certificate guidance](https://learn.microsoft.com/en-us/intune/configmgr/sum/tools/updates-publisher-security).

## Test coverage

Automated tests cover simulated Graph requests, and generated PowerShell that runs under Windows PowerShell 5.1 against stand-in ConfigMgr and WSUS operations. These tests do not replace acceptance tests on live servers and clients.
