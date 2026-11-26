<#
.SYNOPSIS
    The lab capture run of the update window and of the command-line help (decision D-21): the update window with a newer version available and after the download and the checks,
    and the output of AppPackagerBuild --help.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from a folder that holds an OLDER published build than the latest release
    (a newer version must exist on GitHub for the first image), with these scripts:
        Run-VisibleProbe.ps1 -Worktree <folder of the older build with these scripts> -ProbeArgs "& .\Capture-UpdateAndCli.ps1 -Run C:\guide\apd-pending\misc"
    The images are <Run>\images\<theme>\<id>.png. The app of the run gets its own TEMP and TMP under -Run, so the download of the update goes there; the run lists that folder and deletes it, and
    nothing else, after the app ends.
    The installer of the update is never started. The help image is taken in a console window with a small font; the run sets the console font of the cmd.exe key of the user for the shot and puts it back.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$cli = Join-Path $AppFolder 'AppPackagerBuild.exe'

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

# ---- AppPackagerBuild --help in a console window
$key = 'HKCU:\Console\%SystemRoot%_system32_cmd.exe'
$had = Test-Path $key
$old = if ($had) { Get-ItemProperty $key } else { $null }
$createdKey = $false
try {
    # New-Item -Force would replace an existing key and drop its values: the key is made only when it is missing.
    if (-not $had) { $null = New-Item -Path $key; $createdKey = $true }
    # Font height 12 px; window 300 columns x 85 lines; buffer 300 x 300; black on white would hide the text color, so the default colors stay.
    Set-ItemProperty $key -Name FaceName -Value 'Consolas'
    Set-ItemProperty $key -Name FontSize -Type DWord -Value 0x000C0000
    Set-ItemProperty $key -Name FontFamily -Type DWord -Value 54
    Set-ItemProperty $key -Name WindowSize -Type DWord -Value 0x0055012C
    Set-ItemProperty $key -Name ScreenBufferSize -Type DWord -Value 0x012C012C
    Set-ItemProperty $key -Name WindowPosition -Type DWord -Value 0x00000000
    $p = Start-Process cmd.exe -ArgumentList '/k', ('"' + $cli + '" --help') -PassThru
    Add-ApdOwnedProcess $p
    Start-Sleep 6
    # The shot is the console window only: its rectangle, so that nothing of the desktop shows.
    if (-not ('ApdRect' -as [type])) { Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class ApdRect { [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; } [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r); }' }
    $p.Refresh()
    $r = New-Object 'ApdRect+R'
    [void][ApdRect]::GetWindowRect($p.MainWindowHandle, [ref]$r)
    "console window: $($r.L),$($r.T) $($r.Rt - $r.L)x$($r.B - $r.T)"
    foreach ($theme in $Themes) { $script:GuideTheme = $theme; Shot 'core-cli-help' @($r.L, $r.T, ($r.Rt - $r.L), ($r.B - $r.T)) }
    Stop-ApdProcess $p
}
finally {
    if ($had) { foreach ($n in 'FaceName', 'FontSize', 'FontFamily', 'WindowSize', 'ScreenBufferSize', 'WindowPosition') { if ($null -ne $old.$n) { Set-ItemProperty $key -Name $n -Value $old.$n } else { Remove-ItemProperty $key -Name $n -ErrorAction SilentlyContinue } } }
    elseif ($createdKey) { Remove-Item $key -Recurse -Force -ErrorAction SilentlyContinue }
}
