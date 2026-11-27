<#
.SYNOPSIS
    The lab capture run of the DP Content Manager views that need a failed distribution (decision D-21): the Status Issues tab with a failed pair, the result panel after a redistribute and
    the History tab with the record of a removal. The real app is driven with UI Automation, one theme after the other.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <published folder with these scripts> -ProbeArgs "& .\Capture-ToolsDp.ps1 -Run C:\guide\apd-pending\dp"
    What the site must hold first (the caller creates it and deletes it after the run): for each theme one throw-away package "ZZAPD DP Guide <Theme>" whose source folder does
    not exist, distributed to the distribution point. The distribution of such a package fails, so the Status Issues tab lists the pair.
    The run redistributes the failing package (the distribution fails again) and removes the content of the package from the distribution point.
    A step that fails says FAIL for its image, saves the screen under <Run>\fail and the run goes on.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot,
    # The copy of the record file of the seed script: only the recorded packages can be redistributed or removed.
    [string]$RecordFile = (Join-Path $PSScriptRoot 'zzapd-records.local.json'),
    [string]$DistributionPoint = 'CM01.CONTOSO.COM'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
. (Join-Path $PSScriptRoot 'PublishCleanup.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'

function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Tab([string]$Id) { Real-Click (Need-El $Id); Start-Sleep 2 }
function Wait-Window([string]$Id, [double]$Seconds = 30) { $e = Wait-El $Id $Seconds; if (-not $e) { throw "the window with $Id did not open" }; Start-Sleep 2; $e }
# The pair window of an action: check every pair, then continue to the plan.
function Pairs-Continue {
    $null = Wait-Window 'btnPairsContinue' 60
    Real-Click (Need-El 'btnPairsCheckShown'); Start-Sleep 1
    Real-Click (Need-El 'btnPairsContinue')
}
# Ticks the boxes that the plan asks, runs its action button, waits for the result and closes the window.
function Finish-Plan([string]$Action, [int]$Seconds = 240) {
    foreach ($box in 'chkChangePlanExtra', 'chkChangePlanDestructive', 'chkChangePlanSecond') {
        $el = Find-El $null $box -Timeout 2
        if ($el -and -not $el.Current.IsOffscreen) { Set-Check $el $true }
    }
    # Only the pair of the recorded package of this theme and the distribution point is confirmed, with the action of the step.
    Assert-ChangePlanRows @($script:AllowedObjects | ForEach-Object { @{ Action = $Action; Object = '^' + [regex]::Escape($_) + '$'; Count = 1 } })
    Real-Click (Need-El 'btnChangePlanConfirm')
    $close = $null
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline -and -not $close) {
        Start-Sleep 3
        $c = Find-El $null 'btnChangePlanClose' -Timeout 1
        if ($c -and -not $c.Current.IsOffscreen -and $c.Current.IsEnabled) { $close = $c }
    }
    if (-not $close) { throw 'the plan did not finish' }
    $r = Find-El $null 'txtChangePlanResult' -Timeout 2
    "plan result: " + $(if ($r) { $r.Current.Name } else { '(no text)' })
    Start-Sleep 1
    Real-Click $close; Start-Sleep 3
}

Connect-GuideSite
# Checked for every theme before the first one starts.
$packages = @{}
foreach ($theme in $Themes) { $packages[$theme] = @(Get-GuideRecordedPackages $RecordFile @("ZZAPD DP Guide $theme") "ZZAPD DP Guide $theme")[0] }

foreach ($theme in $Themes) {
    $package = "ZZAPD DP Guide $theme"
    $script:AllowedObjects = @("$package ($($packages[$theme].Id)) on $DistributionPoint")
    $data = Start-RunApp $exe $theme @{ Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false } }
    try {
        $null = Click-Nav 'DP Content Manager'; Start-Sleep 14
        Step 'tools-dp-content-issues' {
            Tab 'tabIssues'; Start-Sleep 3
            Set-ElText (Need-El 'txtIssueFilter') $package; Start-Sleep 3
            $null = Pick-Row 'dgIssues' "*$package*"
            Start-Sleep 2
            Shot 'tools-dp-content-issues'
        }
        Step 'tools-dp-content-results' {
            Real-Click (Need-El 'btnRedistributeIssues')
            Pairs-Continue
            $null = Wait-Window 'btnChangePlanConfirm' 90
            Finish-Plan 'Redistribute'
            Start-Sleep 3
            $null = Wait-El 'pnlDpContentResults' 30
            Start-Sleep 2
            Shot 'tools-dp-content-results'
        }
        Step 'tools-dp-content-history' {
            Tab 'tabIssues'; Start-Sleep 2
            $null = Pick-Row 'dgIssues' "*$package*"
            Real-Click (Need-El 'btnRemoveFailures')
            Pairs-Continue
            $null = Wait-Window 'btnChangePlanConfirm' 90
            Finish-Plan 'Remove from DP'
            Tab 'tabDpHistory'; Start-Sleep 3
            Real-Click (Need-El 'btnDpHistoryRefresh'); Start-Sleep 3
            $null = Pick-First 'dgDpHistory'
            Start-Sleep 2
            Shot 'tools-dp-content-history'
        }
    }
    finally { Stop-Apd }
}
