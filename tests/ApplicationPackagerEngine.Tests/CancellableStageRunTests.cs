using ApplicationPackagerEngine.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Cancelling the stage of a dropped installer or a bring-your-own application (Q-69). The stage itself is a fake
/// runner, so each cancel point is hit exactly. The status line is the line of every other cancelled run.
/// </summary>
public class CancellableStageRunTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static StagingResult Staged() => new() { Success = true, ContentPath = "C:\\stage", ManifestPath = "C:\\stage\\stage-manifest.json" };

    [Fact]
    public async Task AStageThatFinishesIsASuccessAndNotCancelled()
    {
        using var cts = new CancellationTokenSource();
        var outcome = await CancellableStageRun.RunAsync(_ => Task.FromResult(Staged()), cts.Token);

        Assert.False(outcome.Cancelled);
        Assert.True(outcome.Result!.Success);
        Assert.Equal(1, outcome.Tally.Succeeded);
    }

    [Fact]
    public async Task AFailedStageIsAFailureAndNotCancelled()
    {
        using var cts = new CancellationTokenSource();
        var outcome = await CancellableStageRun.RunAsync(_ => Task.FromResult(new StagingResult { Success = false, ErrorMessage = "no disk" }), cts.Token);

        Assert.False(outcome.Cancelled);
        Assert.Equal("no disk", outcome.ErrorMessage);
        Assert.Equal(1, outcome.Tally.Failed);
    }

    [Fact]
    public async Task CancelDuringTheStageEndsItAndShowsTheCancelledLine()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var run = CancellableStageRun.RunAsync(async ct =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Staged();
        }, cts.Token);

        await started.Task.WaitAsync(Limit);
        cts.Cancel();
        var outcome = await run.WaitAsync(Limit);

        Assert.True(outcome.Cancelled);
        Assert.Null(outcome.Result);
        Assert.Equal(1, outcome.Tally.Interrupted);
        Assert.Equal("Cancelled: 0 of 1 product done.", outcome.Tally.Summarize("Stage", 1, "staged"));
    }

    [Fact]
    public async Task TheRunnerReceivesTheCallersToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        CancellationToken seen = default;
        var outcome = await CancellableStageRun.RunAsync(ct => { seen = ct; ct.ThrowIfCancellationRequested(); return Task.FromResult(Staged()); }, cts.Token);

        Assert.Equal(cts.Token, seen);
        Assert.True(outcome.Cancelled);
    }

    [Fact]
    public async Task ATimeoutInsideTheServiceIsAFailureNotACancel()
    {
        using var cts = new CancellationTokenSource();
        var outcome = await CancellableStageRun.RunAsync(_ => throw new TaskCanceledException("The request timed out."), cts.Token);

        Assert.False(outcome.Cancelled);
        Assert.Equal(1, outcome.Tally.Failed);
        Assert.Equal("The request timed out.", outcome.ErrorMessage);
    }

    [Fact]
    public void TheStageSitesOfThePackagesPageRunUnderTheOperationToken()
    {
        var root = ApplicationPackagerEngine.Tests.Compliance.CatalogServiceTests.RepositoryRoot();
        var folder = Path.Combine(root, "src", "ApplicationPackagerUI", "ViewModels", "Pages");
        foreach (var file in new[] { "PackagesViewModel.cs", "PackagesViewModel.Byo.cs" })
        {
            var text = File.ReadAllText(Path.Combine(folder, file));
            Assert.Contains("CancellableStageRun.RunAsync", text);
            Assert.DoesNotContain("ByoStaging.StageAsync(_stagingService, plan, settings.DownloadRoot, line => AddLogLine($\"  {line}\"), CancellationToken.None", text);
        }
    }
}
