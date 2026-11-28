using System.Diagnostics;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Cancelling a run: the line it shows, and the child processes it ends. The loop below is
/// the same shape as the loops in the Packages page (check the token between products, record
/// an interrupted product from the caller's own token, settle with Finish), driven by a fake
/// product step so each cancel point can be hit exactly. The processes are real, so the kill
/// by process id is observed rather than assumed.
/// </summary>
// RunChildProcesses.KillAll ends every tracked child of the test process, so a test that calls it ends the children of any test that runs beside it.
// These tests run alone.
[Collection(TrackedChildrenCollection.Name)]
public class RunCancellationTests
{
    private static readonly TimeSpan Seconds5 = TimeSpan.FromSeconds(5);
    // A hosted runner is slower than a developer PC: the kill assertions allow 20 seconds, which is still seconds, not the 5 minutes of the old wait.
    private static readonly TimeSpan KillLimit = TimeSpan.FromSeconds(20);

    private static async Task<PackagingRunTally> RunAsync(int total, Func<int, CancellationToken, Task> product, CancellationToken ct)
    {
        var tally = new PackagingRunTally();
        for (var i = 0; i < total; i++)
        {
            if (ct.IsCancellationRequested) { tally.MarkCancelled(); break; }
            try
            {
                await product(i, ct);
                tally.RecordSuccess();
            }
            catch (OperationCanceledException)
            {
                if (tally.RecordInterruptedOrFailed(ct)) break;
            }
        }
        return tally.Finish(ct);
    }

    [Fact]
    public async Task CancelBeforeTheFirstProductRunsNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ran = 0;

        var tally = await RunAsync(3, (_, _) => { ran++; return Task.CompletedTask; }, cts.Token);

        Assert.Equal(0, ran);
        Assert.Equal("Cancelled: 0 of 3 products done.", tally.CancelledLine(3));
    }

    [Fact]
    public async Task CancelBetweenProductsKeepsTheFinishedOnes()
    {
        using var cts = new CancellationTokenSource();
        var tally = await RunAsync(5, (i, _) => { if (i == 1) cts.Cancel(); return Task.CompletedTask; }, cts.Token);

        Assert.Equal(2, tally.Succeeded);
        Assert.Equal("Cancelled: 2 of 5 products done.", tally.Summarize("Stage", 5, "staged"));
    }

    [Fact]
    public async Task CancelDuringADownloadStopsItWithinSecondsAndDoesNotCountIt()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var sw = Stopwatch.StartNew();
        var run = RunAsync(4, async (i, ct) =>
        {
            if (i < 2) return;
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, cts.Token);

        await started.Task.WaitAsync(Seconds5);
        cts.Cancel();
        var tally = await run.WaitAsync(Seconds5);

        Assert.True(sw.Elapsed < KillLimit, $"took {sw.Elapsed}");
        Assert.Equal(1, tally.Interrupted);
        Assert.Equal("Cancelled: 2 of 4 products done.", tally.CancelledLine(4));
    }

    [Fact]
    public async Task CancelAfterTheLastProductStillReadsAsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var tally = await RunAsync(2, (i, _) => { if (i == 1) cts.Cancel(); return Task.CompletedTask; }, cts.Token);

        Assert.True(tally.Cancelled);
        Assert.Equal("Cancelled: 2 of 2 products done.", tally.CancelledLine(2));
    }

    [Fact]
    public async Task OneProductRunUsesTheSingularWord()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var tally = await RunAsync(1, (_, _) => Task.CompletedTask, cts.Token);

        Assert.Equal("Cancelled: 0 of 1 product done.", tally.CancelledLine(1));
    }

    private static Process StartSleeper()
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        process.Start();
        _ = process.StandardOutput.ReadToEndAsync();
        return process;
    }

    [Fact]
    public async Task CancellingTheTokenKillsTheChildByIdWithinSeconds()
    {
        using var process = StartSleeper();
        RunChildProcesses.Track(process);
        using var cts = new CancellationTokenSource();
        var wait = RunChildProcesses.WaitOrKillAsync(process, TimeSpan.FromMinutes(5), cts.Token);
        var sw = Stopwatch.StartNew();

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);

        Assert.True(process.HasExited);
        Assert.True(sw.Elapsed < KillLimit, $"took {sw.Elapsed}");
        Assert.DoesNotContain(process.Id, RunChildProcesses.Ids);
    }

    [Fact]
    public async Task AnOverrunEndsTheChildAndReportsFalse()
    {
        using var process = StartSleeper();

        var exited = await RunChildProcesses.WaitOrKillAsync(process, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.False(exited);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task KillAllEndsOnlyTrackedChildren()
    {
        using var mine = StartSleeper();
        using var other = StartSleeper();
        try
        {
            RunChildProcesses.Track(mine);

            RunChildProcesses.KillAll();
            RunChildProcesses.KillAll(); // a second call is harmless

            await mine.WaitForExitAsync().WaitAsync(Seconds5);
            Assert.True(mine.HasExited);
            Assert.False(other.HasExited);
        }
        finally
        {
            if (!other.HasExited) other.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task TheSharedRunnerKillsItsChildWhenTheTokenIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var info = new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1");
        var run = BoundedProcessRunner.RunAsync(info, TimeSpan.FromMinutes(5), cts.Token);
        await Task.Delay(500);
        var sw = Stopwatch.StartNew();

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(sw.Elapsed < KillLimit, $"took {sw.Elapsed}");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TrackedChildrenCollection
{
    public const string Name = "Tracked child processes";
}
