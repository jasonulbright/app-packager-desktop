<#
.SYNOPSIS
    The lab window run of the requirement columns of the Application Settings grid (Q-106): the real app is driven with real mouse and key input. The run chooses the columns
    Free disk space (MB), Memory (MB) and OS build (minimum) in the column chooser, types a value into the cell of 7-Zip, tries invalid numbers, saves the settings, reads the
    Workbench profile file back, clears one cell, saves again and reads the file again. The result of the run is a list of lines that start with OK or FAIL.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock, from the folder of the published app with ApdUiDriver.ps1:
        Run-VisibleProbe.ps1 -Worktree <published folder with both scripts> -ProbeArgs "& .\Capture-SettingsRequirements.ps1 -OutDir C:\guide\out-req"
    The settings of the run are written into an isolated data folder, so the settings of the lab user stay as they are. Nothing is published and the site is not read.
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'out'),
    [string[]]$Themes = @('Light', 'Dark')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')

$failures = 0
function Check([bool]$Condition, [string]$Text) {
    if ($Condition) { "OK   $Text" } else { $script:failures++; "FAIL $Text" }
}

function Find-GridRow($Grid, [string]$Name) {
    $scroll = $Grid.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $items = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::DataItem)
    foreach ($percent in @(0..50 | ForEach-Object { $_ * 2 })) {
        $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $percent)
        Start-Sleep -Milliseconds 400
        foreach ($item in $Grid.FindAll([System.Windows.Automation.TreeScope]::Children, $items)) {
            if ($item.Current.Name -like "$Name*") { return $item }
        }
    }
    $null
}

# Shows a column with the column chooser (real mouse) and closes the chooser with Escape.
function Show-Column($Window, [string]$Header) {
    Click-El (Find-El $Window 'btnColumns' -Timeout 5); Start-Sleep -Milliseconds 800
    $item = Find-El $null $null $Header 'MenuItem' -Timeout 5
    Check ($null -ne $item) "the column chooser lists $Header"
    if ($item) { Click-El $item; Start-Sleep -Milliseconds 700 }
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Milliseconds 700
}

# Types a text into the cell of a row with real keys: click the cell, select its text, type, and leave the cell with Tab so that the value commits.
function Type-Cell($Grid, [string]$Row, [string]$CellName, [string]$Text) {
    $item = Find-GridRow $Grid $Row
    if (-not $item) { throw "The grid has no row for '$Row'." }
    try { $item.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView(); Start-Sleep -Milliseconds 700 } catch { $null = $_ }
    # The requirement columns are the last ones: scroll the grid to its right edge so that the cells are on screen, then find the row again.
    $Grid.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(100, [System.Windows.Automation.ScrollPattern]::NoScroll); Start-Sleep -Milliseconds 800
    $item = Find-GridRow $Grid $Row
    $Grid.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(100, [System.Windows.Automation.ScrollPattern]::NoScroll); Start-Sleep -Milliseconds 800
    $cell = $item.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $CellName))
    if (-not $cell) { throw "The row of '$Row' has no cell '$CellName'." }
    try { $cell.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView(); Start-Sleep -Milliseconds 500 } catch { $null = $_ }
    Click-El $cell; Start-Sleep -Milliseconds 400
    [System.Windows.Forms.SendKeys]::SendWait('^a'); Start-Sleep -Milliseconds 200
    if ($Text.Length -eq 0) { [System.Windows.Forms.SendKeys]::SendWait('{BACKSPACE}') } else { [System.Windows.Forms.SendKeys]::SendWait($Text) }
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('{TAB}'); Start-Sleep -Milliseconds 800
    $Grid.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(100, [System.Windows.Automation.ScrollPattern]::NoScroll); Start-Sleep -Milliseconds 600
    $again = (Find-GridRow $Grid $Row).FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $CellName))
    try { $again.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { '' }
}

function Get-RequirementOperations([string]$Data) {
    $files = @(Get-ChildItem $Data -Recurse -File -Filter *.json -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match 'profile' -and (Select-String -LiteralPath $_.FullName -Pattern 'MigratedFrom' -Quiet) })
    foreach ($f in $files) {
        $json = Get-Content $f.FullName -Raw | ConvertFrom-Json
        if ($json.Requirements.Operations) { return @($json.Requirements.Operations) }
    }
    @()
}

foreach ($theme in $Themes) {
    $data = Join-Path $OutDir "data-$theme"
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    $null = Start-Apd (Join-Path $PSScriptRoot 'ApplicationPackagerUI.exe') $data $theme @{}
    try {
        $w = @(Get-ApdWindows)[0]
        if (-not (Click-Nav 'Settings')) { throw 'The sidebar has no Settings entry.' }
        Start-Sleep 3
        Click-El (Find-El $w 'nav_supersedence' -Timeout 10); Start-Sleep 2
        $grid = Find-El $w 'dgSupersedenceApps' -Timeout 10
        if (-not $grid) { Save-Shot (Join-Path $OutDir 'debug.png'); Get-ElList $w 60; throw 'Settings, Application Settings has no grid of applications.' }

        foreach ($header in 'Free disk space (MB)', 'Memory (MB)', 'OS build (minimum)') { Show-Column $w $header }
        # The grid scrolls to the right edge so that the three columns are in view.
        $gs = $grid.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)

        $disk = 'Free disk space (MB) of the application'
        $memory = 'Memory (MB) of the application'
        $build = 'OS build (minimum) of the application'

        # Invalid numbers are refused: the cell goes back to its stored value (empty).
        $text = Type-Cell $grid '7-Zip' $memory 'plenty'
        Check ($text -eq '') "$theme the cell Memory (MB) refuses the text 'plenty' (shows '$text')"
        $text = Type-Cell $grid '7-Zip' $build '1909'
        Check ($text -eq '') "$theme the cell OS build (minimum) refuses 1909, which is a version name and not a build (shows '$text')"
        $text = Type-Cell $grid '7-Zip' $disk '0'
        Check ($text -eq '') "$theme the cell Free disk space (MB) refuses 0 (shows '$text')"

        # Valid numbers.
        $text = Type-Cell $grid '7-Zip' $disk '2048'
        Check ($text -eq '2048') "$theme the cell Free disk space (MB) takes 2048 (shows '$text')"
        $text = Type-Cell $grid '7-Zip' $memory '4096'
        Check ($text -eq '4096') "$theme the cell Memory (MB) takes 4096 (shows '$text')"
        $text = Type-Cell $grid '7-Zip' $build '19045'
        Check ($text -eq '19045') "$theme the cell OS build (minimum) takes 19045 (shows '$text')"

        Move-MouseAway
        Save-Shot (Join-Path $OutDir (Join-Path $theme.ToLower() 'settings-requirements.png')) -MaskFooter
        Click-El (Find-El $w $null 'Save Settings' 'Button' -Timeout 5); Start-Sleep -Seconds 3

        $ops = @(Get-RequirementOperations $data)
        Check ($ops.Count -eq 3) "$theme the Workbench profile holds 3 requirement operations (found $($ops.Count))"
        foreach ($pair in @(@('min-disk-space', '2048'), @('min-memory', '4096'), @('min-os-build', '19045'))) {
            $op = $ops | Where-Object { $_.RuleId -eq $pair[0] }
            Check ($op -and $op.Op -eq 'Add' -and $op.Rule.ConditionId -eq $pair[0] -and $op.Rule.Value -eq $pair[1] -and @($op.AppliesTo) -contains '*') "$theme profile: operation $($pair[0]) is Add with Value $($pair[1]) for all deployment types"
        }

        # An empty cell removes the operation.
        $text = Type-Cell $grid '7-Zip' $memory ''
        Check ($text -eq '') "$theme the cell Memory (MB) is empty after it is cleared"
        Click-El (Find-El $w $null 'Save Settings' 'Button' -Timeout 5); Start-Sleep -Seconds 3
        $ops = @(Get-RequirementOperations $data)
        Check ($ops.Count -eq 2 -and -not ($ops | Where-Object { $_.RuleId -eq 'min-memory' })) "$theme profile: clearing Memory (MB) removed only min-memory (left: $((@($ops | ForEach-Object RuleId)) -join ', '))"
        'DONE {0}' -f $theme
    }
    finally { Stop-Apd }
}
if ($failures) { throw "$failures check(s) failed." }
'ALL OK'
