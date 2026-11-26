<#
.SYNOPSIS
    The lab capture run of the guide images that need a state (decision D-21): starts the published app maximized on the 1080p work area (1920 x 1032), in both
    themes, drives it through a scenario and saves each image under its guide image id.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock, from the folder of the published app (like Capture-ApdPages.ps1):
        Run-VisibleProbe.ps1 -Worktree <published folder with this script> -ProbeArgs "& .\Capture-Scenarios.ps1 -Scenario InstallerAnalysis"
    The images are C:\guide\apdcap-out\<theme>\<id>.png. An image whose body has no more content than an empty page is not kept: the run says REFUSED for it (the guard
    that stops a capture run from replacing a guide image with an empty page; the test GuideImageContentTests checks the committed images the same way).
    The clicks use the positions of the page at 100 percent on the work area of 1920 x 1032; the tabs of Deployment Helper are found by automation id.
#>
param(
    [Parameter(Mandatory)][ValidateSet('InstallerAnalysis', 'SiteHygiene', 'LivePages', 'SettingsScroll')][string]$Scenario,
    [string[]]$Themes = @('Light', 'Dark')
)
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class W { [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e); }'
[void][W]::SetProcessDPIAware()
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
$out = 'C:\guide\apdcap-out'
New-Item -ItemType Directory -Force $out | Out-Null

function Click($x, $y) {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point $x, $y
    Start-Sleep -Milliseconds 300
    [W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}

# The share of the pixels of the body of the window (right of the sidebar, below the header and the toolbars) that differ from the most common color.
function BodyInk($bmp) {
    $left = [int]($bmp.Width * 0.18); $top = [int]($bmp.Height * 0.34); $bottom = [int]($bmp.Height * 0.95)
    $counts = @{}; $total = 0
    for ($y = $top; $y -lt $bottom; $y += 2) { for ($x = $left; $x -lt $bmp.Width; $x += 2) { $c = $bmp.GetPixel($x, $y).ToArgb(); $counts[$c] = 1 + [int]$counts[$c]; $total++ } }
    $max = ($counts.Values | Measure-Object -Maximum).Maximum
    return 1.0 - ($max / $total)
}

function Shot($path, [double]$minimumInk = 0.01) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $g.Dispose()
    $ink = BodyInk $bmp
    if ($ink -lt $minimumInk) { "REFUSED $(Split-Path $path -Leaf): the body has ink $([math]::Round($ink, 4)), like an empty page" }
    else { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); "OK $(Split-Path $path -Leaf) ink $([math]::Round($ink, 4))" }
    $bmp.Dispose()
}

foreach ($theme in $Themes) {
    $root = Join-Path 'C:\guide\apdcap-data' $theme
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $userRoot = Join-Path $root (Join-Path 'Users' $sid)
    New-Item -ItemType Directory -Force $userRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $userRoot 'settings.json') -Value ('{"Theme":"' + $theme + '","FirstRunCompleted":true,"SiteCode":"MCM","ProviderMachine":"cm01.contoso.com","UseConfigMgr":true}') -Encoding Ascii
    $env:APP_PACKAGER_DATA_ROOT = $root
    $p = Start-Process -FilePath (Join-Path $PSScriptRoot 'ApplicationPackagerUI.exe') -PassThru
    $script:App = $p
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline -and $p.MainWindowHandle -eq 0) { Start-Sleep -Milliseconds 500; $p.Refresh() }
    if ($p.MainWindowHandle -eq 0) { "FAIL $theme : no main window"; Stop-Process -Id $p.Id -Force; continue }
    [void][W]::ShowWindow($p.MainWindowHandle, 3)
    [void][W]::SetForegroundWindow($p.MainWindowHandle)
    Start-Sleep -Seconds 4
    $dir = Join-Path $out $theme.ToLower()
    New-Item -ItemType Directory -Force $dir | Out-Null

    if ($Scenario -eq 'InstallerAnalysis') {
        # A sample installer: the largest MSI of the Windows installer cache, copied so that nothing locks it.
        $sample = Get-ChildItem C:\Windows\Installer -Filter *.msi -ErrorAction SilentlyContinue | Sort-Object Length -Descending | Select-Object -First 1
        if (-not $sample) { 'FAIL no MSI in C:\Windows\Installer'; Stop-Process -Id $p.Id -Force; continue }
        New-Item -ItemType Directory -Force 'C:\guide\apdcap-sample' | Out-Null
        $copy = Copy-Item $sample.FullName 'C:\guide\apdcap-sample\sample.msi' -PassThru -Force
        $null = Click-Nav 'Installer Analysis'; Start-Sleep -Seconds 3
        Click 900 157; Start-Sleep -Milliseconds 500
        [System.Windows.Forms.SendKeys]::SendWait($copy.FullName.Replace('+', '{+}').Replace('^', '{^}').Replace('%', '{%}').Replace('~', '{~}') + '{ENTER}')
        Start-Sleep -Seconds 12
        $tabs = [ordered]@{
            'tools-installer-analysis-overview' = 288; 'tools-installer-analysis-msi-properties' = 376; 'tools-installer-analysis-payload' = 459
            'tools-installer-analysis-inner' = 542; 'tools-installer-analysis-file-browser' = 638; 'tools-installer-analysis-registry' = 741; 'tools-installer-analysis-strings' = 827
        }
        foreach ($id in $tabs.Keys) {
            Click $tabs[$id] 243; Start-Sleep -Seconds 3
            [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point 1000, 1060
            Start-Sleep -Milliseconds 400
            Shot (Join-Path $dir ($id + '.png'))
        }
    }
    if ($Scenario -eq 'SiteHygiene') {
        # Scan reads the site (CM01); the findings and the other tabs are shown after it.
        $null = Click-Nav 'Site Hygiene'; Start-Sleep -Seconds 4
        Click 291 154
        Start-Sleep -Seconds 150
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point 1000, 1060
        Shot (Join-Path $dir 'tools-site-hygiene-findings.png')
        $tabs = [ordered]@{ 'tools-site-hygiene-relationships' = 366; 'tools-site-hygiene-summary' = 488; 'tools-site-hygiene-history' = 553; 'tools-site-hygiene-options' = 613 }
        foreach ($id in $tabs.Keys) {
            Click $tabs[$id] 237; Start-Sleep -Seconds 5
            [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point 1000, 1060
            Start-Sleep -Milliseconds 400
            Shot (Join-Path $dir ($id + '.png'))
        }
    }
    if ($Scenario -eq 'LivePages') {
        # Pages that read the lab site when they open: (sidebar y, wait, then the tabs: id, x, y; a null x is the page as it opens; a text x is the
        # automation id of the tab).
        $pages = @(
            @{ Nav = 'Maintenance Windows'; Wait = 12; Tabs = @(@('tools-maintenance-windows-list', $null, 0), @('tools-maintenance-windows-coverage', 360, 155), @('tools-maintenance-windows-templates', 434, 155)) }
            @{ Nav = 'Collection and Compliance'; Wait = 15; Tabs = @(@('tools-collections-list', $null, 0), @('tools-collections-wql', 377, 155), @('tools-collections-templates', 457, 155), @('tools-collections-site-queries', 539, 155)) }
            @{ Nav = 'Deployment Helper'; Wait = 15; Tabs = @(@('tools-deployment-helper-deploy', $null, 0), @('tools-deployment-helper-history', 'tabHistory', 0), @('tools-deployment-helper-options', 'tabOptions', 0)) }
            @{ Nav = 'DP Content Manager'; Wait = 15; Tabs = @(@('tools-dp-content-dps', $null, 0), @('tools-dp-content-content', 322, 219), @('tools-dp-content-issues', 399, 219), @('tools-dp-content-history', 475, 219)) }
        )
        foreach ($page in $pages) {
            $null = Click-Nav $page.Nav; Start-Sleep -Seconds $page.Wait
            foreach ($tab in $page.Tabs) {
                if ($tab[1] -is [string]) {
                    $el = Find-El $null $tab[1]
                    if (-not $el) { "REFUSED $($tab[0]).png: no tab $($tab[1])"; continue }
                    Click-El $el; Start-Sleep -Seconds 6
                }
                elseif ($null -ne $tab[1]) { Click $tab[1] $tab[2]; Start-Sleep -Seconds 6 }
                [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point 1000, 1060
                Start-Sleep -Milliseconds 400
                Shot (Join-Path $dir ($tab[0] + '.png'))
            }
        }
    }    if ($Scenario -eq 'SettingsScroll') {
        # The Settings page from the top, one screen at a time (the wheel scrolls three lines of 40 px for each notch at 100 percent).
        $null = Click-Nav 'Settings'; Start-Sleep -Seconds 5
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point 1000, 600
        for ($i = 0; $i -lt 30; $i++) {
            Shot (Join-Path $dir ('settings-scroll-{0:00}.png' -f $i))
            for ($n = 0; $n -lt 18; $n++) { [W]::mouse_event(0x0800, 0, 0, [uint32]4294966576, [UIntPtr]::Zero); Start-Sleep -Milliseconds 120 }
            Start-Sleep -Milliseconds 500
        }
    }
    Stop-Process -Id $p.Id -Force
    Start-Sleep -Seconds 2
}
Remove-Item 'C:\guide\apdcap-sample' -Recurse -Force -ErrorAction SilentlyContinue
