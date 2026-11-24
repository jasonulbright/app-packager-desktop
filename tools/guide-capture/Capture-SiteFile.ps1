<#
.SYNOPSIS
    The lab run of export and import of a site file (Q-112h): the end-to-end proof with fictional sites AB1 and CD2.

.DESCRIPTION
    Run it in the interactive session of the lab user under the capture lock, from the folder of the published app:
        Run-VisibleProbe.ps1 -Worktree <published folder with these scripts> -ProbeArgs "& .\Capture-SiteFile.ps1 -Run C:\guide\apd-site-file"
    The run starts the app on site AB1 (with planted fake secrets) in an isolated data folder and checks, on screen and on disk:
      1. Export site file writes one zip with three entries; no planted secret is in it;
      2. a hostile zip (path traversal entry) is refused: no import window, no file written;
      3. a zip with a changed settings entry (hash mismatch) is refused;
      4. after Add site CD2, Import site file of the AB1 file offers merge and new site; New site AB1~2 creates the site with the file's values;
      5. AB1 files are unchanged (SHA-256); the new site holds no secret; the log names the file as the source.
    Every dialog and window that the run opens is closed; the app is stopped at the end. Lines are PASS or FAIL with the reason.
    The image core-site-file-import (the Import window opened from a site file, with the new site AB1~2 chosen and the preview) is saved as <Run>\images\<theme>\core-site-file-import.png.
    Run it once for each theme: -Theme Light, then -Theme Dark.
#>
param(
    [Parameter(Mandatory)][string]$Run,
    [ValidateSet('Light', 'Dark')][string]$Theme = 'Light',
    [string]$AppFolder = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApdUiDriver.ps1')
. (Join-Path $PSScriptRoot 'GuideLib.ps1')
Initialize-Run $Run
$exe = Join-Path $AppFolder 'ApplicationPackagerUI.exe'
$script:Fails = 0
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { "PASS $Name" } else { $script:Fails++; "FAIL ${Name}: $Detail" }
}
function Real-Click($El) { Click-El $El; Start-Sleep -Milliseconds 800 }
function Open-SiteMenu { Real-Click (Need-El 'btnSiteSwitcher'); Start-Sleep 1 }
function Get-MenuItem([string]$Name) {
    for ($i = 0; $i -lt 4; $i++) {
        Start-Sleep 3
        try { (Main-Window).SetFocus() } catch { $null = $_ }
        Open-SiteMenu
        $e = Find-El $null -Name $Name -ControlType 'MenuItem' -Timeout 6
        if ($e) { return $e }
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep 2
    }
    Save-Shot (Join-Path $Run 'menu-missing.png')
    $names = @(Get-ApdWindows | ForEach-Object { $_.Current.Name + '/' + $_.Current.ClassName })
    Write-Host "INFO windows: $($names -join '; ')"
    foreach ($w in @(Get-ApdWindows)) { try { Write-Host ("INFO list of $($w.Current.Name): " + ((Get-ElList $w 60 | Out-String -Width 200))) } catch { Write-Host "INFO list failed: $($_.Exception.Message)" } }
    throw "no menu item named $Name"
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
function Wait-File([string]$Path, [double]$Seconds = 20) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) { if (Test-Path -LiteralPath $Path) { return $true }; Start-Sleep -Milliseconds 500 }
    $false
}
function Get-ZipText([string]$Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $names = @($zip.Entries | ForEach-Object FullName | Sort-Object)
        $text = foreach ($e in $zip.Entries) { $r = New-Object IO.StreamReader($e.Open()); try { $r.ReadToEnd() } finally { $r.Dispose() } }
        [pscustomobject]@{ Names = $names; Text = ($text -join "`n") }
    }
    finally { $zip.Dispose() }
}
function Close-Snack { Start-Sleep -Milliseconds 300 }

$out = Join-Path $Run 'files'
Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out | Out-Null
$script:GuideTheme = $theme
$data = Join-Path $Run "data-$theme"
Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$sites = Join-Path $data "Users\$sid\sites"
$ab1 = Join-Path $sites 'AB1'
$staging = New-RunStaging $data
$settings = @{
    DownloadRoot = $staging; SiteCode = 'AB1'; SiteCodeSaved = $true; ProviderMachine = 'cm.ab1.example.com'
    DiscoverySqlServer = 'sql.ab1.example.com'; FileShareRoot = '\\files.ab1.example.com\content$'
    ContentDistribution = @{ AutoDistribute = $true; DpGroupName = 'AB1 Distribution Points'; TestCollectionName = 'AB1 Pilot Devices' }
    Wsus = @{ ServerName = 'wsus.ab1.example.com'; UseSsl = $true }
    GitHubTokenProtected = 'FAKE_TOKEN_VALUE_AB1'
}
$zipGood = Join-Path $out 'site-AB1.zip'
$zipHostile = Join-Path $out 'hostile.zip'
$zipTamper = Join-Path $out 'tampered.zip'
$null = Start-Apd $exe $data $theme $settings
(Main-Window).SetFocus()
try {
    $null = Wait-El 'btnSiteSwitcher' 30
    Start-Sleep 3
    Check 'e2e-01 the first start opens AB1 and copies its data' (Test-Path (Join-Path $ab1 'settings.json')) 'no sites\AB1\settings.json'

    # ---- export
    Open-SiteMenu
    $exp = Need-Name 'Export site file...' 'MenuItem' 8
    Check 'e2e-02 Export site file is enabled with an active site' $exp.Current.IsEnabled 'disabled'
    Real-Click $exp
    Send-DialogPath $zipGood
    Check 'e2e-03 the export wrote the zip' (Wait-File $zipGood) 'no file'
    if (Test-Path $zipGood) {
        $z = Get-ZipText $zipGood
        Check 'e2e-04 the zip holds exactly manifest, scope and settings' (($z.Names -join ',') -eq 'manifest.json,scope.json,settings.json') ($z.Names -join ',')
        Check 'e2e-05 no planted secret or token is in the zip' ($z.Text -notmatch 'FAKE_TOKEN|Protected|ClientSecret|ApiKey') 'a secret name or value is in the file'
        Check 'e2e-06 the zip holds the DP group of AB1' ($z.Text -match 'AB1 Distribution Points') 'DP group missing'
    }

    # ---- a hostile zip and a tampered zip
    $s = [Text.Encoding]::UTF8.GetBytes('{}')
    $sc = [Text.Encoding]::UTF8.GetBytes('{ "items": [] }')
    $hash = { param($b) ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b))).Replace('-', '').ToLower() }
    $man = [ordered]@{ kind = 'app-packager-site-file'; formatVersion = 1; siteCode = 'AB1'; label = ''; createdUtc = '2026-11-23T10:00:00Z'
        entries = @([ordered]@{ name = 'settings.json'; sha256 = (& $hash $s); bytes = $s.Length }, [ordered]@{ name = 'scope.json'; sha256 = (& $hash $sc); bytes = $sc.Length }) }
    $mb = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json $man -Depth 5))
    function New-TestZip([string]$Path, $Entries) {
        $fs = [IO.File]::Create($Path); $zip = New-Object IO.Compression.ZipArchive($fs, 'Create')
        foreach ($e in $Entries) { $en = $zip.CreateEntry($e[0]); $st = $en.Open(); $st.Write($e[1], 0, $e[1].Length); $st.Dispose() }
        $zip.Dispose(); $fs.Dispose()
    }
    New-TestZip $zipHostile @(@('manifest.json', $mb), @('settings.json', $s), @('scope.json', $sc), @('..\..\evil.txt', [Text.Encoding]::UTF8.GetBytes('MZ-evil')))
    New-TestZip $zipTamper @(@('manifest.json', $mb), @('settings.json', [Text.Encoding]::UTF8.GetBytes('{ "Wsus": { "ServerName": "evil.example.com" } }')), @('scope.json', $sc))
    $treeBefore = Hash-Tree $sites
    foreach ($case in @(@('e2e-07 a hostile zip (path traversal entry) is refused', $zipHostile), @('e2e-08 a zip with a changed entry (hash mismatch) is refused', $zipTamper))) {
        Real-Click (Get-MenuItem 'Import site file...')
        Send-DialogPath $case[1]
        Start-Sleep 3
        $win = Find-El $null 'cmbImportTarget' -Timeout 4
        Check $case[0] (($null -eq $win) -and (Same-Tree $treeBefore (Hash-Tree $sites))) 'the import window opened or a file changed'
        if ($win) { $c = Find-El $null 'btnImportCancel' -Timeout 3; if ($c) { Real-Click $c } }
    }
    Check 'e2e-09 no evil.txt was written next to the run' (-not (Test-Path (Join-Path $Run 'evil.txt')) -and -not (Test-Path (Join-Path (Split-Path $Run -Parent) 'evil.txt'))) 'evil.txt exists'

    # ---- add CD2, then import the AB1 file
    Real-Click (Get-MenuItem 'Add site...')
    $null = Wait-El 'txtAddSiteCode' 15
    Set-ElText (Need-El 'txtAddSiteCode') 'CD2'
    Set-ElText (Need-El 'txtAddSiteProvider') 'cm.cd2.example.com'
    Start-Sleep 1
    Real-Click (Need-El 'btnAddSiteAccept')
    Start-Sleep 4
    $ab1Before = Hash-Tree $ab1
    Real-Click (Get-MenuItem 'Import site file...')
    Send-DialogPath $zipGood
    $target = Wait-El 'cmbImportTarget' 20
    Check 'e2e-10 a valid file opens the import window with a target list' ($null -ne $target) 'no window'
    if ($target) {
        Start-Sleep 2
        $shown = ''
        try {
            $combo0 = Need-Name 'Target site' 'ComboBox' 10
            $combo0.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep 1
            $sel = @($combo0.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::ListItem))) | Where-Object { $_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected })
            if ($sel.Count -gt 0) { $shown = (@($sel[0].FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -join ' | ') + ' | ' + $sel[0].Current.Name }
            $combo0.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse(); Start-Sleep 1
        } catch { $shown = 'error: ' + $_.Exception.Message }
        Check 'e2e-11 the target defaults to the existing site with the code of the file (AB1)' ($shown -like '*Merge into AB1*') "shown: '$shown'"
        $combo = Need-Name 'Target site' 'ComboBox' 10
        Select-Combo $combo '*New site AB1~2*'
        Start-Sleep 2
        $sum = [string](Need-El 'txtImportSummary').Current.Name
        Check 'e2e-12 the preview says what the import copies' ($sum -match 'will copy') "summary: '$sum'"
        Step 'core-site-file-import' { Shot 'core-site-file-import' }
        Real-Click (Need-El 'btnImportSettings')
        Start-Sleep 4
        $new = Join-Path $sites 'AB1~2'
        Check 'e2e-13 the new site AB1~2 exists with the DP group of the file' ((Test-Path (Join-Path $new 'settings.json')) -and ((Get-Content (Join-Path $new 'settings.json') -Raw) -match 'AB1 Distribution Points')) 'site or value missing'
        $siteJson = if (Test-Path (Join-Path $new 'site.json')) { Get-Content (Join-Path $new 'site.json') -Raw | ConvertFrom-Json } else { $null }
        Check 'e2e-14 the new site keeps the code AB1' ($siteJson -and $siteJson.siteCode -eq 'AB1') 'site.json wrong'
        $log = Join-Path $new 'imported-from.json'
        $logObj = if (Test-Path $log) { Get-Content $log -Raw | ConvertFrom-Json } else { $null }
        Check 'e2e-15 imported-from.json names the file as the source' ($logObj -and $logObj.sourceKind -eq 'file' -and $logObj.sourceSite -eq 'AB1') 'log missing or wrong'
        Check 'e2e-16 the environment values were not copied (off by default)' ((Get-Content (Join-Path $new 'settings.json') -Raw) -notmatch 'wsus.ab1.example.com') 'Wsus copied'
    }
    Check 'e2e-17 the files of AB1 are unchanged (SHA-256)' (Same-Tree $ab1Before (Hash-Tree $ab1)) 'a file of AB1 changed'
}
finally { Stop-Apd }
$secretHits = @(Get-ChildItem $sites -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -ne '.db' -and $_.Name -notlike 'settings.json' } | Select-String -Pattern 'FAKE_TOKEN' -ErrorAction SilentlyContinue)
$newSecret = @(Get-ChildItem (Join-Path $sites 'AB1~2') -Recurse -File -ErrorAction SilentlyContinue | Select-String -Pattern 'FAKE_TOKEN|Protected"' -ErrorAction SilentlyContinue)
Check 'e2e-18 no secret reached the new site' ($newSecret.Count -eq 0) ('hits: ' + (($newSecret | Select-Object -First 3 | ForEach-Object { $_.Path }) -join ', '))
$left = @(Get-Process -Name ApplicationPackagerUI -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$AppFolder*" })
Check 'e2e-19 the app is closed at the end' ($left.Count -eq 0) 'the app still runs'
"E2E-FAILS=$script:Fails"
