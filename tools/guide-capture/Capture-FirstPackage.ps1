<#
.SYNOPSIS
    The lab capture run of the first package and of the WSUS views (decision D-21): Stage and Publish to ConfigMgr of one application, the grid after Check ConfigMgr, Publish to WSUS,
    the WSUS Updates window and the Catalog Import window. The real app is driven with UI Automation, one theme after the other.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <published folder with these scripts> -ProbeArgs "& .\Capture-FirstPackage.ps1 -Run C:\guide\apd-pending\first"
    The images are <Run>\images\<theme>\<id>.png. The Stage goes to the staging folder of the run.
    What the run writes to the lab, and removes again at the end of each theme: the application of the application name on the ConfigMgr site (with its content folder on the file share) and
    its update on the WSUS server (the Remove button of the WSUS Updates window). The application is one that the site does not hold (PuTTY).
    The computer needs the WSUS administration API (the lab copies it to the Update Services folder) and the lab site needs a WSUS signing certificate.
    A step that fails says FAIL for its image and the run goes on.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot,
    [string]$Application = 'PuTTY (x64)',
    [string]$Search = 'PuTTY',
    [string]$SiteName = 'PuTTY',
    [string]$FileShareRoot = '\\cm01.contoso.com\ContentShare$',
    [string]$Server = 'cm01.contoso.com',
    [string]$SiteCode = 'MCM',
    [string[]]$ImportIds = @()
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$ctCheck = [System.Windows.Automation.ControlType]::CheckBox

function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Status-Text($w) { $e = Find-El $w 'StatusLabel' -Timeout 2; if ($e) { $e.Current.Name } else { '' } }
function Wait-Idle($w, [int]$Seconds) {
    Start-Sleep 4
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline -and (Find-El $w 'btnCancelOperation' -Timeout 1)) { Start-Sleep 2 }
}
function Other-Windows { @(Get-ApdWindows | Where-Object { $_.Current.Name -notmatch '^App Packager' -and $_.Current.Name -ne 'Workbench' }) }
function Answer-Dialogs($w, [int]$Seconds) {
    foreach ($i in 1..3) {
        $dlg = Other-Windows | Select-Object -First 1
        if (-not $dlg) { break }
        "dialog: $($dlg.Current.Name)"
        foreach ($n in 'Continue', 'OK', 'Yes') { $b = Find-El $dlg -Name $n -ControlType 'Button' -Timeout 2; if ($b) { Real-Click $b; break } }
        Wait-Idle $w $Seconds
    }
}
function Check-Row($w, [string]$Name) {
    Set-ElText (Find-El $w 'txtAppPackagerSearch') $Name
    Start-Sleep 2
    $grid = Find-El $w 'PackagerGrid'
    $box = $grid.FindAll($script:TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($script:AE::ControlTypeProperty), $ctCheck)) |
        Where-Object { $_.Current.AutomationId -ne 'SelectAllCheckBox' } | Select-Object -First 1
    if ($box -and $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { Real-Click $box }
}

# Removes what a theme made on the site: the application on the ConfigMgr site (named like the site name, with the version after it) with the console cmdlets.
function Remove-SiteApplication {
    $ui = $env:SMS_ADMIN_UI_PATH
    if (-not $ui) { throw 'The ConfigMgr console is not installed on this computer.' }
    Import-Module (Join-Path $ui 'ConfigurationManager.psd1') -ErrorAction Stop
    Push-Location "${SiteCode}:"
    try {
        foreach ($a in @(Get-CMApplication -Name "$SiteName*" | Where-Object { $_.LocalizedDisplayName -like "$Application*" -or $_.LocalizedDisplayName -eq $SiteName })) {
            Remove-CMApplication -InputObject $a -Force
            "site: removed application $($a.LocalizedDisplayName)"
        }
    }
    finally { Pop-Location }
    foreach ($dir in @(Get-ChildItem $FileShareRoot -Directory -Recurse -Depth 3 -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'PuTTY*' })) { "share folder left for the caller: $($dir.FullName)" }
}

foreach ($theme in $Themes) {
    $data = Start-RunApp $exe $theme @{ FileShareRoot = $FileShareRoot; Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $true }; Wsus = @{ ServerName = $Server; PortNumber = 8530 } }
    try {
        $w = Main-Window
        Step 'start-first-package-end-to-end' {
            $null = Click-Nav 'App Packager'; Start-Sleep 5
            Check-Row $w $Search
            Open-ApdMenuItem $w 'btnCheckMenu' 'miCheckLatest'
            Wait-Idle $w 240
            Real-Click (Find-El $w -Name 'Stage' -ControlType 'Button' -Timeout 5)
            Wait-Idle $w 420
            Answer-Dialogs $w 300
            "status after Stage: $(Status-Text $w)"
            Set-ElText (Find-El $w 'txtAppPackagerComment') 'Guide capture'
            Real-Click (Need-El 'btnPublishAll')
            Wait-Idle $w 420
            Answer-Dialogs $w 420
            "status after Publish: $(Status-Text $w)"
            Check-Row $w $Search
            Open-ApdMenuItem $w 'btnCheckMenu' 'miCheckConfigMgr'
            Wait-Idle $w 240
            Set-ElText (Find-El $w 'txtAppPackagerSearch') $Search
            Start-Sleep 2
            Shot 'start-first-package-end-to-end'
        }
        Step 'core-publish-wsus-row' {
            Check-Row $w $Search
            Real-Click (Need-El 'btnPublishWsus')
            Wait-Idle $w 420
            Answer-Dialogs $w 420
            $deadline = (Get-Date).AddSeconds(240)
            do { Start-Sleep 4; $st = (Get-ElList (Find-El $w 'PackagerGrid') 200) -join ' ' } while ((Get-Date) -lt $deadline -and $st -notmatch 'Published|Already on the server|Publish error|not supported')
            "status after WSUS publish: $(Status-Text $w)"
            Set-ElText (Find-El $w 'txtAppPackagerSearch') $Search
            Start-Sleep 2
            Shot 'core-publish-wsus-row'
        }
        Step 'core-wsus-updates-window' {
            $null = Click-Nav 'WSUS Updates'
            $grid = Wait-El 'gridUpdates' 60
            if (-not $grid) { throw 'no WSUS Updates window' }
            Start-Sleep 6
            "wsus summary: $((Need-El 'txtSummary').Current.Name)"
            Shot 'core-wsus-updates-window'
        }
        Step 'core-wsus-catalog-import' {
            if (-not $ImportIds) { throw 'no update ids given (-ImportIds)' }
            Real-Click (Need-El 'btnCatalog')
            $box = Need-El 'txtInput'
            Set-ElText $box ($ImportIds -join "`r`n")
            Start-Sleep 1
            Real-Click (Need-El 'btnImport')
            $deadline = (Get-Date).AddSeconds(300)
            do { Start-Sleep 4; $s = (Need-El 'txtStatus').Current.Name } while ((Get-Date) -lt $deadline -and $s -notmatch 'mport(ed|ing finished)|done|Done|FAILED|failed')
            "import status: $s"
            Shot 'core-wsus-catalog-import'
            $c = Find-El $null 'btnClose' -Timeout 3
            if ($c) { Real-Click $c }
        }
        Step 'cleanup-wsus' {
            # Remove the update of this run: select its row in the WSUS Updates window and use Remove.
            $null = Click-Nav 'WSUS Updates'
            $grid = Wait-El 'gridUpdates' 60
            if (-not $grid) { throw 'no WSUS Updates window' }
            Start-Sleep 5
            $row = $null
            foreach ($e in $grid.FindAll($script:TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) { if ($e.Current.Name -like "*$Search*" -and -not $e.Current.IsOffscreen) { $row = $e; break } }
            if ($row) {
                Click-El $row; Start-Sleep 1
                Real-Click (Need-El 'btnRemove'); Start-Sleep 2
                $dlg = Other-Windows | Where-Object { $_.Current.Name -like 'Remove*' } | Select-Object -First 1
                if ($dlg) { $y = Find-El $dlg -Name 'Yes' -ControlType 'Button' -Timeout 3; if ($y) { Real-Click $y } }
                Start-Sleep 6
                "wsus summary after remove: $((Need-El 'txtSummary').Current.Name)"
            } else { 'cleanup-wsus: no row for the application' }
            $c = Find-El $null 'btnClose' -Timeout 3
            if ($c) { Real-Click $c }
        }
    }
    finally { Stop-Apd }
    try { Remove-SiteApplication } catch { "cleanup of the site failed: $($_.Exception.Message)" }
}
