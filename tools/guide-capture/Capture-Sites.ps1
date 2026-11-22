<#
.SYNOPSIS
    The lab run of the per-site data layout (Q-112d): the end-to-end proof with two fictional sites and the guide images of the site switcher.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock, from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <published folder with these scripts> -ProbeArgs "& .\Capture-Sites.ps1 -Run C:\guide\apd-sites"
    The sites are fictional: AB1 (provider cm.ab1.example.com) and CD2 (provider cm.cd2.example.com). The run starts the app on an old single-site profile
    of AB1 in an isolated data folder, and then checks, on screen and on disk:
      1. the first start copies the data of AB1 into sites\AB1, keeps every old file unchanged and shows Site AB1 in the footer;
      2. Add site CD2 in the footer: the footer shows CD2, Settings shows no value of AB1, the ring plan of AB1 is not listed;
      3. the values saved under CD2 do not reach AB1 (the files of sites\AB1 are unchanged);
      4. a switch back to AB1: the values and the ring plan of AB1 return, and nothing of CD2 shows;
      5. a restart opens the last active site (AB1) and migrates nothing again.
    Lines are PASS or FAIL with the reason. The images are <Run>\images\<theme>\<id>.png.
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
$script:Fails = 0

function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { "PASS $Name" } else { $script:Fails++; "FAIL ${Name}: $Detail" }
}

function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }

function Get-Text($El) {
    $p = $null
    if ($El.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) { return [string]$p.Current.Value }
    [string]$El.Current.Name
}

function Wait-Footer([string]$Like, [double]$Seconds = 20) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $t = Get-FooterShown
        if ($t -like $Like) { return $t }
        Start-Sleep -Milliseconds 700
    } while ((Get-Date) -lt $deadline)
    Get-FooterShown
}

function Get-FooterShown {
    # The switcher button names itself "Site switcher. <footer text>".
    $e = Find-El $null 'btnSiteSwitcher' -Timeout 3
    if (-not $e) { return '' }
    ([string]$e.Current.Name) -replace '^Site switcher\.\s*', ''
}

function Hash-Tree([string]$Path) {
    $h = @{}
    if (Test-Path $Path) { Get-ChildItem $Path -Recurse -File -Force | ForEach-Object { $h[$_.FullName] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash } }
    $h
}

function Open-Settings-ConfigMgr {
    $tree = Find-El $null 'tvOptions' -Timeout 2
    if (-not $tree) { $null = Click-Nav 'Settings'; Start-Sleep 3 }
    $item = Need-El 'nav_configmgr' 15
    Real-Click $item; Start-Sleep 2
}

function Open-SiteMenu {
    Real-Click (Need-El 'btnSiteSwitcher')
    Start-Sleep 1
}

function Switch-To([string]$Like) {
    Open-SiteMenu
    $item = Need-Name $Like 'MenuItem' 8
    Real-Click $item
    Start-Sleep 4
}

foreach ($theme in $Themes) {
    $isE2e = ($theme -eq $Themes[0])
    $script:GuideTheme = $theme
    $data = Join-Path $Run "data-$theme"
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $userRoot = Join-Path $data (Join-Path 'Users' $sid)
    $legacy = Join-Path $userRoot 'SRLPackr'
    $staging = New-RunStaging $data
    # The old single-site profile of AB1: a ring plan, a Check ConfigMgr result, and the values in settings.json.
    New-Item -ItemType Directory -Force (Join-Path $userRoot 'Deployments\Rings'), $legacy | Out-Null
    Set-Content -LiteralPath (Join-Path $userRoot 'Deployments\Rings\AB1-Pilot-Rings.json') -Encoding Ascii -Value (@{
        Name = 'AB1-Pilot-Rings'; Description = 'Ring plan of site AB1.'; HoldLaterRings = $false; TimeBasedOn = 'LocalTime'
        Rings = @(@{ Name = 'Pilot'; CollectionID = 'AB100010'; CollectionName = 'Pilot Workstations'; Purpose = 'Required'; AvailableOffsetDays = 0; DeadlineOffsetDays = 2 })
    } | ConvertTo-Json -Depth 5)
    Set-Content -LiteralPath (Join-Path $legacy 'CheckConfigMgrResult.csv') -Encoding Ascii -Value "Application,Status`r`n7-Zip,Current`r`n"
    $before = Hash-Tree $userRoot
    $beforeOld = @{}; foreach ($k in $before.Keys) { $beforeOld[$k] = $before[$k] }

    $settings = @{
        DownloadRoot = $staging; SiteCode = 'AB1'; SiteCodeSaved = $true; ProviderMachine = 'cm.ab1.example.com'
        DiscoverySqlServer = 'sql.ab1.example.com'; FileShareRoot = '\\files.ab1.example.com\content$'
    }
    $null = Start-Apd $exe $data $theme $settings
    (Main-Window).SetFocus()
    try {
        # ---- 1. The first start
        if ($isE2e) {
            $footer = Wait-Footer 'Site AB1*'
            Check 'e2e-01 footer shows AB1 after the first start' ($footer -like 'Site AB1*cm.ab1.example.com') "footer: '$footer'"
            $site1 = Join-Path $data "Users\$sid\sites\AB1"
            Check 'e2e-02 sites\AB1\site.json and migration.json exist' ((Test-Path (Join-Path $site1 'site.json')) -and (Test-Path (Join-Path $site1 'migration.json'))) $site1
            Check 'e2e-03 the ring plan and the result are copied' ((Test-Path (Join-Path $site1 'deployments\rings\AB1-Pilot-Rings.json')) -and (Test-Path (Join-Path $site1 'results\CheckConfigMgrResult.csv'))) 'copy missing'
            $same = $true
            foreach ($f in $beforeOld.Keys) { if (-not (Test-Path $f) -or ((Get-FileHash $f -Algorithm SHA256).Hash -ne $beforeOld[$f] -and $f -notmatch 'settings\.json$')) { $same = $false; "  changed or missing: $f" } }
            Check 'e2e-04 every old file is kept and unchanged (settings.json may be rewritten on close only)' $same ''
            Check 'e2e-05 active-site.json names AB1' ((Get-Content (Join-Path $data "Users\$sid\active-site.json") -Raw -ErrorAction SilentlyContinue) -match '"siteCode":\s*"AB1"') 'active-site.json'
        }
        Step 'core-site-footer' { Shot 'core-site-footer' }

        # ---- The site menu (image) and the Settings values of AB1
        Open-Settings-ConfigMgr
        if ($isE2e) {
            $sql = Get-Text (Need-El 'txtDiscoverySqlServer')
            Check 'e2e-06 Settings shows the SQL Server of AB1' ($sql -eq 'sql.ab1.example.com') "value: '$sql'"
        }

        # ---- 2. Add site CD2
        Open-SiteMenu
        Step 'core-site-switcher-menu' {
            $b = (Need-El 'btnSiteSwitcher').Current.BoundingRectangle
            $scr = Get-ApdScreen
            Shot 'core-site-switcher-menu' @([int]($scr.Width - 760), [int]($scr.Height - 240), 760, 240)
        }
        $add = Need-Name 'Add site...' 'MenuItem' 8
        Real-Click $add
        $null = Wait-El 'txtAddSiteCode' 15
        Set-ElText (Need-El 'txtAddSiteCode') 'CD2'
        Set-ElText (Need-El 'txtAddSiteProvider') 'cm.cd2.example.com'
        Set-ElText (Need-El 'txtAddSiteLabel') 'Lab'
        Start-Sleep 1
        Step 'core-site-add' { Shot 'core-site-add' }
        Real-Click (Need-El 'btnAddSiteAccept')
        $footer2 = Wait-Footer 'Site CD2*'
        if ($isE2e) {
            Check 'e2e-07 footer shows CD2 after Add site' ($footer2 -like 'Site CD2*cm.cd2.example.com') "footer: '$footer2'"
            Start-Sleep 3
            Open-Settings-ConfigMgr
            $sql2 = Get-Text (Need-El 'txtDiscoverySqlServer')
            Check 'e2e-08 under CD2 Settings shows no value of AB1' (($sql2 -ne 'sql.ab1.example.com') -and ($sql2 -notmatch 'ab1')) "value: '$sql2'"
            $ab1Stamp = Hash-Tree (Join-Path $data "Users\$sid\sites\AB1")
            Set-ElText (Need-El 'txtDiscoverySqlServer') 'sql.cd2.example.com'
            Start-Sleep 1
            $save = Find-El $null 'btnSaveSettings' -Timeout 5
            if (-not $save) { $save = Need-Name 'Save Settings' 'Button' 5 }
            Real-Click $save
            Start-Sleep 3
            $cd2Settings = Get-Content (Join-Path $data "Users\$sid\sites\CD2\settings.json") -Raw -ErrorAction SilentlyContinue
            Check 'e2e-09 the value saved under CD2 is in sites\CD2\settings.json' ($cd2Settings -match 'sql\.cd2\.example\.com') "file: $cd2Settings"
            $afterStamp = Hash-Tree (Join-Path $data "Users\$sid\sites\AB1")
            $unchanged = ($afterStamp.Count -eq $ab1Stamp.Count) -and (-not ($ab1Stamp.Keys | Where-Object { $afterStamp[$_] -ne $ab1Stamp[$_] }))
            Check 'e2e-10 the files of sites\AB1 are unchanged by work under CD2' $unchanged 'a file of AB1 changed'

            # The ring plans of CD2 do not list the plan of AB1.
            $null = Click-Nav 'Deployment Helper'; Start-Sleep 10
            Real-Click (Need-El 'tabRings'); Start-Sleep 6
            $grid = Need-El 'dgRingPlans' 20
            $names = @($grid.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name })
            Check 'e2e-11 under CD2 the ring plan list has no plan of AB1' (-not ($names -contains 'AB1-Pilot-Rings')) ('rows: ' + (($names | Select-Object -First 12) -join ', '))
        }

        # ---- 4. Back to AB1
        Switch-To 'AB1 (cm.ab1.example.com)'
        $footer3 = Wait-Footer 'Site AB1*'
        if ($isE2e) {
            Check 'e2e-12 footer shows AB1 after the switch back' ($footer3 -like 'Site AB1*cm.ab1.example.com') "footer: '$footer3'"
            Open-Settings-ConfigMgr
            $sql3 = Get-Text (Need-El 'txtDiscoverySqlServer')
            Check 'e2e-13 the values of AB1 return and nothing of CD2 shows' ($sql3 -eq 'sql.ab1.example.com') "value: '$sql3'"
            $null = Click-Nav 'Deployment Helper'; Start-Sleep 8
            Real-Click (Need-El 'tabRings'); Start-Sleep 5
            $grid2 = Need-El 'dgRingPlans' 20
            $names2 = @($grid2.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name })
            Check 'e2e-14 the ring plan of AB1 is listed again' ($names2 -contains 'AB1-Pilot-Rings') ('rows: ' + (($names2 | Select-Object -First 12) -join ', '))
            # The last switch before the close is to CD2; the restart must open CD2 although settings.json still names AB1.
            Switch-To 'CD2 - Lab (cm.cd2.example.com)'
            Check 'e2e-14b footer shows CD2 before the close' ((Wait-Footer 'Site CD2*') -like 'Site CD2*') 'switch to CD2 failed'
        }
    }
    finally { Stop-Apd }

    if ($isE2e) {
        # ---- 5. A restart opens the last active site and migrates nothing again
        $mig = Join-Path $data "Users\$sid\sites\AB1\migration.json"
        $migStamp = (Get-FileHash $mig -Algorithm SHA256).Hash
        $null = Start-Apd $exe $data $theme @{ SiteCode = 'AB1'; SiteCodeSaved = $true; ProviderMachine = 'cm.ab1.example.com' }
        try {
            $footer4 = Wait-Footer 'Site *'
            Check 'e2e-15 a restart opens the last active site (active-site.json wins over settings.json)' ($footer4 -like 'Site CD2*') "footer: '$footer4'"
            Check 'e2e-16 the restart did not migrate again (migration.json unchanged)' ((Get-FileHash $mig -Algorithm SHA256).Hash -eq $migStamp) 'migration.json changed'
            $secretHits = @(Get-ChildItem (Join-Path $data "Users\$sid\sites") -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -ne '.db' } | Select-String -Pattern 'Protected"|ClientSecret|ApiKey|Token"' -ErrorAction SilentlyContinue)
            Check 'e2e-17 no secret or token key in any file under sites' ($secretHits.Count -eq 0) ('hits: ' + (($secretHits | Select-Object -First 3 | ForEach-Object { $_.Path }) -join ', '))
        }
        finally { Stop-Apd }
    }
}
"E2E-FAILS=$script:Fails"
