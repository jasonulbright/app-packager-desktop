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
function Nav([int]$y) { Click-At (New-Object System.Drawing.Point 110, $y); Start-Sleep -Seconds 4 }

foreach ($theme in $Themes) {
    $data = Join-Path $Run "data-$theme"
    # The first-run window (SetupWindow).
    Step $theme 'setup' {
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $root = Join-Path $Run "setup-$theme"
        $userRoot = Join-Path $root (Join-Path 'Users' $sid)
        New-Item -ItemType Directory -Force $userRoot | Out-Null
        Set-Content -LiteralPath (Join-Path $userRoot 'settings.json') -Value ('{"Theme":"' + $theme + '","FirstRunCompleted":false}') -Encoding Ascii
        $env:APP_PACKAGER_DATA_ROOT = $root
        $p = Start-Process -FilePath $exe -PassThru
        Start-Sleep -Seconds 12
        $script:App = $p
        'windows: ' + ((Get-ApdWindows | ForEach-Object { $_.Current.Name }) -join ' | ')
        $w = @(Get-ApdWindows)[0]
        if ($w) { $texts.Add("== setup $theme"); Get-ElList $w 80 | ForEach-Object { $texts.Add($_) } }
        Move-MouseAway
        Shot $theme 'setup' '1920'
        Stop-Apd
    }
    $settings = @{ FileShareRoot = '\\cm01.contoso.com\ContentShare$'; Systems = @{ ConfigMgr = $true; Intune = $true; Wsus = $true } }
    $p = Start-Apd $exe $data $theme $settings
    try {
        $w = Main-Window
        $w.SetFocus()
        # The pages that the change touched, at the sidebar positions of 1920 x 1032.
        foreach ($page in @(@('add-from-file', 209), @('installer-analysis', 321), @('detection-tester', 361), @('site-hygiene', 601))) {
            Step $theme $page[0] { Nav $page[1]; Show-Both $theme $page[0] $p }
        }
        Step $theme 'settings-pages' {
            Nav 984
            $tree = Find-El $null 'tvOptions' -Timeout 10
            foreach ($id in 'nav_systems', 'nav_configmgr', 'nav_intune', 'nav_installparams', 'nav_supersedence') {
                $item = Find-El $null $id -Timeout 5
                if ($item) { Click-El $item; Start-Sleep -Seconds 2; Show-Both $theme "settings-$id" $p }
            }
        }
        Step $theme 'convert-window' {
            Nav 129
            Click-El (Find-El $null 'btnConvertFromCm' -Timeout 10)
            Start-Sleep -Seconds 4
            'windows: ' + ((Get-ApdWindows | ForEach-Object { $_.Current.Name }) -join ' | ')
            $cw = @(Get-ApdWindows | Where-Object { $_.Current.Name -notmatch '^App Packager' })[0]
            if ($cw) { $texts.Add("== convert $theme"); Get-ElList $cw 80 | ForEach-Object { $texts.Add($_) } }
            Move-MouseAway
            Shot $theme 'convert-window' '1920'
            [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Seconds 2
            if (Get-ApdWindows | Where-Object { $_.Current.Name -notmatch '^App Packager' }) { [System.Windows.Forms.SendKeys]::SendWait('%{F4}'); Start-Sleep -Seconds 2 }
        }
        Step $theme 'workbench-window' {
            # The Workbench entry of the sidebar opens the Workbench window over the page.
            Nav 249
            Show-Both $theme 'workbench' $p
            if (Get-ApdWindows | Where-Object { $_.Current.Name -match 'Workbench' }) { [System.Windows.Forms.SendKeys]::SendWait('%{F4}') }
            Start-Sleep -Seconds 2
        }
    }
    finally { Stop-Apd }
}
$texts | Set-Content -LiteralPath (Join-Path $Run 'window-texts.txt') -Encoding UTF8
