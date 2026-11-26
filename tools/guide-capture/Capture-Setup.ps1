<#
.SYNOPSIS
    The lab capture run of the Setup window of the first start (decision D-21): the first page with the Systems in Use check boxes and the Connection details page.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <published folder with these scripts> -ProbeArgs "& .\Capture-Setup.ps1 -Run C:\guide\apd-pending\setup"
    The app starts with FirstRunCompleted false in the data folder of the run. The Setup window does not open by itself: the run opens it with More, Setup
    on the App Packager page. The window is closed without Save.
    The images are <Run>\images\<theme>\<id>.png. A step that fails says FAIL for its image, saves the screen under <Run>\fail and the run goes on.
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

foreach ($theme in $Themes) {
    $data = Start-RunApp $exe $theme @{ FirstRunCompleted = $false; DownloadRoot = 'C:\Packaging\Staging'; FileShareRoot = '\\cm01.contoso.com\ContentShare$' }
    try {
        Step 'start-setup-systems' {
            if (-not (Wait-El 'btnMore' 40 -Enabled)) { throw 'the App Packager page shows no More button' }
            Start-Sleep 3
            Open-ApdMenuItem (Main-Window) 'btnMore' 'miSetup'
            $cm = Wait-El 'chkSetupConfigMgr' 40
            if (-not $cm) { throw 'the Setup window did not open' }
            Start-Sleep 2
            Set-Check $cm $true
            Set-Check (Need-El 'chkSetupWsus') $true
            Start-Sleep 1
            Shot 'start-setup-systems'
        }
        Step 'start-setup-connection' {
            $next = Need-Name 'Next' 'Button' 5
            Click-El $next; Start-Sleep 3
            $server = Wait-El 'txtSetupWsusServer' 10
            if ($server) { Set-ElText $server 'cm01.contoso.com' }
            Start-Sleep 2
            Shot 'start-setup-connection'
        }
        Close-OtherWindows
    }
    finally { Stop-Apd }
}
