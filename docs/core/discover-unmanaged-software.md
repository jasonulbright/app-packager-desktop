# Discover unmanaged software

## What it does

**Discover unmanaged software** finds applications that your clients have installed and that ConfigMgr does not manage. It reads the Add/Remove Programs inventory in the ConfigMgr site database. It offers an application when the inventory has its name (and its version, when the definition names one) and the site has no ConfigMgr application for it.

An application that you accept appears in the App Packager grid. You can then use **Check Latest**, **Check ConfigMgr**, **Stage** and the publish buttons on it.

Discover reads application names and versions only. It reads no device name, no device identifier, no device count and no publisher. It shows no count of devices.

## When to use

- Use Discover to find the software that the inventory lists and that nobody has packaged for ConfigMgr yet.
- Use it after hardware inventory ran on new clients.
- Discover considers only applications that are hidden in the [Catalog](applications.md). Hide the applications that you want Discover to consider.

## Requirements

- ConfigMgr selected under **Systems in Use** in Settings.
- A **Site Code** in **ConfigMgr Site Settings**, or both **Discovery SQL Server** and **Discovery Database**. The default Site Code is `MCM`. Save your own Site Code before you use Discover.
- Windows PowerShell 5.1. Discover runs its query through `powershell.exe`.
- A domain account on a domain-joined computer. A computer outside the domain cannot sign in to the database.
- The role `db_datareader` on the site database `CM_<site code>` for that account. Discover uses Windows authentication only.
- Hardware inventory that collects the Add/Remove Programs classes.
- The ConfigMgr console is not needed. Discover reads the database over SQL.

The rights of each page are in [Permissions](../reference/permissions.md).

The database views bypass the role-based access of ConfigMgr. The scan reads the inventory of all devices that are not marked obsolete or decommissioned, whatever scope your console role has.

## Steps

### Set up Discover

1. Select **Settings** in the sidebar.
2. Go to **ConfigMgr Site Settings**.
3. Check **Site Code** and **Site Server / SMS Provider Host**.
4. Optional: enter **Discovery SQL Server**. Enter the SQL Server instance of the site database, for example `sqlserver.example.com` or `server\instance`.
5. Optional: enter **Discovery Database**, for example `CM_MCM`.
6. Select **Save Settings**.

With both optional boxes empty, Discover reads the SQL Server name and the database name from the SMS Provider. It uses DCOM first and WinRM second. When you fill both boxes, Discover needs no site code and no provider. When a site code is missing, fill both boxes.

![The ConfigMgr Site Settings section with the Discovery fields](../images/core-discover-settings.png)

The **Discovery SQL Server** box takes `name`, `name,port` or `name\instance`. **Encrypt the connection** asks SQL Server for an encrypted connection. **Trust server certificate** accepts a certificate that this computer does not trust (self-signed, or issued by an unknown authority); it is the same option as in Site Hygiene, Live, and it is stored for the site. **Test connection** connects with the values on the page, reads one row of the inventory views and shows the result or the error under the buttons. When the **Discovery Database** box is empty, Discover reads the database name from the site definition, and uses `CM_<site code>` when the definition has none.

### Find the applications

1. Select **App Packager** in the sidebar. Select **Catalog**. Clear the check box of each application that Discover must consider. Select **Save**.
2. Close the Catalog.
3. Select **Settings**, then **ConfigMgr Site Settings**. Select **Discover not selected** to scan only the applications that you cleared in the Catalog and did not dismiss. Select **Discover all** to scan every application, also the applications that you selected or dismissed.
4. Wait for the scan. The text under the two buttons shows `Reading Add/Remove Programs inventory from the ConfigMgr site database...`. Select **Cancel** on the App Packager page to stop the scan.
5. View the text under the buttons and the log. They show the number of applications that Discover found, for example `Discovered 3 apps.`, or the error that ended the scan.
6. If Discover found applications, read the prompt. The prompt title is **Discover unmanaged software**. The text is `Discovered <N> apps. Enable console view?`.
7. Check the applications in the list. The columns are **Application** and **Vendor**, with a check box in front. The list is sorted by the ConfigMgr name of the application. Press Space to check or clear the selected row.

![The Discover unmanaged software prompt with the list of applications](../images/core-discover-prompt.png)

8. Select one of the four buttons.

| Button | Effect |
|---|---|
| **Enable selected** | Shows the checked applications in the App Packager grid. |
| **Enable all** | Shows every application of the list in the App Packager grid. |
| **Not now** | Closes the prompt and changes nothing. The next scan offers the applications again. |
| **Don't ask again for selected** | Keeps the checked applications hidden. Later scans leave them out. |

When you select **Enable selected** or **Don't ask again for selected** and no row is checked, the prompt stays open and changes nothing. The note line shows `Check at least one app, or select Enable all.` or `Check the apps to leave out of later discoveries. Nothing changed.`

After **Enable selected** or **Enable all**, the log shows `<N> app(s) added to the grid. Show or hide them with Catalog on the App Packager page.` Enabling an application only shows its row. Select **Check ConfigMgr** to see its real state.

### Offer the dismissed applications again

1. Select **Settings** in the sidebar.
2. Go to **ConfigMgr Site Settings**.
3. Read **Not offered by Discover**. It shows `<N> app(s)`.
4. Select **Clear list**. The text changes to `0 app(s) after Save`.
5. Select **Save Settings**.

## How the match works

- A definition names its Add/Remove Programs entry with `ArpName`. The value is one or more wildcard patterns, separated by ` | `. The value `none` means that the application has no stable entry. The optional `ArpVersion` and `ArpArch` fields narrow the match by version and by architecture. 299 of the 323 shipped definitions carry a pattern.
- Discover reads these database views: `v_GS_ADD_REMOVE_PROGRAMS`, `v_GS_ADD_REMOVE_PROGRAMS_64`, `v_R_System`, `v_Applications` and `v_ConfigurationItems`. It reads two columns of the inventory, the display name and the version. It ignores obsolete and decommissioned clients.
- Some applications share one name with a sibling: Citrix Workspace CR and LTSR, QGIS and QGIS LTR, NVIDIA GeForce and RTX Enterprise, and the Microsoft 365 Apps x64 and x86 editions with Project and Visio. Discover cannot tell which variant a client runs. It offers one application of each group and never offers its siblings.
- An application counts as managed when a current ConfigMgr application title starts with its ConfigMgr name, and the rest is a version or a detail in parentheses. A title with another installer method (EXE or MSI) or another architecture does not count. **Check ConfigMgr** uses the same rule.
- Discover offers an application only when it is hidden, not dismissed, has an `ArpName` other than `none`, and matches at least one inventory row (name, and version and architecture when the definition has them).

## Options and settings

| Setting | Default | Effect |
|---|---|---|
| **Discovery SQL Server** | empty | Empty means the SQL Server of the site, read from the SMS Provider. |
| **Discovery Database** | empty | Empty means the database of the site, `CM_<site code>`. |
| **Not offered by Discover** | empty | The applications that Discover no longer offers. **Clear list** empties it when you select **Save Settings**. |

Discover also changes two lists in `settings.json`: `DiscoveryDismissed` and `DiscoveryEnabled`. The settings file stores no credential. See [Settings](../reference/settings.md).

The SMS Provider for the site lookup comes from these places, in this order: **Site Server / SMS Provider Host**; the environment variable `SUITE_CM_PROVIDER`; the CMSite drive that has the name of the site code; the first CMSite drive.

## Limits

- A scan stops after 600 seconds, when you select **Cancel**, and when you close the main window. The connection to SQL Server has a limit of 30 seconds.
- Discover sends no request to the internet. It connects to the SMS Provider (CIM over DCOM, then WinRM) and to the site database (SQL Server, Windows authentication, read-only).
- Discover reads inventory that clients have reported. When the inventory has no rows, it reports `No inventory data`.
- Discover offers one application of each sibling group. See above.
- Discover reads the inventory of all devices that are not marked obsolete or decommissioned, not only the devices in your security scope. The result holds no device identity.

## Troubleshooting

All messages in the log start with `Discover unmanaged software:`. The same text shows under the two buttons in Settings and in the status line of the App Packager page.

A failure message names what was tried and what failed. A database error ends with `Server <s>, database <db>. Error: <type> <number>: <text>`, where `<type>` is the exception type, `<number>` the SQL Server error number and `<text>` the text of the error, for example `The certificate chain was issued by an authority that is not trusted.` The text before it says what to check. A failed lookup of the SMS Provider names the failure of each protocol (DCOM and WinRM).

| Message | Cause | Fix |
|---|---|---|
| The button is disabled. The tooltip reads `ConfigMgr is not in use...` or `Set the site code, or the SQL server and database, in Settings, ConfigMgr Site Settings.` | ConfigMgr is not in use, or the site code and the SQL boxes are empty. | Fix the setting that the tooltip names. |
| `A site code or SQL overrides are required...` | No site code and no SQL override. | Enter a site code, or both SQL boxes. |
| `The site code <x> is not three letters or digits...` | The site code is not three characters. | Correct the **Site Code**. |
| `The SQL Server for site <x> is not known (<reason>). Enter the SQL Server name in Settings, ConfigMgr Site Settings.` | The provider did not return the site definition, or no provider is set. | Enter **Discovery SQL Server** and **Discovery Database**. |
| `Windows PowerShell could not run the scan (<type>: <text>)...` | `powershell.exe` is not available, or application control blocks it. | Install or repair Windows PowerShell 5.1. Allow it to start a script from the temporary folder. |
| `The site database query on <s>, database <db>, did not finish (exit code <n>, <text>)...` | The script ended without a result: Constrained Language Mode, AppLocker or a missing component. | Read the exit code and the text. Allow the script to run in Windows PowerShell 5.1. |
| `...did not return the site definition: <reason>. Check that the provider name is the SMS Provider computer...` | DCOM and WinRM to the SMS Provider failed. | Enter the SMS Provider computer in **Site Server / SMS Provider Host**, or enter **Discovery SQL Server** and **Discovery Database**. |
| `SQL Server on <s> denied the Windows login. The account needs the db_datareader role on <db>.` | SQL Server denied access to the account. | Give the account `db_datareader` on the database. |
| `Windows authentication to SQL Server <s> failed. Run the app as a domain account on a domain-joined computer...` | Windows authentication failed. | Run the app as a domain account on a domain-joined computer. Check the service principal name of the SQL Server. |
| `SQL Server on <s> received an anonymous login...` | The credentials did not pass a second remoting hop. | Run the scan from a session that holds your own logon. |
| `Cannot reach SQL Server <s>. Check the server name, the network path and the SQL Server firewall port.` | No connection. | Check the name, the network path and the firewall. |
| `Database <db> does not exist on SQL Server <s>, or the Windows login cannot open it. Check the database name.` | Wrong database name or no access. | Correct **Discovery Database**. |
| `The Windows login cannot read the inventory views in <db> on <s>...` | The account cannot read the views. | Give the account `db_datareader` on the database. |
| `SQL Server <s> did not answer within <N> seconds.` | The query timed out. | Check the load of the SQL Server. Run Discover again. |
| `The scan stopped after <N> seconds.` or `the query did not finish within 600 seconds and was stopped.` | The scan reached its time limit. | Run Discover again. |
| `No inventory data: the site database has no Add or Remove Programs rows...` | Hardware inventory does not collect Add/Remove Programs. | Enable the Add/Remove Programs classes in hardware inventory. |
| `the settings could not be saved. Nothing changed.` | `settings.json` could not be written. | Check the data folder. See [Data folders](../reference/data-folders.md). |

## Links

- [Introduction to hardware inventory](https://learn.microsoft.com/intune/configmgr/core/clients/manage/inventory/introduction-to-hardware-inventory): Explains hardware inventory.
- [How to configure hardware inventory in Configuration Manager](https://learn.microsoft.com/intune/configmgr/core/clients/manage/inventory/configure-hardware-inventory): Shows how to configure hardware inventory.
- [Introduction to software inventory in Configuration Manager](https://learn.microsoft.com/intune/configmgr/core/clients/manage/inventory/introduction-to-software-inventory): Explains software inventory.
- [Windows Installer Properties for the Uninstall Registry Key](https://learn.microsoft.com/windows/win32/msi/uninstall-registry-key): Lists the Uninstall registry values that Windows Installer writes.
