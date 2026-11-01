# Bring your own installer

## What it does

You drop one installer on the Packages page. The app analyzes the file and proposes the application name, the publisher, the version, the silent switches, the uninstall command and a detection rule. You review the values. Then you stage the installer, save it as a definition, or save it as an application.

An application saved from a drop is a bring-your-own application. It has numbered installer revisions. It has no download script. Its updates are manual: you add a new revision with **Replace installer...**.

## When to use it

- Use it for an installer that no definition in the catalog covers.
- Use it for an installer that you receive as a file and cannot download from a fixed address.
- Use **Save as Application** to keep the installer and its revisions, and to change the profile in the [Application Workbench](workbench.md).
- Use **Save as definition** when you can later fill in a download address and a version scraper. See [Definitions](definitions.md).
- Use [Installer Analysis](../tools/installer-analysis.md) to read an installer without staging it. That tool accepts more file types than the drop does.

## Requirements

- One file with the extension `.msi` or `.exe`. The extension check ignores case.
- For **Stage**: **Download Root (local staging)** in Settings.
- For publishing: the settings of the target. See [Stage and publish](stage-and-publish.md).
- Do not run the app as administrator. Drag and drop from Explorer cannot reach an app that runs as administrator.

The analysis does not use the network.

## Steps: drop an installer and save it as an application

1. Open the **Packages** page.
2. Drag one `.msi` or `.exe` file from Explorer onto the page.
3. Wait for the dialog **Installer drop**. The log shows `Drop: analysing <file>...` and `Engine: <engine> (<type>), confidence: <confidence>`.
4. Read the line under the file name: `Detected: <engine> (<type>)   Architecture: <architecture>   Confidence: <confidence>`.
5. If the box **Install for** is visible, choose `System` or `User`. The box appears only when the installer accepts a mode switch. The arguments, the uninstall command and the detection rule change together with this choice.
6. Check each value. Each row has a badge, `Confirmed` or `Predicted`, and a line that names the source of the value. The rows **Application**, **Publisher**, **Version**, **Install args**, **Uninstall args** and **Uninstall command** are editable. The row **Detection** is read-only.
7. If a row has the badge `Predicted`, select **I confirmed the predicted values**. Until you select it, the buttons **Stage** and **Save as definition** are hidden and **Save as Application** is disabled.
8. Select **Save as Application**.
9. In the dialog **Saved**, select **Yes** to open the application in the Application Workbench. Select **No** to stay on the Packages page.

![The Installer drop dialog for an MSI](../images/core-byo-drop-review.png)

![The Saved prompt after Save as Application](../images/core-byo-saved-prompt.png)

**Save as Application** keeps this installer as source revision 1 of a bring-your-own application. It does not stage or deploy anything. The log shows `Saved as application <id>. It appears in the workbench with manual updates; nothing was staged or deployed.`

## Other buttons of the dialog

| Button | Result |
|---|---|
| **Stage** | Stages the dropped installer. The row appears in the grid with the status `Staged`. Select the row and a publish button to continue. |
| **Save as definition** | Writes a JSON definition in the user definitions folder. The definition has no download address and no version scraper. Fill them in before **Check Latest** works. The app never overwrites an existing definition. |
| **Cancel** | Closes the dialog. The log shows `Drop cancelled.` |

## Steps: stage and publish a bring-your-own application

1. Open the application in the Application Workbench.
2. Change the profile if you need to. See [Application Workbench](workbench.md).
3. Select **Stage**. The Workbench stages the newest installer revision. The log shows `Stage: <name> <version> (source revision <n>, <file>)`.
4. Choose the build in **Build**.
5. Under **Publish to:**, select **ConfigMgr**, **Intune** or **WSUS**.

A bring-your-own application has no row in the Packages grid until a stage adds one.

## Steps: add a new installer revision

1. Open the application in the Application Workbench.
2. In the section **Application**, select **Replace installer...**.
3. Choose the new `.msi` or `.exe` file.
4. In the dialog **Replace Installer**, read **Identity differences**. The app compares the installer type, the architecture, the product code, the application name and the publisher with the newest revision.
   - A difference in installer type or architecture is `Blocking`. The existing package does not match the new installer.
   - A difference in product code, name or publisher is `Review`.
5. Check the values under **New revision**. The application name and the publisher stay as they are (`kept by the application`).
6. Select **I confirmed the predicted values** if the box is visible.
7. Select **Add revision**.
8. The dialog **Installer Replaced** reports `Source revision <n> added for <name>.` If a field was Blocking, the dialog names it. Stage again before the next publish.

The app keeps the earlier revisions. The list **Installer source** shows **Revision**, **Installer**, **Version**, **Type**, **Architecture**, **SHA-256**, **Added** and **Blocking changes**.

![The replacement revision dialog](../images/core-byo-replacement.png)

## Options and settings

| Item | Effect |
|---|---|
| Download Root (local staging) | Folder for staged content. |
| Workbench data folder | The app stores installer revisions in `Workbench\applications\<key>\sources\<revision>`. The variable `APP_PACKAGER_WORKBENCH_ROOT` replaces the Workbench folder. |
| Bundle export | Includes the installer files only if you answer `Yes` to `Include the installer binaries?`. See [Application Workbench](workbench.md). |

The identifier of a bring-your-own application starts with `byo:`. Use it as `--application byo:<id>` in the [command-line build](command-line-build.md).

## Limits

- The drop accepts one file at a time, and only `.msi` or `.exe`.
- A predicted value is a guess. A wrong silent switch produces a package that fails on every target device. Check each predicted value before you confirm it.
- Updates are manual. The app has no update source for a bring-your-own application.
- The lab validated the drop, **Save as Application**, stage and publish to ConfigMgr and WSUS for an MSI installer.

## Troubleshooting

| Message | Cause | Fix |
|---|---|---|
| `Drop ignored: <n> files dropped, one installer at a time.` | You dropped more than one file. | Drop one file. |
| `Drop ignored: <file> is not an .msi or .exe.` | The extension is not supported. | Drop an `.msi` or `.exe`. |
| `Drop analysis failed: <reason>` | The analysis could not read the file. | Read the reason. Check that the file is an installer. |
| `Application name is required.` | The row **Application** is empty. | Enter a name. |
| `Version is required.` | The row **Version** is empty. | Enter a version. |
| `Install args are required for a non-MSI installer.` | The row **Install args** is empty for an EXE. | Enter the silent switches. |
| `Stage: DownloadRoot is not configured. Open Settings.` | **Download Root (local staging)** is empty. | Set the folder in Settings. |
| `Save as definition refused: <file> already exists. Existing definitions are never overwritten.` | A definition with that file name exists. | Change the application name. |
| `Save as Application failed: <reason>` | The app could not write the application. | Read the reason. |
| `Stage refused: <reason>` | The stage plan of the application failed. The reason follows the message. | Read the reason. If the installer revision is missing, select **Replace installer...** and add a revision. |

An Explorer drop does nothing when the app runs as administrator. Start the app without elevation.

See [Error messages](../reference/error-messages.md).

## Links

- [Windows Installer](https://learn.microsoft.com/windows/win32/msi/windows-installer-portal): Gives the entry point to the Windows Installer documentation.
- [Installer Database](https://learn.microsoft.com/windows/win32/msi/installer-database): Explains the structure of an installer database.
- [msiexec](https://learn.microsoft.com/windows-server/administration/windows-commands/msiexec): Documents the msiexec command line.
- [Prepare Win32 App Content for Upload](https://learn.microsoft.com/intune/app-management/deployment/create-win32-package): Shows how the Content Prep Tool makes an intunewin file.
