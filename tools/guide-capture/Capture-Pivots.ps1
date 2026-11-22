<#
.SYNOPSIS
    The lab capture run of the Pivots and Scripts views that need a run (decision D-21): the Script tab with an approved script, the plan of the script run with its two
    confirmation boxes, the results of the run on one device and the History tab with the saved run. The real app is driven with UI Automation, one theme after the other.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <published folder with these scripts> -ProbeArgs "& .\Capture-Pivots.ps1 -Run C:\guide\apd-pending\pivots"
    The images are <Run>\images\<theme>\<id>.png.
    What the site must hold first: the approved script $Script with one parameter, ServiceName, as a string with a default value (the caller creates it with New-CMScript, stores the
    parameter definition with UpdateScript and approves it). The run only reads the status of one service on the device $Device; the script changes nothing.
    A step that fails says FAIL for its image, saves the screen under <Run>\fail and the run goes on.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot,
    [string]$Script = 'Guide - Service status',
    [string]$Device = 'CLIENT01'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'

function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Tab([string]$Id) { Real-Click (Need-El $Id); Start-Sleep 2 }
function Wait-Window([string]$Id, [double]$Seconds = 30) { $e = Wait-El $Id $Seconds; if (-not $e) { throw "the window with $Id did not open" }; Start-Sleep 2; $e }

foreach ($theme in $Themes) {
    $data = Start-RunApp $exe $theme @{ Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false } }
    try {
        $null = Click-Nav 'Pivots and Scripts'; Start-Sleep 6
        Step 'tools-pivots-script' {
            Tab 'tabScript'; Start-Sleep 2
            Select-Combo (Need-El 'cboTargetKind') '*Device*'
            Set-ElText (Need-El 'txtTarget') $Device
            Real-Click (Need-El 'btnResolve'); Start-Sleep 8
            Real-Click (Need-El 'btnRefreshScripts'); Start-Sleep 6
            $null = Pick-Row 'dgApproved' "*$Script*"
            Start-Sleep 3
            Shot 'tools-pivots-script'
        }
        Step 'tools-pivots-plan-script' {
            Real-Click (Need-El 'btnParameters')
            $null = Wait-Window 'btnParametersOk'
            $edit = @((Need-El 'listParameters').FindAll($script:TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Edit))) | Select-Object -First 1)
            if ($edit) { Set-ElText $edit[0] 'Spooler' }
            Real-Click (Need-El 'btnParametersOk'); Start-Sleep 2
            Invoke-El (Need-El 'btnRunScript'); Start-Sleep 1
            $null = Wait-Window 'btnChangePlanConfirm' 90
            foreach ($box in 'chkChangePlanExtra', 'chkChangePlanSecond', 'chkChangePlanDestructive') {
                $el = Find-El $null $box -Timeout 2
                if ($el -and -not $el.Current.IsOffscreen) { Set-Check $el $true }
            }
            Start-Sleep 1
            Shot 'tools-pivots-plan-script'
            Real-Click (Need-El 'btnChangePlanConfirm')
            # The results come back on the page: the status line at the foot says "Run n ended" when the wait is over.
            $deadline = (Get-Date).AddSeconds(100)
            do {
                Start-Sleep 5
                $status = $(try { (Find-El $null 'txtPivotsStatus' -Timeout 2).Current.Name } catch { '' })
            } while ((Get-Date) -lt $deadline -and $status -notmatch 'ended')
            "pivots status: $status"
            $close = Find-El $null 'btnChangePlanClose' -Timeout 2
            if ($close -and -not $close.Current.IsOffscreen) { Real-Click $close }
            Start-Sleep 3
        }
        Step 'tools-pivots-results' {
            $null = Scroll-ToId 'txtResultHeader'
            # Two more notches of the wheel: the result rows come into view under the header.
            for ($i = 0; $i -lt 6; $i++) { [ApdW]::mouse_event(0x0800, 0, 0, [uint32]4294967176, [UIntPtr]::Zero); Start-Sleep -Milliseconds 400 }
            Start-Sleep 2
            Shot 'tools-pivots-results'
        }
        Step 'tools-pivots-history' {
            Tab 'tabPivotsHistory'; Start-Sleep 3
            Real-Click (Need-El 'btnPivotsHistoryRefresh'); Start-Sleep 3
            $null = Pick-First 'dgSessions'
            Start-Sleep 2
            Shot 'tools-pivots-history'
        }
    }
    finally { Stop-Apd }
}
