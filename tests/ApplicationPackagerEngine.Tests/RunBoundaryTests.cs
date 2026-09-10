using System.Diagnostics;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The summaries the operator reads are only as honest as the results feeding them.
/// Staging turned a cancelled run into an ordinary failure, so the loop above it saw a
/// failed item, finished the batch and let One Click carry on into the next phase.
///
/// These drive the production helpers the loops call and the staging entry point itself.
/// The loops in the view model are not exercised here; the test project references only
/// the engine.
/// </summary>
public sealed class RunBoundaryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "packr-boundary-" + Guid.NewGuid().ToString("N"));

    public RunBoundaryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static StagingService Service() =>
        new(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(),
            new WrapperGeneratorService(), new ManifestService());

    // ── Staging cancellation ────────────────────────────────────

    /// <summary>
    /// A token already cancelled stops at the entry check, before any pipeline runs.
    /// </summary>
    [Fact]
    public async Task AStageRequestedWithACancelledTokenDoesNoWork()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service().StageAsync(DefinitionFixture.Load("winrar.json"), _root, null, cts.Token, "7.23"));

        // Nothing was created for a run that never started.
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    /// <summary>
    /// Cancelled from inside the pipeline body. The callback cancels and then throws, so
    /// the cancellation is raised from within the try before any scraper or download can
    /// start. That is precisely the catch that used to convert cancellation into a failed
    /// result, and it does not depend on a scraper checking the token.
    /// </summary>
    [Fact]
    public async Task ACancellationRaisedInsideThePipelineStillThrows()
    {
        using var cts = new CancellationTokenSource();
        var progressSeen = 0;

        void OnProgress(string _)
        {
            progressSeen++;
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service().StageAsync(
                DefinitionFixture.Load("winrar.json"), _root, OnProgress, cts.Token, "7.23"));

        // Exactly one progress line ran: the throw happened on the first callback, inside
        // the try, with no network work of any kind.
        Assert.Equal(1, progressSeen);
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    /// <summary>A staged result is never how cancellation is reported.</summary>
    [Fact]
    public async Task CancellationIsNeverReportedAsAStagingResult()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var thrown = await Record.ExceptionAsync(() =>
            Service().StageAsync(DefinitionFixture.Load("winrar.json"), _root, null, cts.Token, "7.23"));

        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
    }

    // ── Classifying an item that produced no result ─────────────

    /// <summary>
    /// A null or failed result while the operator's token is cancelled is an interrupted
    /// item, not a genuine failure, and it stops the run.
    /// </summary>
    [Fact]
    public void AFailedResultUnderACancelledTokenIsAnInterruption()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var tally = new PackagingRunTally();

        var stop = tally.RecordInterruptedOrFailed(cts.Token);

        Assert.True(stop);
        Assert.Equal(1, tally.Interrupted);
        Assert.Equal(0, tally.Failed);
        Assert.True(tally.Cancelled);
    }

    /// <summary>
    /// The same result with the operator's token untouched is that item failing. An HTTP
    /// timeout throws the same exception type as a cancellation, so the token is what
    /// separates them.
    /// </summary>
    [Fact]
    public void ATimeoutWithoutUserCancellationIsAnItemFailure()
    {
        using var cts = new CancellationTokenSource();
        var tally = new PackagingRunTally();

        var stop = tally.RecordInterruptedOrFailed(cts.Token);

        Assert.False(stop);
        Assert.Equal(1, tally.Failed);
        Assert.Equal(0, tally.Interrupted);
        Assert.False(tally.Cancelled);
    }

    /// <summary>A batch keeps going through several item timeouts and still completes.</summary>
    [Fact]
    public void RepeatedItemTimeoutsDoNotCancelTheBatch()
    {
        using var cts = new CancellationTokenSource();
        var tally = new PackagingRunTally();

        for (var i = 0; i < 3; i++) Assert.False(tally.RecordInterruptedOrFailed(cts.Token));

        Assert.False(tally.Cancelled);
        Assert.Equal("Latest check complete. 0 of 3 checked, 3 failed.",
            tally.Finish(cts.Token).Summarize("Latest check", 3, "checked"));
    }

    // ── Finalizing the run ──────────────────────────────────────

    /// <summary>
    /// The last item can succeed after the operator cancelled, especially on a cached or
    /// synchronous path. The success stands and the run is still cancelled.
    /// </summary>
    [Fact]
    public void ALateSuccessAfterCancellationStillCancelsTheRun()
    {
        using var cts = new CancellationTokenSource();
        var tally = new PackagingRunTally();
        tally.RecordSuccess();
        tally.RecordSuccess();

        // The operator cancels while the last item is already finishing successfully.
        cts.Cancel();
        tally.RecordSuccess();

        tally.Finish(cts.Token);

        Assert.Equal(3, tally.Succeeded);
        Assert.True(tally.Cancelled);
        Assert.False(tally.AllSucceeded(3));
        Assert.Equal("Latest check cancelled. 3 of 3 checked.",
            tally.Summarize("Latest check", 3, "checked"));
    }

    [Fact]
    public void FinishLeavesAnUncancelledRunAlone()
    {
        using var cts = new CancellationTokenSource();
        var tally = new PackagingRunTally();
        tally.RecordSuccess();

        tally.Finish(cts.Token);

        Assert.False(tally.Cancelled);
        Assert.True(tally.AllSucceeded(1));
    }

    [Fact]
    public void FinishReturnsTheSameTallyForChaining()
    {
        var tally = new PackagingRunTally();

        Assert.Same(tally, tally.Finish(CancellationToken.None));
    }

    /// <summary>
    /// Cancellation on the last item still has to mark the run cancelled, or One Click
    /// reads a full batch and starts the next phase.
    /// </summary>
    [Fact]
    public void CancellationOnTheLastItemStillCancelsTheRun()
    {
        using var cts = new CancellationTokenSource();
        var tally = new PackagingRunTally();
        tally.RecordSuccess();
        tally.RecordSuccess();

        cts.Cancel();
        tally.RecordInterruptedOrFailed(cts.Token);

        Assert.True(tally.Finish(cts.Token).Cancelled);
        Assert.False(tally.AllSucceeded(3));
        Assert.Equal("Stage cancelled. 2 of 3 staged, 1 interrupted.", tally.Summarize("Stage", 3, "staged"));
    }

    // ── The gate One Click reads ────────────────────────────────

    /// <summary>
    /// One Click replaces the footer with a conclusion of its own only when the run earned
    /// it. A batch whose lookups all failed has established nothing about currency.
    /// </summary>
    [Fact]
    public void ABatchWhereEveryLookupFailedHasNotEarnedAConclusion()
    {
        var tally = new PackagingRunTally();
        for (var i = 0; i < 4; i++) tally.RecordFailure();

        Assert.False(tally.AllSucceeded(4));
        Assert.Equal("0 of 4 checked, 4 failed", tally.Describe(4, "checked"));
    }

    [Fact]
    public void AMixedBatchHasNotEarnedAConclusionEither()
    {
        var tally = new PackagingRunTally();
        tally.RecordSuccess();
        tally.RecordFailure();

        Assert.False(tally.AllSucceeded(2));
    }

    [Fact]
    public void AWholeCleanBatchEarnsItsConclusion()
    {
        var tally = new PackagingRunTally();
        for (var i = 0; i < 3; i++) tally.RecordSuccess();

        Assert.True(tally.AllSucceeded(3));
    }

    [Fact]
    public void AnUnreachedItemMeansTheBatchIsIncomplete()
    {
        var tally = new PackagingRunTally();
        tally.RecordSuccess();

        Assert.False(tally.AllSucceeded(3));
    }

    // ── Bounded child processes ─────────────────────────────────

    private static ProcessStartInfo PowerShell(string command)
    {
        var info = new ProcessStartInfo { FileName = "powershell.exe" };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(command);
        return info;
    }

    /// <summary>
    /// A child that records its own process id and then sleeps. The test can then follow
    /// that exact process rather than counting every powershell on the machine, where an
    /// unrelated one would either fake a failure or hide a real orphan.
    /// </summary>
    private ProcessStartInfo SleepingChildRecordingItsPid(string pidFile, int seconds) =>
        PowerShell($"[IO.File]::WriteAllText('{pidFile.Replace("'", "''")}', $PID); Start-Sleep -Seconds {seconds}");

    private static async Task<int> WaitForRecordedPid(string pidFile)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var text = File.ReadAllText(pidFile).Trim();
                if (text.Length > 0 && int.TryParse(text, out var pid)) return pid;
            }
            catch (IOException) { }
            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException("the child never recorded its process id");
    }

    /// <summary>True when that exact process is gone, rather than any process like it.</summary>
    private static async Task<bool> ProcessHasGone(int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var found = Process.GetProcessById(pid);
                if (found.HasExited) return true;
            }
            catch (ArgumentException)
            {
                // No process carries that id any more.
                return true;
            }
            await Task.Delay(50);
        }

        return false;
    }

    [Fact]
    public async Task ANormalChildProcessReturnsItsOutputAndExitCode()
    {
        var run = await BoundedProcessRunner.RunAsync(
            PowerShell("Write-Output 'NOT_FOUND'; exit 0"), TimeSpan.FromSeconds(60));

        Assert.False(run.TimedOut);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("NOT_FOUND", run.StdOut);
    }

    [Fact]
    public async Task ANonzeroExitIsReportedRatherThanThrown()
    {
        var run = await BoundedProcessRunner.RunAsync(
            PowerShell("Write-Output 'x'; exit 7"), TimeSpan.FromSeconds(60));

        Assert.False(run.TimedOut);
        Assert.Equal(7, run.ExitCode);
    }

    /// <summary>
    /// A run that overruns its own timeout is that run failing, not the operator stopping
    /// a batch, so it reports rather than throws and the child is actually terminated.
    /// </summary>
    [Fact]
    public async Task AnOverrunningChildIsTerminatedAndReportedAsATimeout()
    {
        var pidFile = Path.Combine(_root, "timeout-child.pid");
        var stopwatch = Stopwatch.StartNew();

        var runTask = BoundedProcessRunner.RunAsync(
            SleepingChildRecordingItsPid(pidFile, 45), TimeSpan.FromSeconds(3));

        var childPid = await WaitForRecordedPid(pidFile);
        var run = await runTask;
        stopwatch.Stop();

        Assert.True(run.TimedOut);
        Assert.Equal(-1, run.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"the wait was not bounded ({stopwatch.Elapsed.TotalSeconds:0}s)");

        // That exact child is gone rather than abandoned.
        Assert.True(await ProcessHasGone(childPid),
            $"the timed-out child (pid {childPid}) outlived the run");
    }

    /// <summary>The operator's own cancellation propagates instead of reporting a timeout.</summary>
    [Fact]
    public async Task TheCallersCancellationPropagates()
    {
        var pidFile = Path.Combine(_root, "cancelled-child.pid");
        using var cts = new CancellationTokenSource();

        var runTask = BoundedProcessRunner.RunAsync(
            SleepingChildRecordingItsPid(pidFile, 45), TimeSpan.FromSeconds(120), cts.Token);

        var childPid = await WaitForRecordedPid(pidFile);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        Assert.True(await ProcessHasGone(childPid),
            $"the cancelled child (pid {childPid}) outlived the run");
    }

    [Fact]
    public async Task AnAlreadyCancelledRunStartsNothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedProcessRunner.RunAsync(PowerShell("Write-Output 'x'"), TimeSpan.FromSeconds(60), cts.Token));
    }

    /// <summary>
    /// Disposing the process handle ends the wait but not curl, which carried on filling
    /// the file after the caller had given up. Observe this transfer's socket closing,
    /// without counting unrelated curl processes or relying only on a deleted fragment.
    /// </summary>
    [Fact]
    public async Task ACancelledDownloadTerminatesTheTransferBeforeItReturns()
    {
        var target = Path.Combine(_root, "payload.bin");
        using var server = new SlowLoopbackServer();
        using var downloadStop = new CancellationTokenSource();

        var download = new DownloadService()
            .DownloadAsync(server.Url, target, retryCount: 0, ct: downloadStop.Token);

        // The transfer writes to a temporary sibling until it completes.
        await server.WaitUntilSending();
        var partFile = await WaitForPartFile();

        var stopwatch = Stopwatch.StartNew();
        await downloadStop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download)
            .WaitAsync(TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        // Cancellation returns promptly because the transfer is ended, not because it was
        // waited out. Without terminating, reading the child's stderr to completion blocks
        // until curl finishes the whole transfer on its own.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"cancellation waited for the transfer instead of ending it ({stopwatch.Elapsed.TotalSeconds:0}s)");

        await server.WaitUntilTransferStops();

        Assert.False(File.Exists(partFile), "the interrupted fragment was left on disk");
        Assert.False(File.Exists(target));
    }

    private async Task<string> WaitForPartFile()
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var found = Directory.GetFiles(_root, "*.part");
            if (found.Length > 0 && new FileInfo(found[0]).Length > 0) return found[0];
            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException("the download never began writing");
    }

    /// <summary>
    /// Staging skips the download when the installer is already there, so a truncated file
    /// left at the destination would be packaged as a cached payload.
    /// </summary>
    [Fact]
    public async Task ACancelledDownloadLeavesNoCachedInstaller()
    {
        var target = Path.Combine(_root, "installer.exe");
        using var server = new SlowLoopbackServer();
        using var downloadStop = new CancellationTokenSource();

        var download = new DownloadService()
            .DownloadAsync(server.Url, target, retryCount: 0, ct: downloadStop.Token);

        await server.WaitUntilSending();
        await downloadStop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);

        Assert.False(File.Exists(target), "a cancelled transfer left a file the next stage would reuse");
        Assert.Empty(Directory.GetFiles(_root, "*.part"));
    }

    /// <summary>A transfer that never completes must not replace a payload already on disk.</summary>
    [Fact]
    public async Task ACancelledDownloadDoesNotOverwriteAnExistingPayload()
    {
        var target = Path.Combine(_root, "installer.exe");
        await File.WriteAllTextAsync(target, "the good payload from an earlier run");
        using var server = new SlowLoopbackServer();
        using var downloadStop = new CancellationTokenSource();

        var download = new DownloadService()
            .DownloadAsync(server.Url, target, retryCount: 0, ct: downloadStop.Token);

        await server.WaitUntilSending();
        await downloadStop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);

        Assert.Equal("the good payload from an earlier run", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(_root, "*.part"));
    }

    /// <summary>A truncated HTTP response must not replace a previously complete file.</summary>
    [Fact]
    public async Task ATruncatedResponseDoesNotReplaceAnExistingPayload()
    {
        var target = Path.Combine(_root, "installer.exe");
        await File.WriteAllTextAsync(target, "existing complete payload");
        using var server = new SlowLoopbackServer("incomplete response", truncateResponse: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DownloadService().DownloadAsync(server.Url, target, retryCount: 0));

        Assert.Equal("existing complete payload", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(_root, "*.part"));
    }

    /// <summary>A completed transfer replaces the previous file in full.</summary>
    [Fact]
    public async Task ACompletedDownloadPublishesTheWholeFile()
    {
        var target = Path.Combine(_root, "installer.exe");
        await File.WriteAllTextAsync(target, "previous payload");
        var payload = new string('p', 40000);
        using var server = new SlowLoopbackServer(completeBody: payload);

        await new DownloadService().DownloadAsync(server.Url, target, retryCount: 0);

        Assert.True(File.Exists(target));
        Assert.Equal(payload, await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(_root, "*.part"));
    }

    /// <summary>
    /// A child writing heavily to stderr must not fill a pipe and block, which is what an
    /// undrained stream does.
    /// </summary>
    [Fact]
    public async Task ANoisyStandardErrorDoesNotBlockTheRun()
    {
        var run = await BoundedProcessRunner.RunAsync(
            PowerShell("1..2000 | ForEach-Object { [Console]::Error.WriteLine('e' * 200) }; Write-Output 'done'; exit 0"),
            TimeSpan.FromSeconds(60));

        Assert.False(run.TimedOut);
        Assert.Contains("done", run.StdOut);
        Assert.Contains("eee", run.StdErr);
    }
}
