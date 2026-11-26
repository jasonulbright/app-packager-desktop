<#
.SYNOPSIS
    The lab check of the V1 overhaul phases 3c, 5b, 6, 7a, 7b, 7c and 7d (queue row Q-126) in the real app on CLIENT01.

.DESCRIPTION
    Run under the capture lock (Run-VisibleProbe.ps1), from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <folder> -ProbeArgs "& .\Probe-OverhaulV6.ps1 -Run C:\guide\runs\v6-<timestamp>"
    Without -Recheck nothing is written to the site: each window is opened, read, captured and closed.
    With -Recheck, the probe checks the row of an application that the lab site already has, opens Publish All with no check before it, confirms,
    and expects the review to open again with nothing published. It then presses Back. If the review does not open again, the run would publish:
    use this only on the lab.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot,
    [switch]$Recheck,
    [string]$Application = '7-Zip',
    [string]$TrackedId = 'fa505d1415214f14'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value

function Shot([string]$Theme, [string]$Id) { Save-Shot (Join-Path $Run (Join-Path 'images' (Join-Path $Theme.ToLower() ($Id + '.png')))) -MaskFooter; "OK $Theme $Id" }
function Main-Window { Get-ApdWindows | Where-Object { $_.Current.Name -match 'App Packager' } | Select-Object -First 1 }
function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Step([string]$Theme, [string]$Id, [scriptblock]$Body) { try { & $Body } catch { "FAIL $Theme ${Id}: $($_.Exception.Message)" } }
function Names([string[]]$Ids) { foreach ($i in $Ids) { $e = Find-El $null $i -Timeout 4; "$i = " + $(if ($e) { $e.Current.Name } else { '(absent)' }) } }
function Press-Escape { Send-ApdKeys '{ESC}'; Start-Sleep -Milliseconds 700 }
function Wait-Idle($w, [int]$Seconds) {
    Start-Sleep 4
    $deadline = (Get-Date).AddSeconds($Seconds); $beat = Get-Date
    while ((Get-Date) -lt $deadline -and (Find-El $w 'btnCancelOperation' -Timeout 1)) {
        if (((Get-Date) - $beat).TotalSeconds -ge 30) { 'waiting'; $beat = Get-Date }
        Start-Sleep 2
    }
}
function Check-Row($w, [string]$Name) {
    Set-ElText (Find-El $w 'txtAppPackagerSearch') $Name
    Start-Sleep 2
    $grid = Find-El $w 'PackagerGrid'
    $box = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::CheckBox))) |
        Where-Object { $_.Current.AutomationId -notin 'SelectAllCheckBox', 'chkSelectAllPackages' } | Select-Object -First 1
    if ($box -and $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { Real-Click $box }
}
function Show-Menu($w, [string]$Theme, [string]$Button, [string[]]$Items, [string]$Id) {
    Real-Click (Find-El $w $Button -Timeout 5)
    Names $Items
    Shot $Theme $Id
    Press-Escape
}
function Open-Page([string]$Name) {
    if (-not (Click-Nav $Name)) { throw "the sidebar has no entry $Name" }
    Start-Sleep 3
}

foreach ($theme in $Themes) {
    $data = Join-Path $Run "data-$theme"
    $staging = Join-Path $data (Join-Path 'Users' (Join-Path $sid 'Staging'))
    New-Item -ItemType Directory -Force $staging | Out-Null
    & icacls $staging /inheritance:r /grant:r "${env:USERNAME}:(OI)(CI)F" '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
    $settings = @{ FileShareRoot = '\\cm01.contoso.com\ContentShare$'; DownloadRoot = $staging; Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false }
        OneClick = @{ TrackedApplications = @($TrackedId); Action = 'Report' } }
    $null = Start-Apd $exe $data $theme $settings
    try {
        $w = Main-Window
        $w.SetFocus()
        Start-Sleep 6
        Step $theme 'packages-layout' {
            Names 'btnOneClick', 'btnCheckMenu', 'btnColumns', 'btnMore', 'btnAddOrImport', 'btnStage', 'btnPublishAll', 'txtSelectionCount', 'txtPublishScope'
            Move-MouseAway
            Shot $theme 'packages-v1'
        }
        Step $theme 'menus' {
            Show-Menu $w $theme 'btnCheckMenu' @('miCheckLatest', 'miCheckConfigMgr', 'miCheckCves') 'menu-check'
            Show-Menu $w $theme 'btnMore' @('miCveReport', 'miOperationHistory', 'miExportCheckResults', 'miShowDebugColumns', 'miSetup', 'miSetupChecklist') 'menu-more'
            Show-Menu $w $theme 'btnAddOrImport' @('miAddFromWeb', 'miAddFromFile', 'miConvertFromCm', 'miImportFromUrl') 'menu-add'
        }
        Step $theme 'checklist' {
            Open-ApdMenuItem $w 'btnMore' 'miSetupChecklist'
            Start-Sleep 2
            Move-MouseAway
            Shot $theme 'setup-checklist'
            # The checklist stands where the detail pane is, so it is closed before the later steps.
            $closeList = Find-El $w 'btnSetupClose' -Timeout 3
            if ($closeList) { Real-Click $closeList }
        }
        Step $theme 'one-click-plan' {
            # The saved action is Report, so One Click runs the checks only and the plan window writes nothing.
            Open-Page 'App Packager'
            Real-Click (Find-El $w 'btnOneClick' -Timeout 5)
            # PowerShell names have no case: a variable $run would hide the parameter $Run, also inside Shot, and every screenshot path would break.
            $planButton = $null; $beat = Get-Date; $deadline = (Get-Date).AddMinutes(4)
            while ((Get-Date) -lt $deadline -and -not $planButton) {
                $planButton = Find-El $null 'btnPlanOnly' -Timeout 3
                if (((Get-Date) - $beat).TotalSeconds -ge 30) { 'waiting'; $beat = Get-Date }
            }
            if (-not $planButton) { throw 'the plan window did not open' }
            Names 'txtPlanHeader', 'txtPlanFlow', 'txtPlanSystems', 'txtPlanComment', 'btnHistory', 'btnRun', 'btnClose'
            Move-MouseAway
            Save-Shot (Join-Path $Run (Join-Path 'images' (Join-Path $theme.ToLower() 'one-click-plan.png')))
            Real-Click (Find-El $null 'btnHistory' -Timeout 3)
            Names 'txtHistorySummary', 'btnHistoryClose'
            Shot $theme 'one-click-history'
            Real-Click (Find-El $null 'btnHistoryClose' -Timeout 3)
            Real-Click (Find-El $null 'btnClose' -Timeout 3)
        }
        Step $theme 'deployment-helper' {
            Open-Page 'Deployment Helper'
            Names 'tabHistory', 'tabOptions', 'btnDeploy'
            Move-MouseAway
            Shot $theme 'deployment-helper'
        }
        Step $theme 'add-from-web' {
            Open-Page 'Add from web'
            Move-MouseAway
            Shot $theme 'add-from-web'
        }
        Step $theme 'settings' {
            Open-Page 'Settings'
            $node = Find-El $w -Name 'ConfigMgr' -ControlType 'TreeItem' -Timeout 3
            if ($node) { Real-Click $node }
            Names 'btnSaveSettings', 'btnDiscardChanges'
            Move-MouseAway
            Shot $theme 'settings-connection'
        }
        Step $theme 'workbench' {
            Open-Page 'App Packager'
            Check-Row $w $Application
            $row = (Find-El $w 'PackagerGrid').FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::DataItem)))
            Real-Click $row
            $open = Find-El $w 'btnDetailWorkbench' -Timeout 3
            if (-not $open) { throw 'no Open in Workbench button' }
            Real-Click $open
            Start-Sleep 6
            Names 'btnWorkbenchPublishAll'
            Move-MouseAway
            Shot $theme 'workbench'
            $close = Get-ApdWindows | Where-Object { $_.Current.Name -match 'Workbench' } | Select-Object -First 1
            if ($close) { $close.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 2 }
        }
        # The recheck runs in the first theme of -Themes only: it tests the flow, not the look, and one pass reads the site once.
        if (-not $Recheck -or $theme -ne $Themes[0]) { continue }
        Step $theme 'recheck' {
            Stop-Apd
            # A data folder of its own: a stored check from an earlier step would give the first review the site's answer.
            $fresh = Join-Path $Run 'data-recheck'
            $freshStaging = Join-Path $fresh (Join-Path 'Users' (Join-Path $sid 'Staging'))
            New-Item -ItemType Directory -Force $freshStaging | Out-Null
            & icacls $freshStaging /inheritance:r /grant:r "${env:USERNAME}:(OI)(CI)F" '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
            $settings.DownloadRoot = $freshStaging
            $null = Start-Apd $exe $fresh $theme $settings
            $w = Main-Window
            $w.SetFocus()
            Start-Sleep 6
            # No check runs first: the plan then knows no ConfigMgr application, and the read after Confirm finds the one the lab site has.
            Check-Row $w $Application
            Real-Click (Find-El $w 'btnPublishAll')
            Start-Sleep 4
            Names 'txtReviewCounts', 'btnReviewConfirm'
            Move-MouseAway
            Shot $theme 'recheck-first-review'
            Real-Click (Find-El $null 'btnReviewConfirm' -Timeout 5)
            $changed = $null
            foreach ($i in 1..40) { $changed = Find-El $null 'txtReviewChanged' -Timeout 3; if ($changed -and $changed.Current.Name) { break } }
            "review opened again: $([bool]($changed -and $changed.Current.Name))"
            "txtReviewChanged = $(if ($changed) { $changed.Current.Name } else { '(absent)' })"
            Move-MouseAway
            Shot $theme 'recheck-second-review'
            Real-Click (Find-El $null 'btnReviewBack' -Timeout 5)
        }
    }
    finally { Stop-Apd }
}
