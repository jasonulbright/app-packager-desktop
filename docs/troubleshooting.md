# Troubleshooting

This page lists the most common messages of App Packager Desktop with their cause and their fix. Each entry has three parts: the message, the cause and the fix.

## What it does

The page helps you find the fix for a message that you see on the **App Packager** page, in the log pane, in a tooltip or in a dialog. [error-messages.md](reference/error-messages.md) lists all messages.

## When to use it

Use this page when a button is disabled, an operation fails, or a message names a setting.

## Requirements

None. Some fixes need rights on the ConfigMgr site or on the SQL Server of the site database.

## Steps

1. Read the message. A message in `<angle brackets>` shows a value that the application inserts.
2. Find the message in the tables below.
3. Do the fix.
4. Click the grid on the **App Packager** page, then press F5 to reload the list and test the ConfigMgr connection again.

## Publish buttons and settings

A disabled publish button shows a tooltip that names the missing setting.

| Message | Cause | Fix |
|---|---|---|
| ConfigMgr is not in use. Select it under Systems in use in Settings. (The tooltip begins with the name of the button, for example "Publish to ConfigMgr:".) | The system is cleared under **Systems in use**. The same text applies to Intune and WSUS with the system name. | Select the system under **Systems in use** in **Settings**. Select **Save Settings**. |
| Needs the ConfigMgr console on this computer. | The application did not find the Configuration Manager console. | Install the console. **Settings**, **Detected Tools** shows if the application finds it. |
| Set the site code in Settings, ConfigMgr Site Settings. | **Site Code** is empty. | Enter the site code. |
| Publish to Intune: set Tenant ID, Client ID and Client Secret in Settings, Intune. | Intune is in use, and a credential is missing. | Enter the three values under **Intune**. |
| Set the WSUS server in Settings, WSUS Publishing. | WSUS is in use, and **WSUS server** is empty. | Enter the server name under **WSUS Publishing**. |

## Messages when a run starts

| Message | Cause | Fix |
|---|---|---|
| Publish requires Intune credentials. Open Settings, Intune, to configure Tenant ID, Client ID, and Client Secret. | An Intune publish started without credentials. | Enter the credentials in **Settings**. |
| Publish requires a WSUS server. Open Settings, WSUS Publishing, to configure it. | A WSUS publish started without a server name. | Enter the server in **Settings**. |
| Publish to ConfigMgr requires the ConfigMgr Console. Not detected on this workstation. | The environment variable `SMS_ADMIN_UI_PATH` is missing, or the module file is missing. | Install the Configuration Manager console on this computer. |
| SiteCode is required. Open Settings to configure. | The site code is empty. | Enter the site code. |
| File Share Root is required. Open Settings to configure. | The share root is empty. | Enter the UNC path under **File Share Root (UNC)**. |
| SiteCode is required. Open Settings. | **Check ConfigMgr** ran with an empty site code. | Enter the site code. |
| No packagers selected. | A button was selected and no row is checked. | Check at least one row. |

## ConfigMgr connection

| Message | Cause | Fix |
|---|---|---|
| Could not connect to site `<code>`. Check Settings and ensure the CM console is available. | The connection test failed when the list loaded. The log pane shows "ConfigMgr: Connection failed" and the reason. | Check **Site Code** and **Site Server / SMS Provider Host**. Check that the console is on this computer. |
| The ConfigurationManager PowerShell module was not found. Install the Configuration Manager console on this computer. | The module file is missing. | Install the console. |
| The SMS Provider `<p>` denied access. The account needs a ConfigMgr security role for site `<code>`. | The provider refused the account. | Give the account a ConfigMgr security role for the site. |
| The CMSite drive for site `<code>` on `<provider>` could not connect. Check the site code and the SMS Provider in Settings, ConfigMgr Site Settings. | The CMSite drive did not connect. | Check **Site Code** and **Site Server / SMS Provider Host**. |

## Settings file

| Message | Cause | Fix |
|---|---|---|
| Settings file could not be read: `<path>` (`<reason>`). Defaults are in use and nothing is written to that file until Settings is saved. | `settings.json` is not valid or not readable. | Select **Save Settings** to replace the file. The save keeps the unreadable file as `settings.json.unreadable-<timestamp>`. |

## Discover unmanaged software

| Message | Cause | Fix |
|---|---|---|
| A site code or SQL overrides are required. Set the site code, or the SQL server and database, in Settings, ConfigMgr Site Settings. | No site code and no SQL override. | Enter **Site Code**, or **Discovery SQL Server** and **Discovery Database**. |
| The site code `<x>` is not three letters or digits. Correct it in Settings, ConfigMgr Site Settings. | The site code is not valid. | Correct **Site Code**. |
| The SQL Server for site `<x>` is not known (`<reason>`). Enter the SQL Server name in Settings, ConfigMgr Site Settings. | The SMS Provider did not return the SQL Server name. | Enter **Discovery SQL Server**. |
| Windows PowerShell could not start. Discover needs Windows PowerShell 5.1 (powershell.exe). | `powershell.exe` is missing. | Install Windows PowerShell 5.1. |
| SQL Server on `<s>` denied the Windows login. The account needs the db_datareader role on `<db>`. | The SQL login was refused. | Give the account the `db_datareader` role on the database. |
| Windows authentication to SQL Server `<s>` failed. Run the app as a domain account on a domain-joined computer, and check the SQL Server service principal name. | Windows authentication failed. | Run the application as a domain account on a domain-joined computer. |
| Cannot reach SQL Server `<s>`. Check the server name, the network path and the SQL Server firewall port. | The connection failed. | Check the server name, the network path and the firewall port. |
| The scan stopped after `<N>` seconds. | The scan reached its time limit of 600 seconds. | The message names no fix. |
| SQL Server on `<s>` received an anonymous login. Windows credentials do not pass through a second remoting hop without Kerberos delegation. Run the scan from a session that holds your own logon. | A second hop passed no credentials. | Run the scan from a session that holds your own logon. |

## Check Latest

| Message | Cause | Fix |
|---|---|---|
| The **Status** column shows **Error**. The log pane shows "ERROR: Could not determine latest version for `<application>`". | The vendor lookup returned no version. | Read the log pane for the reason. The grid shows only **Error**. |

## Limits

This page lists a selection of the messages. The tools show more messages. The Discover scan time limit is 600 seconds.

## Related pages

See [error-messages.md](reference/error-messages.md) for all messages and [Settings page](core/settings-page.md) for every setting.
