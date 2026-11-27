# Guide images: run book

The guide has 140 images.

Run every command on the development computer from the repository root, with the final build. The captures run on CLIENT01. The seed scripts read and write the ConfigMgr site of the lab through a remote session.

## 0. Preparation

### 0.1 Capture folder (final build)

Use a build of the final source: `origin/release/2026.1127.66` (`overhaul/v1` with the version stamp). The scripts match that interface. Each run writes the columns of a new installation into its settings (compact view); only the CVE run adds the CVE columns. Every publish opens the review; the comment of a capture is typed in the review. The log images show the Logs window.

```powershell
$out = 'C:\temp\apd-guide'                     # any local work folder outside the repository
pwsh tools\build-release.ps1 -PublishOnly -OutputRoot "$out\build"
$cap = "$out\capture"
New-Item -ItemType Directory -Force $cap | Out-Null
Copy-Item "$out\build\release-stage\*" $cap -Recurse -Force
Copy-Item tools\guide-capture\*.ps1, tools\guide-capture\definitions $cap -Recurse -Force
Copy-Item tools\lab-tests\ProcessJob.ps1, src\ApplicationPackagerEngine\Services\Wsus\WsusAdapter.ps1 $cap -Force
```

### 0.2 Capture folder with an older version stamp (update images only)

The update check cannot use a local test feed without a change of product code. `UpdateService` has the address `https://api.github.com/repos/jasonulbright/app-packager-desktop/releases/latest` in its code. The app has no setting or variable for it. It takes the installer only from `https://github.com/<repository>/releases/download/`, and only with a valid, timestamped signature of the publisher. A build of the final source with a lower version stamp needs no code change: it reads the real latest release. `2026.1118.48` must be lower than the latest published release.

```powershell
$old = "$out\capture-old"
foreach ($p in 'src\ApplicationPackagerBuild\ApplicationPackagerBuild.csproj', 'src\ApplicationPackagerUI\ApplicationPackagerUI.csproj') {
    dotnet publish $p -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:Version=2026.1118.48 -o $old
}
Copy-Item tools\guide-capture\*.ps1 $old -Force
Copy-Item tools\lab-tests\ProcessJob.ps1 $old -Force
```

The update window shows the version and the release notes of the latest published release.

### 0.3 Run wrapper

`$sid` is the session id of the signed-in lab user on CLIENT01 (`quser`). Each capture call below is run with this function. `$w` is a work folder inside the run folder on CLIENT01. Only the images and the failure screens come back.

```powershell
function Invoke-Capture([string]$Name, [string]$Line, [string]$Folder = $cap, [int]$Minutes = 40) {
    $command = '$w = Join-Path (Split-Path $RunOutput) ''work''; New-Item -ItemType Directory -Force $w | Out-Null; ' + $Line +
        '; foreach ($d in ''images'', ''fail'', ''checks'', ''Light'', ''Dark'') { if (Test-Path "$w\$d") { Copy-Item "$w\$d" $RunOutput -Recurse -Force } }'
    pwsh tools\lab-tests\Run-InSession.ps1 -Folder $Folder -SessionId $sid -OutFile "$out\$Name.log" -ResultFolder "$out\$Name" -Minutes $Minutes -Command $command
}
```

Preconditions of every capture: the work area of CLIENT01 is 1920 x 1032 at 100 percent scaling, the capture lock is free, `C:\guide\icon-pack.zip` is there, and no person uses the session during the run.

Read every log. A line with `FAIL`, `REFUSED` or `LEFT` needs action. A script can end with exit code 0 when an image step failed.

The guide images are the Light images: `images\light\<id>.png` (scripts with `-OutDir`: `Light\<id>.png` or `light\<id>.png`). Copy each one to `docs\images`. Never edit or mask an image.

## 1. Seed steps and preflight

Run the parts `Core`, `Deploy`, `Collection` and `Script` one time for a record file. The seed script stops at a name that is on the site. When the record file already holds these parts, do not run them again. Use that record file as `$rec` for every later step and for the cleanup.

A list for `-Part` works only through `-Command`. With `pwsh <script>` (that is `-File`), `Core, Deploy` arrives as one text and the script refuses it.

```powershell
$rec = '<full path of zzapd-records.local.json>'   # keep this file until the cleanup is done
# Only for a record file without these parts:
pwsh -NoProfile -Command "& .\tools\guide-capture\lab-seed\Seed-GuideLab.ps1 -RecordFile '$rec' -Part Core,Deploy,Collection,Script"
pwsh tools\guide-capture\lab-seed\Test-GuideLabData.ps1
Copy-Item $rec "$cap\zzapd-records.local.json" -Force
```

`Capture-Pivots.ps1`, `Capture-ToolsHygiene.ps1` and `Capture-ToolsDp.ps1` start only with a copy of the record file in the capture folder. They bound the script run, the deletes and the removals to the recorded ids. Copy the file again after every seed step.

No capture needs a member in `ZZAPD Workstations`. The collection has no member and its maintenance window is disabled. The images therefore show 0 members in the remove plan and a disabled window, also in the window list and the coverage view.

`Test-GuideLabData.ps1` changes nothing. Its `existing:` lines name data that no script creates: the compliance baselines (CMCO-CANARY and the Contoso baselines with the states Current, Behind and Untagged), the configuration item `SMB Signing Required (client)`, the deployments of the Live views and the content `ConfigMgr Client Health`. If one is missing, the images in 2.8 that show it cannot be made as the old image shows them.

| Step | Creates | Needed by |
|---|---|---|
| 1a `Core` | applications `ZZAPD Firefox` 140.0 and `ZZAPD Sample Viewer 1.0`, one content folder `ZZAPD-Seed-*` | CVE columns, window and report; Convert window |
| 1b `Deploy` | application `ZZAPD Deploy Tool`, distributed to the DP, no deployment | Deployment Helper, Rings |
| 1c `Collection` | collection `ZZAPD Workstations` without members, with the disabled monthly window `ZZAPD monthly patch window` | collection editor and remove plan; maintenance window editor and plan |
| 1d `Script` | approved script `ZZAPD Service status` | Pivots and Scripts |

Later seed steps run just before the captures that need them (sections 3 to 6).

## 2. Captures that change nothing on the site

| # | Command (`Invoke-Capture <name> '<line>'`) | Extra preconditions | Image ids |
|---|---|---|---|
| 2.1 | `apdpages` `& .\Capture-ApdPages.ps1 -Run $w -AppFolder $PWD.Path` | none | start-sidebar-default, core-add-app-step1, core-add-file-pick |
| 2.2 | `settingstree` `& .\Capture-SettingsTree.ps1 -OutDir $w -DataRoot "$w\data"` | none. For core-settings-citrix the run selects Preview on the page Citrix Updates and Policy; nothing is saved. | core-settings-configmgr, core-settings-citrix, core-settings-intune-cve, core-settings-top, core-settings-install-parameters, core-settings-tools, core-settings-github-api, core-settings-defaults, core-settings-install-parameters-sources, core-settings-about, core-script-signing-settings, core-one-click-settings |
| 2.3 | `settingswsus` `& .\Capture-SettingsTree.ps1 -OutDir $w -DataRoot "$w\data" -Only core-settings-wsus -WsusInUse` | WSUS console on CLIENT01. Run after 2.2 and take this image from this run. | core-settings-wsus |
| 2.4 | `settingsapps` `& .\Capture-SettingsApplications.ps1 -OutDir $w` | none | core-settings-application-settings |
| 2.5 | `supsettings` `& .\Capture-SettingsSupersedence.ps1 -OutDir $w` | none; nothing is saved | tools-settings-supersedence |
| 2.6 | `setup` `& .\Capture-Setup.ps1 -Run $w` | none | start-setup-systems, start-setup-connection |
| 2.7 | `scn-<s>`, one call for each of `InstallerAnalysis`, `SiteHygiene`, `LivePages`: `& .\Capture-Scenarios.ps1 -Scenario <s> -Run $w` | run before section 3; internet (`InstallerAnalysis` downloads the latest Notepad++ installer for the NSIS image and never starts it). `LivePages` needs a collection with a query rule that matches a device (`-WqlCollections`). The run selects and reads only. An image with a `FAIL` line is not saved. Do not take `tools-dp-content-issues` from this run. | tools-installer-analysis-overview, tools-installer-analysis-msi-properties, tools-installer-analysis-payload, tools-installer-analysis-file-browser, tools-installer-analysis-registry, tools-installer-analysis-strings; tools-site-hygiene-findings, tools-site-hygiene-relationships, tools-site-hygiene-summary, tools-site-hygiene-options; tools-maintenance-windows-list, tools-maintenance-windows-coverage, tools-maintenance-windows-templates; tools-collections-list, tools-collections-wql, tools-collections-templates, tools-collections-site-queries; tools-deployment-helper-options; tools-dp-content-content |
| 2.8 | `tools` `& .\Capture-Tools.ps1 -Run $w` | seeds 1c and 1d; internet (bundle sample); 7-Zip on CLIENT01. Every plan is cancelled. | tools-detection-tester-registry, tools-detection-tester-script, tools-detection-tester-installed; tools-collections-editor, tools-collections-remove-plan; tools-compliance-catalog, tools-compliance-deploy, tools-compliance-plan, tools-compliance-editor, tools-compliance-live, tools-compliance-drift, tools-compliance-compliance; tools-maintenance-windows-editor, tools-maintenance-windows-plan, tools-maintenance-windows-apply-template; tools-dp-content-dps, tools-dp-content-plan-remove; tools-pivots-options, tools-pivots-site-scripts; tools-installer-analysis-inner |
| 2.9 | `packages` `& .\Capture-PackagesPage.ps1 -Run $w` | internet | core-packages-list, packages, core-packages-select-update, core-packages-export-check-results, core-packages-buttons-disabled, core-packages-operation-history, core-packages-review-dialog |
| 2.10 | `rowmenu` `& .\Capture-PackagesContextMenu.ps1 -Run $w` | none | core-packages-context-menu |
| 2.11 | `addwb` `& .\Capture-AddAndWorkbench.ps1 -Run $w` | internet | core-add-app-step2-test, core-add-app-step3, core-add-app-step4-analysis, core-add-app-step5-install, core-add-app-step6-detection, core-add-app-step7-review; core-add-file-review, core-add-result-panel; core-workbench-application, core-workbench-install, core-workbench-detection, core-workbench-requirements, core-workbench-timing, core-workbench-source-files, core-workbench-review, core-workbench-bundle-menu |
| 2.12 | `byo` `& .\Capture-Byo.ps1 -Run $w` | internet | core-byo-replacement |
| 2.13 | `sites` `& .\Capture-Sites.ps1 -Run $w -Themes Light` | none (fictional sites). With `-Themes Light,Dark` the end-to-end checks run on Light only. | core-site-add, core-site-switcher-menu |
| 2.14 | `siteimport` `& .\Capture-SiteImport.ps1 -Run $w` | none | core-site-import |
| 2.15 | `sitefile` `& .\Capture-SiteFile.ps1 -Run $w -Theme Light` | none | core-site-file-import |
| 2.16 | `update` `& .\Capture-UpdateAndCli.ps1 -Part Update -Run $w`, with `-Folder $old` | folder of 0.2; internet; GitHub rate limit not reached. The installer is downloaded to a run folder and never started. | core-update-available, core-update-ready |
| 2.17 | `cli` `& .\Capture-UpdateAndCli.ps1 -Part Cli -Run $w -AppFolder $PWD.Path` | final build | core-cli-help |

## 3. Captures that write to the site and undo it in the same run

Before each confirmation, the scripts of sections 3 to 5 read the action and the object of every row of the change plan. The two cells must also agree with the name of the row. Each row must be an expected change, else nothing is confirmed.

| # | Command | Preconditions | Image ids | What the run removes |
|---|---|---|---|---|
| 3.1 | `pivots` `& .\Capture-Pivots.ps1 -Run $w` | seed 1d; the record copy; CLIENT01 online in the site. The script reads one service. The run checks the recorded GUID before the start and in the plan, and stops when its first step fails. The result and history images are made only after a confirmed run. The last step runs the library query `BIOS and serial` on CLIENT01 (CMPivot changes nothing on the client; the site records the client operation). | tools-pivots-script, tools-pivots-plan-script, tools-pivots-results, tools-pivots-history, tools-pivots-pivot | nothing to remove |
| 3.2 | `deploy` `& .\Capture-ToolsDeploy.ps1 -Run $w` (`-Minutes 60`) | seed 1b with the content installed; Zone 06 to Zone 13 have 0 members; the application has no deployment. The script checks all of this and each plan before it confirms. | tools-deployment-helper-plan-required, tools-deployment-helper-distribute-create, tools-deployment-helper-history, tools-deployment-helper-deploy (after the Undo, with Validate); tools-deployment-rings-plan-editor, tools-deployment-rings-new-run, tools-deployment-rings-plan-required, tools-deployment-rings-open-runs | the Available deployment (Zone 13) and the ring run (Zone 06) |

## 4. Site Hygiene

Run 4.1 before the DP seed of section 5, so the Content Live view has no failed content, as the guide shows it.

4.1 Seed, then capture:

```powershell
pwsh tools\guide-capture\lab-seed\Seed-GuideLab.ps1 -RecordFile $rec -Part Hygiene
Copy-Item $rec "$cap\zzapd-records.local.json" -Force
```

| Command | Preconditions | Image ids | Writes |
|---|---|---|---|
| `hygiene` `& .\Capture-ToolsHygiene.ps1 -Run $w` (`-Minutes 60`) | the six `ZZAPD Hygiene` packages, recorded, with no other object that matches the filter; the record copy; SQL read rights for the Live views; a deployment compliance from 50 to 98 percent for the alert image | tools-site-hygiene-import, tools-site-hygiene-live-deployments, tools-site-hygiene-live-content, tools-site-hygiene-live-dps, tools-site-hygiene-live-clients, tools-site-hygiene-live-inactive, tools-site-hygiene-live-site-health, tools-site-hygiene-trends, tools-site-hygiene-alerts, tools-site-hygiene-live-options, tools-site-hygiene-picker, tools-site-hygiene-plan-fix, tools-site-hygiene-history | The two fix runs delete the packages of the theme. Each plan row must have the action `Delete package` and exactly one of the three recorded packages as its object. The settings file of the retired dashboard on CLIENT01 is written only when none is there, and removed at the end. |

4.2 Seed, then capture:

```powershell
pwsh tools\guide-capture\lab-seed\Seed-GuideLab.ps1 -RecordFile $rec -Part Blockers
```

| Command | Preconditions | Image ids |
|---|---|---|
| `blockers` `& .\Capture-SiteHygieneRetired.ps1 -Run $w -Theme Light` | seed Blockers. The script runs no fix. | tools-site-hygiene-blockers |

## 5. DP Content Manager failures

```powershell
pwsh tools\guide-capture\lab-seed\Seed-GuideLab.ps1 -RecordFile $rec -Part Dp
Copy-Item $rec "$cap\zzapd-records.local.json" -Force
pwsh tools\guide-capture\lab-seed\Test-GuideLabData.ps1    # repeat until both "failed distribution" lines are OK
```

| Command | Preconditions | Image ids | Writes |
|---|---|---|---|
| `dp` `& .\Capture-ToolsDp.ps1 -Run $w` | both distributions show Failed; the record copy; no other object matches the package names | tools-dp-content-issues, tools-dp-content-results, tools-dp-content-history | Redistributes and removes one pair only: each plan row must have the action of the step (`Redistribute`, `Remove from DP`) and the recorded package of the theme on the distribution point as its object. A second run needs a new `Dp` seed: first `Remove-GuideLab.ps1 -RecordFile $rec -Type Package`. That also removes `ZZAPD Hygiene` packages that are left. |

## 6. Publish captures

Every publish review is confirmed only when it lists exactly the expected applications and systems, each with an expected action. Else the script selects Back and the image step fails. The checked count must also match before Publish All.

WSUS precondition for 6.1, 9.2 and 9.3: the WSUS server holds no unexpired update of any version of an application that the run publishes to WSUS (`ZZAPD gsudo` in 6.1; `WizTree` in `grid-publish`; `KeePass`, `-WsusApp`, in `wsus-only`; `CMake`, `-RetryApp`, in `retry`). A new publish supersedes the older updates of the application. Check this in the WSUS Updates window before the run. If there is such an update, do not start the run. The scripts do not check this.

Each run removes what it can prove it published (`PublishCleanup.ps1`): the application by its ModelName, the content folders that its log names in `Network content path:` lines and that did not exist before, and, for 6.1, the WSUS update that the log says this run published. After each run, read the lines `site:`, `share:`, `WSUS:` and `LEFT`.

If Run-InSession ends a run at a time limit, the cleanup of that run does not run. Then run the preflight, and remove each left object by hand with the ids that the log of the run names.

The import of the two update ids stays on the WSUS server: no run removes it. Use these two ids: `08488e39-fb0d-47ab-b89c-35ee834dade7,20a25326-d78f-4a69-bc81-fa8430093374`. The image is saved only when the result names both ids and none failed. To make only that image, with no publish and no write to the ConfigMgr site (no precondition of 6.1 except the WSUS console and internet on the WSUS server):

```powershell
Invoke-Capture 'catalog' '& .\Capture-FirstPackage.ps1 -Run $w -CatalogImportOnly -ImportIds 08488e39-fb0d-47ab-b89c-35ee834dade7,20a25326-d78f-4a69-bc81-fa8430093374'
```

| # | Command | Preconditions | Image ids |
|---|---|---|---|
| 6.1 | `first` `& .\Capture-FirstPackage.ps1 -Run $w -ImportIds 08488e39-fb0d-47ab-b89c-35ee834dade7,20a25326-d78f-4a69-bc81-fa8430093374` (`-Minutes 60`) | WSUS console on CLIENT01, signing certificate trusted (`tools/lab-tests/Initialize-WsusClient.ps1` ends without an error; it runs `Test-WsusPublishing.ps1`); no application `ZZAPD gsudo*`; no unexpired WSUS update of any version of `ZZAPD gsudo`; internet. Use only update ids that the WSUS server holds already. | start-first-package-end-to-end, core-publish-wsus-row, core-wsus-updates-window, core-wsus-catalog-import |
| 6.2 | `dialogs` `& .\Capture-PackagesDialogs.ps1 -Run $w` (`-Minutes 60`) | no application `ZZAPD gsudo*`; internet. Before the conflict image the run sets the version of its own new application to 0.0.1, so the review lets the second publish through. The conflict dialog is answered with Skip. | start-sidebar-context-menu, core-one-click-plan, core-stage-result-log, core-publish-configmgr-log, core-packages-conflict-dialog |

6.4 Seed the older versions, then run the two scenarios:

```powershell
pwsh tools\guide-capture\lab-seed\Seed-GuideLab.ps1 -RecordFile $rec -Part Supersedence
```

| # | Command | Image ids to take from this run |
|---|---|---|
| 6.4a | `supremember` `& .\Capture-PackagesSupersedence.ps1 -Run $w -Scenario Remember -Theme Light -AppFolder $PWD.Path` | core-packages-supersedence-confirm-remember only |
| 6.4b | `supyes` `& .\Capture-PackagesSupersedence.ps1 -Run $w -Scenario Yes -Theme Light -AppFolder $PWD.Path` | core-packages-supersedence-confirm, core-packages-supersedence-log |

Preconditions: exactly one older version of each application is on the site. Start 6.4b only when the log of 6.4a says `site: all applications of the run are removed`.

## 7. CVE, catalog, convert and discover (run last)

The CVE report opens in Edge on CLIENT01. The script does not close the browser. The browser then covers the app, and later runs refuse to send input. Run this step last, Light only. Close the browser window by hand afterwards.

| Command | Preconditions | Image ids |
|---|---|---|
| `core` `& .\Capture-CoreWindows.ps1 -Run $w -Themes Light` | seed 1a; no other `ZZAPD Firefox*` application; internet (NVD can limit requests); SQL read rights and hardware inventory for Discover | core-catalog, core-convert-window, core-discover-settings, core-discover-prompt, core-packages-cve-columns, core-cve-window, core-cve-report-html |

For Dark images of this step, close the browser window by hand, then run with `-Themes Dark`.

## 8. Cleanup

1. Look at the list first: `pwsh tools\guide-capture\lab-seed\Remove-GuideLab.ps1 -RecordFile $rec -WhatIf`
2. Remove: the same command without `-WhatIf`. It removes only recorded ids whose name still matches, newest first. A package that a capture deleted shows as `already gone`.
3. A collection, package, task sequence or application with a deployment is not removed. A `LEFT` line keeps its record. Fix the cause (for example a deployment of a recorded application), then run step 2 again.
4. Run `Test-GuideLabData.ps1`. The line `other ZZAPD applications` must be MISSING (nothing found).
5. WSUS: read the `LEFT on WSUS` lines of `first.log`. The catalog import is not removed: it stays on the WSUS server.
6. CLIENT01: Run-InSession removes its run folders. It keeps a folder when the copy of the results fails, and it says so. Remove that folder after you copy the results.
7. CM01 content library: the site removes the content of deleted applications and packages in the background.
8. Development computer: delete `$out` after the images are committed and the record file holds no object.

## 9. Release end-to-end check and keyboard pass

Both use the installed build under test. Copy the installer's app folder or give `-AppFolder`. The run folder needs `ApdUiDriver.ps1`, `GuideLib.ps1` and `ProcessJob.ps1`; the capture folder `$cap` holds them. Without `-AppFolder $PWD.Path`, both scripts take `%LOCALAPPDATA%\Programs\App Packager Desktop`.

`-Only` takes a list. Give it inside the command text, as below.

### 9.1 Steps that write nothing to the site or to WSUS

```powershell
Invoke-Capture 'e2e-ui' '& .\Probe-ReleaseE2E.ps1 -Run "$w\e2e" -Only first-start,settings,settings-search,columns,detail-pane,logs; Copy-Item "$w\e2e\images" $RunOutput -Recurse -Force' -Minutes 60
```

Preconditions: no release newer than the installed build is on GitHub; internet (vendor downloads for Check Latest and for the Stage of `logs`); the site is reachable. `settings` and `settings-search` change only the settings of the run. `logs` stages `WizTree` into the new staging folder of the step, so the Stage downloads the installer.

### 9.2 grid-publish, workbench-publish, one-click, then deployment-helper

```powershell
Invoke-Capture 'e2e-publish' '& .\Probe-ReleaseE2E.ps1 -Run "$w\e2e" -Only grid-publish,workbench-publish,one-click,deployment-helper; Copy-Item "$w\e2e\images" $RunOutput -Recurse -Force' -Minutes 90
```

Preconditions: the site holds no application of `WizTree`, `Everything` or `WinSCP`; the WSUS server holds no unexpired update of any version of `WizTree`; WSUS console and signing certificate on CLIENT01; `Zone 13` exists with 0 members. `grid-publish` publishes to ConfigMgr and WSUS and types the comment in the review. The run removes nothing: remove each object that a `RECORD` line names.

### 9.3 wsus-only and retry

```powershell
Invoke-Capture 'e2e-wsus' '& .\Probe-ReleaseE2E.ps1 -Run "$w\e2e" -Only wsus-only,retry; Copy-Item "$w\e2e\images" $RunOutput -Recurse -Force' -Minutes 60
```

Preconditions: WSUS console and signing certificate on CLIENT01; the WSUS server holds no unexpired update of any version of `KeePass` (`-WsusApp`) or `CMake` (`-RetryApp`); nothing listens on port 8599 of the WSUS server (`-WsusBadPort`); internet. Each step publishes one WSUS update. The `RECORD` lines name the update ids. Remove these updates in WSUS Updates, and only these.

`retry` cannot save the right port through the Settings page: the results window is modal and is the only place of the Retry button. The step writes the port into the settings files of its own data folder while the window is open. The app reads the settings again when the retry starts.

### 9.4 Keyboard pass

```powershell
Invoke-Capture 'keyboard' '& .\Probe-KeyboardPass.ps1 -Run "$w\kb" -AppFolder $PWD.Path; Copy-Item "$w\kb\images" $RunOutput -Recurse -Force' -Minutes 60
```

Preconditions: site and WSUS reachable; internet (the One Click plan checks 7-Zip). The pass writes nothing to a site or to WSUS. It does not open the publish results window, which shows only after a publish.
