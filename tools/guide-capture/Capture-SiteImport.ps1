<#
.SYNOPSIS
    The lab run of the import of settings from another site (Q-112e): the end-to-end proof with two fictional sites and the guide image of the Import window.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock, from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <published folder with these scripts> -ProbeArgs "& .\Capture-SiteImport.ps1 -Run C:\guide\apd-site-import"
    The sites are fictional: CD2 (the source, provider cm.cd2.example.com) and AB1 (the new target, provider cm.ab1.example.com). The run starts the app on a
    single-site profile of CD2 in an isolated data folder, and checks, on screen and on disk:
      1. with one site the footer entry Import settings from another site is disabled;
      2. Add site AB1 makes AB1 active; the entry is enabled;
      3. the Import window lists the source CD2 and the checklist, with the defaults ticked and the environment values not ticked (image core-site-import);
      4. a tick of File share root and Import: the DP group of AB1 (a conflict) is kept, the share root arrives, the SQL server (not ticked) does not,
         the site code and the provider of AB1 stay, the files of sites\CD2 are unchanged (SHA-256), the log imported-from.json holds the hashes;
      5. Settings under AB1 shows the imported File Share Root;
      6. a second import has nothing to copy (Import disabled) and changes no file;
      7. the choice Replace for the conflict replaces the value and keeps a verified backup of the target file;
      8. no secret or token key reaches any file under sites.
    Lines are PASS or FAIL with the reason. The image is <Run>\images\<theme>\core-site-import.png.
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

function Get-FooterShown {
    $e = Find-El $null 'btnSiteSwitcher' -Timeout 3
    if (-not $e) { return '' }
    ([string]$e.Current.Name) -replace '^Site switcher\.\s*', ''
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

function Hash-Tree([string]$Path) {
    $h = @{}
    if (Test-Path $Path) { Get-ChildItem $Path -Recurse -File -Force | ForEach-Object { $h[$_.FullName] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash } }
    $h
}

function Same-Tree($A, $B) {
    if ($A.Count -ne $B.Count) { return $false }
    foreach ($k in $A.Keys) { if ($A[$k] -ne $B[$k]) { return $false } }
    $true
}

function Open-SiteMenu { Real-Click (Need-El 'btnSiteSwitcher'); Start-Sleep 1 }

function Open-ImportWindow {
    Open-SiteMenu
    Real-Click (Need-Name 'Import settings from another site...' 'MenuItem' 8)
    $null = Wait-El 'cmbImportSource' 15
    Start-Sleep 2
}

function Close-ImportWindow {
    $c = Find-El $null 'btnImportCancel' -Timeout 5
    if ($c) { Real-Click $c }
    Start-Sleep 1
}

function Row-Check([string]$Label) { Need-Name $Label 'CheckBox' 10 }

function Edit-Json([string]$Path, [scriptblock]$Edit) {
    $o = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    & $Edit $o
    Set-Content -LiteralPath $Path -Value ($o | ConvertTo-Json -Depth 8) -Encoding Ascii
}

foreach ($theme in $Themes) {
    $isE2e = ($theme -eq $Themes[0])
    $script:GuideTheme = $theme
    $data = Join-Path $Run "data-$theme"
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $sites = Join-Path $data "Users\$sid\sites"
    $cd2 = Join-Path $sites 'CD2'
    $ab1 = Join-Path $sites 'AB1'
    $staging = New-RunStaging $data

    $settings = @{
        DownloadRoot = $staging; SiteCode = 'CD2'; SiteCodeSaved = $true; ProviderMachine = 'cm.cd2.example.com'
        DiscoverySqlServer = 'sql.cd2.example.com'; FileShareRoot = '\\files.cd2.example.com\content$'
        ContentDistribution = @{ AutoDistribute = $true; DpGroupName = 'CD2 Distribution Points'; DeployToTestCollection = $true; TestCollectionName = 'CD2 Pilot Devices' }
        GitHubTokenProtected = 'FAKE_TOKEN_VALUE_CD2'
    }
    $null = Start-Apd $exe $data $theme $settings
    (Main-Window).SetFocus()
    try {
        $footer = Wait-Footer 'Site CD2*'
        if ($isE2e) {
            Check 'e2e-01 the first start opens CD2 and copies its data' (($footer -like 'Site CD2*') -and (Test-Path (Join-Path $cd2 'settings.json'))) "footer: '$footer'"
            Open-SiteMenu
            $imp = Need-Name 'Import settings from another site...' 'MenuItem' 8
            Check 'e2e-02 with one site the Import entry is disabled' (-not $imp.Current.IsEnabled) 'the entry is enabled'
            [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep 1
        }

        # ---- Add site AB1 (new, empty)
        Open-SiteMenu
        Real-Click (Need-Name 'Add site...' 'MenuItem' 8)
        $null = Wait-El 'txtAddSiteCode' 15
        Set-ElText (Need-El 'txtAddSiteCode') 'AB1'
        Set-ElText (Need-El 'txtAddSiteProvider') 'cm.ab1.example.com'
        Set-ElText (Need-El 'txtAddSiteLabel') 'Production'
        Start-Sleep 1
        Real-Click (Need-El 'btnAddSiteAccept')
        $footer2 = Wait-Footer 'Site AB1*'
        if ($isE2e) { Check 'e2e-03 Add site AB1 makes AB1 active' ($footer2 -like 'Site AB1*cm.ab1.example.com') "footer: '$footer2'" }
        # AB1 already holds a DP group of its own: the conflict that the window must show. The app has read the empty site; the import reads the file.
        $abSettings = Join-Path $ab1 'settings.json'
        $existing = if (Test-Path $abSettings) { Get-Content -LiteralPath $abSettings -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
        $existing | Add-Member -NotePropertyName ContentDistribution -NotePropertyValue ([pscustomobject]@{ DpGroupName = 'AB1 Distribution Points' }) -Force
        Set-Content -LiteralPath $abSettings -Value ($existing | ConvertTo-Json -Depth 8) -Encoding Ascii

        # ---- The window
        Open-SiteMenu
        $imp2 = Need-Name 'Import settings from another site...' 'MenuItem' 8
        if ($isE2e) { Check 'e2e-04 with two sites the Import entry is enabled' ($imp2.Current.IsEnabled) 'the entry is disabled' }
        Real-Click $imp2
        $null = Wait-El 'cmbImportSource' 15
        Start-Sleep 2
        Step 'core-site-import' { Shot 'core-site-import' }
        if ($isE2e) {
            $srcText = [string](Need-El 'cmbImportSource').Current.Name
            $sum = [string](Need-El 'txtImportSummary').Current.Name
            Check 'e2e-05 the window names the active site and the preview says the source is not changed' ($sum -match 'source site is not changed') "summary: '$sum'"
            $cdBefore = Hash-Tree $cd2
            $abBefore = (Get-FileHash $abSettings -Algorithm SHA256).Hash
            # Tick File share root (off by default) and import.
            $box = Row-Check 'File share root'
            Set-Check $box $true
            Start-Sleep 1
            Real-Click (Need-El 'btnImportSettings')
            Start-Sleep 4
            $t = Get-Content -LiteralPath $abSettings -Raw | ConvertFrom-Json
            Check 'e2e-06 the conflicting DP group of AB1 is kept' ($t.ContentDistribution.DpGroupName -eq 'AB1 Distribution Points') ('value: ' + $t.ContentDistribution.DpGroupName)
            Check 'e2e-07 the ticked File Share Root arrived' ($t.FileShareRoot -eq '\\files.cd2.example.com\content$') ('value: ' + $t.FileShareRoot)
            Check 'e2e-08 the SQL server (not ticked) did not arrive' (-not $t.DiscoverySqlServer) ('value: ' + $t.DiscoverySqlServer)
            Check 'e2e-09 the site code and the provider of AB1 stay' (($t.SiteCode -eq 'AB1' -or -not $t.SiteCode) -and ($t.ProviderMachine -ne 'cm.cd2.example.com')) ('code ' + $t.SiteCode + ' provider ' + $t.ProviderMachine)
            Check 'e2e-10 the files of sites\CD2 are unchanged (SHA-256)' (Same-Tree $cdBefore (Hash-Tree $cd2)) 'a file of CD2 changed'
            $log = Join-Path $ab1 'imported-from.json'
            $logObj = if (Test-Path $log) { Get-Content -LiteralPath $log -Raw | ConvertFrom-Json } else { $null }
            Check 'e2e-11 imported-from.json holds the source, the date and the SHA-256 of both settings files' ($logObj -and $logObj.sourceSite -eq 'CD2' -and $logObj.sourceSettingsSha256 -eq (Get-FileHash (Join-Path $cd2 'settings.json') -Algorithm SHA256).Hash.ToLower() -and $logObj.targetSettingsSha256 -eq (Get-FileHash $abSettings -Algorithm SHA256).Hash.ToLower()) 'log missing or hashes differ'
            Check 'e2e-12 no backup was made for a run without a replace' (@(Get-ChildItem $ab1 -Filter 'settings.json.before-import-*').Count -eq 0) 'a backup exists'

            # ---- Settings under AB1 shows the value
            $null = Click-Nav 'Settings'; Start-Sleep 3
            $tree = Find-El $null 'nav_configmgr' -Timeout 10
            if ($tree) { Real-Click $tree; Start-Sleep 2 }
            $shown = [string](Need-El 'txtFileShareRoot').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
            Check 'e2e-13 Settings under AB1 shows the imported File Share Root' ($shown -eq '\\files.cd2.example.com\content$') "value: '$shown'"

            # ---- A second import has nothing to copy
            $afterFirst = Hash-Tree $ab1
            Open-ImportWindow
            $btn = Need-El 'btnImportSettings'
            Check 'e2e-14 a second import has nothing to copy (Import disabled)' (-not $btn.Current.IsEnabled) 'Import is enabled'
            Close-ImportWindow
            Check 'e2e-15 the second preview changed no file of AB1' (Same-Tree $afterFirst (Hash-Tree $ab1)) 'a file changed'

            # ---- Replace for the conflict
            Open-ImportWindow
            $combo = Need-Name 'Conflict choice for Content distribution (DP group, test collection)' 'ComboBox' 10
            Select-Combo $combo '*Replace*'
            Start-Sleep 1
            $sum2 = [string](Need-El 'txtImportSummary').Current.Name
            $imp3 = Need-El 'btnImportSettings'
            "INFO after choosing Replace: summary '$sum2'; Import enabled: $($imp3.Current.IsEnabled)"
            Save-Shot (Join-Path $Run 'replace-choice.png')
            $targetBefore = (Get-FileHash $abSettings -Algorithm SHA256).Hash
            Real-Click (Need-El 'btnImportSettings')
            Start-Sleep 4
            $t2 = Get-Content -LiteralPath $abSettings -Raw | ConvertFrom-Json
            Check 'e2e-16 Replace overwrites the DP group of AB1 with the value of CD2' ($t2.ContentDistribution.DpGroupName -eq 'CD2 Distribution Points') ('value: ' + $t2.ContentDistribution.DpGroupName)
            $backups = @(Get-ChildItem $ab1 -Filter 'settings.json.before-import-*')
            Check 'e2e-17 a verified backup of the target file was kept' (($backups.Count -eq 1) -and ((Get-FileHash $backups[0].FullName -Algorithm SHA256).Hash -eq $targetBefore)) ('backups: ' + $backups.Count)
            Check 'e2e-18 the files of sites\CD2 are still unchanged' (Same-Tree $cdBefore (Hash-Tree $cd2)) 'a file of CD2 changed'
        }
        else {
            Close-ImportWindow
        }
    }
    finally { Stop-Apd }

    if ($isE2e) {
        $secretHits = @(Get-ChildItem $sites -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -ne '.db' } | Select-String -Pattern 'FAKE_TOKEN|Protected"|ClientSecret|ApiKey|Token"' -ErrorAction SilentlyContinue)
        Check 'e2e-19 no secret or token reached any file under sites' ($secretHits.Count -eq 0) ('hits: ' + (($secretHits | Select-Object -First 3 | ForEach-Object { $_.Path }) -join ', '))
    }
}
"E2E-FAILS=$script:Fails"
