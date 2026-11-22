<#
.SYNOPSIS
    The lab capture run of the dialogs and tabs of the tools that read the site (decision D-21): Detection Tester, Collection and Compliance, Maintenance Windows, DP Content Manager,
    Pivots and Scripts and Installer Analysis. The real app is driven with UI Automation, one theme after the other.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <published folder with these scripts> -ProbeArgs "& .\Capture-Tools.ps1 -Run C:\guide\apd-pending\tools"
    The images are <Run>\images\<theme>\<id>.png. The run only reads the site and opens the plan, edit and apply dialogs. It closes each plan with Cancel, so nothing is written to the site.
    A step that fails says FAIL for its image, saves the screen under <Run>\fail and the run goes on.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot,
    [string]$Collection = 'Copy of All Workstations',
    [string]$MaintenanceWindow = 'Zone 02 patch window'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'

function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Tab([string]$Id) { Real-Click (Need-El $Id); Start-Sleep 2 }
# Clicks the first visible element below the scope (a grid or a list) whose name matches.
function Combo([string]$Id, [string[]]$Likes) {
    $c = Need-El $Id
    foreach ($l in $Likes) { try { Select-Combo $c $l; return } catch { } }
    throw "no entry like $($Likes -join ' / ') in $Id"
}
function Wait-Window([string]$Id, [double]$Seconds = 30) { $e = Wait-El $Id $Seconds; if (-not $e) { throw "the window with $Id did not open" }; Start-Sleep 2; $e }

# A sample installer that has inner installers: a self-extracting bundle of two MSI packages (7-Zip and PuTTY), built with the 7-Zip of the lab client. The Visual C++
# redistributable is a bundle that 7-Zip does not list, so it holds no inner installer for the page.
$bundle = Join-Path $Run 'sample\Guide-Bundle-Setup.exe'
if (-not (Test-Path $bundle)) {
    New-Item -ItemType Directory -Force (Split-Path $bundle) | Out-Null
    try {
        $parts = Join-Path $Run 'sample\parts'
        New-Item -ItemType Directory -Force $parts | Out-Null
        $rel = Invoke-RestMethod -UseBasicParsing 'https://api.github.com/repos/ip7z/7zip/releases/latest' -Headers @{ 'User-Agent' = 'guide-capture' }
        $asset = $rel.assets | Where-Object { $_.name -like '7z*-x64.msi' } | Select-Object -First 1
        Invoke-WebRequest -UseBasicParsing $asset.browser_download_url -OutFile (Join-Path $parts $asset.name)
        $putty = 'https://the.earth.li/~sgtatham/putty/latest/w64/'
        $page = (Invoke-WebRequest -UseBasicParsing $putty).Content
        $file = [regex]::Match($page, 'putty-64bit-[0-9.]+-installer\.msi').Value
        Invoke-WebRequest -UseBasicParsing ($putty + $file) -OutFile (Join-Path $parts $file)
        $sevenZip = Join-Path $env:ProgramFiles '7-Zip'
        & (Join-Path $sevenZip '7z.exe') a "-sfx$(Join-Path $sevenZip '7z.sfx')" $bundle (Join-Path $parts '*.msi') | Out-Null
        Remove-Item $parts -Recurse -Force
    } catch { "bundle build failed: $($_.Exception.Message)" }
}

foreach ($theme in $Themes) {
    $data = Start-RunApp $exe $theme @{ Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false } }
    try {
        # ---- Detection Tester
        Step 'tools-detection-tester-registry' {
            $null = Click-Nav 'Detection Tester'; Start-Sleep 3
            Combo 'cboHive' @('HKLM*', '*LOCAL_MACHINE*')
            Set-ElText (Need-El 'txtKeyPath') 'SOFTWARE\Microsoft\Windows NT\CurrentVersion'
            Set-ElText (Need-El 'txtValueName') 'CurrentBuildNumber'
            Set-ElText (Need-El 'txtExpectedValue') ''
            Real-Click (Need-El 'btnRun'); Start-Sleep 6
            Shot 'tools-detection-tester-registry'
        }
        Step 'tools-detection-tester-script' {
            Combo 'cboClauseType' @('*Script*')
            Start-Sleep 2
            Set-ElText (Need-El 'txtScript') "if (Test-Path (Join-Path `$env:windir 'System32\notepad.exe')) { Write-Output 'Notepad is installed'; exit 0 } else { exit 1 }"
            Real-Click (Need-El 'btnRun'); Start-Sleep 8
            Shot 'tools-detection-tester-script'
        }
        Step 'tools-detection-tester-installed' {
            Tab 'tabInstalledApplications'; Start-Sleep 4
            $null = Pick-Row 'dgApplications' '*Microsoft Edge*'
            Shot 'tools-detection-tester-installed'
        }

        # ---- Collection and Compliance: collections
        Step 'tools-collections-editor' {
            $null = Click-Nav 'Collection and Compliance'; Start-Sleep 12
            $null = Pick-Row 'dgCollections' "*$Collection*"
            Real-Click (Need-El 'btnEditCollection')
            $null = Wait-Window 'btnCollectionSave'
            Shot 'tools-collections-editor'
            Real-Click (Need-El 'btnCollectionCancel'); Start-Sleep 2
        }
        Step 'tools-collections-remove-plan' {
            Real-Click (Need-El 'btnRemoveCollection')
            $null = Wait-Window 'btnChangePlanCancel' 60
            Shot 'tools-collections-remove-plan'
            Real-Click (Need-El 'btnChangePlanCancel'); Start-Sleep 2
        }

        # ---- Collection and Compliance: compliance
        Step 'tools-compliance-catalog' {
            Real-Click (Need-El 'rbSectionCompliance'); Start-Sleep 8
            Tab 'tabCatalog'
            $null = Pick-Row 'dgCatalog' '*SMB Signing Required*'
            Start-Sleep 2
            Shot 'tools-compliance-catalog'
        }
        Step 'tools-compliance-deploy' {
            Real-Click (Need-El 'btnCatalogDeploy')
            $null = Wait-Window 'btnDeployShowPlan'
            Shot 'tools-compliance-deploy'
            # The plan: pick the collection with its button, then Show plan.
            $pick = @((Find-El $null 'scrollDeploy' -Timeout 3).FindAll($script:TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.AutomationId -like 'btnPick*' }) | Select-Object -First 1
            if ($pick) {
                # The picker window: filter to the collection, tick it and use it.
                Real-Click $pick; Start-Sleep 3
                $null = Wait-Window 'txtPickerFilter'
                Set-ElText (Need-El 'txtPickerFilter') 'Zone 13'; Start-Sleep 2
                Real-Click (Need-El 'btnPickerCheckShown'); Start-Sleep 1
                Real-Click (Need-Name 'Use collection' 'Button'); Start-Sleep 3
            }
        }
        Step 'tools-compliance-plan' {
            $plan = Need-El 'btnDeployShowPlan'
            if (-not $plan.Current.IsEnabled) { throw 'Show plan is disabled' }
            # A deploy with remediation: switch DetectOnly off, so the plan asks for its confirmation box.
            $detect = Find-El $null $null 'DetectOnly' 'CheckBox' -Timeout 5
            if ($detect) { Set-Check $detect $false; Start-Sleep 1 }
            Real-Click $plan
            $null = Wait-Window 'btnChangePlanCancel' 60
            foreach ($box in 'chkChangePlanExtra', 'chkChangePlanDestructive', 'chkChangePlanSecond') {
                $el = Find-El $null $box -Timeout 2
                if ($el -and -not $el.Current.IsOffscreen) { Set-Check $el $true }
            }
            Start-Sleep 1
            Shot 'tools-compliance-plan'
            Real-Click (Need-El 'btnChangePlanCancel'); Start-Sleep 2
            $c = Find-El $null 'btnDeployCancel' -Timeout 3
            if ($c) { Real-Click $c; Start-Sleep 2 }
        }
        Step 'tools-compliance-editor' {
            Tab 'tabEditor'; Start-Sleep 3
            $null = Pick-Row 'lstEditorItems' '*SMB Signing Required*'
            Start-Sleep 3
            $null = Pick-First 'dgSettings'
            Real-Click (Need-El 'btnEditSetting')
            $null = Wait-Window 'btnSettingSave'
            Shot 'tools-compliance-editor'
            Real-Click (Need-El 'btnSettingCancel'); Start-Sleep 2
        }
        Step 'tools-compliance-live' {
            Tab 'tabLive'; Start-Sleep 8
            Shot 'tools-compliance-live'
        }
        Step 'tools-compliance-drift' {
            Tab 'tabAuditing'; Start-Sleep 5
            Real-Click (Need-El 'btnAuditingRefresh'); Start-Sleep 8
            Shot 'tools-compliance-drift'
        }
        Step 'tools-compliance-compliance' {
            Tab 'tabComplianceCounts'; Start-Sleep 6
            Shot 'tools-compliance-compliance'
        }

        # ---- Maintenance Windows
        Step 'tools-maintenance-windows-editor' {
            $null = Click-Nav 'Maintenance Windows'; Start-Sleep 12
            $null = Pick-Row 'dgWindows' "*$MaintenanceWindow*"
            Real-Click (Need-El 'btnEditWindow')
            $null = Wait-Window 'btnEditorSave'
            Shot 'tools-maintenance-windows-editor'
            Real-Click (Need-El 'btnEditorCancel'); Start-Sleep 2
        }
        Step 'tools-maintenance-windows-plan' {
            Real-Click (Need-El 'btnRemoveWindow')
            $null = Wait-Window 'btnChangePlanCancel' 60
            Shot 'tools-maintenance-windows-plan'
            Real-Click (Need-El 'btnChangePlanCancel'); Start-Sleep 2
        }
        Step 'tools-maintenance-windows-apply-template' {
            Tab 'tabTemplates'; Start-Sleep 3
            $null = Pick-First 'dgTemplates'
            Real-Click (Need-El 'btnApplyTemplate')
            $null = Wait-Window 'btnApplyShowPlan'
            # Two collections ticked: the first two rows of the list.
            $boxes = @((Need-El 'dgApplyTargets').FindAll($script:TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::CheckBox))) | Where-Object { -not $_.Current.IsOffscreen })
            foreach ($b in ($boxes | Select-Object -First 2)) { Set-Check $b $true }
            Start-Sleep 1
            Shot 'tools-maintenance-windows-apply-template'
            Real-Click (Need-El 'btnApplyCancel'); Start-Sleep 2
        }

        # ---- DP Content Manager
        Step 'tools-dp-content-dps' {
            $null = Click-Nav 'DP Content Manager'; Start-Sleep 12
            $null = Pick-Row 'dgDps' '*CM01*'
            Start-Sleep 4
            Shot 'tools-dp-content-dps'
        }
        Step 'tools-dp-content-plan-remove' {
            Tab 'tabContent'; Start-Sleep 3
            $null = Pick-Row 'dgContent' '*ConfigMgr Client Health*'
            Real-Click (Need-El 'btnRemoveFromDp')
            # The pair window first: check every pair of the content, then continue to the plan.
            $null = Wait-Window 'btnPairsContinue' 60
            Real-Click (Need-El 'btnPairsCheckShown'); Start-Sleep 1
            Real-Click (Need-El 'btnPairsContinue')
            $null = Wait-Window 'btnChangePlanCancel' 60
            Shot 'tools-dp-content-plan-remove'
            Real-Click (Need-El 'btnChangePlanCancel'); Start-Sleep 2
        }

        # ---- Pivots and Scripts
        Step 'tools-pivots-options' {
            $null = Click-Nav 'Pivots and Scripts'; Start-Sleep 6
            Tab 'tabPivotsOptions'
            Real-Click (Need-El 'btnTestConnection'); Start-Sleep 8
            Shot 'tools-pivots-options'
        }
        Step 'tools-pivots-site-scripts' {
            Tab 'tabSiteScripts'; Start-Sleep 3
            Real-Click (Need-El 'btnRefreshSiteScripts'); Start-Sleep 6
            Shot 'tools-pivots-site-scripts'
        }

        # ---- Installer Analysis: the Inner Installers tab with the breadcrumb
        Step 'tools-installer-analysis-inner' {
            if (-not (Test-Path $bundle)) { throw 'no sample bundle' }
            $null = Click-Nav 'Installer Analysis'; Start-Sleep 3
            Real-Click (Need-El 'btnOpenInstaller')
            Send-DialogPath $bundle
            $null = Wait-El 'tabInnerInstallers' 120
            Start-Sleep 15
            Tab 'tabInnerInstallers'; Start-Sleep 3
            $null = Pick-First 'dgInner'
            Real-Click (Need-El 'btnInnerAnalyze'); Start-Sleep 15
            Tab 'tabInnerInstallers'; Start-Sleep 3
            Shot 'tools-installer-analysis-inner'
        }
    }
    finally { Stop-Apd }
}
