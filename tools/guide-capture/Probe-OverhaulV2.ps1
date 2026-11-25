<#
.SYNOPSIS
    The lab check of the V1 overhaul phase 2 (queue row Q-126): the context strip and the plain page title, in the real app on CLIENT01 against CM01, light and dark.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <published folder with this script> -ProbeArgs "& .\Probe-OverhaulV2.ps1 -Run C:\guide\runs\v2-<timestamp>"
    Each theme: the strip name after the grid loads (ConfigMgr connected), the strip on the Settings page, then Systems in Use: WSUS on and Settings saved, and the
    strip is read again without a restart (WSUS not configured: no server is set), then WSUS off and saved again. A step that fails says FAIL and the run goes on.
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
function Strip-Name { $e = Find-El $null 'pnlContextStrip' -Timeout 8; if ($e) { $e.Current.Name } else { '(no strip)' } }
function Strip-Texts {
    $e = Find-El $null 'pnlContextStrip' -Timeout 8
    if (-not $e) { return '(no strip)' }
    $all = $e.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    (@($all | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' } | ForEach-Object { $_.Current.Name }) -join ' | ')
}

foreach ($theme in $Themes) {
    $data = Join-Path $Run "data-$theme"
    $settings = @{ FileShareRoot = '\\cm01.contoso.com\ContentShare$'; Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false } }
    $null = Start-Apd $exe $data $theme $settings
    try {
        $w = Main-Window
        $w.SetFocus()
        Step $theme 'strip-packages' {
            Start-Sleep 6
            "strip name (Packages): $(Strip-Name)"
            "strip texts: $(Strip-Texts)"
            $title = Find-El $w 'txtAppPackagerTitle' -Timeout 5
            if ($title) { $r = $title.Current.BoundingRectangle; "title: '$($title.Current.Name)' [$([int]$r.X),$([int]$r.Y) $([int]$r.Width)x$([int]$r.Height)]" }
            Move-MouseAway
            Shot $theme 'strip-packages'
        }
        Step $theme 'strip-settings' {
            if (-not (Click-Nav 'Settings')) { throw 'no Settings entry' }
            Start-Sleep 3
            "strip name (Settings): $(Strip-Name)"
            Move-MouseAway
            Shot $theme 'strip-settings'
        }
        Step $theme 'strip-follows-settings' {
            $w = Main-Window
            Real-Click (Find-El $w 'nav_systems' -Timeout 5)
            Start-Sleep 2
            $box = Find-El $w 'chkSystemsWsus' -Timeout 5
            $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
            Start-Sleep 1
            Real-Click (Find-El $w -Name 'Save Settings' -ControlType 'Button' -Timeout 5)
            Start-Sleep 3
            "strip name after WSUS on and Save: $(Strip-Name)"
            Move-MouseAway
            Shot $theme 'strip-wsus-on'
            $box = Find-El $w 'chkSystemsWsus' -Timeout 5
            $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
            Start-Sleep 1
            Real-Click (Find-El $w -Name 'Save Settings' -ControlType 'Button' -Timeout 5)
            Start-Sleep 3
            "strip name after WSUS off and Save: $(Strip-Name)"
        }
        Step $theme 'connections-button' {
            $b = Find-El $null 'btnContextConnections' -Timeout 5
            if (-not $b) { throw 'no Connections button' }
            $null = Click-Nav 'App Packager'
            Start-Sleep 2
            Real-Click (Find-El $null 'btnContextConnections' -Timeout 5)
            Start-Sleep 3
            $tree = Find-El $null 'nav_systems' -Timeout 5
            "Connections button opens Settings: $([bool]$tree)"
        }
        Step $theme 'site-switch' {
            # A second site: Add site makes it active; the strip must follow (site, host, states); switching back restores the first site.
            $null = Click-Nav 'App Packager'
            Start-Sleep 2
            Real-Click (Find-El $null 'btnSiteSwitcher' -Timeout 5)
            Start-Sleep 1
            Real-Click (Find-El $null 'miAddSite' -Timeout 5)
            Start-Sleep 2
            $dlg = Find-El $null 'txtAddSiteCode' -Timeout 8
            if (-not $dlg) { throw 'the Add site window did not open' }
            Set-ElText $dlg 'PS1'
            Set-ElText (Find-El $null 'txtAddSiteProvider' -Timeout 5) 'ps1.contoso.com'
            Real-Click (Find-El $null 'btnAddSiteAccept' -Timeout 5)
            Start-Sleep 5
            "strip name after Add site PS1: $(Strip-Name)"
            Move-MouseAway
            Shot $theme 'strip-site-ps1'
            Real-Click (Find-El $null 'btnSiteSwitcher' -Timeout 5)
            Start-Sleep 1
            $items = @(foreach ($w in (Get-ApdWindows)) { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::MenuItem))) })
            "site menu: " + (($items | ForEach-Object { $_.Current.AutomationId + '=' + $_.Current.Name }) -join ' | ')
            $mcm = $items | Where-Object { $_.Current.AutomationId -like 'miSite*' -and $_.Current.Name -match 'MCM' } | Select-Object -First 1
            if (-not $mcm) { throw 'no MCM entry in the site menu' }
            Real-Click $mcm
            Start-Sleep 6
            "strip name after switch back to MCM: $(Strip-Name)"
            Move-MouseAway
            Shot $theme 'strip-site-mcm'
        }
    }
    finally { Stop-Apd }
}
