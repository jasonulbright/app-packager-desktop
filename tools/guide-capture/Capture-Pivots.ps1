<#
.SYNOPSIS
    The lab capture run of the Pivots and Scripts views that need a run (decision D-21): the Script tab with an approved script, the plan of the script run with its two
    confirmation boxes, the results of the run on one device, the History tab with the saved run and the Pivot tab with a query and its results. The real app is driven with UI Automation, one theme after the other.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-InSession.ps1 takes it), from the folder of the published app.
    The images are <Run>\images\<theme>\<id>.png.
    The site must hold the approved script "ZZAPD Service status" with the string parameter ServiceName (tools/guide-capture/lab-seed, part Script), and a copy
    of the record file of the seed script lies beside this script. The run reads the status of one service on CLIENT01; the script changes nothing.
    The run stops before the app starts when the script on the site is not the recorded one, and stops when the first step fails. It confirms the plan only
    when the plan runs that script, with the recorded GUID, on CLIENT01 only.
    The Pivot image runs the query "BIOS and serial" of the query library (CMPivot reads data of the client and changes nothing on it; the site records the
    client operation). The plan of the query is confirmed only when it holds one row, Run query on CLIENT01, shows the query of the editor and asks for no
    confirmation box.
    A step that fails says FAIL for its image, saves the screen under <Run>\fail and the run goes on.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot,
    # The copy of the record file of the seed script: it holds the GUID of the script that the run may start.
    [string]$RecordFile = (Join-Path $PSScriptRoot 'zzapd-records.local.json')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
. (Join-Path $PSScriptRoot 'PublishCleanup.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
# The only script and the only device that this run may use.
$Script = 'ZZAPD Service status'
$Device = 'CLIENT01'
$recorded = @(Read-GuideRecordCopy $RecordFile | Where-Object { $_.Type -eq 'Script' -and $_.Name -ceq $Script })
if ($recorded.Count -ne 1) { throw "The record file must hold exactly one script '$Script'." }
$guid = ([string]$recorded[0].Id).Trim('{}').ToUpperInvariant()
Connect-GuideSite
$onSite = @(Invoke-GuideSite { Get-CMScript -ScriptName $Script -Fast -ErrorAction Stop })
if ($onSite.Count -ne 1 -or ([string]$onSite[0].ScriptGuid).Trim('{}').ToUpperInvariant() -ne $guid -or [int]$onSite[0].ApprovalState -ne 3) {
    throw "The site must hold exactly one script '$Script', approved, with the recorded GUID $guid. The run does not start."
}

# Before the run is confirmed: one row, which runs the script on the device and on no other; the plan names the script with the recorded GUID.
# The rows of this plan name the device only; the script and its GUID stand in the notes of the plan and in a confirmation box.
function Assert-ScriptRunPlan {
    $rows = @(Get-ChangePlanRows)
    if ($rows.Count -ne 1 -or $rows[0].Action -cne 'Run script' -or $rows[0].Object -cnotmatch ('^' + $Device + ' \(ResourceID \d+\)$')) {
        throw "The plan must hold one row that runs the script on $Device only: $(($rows | ForEach-Object { "$($_.Action) $($_.Object)" }) -join '; '). Nothing was confirmed."
    }
    $notes = [string](Need-El 'txtChangePlanNotes' 10).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($notes -notmatch ('(?m)^Targets: Device ' + $Device + '\. 1 device\.')) { throw "The plan does not name the one target $Device. Nothing was confirmed." }
    $named = [regex]::Matches($notes, '(?m)^Script: (.+?)\. Version \d+\. GUID \{?([0-9A-Fa-f-]{36})\}?\.')
    if ($named.Count -ne 1 -or $named[0].Groups[1].Value -cne $Script -or $named[0].Groups[2].Value.ToUpperInvariant() -ne $guid) {
        throw "The plan does not name the script '$Script' with the recorded GUID $guid. Nothing was confirmed."
    }
    $box = [string](Need-El 'chkChangePlanExtra' 5).Current.Name
    if (-not $box.StartsWith("Run $Script version ", [StringComparison]::Ordinal)) { throw "The confirmation box names another run: '$box'. Nothing was confirmed." }
}

# Before a query is confirmed: one row that runs the query on the one device, the text of the editor, and no confirmation box.
function Assert-QueryPlan([string]$Query) {
    Assert-ChangePlanRows @{ Action = 'Run query'; Object = '^' + $Device + ' \(ResourceID \d+\)$'; Count = 1 }
    if (@(Get-ChangePlanRows).Count -ne 1) { throw 'The plan of the query holds more than one row. Nothing was confirmed.' }
    $notes = Get-ElValue (Need-El 'txtChangePlanNotes' 10)
    if ($notes -notmatch ('(?m)^Targets: Device ' + $Device + '\. 1 device\.')) { throw "The plan does not name the one target $Device. Nothing was confirmed." }
    $body = (Get-ElValue (Need-El 'txtChangePlanBody' 10)).Replace("`r", '').Trim()
    if ($body -cne $Query.Replace("`r", '').Trim()) { throw "The plan shows another query than the editor: '$body'. Nothing was confirmed." }
    foreach ($box in 'chkChangePlanExtra', 'chkChangePlanDestructive', 'chkChangePlanSecond') {
        $el = Find-El $null $box -Timeout 1
        if ($el -and -not $el.Current.IsOffscreen) { throw "The plan of the query asks for the box $box. Nothing was confirmed." }
    }
}

function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Tab([string]$Id) { Real-Click (Need-El $Id); Start-Sleep 2 }
function Wait-Window([string]$Id, [double]$Seconds = 30) { $e = Wait-El $Id $Seconds; if (-not $e) { throw "the window with $Id did not open" }; Start-Sleep 2; $e }

foreach ($theme in $Themes) {
    $data = Start-RunApp $exe $theme @{ Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false } }
    try {
        $null = Click-Nav 'Pivots and Scripts'; Start-Sleep 6
        $script:PivotsReady = $false
        # The result and the history views are empty without a run, so their images need a confirmed run.
        $script:PivotsRan = $false
        Step 'tools-pivots-script' {
            Tab 'tabScript'; Start-Sleep 2
            Select-Combo (Need-El 'cboTargetKind') '*Device*'
            Set-ElText (Need-El 'txtTarget') $Device
            Real-Click (Need-El 'btnResolve'); Start-Sleep 8
            Real-Click (Need-El 'btnRefreshScripts'); Start-Sleep 6
            $null = Pick-Row 'dgApproved' "*$Script*"
            Start-Sleep 3
            Shot 'tools-pivots-script'
            $script:PivotsReady = $true
        }
        if (-not $script:PivotsReady) { throw 'The first step failed: the run stops, and no script is started.' }
        Step 'tools-pivots-plan-script' {
            Real-Click (Need-El 'btnParameters')
            $null = Wait-Window 'btnParametersOk'
            $edit = @((Need-El 'listParameters').FindAll($script:TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Edit))) | Select-Object -First 1)
            if (-not $edit) { throw 'The parameter window shows no value box. Nothing was run.' }
            Set-ElText $edit[0] 'Spooler'
            Real-Click (Need-El 'btnParametersOk'); Start-Sleep 2
            Invoke-El (Need-El 'btnRunScript'); Start-Sleep 1
            $null = Wait-Window 'btnChangePlanConfirm' 90
            Assert-ScriptRunPlan
            foreach ($box in 'chkChangePlanExtra', 'chkChangePlanSecond', 'chkChangePlanDestructive') {
                $el = Find-El $null $box -Timeout 2
                if ($el -and -not $el.Current.IsOffscreen) { Set-Check $el $true }
            }
            Start-Sleep 1
            Shot 'tools-pivots-plan-script'
            Assert-ScriptRunPlan
            $script:PivotsRan = $true
            Real-Click (Need-El 'btnChangePlanConfirm')
            # The plan window is modal, and the page starts its wait for the results only after the window is closed.
            $close = Wait-El 'btnChangePlanClose' 120 -Enabled
            if (-not $close) { throw 'The plan window did not finish.' }
            Real-Click $close
            # The results come back on the page: the status line at the foot says "Run n ended" when the wait is over.
            $deadline = (Get-Date).AddSeconds(100)
            do {
                Start-Sleep 5
                $status = $(try { (Find-El $null 'txtPivotsStatus' -Timeout 2).Current.Name } catch { '' })
            } while ((Get-Date) -lt $deadline -and $status -notmatch 'ended')
            "pivots status: $status"
            Start-Sleep 3
        }
        Step 'tools-pivots-results' {
            if (-not $script:PivotsRan) { throw 'Not run: no script run was confirmed, so there is no result.' }
            $null = Scroll-ToId 'txtResultHeader'
            # Two more notches of the wheel: the result rows come into view under the header.
            for ($i = 0; $i -lt 6; $i++) { Send-ApdWheel ([uint32]4294967176); Start-Sleep -Milliseconds 400 }
            Start-Sleep 2
            Shot 'tools-pivots-results'
        }
        Step 'tools-pivots-history' {
            if (-not $script:PivotsRan) { throw 'Not run: no script run was confirmed, so there is no saved run.' }
            Tab 'tabPivotsHistory'; Start-Sleep 3
            Real-Click (Need-El 'btnPivotsHistoryRefresh'); Start-Sleep 3
            $null = Pick-First 'dgSessions'
            Start-Sleep 2
            Shot 'tools-pivots-history'
        }
        Step 'tools-pivots-pivot' {
            Tab 'tabPivot'; Start-Sleep 2
            Select-Combo (Need-El 'cboTargetKind') '*Device*'
            Set-ElText (Need-El 'txtTarget') $Device
            Real-Click (Need-El 'btnResolve'); Start-Sleep 8
            $null = Pick-Row 'lstQueryLibrary' 'BIOS and serial'
            Real-Click (Need-El 'btnUseQuery'); Start-Sleep 2
            $query = Get-ElValue (Need-El 'txtQuery')
            if (-not $query.Trim()) { throw 'The editor holds no query after Use query. Nothing was run.' }
            "query: $($query -replace '\s+', ' ')"
            Invoke-El (Need-El 'btnRunPivot'); Start-Sleep 1
            $null = Wait-Window 'btnChangePlanConfirm' 90
            Assert-QueryPlan $query
            Real-Click (Need-El 'btnChangePlanConfirm')
            $close = Wait-El 'btnChangePlanClose' 120 -Enabled
            if (-not $close) { throw 'The plan window did not finish.' }
            Real-Click $close
            Wait-State { @(Get-VisibleRows 'dgResults').Count -ge 1 } 180 "the results grid holds a row of $Device"
            $status = $(try { (Find-El $null 'txtPivotsStatus' -Timeout 2).Current.Name } catch { '' })
            "pivots status: $status"
            Start-Sleep 3
            Shot 'tools-pivots-pivot'
        }
    }
    finally { Stop-Apd }
}
