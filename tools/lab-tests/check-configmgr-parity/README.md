# Permanent lab test: stage, package, check (Q-128c)

Purpose: every packager that the lab can run is packaged by the PowerShell app (this creates the application in ConfigMgr), then Check ConfigMgr must find it in
the PowerShell app and in App Packager Desktop. A title or search mismatch is a failure.

Steps (run on the lab, never on the dev PC):

1. CM01: copy the PowerShell app (`Packagers`, `Lib`, `start-apppackager.ps1`) to `C:\guide\ps-run`, `one.ps1`, `batch.ps1`, `cleanup.ps1` and `tools\RemoteTestHarness\ProcessJob.ps1` of this repository to `C:\guide\ps-batch`, and run `batch.ps1 -KeepForChecks`
   (a scheduled task as SYSTEM works). It writes `C:\guide\ps-batch\results.csv`: packager, status (Created, Failed, Skipped), created name, version, reason.
   Packagers with a very large download or a combined packager are skipped (see `$skip` in `batch.ps1`); a failed stage is listed with its reason.
2. PowerShell app check: call `Get-MecmCurrentVersionByCMName` of `start-apppackager.ps1` with the CMName of each created packager (AppPackagerDiscovery module loaded).
3. APD check: run the query script of `MecmQueryService.QueryScript` (the same text the app runs, extracted from the source) with `-CMName` and `-ArpName` of each definition.
4. Real clicks: `tools/guide-capture/Probe-CheckConfigMgr.ps1` through `Run-VisibleProbe.ps1` for a list of packagers.

The result of 2026-11-24 is in the vault page title-parity-2026-11-24 and queue row Q-128c.

5. Clean up (required, 2026-11-25: the 288 applications of the first run filled CM01's C: drive: 50 GB of source files and 78 GB of content library).
   Run `cleanup.ps1` on CM01 when steps 2 to 4 are done (or run `batch.ps1` without `-KeepForChecks` when no checks follow: it calls `cleanup.ps1` at its end).
   It removes the applications listed as Created in `results.csv` with `Remove-CMApplication` (the site removes the content), except `-Keep` (default `PuTTY*`, `7-Zip*`, which existed before)
   and any application with a deployment; then the source folders that the deployment types name, under `C:\ContentShare\Applications`. It writes the removed names and folders to
   `C:\guide\lab-test-cleanup`. `-WhatIf` lists without removing. Budget for a full run: about 130 GB (source about 50 GB, content library about 80 GB), and C: on CM01 is 400 GB.
