using System.Diagnostics;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.DetectionTester;

/// <summary>Real Windows PowerShell child processes.</summary>
public sealed class DetectionScriptHostTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task StdoutStderrAndTheExitCodeAreCaptured()
    {
        var run = await new WindowsPowerShellDetectionScriptHost().RunAsync(
            "Write-Output 'Installed'\n[Console]::Error.WriteLine('warned')\nexit 3", Generous, CancellationToken.None);

        Assert.Equal("Installed", run.StdOut);
        Assert.Contains("warned", run.StdErr);
        Assert.Equal(3, run.ExitCode);
        Assert.False(run.TimedOut);
        Assert.False(run.OutputTruncated);
    }

    [Fact]
    public async Task AScriptThatPrintsNothingExitsZeroWithEmptyOutput()
    {
        var run = await new WindowsPowerShellDetectionScriptHost().RunAsync("$x = 1", Generous, CancellationToken.None);

        Assert.Equal("", run.StdOut);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public async Task WriteHostReachesStdout()
    {
        var run = await new WindowsPowerShellDetectionScriptHost().RunAsync("Write-Host 'found'", Generous, CancellationToken.None);

        Assert.Equal("found", run.StdOut);
    }

    [Fact]
    public async Task AScriptThatReadsTheHostGetsEndOfInputInsteadOfWaiting()
    {
        var run = await new WindowsPowerShellDetectionScriptHost().RunAsync(
            "$a = Read-Host 'Name'\n$b = [Console]::ReadLine()\nif ($a -or $b) { 'got input' } else { 'no input' }",
            TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(run.TimedOut);
        Assert.Equal("no input", run.StdOut);
    }

    [Fact]
    public async Task ATerminatingErrorIsANonZeroExitWithTheErrorOnStderr()
    {
        var run = await new WindowsPowerShellDetectionScriptHost().RunAsync("throw 'broken rule'", Generous, CancellationToken.None);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("broken rule", run.StdErr);
    }

    [Fact]
    public async Task OutputPastTheLimitIsCutAndTheRunStillEnds()
    {
        var run = await new WindowsPowerShellDetectionScriptHost(outputLimit: 1000).RunAsync(
            "Write-Output ('x' * 200000)", Generous, CancellationToken.None);

        Assert.True(run.OutputTruncated);
        Assert.True(run.StdOut.Length <= 1000);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public async Task TheTimeoutEndsTheProcessTreeAndReturnsATimedOutRun()
    {
        const string script = """
            $child = Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile','-Command','Start-Sleep 120' -WindowStyle Hidden -PassThru
            [Console]::Out.WriteLine($child.Id)
            [Console]::Out.Flush()
            Start-Sleep 120
            """;
        var watch = Stopwatch.StartNew();

        var run = await new WindowsPowerShellDetectionScriptHost().RunAsync(script, TimeSpan.FromSeconds(8), CancellationToken.None);

        Assert.True(run.TimedOut);
        Assert.Null(run.ExitCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60));
        Assert.True(int.TryParse(run.StdOut.Trim(), out var childId), $"stdout: '{run.StdOut}'");
        Assert.True(HasExited(childId));
    }

    [Fact]
    public async Task CancellationEndsTheRunAndThrows()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsPowerShellDetectionScriptHost().RunAsync("Start-Sleep 120", Generous, cts.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task TheScriptFileIsRemovedAfterTheRun()
    {
        var before = Directory.GetFiles(Path.GetTempPath(), "ap-detection-*.ps1").ToHashSet();

        await new WindowsPowerShellDetectionScriptHost().RunAsync("'x'", Generous, CancellationToken.None);

        var after = Directory.GetFiles(Path.GetTempPath(), "ap-detection-*.ps1").Where(f => !before.Contains(f));
        Assert.Empty(after);
    }

    private static bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(10_000);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
