<#
.SYNOPSIS
    The lab check of the V1 overhaul phase 5a (queue row Q-126): the publish review window and, with -Publish, the results window, in the real app on CLIENT01.

.DESCRIPTION
    Run under the capture lock (Run-VisibleProbe.ps1), from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <folder> -ProbeArgs "& .\Probe-OverhaulV5.ps1 -Run C:\guide\runs\v5-<timestamp> -Publish"
    Without -Publish nothing is written: Publish All opens the review, the probe reads it and presses Back. With -Publish the 7-Zip application is staged and published
    to ConfigMgr through the review, the results window is read, and the caller removes the application from the site afterward.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [switch]$PublishOnly,
    [string]$AppFolder = $PSScriptRoot,
    [switch]$Publish,
    [string]$Application = '7-Zip',
    # With -Publish: the name of the application in the Application column of the review. The review is confirmed only for exactly this row.
    [string]$ApplicationName = $Application
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value

function Shot([string]$Theme, [string]$Id) { Save-Shot (Join-Path $Run (Join-Path 'images' (Join-Path $Theme.ToLower() ($Id + '.png')))) -MaskFooter; "OK $Theme $Id" }
function Main-Window { Get-ApdWindows | Where-Object { $_.Current.Name -match 'App Packager' } | Select-Object -First 1 }
function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Step([string]$Theme, [string]$Id, [scriptblock]$Body) { try { & $Body } catch { "FAIL $Theme ${Id}: $($_.Exception.Message)" } }
function Names([string[]]$Ids) { foreach ($i in $Ids) { $e = Find-El $null $i -Timeout 5; "$i = " + $(if ($e) { $e.Current.Name } else { '(absent)' }) } }
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

foreach ($theme in $Themes) {
    $data = Join-Path $Run "data-$theme"
    $staging = Join-Path $data (Join-Path 'Users' (Join-Path $sid 'Staging'))
    New-Item -ItemType Directory -Force $staging | Out-Null
    & icacls $staging /inheritance:r /grant:r "${env:USERNAME}:(OI)(CI)F" '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
    $settings = @{ FileShareRoot = '\\cm01.contoso.com\ContentShare$'; DownloadRoot = $staging; Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false } }
    $null = Start-Apd $exe $data $theme $settings
    try {
        $w = Main-Window
        $w.SetFocus()
        Start-Sleep 5
        if (-not $PublishOnly) { Step $theme 'review-back' {
            Check-Row $w $Application
            Check-Row $w 'Notepad'
            Set-ElText (Find-El $w 'txtAppPackagerSearch') $Application
            Start-Sleep 2
            Real-Click (Find-El $w 'btnPublishAll')
            Start-Sleep 4
            Names 'txtReviewCounts', 'txtReviewScope', 'txtReviewHidden', 'txtReviewBlocked', 'btnReviewConfirm', 'btnReviewBack', 'txtReviewComment'
            $cm = Find-El $null -Name 'Create application and deployment type' -Timeout 3
            "matrix cell text found: $([bool]$cm)"
            Move-MouseAway
            Shot $theme 'publish-review'
            Real-Click (Find-El $null 'btnReviewBack' -Timeout 3)
            Start-Sleep 2
            "review closed after Back: $(-not (Find-El $null 'btnReviewConfirm' -Timeout 2))"
            Set-ElText (Find-El $w 'txtAppPackagerSearch') ''
        } }
        if (-not $Publish -or $theme -ne $Themes[0]) { continue }
        Step $theme 'review-publish' {
            # A fresh start so that one application is checked.
            Stop-Apd
            $null = Start-Apd $exe $data $theme $settings
            $w = Main-Window
            $w.SetFocus()
            Start-Sleep 5
            Check-Row $w $Application
            Open-ApdMenuItem $w 'btnCheckMenu' 'miCheckLatest'
            Wait-Idle $w 240
            Real-Click (Find-El $w -Name 'Stage' -ControlType 'Button' -Timeout 5)
            Wait-Idle $w 420
            Set-ElText (Find-El $w 'txtAppPackagerSearch') ''
            Start-Sleep 1
            Assert-ApdSelection $w 1
            Real-Click (Find-El $w 'btnPublishAll')
            Start-Sleep 4
            Names 'txtReviewCounts', 'btnReviewConfirm'
            Confirm-ApdReview -Applications $ApplicationName -Systems 'ConfigMgr' -Comment 'Overhaul V1 lab check'
            Wait-Idle $w 600
            $dlg = $null
            foreach ($i in 1..3) {
                $skip = Find-El $w -Name 'Skip' -ControlType 'Button' -Timeout 3
                if ($skip) { 'Application Already Exists: Skip'; Real-Click $skip; Wait-Idle $w 300 }
            }
            Start-Sleep 3
            Names 'txtResultsHeadline', 'txtResultsPublished', 'txtResultsFailed', 'txtResultsSkipped'
            Move-MouseAway
            Shot $theme 'publish-results'
            Real-Click (Find-El $null 'btnResultsDone' -Timeout 3)
        }
    }
    finally { Stop-Apd }
}
