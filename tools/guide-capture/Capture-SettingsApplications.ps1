<#
.SYNOPSIS
    The lab capture run of the Application Settings grid of Settings (Q-106, decision D-21): the real app is driven with real mouse input. The run checks two applications,
    sets a column for the checked applications with the bulk buttons, sets one cell of a row with the mouse, saves the settings, reads the settings file back, and saves the
    work area as core-settings-application-settings.png. The result of the run is a list of lines that start with OK or FAIL.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock, from the folder of the published app with ApdUiDriver.ps1:
        Run-VisibleProbe.ps1 -Worktree <published folder with both scripts> -ProbeArgs "& .\Capture-SettingsApplications.ps1 -OutDir C:\guide\out-apps"
    The settings of the run are written into an isolated data folder, so the settings of the lab user stay as they are. Nothing is published and the site is not read.
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'out'),
    [string[]]$Themes = @('Light', 'Dark')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')

# The identifiers of the definitions of the two applications (7-Zip offers the architecture choice, Bitwarden offers the install scope).
$sevenZipId = 'fa505d1415214f14'
$bitwardenId = '000000280bb18b20'
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

# Opens a drop-down list with the mouse and clicks the entry of the list with the text.
function Choose-Item($Combo, [string]$Text) {
    # Bring the cell into view first: the grid scrolls, and a click on a cell below the visible part misses.
    try { $Combo.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView(); Start-Sleep -Milliseconds 500 } catch { $null = $_ }
    $r = $Combo.Current.BoundingRectangle
    Click-At (New-Object System.Drawing.Point ([int]($r.X + $r.Width - 14)), ([int]($r.Y + $r.Height / 2)))
    Start-Sleep -Milliseconds 900
    $entry = Find-El $null $null $Text 'ListItem' -Timeout 3
    if (-not $entry) {
        Save-Shot (Join-Path $OutDir 'debug.png')
        $names = foreach ($w2 in (Get-ApdWindows)) { $w2.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::ListItem))) | ForEach-Object { $_.Current.Name } }
        throw "The list has no entry '$Text'. The list items on screen: $($names -join ' | ')"
    }
    Click-El $entry
    Start-Sleep -Milliseconds 700
}

function Check-Row($Grid, [string]$Name) {
    $row = Find-GridRow $Grid $Name
    if (-not $row) { throw "The grid has no row for '$Name'." }
    try { $row.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView(); Start-Sleep -Milliseconds 700 } catch { $null = $_ }
    $cell = $row.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), ('Item: ' + $row.Current.Name + ', Column Display Index: 0')))
    if (-not $cell) { throw "The row of '$Name' has no check box cell." }
    Click-El $cell
    Start-Sleep -Milliseconds 700
    $r = $row.Current.BoundingRectangle
    "     clicked the check box of '$Name' at {0},{1}; the page says: {2}" -f [int]$r.X, [int]$r.Y, (Find-El $null 'txtCheckedCount' -Timeout 2).Current.Name
    $row
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
        Check ($null -ne (Find-El $w 'cboBulkColumn' -Timeout 5)) "$theme the page has the column list of the bulk action"

        # Check one application with the mouse, then set two columns for the checked application with the bulk buttons.
        $null = Check-Row $grid 'Bitwarden'
        Choose-Item (Find-El $w 'cboBulkColumn' -Timeout 5) 'Install scope'
        Choose-Item (Find-El $w 'cboBulkValue' -Timeout 5) 'User'
        Click-El (Find-El $w 'btnBulkApply' -Timeout 5); Start-Sleep -Milliseconds 800
        Choose-Item (Find-El $w 'cboBulkColumn' -Timeout 5) 'Supersedence'
        Choose-Item (Find-El $w 'cboBulkValue' -Timeout 5) 'Replace'
        Click-El (Find-El $w 'btnBulkApply' -Timeout 5); Start-Sleep -Milliseconds 800

        # One cell of a row with the mouse: the package source of 7-Zip.
        $row = Find-GridRow $grid '7-Zip'
        $cell = $row.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), 'Package source of the application'))
        if (-not $cell) { throw 'The row of 7-Zip has no package source cell.' }
        Choose-Item $cell 'ARM64 only'

        # The version in the title of Bitwarden is locked to Include version while supersedence is on.
        $row = Find-GridRow $grid 'Bitwarden'
        $title = $row.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), 'Version in the title of the application'))
        Check ((-not $title.Current.IsEnabled)) "$theme the version in the title of Bitwarden is locked while supersedence is on"

        # The column chooser lists every setting of the registry: show the One Click column with the mouse.
        Click-El (Find-El $w 'btnColumns' -Timeout 5); Start-Sleep -Milliseconds 800
        $item = Find-El $null $null 'In One Click' 'MenuItem' -Timeout 5
        Check ($null -ne $item) "$theme the column chooser lists In One Click"
        if ($item) { Click-El $item; Start-Sleep -Milliseconds 700 }
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Milliseconds 700
        Check ($null -ne (Find-El $w $null 'One Click inclusion of the application' 'ComboBox' -Timeout 5)) "$theme the grid shows the One Click column after it is chosen"

        # Filtered scope: type in the search box (real keys), set the Network column for the filtered applications only.
        $box = Find-El $w 'txtApplicationFilter' -Timeout 5
        Click-El $box; Start-Sleep -Milliseconds 500
        [System.Windows.Forms.SendKeys]::SendWait('Bitwarden'); Start-Sleep -Seconds 1
        Choose-Item (Find-El $w 'cboBulkColumn' -Timeout 5) 'Network'
        Choose-Item (Find-El $w 'cboBulkValue' -Timeout 5) 'VPN only'
        Choose-Item (Find-El $w 'cboBulkScope' -Timeout 5) 'Filtered applications'
        Click-El (Find-El $w 'btnBulkApply' -Timeout 5); Start-Sleep -Milliseconds 800
        # The filter stays for the image: the changed row and nothing else; clear it afterwards so the rows around it show.
        Click-El $box; [System.Windows.Forms.SendKeys]::SendWait('^a{BACKSPACE}'); Start-Sleep -Seconds 1

        # Scroll to the rows around Bitwarden for the image: the changed row and its neighbours.
        $near = Find-GridRow $grid 'BleachBit'
        if ($near) { try { $near.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView() } catch { $null = $_ }; Start-Sleep -Milliseconds 700 }
        Move-MouseAway
        Save-Shot (Join-Path $OutDir (Join-Path $theme.ToLower() 'core-settings-application-settings.png')) -MaskFooter

        $save = Find-El $w $null 'Save Settings' 'Button' -Timeout 5
        Click-El $save; Start-Sleep -Seconds 3
        $file = (Get-ChildItem $data -Recurse -Filter settings.json | Select-Object -First 1).FullName
        $json = Get-Content $file -Raw | ConvertFrom-Json
        $conditions = $json.DeploymentConditions
        Check ($conditions.$bitwardenId.InstallMode -eq 'CurrentUser') "$theme settings.json: Bitwarden install scope is CurrentUser"
        Check ($conditions.$sevenZipId.Architecture -eq 'ARM64') "$theme settings.json: 7-Zip package source is ARM64"
        Check ($null -eq $conditions.$sevenZipId.InstallMode -or $conditions.$sevenZipId.InstallMode -eq '') "$theme settings.json: 7-Zip has no install scope (the installer offers none)"
        $overrides = $json.Supersedence.TitleOverrides.PSObject.Properties
        Check (@($overrides | Where-Object { $_.Value.Mode -eq 'Replace' }).Count -eq 1) "$theme settings.json: one application has the supersedence mode Replace"
        Check ($conditions.$bitwardenId.Network -eq 'VpnOnly') "$theme settings.json: the filtered bulk set the Network requirement of Bitwarden"
        Check ($null -eq $conditions.$sevenZipId.Network -or $conditions.$sevenZipId.Network -eq 'Any') "$theme settings.json: the filtered bulk left 7-Zip alone"
        'DONE {0}' -f $theme
    }
    finally { Stop-Apd }
}
if ($failures) { throw "$failures check(s) failed." }
'ALL OK'
