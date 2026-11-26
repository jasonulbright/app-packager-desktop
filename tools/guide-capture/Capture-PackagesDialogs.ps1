<#
.SYNOPSIS
    The lab capture run of the dialogs and results of the App Packager page (decision D-21, queue rows Q-20a and Q-65c): the sidebar context menu, the One Click plan
    window, the Configure dialog, the result of Stage, and the Application Already Exists dialog. The real app is driven with UI Automation, one theme after the other.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-InSession.ps1 takes it), from the folder of the published app.
    The images are <Run>\images\<theme>\<id>.png. The Stage goes to the staging folder of the run. The application that the run stages and publishes is the guide
    definition zzapd-gsudo. The first Publish creates it on the site; the second Publish meets it and Skip answers the Application Already Exists dialog.
    The run does not start when the site already holds it. At the end of a theme it removes the application and its new content folders, only as far as
    PublishCleanup.ps1 can prove that the run created them. A step that fails says FAIL for its image and the run goes on.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot,
    [string]$Application = 'ZZAPD gsudo',
    [string]$FileShareRoot = '\\cm01.contoso.com\ContentShare$'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
. (Join-Path $PSScriptRoot 'PublishCleanup.ps1')
Connect-GuideSite
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value

function Shot([string]$Theme, [string]$Id) { Save-Shot (Join-Path $Run (Join-Path 'images' (Join-Path $Theme.ToLower() ($Id + '.png')))) -MaskFooter; "OK $Theme $Id" }
function Main-Window { Get-ApdWindows | Where-Object { $_.Current.Name -match 'App Packager' } | Select-Object -First 1 }
function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Status-Text($w) { $e = Find-El $w 'StatusLabel' -Timeout 2; if ($e) { $e.Current.Name } else { '' } }
function Wait-Idle($w, [int]$Seconds) {
    Start-Sleep 4
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline -and (Test-ApdBusy $w)) { Start-Sleep 2 }
}
function Step([string]$Theme, [string]$Id, [scriptblock]$Body) { try { & $Body } catch { "FAIL $Theme ${Id}: $($_.Exception.Message)" } }
function Check-Row($w, [string]$Name) {
    Set-ElText (Find-El $w 'txtAppPackagerSearch') $Name
    Start-Sleep 2
    $grid = Find-El $w 'PackagerGrid'
    $box = Get-ApdRowBoxes $grid | Select-Object -First 1
    if ($box -and $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { Real-Click $box }
    Set-ElText (Find-El $w 'txtAppPackagerSearch') ''
    Start-Sleep 1
}

foreach ($theme in $Themes) {
    $data = Join-Path $Run "data-$theme"
    $staging = Join-Path $data (Join-Path 'Users' (Join-Path $sid 'Staging'))
    New-Item -ItemType Directory -Force $staging | Out-Null
    & icacls $staging /inheritance:r /grant:r "${env:USERNAME}:(OI)(CI)F" '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
    Install-GuideDefinition $data @('zzapd-gsudo')
    $before = @(Get-GuideApplications)
    $same = @($before | Where-Object { $_.LocalizedDisplayName -like "$Application*" })
    if ($same) { $same | ForEach-Object { "on the site before the run: $(Format-GuideApp $_)" }; throw "The site already holds an application whose title starts with '$Application'. The run does not start." }
    $beforeModels = @($before | ForEach-Object { [string]$_.ModelName })
    $beforeFolders = @(Get-GuideShareFolders $FileShareRoot)
    $settings = @{ FileShareRoot = $FileShareRoot; DownloadRoot = $staging; Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false }
        Sidebar = @{ Order = @(); Hidden = @('site-hygiene') }
        OneClick = @{ Action = 'Report'; TrackedApplications = @('fa505d1415214f14', 'c078c933ebd84276', 'a2f7b815dc6e4903') } }
    $null = Start-Apd $exe $data $theme $settings
    try {
        $w = Main-Window
        $w.SetFocus()
        Step $theme 'start-sidebar-context-menu' {
            $item = Find-El $w -Name 'Detection Tester' -Timeout 5
            Click-El $item -Right
            Start-Sleep 1
            $show = Find-El $null -Name 'Show tool' -Timeout 3
            if ($show) { [System.Windows.Forms.Cursor]::Position = Get-ElCenter $show; Start-Sleep -Seconds 2 }
            Shot $theme 'start-sidebar-context-menu'
            Send-ApdKeys '{ESC}{ESC}'
            Start-Sleep 1
        }
        Step $theme 'checks' {
            Check-Row $w $Application
            Open-ApdMenuItem $w 'btnCheckMenu' 'miCheckLatest'
            Wait-Idle $w 240
            "status: $(Status-Text $w)"
        }
        Step $theme 'core-one-click-plan' {
            # One Click checks every tracked application before the window opens, so the wait is for the summary line of the window.
            Real-Click (Find-El $w 'btnOneClick')
            if (-not (Wait-El 'txtPlanSummary' 600)) { throw 'The One Click window did not open.' }
            Start-Sleep 3
            Move-MouseAway
            Shot $theme 'core-one-click-plan'
            if (-not (Close-ApdWindow 'One Click')) { Send-ApdCloseKey 'One Click' }
            Start-Sleep 3
        }
        Step $theme 'core-stage-result-log' {
            Real-Click (Find-El $w 'btnStage' -Timeout 5)
            Wait-Idle $w 420
            # A dialog after a Stage is not expected: it is not clicked, and the step fails.
            Resolve-ApdContentDialog $w @()
            "status after Stage: $(Status-Text $w)"
            Move-MouseAway
            Shot $theme 'core-stage-result-log'
        }
        Step $theme 'core-publish-configmgr-log' {
            Set-ElText (Find-El $w 'txtAppPackagerComment') 'Guide capture'
            Real-Click (Find-El $w 'btnPublishAll')
            Complete-ApdPublish $w 600
            "status after Publish: $(Status-Text $w)"
            Move-MouseAway
            Shot $theme 'core-publish-configmgr-log'
        }
        Step $theme 'core-packages-conflict-dialog' {
            # The application of the first publish is on the site now: a second publish of the same version asks what to do. Skip answers it: nothing changes.
            Check-Row $w $Application
            Real-Click (Find-El $w 'btnPublishAll')
            # The review opens first (and again when ConfigMgr differs from the plan); the dialog comes during the run.
            Complete-ApdPublish $w 420 -OnConflict Return
            if (-not (Find-El $w -Name 'Skip' -ControlType 'Button' -Timeout 5)) { throw 'The Application Already Exists dialog did not open.' }
            Start-Sleep 2
            Move-MouseAway
            Shot $theme 'core-packages-conflict-dialog'
            # The Application Already Exists dialog is a dialog inside the main window: Skip keeps the application as it is.
            $skip = Find-El $w -Name 'Skip' -ControlType 'Button' -Timeout 5
            if ($skip) { Real-Click $skip; Complete-ApdPublish $w 300 } else { 'No Skip button found: the dialog stays open and the run ends the app.' }
            if (Test-ApdBusy $w) { Real-Click (Find-El $w 'btnCancelOperation'); Start-Sleep 3 }
        }
    }
    finally {
        Stop-Apd
        try { Complete-GuideSiteCleanup $data $beforeModels $FileShareRoot $beforeFolders }
        catch { "cleanup of the site failed, nothing more was removed: $($_.Exception.Message)" }
    }
}
