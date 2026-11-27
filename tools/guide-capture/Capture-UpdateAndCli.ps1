<#
.SYNOPSIS
    The lab capture run of the update window and of the command-line help (decision D-21): the update window with a newer version available and after the download and the checks,
    and the output of AppPackagerBuild --help.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-InSession.ps1 takes it). The two parts need two builds, so each has its own call.
      -Part Update: from a folder with a build of the final source whose version is OLDER than the latest release on GitHub (publish with -p:Version=<older>).
        The app has no setting for another release feed: it reads the latest release of its own repository, takes its installer only from the release
        downloads of that repository and accepts it only with the publisher's signature.
      -Part Cli: from a folder of the final build.
    The images are <Run>\images\<theme>\<id>.png. The app of the run gets its own TEMP and TMP under -Run, so the download of the update goes there; the run lists that folder and deletes it, and
    nothing else, after the app ends. The installer of the update is never started.
    The help image is taken in a classic console window: conhost.exe is started by its path, so Windows Terminal does not take the console also when it is
    the default. The script inside the window sets the font and the size of that window only; no console setting of the user changes.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot,
    [ValidateSet('Update', 'Cli')][string]$Part = 'Update'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$cli = Join-Path $AppFolder 'AppPackagerBuild.exe'

if ($Part -eq 'Update') {
    foreach ($theme in $Themes) {
        # A new folder for this app only: its TEMP and TMP. The app inherits them at its start; the run gets its own values back at once.
        $temp = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath((Join-Path $Run ("temp-$theme-" + [guid]::NewGuid().ToString('N').Substring(0, 8))))
        $null = New-Item -ItemType Directory -Path $temp
        $oldTemp = $env:TEMP; $oldTmp = $env:TMP
        try { $env:TEMP = $temp; $env:TMP = $temp; $data = Start-RunApp $exe $theme @{ CheckForUpdatesAtStartup = $true } }
        finally { $env:TEMP = $oldTemp; $env:TMP = $oldTmp }
        try {
            Step 'core-update-available' {
                # With a newer release on GitHub the start check opens the update window once; when it does not show, Settings, Updates, Check for updates opens it.
                $latest = Wait-El 'txtUpdateLatest' 45
                if (-not $latest) {
                    $null = Click-Nav 'Settings'; Start-Sleep 4
                    # The button is on the General page, which the Settings page shows first; the other pages are the fallback.
                    $check = Find-El $null 'btnCheckForUpdates' -Timeout 5
                    foreach ($page in $(if ($check) { @() } else { 'nav_general', 'nav_about', 'nav_updates' })) {
                        $node = Find-El $null $page -Timeout 5
                        if ($node) { Click-El $node; Start-Sleep 2; $check = Find-El $null 'btnCheckForUpdates' -Timeout 3; if ($check) { break } }
                    }
                    if (-not $check) { throw 'The Settings page has no Check for updates button.' }
                    Invoke-El $check
                    $latest = Need-El 'txtUpdateLatest' 30
                }
                $deadline = (Get-Date).AddSeconds(60)
                do { Start-Sleep 2; $s = (Need-El 'txtUpdateStatus').Current.Name } while ((Get-Date) -lt $deadline -and $s -notmatch 'newer|available|latest|Download')
                Start-Sleep 2
                "update status: $s | current: $((Need-El 'txtUpdateCurrent').Current.Name) | latest: $((Need-El 'txtUpdateLatest').Current.Name)"
                $download = Need-El 'btnUpdateDownload'
                if (-not $download.Current.IsEnabled) { throw "No newer release is offered: $s" }
                Shot 'core-update-available'
            }
            Step 'core-update-ready' {
                Invoke-El (Need-El 'btnUpdateDownload')
                $install = Wait-El 'btnUpdateInstall' 240 -Enabled
                if (-not $install) { throw "no ready state: $((Need-El 'txtUpdateStatus').Current.Name)" }
                Start-Sleep 2
                "update status: $((Need-El 'txtUpdateStatus').Current.Name)"
                Shot 'core-update-ready'
                Invoke-El (Need-El 'btnUpdateClose'); Start-Sleep 2
            }
        }
        finally { Stop-Apd }
        # The folder that this run made for the app: list the files of the download, delete the folder.
        "removing the temporary folder of the run $($temp): $(@(Get-ChildItem -LiteralPath $temp -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object Name) -join ', ')"
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $temp) { "LEFT: $temp" }
    }
    return
}

# ---- AppPackagerBuild --help in a classic console window
if (-not (Test-Path -LiteralPath $cli)) { throw "no command-line build at $cli (give -AppFolder)" }
if (-not ('ApdConsole' -as [type])) {
    Add-Type -TypeDefinition 'using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text; public static class ApdConsole { [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; } delegate bool Each(IntPtr h, IntPtr l); [DllImport("user32.dll")] static extern bool EnumWindows(Each f, IntPtr l); [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int max); [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h); [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r); [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags); public static long[] Consoles() { List<long> found = new List<long>(); EnumWindows(delegate(IntPtr h, IntPtr l) { StringBuilder s = new StringBuilder(64); GetClassNameW(h, s, 64); if (s.ToString() == "ConsoleWindowClass" && IsWindowVisible(h)) { found.Add(h.ToInt64()); } return true; }, IntPtr.Zero); return found.ToArray(); } }'
}
$inner = Join-Path $script:GuideRun 'help-console.ps1'
$ready = Join-Path $script:GuideRun ('help-ready-' + [guid]::NewGuid().ToString('N'))
Set-Content -LiteralPath $inner -Encoding UTF8 -Value @'
param([string]$Cli, [string]$Ready)
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class ApdFont { [StructLayout(LayoutKind.Sequential)] public struct Coord { public short X; public short Y; } [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct Info { public uint Size; public uint Index; public Coord Font; public int Family; public int Weight; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Face; } [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n); [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool SetCurrentConsoleFontEx(IntPtr h, bool max, ref Info info); public static bool Set(string face, short height) { Info i = new Info(); i.Size = (uint)Marshal.SizeOf(typeof(Info)); i.Font.Y = height; i.Family = 54; i.Weight = 400; i.Face = face; return SetCurrentConsoleFontEx(GetStdHandle(-11), false, ref i); } }'
$null = [ApdFont]::Set('Consolas', 12)
$raw = $Host.UI.RawUI
$buffer = $raw.BufferSize; $buffer.Width = 300; $buffer.Height = 600; $raw.BufferSize = $buffer
$size = $raw.WindowSize
$size.Width = [Math]::Min(300, $raw.MaxPhysicalWindowSize.Width); $size.Height = [Math]::Min(85, $raw.MaxPhysicalWindowSize.Height)
$raw.WindowSize = $size
$raw.WindowTitle = 'AppPackagerBuild --help'
Clear-Host
& $Cli --help
Set-Content -LiteralPath $Ready -Value 'ready'
'@

$beforeConsoles = [ApdConsole]::Consoles()
$p = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\conhost.exe') -ArgumentList ('powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -NoExit -File "{0}" -Cli "{1}" -Ready "{2}"' -f $inner, $cli, $ready) -PassThru
Add-ApdOwnedProcess $p
try {
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath $ready)) { Start-Sleep -Milliseconds 500 }
    if (-not (Test-Path -LiteralPath $ready)) { throw 'The console did not finish the help text in 60 seconds.' }
    Start-Sleep 2
    $new = @([ApdConsole]::Consoles() | Where-Object { $beforeConsoles -notcontains $_ })
    if ($new.Count -ne 1) { throw "The run expects one new console window and found $($new.Count)." }
    $h = [IntPtr]$new[0]
    # Move to the top left corner without a change of the size, then to the front.
    [void][ApdConsole]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, 0, 0, 0x0001)
    [void][ApdConsole]::SetForegroundWindow($h)
    Start-Sleep 2
    if ([ApdConsole]::GetForegroundWindow() -ne $h) { throw 'The console window did not come to the front, so the image could show another window.' }
    $r = New-Object 'ApdConsole+R'
    [void][ApdConsole]::GetWindowRect($h, [ref]$r)
    $area = Get-ApdScreen
    $x = [Math]::Max($r.L, 0); $y = [Math]::Max($r.T, 0)
    $rect = @($x, $y, ([Math]::Min($r.Rt, $area.Width) - $x), ([Math]::Min($r.B, $area.Height) - $y))
    "console window: $($r.L),$($r.T) $($r.Rt - $r.L)x$($r.B - $r.T)"
    foreach ($theme in $Themes) { $script:GuideTheme = $theme; Shot 'core-cli-help' $rect }
}
finally {
    # The process that this run started, through its handle: the end of the console host closes its window. Run-InSession also ends the job of the run.
    Stop-ApdProcess $p
    Remove-Item -LiteralPath $ready, $inner -Force -ErrorAction SilentlyContinue
}
