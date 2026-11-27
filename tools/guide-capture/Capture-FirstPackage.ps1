<#
.SYNOPSIS
    The lab capture run of the first package and of the WSUS views (decision D-21): Stage and Publish All to ConfigMgr of one application, the grid after Check ConfigMgr, Publish All to WSUS,
    the WSUS Updates window and the Catalog Import window. The real app is driven with UI Automation, one theme after the other.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-InSession.ps1 takes it), from the folder of the published app.
    The images are <Run>\images\<theme>\<id>.png. The Stage goes to the staging folder of the run.
    The application is the guide definition zzapd-gsudo (title "ZZAPD gsudo (x64) - <version>"). What the run writes to the lab: that application on the
    ConfigMgr site, its content on the file share, its update on the WSUS server, and the catalog updates of -ImportIds.
    Before each theme the run reads the ZZAPD applications of the site and stops when one starts with -Search: it never starts beside an application that it did
    not make. It removes what PublishCleanup.ps1 can prove it created: the application, its new content folders and the WSUS update that it published.
    It removes no imported catalog update: an import stays on the WSUS server. The image of the Catalog Import window is saved only when the result
    names every id and none failed. With -CatalogImportOnly the run makes only that image: it starts the app with WSUS in use, publishes nothing and does
    not read or write the ConfigMgr site.
    The WSUS server must hold no unexpired update of any version of the application before the run: a publish supersedes the older updates of it.
    Every publish review is confirmed only when it lists exactly -ApplicationName and the one system of that start; else the run selects Back. The folder needs WsusAdapter.ps1 (src\ApplicationPackagerEngine\Services\Wsus) beside this script.
    The computer needs the WSUS console and the lab site needs a WSUS signing certificate. A step that fails says FAIL for its image and the run goes on.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [string[]]$Themes = @('Light', 'Dark'),
    [string]$AppFolder = $PSScriptRoot,
    [string]$Search = 'ZZAPD gsudo',
    # The name of the application in the Application column of the publish review.
    [string]$ApplicationName = 'ZZAPD gsudo (x64)',
    [string]$FileShareRoot = '\\cm01.contoso.com\ContentShare$',
    [string]$Server = 'cm01.contoso.com',
    [string]$SiteCode = 'MCM',
    [string[]]$ImportIds = @(),
    # Only the image of the Catalog Import window: no Stage and no publish.
    [switch]$CatalogImportOnly
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
. (Join-Path $PSScriptRoot 'PublishCleanup.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'

function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Status-Text($w) { $e = Find-El $w 'StatusLabel' -Timeout 2; if ($e) { $e.Current.Name } else { '' } }
function Wait-Idle($w, [int]$Seconds) {
    Start-Sleep 4
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline -and (Test-ApdBusy $w)) { Start-Sleep 2 }
}
function Check-Row($w, [string]$Name) {
    Set-ElText (Find-El $w 'txtAppPackagerSearch') $Name
    Start-Sleep 2
    $grid = Find-El $w 'PackagerGrid'
    $box = Get-ApdRowBoxes $grid | Select-Object -First 1
    if ($box -and $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { Real-Click $box }
}

# The Catalog Import window of the WSUS Updates window, which is open already.
$catalogImport = {
    if (-not $ImportIds) { throw 'no update ids given (-ImportIds)' }
    # The App Packager page has a button with the same id: take the one of the WSUS Updates window.
    $wsus = Need-El 'gridUpdates'
    while ($wsus -and $wsus.Current.ControlType -ne [System.Windows.Automation.ControlType]::Window) { $wsus = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($wsus) }
    if (-not $wsus) { throw 'the WSUS Updates window was not found' }
    Real-Click (Find-El $wsus 'btnCatalog' -Timeout 5)
    $box = Need-El 'txtInput'
    Set-ElText $box ($ImportIds -join "`r`n")
    Start-Sleep 1
    Real-Click (Need-El 'btnImport')
    $deadline = (Get-Date).AddSeconds(300)
    do { Start-Sleep 4; $s = (Need-El 'txtStatus').Current.Name } while ((Get-Date) -lt $deadline -and $s -notmatch 'mport(ed|ing finished)|done|Done|FAILED|failed')
    "import status: $s"
    $lines = @((Get-ElValue (Need-El 'txtResults')) -split "`r?`n" | Where-Object { $_.Trim() })
    $lines | ForEach-Object { "import result: $_" }
    foreach ($id in $ImportIds) {
        $mine = @($lines | Where-Object { $_ -like "*$id*" })
        if (-not $mine -or ($mine -match 'FAILED')) { throw "The import of $id has no result or failed. No image is saved." }
    }
    Shot 'core-wsus-catalog-import'
    $c = Find-El $null 'btnClose' -Timeout 3
    if ($c) { Real-Click $c }
}

if ($CatalogImportOnly) {
    foreach ($theme in $Themes) {
        $data = Start-RunApp $exe $theme @{ Systems = @{ ConfigMgr = $false; Intune = $false; Wsus = $true }; Wsus = @{ ServerName = $Server; PortNumber = 8530 } }
        try {
            Step 'core-wsus-catalog-import' {
                $null = Click-Nav 'WSUS Updates'
                if (-not (Wait-El 'gridUpdates' 60)) { throw 'no WSUS Updates window' }
                Start-Sleep 6
                & $catalogImport
            }
        }
        finally { Stop-Apd }
        "LEFT on WSUS: this run removes no imported catalog update. Asked for import: $($ImportIds -join ', ')"
    }
    return
}

Connect-GuideSite $SiteCode $Server

foreach ($theme in $Themes) {
    $before = @(Get-GuideApplications)
    $same = @($before | Where-Object { $_.LocalizedDisplayName -like "$Search*" })
    if ($same) {
        $same | ForEach-Object { "on the site before the run: $(Format-GuideApp $_)" }
        throw "The site already holds $($same.Count) application(s) whose title starts with '$Search'. The run does not start and removes nothing."
    }
    $beforeModels = @($before | ForEach-Object { [string]$_.ModelName })
    $beforeFolders = @(Get-GuideShareFolders $FileShareRoot)
    $wsusData = $null; $siteDone = $false; $data = $null
    # Publish All publishes to every system in use, so the ConfigMgr part and the WSUS part run in two starts of the app with one system each.
    $data = Start-RunApp $exe $theme @{ FileShareRoot = $FileShareRoot; Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false } } @() @('zzapd-gsudo')
    try {
        $w = Main-Window
        Step 'start-first-package-end-to-end' {
            $null = Click-Nav 'App Packager'; Start-Sleep 5
            Check-Row $w $Search
            Assert-ApdSelection $w 1
            Open-ApdMenuItem $w 'btnCheckMenu' 'miCheckLatest'
            Wait-Idle $w 240
            Real-Click (Find-El $w 'btnStage' -Timeout 5)
            Wait-Idle $w 420
            # A dialog after a Stage is not expected: it is not clicked, and the step fails.
            Resolve-ApdContentDialog $w @()
            "status after Stage: $(Status-Text $w)"
            Assert-ApdSelection $w 1
            Real-Click (Need-El 'btnPublishAll')
            Complete-ApdPublish $w 600 -Applications $ApplicationName -Systems 'ConfigMgr' -Comment 'Guide capture'
            "status after Publish: $(Status-Text $w)"
            Check-Row $w $Search
            Open-ApdMenuItem $w 'btnCheckMenu' 'miCheckConfigMgr'
            Wait-Idle $w 240
            Set-ElText (Find-El $w 'txtAppPackagerSearch') $Search
            Start-Sleep 2
            Shot 'start-first-package-end-to-end'
        }
        Stop-Apd
        # The cleanup of the ConfigMgr publish runs now: the WSUS part empties the data folder of the theme, which holds the log.
        Complete-GuideSiteCleanup $data $beforeModels $FileShareRoot $beforeFolders
        $siteDone = $true
        $data = Start-RunApp $exe $theme @{ FileShareRoot = $FileShareRoot; Systems = @{ ConfigMgr = $false; Intune = $false; Wsus = $true }; Wsus = @{ ServerName = $Server; PortNumber = 8530 } } @() @('zzapd-gsudo')
        $wsusData = $data
        $w = Main-Window
        Step 'core-publish-wsus-row' {
            $null = Click-Nav 'App Packager'; Start-Sleep 5
            Check-Row $w $Search
            Open-ApdMenuItem $w 'btnCheckMenu' 'miCheckLatest'
            Wait-Idle $w 240
            Assert-ApdSelection $w 1
            Real-Click (Need-El 'btnPublishAll')
            Complete-ApdPublish $w 600 -Applications $ApplicationName -Systems 'WSUS'
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
        Step 'core-wsus-catalog-import' { & $catalogImport }
    }
    finally {
        Stop-Apd
        # The cleanup runs also when the theme stops early. Each part removes nothing that it cannot prove, and says what is left.
        try {
            if (-not $siteDone -and $data) { Complete-GuideSiteCleanup $data $beforeModels $FileShareRoot $beforeFolders }
        } catch { "cleanup of the site failed, nothing more was removed: $($_.Exception.Message)" }
        if ($wsusData) { try { Remove-GuideRunUpdates $wsusData $Server 8530 (Join-Path $PSScriptRoot 'WsusAdapter.ps1') } catch { "cleanup of WSUS failed: $($_.Exception.Message)" } }
        if ($ImportIds) { "LEFT on WSUS: this run removes no imported catalog update. Asked for import: $($ImportIds -join ', ')" }
    }
}
