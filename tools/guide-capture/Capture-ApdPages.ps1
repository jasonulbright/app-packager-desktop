<#
.SYNOPSIS
    The lab capture run of the default view of every page (decision D-21): starts the app maximized on the 1080p work area of CLIENT01 (1920 x 1032), in both
    themes, selects each sidebar entry by its name and saves the work area.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock, for example:
        Run-VisibleProbe.ps1 -Worktree <published folder with this script> -ProbeArgs "& .\Capture-ApdPages.ps1 -Run C:\guide\runs\apd-<timestamp>"
    Every page is saved as <Run>\pages\<theme>\<page>.png. The pages that are the source of a guide image are also saved as <Run>\images\<theme>\<id>.png.
    The run only opens pages; it writes nothing to the site.
#>
param(
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$Run = (Join-Path $PSScriptRoot 'out'),
    [string]$AppFolder = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
# Page file name to guide image id. The other guide images of these pages come from runs that put the page into a state.
$guide = @{ 'app-packager' = 'start-sidebar-default'; 'add-from-web' = 'core-add-app-step1'; 'add-from-file' = 'core-add-file-pick' }
$nav = 'App Packager', 'Add from web', 'Add from file', 'Installer Analysis', 'Detection Tester', 'Deployment Helper', 'Pivots and Scripts', 'Collection and Compliance', 'Maintenance Windows', 'DP Content Manager', 'Site Hygiene', 'Settings', 'Workbench'

foreach ($theme in $Themes) {
    $data = Join-Path $Run "data-$theme"
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    try { $null = Start-Apd $exe $data $theme } catch { "FAIL $theme : $($_.Exception.Message)"; continue }
    try {
        Start-Sleep -Seconds 4
        Move-MouseAway
        Save-Shot (Join-Path $Run (Join-Path 'pages' (Join-Path $theme.ToLower() 'window-start.png')))
        foreach ($page in $nav) {
            if (-not (Click-Nav $page)) { "FAIL $theme $page : the sidebar has no entry with this name"; continue }
            Start-Sleep -Seconds 6
            Move-MouseAway
            $name = ($page -replace '[^A-Za-z0-9]+', '-').ToLower()
            Save-Shot (Join-Path $Run (Join-Path 'pages' (Join-Path $theme.ToLower() ($name + '.png'))))
            "OK $theme $page"
            if ($guide.ContainsKey($name)) {
                Save-Shot (Join-Path $Run (Join-Path 'images' (Join-Path $theme.ToLower() ($guide[$name] + '.png'))))
                "OK $theme $($guide[$name])"
            }
            if ($page -eq 'Workbench') { if (-not (Close-ApdWindow 'Workbench')) { "FAIL $theme : the Workbench window did not close" } }
        }
    }
    finally { Stop-Apd }
}
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
"screen {0}x{1}" -f $bounds.Width, $bounds.Height
