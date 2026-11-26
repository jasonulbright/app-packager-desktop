<#
.SYNOPSIS
    The lab capture run of the row context menu of the App Packager page (decision D-21, queue row Q-113): one application row is searched, selected and opened with the right mouse
    button, and the menu is saved. The real app is driven with UI Automation, one theme after the other.
.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <published folder with these scripts> -ProbeArgs "& .\Capture-PackagesContextMenu.ps1 -Run C:\guide\runs\apd-<timestamp>"
    The images are <Run>\images\<theme>\<id>.png. Nothing is written to the site: the menu is closed with Escape.
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
function Step([string]$Theme, [string]$Id, [scriptblock]$Body) { try { & $Body } catch { "FAIL $Theme ${Id}: $($_.Exception.Message)" } }

foreach ($theme in $Themes) {
    $data = Join-Path $Run "data-$theme"
    New-Item -ItemType Directory -Force $data | Out-Null
    $settings = @{ FileShareRoot = $FileShareRoot; Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $true }; CheckForUpdatesAtStartup = $false }
    $null = Start-Apd $exe $data $theme $settings
    try {
        $w = Main-Window
        $w.SetFocus()
        Step $theme 'core-packages-context-menu' {
            Set-ElText (Find-El $w 'txtAppPackagerSearch') $Application
            Start-Sleep 2
            $grid = Find-El $w 'PackagerGrid'
            $box = Get-ApdRowBoxes $grid | Select-Object -First 1
            if ($box -and $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { Click-El $box; Start-Sleep 1 }
            $cell = Find-El $w -Name $Application -ControlType 'Custom' -Timeout 10
            if (-not $cell) { $cell = Find-El $w -Name "$Application (x64)" -Timeout 5 }
            if (-not $cell) { throw "The grid has no row named '$Application'." }
            Click-El $cell -Right; Start-Sleep 2
            $item = Find-El $null -Name 'Open Network Share' -Timeout 5
            if (-not $item) { throw 'The context menu did not open.' }
            [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point ([int]$item.Current.BoundingRectangle.X + 20), ([int]$item.Current.BoundingRectangle.Y + 5)
            Start-Sleep 1
            Shot $theme 'core-packages-context-menu'
            Send-ApdKeys '{ESC}{ESC}'
            Start-Sleep 1
        }
    }
    finally { Stop-Apd }
}
