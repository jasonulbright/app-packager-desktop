<#
.SYNOPSIS
    The lab capture run of the Add from web wizard (steps 2 to 7), the Add from file review and result panel, and the sections of the Workbench (decision D-21): drives the real app
    on the lab client with UI Automation, one theme after the other, and saves each image under its guide image id.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the published app with ApdUiDriver.ps1:
        Run-VisibleProbe.ps1 -Worktree <published folder with these scripts> -ProbeArgs "& .\Capture-AddAndWorkbench.ps1 -Run C:\guide\apd-pending\add"
    The images are <Run>\images\<theme>\<id>.png (the work area, 1920 x 1032). A step that fails says FAIL for its image and the run goes on.
    The wizard saves one definition (PuTTY) in the user definitions folder of the run, so nothing of the lab user changes. The lab needs internet access:
    the version source and the download address are the PuTTY site. Nothing is staged and nothing goes to a site.
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
$putty = 'https://the.earth.li/~sgtatham/putty/latest/w64/'

# The sample installer for Add from file: the PuTTY MSI, downloaded once into the run.
$sample = Join-Path $Run 'sample\putty-installer.msi'
if (-not (Test-Path $sample)) {
    New-Item -ItemType Directory -Force (Split-Path $sample) | Out-Null
    $page = (Invoke-WebRequest -UseBasicParsing $putty).Content
    $file = [regex]::Match($page, 'putty-64bit-[0-9.]+-installer\.msi').Value
    Invoke-WebRequest -UseBasicParsing ($putty + $file) -OutFile $sample
}
"sample: $((Get-Item $sample).Length) bytes"

foreach ($theme in $Themes) {
    $data = Start-RunApp $exe $theme @{ Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false } }
    try {
        # ---- Add from web: the steps 2 to 7 with PuTTY
        Step 'core-add-app-step2-test' {
            $null = Click-Nav 'Add from web'; Start-Sleep 3
            Set-ElText (Need-El 'txtAddWebVendor') 'Simon Tatham'
            Set-ElText (Need-El 'txtAddWebName') 'PuTTY (x64)'
            Set-ElText (Need-El 'txtAddWebVendorUrl') 'https://www.chiark.greenend.org.uk/~sgtatham/putty/'
            Invoke-El (Need-El 'btnAddWebNext'); Start-Sleep 2
            Set-ElText (Need-El 'txtAddWebScraperUrl') $putty
            Set-ElText (Need-El 'txtAddWebPattern') 'putty-64bit-([0-9]+\.[0-9]+)-installer\.msi'
            Invoke-El (Need-El 'btnAddWebTestScrape')
            $deadline = (Get-Date).AddSeconds(60)
            do { Start-Sleep 2; $r = (Need-El 'txtAddWebScrapeResult').Current.Name; $v = (Need-El 'txtAddWebScrapeResult').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } while ((Get-Date) -lt $deadline -and $v -notmatch 'Version:')
            "scrape result: $v"
            Shot 'core-add-app-step2-test'
        }
        Step 'core-add-app-step3' {
            Invoke-El (Need-El 'btnAddWebNext'); Start-Sleep 2
            Set-ElText (Need-El 'txtAddWebDownloadUrl') ($putty + 'putty-64bit-{version}-installer.msi')
            Select-Combo (Need-El 'cboAddWebInstallerType') 'MSI'
            Set-ElText (Need-El 'txtAddWebFramework') 'NativeMSI'
            Shot 'core-add-app-step3'
        }
        Step 'core-add-app-step4-analysis' {
            Invoke-El (Need-El 'btnAddWebNext'); Start-Sleep 2
            Invoke-El (Need-El 'btnAddWebAnalyze')
            if (-not (Wait-El 'btnAddWebAccept' 240)) { throw "no proposals: $((Need-El 'txtAddWebAnalysisStatus').Current.Name)" }
            Start-Sleep 2
            Shot 'core-add-app-step4-analysis'
            Invoke-El (Need-El 'btnAddWebAccept'); Start-Sleep 2
        }
        Step 'core-add-app-step5-install' {
            Invoke-El (Need-El 'btnAddWebNext'); Start-Sleep 2
            Shot 'core-add-app-step5-install'
        }
        Step 'core-add-app-step6-detection' {
            Invoke-El (Need-El 'btnAddWebNext'); Start-Sleep 2
            Shot 'core-add-app-step6-detection'
        }
        Step 'core-add-app-step7-review' {
            Invoke-El (Need-El 'btnAddWebNext'); Start-Sleep 2
            Shot 'core-add-app-step7-review'
            Invoke-El (Need-El 'btnAddWebSave'); Start-Sleep 4
            $done = Find-El $null 'btnAddResultDone' -Timeout 20
            if ($done) { Invoke-El $done; Start-Sleep 2 }
        }

        # ---- Add from file: the review and the result panel
        Step 'core-add-file-review' {
            $null = Click-Nav 'Add from file'; Start-Sleep 3
            Invoke-El (Need-El 'btnAddFileOpen')
            Send-DialogPath $sample
            if (-not (Wait-El 'btnAddFileSave' 120)) { throw 'no review' }
            Start-Sleep 3
            $accept = Find-El $null 'chkAddFileAcceptPredicted' -Timeout 2
            if ($accept -and -not $accept.Current.IsOffscreen) { Set-Check $accept $true }
            Shot 'core-add-file-review'
        }
        Step 'core-add-result-panel' {
            $save = Wait-El 'btnAddFileSave' 20 -Enabled
            if (-not $save) { throw 'Save is disabled' }
            Click-El $save; Start-Sleep 4
            if (-not (Wait-El 'btnAddResultDone' 30)) { throw "no result panel: $((Need-El 'txtAddFileStatus').Current.Name)" }
            Shot 'core-add-result-panel'
            $done = Find-El $null 'btnAddResultDone' -Timeout 5
            if ($done) { Invoke-El $done; Start-Sleep 2 }
        }

        # ---- Workbench: the sections of a catalog application over the App Packager page
        Step 'workbench' {
            $null = Click-Nav 'App Packager'; Start-Sleep 4
            # The Workbench opens on the highlighted row of the grid: highlight the 7-Zip row.
            $w = Main-Window
            Set-ElText (Need-El 'txtAppPackagerSearch') '7-Zip'; Start-Sleep 2
            Click-El (Find-El $w -Name '7-Zip (x64)' -Timeout 8); Start-Sleep 1
            Set-ElText (Need-El 'txtAppPackagerSearch') ''; Start-Sleep 2
            $null = Click-Nav 'Workbench'; Start-Sleep 5
            "workbench application: $((Need-El 'PART_EditableTextBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value)"
            Shot 'core-workbench-application'
            foreach ($pair in @(@('Install & uninstall', 'core-workbench-install'), @('Detection', 'core-workbench-detection'), @('Requirements & variants', 'core-workbench-requirements'),
                                @('Timing & execution', 'core-workbench-timing'), @('Source files', 'core-workbench-source-files'))) {
                Step $pair[1] {
                    Click-El (Need-Name $pair[0] 'ListItem'); Start-Sleep 2
                    Shot $pair[1]
                }
            }
            Step 'core-workbench-review' {
                Click-El (Need-Name 'Review & build' 'ListItem'); Start-Sleep 1
                Invoke-El (Need-El 'btnValidate'); Start-Sleep 5
                Shot 'core-workbench-review'
            }
            Step 'core-workbench-bundle-menu' {
                Click-El (Need-El 'btnBundle'); Start-Sleep 1
                Shot 'core-workbench-bundle-menu'
                Send-ApdKeys '{ESC}'; Start-Sleep 1
            }
            if (-not (Close-ApdWindow 'Workbench')) { 'The Workbench window did not close.' }
        }
    }
    finally { Stop-Apd }
}
