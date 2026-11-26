using System.Diagnostics;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.Compliance;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The lab harness scripts stop their processes through a job object (tools/RemoteTestHarness/ProcessJob.ps1). Windows uses process ids again,
/// so an older process can record the id of a harness process as its parent. A stop that follows parent ids, as taskkill /T does, then reaches
/// a process that the harness did not start. These tests run the real scripts in Windows PowerShell with processes of their own.
/// </summary>
public sealed class HarnessProcessJobTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("apd-harness-job-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string RepositoryFile(params string[] parts) => Path.Combine([CatalogServiceTests.RepositoryRoot(), .. parts]);

    /// <summary>
    /// Windows cannot be told to use an id again. The stranger gets the same record in another way: it is created with the harness process as
    /// its named parent while that process is not in the job yet. So the stranger records the id of a harness process as its parent and is not
    /// a member of the job, which is the state of an older process after the harness process got the id of its dead parent.
    /// </summary>
    private const string Stranger = """
        Add-Type -TypeDefinition @'
        using System;
        using System.ComponentModel;
        using System.Diagnostics;
        using System.Runtime.InteropServices;
        using System.Text;
        public static class Stranger
        {
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct StartupInfo
            {
                public int cb; public string r1, r2, r3; public int x, y, xs, ys, xc, yc, fill, flags;
                public short show, cb2; public IntPtr r4, i, o, e;
            }
            [StructLayout(LayoutKind.Sequential)] struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
            [StructLayout(LayoutKind.Sequential)] struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
            [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
            [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
            [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
            [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
            [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);

            public static Process Start(string commandLine, int parentId)
            {
                IntPtr parent = OpenProcess(0x80, false, parentId); // PROCESS_CREATE_PROCESS
                if (parent == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                IntPtr list = Marshal.AllocHGlobal(size);
                IntPtr value = Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    Marshal.WriteIntPtr(value, parent);
                    // PROC_THREAD_ATTRIBUTE_PARENT_PROCESS
                    if (!UpdateProcThreadAttribute(list, 0, new IntPtr(0x00020000), value, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var startup = new StartupInfoEx();
                    startup.Info.cb = Marshal.SizeOf(typeof(StartupInfoEx));
                    startup.Attributes = list;
                    ProcessInformation information;
                    // EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW
                    if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, 0x00080000 | 0x08000000, IntPtr.Zero, null, ref startup, out information))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    var process = Process.GetProcessById(information.ProcessId);
                    IntPtr keep = process.Handle; // opened while the creation handle is open, so it is this process
                    CloseHandle(information.Thread);
                    CloseHandle(information.Process);
                    DeleteProcThreadAttributeList(list);
                    return process;
                }
                finally { Marshal.FreeHGlobal(list); Marshal.FreeHGlobal(value); CloseHandle(parent); }
            }
        }
        '@
        $script:strangers = @()
        $hook = [Action[int]] { param($id) $script:strangers += [Stranger]::Start('ping.exe -n 120 127.0.0.1', $id) }
        function Get-RecordedParent($Process) { [int](Get-CimInstance Win32_Process -Filter "ProcessId=$($Process.Id)").ParentProcessId }
        """;

    private const string StopScenario = """
        param($Helper, $Evaluator, $Dir)
        $ErrorActionPreference = 'Stop'
        . $Helper
        __STRANGER__
        $pidFile = Join-Path $Dir 'child.txt'
        $parent = Join-Path $Dir 'parent.ps1'
        Set-Content -LiteralPath $parent -Encoding Ascii -Value @(
            'param($PidFile)'
            '$c = Start-Process ping.exe -ArgumentList ''-n'',''120'',''127.0.0.1'' -PassThru -WindowStyle Hidden'
            'Set-Content -LiteralPath $PidFile -Value $c.Id'
            'Start-Sleep -Seconds 120')
        try {
            $run = Start-JobProcess -FilePath powershell.exe -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', "`"$parent`"", "`"$pidFile`"") -BeforeJoin $hook
            $deadline = (Get-Date).AddSeconds(90)
            while ((Get-Date) -lt $deadline -and -not ((Test-Path -LiteralPath $pidFile) -and (Get-Content -LiteralPath $pidFile -ErrorAction SilentlyContinue))) { Start-Sleep -Milliseconds 300 }
            # The object of the child is taken while the child runs, so it keeps naming that very process.
            $child = Get-Process -Id ([int](Get-Content -LiteralPath $pidFile))
            $null = $child.Handle
            "strangerRecordsTheRunAsParent=$((Get-RecordedParent $script:strangers[0]) -eq $run.Process.Id)"
            Stop-JobProcess $run
            "parentExited=$($run.Process.WaitForExit(15000))"
            "childExited=$($child.WaitForExit(15000))"
            "strangerAlive=$(-not $script:strangers[0].WaitForExit(3000))"
            Stop-JobProcess $run
            'secondStop=ok'
        }
        finally { foreach ($s in $script:strangers) { if (-not $s.HasExited) { $s.Kill() } } }
        """;

    // The timeout of a detection script is the stop that used taskkill /T. The same function runs here, with a stranger added to its run.
    private const string TimeoutScenario = """
        param($Helper, $Evaluator, $Dir)
        $ErrorActionPreference = 'Stop'
        . $Helper
        . $Evaluator
        __STRANGER__
        $real = ${function:Start-JobProcess}
        function Start-JobProcess {
            param($FilePath, $ArgumentList, $StandardOutput, $StandardError)
            $script:lastRun = & $real @PSBoundParameters -BeforeJoin $hook
            $script:recorded = (Get-RecordedParent $script:strangers[-1]) -eq $script:lastRun.Process.Id
            $script:lastRun
        }
        try {
            $result = Invoke-DetectionScript -ScriptText 'Start-Sleep -Seconds 120' -TimeoutSeconds 3
            "strangerRecordsTheRunAsParent=$($script:recorded)"
            "timedOut=$($result.TimedOut)"
            "scriptExited=$($script:lastRun.Process.WaitForExit(15000))"
            "strangerAlive=$(-not $script:strangers[-1].WaitForExit(3000))"
            $normal = Invoke-DetectionScript -ScriptText '1..2000 | ForEach-Object { [Console]::Error.WriteLine("e" * 200) }; Write-Output "Installed"; exit 7' -TimeoutSeconds 90
            "normalExit=$($normal.ExitCode)"
            "normalOutput=$($normal.StdOut.Trim())"
            "strangerOfTheNormalRunAlive=$(-not $script:strangers[-1].WaitForExit(2000))"
        }
        finally { foreach ($s in $script:strangers) { if (-not $s.HasExited) { $s.Kill() } } }
        """;

    // The install steps run on the lab client from text: no file path of the helper is known there.
    private const string TreeScenario = """
        param($Helper, $Evaluator, $Dir)
        $ErrorActionPreference = 'Stop'
        . ([scriptblock]::Create((Get-Content -LiteralPath $Helper -Raw) + "`r`n" + (Get-Content -LiteralPath $Evaluator -Raw)))
        $marker = Join-Path $Dir 'late.txt'
        $late = Join-Path $Dir 'late.ps1'
        Set-Content -LiteralPath $late -Encoding Ascii -Value @('param($Marker)', 'Start-Sleep -Seconds 4', 'Set-Content -LiteralPath $Marker -Value done')
        $first = Join-Path $Dir 'first.ps1'
        Set-Content -LiteralPath $first -Encoding Ascii -Value @(
            'param($Late, $Marker)'
            'Start-Process powershell.exe -ArgumentList ''-NoProfile'',''-ExecutionPolicy'',''Bypass'',''-File'',"`"$Late`"","`"$Marker`"" -WindowStyle Hidden'
            'exit 5')
        $code = Invoke-JobTree -FilePath powershell.exe -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', "`"$first`"", "`"$late`"", "`"$marker`"")
        "exitCode=$code"
        "waitedForTheChild=$(Test-Path -LiteralPath $marker)"
        """;

    private async Task AssertScenarioAsync(string scenario, params string[] expected)
    {
        var file = Path.Combine(_dir, "scenario.ps1");
        File.WriteAllText(file, scenario.Replace("__STRANGER__", Stranger));
        var start = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file,
            RepositoryFile("tools", "RemoteTestHarness", "ProcessJob.ps1"), RepositoryFile("tools", "RemoteTestHarness", "DetectionEvaluation.ps1"), _dir,
        })
            start.ArgumentList.Add(a);
        WindowsPowerShellHost.ApplyLogonModulePath(start);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await process.WaitForExitAsync(limit.Token);
        var output = await stdout;

        Assert.True(process.ExitCode == 0, await stderr + output);
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in expected)
            Assert.True(lines.Contains(line), $"missing '{line}' in:{Environment.NewLine}{output}");
    }

    [Fact]
    public Task StoppingARunLeavesAProcessThatRecordsTheRunAsItsParent() => AssertScenarioAsync(StopScenario,
        "strangerRecordsTheRunAsParent=True", "parentExited=True", "childExited=True", "strangerAlive=True", "secondStop=ok");

    [Fact]
    public Task ADetectionTimeoutLeavesAProcessThatRecordsTheScriptAsItsParent() => AssertScenarioAsync(TimeoutScenario,
        "strangerRecordsTheRunAsParent=True", "timedOut=True", "scriptExited=True", "strangerAlive=True",
        "normalExit=7", "normalOutput=Installed", "strangerOfTheNormalRunAlive=True");

    [Fact]
    public Task AnInstallStepWaitsForEverythingThatItStarted() => AssertScenarioAsync(TreeScenario, "exitCode=5", "waitedForTheChild=True");

    [Theory]
    [InlineData("tools/RemoteTestHarness/ProcessJob.ps1")]
    [InlineData("tools/RemoteTestHarness/DetectionEvaluation.ps1")]
    [InlineData("tools/RemoteTestHarness/RemoteTestHarness.ps1")]
    [InlineData("tools/lab-tests/check-configmgr-parity/batch.ps1")]
    public void TheHarnessScriptsHaveNoOtherWayToStopAProcess(string name)
    {
        var script = File.ReadAllText(RepositoryFile(name.Split('/')));
        foreach (var forbidden in new[] { "Stop-Process", "taskkill", "ParentProcessId", ".Kill(", "OpenProcess", "Win32_Process", "tsdiscon", "logoff.exe" })
            Assert.DoesNotContain(forbidden, script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"(?m)^\s*(kill|spps)\s", script);
    }
}
