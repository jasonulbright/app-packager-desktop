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
    'core-settings-citrix'      = 'nav_citrix'
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
