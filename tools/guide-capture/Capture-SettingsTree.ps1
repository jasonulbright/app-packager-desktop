<#
.SYNOPSIS
    The lab capture run of the guide images that show the tree of Settings (decision D-21): starts the published app maximized on the 1080p work area
    (1920 x 1032), opens Settings, selects one page of the tree and saves the work area as a PNG under the guide image id.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock, from the folder of the published app (like Capture-ApdPages.ps1):
        Run-VisibleProbe.ps1 -Worktree <published folder with this script and ApdUiDriver.ps1> -ProbeArgs "& .\Capture-SettingsTree.ps1"
    The images are <OutDir>\<theme>\<id>.png. The run checks that the tree shows no horizontal scroll bar: it fails when the scroll bar of the tree is on screen.
#>
param(
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot,
    [string]$OutDir = (Join-Path $PSScriptRoot 'out'),
    [string]$DataRoot = (Join-Path $PSScriptRoot 'data'),
    # Only these image ids; empty takes all of them.
    [string[]]$Only = @(),
    # WSUS in use, with its page enabled (the image core-settings-wsus).
    [switch]$WsusInUse
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')

# Guide image id and the tree page that it shows (the automation id of the tree item).
$images = [ordered]@{
    'core-settings-configmgr'   = 'nav_configmgr'
    # Preview stands at the end of the second Citrix page (Citrix Updates and Policy).
    'core-settings-citrix'      = 'nav_citrixpolicy'
    'core-settings-intune-cve'  = 'nav_intune'
    'core-settings-top'         = 'nav_general'
    'core-settings-wsus'        = 'nav_wsus'
    'core-settings-install-parameters' = 'nav_installparams'
    'core-settings-tools'       = 'nav_tools'
    'core-settings-github-api'  = 'nav_github'
    'core-settings-defaults'    = 'nav_dtdefaults'
    'core-settings-install-parameters-sources' = 'nav_localsources'
    'core-settings-about'       = 'nav_about'
    'core-script-signing-settings' = 'nav_signing'
    'core-one-click-settings'   = 'nav_oneclick'
}
foreach ($theme in $Themes) {
    $root = Join-Path $DataRoot $theme
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
    $dir = Join-Path $OutDir $theme.ToLower()
    $settings = if ($WsusInUse) { @{ Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $true } } } else { @{} }
    $null = Start-Apd -Exe (Join-Path $AppFolder 'ApplicationPackagerUI.exe') -DataRoot $root -Theme $theme -Settings $settings
    try {
        $window = @(Get-ApdWindows)[0]
        $settings = Find-El $window 'tvOptions' '' '' 3
        if (-not $settings) {
            if (-not (Click-Nav 'Settings')) { throw 'The sidebar has no Settings entry.' }
            Start-Sleep -Seconds 4
        }
        $tree = Find-El $window 'tvOptions' '' '' 15
        if (-not $tree) { throw 'The tree of Settings did not open.' }
        foreach ($id in @($images.Keys | Where-Object { -not $Only -or $Only -contains $_ })) {
            $item = Find-El $window $images[$id] '' '' 10
            if (-not $item) { throw ('The tree item {0} was not found.' -f $images[$id]) }
            Click-El $item
            Start-Sleep -Seconds 2
            if ($id -eq 'core-settings-citrix') {
                # Preview only shows the install arguments of the choices; nothing is saved.
                $preview = Find-El $window '' 'Preview' 'Button' 10
                if (-not $preview) { throw 'The page shows no Preview button.' }
                Invoke-El $preview
                Start-Sleep -Seconds 2
                [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point 1000, 600
                $isText = New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Text)
                $shown = $false
                foreach ($notch in 1..40) {
                    $result = @($window.FindAll($script:TS::Descendants, $isText) | Where-Object { $_.Current.Name -cmatch '(?m)^Current: ' -and -not $_.Current.IsOffscreen })
                    if ($result) { $shown = $true; break }
                    Send-ApdWheel ([uint32]4294967176); Start-Sleep -Milliseconds 300
                }
                if (-not $shown) { throw 'The result of Preview did not come into view. No image is saved.' }
                # To the end of the page, so the whole result shows.
                foreach ($notch in 1..10) { Send-ApdWheel ([uint32]4294967176); Start-Sleep -Milliseconds 150 }
                Start-Sleep -Seconds 1
            }
            Move-MouseAway
            # A horizontal scroll bar of the tree is a part of the tree with the automation id HorizontalScrollBar.
            $bar = Find-El $tree 'HorizontalScrollBar' '' '' 1
            if ($bar -and -not $bar.Current.IsOffscreen) { throw ('{0} / {1}: the tree shows a horizontal scroll bar.' -f $theme, $id) }
            Save-Shot (Join-Path $dir ($id + '.png')) -MaskFooter
            'OK {0} {1}' -f $theme, $id
        }
    }
    finally { Stop-Apd }
}
