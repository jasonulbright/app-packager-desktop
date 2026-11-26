<#
.SYNOPSIS
    The lab capture run of the Settings page of supersedence (decision D-21): opens Settings, Supersedence, checks two applications in the grid, gives them Replace with
    the button of the checked applications, and saves the work area as tools-settings-supersedence.png. Nothing is saved in the settings and the site is not read.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock, from the folder of the published app with ApdUiDriver.ps1:
        Run-VisibleProbe.ps1 -Worktree <published folder with both scripts> -ProbeArgs "& .\Capture-SettingsSupersedence.ps1 -OutDir C:\guidepdsupset"
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'out'),
    [string[]]$Themes = @('Light', 'Dark'),
    [string[]]$Applications = @('Everything', 'WizTree'),
    [string]$AppFolder = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
foreach ($theme in $Themes) {
    $data = Join-Path $OutDir "data-$theme"
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    $null = Start-Apd (Join-Path $AppFolder 'ApplicationPackagerUI.exe') $data $theme @{}
    try {
        $w = @(Get-ApdWindows)[0]
        if (-not (Click-Nav 'Settings')) { throw 'The sidebar has no Settings entry.' }
        Start-Sleep 3
        Click-El (Find-El $w 'nav_supersedence' -Timeout 10); Start-Sleep 2
        $grid = Find-El $w 'dgSupersedenceApps' -Timeout 10
        if (-not $grid) { throw 'Settings, Supersedence has no grid of applications.' }
        function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 700 }
    foreach ($name in $Applications) {
        # The grid shows only the rows that are on screen: scroll it from the top in steps of 2 percent (fewer rows than the grid shows) until the row is there.
        $scroll = $grid.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        $row = $null
        foreach ($percent in @(0..50 | ForEach-Object { $_ * 2 })) {
            $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $percent)
            Start-Sleep -Milliseconds 400
            foreach ($item in $grid.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::DataItem)))) {
                if ($item.Current.Name -like "$name*") { $row = $item; break }
            }
            if ($row) { break }
        }
        if (-not $row) { throw "The supersedence grid has no row for '$name'." }
        # The check box is in the first cell of the row.
        $cell = $row.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), ('Item: ' + $row.Current.Name + ', Column Display Index: 0')))
        if (-not $cell) { throw "The row of '$name' has no check box cell." }
        Real-Click $cell
    }
        Real-Click (Find-El $w 'cboBulkValue' -Timeout 5)
        Real-Click (Find-El $null $null 'Replace' 'ListItem' -Timeout 5)
        Real-Click (Find-El $w 'btnBulkApply' -Timeout 5)
        Move-MouseAway
        Start-Sleep 1
        Save-Shot (Join-Path $OutDir (Join-Path $theme.ToLower() 'tools-settings-supersedence.png')) -MaskFooter
        'OK {0} tools-settings-supersedence' -f $theme
    }
    finally { Stop-Apd }
}
