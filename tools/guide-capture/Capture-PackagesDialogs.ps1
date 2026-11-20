<#
.SYNOPSIS
    The lab capture run of the dialogs and results of the App Packager page (decision D-21, queue rows Q-20a and Q-65c): the sidebar context menu, the One Click plan
    window, the Configure dialog, the result of Stage, and the Application Already Exists dialog. The real app is driven with UI Automation, one theme after the other.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <published folder with these scripts> -ProbeArgs "& .\Capture-PackagesDialogs.ps1 -Run C:\guide\runs\apd-<timestamp>"
    The images are <Run>\images\<theme>\<id>.png. The Stage goes to the staging folder of the run. The Publish step answers the Application Already Exists dialog with
    Skip, so the run creates nothing on the site when the application is there; when the site has no such application, the publish creates it and the run says so.
    A step that fails says FAIL for its image and the run goes on.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot,
    [string]$Application = '7-Zip',
    [string]$FileShareRoot = '\\cm01.contoso.com\ContentShare$'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value

function Shot([string]$Theme, [string]$Id) { Save-Shot (Join-Path $Run (Join-Path 'images' (Join-Path $Theme.ToLower() ($Id + '.png')))) -MaskFooter; "OK $Theme $Id" }
function Main-Window { Get-ApdWindows | Where-Object { $_.Current.Name -match 'App Packager' } | Select-Object -First 1 }
function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Status-Text($w) { $e = Find-El $w 'StatusLabel' -Timeout 2; if ($e) { $e.Current.Name } else { '' } }
function Wait-Idle($w, [int]$Seconds) {
    Start-Sleep 4
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline -and (Find-El $w 'btnCancelOperation' -Timeout 1)) { Start-Sleep 2 }
}
function Step([string]$Theme, [string]$Id, [scriptblock]$Body) { try { & $Body } catch { "FAIL $Theme ${Id}: $($_.Exception.Message)" } }
# The windows of the app other than the main window, with their names.
function Other-Windows { @(Get-ApdWindows | Where-Object { $_.Current.Name -notmatch '^App Packager' }) }
function Show-Other { "windows: " + ((Get-ApdWindows | ForEach-Object { $_.Current.Name }) -join ' | ') }
function Click-ByName($Scope, [string[]]$Names) {
    foreach ($n in $Names) { $b = Find-El $Scope -Name $n -ControlType 'Button' -Timeout 2; if ($b) { Real-Click $b; return $n } }
    throw ('No button named ' + ($Names -join ', ') + ': ' + ((Get-ElList $Scope 40) -join ' / '))
}
function Check-Row($w, [string]$Name) {
    Set-ElText (Find-El $w 'txtAppPackagerSearch') $Name
    Start-Sleep 2
    $grid = Find-El $w 'PackagerGrid'
    $box = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::CheckBox))) |
        Where-Object { $_.Current.AutomationId -ne 'SelectAllCheckBox' } | Select-Object -First 1
    if ($box -and $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { Real-Click $box }
    Set-ElText (Find-El $w 'txtAppPackagerSearch') ''
    Start-Sleep 1
}

foreach ($theme in $Themes) {
    $data = Join-Path $Run "data-$theme"
    $staging = Join-Path $data (Join-Path 'Users' (Join-Path $sid 'Staging'))
    New-Item -ItemType Directory -Force $staging | Out-Null
    & icacls $staging /inheritance:r /grant:r "${env:USERNAME}:(OI)(CI)F" '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
    $settings = @{ FileShareRoot = $FileShareRoot; DownloadRoot = $staging; Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $true }
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
            [System.Windows.Forms.SendKeys]::SendWait('{ESC}{ESC}')
            Start-Sleep 1
        }
        Step $theme 'checks' {
            Check-Row $w $Application
            Real-Click (Find-El $w -Name 'Check Latest' -ControlType 'Button' -Timeout 5)
            Wait-Idle $w 240
            "status: $(Status-Text $w)"
        }
        Step $theme 'core-one-click-plan' {
            # The One Click window is not in the UI Automation tree of the app, so the shot is taken after a wait, and Alt+F4 closes the window.
            Real-Click (Find-El $w 'btnOneClick')
            Start-Sleep 10
            Move-MouseAway
            Shot $theme 'core-one-click-plan'
            [System.Windows.Forms.SendKeys]::SendWait('%{F4}')
            Start-Sleep 3
        }
        Step $theme 'core-stage-result-log' {
            Real-Click (Find-El $w -Name 'Stage' -ControlType 'Button' -Timeout 5)
            Wait-Idle $w 420
            # A second dialog (the allowance of the staging folder, a signature note) is answered with the first button that continues.
            foreach ($i in 1..3) { $dlg = Other-Windows | Select-Object -First 1; if ($dlg) { Show-Other; try { $null = Click-ByName $dlg @('Continue', 'OK', 'Yes'); Wait-Idle $w 300 } catch { break } } }
            "status after Stage: $(Status-Text $w)"
            Move-MouseAway
            Shot $theme 'core-stage-result-log'
        }
        Step $theme 'core-publish-configmgr-log' {
            Set-ElText (Find-El $w 'txtAppPackagerComment') 'Guide capture'
            Real-Click (Find-El $w 'btnPublishConfigMgr')
            Wait-Idle $w 420
            $dlg = Other-Windows | Select-Object -First 1
            if ($dlg) { Show-Other; $null = Click-ByName $dlg @('Continue', 'OK', 'Yes'); Wait-Idle $w 420 }
            "status after Publish: $(Status-Text $w)"
            Move-MouseAway
            Shot $theme 'core-publish-configmgr-log'
        }
        Step $theme 'core-packages-conflict-dialog' {
            # The application of the first publish is on the site now: a second publish of the same version asks what to do. The window closes without a choice: nothing changes.
            Check-Row $w $Application
            Real-Click (Find-El $w 'btnPublishConfigMgr')
            Start-Sleep 20
            Move-MouseAway
            Shot $theme 'core-packages-conflict-dialog'
            [System.Windows.Forms.SendKeys]::SendWait('%{F4}')
            Start-Sleep 5
            if (Find-El $w 'btnCancelOperation' -Timeout 2) { Real-Click (Find-El $w 'btnCancelOperation'); Start-Sleep 3 }
        }
    }
    finally { Stop-Apd }
}
