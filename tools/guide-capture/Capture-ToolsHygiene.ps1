<#
.SYNOPSIS
    The lab capture run of the Site Hygiene views that need a state (decision D-21): the import of the retired Health Dashboard, the Live views, the Trends and Alerts views, the Live settings,
    and two fix runs with the picker, the plan and the History tab. The real app is driven with UI Automation, one theme after the other.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <published folder with these scripts> -ProbeArgs "& .\Capture-ToolsHygiene.ps1 -Run C:\guide\apd-pending\hygiene"
    The images are <Run>\images\<theme>\<id>.png. The Live views read the site and the site database.
    What the site must hold first (throw-away packages that the caller creates and the two fix runs delete): for each theme three standard packages without a program, named
    "ZZAPD Hygiene <Theme> 1", "... 2" and "... 3". The first fix run deletes the packages 1 and 2 (the picker shows two objects), the second run deletes the package 3.
    A step that fails says FAIL for its image, saves the screen under <Run>\fail and the run goes on.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot,
    [switch]$SkipFixes
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$ctItem = [System.Windows.Automation.ControlType]::DataItem

function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Tab([string]$Id) { Real-Click (Need-El $Id); Start-Sleep 2 }
function Wait-Window([string]$Id, [double]$Seconds = 30) { $e = Wait-El $Id $Seconds; if (-not $e) { throw "the window with $Id did not open" }; Start-Sleep 2; $e }
function Combo([string]$Id, [string]$Like) { Select-Combo (Need-El $Id) $Like }
function Cancel-Plan { $c = Find-El $null 'btnChangePlanCancel' -Timeout 5; if ($c) { Real-Click $c; Start-Sleep 2 } }

# Selects every visible row of the findings grid (the filter shows only the rows of the run).
function Select-AllFindings {
    $grid = Need-El 'dgFindings'
    $rows = @($grid.FindAll($script:TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), $ctItem)) | Where-Object { -not $_.Current.IsOffscreen -and -not $_.Current.BoundingRectangle.IsEmpty })
    if (-not $rows) { throw 'the findings grid has no row' }
    $i = 0
    foreach ($r in $rows) {
        $p = $r.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        if ($i -eq 0) { $p.Select() } else { $p.AddToSelection() }
        $i++
    }
    Start-Sleep 1
    $rows.Count
}

# Runs the plan window: the boxes that the plan asks, then the action button; waits for the result and closes the window.
function Confirm-Plan([int]$Seconds = 300) {
    foreach ($box in 'chkChangePlanExtra', 'chkChangePlanDestructive', 'chkChangePlanSecond') {
        $el = Find-El $null $box -Timeout 2
        if ($el -and -not $el.Current.IsOffscreen) { Set-Check $el $true }
    }
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
    Real-Click $close; Start-Sleep 2
}

foreach ($theme in $Themes) {
    $data = Start-RunApp $exe $theme @{}
    try {
        $null = Click-Nav 'Site Hygiene'; Start-Sleep 5
        # ---- The offer to import the retired Health Dashboard (it shows before the first refresh)
        Step 'tools-site-hygiene-import' {
            Tab 'tabHygieneLive'; Start-Sleep 2
            $offer = Wait-El 'btnImportOffer' 20
            if (-not $offer) { $offer = Wait-El 'btnImportDashboard' 5 }
            if (-not $offer) { throw 'no offer to import' }
            Real-Click $offer
            $null = Wait-Window 'btnImportCancel'
            Shot 'tools-site-hygiene-import'
            Real-Click (Need-El 'btnImportCancel'); Start-Sleep 2
            # Not now hides the offer, so the Live views show without the banner.
            $no = Find-El $null 'btnImportNotNow' -Timeout 3
            if ($no) { Real-Click $no; Start-Sleep 2 }
        }
        # ---- The Live views
        Step 'tools-site-hygiene-live-deployments' {
            Real-Click (Need-El 'btnLiveRefresh')
            $deadline = (Get-Date).AddSeconds(300)
            do { Start-Sleep 5; $t = (Need-El 'txtHygieneLiveSummary').Current.Name; $busy = Find-El $null 'btnLiveCancel' -Timeout 1 } while ((Get-Date) -lt $deadline -and ($busy -and $busy.Current.IsEnabled))
            "live summary: $t"
            Combo 'cboLiveView' 'Deployments*'; Start-Sleep 3
            Shot 'tools-site-hygiene-live-deployments'
        }
        foreach ($v in @(@('Content*', 'tools-site-hygiene-live-content'), @('Distribution*', 'tools-site-hygiene-live-dps'), @('Client Health*', 'tools-site-hygiene-live-clients'),
                         @('Inactive*', 'tools-site-hygiene-live-inactive'), @('Site Health*', 'tools-site-hygiene-live-site-health'), @('Trends*', 'tools-site-hygiene-trends'))) {
            Step $v[1] {
                Combo 'cboLiveView' $v[0]; Start-Sleep 4
                Shot $v[1]
            }
        }
        # An alert fires when a value goes into breach after the first complete read. The deployment compliance of the lab is below 80 percent: the first refresh above set the
        # starting state with the floor at 80 (no alert). Lower the floor to 70, refresh (not in breach), raise it to 80 again and refresh: the value is newly in breach and the alert fires.
        Step 'tools-site-hygiene-alerts' {
            function Refresh-Live {
                Real-Click (Need-El 'btnLiveRefresh')
                $end = (Get-Date).AddSeconds(180)
                do { Start-Sleep 5; $busy = Find-El $null 'btnLiveCancel' -Timeout 1 } while ((Get-Date) -lt $end -and ($busy -and $busy.Current.IsEnabled))
                Start-Sleep 2
            }
            foreach ($floor in '70*', '80*') {
                Tab 'tabHygieneOptions'; Start-Sleep 2
                $null = Scroll-ToId 'cboComplianceFloor'
                Combo 'cboComplianceFloor' $floor; Start-Sleep 2
                Tab 'tabHygieneLive'; Start-Sleep 2
                Refresh-Live
            }
            $banner = Find-El $null 'txtAlertBanner' -Timeout 5
            if (-not $banner -or $banner.Current.IsOffscreen) { throw 'no alert was raised' }
            "alert banner: $($banner.Current.Name)"
            Combo 'cboLiveView' 'Alerts*'; Start-Sleep 3
            Shot 'tools-site-hygiene-alerts'
        }
        Step 'tools-site-hygiene-live-options' {
            Tab 'tabHygieneOptions'; Start-Sleep 3
            $null = Scroll-ToId 'cboComplianceFloor'
            Start-Sleep 2
            Shot 'tools-site-hygiene-live-options'
        }
        # ---- The scan, then two fix runs on the throw-away packages
        if (-not $SkipFixes) {
            Step 'tools-site-hygiene-scan' {
                $w = Main-Window
                $w.SetFocus()
                Tab 'tabHygieneFindings'
                Real-Click (Need-El 'btnHygieneScan')
                $deadline = (Get-Date).AddMinutes(8)
                do { Start-Sleep 5; $text = (Need-El 'txtHygieneSummary').Current.Name } while ((Get-Date) -lt $deadline -and $text -notmatch 'finding')
                "scan: $text"
                Tab 'tabHygieneFindings'
            }
            $key = "ZZAPD Hygiene $theme"
            Step 'tools-site-hygiene-picker' {
                Set-ElText (Need-El 'txtFindingFilter') "$key 1"; Start-Sleep 2
                # Rows 1 and 2: the filter text matches the names that start with the key, so the grid is filtered to the packages 1 and 2 by two passes.
                Set-ElText (Need-El 'txtFindingFilter') $key; Start-Sleep 3
                $rows = Select-AllFindings
                "findings of the run: $rows"
                if ($rows -ge 3) {
                    # Keep two rows: clear the selection of the last row.
                    $grid = Need-El 'dgFindings'
                    $items = @($grid.FindAll($script:TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), $ctItem)) | Where-Object { -not $_.Current.IsOffscreen -and -not $_.Current.BoundingRectangle.IsEmpty })
                    $items[$items.Count - 1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).RemoveFromSelection()
                }
                Real-Click (Need-El 'btnHygieneRunFix')
                $null = Wait-Window 'btnHygienePickContinue' 120
                Shot 'tools-site-hygiene-picker'
                Real-Click (Need-El 'btnHygienePickContinue')
            }
            Step 'tools-site-hygiene-plan-fix' {
                $null = Wait-Window 'btnChangePlanConfirm' 180
                Shot 'tools-site-hygiene-plan-fix'
                Confirm-Plan 300
            }
            Step 'tools-site-hygiene-history' {
                # The second run: the remaining package of the run.
                Set-ElText (Need-El 'txtFindingFilter') $key; Start-Sleep 3
                $null = Select-AllFindings
                Real-Click (Need-El 'btnHygieneRunFix')
                $null = Wait-Window 'btnChangePlanConfirm' 180
                Confirm-Plan 300
                Tab 'tabHygieneHistory'; Start-Sleep 2
                Real-Click (Need-El 'btnHygieneHistoryRefresh'); Start-Sleep 3
                Shot 'tools-site-hygiene-history'
            }
        }
    }
    finally { Stop-Apd }
}
