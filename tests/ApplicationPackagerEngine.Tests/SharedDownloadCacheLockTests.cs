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

    [Fact]
    public async Task AHolderWhoseDownloadKeepsGrowingIsWaitedForPastTheBound()
    {
        var cached = Path.Combine(_root, "0123456789abcdef-large.exe");
        var holder = new FileStream(cached + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var part = $"{cached}.{Guid.NewGuid():N}.part";
        var downloading = Task.Run(async () =>
        {
            for (var i = 0; i < 12; i++)
            {
                await File.AppendAllTextAsync(part, new string('x', 1024));
                await Task.Delay(100);
            }
            File.Move(part, cached);
            holder.Dispose();
        });

        await using (await SharedDownloadCache.AcquireEntryLockAsync(cached, CancellationToken.None, TimeSpan.FromMilliseconds(400)))
            Assert.True(File.Exists(cached));
        await downloading;
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
}
