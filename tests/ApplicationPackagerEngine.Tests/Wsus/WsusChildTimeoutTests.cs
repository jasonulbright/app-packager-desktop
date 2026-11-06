using System.Diagnostics;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Wsus;

namespace ApplicationPackagerEngine.Tests.Wsus;

/// <summary>
/// A Windows PowerShell child that never ends held the publish for ever. The host
/// now stops a child that outlives its time limit and the WSUS runner reports it as
/// a failure (PowerShell commit c97d69f).
/// </summary>
public sealed class WsusChildTimeoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apkg-wsus-timeout-" + Guid.NewGuid().ToString("N"));

    public WsusChildTimeoutTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task AChildThatOutlivesItsTimeLimitIsStoppedAndReported()
    {
        var pidFile = Path.Combine(_dir, "child.pid");
        var script = $"[System.IO.File]::WriteAllText('{pidFile}', [string]$PID)\r\nStart-Sleep -Seconds 120";
        var watch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            WindowsPowerShellHost.RunAsync(script, null, default, "ap-timeout-test", timeout: TimeSpan.FromSeconds(4)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"The child ran for {watch.Elapsed}.");
        Assert.Contains("did not finish within 4 seconds and was stopped", ex.Message);
        Assert.True(File.Exists(pidFile), "The child never started.");
        var pid = int.Parse(File.ReadAllText(pidFile));
        Assert.Null(Process.GetProcesses().FirstOrDefault(p => p.Id == pid && p.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task AChildThatFinishesInTimeReturnsItsOutput()
    {
        var run = await WindowsPowerShellHost.RunAsync("Write-Output 'done'", null, default, "ap-timeout-test",
            timeout: TimeSpan.FromSeconds(60));
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("done", run.StdOut);
    }

    [Fact]
    public async Task ACallerCancellationStaysACancellationNotATimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsPowerShellHost.RunAsync("Start-Sleep -Seconds 120", null, cts.Token, "ap-timeout-test", timeout: TimeSpan.FromSeconds(100)));
    }

    [Fact]
    public async Task TheWsusRunnerReportsAStoppedChildAsAFailure()
    {
        var runner = new WsusAdapterRunner(TimeSpan.FromSeconds(7),
            (script, progress, ct, prefix, arguments, stdin, timeout) =>
            {
                Assert.Equal(TimeSpan.FromSeconds(7), timeout);
                throw new TimeoutException("The Windows PowerShell child (ap-wsus) did not finish within 7 seconds and was stopped.");
            });

        var result = await runner.RunAsync("Publish", new JsonObject());

        Assert.False(result.Ok);
        Assert.Equal(WsusErrorKinds.Failed, result.ErrorKind);
        Assert.Contains("did not finish within 7 seconds", result.Error);
    }

    [Fact]
    public void TheWsusRunnerLimitsTheChildToThirtyMinutesByDefault() =>
        Assert.Equal(TimeSpan.FromSeconds(1800), WsusAdapterRunner.DefaultChildTimeout);
}
