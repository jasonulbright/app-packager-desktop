<#
.SYNOPSIS
    The lab capture run of the guide images (decision D-21): starts the published app maximized on the 1080p work area of CLIENT01 (1920 x 1032), in both themes, and saves
    every page as a PNG of the work area. Run it in the interactive session of the lab user under the capture lock, for example:
    Run-VisibleProbe.ps1 -Worktree <published folder with this script> -ProbeArgs "& .\Capture-ApdPages.ps1"
    The images are in C:\guide\apdcap-out\<theme>\<page>.png; GuideCaptureSteps maps them to the guide image ids. The clicks use the positions of the sidebar at 100 percent.
#>
param([string[]]$Themes = @('Light', 'Dark'))
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class W { [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e); }'
[void][W]::SetProcessDPIAware()
$out = 'C:\guide\apdcap-out'
New-Item -ItemType Directory -Force $out | Out-Null
$pages = 'App Packager', 'Add from web', 'Add from file', 'Installer Analysis', 'Detection Tester', 'Deployment Helper', 'Pivots and Scripts', 'Collection and Compliance', 'Maintenance Windows', 'DP Content Manager', 'Site Hygiene', 'Settings'
function Shot($path) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
foreach ($theme in $Themes) {
    $root = Join-Path 'C:\guide\apdcap-data' $theme
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $root | Out-Null
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $userRoot = Join-Path $root (Join-Path 'Users' $sid)
    New-Item -ItemType Directory -Force $userRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $userRoot 'settings.json') -Value ('{"Theme":"' + $theme + '","FirstRunCompleted":true,"SiteCode":"MCM","ProviderMachine":"cm01.contoso.com","UseConfigMgr":true}') -Encoding Ascii
    $env:APP_PACKAGER_DATA_ROOT = $root
    $exe = Join-Path $PSScriptRoot 'ApplicationPackagerUI.exe'
    $p = Start-Process -FilePath $exe -PassThru
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline -and $p.MainWindowHandle -eq 0) { Start-Sleep -Milliseconds 500; $p.Refresh() }
    if ($p.MainWindowHandle -eq 0) { "FAIL $theme : no main window"; Stop-Process -Id $p.Id -Force; continue }
    [void][W]::ShowWindow($p.MainWindowHandle, 3)
    [void][W]::SetForegroundWindow($p.MainWindowHandle)
    Start-Sleep -Seconds 4
    $dir = Join-Path $out $theme.ToLower()
    New-Item -ItemType Directory -Force $dir | Out-Null
    Shot (Join-Path $dir 'window-start.png')
    $nav = [ordered]@{ 'App Packager' = 129; 'Add from web' = 169; 'Add from file' = 209; 'Installer Analysis' = 321; 'Detection Tester' = 361; 'Deployment Helper' = 401; 'Pivots and Scripts' = 441; 'Collection and Compliance' = 481; 'Maintenance Windows' = 521; 'DP Content Manager' = 561; 'Site Hygiene' = 601; 'Settings' = 984; 'Workbench' = 249 }
    foreach ($page in $nav.Keys) {
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point 110, $nav[$page]
        Start-Sleep -Milliseconds 300
        [W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Seconds 4
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point 1000, 1060
        Start-Sleep -Milliseconds 500
        Shot (Join-Path $dir (($page -replace '[^A-Za-z0-9]+', '-').ToLower() + '.png'))
        "OK $theme $page"
        if ($page -eq "Workbench") { [System.Windows.Forms.SendKeys]::SendWait("%{F4}"); Start-Sleep -Seconds 2 }
    }
    Stop-Process -Id $p.Id -Force
    Start-Sleep -Seconds 2
}
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
"screen {0}x{1}" -f $bounds.Width, $bounds.Height
