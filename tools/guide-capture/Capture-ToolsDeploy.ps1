<#
.SYNOPSIS
    The lab capture run of the Deployment Helper and its Rings tab (decision D-21): the plans of a Required deployment and of a distribute-and-deploy, the
    plan editor with 13 rings, the preview of a run, the plan of a run with Required rings and an open run.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-InSession.ps1 takes it), from the folder of the published app.
    The images are <Run>\images\<theme>\<id>.png. The plans of the images are closed with Cancel. The site must hold the application -Application with its content
    on the distribution point and no deployment (tools/guide-capture/lab-seed, part Deploy). The collections Zone 06 to Zone 13 must be empty.
    What the run writes to the site, and removes again in the same theme:
      - one Available deployment of the application to the empty collection $EmptyCollection, removed with Undo of the Deploy tab;
      - one run of the ring plan "Empty zones run" with its first ring (Zone 06), removed with Undo run.
    Every confirmed plan is checked first: the object of each row is the application to an empty collection. At the end of a theme a deployment of the application that is left on an
    empty collection is removed. The ring plans are files in the data folder of the run. "Guide rings" (13 rings, Zone 01 to Zone 13) is only saved and expanded.
    A step that fails says FAIL for its image, saves the screen under <Run>\fail and the run goes on.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot,
    [string]$Application = 'ZZAPD Deploy Tool',
    [string]$EmptyCollection = 'Zone 13'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
. (Join-Path $PSScriptRoot 'PublishCleanup.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'

function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Tab([string]$Id) { Real-Click (Need-El $Id); Start-Sleep 2 }
# A radio button is set with its pattern, so it does not matter where on the page it stands.
function Pick-Radio([string]$Id) { (Need-El $Id).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Combo-Plan([string]$Name) { Select-Combo (Need-El 'cboRingPlan') "*$Name*" }
function Wait-Window([string]$Id, [double]$Seconds = 30) { $e = Wait-El $Id $Seconds; if (-not $e) { throw "the window with $Id did not open" }; Start-Sleep 2; $e }
function Cancel-Plan { $c = Find-El $null 'btnChangePlanCancel' -Timeout 5; if ($c) { Real-Click $c; Start-Sleep 2 } }
# Ticks the extra boxes of a plan and runs its action button; waits for the result and closes the window.
function Confirm-Plan([int]$Seconds = 180) {
    foreach ($box in 'chkChangePlanExtra', 'chkChangePlanDestructive', 'chkChangePlanSecond') {
        $el = Find-El $null $box -Timeout 2
        if ($el -and -not $el.Current.IsOffscreen) { Set-Check $el $true }
    }
    # Nothing is confirmed unless every row of the plan stays on the empty collections.
    Assert-ChangePlanOnly $script:OnlyRows $script:SharedZones
    Real-Click (Need-El 'btnChangePlanConfirm')
    $close = $null
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline -and -not $close) {
        Start-Sleep 3
        # An undo asks once more before it removes a deployment: answer it with the button of the question.
        $ask = Find-El $null $null 'Remove deployment' 'Button' -Timeout 1
        if ($ask -and -not $ask.Current.IsOffscreen -and $ask.Current.IsEnabled) { Click-El $ask; Start-Sleep 2 }
        $c = Find-El $null 'btnChangePlanClose' -Timeout 1
        if ($c -and -not $c.Current.IsOffscreen -and $c.Current.IsEnabled) { $close = $c }
    }
    if (-not $close) { throw 'the plan did not finish' }
    $r = Find-El $null 'txtChangePlanResult' -Timeout 2
    "plan result: " + $(if ($r) { $r.Current.Name } else { '(no text)' })
    Start-Sleep 1
    $close
}

# Confirms the open plan, says its result, and closes its window.
function Finish-Plan([int]$Seconds = 180) {
    $all = @(Confirm-Plan $Seconds)
    if ($all.Count -gt 1) { $all[0..($all.Count - 2)] }
    Real-Click $all[$all.Count - 1]
}

# Zone 01 to Zone 05 are shared and hold devices: no confirmed row may name one. Zone 06 to Zone 13 must be empty before the run starts.
$script:SharedZones = 'Zone 0[1-5]\b'
$script:OnlyRows = [regex]::Escape($Application) + '.* to ' + [regex]::Escape($EmptyCollection) + ' \(\w+\)$'
$emptyZones = @('Zone 06', 'Zone 07', 'Zone 08', 'Zone 09', 'Zone 10', 'Zone 11', 'Zone 12', 'Zone 13')
if ($emptyZones -notcontains $EmptyCollection) { throw "-EmptyCollection must be one of $($emptyZones -join ', ')." }
Connect-GuideSite
foreach ($zone in $emptyZones) {
    $c = Invoke-GuideSite { Get-CMDeviceCollection -Name $zone -ErrorAction Stop }
    if (-not $c -or [int]$c.MemberCount -ne 0) { throw "The collection $zone is missing or not empty. The run does not start." }
}
$appFound = @(Get-GuideApplications | Where-Object { $_.LocalizedDisplayName -ceq $Application })
if ($appFound.Count -ne 1 -or -not $Application.StartsWith('ZZAPD', [StringComparison]::Ordinal)) { throw "The site must hold exactly one application '$Application' with a ZZAPD title (lab-seed, part Deploy)." }
function Get-RunDeployments { @(Invoke-GuideSite { Get-CMApplicationDeployment -Name $Application -ErrorAction Stop }) }
if ((Get-RunDeployments).Count -ne 0) { throw "'$Application' already has a deployment. The run does not start: it removes only deployments that it creates." }

# Writes two ring plans on the empty collections beside the saved plan "Guide rings", as copies of that file: one with Required rings, for its plan
# window only, and one Available plan for the real run.
function Write-EmptyZonePlans([string]$DataRoot) {
    $saved = Get-ChildItem -LiteralPath $DataRoot -Recurse -File -Filter '*.json' | Where-Object { $_.Directory.Name -eq 'rings' -and $_.BaseName -like 'Guide*rings*' } | Select-Object -First 1
    if (-not $saved) { throw 'The saved ring plan "Guide rings" was not found in the data folder.' }
    foreach ($kind in @(@('Empty zones required', 'Required'), @('Empty zones run', 'Available'))) {
        $plan = Get-Content -LiteralPath $saved.FullName -Raw | ConvertFrom-Json
        $plan.Name = $kind[0]
        $plan.Description = 'One ring for each empty Zone collection of the lab.'
        $rings = @($plan.Rings | Where-Object { $emptyZones -contains $_.CollectionName })
        if ($rings.Count -ne $emptyZones.Count) { throw "The saved plan has $($rings.Count) rings on the empty collections, not $($emptyZones.Count)." }
        foreach ($ring in $rings) {
            $ring.Purpose = $kind[1]
            if ($kind[1] -eq 'Required') { $ring | Add-Member -Force -NotePropertyName DeadlineOffsetDays -NotePropertyValue ([int]$ring.AvailableOffsetDays + 3) }
        }
        $plan.Rings = $rings
        Set-Content -LiteralPath (Join-Path $saved.DirectoryName "$($kind[0] -replace ' ', '-').json") -Encoding Ascii -Value ($plan | ConvertTo-Json -Depth 8)
    }
}

foreach ($theme in $Themes) {
    $data = Start-RunApp $exe $theme @{ Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false } }
    try {
        $null = Click-Nav 'Deployment Helper'; Start-Sleep 12
        # ---- Deploy tab: the plan of a Required deployment
        Step 'tools-deployment-helper-plan-required' {
            Set-ElText (Need-El 'txtObjectFilter') $Application; Start-Sleep 2; $null = Pick-First 'dgObjects'
            Set-ElText (Need-El 'txtCollectionFilter') $EmptyCollection; Start-Sleep 2; $null = Pick-First 'dgCollections'
            Pick-Radio 'rbRequired'; Start-Sleep 1
            Invoke-El (Need-El 'btnValidate'); Start-Sleep 1; Start-Sleep 8
            Invoke-El (Need-El 'btnDeploy'); Start-Sleep 1
            $null = Wait-Window 'btnChangePlanCancel' 60
            Shot 'tools-deployment-helper-plan-required'
            Cancel-Plan
        }
        Step 'tools-deployment-helper-distribute-create' {
            Pick-Radio 'rbAvailable'; Start-Sleep 1
            # The distribution point groups of the Content card: check the first group and the box that distributes first.
            $boxes = @((Need-El 'dgDpGroups').FindAll($script:TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::CheckBox))) | Where-Object { -not $_.Current.IsOffscreen })
            if ($boxes) { Set-Check $boxes[0] $true }
            Set-Check (Need-El 'chkDistributeFirst') $true
            Start-Sleep 1
            Invoke-El (Need-El 'btnValidate'); Start-Sleep 1; Start-Sleep 8
            Invoke-El (Need-El 'btnDeploy'); Start-Sleep 1
            $null = Wait-Window 'btnChangePlanCancel' 60
            Shot 'tools-deployment-helper-distribute-create'
            Cancel-Plan
            Set-Check (Need-El 'chkDistributeFirst') $false
        }
        # A real Available deployment of the application to the empty collection: the History tab then holds its create record. The Undo of the Deploy tab removes it again.
        Step 'tools-deployment-helper-history' {
            $script:OnlyRows = [regex]::Escape($Application) + '.* to ' + [regex]::Escape($EmptyCollection) + ' \(\w+\)$'
            Set-ElText (Need-El 'txtCollectionFilter') $EmptyCollection; Start-Sleep 2; $null = Pick-First 'dgCollections'
            Pick-Radio 'rbAvailable'; Start-Sleep 1
            Invoke-El (Need-El 'btnValidate'); Start-Sleep 1; Start-Sleep 8
            Invoke-El (Need-El 'btnDeploy'); Start-Sleep 1
            $null = Wait-Window 'btnChangePlanCancel' 60
            Finish-Plan; Start-Sleep 3
            Tab 'tabHistory'; Start-Sleep 3
            Real-Click (Need-El 'btnHistoryRefresh'); Start-Sleep 4
            $null = Pick-First 'dgHistory'
            Shot 'tools-deployment-helper-history'
        }
        Step 'tools-deployment-helper-history-undo' {
            Tab 'tabDeploy'; Start-Sleep 2
            Invoke-El (Need-El 'btnDeploymentUndo'); Start-Sleep 1
            $null = Wait-Window 'btnChangePlanCancel' 60
            Finish-Plan; Start-Sleep 3
        }
        # ---- Rings
        Step 'tools-deployment-rings-plan-editor' {
            Tab 'tabRings'; Start-Sleep 6
            Invoke-El (Need-El 'btnRingPlanNew'); Start-Sleep 1
            $null = Wait-Window 'btnRingPlanSave'
            Set-ElText (Need-El 'txtRingPlanName') 'Guide rings'
            Set-ElText (Need-El 'txtRingPlanDescription') 'Thirteen rings, one for each Zone collection of the lab.'
            Real-Click (Need-El 'btnRingsFromCollections')
            $null = Wait-Window 'btnPickerPick'
            Set-ElText (Need-El 'txtPickerFilter') 'Zone'; Start-Sleep 2
            Real-Click (Need-El 'btnPickerCheckShown'); Start-Sleep 1
            Real-Click (Need-El 'btnPickerPick'); Start-Sleep 3
            Set-ElText (Need-El 'txtFillFirst') '0'
            Set-ElText (Need-El 'txtFillBetween') '2'
            Set-ElText (Need-El 'txtFillDeadline') '3'
            Real-Click (Need-El 'btnFillOffsets'); Start-Sleep 2
            Shot 'tools-deployment-rings-plan-editor'
            Real-Click (Need-El 'btnRingPlanSave'); Start-Sleep 3
            Write-EmptyZonePlans $data
            Real-Click (Need-El 'btnRingsRefresh'); Start-Sleep 6
        }
        Step 'tools-deployment-rings-new-run' {
            Set-ElText (Need-El 'txtRingObjectFilter') $Application; Start-Sleep 2; $null = Pick-First 'dgRingObjects'
            Combo-Plan 'Guide rings'
            Invoke-El (Need-El 'btnRingExpand'); Start-Sleep 1; Start-Sleep 6
            Invoke-El (Need-El 'btnRingValidate'); Start-Sleep 1; Start-Sleep 12
            Shot 'tools-deployment-rings-new-run'
        }
        Step 'tools-deployment-rings-plan-required' {
            Combo-Plan 'Empty zones required'
            Invoke-El (Need-El 'btnRingExpand'); Start-Sleep 1; Start-Sleep 6
            Invoke-El (Need-El 'btnRingValidate'); Start-Sleep 1; Start-Sleep 12
            Invoke-El (Need-El 'btnRingCreate'); Start-Sleep 1
            $null = Wait-Window 'btnChangePlanCancel' 60
            Shot 'tools-deployment-rings-plan-required'
            Cancel-Plan
        }

        # A real run with its first ring on the empty collection: the run list then shows an open run with its rings. Undo run removes it again.
        Step 'tools-deployment-rings-open-runs' {
            # The real run takes the plan of the empty collections, so its first ring (Zone 06) has no device.
            $script:OnlyRows = [regex]::Escape($Application) + '.* to Zone (0[6-9]|1[0-3]) \(\w+\)$'
            Combo-Plan 'Empty zones run'
            Invoke-El (Need-El 'btnRingExpand'); Start-Sleep 1; Start-Sleep 6
            # Hold the later rings: only the first ring is created, so the run stays open and the other rings wait.
            Set-Check (Need-El 'chkRingHold') $true
            Invoke-El (Need-El 'btnRingValidate'); Start-Sleep 1; Start-Sleep 12
            Invoke-El (Need-El 'btnRingCreate'); Start-Sleep 1
            $null = Wait-Window 'btnChangePlanCancel' 60
            Finish-Plan; Start-Sleep 4
            Real-Click (Need-El 'btnRingsRefresh'); Start-Sleep 8
            # The run list is below the form and the result of the plan: scroll it into view, select the open run, then show the run and its rings.
            # The page scrolls above the pinned action bar; the run list is the last card, so scroll to the end of the page.
            [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point 1000, 400
            for ($i = 0; $i -lt 40; $i++) { Send-ApdWheel ([uint32]4294967176); Start-Sleep -Milliseconds 150 }
            Start-Sleep 2
            try { $null = Pick-First 'dgRingRuns' 0 15 } catch { "no run row to select: $($_.Exception.Message)" }
            Start-Sleep 2
            Shot 'tools-deployment-rings-open-runs'
        }
        Step 'tools-deployment-rings-undo-run' {
            $script:OnlyRows = [regex]::Escape($Application) + '.* to Zone (0[6-9]|1[0-3]) \(\w+\)$'
            Invoke-El (Need-El 'btnRingUndo'); Start-Sleep 1
            $null = Wait-Window 'btnChangePlanCancel' 60
            Finish-Plan; Start-Sleep 3
        }
    }
    finally {
        Stop-Apd
        # The application had no deployment before the run, so a deployment that is left is one of this theme. Only one on an empty collection is removed.
        try {
            foreach ($d in (Get-RunDeployments)) {
                if ($emptyZones -contains [string]$d.CollectionName) { Invoke-GuideSite { Remove-CMApplicationDeployment -InputObject $d -Force -ErrorAction Stop }; "removed the deployment of $Application to $($d.CollectionName)" }
                else { "LEFT: a deployment of $Application to $($d.CollectionName)" }
            }
        }
        catch { "LEFT: the deployments of $Application could not be checked or removed: $($_.Exception.Message)" }
    }
}
