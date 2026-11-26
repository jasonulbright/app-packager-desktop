<#
.SYNOPSIS
    Runs the whole engine test suite on a lab client from a build made on another computer, in slices, with a free-memory watchdog.

.DESCRIPTION
    The test output is built on the development computer (dotnet build -c Release) and copied with the repository files; nothing is compiled here.
    The test classes are listed from the test assembly and run in groups, each group in its own test process with its own TRX file, so that the memory
    of one group is free before the next one starts. A group that stays below the free-memory limit is stopped through its job object (ProcessJob.ps1), so only
    what this script started, and it runs again as two halves. The end of the output has one line with the totals and one line for each failed test.

.PARAMETER Root
    The folder with the repository files and the built test output.
.PARAMETER Results
    The folder that receives the TRX files.
#>
param(
    [string]$Root = (Split-Path $PSScriptRoot -Parent | Split-Path -Parent),
    [Parameter(Mandatory)][string]$Results,
    [string]$DotNet = 'C:\tools\dotnet\dotnet.exe',
    [int]$ClassesPerSlice = 24,
    [int]$MinimumFreeMB = 200
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ProcessJob.ps1')
# Full paths, so that the listing and each group read the same build whatever the current folder is.
$Root = (Resolve-Path -LiteralPath $Root).ProviderPath
if (Test-Path -LiteralPath $DotNet) { $DotNet = (Resolve-Path -LiteralPath $DotNet).ProviderPath }
$env:DOTNET_ROOT = Split-Path $DotNet
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
# The messages of an aborted run are matched by their English text.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$dll = Join-Path $Root 'tests\ApplicationPackagerEngine.Tests\bin\Release\net10.0-windows\ApplicationPackagerEngine.Tests.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw "No test assembly at $dll" }
New-Item -ItemType Directory -Force $Results | Out-Null
$Results = (Resolve-Path -LiteralPath $Results).ProviderPath
# A result file of an earlier run must never count for this one.
if (@(Get-ChildItem -LiteralPath $Results -Filter 'slice-*' -File).Count -gt 0) { throw "The results folder holds the files of an earlier run: $Results" }

$listed = & $DotNet vstest $dll --ListTests 2>&1 | ForEach-Object { "$_" }
$methods = @($listed | Where-Object { $_ -match '^\s+ApplicationPackagerEngine\.Tests\.' } | ForEach-Object { ($_.Trim() -split '\(')[0] } | Sort-Object -Unique)
$methodsByClass = @{}
foreach ($method in $methods) {
    $class = $method.Substring(0, $method.LastIndexOf('.'))
    if (-not $methodsByClass.ContainsKey($class)) { $methodsByClass[$class] = New-Object System.Collections.Generic.List[string] }
    $methodsByClass[$class].Add($method)
}
$classes = @($methodsByClass.Keys | Sort-Object)
if ($classes.Count -eq 0) { throw "The test list is empty. First lines: $(($listed | Select-Object -First 5) -join ' | ')" }
"classes: $($classes.Count)"

$queue = New-Object System.Collections.Generic.Queue[object]
for ($i = 0; $i -lt $classes.Count; $i += $ClassesPerSlice) {
    $queue.Enqueue(@($classes[$i..([math]::Min($i + $ClassesPerSlice, $classes.Count) - 1)]))
}

$n = 0; $passed = 0; $failed = 0; $skipped = 0; $total = 0
$failures = New-Object System.Collections.Generic.List[string]
$notRun = New-Object System.Collections.Generic.List[string]
$lowestFree = [int]::MaxValue
$emptyInARow = 0
while ($queue.Count -gt 0) {
    $slice = @($queue.Dequeue())
    $n++
    $trx = Join-Path $Results ("slice-{0:d3}.trx" -f $n)
    $log = Join-Path $Results ("slice-{0:d3}.log" -f $n)
    $filter = ($slice | ForEach-Object { "FullyQualifiedName~$_." }) -join '|'
    $arguments = @('vstest', "`"$dll`"", "`"--TestCaseFilter:$filter`"", "`"--logger:trx;LogFileName=$trx`"", '--Blame:CollectHangDump;TestTimeout=8m;HangDumpType=none')
    # Each group runs in a job of its own: a stop reaches the test process and what it started, and nothing else.
    $run = Start-JobProcess -FilePath $DotNet -ArgumentList $arguments -StandardOutput $log -StandardError "$log.err"
    $stopped = $false
    try {
        $p = $run.Process
        $beat = Get-Date; $started = Get-Date; $low = 0
        while (-not $p.HasExited) {
            Start-Sleep 3
            $free = [int]((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1024)
            if ($free -lt $lowestFree) { $lowestFree = $free }
            # A virtual machine with dynamic memory gets more within seconds, so one low reading does not stop the group.
            if ($free -lt $MinimumFreeMB) { $low++ } else { $low = 0 }
            if ($low -ge 5) { Stop-JobProcess $run; $stopped = $true; "slice $n stopped: free memory stayed under $MinimumFreeMB MB"; break }
            if (((Get-Date) - $started).TotalMinutes -gt 25) { Stop-JobProcess $run; $stopped = $true; break }
            if (((Get-Date) - $beat).TotalSeconds -ge 45) { "slice $n running, free $free MB"; $beat = Get-Date }
        }
        if (-not $stopped) { $p.WaitForExit() }
    }
    # Closing the job also ends a helper that a test left running, and an error or Ctrl+C leaves no test process behind.
    finally { Stop-JobProcess $run }
    $counters = $null
    $ran = $false
    if (-not $stopped -and (Test-Path -LiteralPath $trx)) {
        [xml]$x = Get-Content -LiteralPath $trx -Raw
        $counters = $x.TestRun.ResultSummary.Counters
        # A run that was aborted (a crash of the test host, a hang) still writes a result file with the tests it finished. Such a group is not complete.
        $aborted = @(Select-String -LiteralPath $log, "$log.err" -Pattern 'Test Run Aborted|test run was aborted|host process crashed|inactivity time of').Count -gt 0
        $seen = New-Object System.Collections.Generic.HashSet[string]
        foreach ($r in @($x.TestRun.Results.UnitTestResult | Where-Object { $_ })) { $null = $seen.Add((([string]$r.testName) -split '\(')[0]) }
        $ran = $seen.Count -gt 0
        $missing = @($slice | ForEach-Object { $methodsByClass[$_] } | Where-Object { -not $seen.Contains($_) })
        if ($aborted -or $missing.Count -gt 0) {
            "slice $n is not complete (aborted: $aborted, tests without a result: $($missing.Count)$(if ($missing.Count) { ', first ' + $missing[0] }))"
            # A failure in an incomplete group stays in the report, also when the group runs again.
            foreach ($r in @($x.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' })) { $failures.Add("$($r.testName) :: failed in an incomplete group") }
            $counters = $null
        }
    }
    # Only groups in which no test ran count toward the stop; an incomplete group with results is progress.
    if ($counters -or $ran) { $emptyInARow = 0 } else { $emptyInARow++ }
    if ($emptyInARow -ge 8) { throw "Eight groups in a row gave no result. The last log: $((Get-Content -LiteralPath $log -Tail 8) -join ' | ') $((Get-Content -LiteralPath "$log.err" -Tail 4) -join ' | ')" }
    if (-not $counters) {
        if ($slice.Count -gt 1) {
            $half = [int][math]::Ceiling($slice.Count / 2)
            $queue.Enqueue(@($slice[0..($half - 1)])); $queue.Enqueue(@($slice[$half..($slice.Count - 1)]))
            "slice $n gave no result (stopped: $stopped); it runs again in two halves"
        }
        else { $notRun.Add($slice[0]); "slice $n gave no result for $($slice[0])" }
        continue
    }
    $passed += [int]$counters.passed; $failed += [int]$counters.failed; $total += [int]$counters.total
    $skipped += [int]$counters.total - [int]$counters.passed - [int]$counters.failed
    foreach ($r in @($x.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' })) {
        $message = ([string]$r.Output.ErrorInfo.Message -replace '\s+', ' ')
        $failures.Add("$($r.testName) :: $($message.Substring(0, [math]::Min(300, $message.Length)))")
    }
    "slice $n done: $($counters.passed) passed, $($counters.failed) failed of $($counters.total)"
}
"SUITE total=$total passed=$passed failed=$failed other=$skipped classes=$($classes.Count) classesWithoutResult=$($notRun.Count) lowestFreeMB=$lowestFree"
foreach ($f in $failures) { "FAILED $f" }
foreach ($c in $notRun) { "NORESULT $c" }
