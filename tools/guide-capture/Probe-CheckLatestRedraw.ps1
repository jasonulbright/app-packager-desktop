<#
.SYNOPSIS
    The lab check that Check Latest in APD updates the cells of the checked row and does not rebuild the rows or the icon cells of the Packages grid.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1), from a folder that holds the scripts of tools\guide-capture:
        Run-VisibleProbe.ps1 -Worktree <folder> -ProbeArgs "& .\Probe-CheckLatestRedraw.ps1 -Run C:\guide\runs\apd-redraw"
    The probe checks the first rows of the grid, clicks Check Latest with a real click and samples the UI Automation runtime ids of the visible rows and of their
    icon images while the run goes. A rebuilt row or icon cell is a new element with a new runtime id. It prints the counts and ends with "REDRAW-FAILS=<n>".
#>
param([Parameter(Mandatory)][string]$Run, [int]$Rows = 24, [int]$MaxSeconds = 300, [string]$AppFolder = (Join-Path $env:LOCALAPPDATA 'Programs\App Packager Desktop'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$fails = 0
function Items($Scope, [string]$Type) { @($Scope.FindAll($script:TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::($Type))))) }
function Rid($El) { try { ($El.GetRuntimeId() -join '.') } catch { '' } }
$data = Join-Path $Run 'data'
Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
$staging = New-RunStaging $data
$proc = Start-Apd $exe $data 'Dark' @{ DownloadRoot = $staging; FileShareRoot = '\cm01.contoso.com\ContentShare$'; SiteCode = 'MCM'; ProviderMachine = 'cm01.contoso.com'; SiteCodeSaved = $true; CheckForUpdatesAtStartup = $false }
try {
    [void](Click-Nav 'App Packager'); Start-Sleep 5
    $g = Find-El $null 'PackagerGrid' -Timeout 20
    $checked = 0
    foreach ($row in ((Items $g 'DataItem') | Select-Object -First $Rows)) {
        $box = $row.FindFirst($script:TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::CheckBox)))
        if (-not $box) { continue }
        $t = $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($t.Current.ToggleState -ne 'On') { $t.Toggle() }
        $checked++
    }
    "rows checked for the run: $checked"
    function Get-Snapshot {
        $g = Find-El $null 'PackagerGrid' -Timeout 5
        $map = @{}
        foreach ($row in (Items $g 'DataItem')) {
            $r = $row.Current.BoundingRectangle
            if ($r.IsEmpty -or $r.Width -le 0) { continue }
            $map[[string][int]$r.Y] = [pscustomobject]@{ Row = (Rid $row); Icon = '' }
        }
        foreach ($img in (Items $g 'Image')) {
            $r = $img.Current.BoundingRectangle
            if ($r.IsEmpty -or $r.Width -le 0) { continue }
            foreach ($d in 4, 3, 5, 2, 6, 1, 7) { $k = [string]([int]$r.Y - $d); if ($map.ContainsKey($k)) { $map[$k].Icon = (Rid $img); break } }
        }
        $map
    }
    $btn = Find-El $null -Name 'Check Latest' -ControlType 'Button' -Timeout 10
    $prev = Get-Snapshot
    $rowRebuilds = 0; $iconRebuilds = 0; $bad = 0; $samples = 0; $sawBusy = $false
    Click-El $btn
    $start = Get-Date
    while (((Get-Date) - $start).TotalSeconds -lt $MaxSeconds) {
        Start-Sleep -Milliseconds 150
        $now = Get-Snapshot; $samples++
        $rb = 0; $ib = 0
        foreach ($k in $now.Keys) {
            if (-not $prev.ContainsKey($k)) { continue }
            if ($now[$k].Row -and $prev[$k].Row -and $now[$k].Row -ne $prev[$k].Row) { $rb++ }
            if ($now[$k].Icon -and $prev[$k].Icon -and $now[$k].Icon -ne $prev[$k].Icon) { $ib++ }
        }
        $rowRebuilds += $rb; $iconRebuilds += $ib; if ($rb -or $ib) { $bad++ }
        $prev = $now
        $b = Find-El $null -Name 'Check Latest' -ControlType 'Button' -Timeout 1
        if ($b -and -not $b.Current.IsEnabled) { $sawBusy = $true }
        if ($sawBusy -and $b -and $b.Current.IsEnabled) { break }
        if (-not $sawBusy -and ((Get-Date) - $start).TotalSeconds -gt 40) { break }
    }
    $statuses = @(Items (Find-El $null 'PackagerGrid' -Timeout 5) 'Text' | ForEach-Object { $_.Current.Name } | Where-Object { $_ -match '^(Up to date|Update available|Latest retrieved|Not found|Current newer|Error)' })
    "samples: $samples, status texts of checked rows: $($statuses.Count)"
    "rows rebuilt: $rowRebuilds, icons rebuilt: $iconRebuilds, samples with a rebuild: $bad"
    if (-not $sawBusy) { 'FAIL: the run never started'; $fails++ }
    if ($statuses.Count -lt 3) { 'FAIL: fewer than 3 rows show a result'; $fails++ }
} finally { Stop-Apd }
"REDRAW-ROWS=$rowRebuilds REDRAW-ICONS=$iconRebuilds"
"REDRAW-FAILS=$fails"
