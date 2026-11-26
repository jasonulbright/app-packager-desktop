using System.Diagnostics;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>A server that stops sending must not hold a download: curl ends a transfer with no data after the stall limit.</summary>
public sealed class DownloadStallLimitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apd-stall-" + Guid.NewGuid().ToString("N"));

    public DownloadStallLimitTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => TestCleanup.DeleteDirectory(_dir);

    [Fact]
    public async Task ATransferThatStopsSendingFailsAndLeavesNoFile()
    {
        using var server = new SlowLoopbackServer(stall: true);
        var file = Path.Combine(_dir, "payload.bin");
        // Without the stall limit the download never ends, so the token is what stops the test then.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var clock = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DownloadService(stallSeconds: 3).DownloadAsync(server.Url, file, retryCount: 0, ct: guard.Token));

        Assert.StartsWith("Download failed after 1 attempts:", error.Message);
        Assert.False(guard.IsCancellationRequested, $"the download ended only after {clock.Elapsed}");
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task ARefreshOfACachedDownloadThatStopsSendingEndsAndKeepsTheCachedFile()
    {
        using var server = new SlowLoopbackServer(stall: true);
        var cache = Path.Combine(_dir, "cache");
        var target = Path.Combine(_dir, "payload.bin");
        Directory.CreateDirectory(cache);
        var cached = Path.Combine(cache, SharedDownloadCache.Key(server.Url, "payload.bin"));
        File.WriteAllText(cached, "cached");
        File.SetLastWriteTimeUtc(cached, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        await SharedDownloadCache.FetchAsync(new DownloadService(stallSeconds: 3), server.Url, target, cache, ct: guard.Token);

        Assert.False(guard.IsCancellationRequested, "the refresh did not end by itself");
        Assert.Equal("cached", File.ReadAllText(target));
    }
}
