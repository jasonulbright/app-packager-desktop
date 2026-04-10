<#
.SYNOPSIS
    Retests apps from REGRESSION-STATUS.md "Needs Retest" list on CLIENT01.
    Runs Stage (on host via Packr) -> copy to CLIENT01 -> Install -> Detect -> Uninstall -> Clean.

.DESCRIPTION
    Requires:
    - Packr staged content in C:\temp\ap\<AppName>\ on the HOST
    - PsExec.exe available (Sysinternals)
    - CLIENT01 reachable via network
    - Lab admin credentials

.NOTES
    Run from the HOST machine (where Packr runs), not from CLIENT01.
    Each app is tested independently. Results are written to console and a CSV.
#>

param(
    [string]$ClientName = 'CLIENT01',
    [string]$StagingRoot = 'C:\temp\ap',
    [string]$RemoteRoot = '\\CLIENT01\C$\temp\retest',
    [string]$ResultsCsv = 'C:\temp\retest-results.csv',
    [string[]]$Apps = @(
        'CCleaner',
        'Sysinternals Suite',
        'WinMerge',
        'WizTree',
        'pgAdmin 4',
        'XenCenter'
    ),
    [int]$PhaseTimeoutSec = 300,
    [string]$Domain = 'lab.local',
    [string]$AdminUser = 'LabAdmin',
    [string]$AdminPass = 'P@ssw0rd!',
    [switch]$SkipStage
)

$ErrorActionPreference = 'Continue'
$results = @()

function Write-Phase {
    param([string]$App, [string]$Phase, [string]$Status, [string]$Detail = '')
    $color = switch ($Status) {
        'PASS'    { 'Green' }
        'FAIL'    { 'Red' }
        'SKIP'    { 'Yellow' }
        'RUNNING' { 'Cyan' }
        default   { 'White' }
    }
    Write-Host "  [$Phase] $Status" -ForegroundColor $color -NoNewline
    if ($Detail) { Write-Host " - $Detail" -ForegroundColor Gray } else { Write-Host }
}

# Find PsExec
$psexec = @(
    'C:\Program Files\Sysinternals\PsExec.exe',
    'C:\Sysinternals\PsExec.exe',
    'C:\Windows\PsExec.exe',
    'C:\Tools\PsExec.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $psexec) {
    Write-Host 'ERROR: PsExec.exe not found. Install Sysinternals Suite.' -ForegroundColor Red
    exit 1
}

Write-Host "`n=== Packr Retest — $($Apps.Count) apps on $ClientName ===" -ForegroundColor White
Write-Host "PsExec: $psexec"
Write-Host "Staging: $StagingRoot"
Write-Host "Remote: $RemoteRoot`n"

# Map admin$ share with credentials
$uncShare = "\\$ClientName\C`$"
$psexecUser = "$Domain\$AdminUser"
Write-Host "Connecting to $uncShare as ${psexecUser}..." -ForegroundColor Cyan
net use $uncShare /user:${psexecUser} $AdminPass /persistent:no 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 2) {
    Write-Host "ERROR: Failed to connect to $uncShare (exit $LASTEXITCODE)" -ForegroundColor Red
    exit 1
}
Write-Host "Connected.`n" -ForegroundColor Green

# Ensure remote directory exists
if (-not (Test-Path $RemoteRoot)) { New-Item -Path $RemoteRoot -ItemType Directory -Force | Out-Null }

foreach ($appName in $Apps) {
    Write-Host "`n--- $appName ---" -ForegroundColor White

    # Find staged content folder (fuzzy match on folder name)
    $stagedDir = Get-ChildItem $StagingRoot -Directory | Where-Object {
        $_.Name -like "*$($appName.Replace(' ', '*'))*"
    } | Select-Object -First 1

    if (-not $stagedDir) {
        Write-Host "  SKIP: No staged content found in $StagingRoot for '$appName'" -ForegroundColor Yellow
        $results += [PSCustomObject]@{
            App = $appName; Install = 'SKIP'; Detect = 'SKIP'
            Uninstall = 'SKIP'; Clean = 'SKIP'; Overall = 'SKIP (not staged)'
        }
        continue
    }

    # Resolve version subfolder (e.g., CCleaner-x64\7.06.1270\)
    $versionDir = Get-ChildItem $stagedDir.FullName -Directory | Select-Object -First 1
    if (-not $versionDir) {
        Write-Host "  SKIP: No version subfolder in $($stagedDir.FullName)" -ForegroundColor Yellow
        $results += [PSCustomObject]@{
            App = $appName; Install = 'SKIP'; Detect = 'SKIP'
            Uninstall = 'SKIP'; Clean = 'SKIP'; Overall = 'SKIP (no version dir)'
        }
        continue
    }

    # Copy version folder contents to CLIENT01
    $remoteAppDir = Join-Path (Join-Path $RemoteRoot $stagedDir.Name) $versionDir.Name
    Write-Phase $appName 'COPY' 'RUNNING' "-> $remoteAppDir"
    if (Test-Path $remoteAppDir) { Remove-Item $remoteAppDir -Recurse -Force }
    Copy-Item $versionDir.FullName $remoteAppDir -Recurse -Force
    Write-Phase $appName 'COPY' 'PASS'

    # Local path on CLIENT01
    $clientLocalDir = $remoteAppDir -replace "\\\\$ClientName\\C\`$", 'C:'

    # Phase 1: Install
    Write-Phase $appName 'INSTALL' 'RUNNING'
    $installBat = Join-Path $remoteAppDir 'install.bat'
    if (-not (Test-Path $installBat)) {
        Write-Phase $appName 'INSTALL' 'FAIL' 'install.bat not found'
        $results += [PSCustomObject]@{
            App = $appName; Install = 'FAIL (no bat)'; Detect = 'SKIP'
            Uninstall = 'SKIP'; Clean = 'SKIP'; Overall = 'FAIL'
        }
        continue
    }

    & $psexec -s -accepteula -u ${psexecUser} -p $AdminPass "\\$ClientName" -w $clientLocalDir cmd.exe /c "install.bat" 2>&1 | Out-Null
    $installCode = $LASTEXITCODE
    $installResult = if ($installCode -in 0, 3010, 1641) { 'PASS' } else { "FAIL (exit $installCode)" }
    Write-Phase $appName 'INSTALL' $installResult

    if ($installCode -notin 0, 3010, 1641) {
        $results += [PSCustomObject]@{
            App = $appName; Install = $installResult; Detect = 'SKIP'
            Uninstall = 'SKIP'; Clean = 'SKIP'; Overall = 'FAIL'
        }
        continue
    }

    # Brief pause for installers that need it
    Start-Sleep -Seconds 5

    # Phase 2: Detect (write .ps1 to CLIENT01, run via psexec -File)
    Write-Phase $appName 'DETECT' 'RUNNING'
    $manifestPath = Join-Path $remoteAppDir 'stage-manifest.json'
    $detectPs1Remote = Join-Path $remoteAppDir 'detect.ps1'
    $detectPs1Local = $detectPs1Remote -replace "\\\\$ClientName\\C\`$", 'C:'
    $detectCreated = $false

    if (Test-Path $manifestPath) {
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $det = $manifest.Detection

        # Build detection script and write as .ps1 file on CLIENT01
        $detectLines = switch ($det.Type) {
            'RegistryKeyValue' {
                @(
                    "`$regPath = 'HKLM:\$($det.RegistryKeyRelative)'"
                    "if (-not (Test-Path `$regPath)) { Write-Host 'NOT DETECTED: key not found'; exit 1 }"
                    "`$val = (Get-ItemProperty `$regPath -ErrorAction SilentlyContinue).'$($det.ValueName)'"
                    "if (`$val) { Write-Host ""DETECTED: `$val""; exit 0 }"
                    "Write-Host 'NOT DETECTED: value empty'; exit 1"
                )
            }
            'RegistryKey' {
                @(
                    "`$regPath = 'HKLM:\$($det.RegistryKeyRelative)'"
                    "if (Test-Path `$regPath) { Write-Host 'DETECTED'; exit 0 }"
                    "Write-Host 'NOT DETECTED'; exit 1"
                )
            }
            'File' {
                @(
                    "`$filePath = '$($det.FilePath)\$($det.FileName)'"
                    "if (Test-Path `$filePath) { Write-Host ""DETECTED: `$filePath""; exit 0 }"
                    "Write-Host ""NOT DETECTED: `$filePath not found""; exit 1"
                )
            }
            default {
                @("Write-Host 'Unknown detection type: $($det.Type)'; exit 1")
            }
        }

        $detectLines | Out-File $detectPs1Remote -Encoding ASCII -Force
        $detectCreated = $true

        & $psexec -s -accepteula -u ${psexecUser} -p $AdminPass "\\$ClientName" powershell.exe -NoProfile -ExecutionPolicy Bypass -File $detectPs1Local 2>&1
        $detectCode = $LASTEXITCODE
        $detectResult = if ($detectCode -eq 0) { 'PASS' } else { 'FAIL' }
    } else {
        $detectResult = 'FAIL (no manifest)'
        $detectCode = 1
    }
    Write-Phase $appName 'DETECT' $detectResult

    # Phase 3: Uninstall
    Write-Phase $appName 'UNINSTALL' 'RUNNING'
    $uninstallBat = Join-Path $remoteAppDir 'uninstall.bat'
    if (Test-Path $uninstallBat) {
        & $psexec -s -accepteula -u ${psexecUser} -p $AdminPass "\\$ClientName" -w $clientLocalDir cmd.exe /c "uninstall.bat" 2>&1 | Out-Null
        $uninstallCode = $LASTEXITCODE
        $uninstallResult = if ($uninstallCode -in 0, 3010) { 'PASS' } else { "FAIL (exit $uninstallCode)" }
    } else {
        $uninstallResult = 'SKIP (no bat)'
        $uninstallCode = 0
    }
    Write-Phase $appName 'UNINSTALL' $uninstallResult

    Start-Sleep -Seconds 5

    # Phase 4: Clean (verify detection no longer matches)
    Write-Phase $appName 'CLEAN' 'RUNNING'
    if ($detectCreated -and $detectCode -eq 0) {
        & $psexec -s -accepteula -u ${psexecUser} -p $AdminPass "\\$ClientName" powershell.exe -NoProfile -ExecutionPolicy Bypass -File $detectPs1Local 2>&1
        $cleanCode = $LASTEXITCODE
        $cleanResult = if ($cleanCode -ne 0) { 'PASS' } else { 'FAIL (still detected)' }
    } else {
        $cleanResult = 'SKIP'
    }
    Write-Phase $appName 'CLEAN' $cleanResult

    $overall = if ($installResult -eq 'PASS' -and $detectResult -eq 'PASS' -and
                   $uninstallResult -match 'PASS|SKIP' -and $cleanResult -match 'PASS|SKIP') { 'FULL PASS' } else { 'FAIL' }

    Write-Host "  >> $overall" -ForegroundColor $(if ($overall -eq 'FULL PASS') { 'Green' } else { 'Red' })

    $results += [PSCustomObject]@{
        App = $appName; Install = $installResult; Detect = $detectResult
        Uninstall = $uninstallResult; Clean = $cleanResult; Overall = $overall
    }
}

# Output summary
Write-Host "`n=== RESULTS ===" -ForegroundColor White
$results | Format-Table -AutoSize

# Export CSV
$results | Export-Csv $ResultsCsv -NoTypeInformation -Force
Write-Host "Results saved to $ResultsCsv" -ForegroundColor Gray




