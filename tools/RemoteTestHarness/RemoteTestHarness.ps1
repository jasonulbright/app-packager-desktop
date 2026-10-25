#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs, detects and uninstalls staged catalog definitions on a test machine through
    PowerShell remoting.
    Runs on the controller. Reboots the test machine when a reboot is pending or an
    installer exits 3010.

.PARAMETER ComputerName
    Test machine name. The machine must accept PowerShell remoting from the controller.

.PARAMETER Credential
    Administrator account on the test machine.

.PARAMETER DefsDir
    Path to the JSON definitions on the controller. Default: the Definitions folder of this
    repository.

.PARAMETER StagedRoot
    Path on the test machine where staged content lives. Default: C:\temp\ap

.PARAMETER ResultsPath
    CSV output path on the controller. Default: C:\temp\remote-test-results-<timestamp>.csv
#>

param(
    [Parameter(Mandatory)][string]$ComputerName,
    [Parameter(Mandatory)][PSCredential]$Credential,
    [string]$DefsDir = (Join-Path $PSScriptRoot '..\..\src\ApplicationPackagerEngine\Definitions'),
    [string]$StagedRoot = 'C:\temp\ap',
    [string]$ResultsPath = "C:\temp\remote-test-results-$(Get-Date -Format 'yyyyMMdd-HHmmss').csv",
    [int]$TimeoutSec = 300
)

$cred = $Credential

$skipDefs = @(
    '7zip.json'          # 7z.exe stages the other definitions; uninstalling it breaks them
    'Dotnet10x64.json'
    'teams.json'         # MSIX
    'foxitreader.json'   # CDN throttle
    'filezilla.json'     # PUP
    'vmwaretools.json'   # Hyper-V
    'vs2026.json'        # Too large
    'vs2026community.json'
)

# Runtimes install first: later installers fail without them.
function Get-Priority($fileName) {
    $n = $fileName.ToLower()
    if ($n -match 'msvcruntimes') { return 0 }
    if ($n -match 'dotnet[89]|dotnet8') { return 1 }
    if ($n -match 'aspnet') { return 2 }
    return 10
}

function Test-PendingReboot {
    param([string]$Computer, [PSCredential]$Credential)
    Invoke-Command -ComputerName $Computer -Credential $Credential -ScriptBlock {
        $pending = $false
        $cbs = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing' -Name RebootPending -ErrorAction SilentlyContinue
        if ($cbs) { $pending = $true }
        $wu = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired' -ErrorAction SilentlyContinue
        if ($wu) { $pending = $true }
        $pfro = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue
        if ($pfro -and $pfro.PendingFileRenameOperations.Count -gt 0) { $pending = $true }
        return $pending
    }
}

function Invoke-RebootAndWait {
    param([string]$Computer, [PSCredential]$Credential)
    Write-Host "    REBOOTING $Computer..." -ForegroundColor Yellow
    Invoke-Command -ComputerName $Computer -Credential $Credential -ScriptBlock { Restart-Computer -Force }
    Start-Sleep -Seconds 15

    $attempts = 0
    while ($attempts -lt 12) {
        if (-not (Test-Connection $Computer -Count 1 -Quiet -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Seconds 5
        $attempts++
    }

    Write-Host "    Waiting for $Computer to come back online..." -ForegroundColor Yellow
    $attempts = 0
    while ($attempts -lt 60) {
        Start-Sleep -Seconds 10
        try {
            $result = Invoke-Command -ComputerName $Computer -Credential $Credential -ScriptBlock { $env:COMPUTERNAME } -ErrorAction Stop
            if ($result) {
                Write-Host "    $Computer is back online." -ForegroundColor Green
                Start-Sleep -Seconds 10
                return $true
            }
        } catch { }
        $attempts++
    }
    Write-Host "    $Computer did not come back after 10 minutes!" -ForegroundColor Red
    return $false
}

"App,Phase,Result,DurationMs" | Out-File $ResultsPath -Encoding UTF8

$defs = Get-ChildItem $DefsDir -Filter '*.json' |
    Where-Object { $_.Name -notin $skipDefs } |
    Sort-Object { Get-Priority $_.Name }, Name

Write-Host "App Packager Desktop remote test harness"
Write-Host "  Target: $ComputerName"
Write-Host "  Definitions: $($defs.Count)"
Write-Host "  Timeout: ${TimeoutSec}s per app"
Write-Host ""

$passed = 0; $failed = 0; $skipped = 0

# Read once and shipped into each remote job, so the client evaluates rules with the same
# semantics as the engine rather than a looser copy.
$evaluationPath = Join-Path $PSScriptRoot 'DetectionEvaluation.ps1'
if (-not (Test-Path -LiteralPath $evaluationPath)) {
    throw "Detection evaluator not found beside the harness: $evaluationPath"
}
$evaluationSource = Get-Content -LiteralPath $evaluationPath -Raw

foreach ($defFile in $defs) {
    $def = Get-Content $defFile.FullName -Raw | ConvertFrom-Json
    $appName = $def.Application
    $fileName = $defFile.Name

    Write-Host "=== $appName ($fileName) ===" -ForegroundColor Cyan

    if (Test-PendingReboot -Computer $ComputerName -Credential $cred) {
        Write-Host "  Pending reboot detected before install" -ForegroundColor Yellow
        if (-not (Invoke-RebootAndWait -Computer $ComputerName -Credential $cred)) {
            Write-Host "  SKIP: VM did not recover from reboot" -ForegroundColor Red
            "$appName,Reboot,FAIL - VM not recoverable,0" | Out-File $ResultsPath -Append -Encoding UTF8
            $failed++
            continue
        }
    }

    # The job does not stage: content must already be staged under StagedRoot on the test machine.
    Write-Host "  Stage+Test..." -NoNewline

    try {
        $job = Invoke-Command -ComputerName $ComputerName -Credential $cred -AsJob -ScriptBlock {
            param($AppJson, $StagedRoot, $TimeoutSec, $EvaluationSource)

            # The evaluator travels as source text: this block runs on the client and the
            # file lives beside this script on the controller.
            . ([scriptblock]::Create($EvaluationSource))

            $def = $AppJson | ConvertFrom-Json

            $appFolder = ($def.Application -replace ' ','') + "-$($def.Architecture)"
            $stagedDir = Get-ChildItem $StagedRoot -Directory -Filter "$appFolder*" -ErrorAction SilentlyContinue |
                ForEach-Object { Get-ChildItem $_.FullName -Directory | Select-Object -First 1 } |
                Select-Object -First 1

            if (-not $stagedDir -or -not (Test-Path "$($stagedDir.FullName)\install.ps1")) {
                return @{ Phase = 'Stage'; Result = 'SKIP - not staged'; ExitCode = -1; Duration = 0 }
            }

            $contentPath = $stagedDir.FullName
            $results = @()

            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $ps1 = Join-Path $contentPath 'install.ps1'
            $proc = Start-Process powershell.exe -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$ps1`"" `
                -WorkingDirectory $contentPath -Wait -PassThru -NoNewWindow
            $sw.Stop()
            $installExit = $proc.ExitCode
            $results += @{ Phase = 'Install'; Result = $(if ($installExit -eq 0) {'PASS'} elseif ($installExit -eq 3010) {'PASS (reboot required)'} else {"FAIL (exit $installExit)"}); ExitCode = $installExit; Duration = $sw.ElapsedMilliseconds }

            if ($installExit -ne 0 -and $installExit -ne 3010) {
                return $results
            }

            $manifestJson = Get-Content (Join-Path $contentPath 'stage-manifest.json') -Raw | ConvertFrom-Json
            $det = $manifestJson.Detection
            $scriptRunner = { param($text) Invoke-DetectionScript -ScriptText $text -TimeoutSeconds $TimeoutSec }
            $verdict = Get-DetectionVerdict -Detection $det -ScriptRunner $scriptRunner

            $detectResult = switch ($verdict) {
                'Detected'    { 'PASS' }
                'NotDetected' { 'FAIL (not detected)' }
                default       { 'FAIL (detection rule could not be evaluated)' }
            }
            $results += @{ Phase = 'Detect'; Result = $detectResult; ExitCode = 0; Duration = 0 }

            if ($verdict -ne 'Detected') { return $results }

            $unPs1 = Join-Path $contentPath 'uninstall.ps1'
            if (Test-Path $unPs1) {
                $sw = [System.Diagnostics.Stopwatch]::StartNew()
                $proc = Start-Process powershell.exe -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$unPs1`"" `
                    -WorkingDirectory $contentPath -Wait -PassThru -NoNewWindow
                $sw.Stop()
                $unExit = $proc.ExitCode
                $results += @{ Phase = 'Uninstall'; Result = $(if ($unExit -eq 0) {'PASS'} elseif ($unExit -eq 3010) {'PASS (reboot required)'} else {"FAIL (exit $unExit)"}); ExitCode = $unExit; Duration = $sw.ElapsedMilliseconds }

                # Clean check. Only a genuine not-detected is clean: a rule that could not
                # be read says nothing about whether the application is gone.
                $cleanVerdict = Get-DetectionVerdict -Detection $det -ScriptRunner $scriptRunner
                $cleanResult = switch ($cleanVerdict) {
                    'NotDetected' { 'PASS' }
                    'Detected'    { 'FAIL (still detected)' }
                    default       { 'FAIL (detection rule could not be evaluated)' }
                }
                $results += @{ Phase = 'Clean'; Result = $cleanResult; ExitCode = 0; Duration = 0 }
            } else {
                $results += @{ Phase = 'Uninstall'; Result = 'SKIP (no uninstall.ps1)'; ExitCode = 0; Duration = 0 }
            }

            return $results
        } -ArgumentList ($def | ConvertTo-Json -Depth 10), $StagedRoot, $TimeoutSec, $evaluationSource -ErrorAction Stop

        $completed = Wait-Job $job -Timeout $TimeoutSec
        if (-not $completed) {
            Write-Host " TIMEOUT (${TimeoutSec}s)" -ForegroundColor Red
            Stop-Job $job
            Remove-Job $job -Force
            "`"$appName`",`"Test`",`"TIMEOUT`",$($TimeoutSec * 1000)" | Out-File $ResultsPath -Append -Encoding UTF8
            $failed++
            Invoke-Command -ComputerName $ComputerName -Credential $cred -ScriptBlock {
                Get-Process -Name msiexec, cmd, powershell -ErrorAction SilentlyContinue |
                    Where-Object { $_.StartTime -gt (Get-Date).AddMinutes(-10) } |
                    Stop-Process -Force -ErrorAction SilentlyContinue
            } -ErrorAction SilentlyContinue
            continue
        }
        $result = Receive-Job $job
        Remove-Job $job -Force

        $needsReboot = $false
        if ($result -is [System.Collections.Hashtable]) { $result = @($result) }

        foreach ($r in $result) {
            $phase = $r.Phase
            $res = $r.Result
            $dur = $r.Duration
            $exit = $r.ExitCode

            Write-Host ""
            Write-Host "    $phase : $res $(if($dur -gt 0){"(${dur}ms)"})" -ForegroundColor $(if ($res -like 'PASS*') {'Green'} elseif ($res -like 'FAIL*') {'Red'} else {'Yellow'})
            "`"$appName`",`"$phase`",`"$res`",$dur" | Out-File $ResultsPath -Append -Encoding UTF8

            if ($exit -eq 3010 -or $res -like '*reboot*') { $needsReboot = $true }
        }

        $allPass = ($result | Where-Object { $_.Result -like 'PASS*' }).Count -eq $result.Count
        if ($allPass) { $passed++ } else { $failed++ }

        if ($needsReboot) {
            Write-Host "    Exit 3010 or reboot required" -ForegroundColor Yellow
            "`"$appName`",`"Reboot`",`"Triggered`",0" | Out-File $ResultsPath -Append -Encoding UTF8
            Invoke-RebootAndWait -Computer $ComputerName -Credential $cred | Out-Null
        }
    }
    catch {
        Write-Host " ERROR: $($_.Exception.Message)" -ForegroundColor Red
        "`"$appName`",`"Error`",`"$($_.Exception.Message)`",0" | Out-File $ResultsPath -Append -Encoding UTF8
        $failed++
    }

    Write-Host ""
}

Write-Host "═══════════════════════════════════════════════" -ForegroundColor White
Write-Host "TOTAL: $($defs.Count) | PASSED: $passed | FAILED: $failed | SKIPPED: $skipped"
Write-Host "Results: $ResultsPath"
