# Getting started

This page shows how to install App Packager Desktop, finish the first-run Setup window, connect to a ConfigMgr site, and package one application from the start to the end.

## What it does

App Packager Desktop packages third-party Windows applications. It reads the current version from the vendor, downloads the installer, stages the content, and publishes to ConfigMgr, Intune or WSUS. This page uses ConfigMgr as the destination.

## When to use it

Use this page on a new installation, or when you connect the application to a different site.

## Requirements

- Windows 10, Windows 11 or Windows Server, 64-bit.
- Windows PowerShell 5.1.
- For ConfigMgr: the Configuration Manager console on this computer. You also need an account with rights to create applications.
- For ConfigMgr publishing: a UNC path to a content share. The account must be able to write to it.
- A local folder for staging. The default is the `Staging` folder in the data folder `%LOCALAPPDATA%\AppPackager`.
- 7-Zip, for applications whose installer is an archive.

The release is self-contained. A separate .NET runtime is not necessary.

For WSUS and Intune requirements, see [Settings page](core/settings-page.md).

## Steps

### Install

1. Download the release from the Releases page of the project.
2. Choose one of the two files:
   - `AppPackagerDesktop-Setup-<version>.exe` installs for the current user without elevation. It adds a Start Menu shortcut. The switch `/S` installs silently.
   - `AppPackagerDesktop-<version>.zip` is the same build without an installer. Extract it and run `ApplicationPackagerUI.exe`.
3. Close the application before you install an update. The installer does not replace an executable that is in use.

The application does not check for a new version. To find a new version, open **Settings**, go to **About**, and select **Releases**.

### Finish the Setup window

The **Setup** window opens one time, after the main window appears. It opens when the first-run flag is not set in the settings file. An existing settings file without the flag counts as completed.

1. Read the first page, **Welcome to App Packager Desktop**.
2. Under **Systems in use**, select **ConfigMgr**, **Intune** and **WSUS** as needed. **ConfigMgr** is selected at first.
3. Select **Next**.
4. On the **Connection details** page, enter the values for the systems that you selected. For ConfigMgr, enter **Site Code**, **Site Server / SMS Provider**, **File Share Root** and **Download Root**.
5. Select **Save**.

![The first page of the Setup window with the Systems in use check boxes](images/start-setup-systems.png)

![The Connection details page of the Setup window with ConfigMgr and WSUS values](images/start-setup-connection.png)

Notes about the Setup window:

- The boxes show the saved values or the defaults. The default **Site Code** is `MCM`. The default **File Share Root** is `\\fileserver\sccm$`. Replace each default value with your own value.
- **Back** returns to the first page. **Skip** closes the window and saves nothing. With **Skip**, the window opens again at the next start.
- The check box **Don't show this again** sets the first-run flag. Use it with **Skip** to stop the window without saving any setting.
- If you clear **ConfigMgr**, **Save** stores an empty site code.
- If you select no system, the window shows this text: "No system is selected. Save stores that, and the publish buttons stay disabled until a system is selected in Settings."
- **Save** makes each selected system a default destination of One Click. The log pane of the **Packages** page then shows a line that starts with "Setup complete."
- All Setup values are also on the **Settings** page. See [Settings page](core/settings-page.md).

The first start shows the Dark theme. To change it, open **Settings** and select **Light**.

### Connect to a site

1. Open **Settings**.
2. Under **ConfigMgr Site Settings**, check **Site Code**. Enter the three-character site code of your site.
3. In **Site Server / SMS Provider Host**, enter the host name of the SMS Provider. Leave the box empty only when this computer is the SMS Provider.
4. In **File Share Root (UNC)**, enter the UNC path of the content share.
5. Select **Save Settings**.
6. Open **Packages**. Press F5 to reload the application list. The reload tests the connection to the site.
7. Read the **ConfigMgr** indicator in the status line. **Connected** means the test passed. **Failed** means the test did not pass. The log pane shows the reason on the line "ConfigMgr: Connection failed".

The indicator shows **Not configured** when the site code is empty. It shows **Not used** when **ConfigMgr** is not selected under **Systems in use**.

The publish buttons are disabled, not hidden, when a system is not ready. The tooltip of a disabled button names the missing setting. See [Troubleshooting](troubleshooting.md).

### Package one application

1. Open **Packages**.
2. In the grid, select the check box of one application. Use **Search applications...** to find it.
3. Select **Check Latest**. The **Latest** column shows the vendor version.
4. Select **Check ConfigMgr**. The **Current (ConfigMgr)** column shows the version that the site holds. The **Status** column shows **Update available** when the site version is older than the vendor version. It shows **Not found in ConfigMgr** when the site has no application with that name.
5. Select **Stage**. App Packager Desktop downloads the installer and prepares the content in the download root. The status shows **Staging...** and then **Staged**.
6. If the dialog **Configure: <application>** opens, check the values. Select **Continue**. Select **Cancel** to skip the application.
7. Select **Publish to ConfigMgr**. The status shows **Packaging...** and then **Packaged**.
8. If the dialog **Application Already Exists** opens, select **Overwrite**, **Skip** or **Cancel remaining**.
9. Select **Check ConfigMgr** again. The status shows **Up to date** when the site version equals the vendor version.

![The Packages grid with one application in the status Up to date after Stage and Publish to ConfigMgr](images/start-first-package-end-to-end.png)

To stop an operation, select **Cancel** or press Esc. **Cancel** is visible only while an operation runs.

### Use the sidebar

The sidebar on the left lists the pages. **Packages** is the first page, and the application starts on it. **Settings** is always the last entry.

1. Right-click an entry to open its menu.
2. Select **Hide** followed by the entry name, for example **Hide Detection Tester**, to remove the entry from the sidebar.
3. Right-click the sidebar and select **Show tool** to return a hidden entry. The menu item **Show tool** appears only when an entry is hidden.
4. Select **Reset sidebar** to restore the default order and show every entry.

![The sidebar with all twelve entries and the Settings entry](images/start-sidebar-default.png)

![The context menu of an entry with Hide Detection Tester, Show tool and Reset sidebar](images/start-sidebar-context-menu.png)

See [Keyboard](reference/keyboard.md) for the keys that move an entry. See [Settings page](core/settings-page.md) for the Sidebar section.

## Options and settings of this page

The first-run flag and the system choices are stored in `settings.json` in the data folder. Every setting that the Setup window changes is also on the **Settings** page.

## Limits

- Lab tests of ConfigMgr publishing used an account with the Full Administrator role. The application was not tested with a custom role.
- The application does not require elevation. A drag from Explorer cannot reach an application that runs elevated. Do not run the application elevated if you use drag and drop.
- The **Setup** window cannot test a connection. Use the **ConfigMgr** indicator on the **Packages** page.

## Troubleshooting

| Message | Cause | Fix |
|---|---|---|
| Could not connect to site `<code>`. Check Settings and ensure the CM console is available. | The connection test failed. | Check the settings. Install the Configuration Manager console on this computer. |
| SiteCode is required. Open Settings to configure. | The site code is empty. | Enter the site code in **Settings**. |
| File Share Root is required. Open Settings to configure. | The share root is empty. | Enter the UNC path in **Settings**. |

See [Troubleshooting](troubleshooting.md) for more messages.
