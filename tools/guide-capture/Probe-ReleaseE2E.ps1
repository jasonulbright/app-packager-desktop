<#
.SYNOPSIS
    The release end-to-end check of the installed app in its real window: first start, saved settings, saved columns, a publish from the grid to ConfigMgr and WSUS, a
    publish from the Workbench, a One Click run, and the deploy plan of the Deployment Helper. Every step says PASS or FAIL with what it read. The last line is
    "E2E-FAILS=<n>" and the exit code is 0.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock, with ApdUiDriver.ps1 and GuideLib.ps1 beside it:
        .\Probe-ReleaseE2E.ps1 -Run <new folder> -AppFolder "$env:LOCALAPPDATA\Programs\App Packager Desktop"

    What must be true before the run (the script cannot prove all of it):
      - the site MCM on cm01.contoso.com holds no application of -GridApp, -WorkbenchApp or -OneClickApp (each publish step also runs Check ConfigMgr first
        and does not publish unless the log says "Not found in ConfigMgr");
      - the WSUS server holds no update of the version of -GridApp that is current;
      - the collection -Collection exists and has no member;
      - no release newer than the installed build is on GitHub (an update window would open by itself);
      - the lab client reaches the vendor download sites.

    Step names for -Only: first-start, settings, columns, grid-publish, workbench-publish, one-click, deployment-helper. Each step has a data folder of its own
    under -Run. deployment-helper needs the application of -GridApp on the site, so it follows grid-publish (also in an earlier run).

    What the run writes: grid-publish creates one application on the site, its content on the file share and one WSUS update; workbench-publish and one-click
    each create one application and its content. The run removes none of them. For each publish it prints lines that start with RECORD: the title, the version,
    the content path, the CI_ID and ModelName and the WSUS update id, as the log of the run gives them. It deletes no file and no folder. It sends keys and
    mouse input only to windows of the app that it started. It never clicks Overwrite: an Application Already Exists dialog gets Skip and the step fails.
    The images are <Run>\images\<step>-<name>.png.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string]$AppFolder = (Join-Path $env:LOCALAPPDATA 'Programs\App Packager Desktop'),
    [string]$GridApp = 'WizTree',
    [string]$WorkbenchApp = 'Everything',
    [string]$OneClickApp = 'WinSCP',
    [string]$Collection = 'Zone 13',
    [string[]]$Only = @(),
    # For a client without the WSUS administration console: the grid step publishes to ConfigMgr only.
    [switch]$GridWithoutWsus,
    [ValidateSet('Light', 'Dark')][string]$Theme = 'Light',
    [string]$FileShareRoot = '\\cm01.contoso.com\ContentShare$',
    [string]$WsusServer = 'cm01.contoso.com',
    [int]$WsusPort = 8530
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')

$stepNames = 'first-start', 'settings', 'columns', 'grid-publish', 'workbench-publish', 'one-click', 'deployment-helper'
$script:Fails = 0
$script:Records = New-Object System.Collections.Generic.List[string]
$script:StepName = ''
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$Run = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Run)

# ---------------- output and evidence
# Write-Host, so that a function that returns a value can also say what it read.
function Say([string]$Text) { Write-Host $Text }
function Check([string]$Name, [bool]$Ok, [string]$Evidence = '') {
    if ($Ok) { Say "PASS $($script:StepName) $Name | $Evidence" } else { $script:Fails++; Say "FAIL $($script:StepName) $Name | $Evidence" }
}
# A check that the rest of the step depends on: when it fails, the step ends.
function Require([string]$Name, [bool]$Ok, [string]$Evidence = '') {
    Check $Name $Ok $Evidence
    if (-not $Ok) { throw "the step cannot go on after: $Name" }
}
function Snap([string]$Name) {
    try { Move-MouseAway; Save-Shot (Join-Path $Run (Join-Path 'images' "$($script:StepName)-$Name.png")) } catch { Say "INFO screenshot $Name failed: $($_.Exception.Message)" }
}
function Get-Text([string]$Id, [double]$Timeout = 5) { $e = Find-El $null $Id -Timeout $Timeout; if ($e) { [string]$e.Current.Name } else { '' } }
function Real-Click($El) { if (-not $El) { throw 'the element to click was not found' }; Click-El $El; Start-Sleep -Milliseconds 800 }
function Click-Id([string]$Id, [double]$Timeout = 15) {
    $e = Find-El $null $Id -Timeout $Timeout
    if (-not $e) { throw "no element with the id $Id" }
    Real-Click $e
}
function Open-Page([string]$Name) { if (-not (Click-Nav $Name 20)) { throw "the sidebar has no entry $Name" }; Start-Sleep -Seconds 3 }
function Get-ToggleState($El) { "$($El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState)" }

# The session log of a data folder (the file that every start of the app on that folder appends to).
function Get-SessionLog([string]$Data) {
    @(Get-ChildItem -LiteralPath $Data -Recurse -File -Filter 'packr.log' -ErrorAction SilentlyContinue | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
}
function Get-LogSince([string]$Data, [int]$Mark) { $log = [string](Get-SessionLog $Data); if ($log.Length -gt $Mark) { $log.Substring($Mark) } else { '' } }
function Get-LogMark([string]$Data) { ([string](Get-SessionLog $Data)).Length }
function Show-Log([string]$Segment, [string]$Pattern, [int]$Max = 25) {
    $lines = @($Segment -split "`r?`n" | Where-Object { $_ -match $Pattern } | Select-Object -Last $Max)
    foreach ($l in $lines) { Say ('  log: ' + $(if ($l.Length -gt 260) { $l.Substring(0, 260) } else { $l })) }
}

# The settings files of a data folder, as objects with their paths.
function Get-SettingsFiles([string]$Data) {
    foreach ($f in @(Get-ChildItem -LiteralPath $Data -Recurse -File -Filter 'settings.json' -ErrorAction SilentlyContinue)) {
        [pscustomobject]@{ Path = $f.FullName; Json = (Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json) }
    }
}
function Get-UserSettings([string]$Data) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $file = Join-Path $Data (Join-Path 'Users' (Join-Path $sid 'settings.json'))
    if (Test-Path -LiteralPath $file) { Get-Content -LiteralPath $file -Raw | ConvertFrom-Json } else { $null }
}

# ---------------- the app
# Starts the app on a data folder and writes no settings file, for a first start and for a restart (Start-Apd of the driver writes the settings file anew).
function Open-App([string]$Data) {
    if (-not (Test-Path -LiteralPath $exe)) { throw "no app at $exe" }
    $env:APP_PACKAGER_DATA_ROOT = $Data
    if (Test-Path -LiteralPath 'C:\tools\dotnet') { $env:DOTNET_ROOT = 'C:\tools\dotnet' }
    $p = Start-Process -FilePath $exe -PassThru
    Add-ApdOwnedProcess $p
    $script:App = $p
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and $p.MainWindowHandle -eq 0) { Start-Sleep -Milliseconds 500; $p.Refresh() }
    if ($p.MainWindowHandle -eq 0) { Stop-ApdProcess $p; throw 'no main window' }
    [void][ApdW]::ShowWindow($p.MainWindowHandle, 3)
    [void][ApdW]::SetForegroundWindow($p.MainWindowHandle)
    Start-Sleep -Seconds 6
}

# Closes the app as a user does (the window saves its state while it closes); ends the process through its handle when it does not close.
function Close-AppNormally {
    $process = $script:App
    $closed = Close-ApdWindow 'App Packager Desktop'
    if ($closed) { $closed = $process.WaitForExit(30000) }
    Check 'the app closes through its window' $closed "closed: $closed"
    Stop-Apd
}

# A data folder of one step, with the settings that Probe-OverhaulV1 uses, and a staging folder under it.
function New-StepData([string]$Name) {
    $data = Join-Path $Run "data-$Name"
    $null = New-Item -ItemType Directory -Path $data
    $data
}
function New-Settings([string]$Data, [bool]$Wsus) {
    @{
        FileShareRoot = $FileShareRoot; DownloadRoot = (New-RunStaging $Data); SiteCodeSaved = $true
        Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $Wsus }
        Wsus = @{ ServerName = $WsusServer; PortNumber = $WsusPort; UseSsl = $false }
    }
}
function Start-StepApp([string]$Data, [hashtable]$Settings) {
    $null = Start-Apd $exe $Data $Theme $Settings
    if (-not (Wait-El 'btnMore' 90 -Enabled)) { throw 'the App Packager page did not load' }
    Start-Sleep -Seconds 5
}

# Every window that the app shows: its top-level windows and the windows below them.
function Get-WindowNames {
    $isWindow = New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Window)
    foreach ($top in @(Get-ApdWindows)) {
        [string]$top.Current.Name
        foreach ($n in $top.FindAll($script:TS::Descendants, $isWindow)) { [string]$n.Current.Name }
    }
}

# ---------------- dialogs and waits
# True when the Application Already Exists dialog shows. Its primary button is Overwrite: only Skip is ever clicked.
function Skip-Conflict {
    if (-not (Find-El $null -Name 'Application Already Exists' -Timeout 0.3)) { return $false }
    $skip = Find-El $null -Name 'Skip' -ControlType 'Button' -Timeout 5
    if (-not $skip) { throw 'The Application Already Exists dialog shows no Skip button. Nothing was clicked.' }
    Real-Click $skip
    $true
}
function Assert-NoDialog {
    if (Find-El $null 'PrimaryButton' -Timeout 0.1) {
        $texts = @(foreach ($w in @(Get-ApdWindows)) { Get-ElList $w 250 | Where-Object { $_ -match '^Text' } }) | Select-Object -Last 8
        throw "A dialog that this step does not expect is open. Nothing was clicked. Last texts: $($texts -join ' // ')"
    }
}

# Waits until the condition holds. Says that it waits, so that a runner with a quiet limit sees output. Stops on an unexpected dialog.
function Wait-Until([scriptblock]$Condition, [int]$Seconds, [string]$What) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $beat = Get-Date
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        if (((Get-Date) - $beat).TotalSeconds -ge 30) { Say "  waiting: $What"; $beat = Get-Date }
        Start-Sleep -Seconds 2
    }
    $false
}
# Waits until no run of the App Packager page is active, seen three times in a row.
function Wait-RunEnd($Window, [int]$Seconds, [string]$What) {
    Start-Sleep -Seconds 4
    $script:Idle = 0
    $ended = Wait-Until { Assert-NoDialog; if (Test-ApdBusy $Window) { $script:Idle = 0 } else { $script:Idle++ }; $script:Idle -ge 3 } $Seconds $What
    if (-not $ended) { throw "$What did not end in $Seconds seconds" }
}

# ---------------- grids
function Get-Items($Scope, [System.Windows.Automation.ControlType]$Type) {
    @($Scope.FindAll($script:TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), $Type)))
}
function Get-RowNames($Row) {
    @(@($Row.Current.Name) + @($Row.FindAll($script:TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) | Where-Object { $_ })
}
# The texts of the cells of one row, in the order of the columns.
function Get-RowCells($Row) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $isText = New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Text)
    $cells = New-Object System.Collections.Generic.List[string]
    for ($c = $walker.GetFirstChild($Row); $c; $c = $walker.GetNextSibling($c)) {
        if ($c.Current.ControlType -eq [System.Windows.Automation.ControlType]::HeaderItem) { continue }
        $text = $c.FindFirst($script:TS::Descendants, $isText)
        if ($text) { $cells.Add([string]$text.Current.Name) } else { $cells.Add([string]$c.Current.Name) }
    }
    , $cells.ToArray()
}
function Get-GridRows([string]$GridId, [double]$Timeout = 10) {
    $grid = Find-El $null $GridId -Timeout $Timeout
    if (-not $grid) { return }
    Get-Items $grid ([System.Windows.Automation.ControlType]::DataItem) | Where-Object { -not $_.Current.IsOffscreen -and -not $_.Current.BoundingRectangle.IsEmpty }
}
function Get-Headers([string]$GridId) {
    $grid = Find-El $null $GridId -Timeout 10
    Get-Items $grid ([System.Windows.Automation.ControlType]::HeaderItem) | Where-Object { $_.Current.Name -and $_.Current.Name -notlike 'System.*' -and $_.Current.BoundingRectangle.Width -gt 0 }
}
function Get-NamePattern([string]$Application) { '^' + [regex]::Escape($Application) + '( \((x64|x86)\))?$' }

# Searches the grid for one application and returns its row and its full name; throws unless exactly one row has that name.
function Find-PackagerRow([string]$Application) {
    $w = Main-Window
    Set-ElText (Find-El $w 'txtAppPackagerSearch') $Application
    Start-Sleep -Seconds 3
    $pattern = Get-NamePattern $Application
    $found = @(foreach ($row in @(Get-GridRows 'PackagerGrid')) {
        $name = @(Get-RowNames $row | Where-Object { $_ -match $pattern } | Select-Object -First 1)
        if ($name) { [pscustomobject]@{ Row = $row; Name = [string]$name[0] } }
    })
    if ($found.Count -ne 1) { throw "the grid shows $($found.Count) rows named like $Application, not 1" }
    $found[0]
}
# Checks the row of one application and no other row. Returns the full name.
function Select-OnlyApplication([string]$Application) {
    $w = Main-Window
    $clear = Find-El $w 'btnClearSelection' -Timeout 2
    if ($clear -and -not $clear.Current.IsOffscreen) { Real-Click $clear }
    $found = Find-PackagerRow $Application
    $box = Get-ApdRowBoxes $found.Row | Select-Object -First 1
    if (-not $box) { throw "the row of $($found.Name) has no check box" }
    if ((Get-ToggleState $box) -ne 'On') { Real-Click $box }
    Set-ElText (Find-El $w 'txtAppPackagerSearch') ''
    Start-Sleep -Seconds 2
    $count = Get-Text 'txtSelectionCount'
    Require 'exactly one application is checked' ($count -ceq '1 selected') "txtSelectionCount='$count', row '$($found.Name)'"
    $found.Name
}

function Invoke-CheckLatest([string]$Data, [string]$FullName) {
    $mark = Get-LogMark $Data
    Open-ApdMenuItem (Main-Window) 'btnCheckMenu' 'miCheckLatest'
    Wait-RunEnd (Main-Window) 600 'Check Latest'
    $seg = Get-LogSince $Data $mark
    Show-Log $seg 'Latest|ERROR'
    Require 'Check Latest reads a version' ($seg -match 'Latest version: \S+' -and $seg -match [regex]::Escape("Latest: $FullName")) "log lines above"
}
function Test-NotFoundLine([string]$Segment, [string]$FullName) {
    $Segment -match ('ConfigMgr: ' + [regex]::Escape($FullName) + '\s*\r?\n\S+ \S+\s+Not found in ConfigMgr')
}
function Assert-NotOnSite([string]$Data, [string]$FullName) {
    $mark = Get-LogMark $Data
    Open-ApdMenuItem (Main-Window) 'btnCheckMenu' 'miCheckConfigMgr'
    Wait-RunEnd (Main-Window) 600 'Check ConfigMgr'
    $seg = Get-LogSince $Data $mark
    Show-Log $seg 'ConfigMgr|Not found|match|ERROR'
    Require 'the site does not hold the application before the publish' (Test-NotFoundLine $seg $FullName) "the log must say 'Not found in ConfigMgr' after 'ConfigMgr: $FullName'"
}

# ---------------- the publish review, the results and the removal record
# The matrix of the review or of the results: one object for each row with its cells.
function Read-Matrix([string]$GridId) {
    foreach ($row in @(Get-GridRows $GridId)) { [pscustomobject]@{ Cells = (Get-RowCells $row) } }
}
# True when the open review lists exactly the one application, for exactly these systems, and shows no ConfigMgr application for it and no failed read.
function Test-Review([string]$FullName, [string[]]$Systems) {
    Start-Sleep -Seconds 2
    $rows = @(Read-Matrix 'gridReviewMatrix')
    $counts = Get-Text 'txtReviewCounts'
    $scope = Get-Text 'txtReviewScope'
    $changed = Get-Text 'txtReviewChanged' 1
    $blocked = Get-Text 'txtReviewBlocked' 1
    Say "  review: counts='$counts' scope='$scope' changed='$changed' blocked='$blocked'"
    foreach ($r in $rows) { Say "  review row: $($r.Cells -join ' | ')" }
    $confirm = Find-El $null 'btnReviewConfirm' -Timeout 3
    # "Publish All: ConfigMgr and WSUS - Site MCM - from Settings, Systems in Use" (the parts are joined by a middle dot).
    $names = if (@($Systems).Count -gt 1) { (@($Systems)[0..(@($Systems).Count - 2)] -join ', ') + ' and ' + @($Systems)[-1] } else { @($Systems)[0] }
    $one = $rows.Count -eq 1 -and @($rows[0].Cells).Count -ge 1 -and $rows[0].Cells[0] -ceq $FullName -and $counts -like '1 application, *' -and $scope -clike "Publish All: $names *from Settings, Systems in Use"
    # "ConfigMgr has ...": the site holds an application. "ConfigMgr was not read": nothing proves that it holds none.
    $known = $rows.Count -eq 1 -and (($rows[0].Cells -join ' ') -match 'ConfigMgr has|ConfigMgr was not read')
    if ($one) { $script:ReviewVersion = [string]$rows[0].Cells[1] }
    $one -and -not $known -and $confirm -and $confirm.Current.IsEnabled
}

# Starts a publish with -Start and follows it: each review passes the guard before it is confirmed, else Back. Reads the results window. Returns $true when
# every system says Published for the one application. Complete-ApdPublish of the driver is not used here: it confirms a review without this guard.
function Invoke-GuardedPublish([scriptblock]$Start, [string]$FullName, [string[]]$Systems) {
    $script:ReviewVersion = ''
    & $Start
    $reviews = 0; $idle = 0; $conflict = $false
    $deadline = (Get-Date).AddSeconds(1500); $beat = Get-Date; $started = Get-Date
    while ((Get-Date) -lt $deadline) {
        if (Skip-Conflict) { $conflict = $true; Check 'no application of this title is on the site' $false 'Application Already Exists showed; Skip was clicked, nothing was overwritten'; $idle = 0; continue }
        $confirm = Find-El $null 'btnReviewConfirm' -Timeout 1
        if ($confirm -and -not $confirm.Current.IsOffscreen) {
            $reviews++
            $good = Test-Review $FullName $Systems
            Snap "review-$reviews"
            if (-not $good -or $reviews -gt 2) {
                Click-Id 'btnReviewBack' 5
                Check 'the review lists exactly the one application and may be confirmed' $false "review number $reviews was closed with Back; nothing was published"
                return $false
            }
            Check "review $reviews lists exactly '$FullName' for $($Systems -join ' and ')" $true "version '$($script:ReviewVersion)'"
            Click-Id 'btnReviewConfirm' 5
            Start-Sleep -Seconds 4
            $idle = 0
            continue
        }
        if (Find-El $null 'btnResultsDone' -Timeout 1) {
            Start-Sleep -Seconds 2
            $headline = Get-Text 'txtResultsHeadline'
            $counted = "$(Get-Text 'txtResultsPublished'), $(Get-Text 'txtResultsFailed'), $(Get-Text 'txtResultsSkipped')"
            $headers = @(Get-Headers 'gridResultsMatrix' | ForEach-Object { $_.Current.Name })
            $rows = @(Read-Matrix 'gridResultsMatrix')
            foreach ($r in $rows) { Say "  results row: $($r.Cells -join ' | ')" }
            $details = Find-El $null 'lstResultsDetails' -Timeout 1
            if ($details) { foreach ($t in (Get-Items $details ([System.Windows.Automation.ControlType]::Text))) { Say "  results detail: $($t.Current.Name)" } }
            Snap 'results'
            $n = $Systems.Count
            $want = "$n $(if ($n -eq 1) { 'publish' } else { 'publishes' }) completed"
            $cells = if ($rows.Count -eq 1) { @($rows[0].Cells) } else { @() }
            $allPublished = $cells.Count -eq ($n + 1) -and $cells[0] -ceq $FullName -and @($cells | Select-Object -Skip 1 | Where-Object { $_ -ceq 'Published' }).Count -eq $n
            $ok = $headline -ceq $want -and $allPublished -and -not $conflict
            Check "the results say Published for $($Systems -join ' and ')" $ok "headline='$headline' ($counted); columns: $($headers -join ', ')"
            Click-Id 'btnResultsDone' 5
            Wait-RunEnd (Main-Window) 300 'the end of the publish'
            return $ok
        }
        Assert-NoDialog
        if (Test-ApdBusy (Main-Window)) { $idle = 0 } else { $idle++ }
        if ($idle -ge 8 -and ((Get-Date) - $started).TotalSeconds -ge 30) { Check 'the publish shows a results window' $false "the run ended with no results window after $reviews review(s)"; return $false }
        if (((Get-Date) - $beat).TotalSeconds -ge 30) { Say '  waiting: the publish'; $beat = Get-Date }
        Start-Sleep -Seconds 2
    }
    Check 'the publish ends' $false 'no end in 1500 seconds'
    $false
}

# The removal record of one publish, from the new part of the log and the stage manifests of the step.
function Write-Record([string]$Data, [int]$Mark, [string]$Application, [string]$ReviewVersion = '') {
    $seg = Get-LogSince $Data $Mark
    $pick = { param($pattern) @([regex]::Matches($seg, $pattern) | ForEach-Object { $_.Groups[1].Value.Trim() } | Select-Object -Unique) -join ' ; ' }
    $versions = @(Get-ChildItem -LiteralPath $Data -Recurse -File -Filter 'stage-manifest.json' -ErrorAction SilentlyContinue |
        ForEach-Object { [string](Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).SoftwareVersion } | Select-Object -Unique) -join ' ; '
    $title = & $pick '(?m)  Packaged: (.+?)\s*$'
    $content = & $pick '(?m)Network content path: (.+?)\s*$'
    $ciid = & $pick 'Final CI_ID: (\d+)'
    $model = & $pick 'Final ModelName: (\S+)'
    $wsus = @([regex]::Matches($seg, '(published|already on the server) ([0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12});') |
        ForEach-Object { "$($_.Groups[2].Value) ($($_.Groups[1].Value))" } | Select-Object -Unique) -join ' ; '
    $created = [bool]($seg -match 'Creating application\.\.\.' -and $seg -match 'Application created successfully')
    $none = { param($v) if ($v) { $v } else { 'none in the log' } }
    $line = "RECORD step=$($script:StepName) application='$Application' title='$(& $none $title)' stagedVersion='$versions' reviewVersion='$ReviewVersion' " +
        "contentPath='$(& $none $content)' createdByThisRun=$created ciId='$(& $none $ciid)' modelName='$(& $none $model)' wsusUpdate='$(& $none $wsus)'"
    $script:Records.Add($line)
    Say $line
    Show-Log $seg 'Stage|Packag|CI_ID|ModelName|content path|Creating application|already|WSUS|FAIL|ERROR' 60
    [pscustomobject]@{ Title = $title; ContentPath = $content; Created = $created; Wsus = $wsus }
}

# ---------------- the steps
$steps = [ordered]@{}

$steps['first-start'] = {
    $data = New-StepData 'first-start'
    $existing = @(Get-ChildItem -LiteralPath $data -Recurse -Force)
    Require 'the profile is empty before the start' ($existing.Count -eq 0) "$($existing.Count) items in $data"
    Open-App $data
    if (-not (Wait-El 'btnMore' 90 -Enabled)) { throw 'the App Packager page did not load' }
    # A tooltip under the pointer is a window of its own.
    Move-MouseAway
    Start-Sleep -Seconds 25
    $windows = @(Get-WindowNames)
    $dialog = [bool](Find-El $null 'PrimaryButton' -Timeout 1)
    Check 'no window and no dialog opens by itself' ($windows.Count -eq 1 -and $windows[0] -ceq 'App Packager Desktop' -and -not $dialog) "windows 25 s after the load: $($windows -join ' | '); dialog: $dialog"
    Snap 'start'

    $progress = Get-Text 'txtSetupProgress' 15
    Check 'the setup checklist shows' ($progress -ceq '1 of 4 complete') "txtSetupProgress='$progress'"
    $expected = [ordered]@{
        txtSetupStepSystems   = 'Step 1 of 4, Choose systems: Complete'
        txtSetupStepSite      = 'Step 2 of 4, Connect to your site: To do'
        txtSetupStepFolders   = 'Step 3 of 4, Set content folders: To do'
        txtSetupStepDiscovery = 'Step 4 of 4, Discover unmanaged software: To do'
    }
    foreach ($id in $expected.Keys) { $read = Get-Text $id; Check "checklist step $id" ($read -ceq $expected[$id]) "read '$read'" }
    Say "  context strip: $(Get-Text 'txtStripSite') $(Get-Text 'txtStripHost'); close button: '$(Get-Text 'btnSetupClose')'"

    $seg = Get-SessionLog $data
    Show-Log $seg 'ConfigMgr|WARNING|ERROR'
    Check 'no connection test runs against the example site code' ($seg -match 'ConfigMgr: Site code not configured\. Open Settings\.' -and $seg -notmatch 'Testing connection to site') 'log lines above'
    $settings = Get-UserSettings $data
    Check 'the settings file records a new install with an open checklist' ($settings -and $settings.SetupChecklist -ceq 'Open' -and -not $settings.FirstRunCompleted -and -not $settings.SiteCodeSaved) `
        "SetupChecklist='$($settings.SetupChecklist)' FirstRunCompleted='$($settings.FirstRunCompleted)' SiteCodeSaved='$($settings.SiteCodeSaved)'"

    # The first check: Check Latest reads the vendor and needs no site.
    $full = Select-OnlyApplication $GridApp
    Invoke-CheckLatest $data $full
    Snap 'first-check'

    # The first scan: the checklist offers it. A scan that works logs "Discovered <n> apps." and opens the prompt when n is above 0. A failed scan logs its
    # error under the same prefix, so every other line with that prefix fails the check. Without a choice in the prompt the scan changes nothing.
    $scan = Find-El $null 'btnSetupScan' -Timeout 10
    Require 'the checklist offers the first scan' ($scan -and $scan.Current.IsEnabled) "button '$(if ($scan) { $scan.Current.Name })'"
    $mark = Get-LogMark $data
    # The scan step can stand below the visible part of the checklist; the focus scrolls it into view.
    $scan.SetFocus(); Start-Sleep -Seconds 1
    Real-Click $scan
    $script:ScanPrompt = $false
    $done = Wait-Until {
        if (Find-El $null 'btnNotNow' -Timeout 0.5) { $script:ScanPrompt = $true; return $true }
        # A line "Discovered <n>" with n above 0 comes just before the prompt, so it does not end the wait.
        $lines = @((Get-LogSince $data $mark) -split "`r?`n" | Where-Object { $_ -match 'Discover unmanaged software: ' -and $_ -notmatch 'reading hardware inventory|Discovered [1-9]\d* apps?\.' })
        $lines.Count -gt 0 -and -not (Test-ApdBusy (Main-Window))
    } 700 'the first scan'
    if ($script:ScanPrompt) { Say "  discovery prompt: $(Get-Text 'txtDiscoverPrompt')"; Snap 'scan-prompt'; Click-Id 'btnNotNow' 5; Start-Sleep -Seconds 3 }
    $seg = Get-LogSince $data $mark
    Show-Log $seg 'Discover'
    $scanLines = @($seg -split "`r?`n" | Where-Object { $_ -match 'Discover unmanaged software: ' } | ForEach-Object { ($_ -replace '^.*?Discover unmanaged software: ', '').Trim() })
    $read = $scanLines -ccontains 'reading hardware inventory from the ConfigMgr site database.'
    $found = @($scanLines | Where-Object { $_ -cmatch '^Discovered \d+ apps?\.$' })
    # "no change." is the line that Not now writes.
    $other = @($scanLines | Where-Object { $_ -cne 'reading hardware inventory from the ConfigMgr site database.' -and $_ -cne 'no change.' -and $_ -cnotmatch '^Discovered \d+ apps?\.$' })
    $noSite = @($other | Where-Object { $_ -match '^The SQL Server for site \w+ is not known \(no SMS Provider is configured\)\. Enter the SQL Server name in Settings, ConfigMgr\.$' })
    if (-not $script:ScanPrompt -and -not $found.Count -and $noSite.Count -eq 1 -and $other.Count -eq 1) {
        Check 'the first scan with no site configured stops and names the missing setting' ($done -and $read) "line: '$($noSite[0])'; checklist now '$(Get-Text 'txtSetupProgress')'"
        Snap 'after-scan'; return
    }
    $result = if ($script:ScanPrompt) { $found.Count -eq 1 -and $found[0] -cmatch '^Discovered [1-9]' } else { $found.Count -eq 1 -and $found[0] -ceq 'Discovered 0 apps.' }
    Check 'the first scan ends with the discovery prompt or with the result of a scan that found no app' ($done -and $read -and $result -and $other.Count -eq 0) `
        "prompt shown: $($script:ScanPrompt); result line: '$($found -join ' / ')'; other lines: '$($other -join ' / ')'; checklist now '$(Get-Text 'txtSetupProgress')'"
    Snap 'after-scan'
}

# The text that a combo box shows.
function Get-ComboText($Combo) {
    $p = $null
    if ($Combo.TryGetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern, [ref]$p)) {
        $selected = @($p.Current.GetSelection())
        if ($selected.Count) { return (@(Get-RowNames $selected[0]) -join ' ') }
    }
    @(Get-Items $Combo ([System.Windows.Automation.ControlType]::Text) | ForEach-Object { $_.Current.Name }) -join ' '
}
# Filters the Application Settings grid to one application and returns the Supersedence cell of its only row.
function Get-SupersedenceCell([string]$Application) {
    Set-ElText (Need-El 'txtApplicationFilter') $Application
    Start-Sleep -Seconds 3
    $rows = @(Get-GridRows 'dgSupersedenceApps')
    Require "the filter shows only the row of $Application" ($rows.Count -eq 1 -and (@(Get-RowNames $rows[0]) -join ' ') -match [regex]::Escape($Application)) "$($rows.Count) row(s)"
    $cell = $rows[0].FindFirst($script:TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($script:AE::NameProperty), 'Supersedence of the application'))
    if (-not $cell) { throw 'the row has no Supersedence cell' }
    $cell
}
function Get-ReplaceOverrides([string]$Data) {
    foreach ($f in @(Get-SettingsFiles $Data)) {
        if ($f.Json.Supersedence -and $f.Json.Supersedence.TitleOverrides) {
            foreach ($o in $f.Json.Supersedence.TitleOverrides.PSObject.Properties) { if ($o.Value.Mode -eq 'Replace') { "$($o.Name) in $($f.Path)" } }
        }
    }
}

$steps['settings'] = {
    $data = New-StepData 'settings'
    Start-StepApp $data (New-Settings $data $false)

    # A plain setting of Settings, General.
    Open-Page 'Settings'
    Click-Id 'nav_general'
    $box = Need-El 'chkCheckForUpdatesAtStartup'
    Require 'the setting is off at the start' ((Get-ToggleState $box) -eq 'Off') "chkCheckForUpdatesAtStartup=$(Get-ToggleState $box)"
    Set-Check $box $true
    Start-Sleep -Seconds 2
    Check 'the page shows an unsaved change' ((Get-Text 'txtUnsavedChanges') -ceq 'Unsaved changes') "txtUnsavedChanges='$(Get-Text 'txtUnsavedChanges')'"

    # Application Settings: one bulk apply to the filtered row.
    Click-Id 'nav_supersedence'
    $null = Get-SupersedenceCell $GridApp
    Select-Combo (Need-El 'cboBulkColumn') 'Supersedence'
    Select-Combo (Need-El 'cboBulkValue') 'Replace'
    Select-Combo (Need-El 'cboBulkScope') 'Filtered applications'
    Click-Id 'btnBulkApply'
    Start-Sleep -Seconds 2
    $shown = Get-ComboText (Get-SupersedenceCell $GridApp)
    Check 'bulk apply sets the cell' ($shown -match '\bReplace\b') "cell shows '$shown'"
    Snap 'before-save'
    Click-Id 'btnSaveSettings'
    Start-Sleep -Seconds 5
    Check 'Save Settings leaves no unsaved change' ((Get-Text 'txtUnsavedChanges') -ceq 'No unsaved changes') "txtUnsavedChanges='$(Get-Text 'txtUnsavedChanges')'"
    Close-AppNormally

    $saved = Get-UserSettings $data
    Check 'settings.json holds the plain setting' ($saved.CheckForUpdatesAtStartup -eq $true) "CheckForUpdatesAtStartup=$($saved.CheckForUpdatesAtStartup)"
    $overrides = @(Get-ReplaceOverrides $data)
    Check 'settings.json holds one supersedence override Replace' (@($overrides | ForEach-Object { ($_ -split ' in ')[0] } | Select-Object -Unique).Count -eq 1) ($overrides -join ' ; ')

    Open-App $data
    if (-not (Wait-El 'btnMore' 90 -Enabled)) { throw 'the App Packager page did not load after the restart' }
    Start-Sleep -Seconds 5
    Open-Page 'Settings'
    Click-Id 'nav_general'
    $state = Get-ToggleState (Need-El 'chkCheckForUpdatesAtStartup')
    Check 'after the restart the page shows the plain setting' ($state -eq 'On') "chkCheckForUpdatesAtStartup=$state"
    Snap 'general-after-restart'
    Click-Id 'nav_supersedence'
    $shown = Get-ComboText (Get-SupersedenceCell $GridApp)
    Check 'after the restart the grid shows the bulk value' ($shown -match '\bReplace\b') "cell shows '$shown'"
    Snap 'applications-after-restart'
    $saved = Get-UserSettings $data
    Check 'settings.json still holds both after the restart' ($saved.CheckForUpdatesAtStartup -eq $true -and @(Get-ReplaceOverrides $data).Count -ge 1) "CheckForUpdatesAtStartup=$($saved.CheckForUpdatesAtStartup)"
}

# Drags the right edge of a column header. The pointer must be over the app when the button goes down; the button is always released.
function Resize-Column($Header, [int]$By) {
    $r = $Header.Current.BoundingRectangle
    $y = [int]($r.Y + $r.Height / 2); $x = [int]($r.X + $r.Width - 2)
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point $x, $y
    Start-Sleep -Milliseconds 500
    Assert-ApdPointer 'The drag'
    [ApdW]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    try {
        foreach ($i in 1..10) { [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point ($x + [int]($By * $i / 10)), $y; Start-Sleep -Milliseconds 60 }
    }
    finally { [ApdW]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero) }
    Start-Sleep -Seconds 2
}
function Get-HeaderNames { @(Get-Headers 'PackagerGrid' | ForEach-Object { $_.Current.Name }) }
function Get-HeaderWidth([string]$Name) { $h = Get-Headers 'PackagerGrid' | Where-Object { $_.Current.Name -ceq $Name } | Select-Object -First 1; if ($h) { [int]$h.Current.BoundingRectangle.Width } else { 0 } }

$steps['columns'] = {
    $data = New-StepData 'columns'
    Start-StepApp $data (New-Settings $data $false)
    $before = Get-HeaderNames
    Require 'the grid shows the columns KEV and Vendor at the start' ($before -ccontains 'KEV' -and $before -ccontains 'Vendor') ($before -join ', ')
    Click-Id 'btnColumns'
    Start-Sleep -Seconds 1
    Click-Id 'miColumnKEV' 5
    Send-ApdKeys '{ESC}'
    Start-Sleep -Seconds 2
    $hidden = Get-HeaderNames
    Check 'Columns hides KEV' ($hidden -cnotcontains 'KEV') ($hidden -join ', ')
    $widthBefore = Get-HeaderWidth 'Vendor'
    Resize-Column (Get-Headers 'PackagerGrid' | Where-Object { $_.Current.Name -ceq 'Vendor' } | Select-Object -First 1) 60
    $widthSet = Get-HeaderWidth 'Vendor'
    Check 'a drag of the header edge widens Vendor' ($widthSet -ge $widthBefore + 30) "width $widthBefore -> $widthSet px"
    Snap 'changed'
    Close-AppNormally

    $saved = Get-UserSettings $data
    $layout = @($saved.PackagesGridLayout.Columns | Where-Object { $_.Id -eq 'Vendor' }) | Select-Object -First 1
    Check 'settings.json holds the hidden column' (@($saved.PackagesHiddenColumns) -ccontains 'KEV') "PackagesHiddenColumns=$(@($saved.PackagesHiddenColumns) -join ',')"
    Check 'settings.json holds the width of Vendor' ($layout -and $layout.Width -gt 0) "PackagesGridLayout Vendor width=$($layout.Width)"

    Open-App $data
    if (-not (Wait-El 'btnMore' 90 -Enabled)) { throw 'the App Packager page did not load after the restart' }
    Start-Sleep -Seconds 5
    $after = Get-HeaderNames
    Check 'after the restart KEV stays hidden' ($after -cnotcontains 'KEV' -and $after -ccontains 'Vendor') ($after -join ', ')
    $widthAfter = Get-HeaderWidth 'Vendor'
    Check 'after the restart Vendor keeps its width' ([math]::Abs($widthAfter - $widthSet) -le 3) "width set $widthSet px, after restart $widthAfter px"
    Snap 'after-restart'
}

# Check Latest and Stage of one application from the page, then the check that the site does not hold it.
function Invoke-StageFromGrid([string]$Data, [string]$FullName) {
    Invoke-CheckLatest $Data $FullName
    Assert-NotOnSite $Data $FullName
    $count = Get-Text 'txtSelectionCount'
    Require 'still exactly one application is checked' ($count -ceq '1 selected') "txtSelectionCount='$count'"
    $mark = Get-LogMark $Data
    Real-Click (Find-El (Main-Window) 'btnStage' -Timeout 10)
    Wait-RunEnd (Main-Window) 900 'Stage'
    $seg = Get-LogSince $Data $mark
    Show-Log $seg 'Stage|Staged|ERROR'
    Require 'Stage ends with a staged version' ($seg -match 'Staged: ' -and $seg -notmatch 'Stage FAILED') 'log lines above'
}

$steps['grid-publish'] = {
    $data = New-StepData 'grid-publish'
    Start-StepApp $data (New-Settings $data (-not $GridWithoutWsus))
    $systems = if ($GridWithoutWsus) { @('ConfigMgr') } else { @('ConfigMgr', 'WSUS') }
    Say "  scope: $(Get-Text 'txtPublishScope')"
    $full = Select-OnlyApplication $GridApp
    Invoke-StageFromGrid $data $full
    Set-ElText (Find-El (Main-Window) 'txtAppPackagerComment') 'Release end-to-end check'
    $count = Get-Text 'txtSelectionCount'
    Require 'exactly one application is checked before Publish All' ($count -ceq '1 selected') "txtSelectionCount='$count'"
    $publish = Find-El (Main-Window) 'btnPublishAll' -Timeout 10
    Require 'Publish All is enabled' ($publish -and $publish.Current.IsEnabled) "help: $($publish.Current.HelpText)"
    $mark = Get-LogMark $data
    try {
        $ok = Invoke-GuardedPublish { Click-Id 'btnPublishAll' } $full $systems
    }
    finally { $record = Write-Record $data $mark $full $script:ReviewVersion }
    Check 'the log says that this run created the ConfigMgr application' $record.Created "title '$($record.Title)', content '$($record.ContentPath)'"
    if (-not $GridWithoutWsus) { Check 'the log says that this run published the WSUS update' ($record.Wsus -match '\(published\)') "wsus '$($record.Wsus)'" }
    Snap 'grid-after'
}

$steps['workbench-publish'] = {
    $data = New-StepData 'workbench-publish'
    Start-StepApp $data (New-Settings $data $false)
    $full = Select-OnlyApplication $WorkbenchApp
    Invoke-CheckLatest $data $full
    Assert-NotOnSite $data $full

    # Opens the Workbench on the row of the application and checks that it shows that application.
    function Open-RowInWorkbench {
        $found = Find-PackagerRow $WorkbenchApp
        $cell = Find-El $found.Row -Name $full -Timeout 5
        Real-Click $(if ($cell) { $cell } else { $found.Row })
        Click-Id 'btnDetailWorkbench' 10
        Require 'the Workbench opens' ([bool](Wait-El 'btnWorkbenchPublishAll' 60)) 'btnWorkbenchPublishAll'
        Start-Sleep -Seconds 5
        $combo = Need-El 'cboApplication'
        $edit = Find-El $combo 'PART_EditableTextBox' -Timeout 5
        $value = if ($edit) { $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } else { '' }
        Require 'the Workbench shows the application' ($value -like "*$WorkbenchApp*") "application box: '$value'"
    }

    # A publish from the Workbench takes a sealed build, so the Workbench stages first.
    Open-RowInWorkbench
    $mark = Get-LogMark $data
    Real-Click (Find-El (Find-ApdWindow 'Workbench') 'btnStage' -Timeout 10)
    Start-Sleep -Seconds 8
    Assert-NoDialog
    Require 'the Workbench closes for the stage' (-not (Find-ApdWindow 'Workbench')) 'the Workbench window is gone'
    Wait-RunEnd (Main-Window) 900 'the Workbench stage'
    $seg = Get-LogSince $data $mark
    Show-Log $seg 'Workbench|Stage|Staged|ERROR'
    Require 'the Workbench stage ends with a staged version' ($seg -match 'Staged: ' -and $seg -notmatch 'Stage FAILED') 'log lines above'

    Open-RowInWorkbench
    $build = Get-ComboText (Need-El 'cboBuild')
    Require 'the Workbench selects a sealed build' ($build -and $build -notmatch 'No sealed builds') "cboBuild '$build'"
    $button = Need-El 'btnWorkbenchPublishAll'
    Require 'Publish All of the Workbench is enabled' $button.Current.IsEnabled "help: $($button.Current.HelpText)"
    Snap 'workbench'
    $mark = Get-LogMark $data
    try {
        $ok = Invoke-GuardedPublish { Click-Id 'btnWorkbenchPublishAll' } $full @('ConfigMgr')
    }
    finally { $record = Write-Record $data $mark $full $script:ReviewVersion }
    Check 'the log says that this run created the ConfigMgr application' $record.Created "title '$($record.Title)', content '$($record.ContentPath)'"
    Snap 'grid-after'
}

$steps['one-click'] = {
    $data = New-StepData 'one-click'
    $settings = New-Settings $data $false
    # The id of the definition of the tracked application, from the definitions of the installed app.
    $definitions = @(Get-ChildItem -LiteralPath (Join-Path $AppFolder 'Definitions') -Filter *.json -File |
        ForEach-Object { try { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json } catch { $null = $_ } } | Where-Object { $_.Application -match (Get-NamePattern $OneClickApp) })
    Require 'one definition has the name of the One Click application' ($definitions.Count -eq 1) "$($definitions.Count) definition(s)"
    $full = [string]$definitions[0].Application
    $settings.OneClick = @{ Action = 'StageAndPackage'; TrackedApplications = @([string]$definitions[0].Id); DefaultDestinations = @{ ConfigMgr = $true; WSUS = $false; Intune = $false } }
    Start-StepApp $data $settings
    $mark = Get-LogMark $data
    Click-Id 'btnOneClick'
    $planShown = Wait-Until { [bool](Find-El $null 'btnPlanOnly' -Timeout 1) } 900 'the checks of One Click'
    Require 'the One Click plan opens' $planShown 'btnPlanOnly'
    Start-Sleep -Seconds 3
    $header = Get-Text 'txtPlanHeader'
    $systems = Get-Text 'txtPlanSystems'
    $rows = @(Get-GridRows 'gridPlan')
    foreach ($r in $rows) { Say "  plan row: $((Get-RowCells $r) -join ' | ')" }
    Say "  plan: header='$header' systems='$systems' flow='$(Get-Text 'txtPlanFlow')' summary='$(Get-Text 'txtPlanSummary')'"
    $boxes = @{}
    if ($rows.Count -eq 1) { foreach ($b in (Get-Items $rows[0] ([System.Windows.Automation.ControlType]::CheckBox))) { $boxes[[string]$b.Current.Name] = Get-ToggleState $b } }
    Say "  plan boxes: $(@($boxes.Keys | ForEach-Object { "$_=$($boxes[$_])" }) -join ', ')"
    Snap 'plan'
    $seg = Get-LogSince $data $mark
    Show-Log $seg 'One Click|ConfigMgr|Not found|Latest version|ERROR'
    $onlyThis = $rows.Count -eq 1 -and (Get-RowNames $rows[0]) -ccontains $full -and $header -like '1 application included, 0 not included.*' -and
        $boxes['Include'] -eq 'On' -and $boxes['ConfigMgr'] -eq 'On' -and $boxes['WSUS'] -ne 'On' -and $boxes['Intune'] -ne 'On' -and
        $systems -ceq 'In use: ConfigMgr. Not in use: Intune, WSUS.'
    $absent = Test-NotFoundLine $seg $full
    $runButton = Find-El $null 'btnRun' -Timeout 5
    if (-not ($onlyThis -and $absent -and $runButton -and $runButton.Current.IsEnabled)) {
        $null = Close-ApdWindow 'One Click'
        Require "the plan includes exactly '$full' for ConfigMgr, which the site does not hold" $false "only this application: $onlyThis; not found on the site: $absent. The plan was closed without Run."
    }
    Check "the plan includes exactly '$full' for ConfigMgr, which the site does not hold" $true "header '$header'"
    $version = [string]((Get-RowCells $rows[0]) | Where-Object { $_ -match '^\d+(\.\d+)+' } | Select-Object -First 1)

    $mark = Get-LogMark $data
    try {
        Real-Click $runButton
        $ended = Wait-Until {
            if (Find-El $null 'btnResultsDone' -Timeout 0.5) { return $true }
            Assert-NoDialog
            if (Find-El $null 'btnSupersedeYes' -Timeout 0.1) { throw 'The supersedence confirmation opened; this run expects none. Nothing was clicked.' }
            (Get-Text 'txtPlanSummary' 1) -match '^(Complete|Canceled|Failed)'
        } 1500 'the One Click run'
        Require 'the One Click run ends' $ended "txtPlanSummary='$(Get-Text 'txtPlanSummary')'"
        Start-Sleep -Seconds 3
        if (Find-El $null 'btnResultsDone' -Timeout 10) {
            $headline = Get-Text 'txtResultsHeadline'
            $matrix = @(Read-Matrix 'gridResultsMatrix')
            foreach ($r in $matrix) { Say "  results row: $($r.Cells -join ' | ')" }
            Snap 'results'
            Check 'the results say Published for ConfigMgr' ($headline -ceq '1 publish completed' -and $matrix.Count -eq 1 -and $matrix[0].Cells[0] -ceq $full -and $matrix[0].Cells[1] -ceq 'Published') "headline='$headline'"
            Click-Id 'btnResultsDone' 5
        }
        else { Check 'a results window shows after the run' $false 'no btnResultsDone' }
        $summary = Get-Text 'txtPlanSummary'
        Check 'the plan window says one published and none failed' ($summary -match '^Complete: 1 published, 0 failed') "txtPlanSummary='$summary'"
        Snap 'plan-after-run'
        Click-Id 'btnHistory' 10
        $history = Wait-El 'btnHistoryClose' 20
        Require 'Run history opens' ([bool]$history) 'btnHistoryClose'
        Start-Sleep -Seconds 2
        $historySummary = Get-Text 'txtHistorySummary'
        $runs = @(Read-Matrix 'gridRunHistory')
        foreach ($r in $runs) { Say "  history row: $($r.Cells -join ' | ')" }
        Snap 'history'
        Check 'Run history lists the run' ($historySummary -ceq '1 run, newest first.' -and $runs.Count -eq 1 -and $runs[0].Cells[2] -ceq '1' -and $runs[0].Cells[3] -ceq '1' -and $runs[0].Cells[4] -ceq '0') "txtHistorySummary='$historySummary'"
        Click-Id 'btnHistoryClose' 5
        $null = Close-ApdWindow 'One Click'
    }
    finally { $record = Write-Record $data $mark $full $version }
    Check 'the log says that this run created the ConfigMgr application' $record.Created "title '$($record.Title)', content '$($record.ContentPath)'"
    $report = [regex]::Match((Get-LogSince $data $mark), '(?m)One Click report: (.+?)\.md\s*$')
    if ($report.Success -and (Test-Path -LiteralPath ($report.Groups[1].Value + '.json'))) {
        foreach ($a in (Get-Content -LiteralPath ($report.Groups[1].Value + '.json') -Raw | ConvertFrom-Json).Applications) { Say "  report: $($a | ConvertTo-Json -Compress -Depth 4)" }
    }
}

# Filters a list of the Deploy tab and selects its only row that has exactly one of the names.
function Select-DeployRow([string]$FilterId, [string]$GridId, [string]$Text, [string]$Pattern) {
    Set-ElText (Need-El $FilterId) $Text
    Start-Sleep -Seconds 3
    # The list of a site can still load when the filter is typed, so the row is waited for.
    $deadline = (Get-Date).AddSeconds(120)
    do {
        $rows = @(Get-GridRows $GridId 30 | Where-Object { (Get-RowCells $_) -match $Pattern })
        if ($rows.Count -ne 1) { Start-Sleep -Seconds 5 }
    } while ($rows.Count -ne 1 -and (Get-Date) -lt $deadline)
    if ($rows.Count -ne 1) { foreach ($r in @(Get-GridRows $GridId 30)) { Say "  row seen: $((Get-RowCells $r) -join ' | ')" } }
    Require "$GridId has exactly one row for '$Text'" ($rows.Count -eq 1) "$($rows.Count) row(s)"
    Say "  selected: $((Get-RowCells $rows[0]) -join ' | ')"
    Click-El $rows[0]
    Start-Sleep -Milliseconds 800
    $rows[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 3
}
# Select Validate, then read the checks.
function Invoke-Validate {
    Invoke-El (Need-El 'btnValidate')
    Start-Sleep -Seconds 3
    $ended = Wait-Until { $m = Get-Text 'txtValidationMessage' 1; ($m -and $m -cne 'Running the checks.') -or (Get-Text 'txtFormMessage' 0.5) } 300 'Validate'
    foreach ($r in @(Read-Matrix 'dgChecks')) { Say "  check: $($r.Cells -join ' | ')" }
    Say "  validation='$(Get-Text 'txtValidationMessage' 1)' form='$(Get-Text 'txtFormMessage' 1)'"
    Say "  to review: $(Get-Text 'txtDeploymentReview')"
    $ended
}
# Select Deploy and return $true when the plan window opens. Nothing is created before its confirm button, which this script never selects.
function Open-DeployPlan {
    Invoke-El (Need-El 'btnDeploy')
    Start-Sleep -Seconds 3
    [bool](Wait-El 'btnChangePlanCancel' 90)
}

$steps['deployment-helper'] = {
    $data = New-StepData 'deployment-helper'
    Start-StepApp $data (New-Settings $data $false)
    Open-Page 'Deployment Helper'
    Start-Sleep -Seconds 12
    Select-DeployRow 'txtObjectFilter' 'dgObjects' $GridApp ('^' + [regex]::Escape($GridApp) + '( .*)?$')
    Select-DeployRow 'txtCollectionFilter' 'dgCollections' $Collection ('^' + [regex]::Escape($Collection) + '$')
    Require 'Validate ends' (Invoke-Validate) 'see the checks above'
    $message = Get-Text 'txtValidationMessage'
    $review = Get-Text 'txtDeploymentReview'
    Check 'Validate gives a result for the application and the empty collection' ($message -and $review -like "*$GridApp*" -and $review -like "*$Collection*" -and $review -match '\b0 members\b') "validation '$message'; review '$review'"
    Snap 'checks'
    $plan = Open-DeployPlan
    if (-not $plan -and $message -match 'did not pass') {
        # A new application has no content on a distribution point: the plan can distribute it first. The plan is only read.
        Say "  no plan while a check blocks; asking for a plan that distributes first. form='$(Get-Text 'txtFormMessage' 1)'"
        $groups = Need-El 'dgDpGroups'
        $groupBox = Get-Items $groups ([System.Windows.Automation.ControlType]::CheckBox) | Select-Object -First 1
        Require 'there is a distribution point group' ([bool]$groupBox) 'dgDpGroups'
        Set-Check $groupBox $true
        Set-Check (Need-El 'chkDistributeFirst') $true
        Start-Sleep -Seconds 2
        $null = Invoke-Validate
        $plan = Open-DeployPlan
    }
    Require 'the deploy plan opens' $plan "form='$(Get-Text 'txtFormMessage' 1)'"
    Start-Sleep -Seconds 2
    $summary = Get-Text 'txtChangePlanSummary'
    $notes = Find-El $null 'txtChangePlanNotes' -Timeout 2
    $notesText = if ($notes) { [string]$notes.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } else { '' }
    $changes = @(Read-Matrix 'gridChangePlan' | ForEach-Object { $_.Cells -join ' | ' })
    Say "  plan summary: $summary"
    foreach ($c in $changes) { Say "  plan change: $c" }
    Say "  plan notes: $notesText"
    Snap 'plan'
    $all = "$summary $notesText $($changes -join ' ')"
    Check 'the plan names the application and the collection' ($all -like "*$GridApp*" -and $all -like "*$Collection*") 'see the plan above'
    Click-Id 'btnChangePlanCancel' 10
    Start-Sleep -Seconds 3
    Check 'the plan is closed without a deployment' (-not (Find-El $null 'btnChangePlanCancel' -Timeout 2)) 'Cancel was selected; the confirm button was never selected'
}

# ---------------- the run
try {
    # A run by the suite launcher would hand a site to the app: the check wants the app as a user starts it.
    foreach ($v in @(Get-ChildItem Env: | Where-Object { $_.Name -like 'APP_PACKAGER_*' })) { [Environment]::SetEnvironmentVariable($v.Name, $null) }
    $unknown = @($Only | Where-Object { $stepNames -notcontains $_ })
    if ($unknown) { throw "unknown step name(s) in -Only: $($unknown -join ', '). The names are: $($stepNames -join ', ')" }
    if ((Test-Path -LiteralPath $Run) -and @(Get-ChildItem -LiteralPath $Run -Force).Count -gt 0) { throw "-Run must be a new or empty folder: $Run" }
    $null = New-Item -ItemType Directory -Force $Run
    if (-not (Test-Path -LiteralPath $exe)) { throw "no app at $exe (give -AppFolder)" }
    $item = Get-Item -LiteralPath $exe
    Say "app: $exe, version $($item.VersionInfo.ProductVersion)"
    foreach ($name in $stepNames) {
        if ($Only.Count -and $Only -notcontains $name) { continue }
        $script:StepName = $name
        Say "---- step $name"
        try { & $steps[$name] }
        catch {
            $script:Fails++
            Say "FAIL $name stopped | $($_.Exception.Message)"
            Say "  at $($_.InvocationInfo.ScriptName):$($_.InvocationInfo.ScriptLineNumber)"
            try { Say "  windows: $(@(Get-WindowNames) -join ' | ')" } catch { }
            Snap 'failed'
        }
        finally { Stop-Apd }
    }
}
catch { $script:Fails++; Say "FAIL run | $($_.Exception.Message)" }
finally {
    Stop-Apd
    Say '---- removal record (the run removes nothing)'
    if ($script:Records.Count) { foreach ($r in $script:Records) { Say $r } } else { Say 'RECORD none: no publish was started' }
    Say "E2E-FAILS=$script:Fails"
}
exit 0
