<#
.SYNOPSIS
    The lab capture run of the replacement revision dialog of a bring-your-own application (decision D-21): the app starts with --analyze-drop on an installer, the drop dialog
    saves it as an application, and Replace installer in the Workbench opens the dialog for the installer of another product. The real app is driven with UI Automation, one
    theme after the other.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <published folder with these scripts> -ProbeArgs "& .\Capture-Byo.ps1 -Run C:\guide\apd-pending\byo"
    The images are <Run>\images\<theme>\<id>.png. The application is saved in the user definitions folder of the run, so nothing of the lab user changes. Nothing is staged and
    nothing goes to a site. The lab needs internet access: the installers are the PuTTY MSI and the 7-Zip MSI.
    A step that fails says FAIL for its image, saves the screen under <Run>\fail and the run goes on.
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

function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }

# The installer that the application is saved from: the PuTTY MSI. The installer of the replacement revision: the 7-Zip MSI.
$putty = 'https://the.earth.li/~sgtatham/putty/latest/w64/'
$sample = Join-Path $Run 'sample\putty-installer.msi'
$other = Join-Path $Run 'sample\other-x64.msi'
New-Item -ItemType Directory -Force (Split-Path $sample) | Out-Null
if (-not (Test-Path $sample)) {
    $page = (Invoke-WebRequest -UseBasicParsing $putty).Content
    $file = [regex]::Match($page, 'putty-64bit-[0-9.]+-installer\.msi').Value
    Invoke-WebRequest -UseBasicParsing ($putty + $file) -OutFile $sample
}
if (-not (Test-Path $other)) {
    $rel = Invoke-RestMethod -UseBasicParsing 'https://api.github.com/repos/ip7z/7zip/releases/latest' -Headers @{ 'User-Agent' = 'guide-capture' }
    $asset = $rel.assets | Where-Object { $_.name -like '7z*-x64.msi' } | Select-Object -First 1
    Invoke-WebRequest -UseBasicParsing $asset.browser_download_url -OutFile $other
}

foreach ($theme in $Themes) {
    $data = Start-RunApp $exe $theme @{ Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false } } @('--analyze-drop', $sample)
    try {
        Step 'core-byo-replacement' {
            # The drop dialog: confirm the predicted values, save as an application, open it in the Workbench.
            $confirm = Wait-El 'DropReviewConfirm' 20
            if ($confirm) { Set-Check $confirm $true }
            Start-Sleep 2
            Invoke-El (Need-Name 'Save as Application' 'Button' 30)
            $yes = Need-Name 'Yes' 'Button' 60
            Real-Click $yes
            # The Workbench window: Replace installer is in the Application section of a bring-your-own application.
            $replace = Wait-El 'btnReplaceInstaller' 60
            if (-not $replace) { throw 'the Workbench shows no Replace installer button' }
            Invoke-El $replace; Start-Sleep 2
            Send-DialogPath $other
            Start-Sleep 6
            Shot 'core-byo-replacement'
            Send-ApdKeys '{ESC}'; Start-Sleep 2
        }
    }
    finally { Stop-Apd }
}
