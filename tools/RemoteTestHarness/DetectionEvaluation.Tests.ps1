# Checks the remote evaluator against the same cases the engine's DetectionEvaluator
# tests cover. No Pester dependency, no install workflow, no VM: dot-sources the
# evaluation functions and asserts on their verdicts.
#
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File DetectionEvaluation.Tests.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'DetectionEvaluation.ps1')

$script:pass = 0
$script:fail = 0

function Assert-Verdict {
    param([string]$Name, [string]$Expected, [string]$Actual)

    if ($Expected -eq $Actual) {
        $script:pass++
    } else {
        $script:fail++
        Write-Host "  FAIL  $Name -- expected '$Expected', got '$Actual'" -ForegroundColor Red
    }
}

function Rule {
    param(
        [string]$Type = 'RegistryKeyValue',
        [string]$ExpectedValue,
        [string]$Operator,
        [string]$PropertyType = 'String',
        [string]$Hive = 'LocalMachine',
        [string]$RegistryKeyRelative,
        [bool]$Is64Bit = $true,
        [string]$ValueName = 'DisplayVersion',
        [string]$ScriptText,
        [string]$ScriptLanguage
    )
    return [pscustomobject]@{
        Type                = $Type
        ExpectedValue       = $ExpectedValue
        Operator            = $Operator
        PropertyType        = $PropertyType
        Hive                = $Hive
        RegistryKeyRelative = $RegistryKeyRelative
        Is64Bit             = $Is64Bit
        ValueName           = $ValueName
        ScriptText          = $ScriptText
        ScriptLanguage      = $ScriptLanguage
        FilePath            = $null
        FileName            = $null
    }
}

Write-Host "Remote detection evaluator checks"
Write-Host ""

# -- String comparisons read the whole value ---------------------------------
Assert-Verdict 'BeginsWith matches a suffixed reading' 'Detected' `
    (Compare-DetectionValue -ActualValue '7.23.0' -Detection (Rule -ExpectedValue '7.23' -Operator 'BeginsWith'))
Assert-Verdict 'BeginsWith matches the EnterpriseDB build suffix' 'Detected' `
    (Compare-DetectionValue -ActualValue '17.11-1' -Detection (Rule -ExpectedValue '17.11' -Operator 'BeginsWith'))
Assert-Verdict 'BeginsWith rejects a shorter reading' 'NotDetected' `
    (Compare-DetectionValue -ActualValue '7.2' -Detection (Rule -ExpectedValue '7.23' -Operator 'BeginsWith'))
Assert-Verdict 'BeginsWith is not equality' 'Detected' `
    (Compare-DetectionValue -ActualValue '7.23.0' -Detection (Rule -ExpectedValue '7.23' -Operator 'BeginsWith'))
Assert-Verdict 'EndsWith matches' 'Detected' `
    (Compare-DetectionValue -ActualValue '1.0.0-beta' -Detection (Rule -ExpectedValue 'beta' -Operator 'EndsWith'))
Assert-Verdict 'EndsWith rejects' 'NotDetected' `
    (Compare-DetectionValue -ActualValue '1.0.0-beta' -Detection (Rule -ExpectedValue 'alpha' -Operator 'EndsWith'))
Assert-Verdict 'Contains matches' 'Detected' `
    (Compare-DetectionValue -ActualValue '1.0.0-beta.2' -Detection (Rule -ExpectedValue 'beta' -Operator 'Contains'))
Assert-Verdict 'Contains rejects' 'NotDetected' `
    (Compare-DetectionValue -ActualValue '1.0.0' -Detection (Rule -ExpectedValue 'beta' -Operator 'Contains'))

# -- Version comparisons honor the declared operator -------------------------
Assert-Verdict 'GreaterThan' 'Detected' `
    (Compare-DetectionValue -ActualValue '2.0.0' -Detection (Rule -ExpectedValue '1.0.0' -Operator 'GreaterThan' -PropertyType 'Version'))
Assert-Verdict 'GreaterThan on equal versions' 'NotDetected' `
    (Compare-DetectionValue -ActualValue '1.0.0' -Detection (Rule -ExpectedValue '1.0.0' -Operator 'GreaterThan' -PropertyType 'Version'))
Assert-Verdict 'GreaterEquals on equal versions' 'Detected' `
    (Compare-DetectionValue -ActualValue '1.0.0' -Detection (Rule -ExpectedValue '1.0.0' -Operator 'GreaterEquals' -PropertyType 'Version'))
Assert-Verdict 'A lower installed version is not detected' 'NotDetected' `
    (Compare-DetectionValue -ActualValue '1.136.2' -Detection (Rule -ExpectedValue '1.137.0' -Operator 'GreaterEquals' -PropertyType 'Version'))
Assert-Verdict 'LessThan' 'Detected' `
    (Compare-DetectionValue -ActualValue '0.9.0' -Detection (Rule -ExpectedValue '1.0.0' -Operator 'LessThan' -PropertyType 'Version'))
Assert-Verdict 'LessEquals' 'Detected' `
    (Compare-DetectionValue -ActualValue '1.0.0' -Detection (Rule -ExpectedValue '1.0.0' -Operator 'LessEquals' -PropertyType 'Version'))
Assert-Verdict 'IsEquals' 'Detected' `
    (Compare-DetectionValue -ActualValue '1.0.0' -Detection (Rule -ExpectedValue '1.0.0' -Operator 'IsEquals' -PropertyType 'Version'))
Assert-Verdict 'NotEquals matches a different version' 'Detected' `
    (Compare-DetectionValue -ActualValue '1.0.0' -Detection (Rule -ExpectedValue '2.0.0' -Operator 'NotEquals' -PropertyType 'Version'))
Assert-Verdict 'NotEquals rejects the same version' 'NotDetected' `
    (Compare-DetectionValue -ActualValue '1.0.0' -Detection (Rule -ExpectedValue '1.0.0' -Operator 'NotEquals' -PropertyType 'Version'))

# -- Deployment defaults -----------------------------------------------------
Assert-Verdict 'A registry rule with no operator is equality' 'Detected' `
    (Compare-DetectionValue -ActualValue '1.0.0' -Detection (Rule -ExpectedValue '1.0.0' -PropertyType 'Version'))
Assert-Verdict 'A registry rule with no operator rejects a newer build' 'NotDetected' `
    (Compare-DetectionValue -ActualValue '1.0.1' -Detection (Rule -ExpectedValue '1.0.0' -PropertyType 'Version'))
Assert-Verdict 'A file rule with no operator is greater-or-equal' 'Detected' `
    (Compare-DetectionValue -ActualValue '2.0.0' -Detection (Rule -ExpectedValue '1.0.0' -PropertyType 'Version') -DefaultOperator 'GreaterEquals')
Assert-Verdict 'A file rule with no operator rejects an older build' 'NotDetected' `
    (Compare-DetectionValue -ActualValue '0.9.0' -Detection (Rule -ExpectedValue '1.0.0' -PropertyType 'Version') -DefaultOperator 'GreaterEquals')

# -- Fail closed -------------------------------------------------------------
foreach ($op in @('Matches', 'OneOf', 'NotBeginsWith')) {
    Assert-Verdict "Unsupported operator '$op' is an error" 'Error' `
        (Compare-DetectionValue -ActualValue '1.0.0' -Detection (Rule -ExpectedValue '1.0.0' -Operator $op -PropertyType 'Version'))
}
Assert-Verdict 'An unsupported operator errors even with an absent value' 'Error' `
    (Compare-DetectionValue -ActualValue $null -Detection (Rule -ExpectedValue '1.0.0' -Operator 'Matches' -PropertyType 'Version'))
Assert-Verdict 'A version comparison against a non-version is an error' 'Error' `
    (Compare-DetectionValue -ActualValue '17.11-1' -Detection (Rule -ExpectedValue '17.11' -Operator 'GreaterEquals' -PropertyType 'Version'))
Assert-Verdict 'An absent value is not-detected, not an error' 'NotDetected' `
    (Compare-DetectionValue -ActualValue $null -Detection (Rule -ExpectedValue '1.0' -Operator 'IsEquals'))
Assert-Verdict 'A rule with no expected value asks only for presence' 'Detected' `
    (Compare-DetectionValue -ActualValue 'anything' -Detection (Rule -Operator 'IsEquals'))
Assert-Verdict 'An unimplemented rule type is an error' 'Error' `
    (Get-DetectionVerdict -Detection (Rule -Type 'Msi'))

# -- Script verdicts ---------------------------------------------------------
Assert-Verdict 'Exit 0 with output is detected' 'Detected' (Get-ScriptDetectionVerdict -ExitCode 0 -StdOut 'Installed' -ScriptLanguage 'PowerShell')
Assert-Verdict 'Exit 0 with no output is not detected' 'NotDetected' (Get-ScriptDetectionVerdict -ExitCode 0 -StdOut '' -ScriptLanguage 'PowerShell')
Assert-Verdict 'Exit 0 with blank output is not detected' 'NotDetected' (Get-ScriptDetectionVerdict -ExitCode 0 -StdOut '   ' -ScriptLanguage 'PowerShell')
Assert-Verdict 'A nonzero exit is an error, not an absence' 'Error' (Get-ScriptDetectionVerdict -ExitCode 1 -StdOut 'Installed' -ScriptLanguage 'PowerShell')
Assert-Verdict 'A nonzero exit with no output is an error' 'Error' (Get-ScriptDetectionVerdict -ExitCode 1 -StdOut '' -ScriptLanguage 'PowerShell')
Assert-Verdict 'A blank language reads as PowerShell' 'Detected' (Get-ScriptDetectionVerdict -ExitCode 0 -StdOut 'Installed' -ScriptLanguage '')
Assert-Verdict 'VBScript is refused' 'Error' (Get-ScriptDetectionVerdict -ExitCode 0 -StdOut 'Installed' -ScriptLanguage 'VBScript')
Assert-Verdict 'A script rule with no runner is an error' 'Error' `
    (Get-DetectionVerdict -Detection (Rule -Type 'Script' -ScriptText 'exit 0'))
Assert-Verdict 'A script rule with no body is an error' 'Error' `
    (Get-DetectionVerdict -Detection (Rule -Type 'Script') -ScriptRunner { param($t) @{ ExitCode = 0; StdOut = 'Installed' } })

# -- The rule's own hive and view (disposable HKCU key) ----------------------
$testKey = 'Software\PackrTests\' + [guid]::NewGuid().ToString('N')
try {
    $created = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testKey)
    $created.SetValue('DisplayVersion', '7.23.0')
    $created.Close()

    $userRule = Rule -Hive 'CurrentUser' -RegistryKeyRelative $testKey -ExpectedValue '7.23' -Operator 'BeginsWith' -Is64Bit $false
    Assert-Verdict 'A per-user rule reads the current user hive' 'Detected' (Get-DetectionVerdict -Detection $userRule)

    $userRule64 = Rule -Hive 'CurrentUser' -RegistryKeyRelative $testKey -ExpectedValue '7.23' -Operator 'BeginsWith' -Is64Bit $true
    Assert-Verdict 'The same rule under the 64-bit view also reads it' 'Detected' (Get-DetectionVerdict -Detection $userRule64)

    $machineRule = Rule -Hive 'LocalMachine' -RegistryKeyRelative $testKey -ExpectedValue '7.23' -Operator 'BeginsWith' -Is64Bit $false
    Assert-Verdict 'The same key is absent from the machine hive' 'NotDetected' (Get-DetectionVerdict -Detection $machineRule)

    $wrongValue = Rule -Hive 'CurrentUser' -RegistryKeyRelative $testKey -ExpectedValue '7.24' -Operator 'BeginsWith' -Is64Bit $false
    Assert-Verdict 'A wrong version is not detected' 'NotDetected' (Get-DetectionVerdict -Detection $wrongValue)

    $badOp = Rule -Hive 'CurrentUser' -RegistryKeyRelative $testKey -ExpectedValue '7.23' -Operator 'Matches' -Is64Bit $false
    Assert-Verdict 'An unsupported operator errors before the key is read' 'Error' (Get-DetectionVerdict -Detection $badOp)

    $missing = Rule -Hive 'CurrentUser' -RegistryKeyRelative ($testKey + '\absent') -ExpectedValue '7.23' -Operator 'BeginsWith' -Is64Bit $false
    Assert-Verdict 'A missing key is not detected' 'NotDetected' (Get-DetectionVerdict -Detection $missing)

    $keyRule = Rule -Type 'RegistryKey' -Hive 'CurrentUser' -RegistryKeyRelative $testKey -Is64Bit $false
    Assert-Verdict 'A key existence rule reads its own hive' 'Detected' (Get-DetectionVerdict -Detection $keyRule)

    # -- Post-uninstall reading: the value is gone, the machine is clean -----
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($testKey, $false)
    Assert-Verdict 'After removal the per-user rule is not detected' 'NotDetected' (Get-DetectionVerdict -Detection $userRule)
    Assert-Verdict 'After removal an unsupported rule still errors' 'Error' (Get-DetectionVerdict -Detection $badOp)
}
finally {
    try { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($testKey, $false) } catch { }
    try { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey('Software\PackrTests', $false) } catch { }
}

# -- A directory is not the file it is named after ---------------------------
$fileFixture = Join-Path $env:TEMP ('packr-file-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $fileFixture | Out-Null

    # A directory carrying the file's name must not satisfy an existence rule.
    $decoy = Join-Path $fileFixture 'setup.exe'
    New-Item -ItemType Directory -Path $decoy | Out-Null
    Assert-Verdict 'A directory named like the file is not the file' 'NotDetected' `
        (Get-FilePresence -Path $decoy)

    $dirRule = Rule -Type 'File' -PropertyType 'Existence'
    $dirRule.FilePath = $fileFixture
    $dirRule.FileName = 'setup.exe'
    Assert-Verdict 'An existence rule is not satisfied by a directory' 'NotDetected' `
        (Get-DetectionVerdict -Detection $dirRule)

    Assert-Verdict 'A missing file is not detected' 'NotDetected' `
        (Get-FilePresence -Path (Join-Path $fileFixture 'absent.exe'))

    # -- The real file-reading path against a known version ------------------
    # The engine's own build output: present, harmless, and its FileVersion parses.
    # Nothing is installed for this.
    $realFile = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) `
        'src\ApplicationPackagerEngine\bin\Release\net10.0-windows\ApplicationPackagerEngine.dll'
    if (-not (Test-Path -LiteralPath $realFile)) {
        throw "Build the engine in Release before running these checks: $realFile"
    }
    Assert-Verdict 'A real file is detected' 'Detected' (Get-FilePresence -Path $realFile)

    $actualVersion = [version]([System.Diagnostics.FileVersionInfo]::GetVersionInfo($realFile).FileVersion)
    Write-Host "  (file fixture version: $actualVersion)"

    $lower = [version]::new([Math]::Max(0, $actualVersion.Major - 1), 0, 0, 0)
    $higher = [version]::new($actualVersion.Major + 1, 0, 0, 0)

    function FileRule {
        param([string]$Expected, [string]$Operator)
        $r = Rule -Type 'File' -PropertyType 'Version' -ExpectedValue $Expected -Operator $Operator
        $r.FilePath = (Split-Path -Parent $realFile)
        $r.FileName = (Split-Path -Leaf $realFile)
        return $r
    }

    Assert-Verdict 'A lower expected version satisfies greater-or-equal' 'Detected' `
        (Get-DetectionVerdict -Detection (FileRule -Expected $lower.ToString() -Operator 'GreaterEquals'))
    Assert-Verdict 'The current version satisfies greater-or-equal' 'Detected' `
        (Get-DetectionVerdict -Detection (FileRule -Expected $actualVersion.ToString() -Operator 'GreaterEquals'))
    Assert-Verdict 'A higher expected version is not detected' 'NotDetected' `
        (Get-DetectionVerdict -Detection (FileRule -Expected $higher.ToString() -Operator 'GreaterEquals'))
    Assert-Verdict 'The current version satisfies equality' 'Detected' `
        (Get-DetectionVerdict -Detection (FileRule -Expected $actualVersion.ToString() -Operator 'IsEquals'))
    Assert-Verdict 'A file rule with no operator defaults to greater-or-equal' 'Detected' `
        (Get-DetectionVerdict -Detection (FileRule -Expected $lower.ToString() -Operator $null))
    Assert-Verdict 'The no-operator default rejects a higher expected version' 'NotDetected' `
        (Get-DetectionVerdict -Detection (FileRule -Expected $higher.ToString() -Operator $null))
    Assert-Verdict 'An unsupported file operator is an error' 'Error' `
        (Get-DetectionVerdict -Detection (FileRule -Expected $actualVersion.ToString() -Operator 'Matches'))

    # A Windows system binary carries a build comment after the numbers, so its
    # FileVersion is not a version. That has to fail closed, not read as absent.
    $commented = Join-Path $env:SystemRoot 'System32\kernel32.dll'
    if (Test-Path -LiteralPath $commented) {
        $commentedRule = Rule -Type 'File' -PropertyType 'Version' -ExpectedValue '10.0.0.0' -Operator 'GreaterEquals'
        $commentedRule.FilePath = (Split-Path -Parent $commented)
        $commentedRule.FileName = (Split-Path -Leaf $commented)
        Assert-Verdict 'An unparseable file version is an error, not an absence' 'Error' `
            (Get-DetectionVerdict -Detection $commentedRule)
    }
}
finally {
    Remove-Item -LiteralPath $fileFixture -Recurse -Force -ErrorAction SilentlyContinue
}

# -- An unsupported language is refused before the runner is invoked ---------
foreach ($language in @('VBScript', 'JavaScript')) {
    $script:runnerCalled = $false
    $spyRunner = { param($t) $script:runnerCalled = $true; @{ ExitCode = 0; StdOut = 'Installed' } }
    $langRule = Rule -Type 'Script' -ScriptText 'wscript.echo "x"' -ScriptLanguage $language

    Assert-Verdict "$language is refused" 'Error' (Get-DetectionVerdict -Detection $langRule -ScriptRunner $spyRunner)
    if ($script:runnerCalled) {
        $script:fail++
        Write-Host "  FAIL  the runner was invoked for $language" -ForegroundColor Red
    } else { $script:pass++ }
}

# -- A timed-out script is actually terminated ------------------------------
$sleepScript = 'Start-Sleep -Seconds 30; Write-Output "finished"'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$run = Invoke-DetectionScript -ScriptText $sleepScript -TimeoutSeconds 1
$sw.Stop()

if ($run.TimedOut -and $run.ExitCode -eq -1) { $script:pass++ }
else { $script:fail++; Write-Host "  FAIL  the timeout was not reported (TimedOut=$($run.TimedOut) ExitCode=$($run.ExitCode))" -ForegroundColor Red }

if ($sw.Elapsed.TotalSeconds -lt 25) { $script:pass++ }
else { $script:fail++; Write-Host "  FAIL  the wait ran long ($([int]$sw.Elapsed.TotalSeconds)s)" -ForegroundColor Red }

# The child must be gone, not merely abandoned by a Kill call that threw.
Start-Sleep -Milliseconds 500
$leftovers = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and $_.CommandLine -like '*packr-detect-*' })
if ($leftovers.Count -eq 0) { $script:pass++ }
else { $script:fail++; Write-Host "  FAIL  $($leftovers.Count) detection process(es) survived the timeout" -ForegroundColor Red }

Assert-Verdict 'A timed-out script is an evaluation error' 'Error' `
    (Get-ScriptDetectionVerdict -ExitCode $run.ExitCode -StdOut $run.StdOut -ScriptLanguage 'PowerShell')

# -- The real script path still runs a normal body --------------------------
$okRun = Invoke-DetectionScript -ScriptText 'Write-Output "Installed"; exit 0' -TimeoutSeconds 60
Assert-Verdict 'A normal script body is detected' 'Detected' `
    (Get-ScriptDetectionVerdict -ExitCode $okRun.ExitCode -StdOut $okRun.StdOut -ScriptLanguage 'PowerShell')

$absentRun = Invoke-DetectionScript -ScriptText 'exit 0' -TimeoutSeconds 60
Assert-Verdict 'A silent exit 0 is not detected' 'NotDetected' `
    (Get-ScriptDetectionVerdict -ExitCode $absentRun.ExitCode -StdOut $absentRun.StdOut -ScriptLanguage 'PowerShell')

# Both streams drained: a body writing heavily to stderr must not block.
$noisyRun = Invoke-DetectionScript -ScriptText '1..2000 | ForEach-Object { [Console]::Error.WriteLine("e" * 200) }; Write-Output "Installed"; exit 0' -TimeoutSeconds 60
Assert-Verdict 'A noisy stderr does not block the run' 'Detected' `
    (Get-ScriptDetectionVerdict -ExitCode $noisyRun.ExitCode -StdOut $noisyRun.StdOut -ScriptLanguage 'PowerShell')

Write-Host ""
Write-Host "Passed: $script:pass  Failed: $script:fail"
if ($script:fail -gt 0) { exit 1 }
exit 0
