<#
.SYNOPSIS
    The lab window check of the pages and windows that the wording change (queue row Q-65c, pull request 201) touched: the real app is started on the lab client in both
    themes, each page and window is shown at the work area (1920 x 1032) and at the clipping size (1024 x 720), and each view is saved as a PNG. A person or a reviewer
    reads the images for clipped text; the run adds the texts that the windows show to a list file.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <published folder with these scripts> -ProbeArgs "& .\Capture-WindowChecks.ps1 -Run C:\guide\runs\apd-<timestamp>"
    The images are <Run>\checks\<theme>\<view>-<size>.png. The run creates nothing on the site and writes nothing outside the run folder.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class ApdMove { [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int h2, bool repaint); }'
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$texts = New-Object System.Collections.Generic.List[string]

function Main-Window { Get-ApdWindows | Where-Object { $_.Current.Name -match 'App Packager' } | Select-Object -First 1 }
function Shot([string]$Theme, [string]$View, [string]$Size) { Save-Shot (Join-Path $Run (Join-Path 'checks' (Join-Path $Theme.ToLower() "$View-$Size.png"))); "OK $Theme $View $Size" }
function Step([string]$Theme, [string]$Id, [scriptblock]$Body) { try { & $Body } catch { "FAIL $Theme ${Id}: $($_.Exception.Message)" } }
function Show-Both([string]$Theme, [string]$View, $Process) {
    Move-MouseAway
    Shot $Theme $View '1920'
    [void][ApdW]::ShowWindow($Process.MainWindowHandle, 9)
    [void][ApdMove]::MoveWindow($Process.MainWindowHandle, 0, 0, 1024, 720, $true)
    Start-Sleep -Seconds 2
    Save-Shot (Join-Path $Run (Join-Path 'checks' (Join-Path $Theme.ToLower() "$View-1024.png"))) -Rect @(0, 0, 1024, 720)
    "OK $Theme $View 1024"
    [void][ApdW]::ShowWindow($Process.MainWindowHandle, 3)
    Start-Sleep -Seconds 2
}
function Nav([string]$Name) { if (-not (Click-Nav $Name)) { throw "the sidebar has no entry $Name" }; Start-Sleep -Seconds 4 }

foreach ($theme in $Themes) {
    $data = Join-Path $Run "data-$theme"
    # The Setup window. It does not open by itself: More, Setup of the App Packager page opens it.
    Step $theme 'setup' {
        $null = Start-Apd $exe (Join-Path $Run "setup-$theme") $theme @{ FirstRunCompleted = $false }
        try {
            Start-Sleep -Seconds 8
            Open-ApdMenuItem (Main-Window) 'btnMore' 'miSetup'
            if (-not (Find-El $null 'chkSetupConfigMgr' -Timeout 15)) { throw 'the Setup window did not open' }
            Start-Sleep -Seconds 2
            $texts.Add("== setup $theme"); Get-ElList (Main-Window) 150 | ForEach-Object { $texts.Add($_) }
            Move-MouseAway
            Shot $theme 'setup' '1920'
        }
        finally { Stop-Apd }
    }
    $settings = @{ FileShareRoot = '\\cm01.contoso.com\ContentShare$'; Systems = @{ ConfigMgr = $true; Intune = $true; Wsus = $true } }
    $p = Start-Apd $exe $data $theme $settings
    try {
        $w = Main-Window
        $w.SetFocus()
        # The pages that the change touched, by their sidebar names.
        foreach ($page in @(@('add-from-file', 'Add from file'), @('installer-analysis', 'Installer Analysis'), @('detection-tester', 'Detection Tester'), @('site-hygiene', 'Site Hygiene'))) {
            Step $theme $page[0] { Nav $page[1]; Show-Both $theme $page[0] $p }
        }
        Step $theme 'settings-pages' {
            Nav 'Settings'
            $tree = Find-El $null 'tvOptions' -Timeout 10
            foreach ($id in 'nav_systems', 'nav_configmgr', 'nav_intune', 'nav_installparams', 'nav_supersedence') {
                $item = Find-El $null $id -Timeout 5
                if ($item) { Click-El $item; Start-Sleep -Seconds 2; Show-Both $theme "settings-$id" $p }
            }
        }
        Step $theme 'convert-window' {
            Nav 'App Packager'
            Open-ApdMenuItem (Get-ApdWindows | Where-Object { $_.Current.Name -match 'App Packager' } | Select-Object -First 1) 'btnAddOrImport' 'miConvertFromCm'
            Start-Sleep -Seconds 4
            'windows: ' + ((Get-ApdWindows | ForEach-Object { $_.Current.Name }) -join ' | ')
            $cw = @(Get-ApdWindows | Where-Object { $_.Current.Name -notmatch '^App Packager' })[0]
            if ($cw) { $texts.Add("== convert $theme"); Get-ElList $cw 80 | ForEach-Object { $texts.Add($_) } }
            Move-MouseAway
            Shot $theme 'convert-window' '1920'
            Send-ApdKeys '{ESC}'; Start-Sleep -Seconds 2
            $null = Close-ApdWindow 'Convert from ConfigMgr'
        }
        Step $theme 'workbench-window' {
            # The Workbench entry of the sidebar opens the Workbench window over the page.
            Nav 'Workbench'
            Show-Both $theme 'workbench' $p
            $null = Close-ApdWindow 'Workbench'
            Start-Sleep -Seconds 2
        }
    }
    finally { Stop-Apd }
}
$texts | Set-Content -LiteralPath (Join-Path $Run 'window-texts.txt') -Encoding UTF8
