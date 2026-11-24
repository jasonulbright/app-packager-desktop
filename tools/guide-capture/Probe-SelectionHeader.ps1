<#
.SYNOPSIS
    The lab check of Q-130: the header check box of the Packages grid cycles like the PowerShell app, shows the indeterminate state, writes the log lines, and acts on the visible rows only.
    The app starts in an isolated data folder and is driven with real mouse input. Each check says PASS or FAIL. The run ends with "SELECTION-FAILS=<n>".

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from a folder that holds the scripts of tools\guide-capture:
        Run-VisibleProbe.ps1 -Worktree <folder with these scripts> -ProbeArgs "& .\Probe-SelectionHeader.ps1 -Run C:\guide\runs\apd-q130 -AppFolder C:\guide\q130-app"
    The run reads and changes nothing on the site. Per theme:
      1. the header check box starts unchecked and no row is checked;
      2. click 1: header checked, every row checked, log line "Selected all visible rows.";
      3. click 2: header indeterminate, log line "Selected rows with 'Update available' status." (no row has an update, so no row stays checked);
      4. click 3: header unchecked, no row checked, log line "Deselected all rows.";
      5. with the search box filtered to one application, a header click checks only the shown row, and clearing the filter keeps that selection (the other rows stay unchecked);
      6. Select Update Available puts the header in the indeterminate state and writes its log line.
    A screenshot of each state is saved under <Run>\checks\<theme>.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = (Join-Path $env:LOCALAPPDATA 'Programs\App Packager Desktop'),
    [string]$Filter = '7-Zip'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$script:Fails = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') { if ($Ok) { "PASS $Name" } else { $script:Fails++; "FAIL ${Name}: $Detail" } }
function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Check-Shot([string]$Theme, [string]$Id) { Move-MouseAway; Save-Shot (Join-Path $Run (Join-Path 'checks' (Join-Path $Theme.ToLower() ($Id + '.png')))) }
function Session-Log([string]$Data) {
    $f = Get-ChildItem $Data -Recurse -Filter packr.log -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($f) { Get-Content $f.FullName -Raw -ErrorAction SilentlyContinue } else { '' }
}
function Run-Check([string]$Name, [scriptblock]$Body) { try { & $Body } catch { $script:Fails++; "FAIL ${Name}: $($_.Exception.Message)" } }
function Header-State($w) {
    $box = Find-El $w 'chkSelectAllPackages' -Timeout 10
    if (-not $box) { throw 'The header check box was not found.' }
    $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState.ToString()
}
# The row check boxes: every check box in the grid except the one in the header.
function Row-States($w) {
    $grid = Find-El $w -ControlType 'DataGrid' -Timeout 10
    $boxes = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), [System.Windows.Automation.ControlType]::CheckBox))
    @(foreach ($b in $boxes) { if ($b.Current.AutomationId -ne 'chkSelectAllPackages') { $b.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState.ToString() } })
}
function Click-Header($w) { Real-Click (Find-El $w 'chkSelectAllPackages' -Timeout 10) }

foreach ($theme in $Themes) {
    $data = Join-Path $Run "data-$theme"
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    $staging = New-RunStaging $data
    $settings = @{ DownloadRoot = $staging; SiteCode = 'MCM'; SiteCodeSaved = $true; CheckForUpdatesAtStartup = $false }
    $proc = Start-Apd $exe $data $theme $settings
    try {
        $w = Main-Window
        $w.SetFocus()
        Start-Sleep 5
        Run-Check "$theme-01" {
            $rows = Row-States $w
            Check "$theme-01a the grid shows rows" ($rows.Count -gt 3) "rows: $($rows.Count)"
            Check "$theme-01b the header starts unchecked" ((Header-State $w) -eq 'Off') (Header-State $w)
            Check "$theme-01c no row is checked" (@($rows | Where-Object { $_ -ne 'Off' }).Count -eq 0) 'a row is checked'
        }
        Run-Check "$theme-02" {
            Click-Header $w
            $rows = Row-States $w
            Check "$theme-02a header checked" ((Header-State $w) -eq 'On') (Header-State $w)
            Check "$theme-02b every row is checked" (@($rows | Where-Object { $_ -ne 'On' }).Count -eq 0) "states: $(@($rows | Select-Object -Unique) -join ',')"
            Check "$theme-02c the log has the line" ((Session-Log $data) -match 'Selected all visible rows\.') 'no log line'
            Check-Shot $theme 'header-all'
        }
        Run-Check "$theme-03" {
            Click-Header $w
            Check "$theme-03a header indeterminate" ((Header-State $w) -eq 'Indeterminate') (Header-State $w)
            Check "$theme-03b no row stays checked (no row has an update)" (@(Row-States $w | Where-Object { $_ -ne 'Off' }).Count -eq 0) 'a row is checked'
            Check "$theme-03c the log has the line" ((Session-Log $data) -match "Selected rows with 'Update available' status\.") 'no log line'
            Check-Shot $theme 'header-updates'
        }
        Run-Check "$theme-04" {
            Click-Header $w
            Check "$theme-04a header unchecked" ((Header-State $w) -eq 'Off') (Header-State $w)
            Check "$theme-04b the log has the line" ((Session-Log $data) -match 'Deselected all rows\.') 'no log line'
            Check-Shot $theme 'header-none'
        }
        Run-Check "$theme-05" {
            $search = Find-El $w 'txtAppPackagerSearch' -Timeout 10
            Set-ElText $search $Filter
            Start-Sleep 2
            $shown = @(Row-States $w).Count
            Click-Header $w
            $on = @(Row-States $w | Where-Object { $_ -eq 'On' }).Count
            Check "$theme-05a the filter shows fewer rows" ($shown -ge 1 -and $shown -lt 20) "shown: $shown"
            Check "$theme-05b the header click checks only the shown rows" ($on -eq $shown) "on: $on, shown: $shown"
            Set-ElText $search ''
            Start-Sleep 2
            $all = @(Row-States $w)
            $checked = @($all | Where-Object { $_ -eq 'On' }).Count
            Check "$theme-05c the selection survives clearing the filter and no other row is checked" ($checked -eq $shown -and $all.Count -gt $shown) "checked: $checked, shown: $shown, all: $($all.Count)"
            Check-Shot $theme 'filter-selection'
        }
        Run-Check "$theme-06" {
            $btn = Find-El $w 'btnSelectUpdateAvailable' -Timeout 5
            if (-not $btn) { $btn = Find-El $w -Name 'Select Update Available' -Timeout 5 }
            Real-Click $btn
            Check "$theme-06a the header is indeterminate" ((Header-State $w) -eq 'Indeterminate') (Header-State $w)
            $n = ([regex]::Matches((Session-Log $data), "Selected rows with 'Update available' status\.")).Count
            Check "$theme-06b the button wrote its log line" ($n -ge 2) "lines: $n"
        }
    }
    finally { Stop-Apd }
}
"SELECTION-FAILS=$($script:Fails)"
