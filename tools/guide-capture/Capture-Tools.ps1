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

# A sample installer that has inner installers: the Visual C++ redistributable (a bundle that carries MSI packages).
$bundle = Join-Path $Run 'sample\vc_redist.x64.exe'
if (-not (Test-Path $bundle)) {
    New-Item -ItemType Directory -Force (Split-Path $bundle) | Out-Null
    try { Invoke-WebRequest -UseBasicParsing 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile $bundle } catch { "bundle download failed: $($_.Exception.Message)" }
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
                Real-Click $pick; Start-Sleep 3
                $null = Pick-Row 'dgCollections' '*Zone 13*' 15
            }
        }
        Step 'tools-compliance-plan' {
            $plan = Need-El 'btnDeployShowPlan'
            if (-not $plan.Current.IsEnabled) { throw 'Show plan is disabled' }
            Real-Click $plan
            $null = Wait-Window 'btnChangePlanCancel' 60
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
            $null = Pick-First 'dgContent'
            Real-Click (Need-El 'btnRemoveFromDp')
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
