using System.Diagnostics;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.Compliance;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The lab suite script stops its test processes through a job object (tools/lab-tests/ProcessJob.ps1). A first form of the script walked parent
/// process ids and ended the signed-in session of a lab client. These tests run the real helper in Windows PowerShell with processes of their own.
/// </summary>
public sealed class LabSuiteProcessJobTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("apd-job-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string LabFile(string name) => Path.Combine(CatalogServiceTests.RepositoryRoot(), "tools", "lab-tests", name);

    private const string Scenario = """
        param($Helper, $Dir)
        $ErrorActionPreference = 'Stop'
        . $Helper
        $sleep = @('-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 120')
        # A process that the run did not start: it must survive.
        $bystander = Start-Process powershell.exe -ArgumentList $sleep -PassThru -WindowStyle Hidden
        $pidFile = Join-Path $Dir 'child.txt'
        $parent = Join-Path $Dir 'parent.ps1'
        # The child starts after the parent had time to join the job.
        $lines = @(
            'param($PidFile)'
            'Start-Sleep -Seconds 3'
            '$c = Start-Process powershell.exe -ArgumentList ''-NoProfile'',''-NonInteractive'',''-Command'',''Start-Sleep -Seconds 120'' -PassThru -WindowStyle Hidden'
            'Set-Content -LiteralPath $PidFile -Value $c.Id'
            'Start-Sleep -Seconds 120')
        Set-Content -LiteralPath $parent -Encoding Ascii -Value $lines
        try {
            $run = Start-JobProcess -FilePath powershell.exe -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', "`"$parent`"", "`"$pidFile`"")
            $deadline = (Get-Date).AddSeconds(60)
            while ((Get-Date) -lt $deadline -and -not ((Test-Path -LiteralPath $pidFile) -and (Get-Content -LiteralPath $pidFile -ErrorAction SilentlyContinue))) { Start-Sleep -Milliseconds 300 }
            # The object of the child is taken while the child runs, so it keeps naming that very process.
            $child = Get-Process -Id ([int](Get-Content -LiteralPath $pidFile))
            $null = $child.Handle
            Stop-JobProcess $run
            "parentExited=$($run.Process.WaitForExit(15000))"
            "childExited=$($child.WaitForExit(15000))"
            Start-Sleep -Seconds 2
            "bystanderAlive=$(-not $bystander.HasExited)"
            Stop-JobProcess $run
            'secondStop=ok'
        }
        finally { if (-not $bystander.HasExited) { $bystander.Kill() } }
        """;

    [Fact]
    public async Task StoppingARunEndsItsProcessesAndTheirChildrenAndNothingElse()
    {
        var scenario = Path.Combine(_dir, "scenario.ps1");
        File.WriteAllText(scenario, Scenario);
        var start = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scenario, LabFile("ProcessJob.ps1"), _dir })
            start.ArgumentList.Add(a);
        WindowsPowerShellHost.ApplyLogonModulePath(start);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(limit.Token);
        var output = await stdout;

        Assert.True(process.ExitCode == 0, await stderr + output);
        foreach (var expected in new[] { "parentExited=True", "childExited=True", "bystanderAlive=True", "secondStop=ok" })
            Assert.Contains(expected, output);
    }

    [Fact]
    public void TheSuiteScriptHasNoOtherWayToStopAProcess()
    {
        var script = File.ReadAllText(LabFile("Run-LabSuite.ps1"));
        foreach (var forbidden in new[] { "Stop-Process", "taskkill", "ParentProcessId", ".Kill(", "Terminate" })
            Assert.DoesNotContain(forbidden, script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Start-JobProcess", script);
        Assert.Contains("Stop-JobProcess", script);
    }
}
