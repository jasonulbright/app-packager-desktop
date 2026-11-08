using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The shared cache entry lock waits only for another holder, and only for a bounded
/// time; any other I/O error fails at once. Each failure names the cache entry.
/// </summary>
public sealed class SharedDownloadCacheLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-cache-lock-" + Guid.NewGuid().ToString("N"));

    public SharedDownloadCacheLockTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task AnEntryHeldByAnotherStageStopsTheWaitAfterTheBoundAndNamesTheEntry()
    {
        var cached = Path.Combine(_root, "0123456789abcdef-setup.exe");
        using var holder = new FileStream(cached + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SharedDownloadCache.AcquireEntryLockAsync(cached, CancellationToken.None, TimeSpan.FromMilliseconds(400)));

        Assert.Contains(cached, ex.Message);
        Assert.Contains("stayed locked", ex.Message);
    }

    /// <summary>
    /// A clock that moves forward by a fixed step at every read and runs a step of the
    /// simulated download before it answers, so the waiter's elapsed time and the holder's
    /// progress advance together, independent of thread scheduling.
    /// </summary>
    private sealed class SteppingClock(TimeSpan step, Action onRead) : TimeProvider
    {
        private long _now;
        public int Reads;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            Reads++;
            onRead();
            var now = _now;
            _now += step.Ticks;
            return now;
        }
    }

    [Fact]
    public async Task AHolderWhoseDownloadKeepsGrowingIsWaitedForPastTheBound()
    {
        var cached = Path.Combine(_root, "0123456789abcdef-large.exe");
        var holder = new FileStream(cached + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var part = $"{cached}.{Guid.NewGuid():N}.part";
        var bound = TimeSpan.FromSeconds(1);
        var appends = 0;
        var clock = new SteppingClock(TimeSpan.FromMilliseconds(600), () =>
        {
            if (appends < 10)
            {
                File.AppendAllText(part, new string('x', 1024));
                appends++;
            }
            else if (File.Exists(part))
            {
                File.Move(part, cached);
                holder.Dispose();
            }
        });

        try
        {
            await using (await SharedDownloadCache.AcquireEntryLockAsync(cached, CancellationToken.None, bound, clock))
                Assert.True(File.Exists(cached));
        }
        finally { holder.Dispose(); }

        Assert.True(TimeSpan.FromMilliseconds(600) * (clock.Reads - 1) > bound * 5, $"reads {clock.Reads}");
    }

    [Fact]
    public async Task AHolderWithNoProgressFailsOnceTheClockPassesTheBound()
    {
        var cached = Path.Combine(_root, "0123456789abcdef-quiet.exe");
        using var holder = new FileStream(cached + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.WriteAllText($"{cached}.{Guid.NewGuid():N}.part", "partial");
        var clock = new SteppingClock(TimeSpan.FromMilliseconds(600), () => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SharedDownloadCache.AcquireEntryLockAsync(cached, CancellationToken.None, TimeSpan.FromSeconds(1), clock));

        Assert.Equal(3, clock.Reads);
    }

    [Fact]
    public async Task TheStallMessageStatesTheBound()
    {
        var cached = Path.Combine(_root, "0123456789abcdef-stalled.exe");
        using var holder = new FileStream(cached + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.WriteAllText($"{cached}.{Guid.NewGuid():N}.part", "partial");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SharedDownloadCache.AcquireEntryLockAsync(cached, CancellationToken.None, TimeSpan.FromMilliseconds(300)));

        Assert.Contains("no download progress", ex.Message);
        Assert.Contains("0.3 seconds", ex.Message);
    }

    [Fact]
    public async Task ACacheRootThatIsGoneFailsAtOnceWithTheEntry()
    {
        var cached = Path.Combine(_root, "offline-share", "0123456789abcdef-setup.exe");
        var started = DateTime.UtcNow;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SharedDownloadCache.AcquireEntryLockAsync(cached, CancellationToken.None, TimeSpan.FromMinutes(5)));

        Assert.Contains(cached, ex.Message);
        Assert.Contains("could not be locked", ex.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task AFreeEntryIsLockedAndReleased()
    {
        var cached = Path.Combine(_root, "0123456789abcdef-free.exe");
        await using (await SharedDownloadCache.AcquireEntryLockAsync(cached, CancellationToken.None))
            Assert.True(File.Exists(cached + ".lock"));
        Assert.False(File.Exists(cached + ".lock"));
    }

    [Fact]
    public async Task InvalidatingAnEntryWaitsForTheStageThatHoldsItsLock()
    {
        var url = "https://example.test/setup.exe";
        var cached = Path.Combine(_root, SharedDownloadCache.Key(url, "setup.exe"));
        await File.WriteAllTextAsync(cached, "payload");
        var holder = await SharedDownloadCache.AcquireEntryLockAsync(cached, CancellationToken.None);

        var invalidate = SharedDownloadCache.InvalidateAsync(_root, url, "setup.exe", CancellationToken.None);
        await Task.Delay(400);

        Assert.False(invalidate.IsCompleted, "The purge must wait for the lock.");
        Assert.True(File.Exists(cached), "The purge must not delete the entry while another stage holds the lock.");

        await holder.DisposeAsync();
        await invalidate;

        Assert.False(File.Exists(cached));
        Assert.False(File.Exists(cached + ".lock"));
    }

    [Fact]
    public async Task InvalidatingAnAbsentEntryDoesNothing()
    {
        await SharedDownloadCache.InvalidateAsync(_root, "https://example.test/none.exe", "none.exe", CancellationToken.None);

        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }
}
