<#
.SYNOPSIS
    The lab capture run of the first package and of the WSUS views (decision D-21): Stage and Publish All to ConfigMgr of one application, the grid after Check ConfigMgr, Publish All to WSUS,
    the WSUS Updates window and the Catalog Import window. The real app is driven with UI Automation, one theme after the other.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock (Run-VisibleProbe.ps1 takes it), from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <published folder with these scripts> -ProbeArgs "& .\Capture-FirstPackage.ps1 -Run C:\guide\apd-pending\first"
    The images are <Run>\images\<theme>\<id>.png. The Stage goes to the staging folder of the run.
    What the run writes to the lab: the application of -Application on the ConfigMgr site, its content on the file share, its update on the WSUS server,
    and the catalog updates of -ImportIds.
    Before each theme the run reads the applications whose name starts with -SiteName and stops when there is one: it never starts beside an application
    that it did not make. After the theme it removes one application only: the one with the CI_ID that the log of this theme says the publish created,
    with the exact title and version of that publish. When it cannot prove that, it removes nothing and prints what is on the site.
    It removes nothing from the file share and nothing from the WSUS server: it prints the update id that the publish reported, for a person to remove.
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

function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Status-Text($w) { $e = Find-El $w 'StatusLabel' -Timeout 2; if ($e) { $e.Current.Name } else { '' } }
function Wait-Idle($w, [int]$Seconds) {
    Start-Sleep 4
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline -and (Test-ApdBusy $w)) { Start-Sleep 2 }
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
    $box = Get-ApdRowBoxes $grid | Select-Object -First 1
    if ($box -and $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { Real-Click $box }
}

if ([string]::IsNullOrWhiteSpace($SiteName) -or $SiteName -match '[\*\?\[\]]') { throw '-SiteName must be a plain, non-empty name.' }

# The applications on the site whose name starts with -SiteName, read with the console cmdlets. Throws when the site cannot be read.
function Get-SiteMatches {
    $ui = $env:SMS_ADMIN_UI_PATH
    if (-not $ui) { throw 'The ConfigMgr console is not installed on this computer.' }
    $module = @((Join-Path $ui 'ConfigurationManager.psd1'), (Join-Path (Split-Path $ui -Parent) 'ConfigurationManager.psd1')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $module) { throw "No ConfigurationManager.psd1 beside $ui." }
    Import-Module $module -ErrorAction Stop
    if (-not (Get-PSDrive -Name $SiteCode -ErrorAction SilentlyContinue)) { $null = New-PSDrive -Name $SiteCode -PSProvider CMSite -Root $Server -Scope Script -ErrorAction Stop }
    Push-Location "${SiteCode}:"
    try { @(Get-CMApplication -Fast -Name "$SiteName*" -ErrorAction Stop) }
    finally { Pop-Location }
}

function Format-App($App) { "CI_ID $($App.CI_ID) '$($App.LocalizedDisplayName)' version '$($App.SoftwareVersion)'" }

function Get-RunLog([string]$DataRoot) {
    @(Get-ChildItem -LiteralPath $DataRoot -Recurse -File -Filter 'packr.log' -ErrorAction SilentlyContinue | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
}

# What the log and the stage of a theme say about the ConfigMgr publish. It is read before the second start of the app empties the data folder.
function Get-PublishEvidence([string]$DataRoot) {
    $log = Get-RunLog $DataRoot
    [pscustomobject]@{
        Created  = ($log -match 'Creating application\.\.\.') -and ($log -match 'Application created successfully') -and ($log -notmatch 'Application already exists')
        CiIds    = @([regex]::Matches($log, 'Final CI_ID: (\d+)') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
        Titles   = @([regex]::Matches($log, '(?m)  Packaged: (.+?)\s*$') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
        Versions = @(Get-ChildItem -LiteralPath $DataRoot -Recurse -File -Filter 'stage-manifest.json' -ErrorAction SilentlyContinue | ForEach-Object { [string](Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).SoftwareVersion } | Select-Object -Unique)
    }
}

# Removes the application that this theme created, and only that one. $Before holds the CI_IDs that matched before the theme.
function Remove-RunApplication($Evidence, [int[]]$Before) {
    $now = @(Get-SiteMatches)
    if (-not $now) { 'site: no application matches, nothing to remove'; return }
    $mine = @()
    if ($Evidence -and $Evidence.Created -and $Evidence.CiIds.Count -eq 1 -and $Evidence.Titles.Count -eq 1 -and $Evidence.Versions.Count -eq 1) {
        $mine = @($now | Where-Object { [string]$_.CI_ID -eq $Evidence.CiIds[0] -and $Before -notcontains [int]$_.CI_ID -and $_.LocalizedDisplayName -ceq $Evidence.Titles[0] -and [string]$_.SoftwareVersion -ceq $Evidence.Versions[0] })
    }
    if ($mine.Count -eq 1) {
        Push-Location "${SiteCode}:"
        try {
            $app = Get-CMApplication -Id ([int]$mine[0].CI_ID) -ErrorAction Stop
            if ($app -and $app.LocalizedDisplayName -ceq $Evidence.Titles[0]) { Remove-CMApplication -InputObject $app -Force -ErrorAction Stop; "site: removed $(Format-App $mine[0])" }
            else { "LEFT on the site: $(Format-App $mine[0]) (it could not be read again by its CI_ID)" }
        }
        finally { Pop-Location }
    }
    else { 'site: nothing removed, because the run cannot prove which application it created' }
    foreach ($a in $now) { if ($mine.Count -ne 1 -or $a.CI_ID -ne $mine[0].CI_ID) { "LEFT on the site: $(Format-App $a)" } }
}

# The WSUS update ids that the publish of this theme reported. The run removes none of them: it prints them for a person.
function Write-WsusLeft([string]$DataRoot) {
    $found = @([regex]::Matches((Get-RunLog $DataRoot), '(published|already on the server) ([0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12});') |
        ForEach-Object { '{0}|{1}' -f $_.Groups[2].Value, $_.Groups[1].Value } | Select-Object -Unique)
    if (-not $found) { 'LEFT on WSUS: the log names no update id; if the publish reached the server, check WSUS Updates by hand'; return }
    foreach ($entry in $found) {
        $id, $what = $entry -split '\|'
        if ($what -eq 'published') { "LEFT on WSUS: update $id, published by this run. Remove it in WSUS Updates." }
        else { "LEFT on WSUS: update $id was already on the server. This run did not create it: leave it." }
    }
}

foreach ($theme in $Themes) {
    $before = @(Get-SiteMatches)
    if ($before) {
        $before | ForEach-Object { "on the site before the run: $(Format-App $_)" }
        throw "The site already holds $($before.Count) application(s) whose name starts with '$SiteName'. The run does not start and removes nothing."
    }
    $beforeIds = [int[]]@($before | ForEach-Object { [int]$_.CI_ID })
    $evidence = $null; $wsusData = $null
    # Publish All publishes to every system in use, so the ConfigMgr part and the WSUS part run in two starts of the app with one system each.
    $data = Start-RunApp $exe $theme @{ FileShareRoot = $FileShareRoot; Systems = @{ ConfigMgr = $true; Intune = $false; Wsus = $false } }
    try {
        $w = Main-Window
        Step 'start-first-package-end-to-end' {
            $null = Click-Nav 'App Packager'; Start-Sleep 5
            Check-Row $w $Search
            Open-ApdMenuItem $w 'btnCheckMenu' 'miCheckLatest'
            Wait-Idle $w 240
            Real-Click (Find-El $w 'btnStage' -Timeout 5)
            Wait-Idle $w 420
            Answer-Dialogs $w 300
            "status after Stage: $(Status-Text $w)"
            Real-Click (Need-El 'btnPublishAll')
            Complete-ApdPublish $w 600
            "status after Publish: $(Status-Text $w)"
            Check-Row $w $Search
            Open-ApdMenuItem $w 'btnCheckMenu' 'miCheckConfigMgr'
            Wait-Idle $w 240
            Set-ElText (Find-El $w 'txtAppPackagerSearch') $Search
            Start-Sleep 2
            Shot 'start-first-package-end-to-end'
        }
        # The same data folder is not kept: the WSUS publish stages the application again. So the facts of the ConfigMgr publish are read now.
        $evidence = Get-PublishEvidence $data
        Stop-Apd
        $data = Start-RunApp $exe $theme @{ FileShareRoot = $FileShareRoot; Systems = @{ ConfigMgr = $false; Intune = $false; Wsus = $true }; Wsus = @{ ServerName = $Server; PortNumber = 8530 } }
        $wsusData = $data
        $w = Main-Window
        Step 'core-publish-wsus-row' {
            $null = Click-Nav 'App Packager'; Start-Sleep 5
            Check-Row $w $Search
            Open-ApdMenuItem $w 'btnCheckMenu' 'miCheckLatest'
            Wait-Idle $w 240
            Real-Click (Need-El 'btnPublishAll')
            Complete-ApdPublish $w 600
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
            Shot 'core-wsus-catalog-import'
            $c = Find-El $null 'btnClose' -Timeout 3
            if ($c) { Real-Click $c }
        }
    }
    finally {
        Stop-Apd
        # The cleanup runs also when the theme stops early. Each part removes nothing that it cannot prove, and says what is left.
        try {
            if (-not $evidence -and -not $wsusData -and $data) { $evidence = Get-PublishEvidence $data }
            Remove-RunApplication $evidence $beforeIds
        } catch { "cleanup of the site failed, nothing more was removed: $($_.Exception.Message)" }
        if ($wsusData) { Write-WsusLeft $wsusData }
        if ($ImportIds) { "LEFT on WSUS: this run removes no imported catalog update. Asked for import: $($ImportIds -join ', ')" }
        foreach ($dir in @(Get-ChildItem $FileShareRoot -Directory -Recurse -Depth 3 -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "$SiteName*" })) { "share folder left for the caller: $($dir.FullName)" }
    }
}
