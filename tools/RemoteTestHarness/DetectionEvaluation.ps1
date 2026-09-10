# Detection rule evaluation for the remote test harness.
#
# Mirrors ApplicationPackagerEngine DetectionEvaluator. A rule is read with its own hive,
# registry view, property type and declared comparison operator. The verdict is one of
# 'Detected', 'NotDetected' or 'Error'; Error is distinct on purpose, because the
# post-uninstall clean check negates the verdict and negating a read failure or an
# unsupported rule would certify a machine clean without having looked.
#
# Dot-source this file; it defines functions and runs nothing.

$script:OrderingOperators = @('IsEquals', 'NotEquals', 'GreaterThan', 'GreaterEquals', 'LessThan', 'LessEquals')
$script:StringOperators = @('BeginsWith', 'EndsWith', 'Contains')

# The operator a rule takes when it declares none. The MECM clause builder defaults a
# registry value rule to equality and a file rule to greater-or-equal, so evaluation has
# to agree with what actually gets deployed.
$script:RegistryDefaultOperator = 'IsEquals'
$script:FileDefaultOperator = 'GreaterEquals'

function Test-DetectionOperatorSupported {
    param([string]$Operator, [string]$DefaultOperator)

    $name = if ([string]::IsNullOrWhiteSpace($Operator)) { $DefaultOperator } else { $Operator.Trim() }
    return ($script:OrderingOperators -contains $name) -or ($script:StringOperators -contains $name)
}

function Compare-DetectionOrdered {
    param([int]$Comparison, [string]$Operator)

    switch ($Operator.ToLowerInvariant()) {
        'isequals'      { return $(if ($Comparison -eq 0) { 'Detected' } else { 'NotDetected' }) }
        'notequals'     { return $(if ($Comparison -ne 0) { 'Detected' } else { 'NotDetected' }) }
        'greaterthan'   { return $(if ($Comparison -gt 0) { 'Detected' } else { 'NotDetected' }) }
        'greaterequals' { return $(if ($Comparison -ge 0) { 'Detected' } else { 'NotDetected' }) }
        'lessthan'      { return $(if ($Comparison -lt 0) { 'Detected' } else { 'NotDetected' }) }
        'lessequals'    { return $(if ($Comparison -le 0) { 'Detected' } else { 'NotDetected' }) }
        default         { return 'Error' }
    }
}

<#
.SYNOPSIS
    Compares a value read off the machine against the rule.
.DESCRIPTION
    The operator is validated first, so an unsupported rule is an error whether or not its
    target happens to be present. An absent reading is NotDetected, not an error. A rule
    carrying no expected value is satisfied by the value existing at all.
#>
function Compare-DetectionValue {
    param(
        [AllowNull()][string]$ActualValue,
        [Parameter(Mandatory)][object]$Detection,
        [string]$DefaultOperator = $script:RegistryDefaultOperator
    )

    if (-not (Test-DetectionOperatorSupported -Operator $Detection.Operator -DefaultOperator $DefaultOperator)) {
        return 'Error'
    }

    if ([string]::IsNullOrEmpty($ActualValue)) { return 'NotDetected' }
    if ([string]::IsNullOrEmpty($Detection.ExpectedValue)) { return 'Detected' }

    $op = if ([string]::IsNullOrWhiteSpace($Detection.Operator)) { $DefaultOperator } else { $Detection.Operator.Trim() }
    $expected = [string]$Detection.ExpectedValue

    # Prefix, suffix and substring are string comparisons whatever the property type says.
    switch ($op.ToLowerInvariant()) {
        'beginswith' {
            return $(if ($ActualValue.StartsWith($expected, [StringComparison]::OrdinalIgnoreCase)) { 'Detected' } else { 'NotDetected' })
        }
        'endswith' {
            return $(if ($ActualValue.EndsWith($expected, [StringComparison]::OrdinalIgnoreCase)) { 'Detected' } else { 'NotDetected' })
        }
        'contains' {
            return $(if ($ActualValue.IndexOf($expected, [StringComparison]::OrdinalIgnoreCase) -ge 0) { 'Detected' } else { 'NotDetected' })
        }
    }

    if ($Detection.PropertyType -and $Detection.PropertyType.ToString().ToLowerInvariant() -eq 'version') {
        # A version rule that cannot read either side as a version has not been evaluated.
        $actualVersion = $null
        $expectedVersion = $null
        if (-not [version]::TryParse($ActualValue, [ref]$actualVersion)) { return 'Error' }
        if (-not [version]::TryParse($expected, [ref]$expectedVersion)) { return 'Error' }
        return Compare-DetectionOrdered -Comparison $actualVersion.CompareTo($expectedVersion) -Operator $op
    }

    if ($op -ieq 'IsEquals') {
        return $(if ([string]::Equals($ActualValue, $expected, [StringComparison]::OrdinalIgnoreCase)) { 'Detected' } else { 'NotDetected' })
    }
    if ($op -ieq 'NotEquals') {
        return $(if ([string]::Equals($ActualValue, $expected, [StringComparison]::OrdinalIgnoreCase)) { 'NotDetected' } else { 'Detected' })
    }

    return Compare-DetectionOrdered -Comparison ([string]::Compare($ActualValue, $expected, [StringComparison]::OrdinalIgnoreCase)) -Operator $op
}

<#
.SYNOPSIS
    Reads a script rule's process result as a verdict.
.DESCRIPTION
    A shipped detection body reports absence by exiting 0 with no output, so a nonzero exit
    is the script having failed rather than a verdict about the application.
#>
function Get-ScriptDetectionVerdict {
    param(
        [Parameter(Mandatory)][int]$ExitCode,
        [AllowNull()][string]$StdOut,
        [AllowNull()][string]$ScriptLanguage
    )

    if (-not [string]::IsNullOrWhiteSpace($ScriptLanguage) -and $ScriptLanguage.Trim() -ine 'PowerShell') {
        return 'Error'
    }
    if ($ExitCode -ne 0) { return 'Error' }
    return $(if (-not [string]::IsNullOrWhiteSpace($StdOut)) { 'Detected' } else { 'NotDetected' })
}

<#
.SYNOPSIS
    Detected when the file is there, NotDetected when it provably is not, Error otherwise.
.DESCRIPTION
    Test-Path reports false for a path it cannot read, which is not the same as the file
    being absent. A directory carrying the file's name is not the file.
#>
function Get-FilePresence {
    param([Parameter(Mandatory)][string]$Path)

    try {
        $attributes = [System.IO.File]::GetAttributes($Path)
        if ($attributes.HasFlag([System.IO.FileAttributes]::Directory)) { return 'NotDetected' }
        return 'Detected'
    }
    catch [System.IO.FileNotFoundException] { return 'NotDetected' }
    catch [System.IO.DirectoryNotFoundException] { return 'NotDetected' }
    catch { return 'Error' }
}

function Open-DetectionRegistryBase {
    param([Parameter(Mandatory)][object]$Detection)

    $hive = if ($Detection.Hive -eq 'CurrentUser') {
        [Microsoft.Win32.RegistryHive]::CurrentUser
    } else {
        [Microsoft.Win32.RegistryHive]::LocalMachine
    }
    $view = if ($Detection.Is64Bit) {
        [Microsoft.Win32.RegistryView]::Registry64
    } else {
        [Microsoft.Win32.RegistryView]::Registry32
    }
    return [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
}

<#
.SYNOPSIS
    Evaluates one staged detection rule against this machine.
.PARAMETER ScriptRunner
    Optional script block taking the script text and returning a hashtable with ExitCode
    and StdOut. Without one, a script rule is not evaluated here.
#>
function Get-DetectionVerdict {
    param(
        [Parameter(Mandatory)][object]$Detection,
        [scriptblock]$ScriptRunner
    )

    try {
        switch ($Detection.Type) {
            'RegistryKeyValue' {
                if (-not $Detection.RegistryKeyRelative) { return 'Error' }
                # Refused before the key is opened, so a missing key cannot mask a bad rule.
                if (-not (Test-DetectionOperatorSupported -Operator $Detection.Operator -DefaultOperator $script:RegistryDefaultOperator)) {
                    return 'Error'
                }

                $root = Open-DetectionRegistryBase -Detection $Detection
                try {
                    $key = $root.OpenSubKey($Detection.RegistryKeyRelative)
                    if (-not $key) { return 'NotDetected' }
                    try {
                        $valueName = if ($Detection.ValueName) { $Detection.ValueName } else { 'DisplayVersion' }
                        $raw = $key.GetValue($valueName)
                        $actual = if ($null -eq $raw) { $null } else { [string]$raw }
                        return Compare-DetectionValue -ActualValue $actual -Detection $Detection -DefaultOperator $script:RegistryDefaultOperator
                    } finally { $key.Close() }
                } finally { $root.Close() }
            }
            'RegistryKey' {
                if (-not $Detection.RegistryKeyRelative) { return 'Error' }
                $root = Open-DetectionRegistryBase -Detection $Detection
                try {
                    $key = $root.OpenSubKey($Detection.RegistryKeyRelative)
                    if (-not $key) { return 'NotDetected' }
                    $key.Close()
                    return 'Detected'
                } finally { $root.Close() }
            }
            'File' {
                if (-not $Detection.FilePath -or -not $Detection.FileName) { return 'Error' }

                $isVersion = $Detection.PropertyType -and $Detection.PropertyType.ToString().ToLowerInvariant() -eq 'version'
                $comparesValue = $isVersion -and -not [string]::IsNullOrEmpty($Detection.ExpectedValue)

                if ($comparesValue -and -not (Test-DetectionOperatorSupported -Operator $Detection.Operator -DefaultOperator $script:FileDefaultOperator)) {
                    return 'Error'
                }

                $filePath = Join-Path ([Environment]::ExpandEnvironmentVariables($Detection.FilePath)) $Detection.FileName
                $presence = Get-FilePresence -Path $filePath
                if ($presence -ne 'Detected') { return $presence }
                if (-not $comparesValue) { return 'Detected' }

                $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($filePath).FileVersion
                if ([string]::IsNullOrWhiteSpace($fileVersion)) { return 'Error' }
                return Compare-DetectionValue -ActualValue $fileVersion -Detection $Detection -DefaultOperator $script:FileDefaultOperator
            }
            'Script' {
                if ([string]::IsNullOrWhiteSpace($Detection.ScriptText)) { return 'Error' }
                # Refused before the runner is invoked: running another language as
                # PowerShell would execute a body that was never written for it.
                $language = [string]$Detection.ScriptLanguage
                if (-not [string]::IsNullOrWhiteSpace($language) -and $language.Trim() -ine 'PowerShell') {
                    return 'Error'
                }
                if (-not $ScriptRunner) { return 'Error' }
                $run = & $ScriptRunner $Detection.ScriptText
                return Get-ScriptDetectionVerdict -ExitCode ([int]$run.ExitCode) -StdOut ([string]$run.StdOut) -ScriptLanguage $language
            }
            default { return 'Error' }
        }
    }
    catch { return 'Error' }
}

<#
.SYNOPSIS
    Terminates a timed-out detection script and whatever it started.
.DESCRIPTION
    The harness runs under Windows PowerShell, whose Process type carries Kill() with no
    tree overload, so taskkill does the tree. Kill() is the fallback and the wait confirms
    the process actually went away.
#>
function Stop-DetectionScriptProcess {
    param([Parameter(Mandatory)][System.Diagnostics.Process]$Process)

    try {
        & taskkill.exe /T /F /PID $Process.Id 2>&1 | Out-Null
    } catch { }

    if (-not $Process.HasExited) {
        try { $Process.Kill() } catch { }
    }
    try { [void]$Process.WaitForExit(10000) } catch { }
}

<#
.SYNOPSIS
    Runs a detection script body and returns its exit code and stdout.
.DESCRIPTION
    Both output streams are read to completion before the wait, so a script that writes a
    large amount of error output cannot fill a pipe and block. The wait is bounded.
#>
function Invoke-DetectionScript {
    param(
        [Parameter(Mandatory)][string]$ScriptText,
        [int]$TimeoutSeconds = 300
    )

    $scriptFile = Join-Path $env:TEMP ("packr-detect-{0}.ps1" -f [guid]::NewGuid().ToString('N'))
    Set-Content -LiteralPath $scriptFile -Value $ScriptText -Encoding UTF8
    $proc = $null
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'powershell.exe'
        $psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$scriptFile`""
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true

        $proc = [System.Diagnostics.Process]::Start($psi)
        # Drained asynchronously: reading one stream to completion while the other fills
        # its buffer is how this deadlocks.
        $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
        $stderrTask = $proc.StandardError.ReadToEndAsync()

        if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-DetectionScriptProcess -Process $proc
            return @{ ExitCode = -1; StdOut = ''; TimedOut = $true }
        }
        [void]$stderrTask.GetAwaiter().GetResult()
        return @{ ExitCode = $proc.ExitCode; StdOut = $stdoutTask.GetAwaiter().GetResult(); TimedOut = $false }
    }
    finally {
        if ($proc) { $proc.Dispose() }
        Remove-Item -LiteralPath $scriptFile -ErrorAction SilentlyContinue
    }
}
