<#
.SYNOPSIS
    The lab capture run of the windows and settings views of the core pages (decision D-21): the Catalog window, the Convert from ConfigMgr window, the Discovery
    settings and prompt, the CVE columns, the CVEs window and the CVE report. The real app is driven with UI Automation, one theme after the other.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <published folder with these scripts> -ProbeArgs "& .\Capture-CoreWindows.ps1 -Run C:\guide\apd-pending\core"
    The images are <Run>\images\<theme>\<id>.png. Nothing is written to the site: the checks read, Convert only previews, Discover only reads the site database.
    A step that fails says FAIL for its image and the run goes on. The lab needs internet access for Check Latest and for the CVE lookup.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot,
    [string[]]$Checked = @('Mozilla Firefox (x64)', 'Audacity'),
    [string]$FileShareRoot = '\\cm01.contoso.com\ContentShare$',
    [string]$ConvertApplication = 'Audacity 3.7.9'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$ctCheck = [System.Windows.Automation.ControlType]::CheckBox

function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Status-Text($w) { $e = Find-El $w 'StatusLabel' -Timeout 2; if ($e) { $e.Current.Name } else { '' } }
function Wait-Idle($w, [int]$Seconds) {
    Start-Sleep 4
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline -and (Find-El $w 'btnCancelOperation' -Timeout 1)) { Start-Sleep 2 }
}
function Check-Row($w, [string]$Name) {
    Set-ElText (Find-El $w 'txtAppPackagerSearch') $Name
    Start-Sleep 2
    $grid = Find-El $w 'PackagerGrid'
    $box = $grid.FindAll($script:TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), $ctCheck)) |
        Where-Object { $_.Current.AutomationId -ne 'SelectAllCheckBox' } | Select-Object -First 1
    if ($box -and $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { Real-Click $box }
    Set-ElText (Find-El $w 'txtAppPackagerSearch') ''
    Start-Sleep 1
}

foreach ($theme in $Themes) {
    $data = Start-RunApp $exe $theme @{ FileShareRoot = $FileShareRoot; Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $true }; Wsus = @{ ServerName = 'cm01.contoso.com'; PortNumber = 8530 } }
    try {
        $w = Main-Window
        # ---- The Catalog window with its vendor groups expanded
        Step 'core-catalog' {
            $null = Click-Nav 'App Packager'; Start-Sleep 4
            Real-Click (Need-El 'btnCatalog')
            $null = Need-El 'txtCatalogTitle'
            Start-Sleep 2
            # Expand the vendor groups at the top of the list: every group that has the expand pattern.
            $list = Need-El 'lstCatalogVendors'
            $n = 0
            foreach ($e in $list.FindAll($script:TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
                if ($n -ge 6) { break }
                $p = $null
                if ($e.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p) -and $p.Current.ExpandCollapseState -eq 'Collapsed') { $p.Expand(); $n++; Start-Sleep -Milliseconds 400 }
            }
            "expanded groups: $n"
            Start-Sleep 2
            Shot 'core-catalog'
            Real-Click (Need-El 'btnCatalogClose')
        }
        # ---- The Convert from ConfigMgr window after a preview
        Step 'core-convert-window' {
            Open-ApdMenuItem $w 'btnAddOrImport' 'miConvertFromCm'
            $name = Need-El 'txtCmApplicationName'
            Set-ElText $name $ConvertApplication
            Invoke-El (Need-El 'btnConvertPreview')
            $deadline = (Get-Date).AddSeconds(90)
            do { Start-Sleep 2; $s = (Need-El 'txtConvertStatus').Current.Name } while ((Get-Date) -lt $deadline -and $s -notmatch 'Preview:|rror|not found|No application')
            "convert status: $s"
            Shot 'core-convert-window'
            Real-Click (Need-El 'btnConvertClose')
        }
        # ---- Settings: the Discovery fields, then the Discover prompt
        Step 'core-discover-settings' {
            $null = Click-Nav 'Settings'; Start-Sleep 4
            Real-Click (Need-El 'nav_configmgr')
            Start-Sleep 2
            $null = Scroll-ToId 'txtDiscoverySqlServer'
            Start-Sleep 2
            Shot 'core-discover-settings'
        }
        Step 'core-discover-prompt' {
            Real-Click (Need-El 'btnDiscoverAll')
            $prompt = Wait-El 'txtDiscoverPrompt' 120
            if (-not $prompt) { throw "no prompt: $((Find-El $null 'StatusLabel' -Timeout 2).Current.Name)" }
            Start-Sleep 2
            "prompt: $($prompt.Current.Name)"
            Shot 'core-discover-prompt'
            Real-Click (Need-El 'btnNotNow')
        }
        # ---- App Packager: the checks, the CVE columns, the CVEs window and the CVE report
        Step 'core-packages-cve-columns' {
            $null = Click-Nav 'App Packager'; Start-Sleep 4
            $w = Main-Window
            foreach ($name in $Checked) { Check-Row $w $name }
            Open-ApdMenuItem $w 'btnCheckMenu' 'miCheckLatest'
            Wait-Idle $w 240
            Open-ApdMenuItem $w 'btnCheckMenu' 'miCheckConfigMgr'
            Wait-Idle $w 240
            Open-ApdMenuItem $w 'btnCheckMenu' 'miCheckCves'
            Wait-Idle $w 600
            "status after Check CVEs: $(Status-Text $w)"
            # The CVE columns are at the right of the grid and show without scrolling at 1920 px; the search shows the row of the application that has CVEs.
            Set-ElText (Need-El 'txtAppPackagerSearch') 'Mozilla Firefox'
            Start-Sleep 3
            Shot 'core-packages-cve-columns'
        }
        Step 'core-cve-window' {
            $cells = @((Main-Window).FindAll($script:TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($script:AE::AutomationIdProperty), 'btnCveCell')) | Where-Object { -not $_.Current.IsOffscreen -and $_.Current.Name -match '^\d+$' -and [int]$_.Current.Name -gt 0 })
            if (-not $cells) { throw 'no row with CVEs on screen' }
            Real-Click $cells[0]
            $null = Need-El 'txtCveTitle' 30
            Start-Sleep 3
            Shot 'core-cve-window'
        }
        Step 'core-cve-report-html' {
            # Export report writes the report of the application and opens the HTML page in the default browser.
            Real-Click (Need-El 'btnCveExport')
            Start-Sleep 12
            Shot 'core-cve-report-html'
            [System.Windows.Forms.SendKeys]::SendWait('%{F4}'); Start-Sleep 2
            $c = Find-El $null 'btnCveClose' -Timeout 3
            if ($c) { Real-Click $c }
        }
    }
    finally { Stop-Apd }
}
