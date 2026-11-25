<#
.SYNOPSIS
    The lab check of the V1 overhaul phases 3a and 3b (queue row Q-126): status tabs, selection line, scope line, removed per-system buttons and the Columns menu,
    in the real app on CLIENT01 against CM01, light and dark.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <published folder with this script> -ProbeArgs "& .\Probe-OverhaulV3.ps1 -Run C:\guide\runs\v3-<timestamp>"
    A step that fails says FAIL and the run goes on. Nothing is published.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'

function Shot([string]$Theme, [string]$Id) { Save-Shot (Join-Path $Run (Join-Path 'images' (Join-Path $Theme.ToLower() ($Id + '.png')))) -MaskFooter; "OK $Theme $Id" }
function Main-Window { Get-ApdWindows | Where-Object { $_.Current.Name -match 'App Packager' } | Select-Object -First 1 }
function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Step([string]$Theme, [string]$Id, [scriptblock]$Body) { try { & $Body } catch { "FAIL $Theme ${Id}: $($_.Exception.Message)" } }
function Names([string[]]$Ids) { foreach ($i in $Ids) { $e = Find-El $null $i -Timeout 4; "$i = " + $(if ($e) { $e.Current.Name } else { '(absent)' }) } }
function Header-Names($w) {
    $g = Find-El $w 'PackagerGrid'
    $c = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::HeaderItem)
    (@($g.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' | ')
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
    $settings = @{ FileShareRoot = '\\cm01.contoso.com\ContentShare$'; Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false } }
    $null = Start-Apd $exe $data $theme $settings
    try {
        $w = Main-Window
        $w.SetFocus()
        Step $theme 'tabs-and-scope' {
            Start-Sleep 6
            Names 'tabStatusAll', 'tabStatusUpdate', 'tabStatusNotFound', 'tabStatusNotChecked', 'txtSelectionCount', 'txtPublishScope'
            foreach ($gone in 'btnPublishConfigMgr', 'btnPublishIntune', 'btnPublishWsus') { "$gone present: $([bool](Find-El $null $gone -Timeout 1))" }
            Move-MouseAway
            Shot $theme 'packages-tabs'
        }
        Step $theme 'selection-line' {
            Check-Row $w '7-Zip'
            Set-ElText (Find-El $w 'txtAppPackagerSearch') 'Notepad'
            Start-Sleep 2
            Names 'txtSelectionCount'
            "Show selected visible: $([bool](Find-El $null 'btnShowSelected' -Timeout 3))"
            Move-MouseAway
            Shot $theme 'packages-selection-hidden'
            Real-Click (Find-El $null 'btnShowSelected' -Timeout 3)
            Start-Sleep 2
            Names 'txtSelectionCount'
            Real-Click (Find-El $null 'btnClearSelection' -Timeout 3)
            Set-ElText (Find-El $w 'txtAppPackagerSearch') ''
            Start-Sleep 1
            Names 'txtSelectionCount'
        }
        Step $theme 'tab-click' {
            Real-Click (Find-El $null 'tabStatusNotChecked' -Timeout 3)
            Start-Sleep 2
            $rows = (Find-El $w 'PackagerGrid').FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition).Count
            "Not checked tab: grid children $rows"
            Real-Click (Find-El $null 'tabStatusUpdate' -Timeout 3)
            Start-Sleep 2
            Move-MouseAway
            Shot $theme 'packages-tab-update'
            Real-Click (Find-El $null 'tabStatusAll' -Timeout 3)
        }
        Step $theme 'detail-pane' {
            $null = Find-El $w 'txtAppPackagerSearch'
            Set-ElText (Find-El $w 'txtAppPackagerSearch') '7-Zip'
            Start-Sleep 2
            $g = Find-El $w 'PackagerGrid'
            $row = $g.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::DataItem)))
            if (-not $row) { throw 'no grid row' }
            Real-Click $row
            Start-Sleep 3
            Names 'txtDetailTitle'
            $texts = foreach ($want in 'Vendor', 'Status', 'ConfigMgr version', 'Latest version', 'CVEs', 'Stage', 'Not staged', 'Not known') { "$want=$([bool](Find-El $w -Name $want -Timeout 1))" }
            "detail texts found: $($texts -join ', ')"
            Move-MouseAway
            Shot $theme 'packages-detail-pane'
            Real-Click (Find-El $null 'btnDetailPaneClose' -Timeout 3)
            Start-Sleep 1
            "pane title present after Close: $([bool](Find-El $null 'txtDetailTitle' -Timeout 1)); open button present: $([bool](Find-El $null 'btnDetailPaneOpen' -Timeout 2))"
            Real-Click (Find-El $null 'btnDetailPaneOpen' -Timeout 3)
            Start-Sleep 1
            "pane title present after Open: $([bool](Find-El $null 'txtDetailTitle' -Timeout 2))"
            Set-ElText (Find-El $w 'txtAppPackagerSearch') ''
        }
        Step $theme 'columns-menu' {
            "headers before: $(Header-Names $w)"
            Real-Click (Find-El $null 'btnColumns' -Timeout 3)
            Start-Sleep 1
            Real-Click (Find-El $null 'miColumnKEV' -Timeout 3)
            Start-Sleep 1
            [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
            Start-Sleep 1
            "headers after hiding KEV: $(Header-Names $w)"
            Move-MouseAway
            Shot $theme 'packages-columns-hidden'
        }
        Step $theme 'columns-saved' {
            $file = (Get-ChildItem $data -Recurse -Filter settings.json | Select-Object -First 1).FullName
            $saved = (Get-Content $file -Raw | ConvertFrom-Json).PackagesHiddenColumns
            "saved in settings.json: $($saved -join ',')"
            Stop-Apd
            # Start-Apd writes a new settings file, so the saved choice goes back in: the check is that the app applies it at start.
            $settings2 = @{} + $settings; $settings2.PackagesHiddenColumns = @($saved)
            $null = Start-Apd $exe $data $theme $settings2
            $w = Main-Window
            $w.SetFocus()
            Start-Sleep 5
            "headers after restart: $(Header-Names $w)"
            Real-Click (Find-El $null 'btnColumns' -Timeout 3)
            Start-Sleep 1
            Real-Click (Find-El $null 'miColumnsReset' -Timeout 3)
            Start-Sleep 1
            "headers after reset: $(Header-Names $w)"
        }
    }
    finally { Stop-Apd }
}
