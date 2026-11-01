# Keyboard

This page lists the keyboard shortcuts, the dialog keys and the access keys of App Packager Desktop.

## What it does

The page lists every key that the application handles. The application defines no shortcut at window level. The main window has no shortcut.

## When to use it

Use this page to work without the mouse.

## Requirements

None. A shortcut works only in the context that the table names.

## Shortcuts

| Key | Context | Effect |
|---|---|---|
| Ctrl+A | **Packages** page | Checks or clears all rows. |
| F5 | **Packages** page | Reloads the application list. |
| Esc | **Packages** page, while an operation runs | Cancels the operation. |
| Ctrl+E | **Packages** page | Exports the shown rows to a CSV file. The save dialog is titled **Export Packages to CSV**. The file name starts as `AppPackager-Export-yyyyMMdd.csv`. |
| Alt+Up, Alt+Down | A focused sidebar entry | Moves the entry one place among the shown entries. |
| Esc | During a sidebar drag | Cancels the drag. |
| Ctrl+F | **Application Workbench** window | Shows the Find box. |
| Enter | Find box of the **Application Workbench** | Finds the next match. |
| Ctrl+Tab, Ctrl+Shift+Tab | Script editor of **Detection Tester** | Leaves the editor. The hint text is "Press Ctrl+Tab to leave the editor." |
| Tab | Editors of **Pivots and Scripts** | Types a tab. |
| Ctrl+Tab, Ctrl+Shift+Tab | Editors of **Pivots and Scripts** | Leaves the editor. |
| Esc, then Tab or Shift+Tab | Editors of **Pivots and Scripts** | Leaves the editor. |
| Ctrl+Space | Editors of **Pivots and Scripts** | Shows the starter list at the caret. |
| Enter | **Collection ID** box on the **Collections** tab | Runs **Open by ID**. |
| Enter | Path box of **Installer Analysis** | Analyzes the typed path. |
| Space | Grid of the **Discover unmanaged software** window | Toggles the checked state of the selected row. |
| Space | Pairs grid of the DP Content Manager pair picker; objects grid of the Site Hygiene findings picker | Toggles the checked state of the selected row. |
| Left, Up, Right, Down, Home, End | Trends chart of **Site Hygiene**, when it has focus | Moves the selected point. |

The **Relationships** tab of **Site Hygiene** has a tree. The tree works from the keyboard with the standard tree keys.

## Dialog keys

In a dialog, Enter selects the default button and Esc selects the cancel button.

| Window | Enter | Esc |
|---|---|---|
| **Discover unmanaged software** | **Enable selected** | **Not now** |
| Catalog deploy dialog of **Collection and Compliance** | **Show plan** | **Cancel** |
| Collection editor of **Collection and Compliance** | **Show plan** | **Cancel** |
| Parameters and comment dialogs of **Pivots and Scripts** | **OK** | **Cancel** |
| **Certificate Password** | **OK** | **Cancel** |
| **Remove deployment** | **Cancel** | **Cancel** |
| Import dialog of **Site Hygiene** | **Cancel** | **Cancel** |
| **WSUS Updates**, **Catalog Import** | No default button | Closes the window. |

The change plan window, the collection picker, the template editors, the pair picker, the findings picker, the maintenance window editor, the ring plan editor and the text prompt have a cancel button only. Esc selects it.

## Access keys

An access key is a letter that is underlined in a label. Press Alt and the letter.

| Window | Keys |
|---|---|
| Change plan window | **Undo** Alt+U, **Close without waiting** Alt+W, **Cancel** Alt+C, **Close** Alt+L |
| **Discover unmanaged software** | **Enable selected** Alt+E, **Enable all** Alt+A, **Not now** Alt+N, **Don't ask again for selected** Alt+D |
| **Remove deployment** | **Remove deployment** Alt+R, **Cancel** Alt+C |
| Pair picker, findings picker, Site Hygiene import dialog, comment dialog and parameters dialog | **Cancel** Alt+C |

## Limits

- The list comes from the code. The application does not list shortcuts in a help window.
- Tools have more keyboard rules in their own pages. Maintenance Windows can create a window with the keyboard only.

## Troubleshooting

| Problem | Cause | Fix |
|---|---|---|
| Esc does nothing on the **Packages** page. | No operation runs. | Esc cancels only a running operation. |
| Alt+Up and Alt+Down do not move an entry. | The sidebar entry has no focus, or the entry is **Packages**. | Focus the entry. **Packages** cannot move. |
