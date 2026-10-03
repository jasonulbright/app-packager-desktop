using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A vendor URL that always serves the current release under one file name is
/// revalidated instead of re-fetched: the request carries the cached file's
/// timestamp, and the outcome decides whether the cache is replaced, kept as
/// current, or kept because the request failed.
/// </summary>
public class CachedDownloadRefreshTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "packr-cache-refresh-" + Guid.NewGuid().ToString("N"));

    public CachedDownloadRefreshTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Cache(string content = "cached payload")
    {
        var path = Path.Combine(_dir, "setup.exe");
        File.WriteAllText(path, content);
        return path;
    }

    // -- Request shape -----------------------------------------------------

    [Fact]
    public void FirstDownload_IsUnconditionalAndKeepsTheRemoteTimestamp()
    {
        var args = string.Join(" ", FileVersionScraper.BuildDownloadArguments(@"C:\cache\setup.exe", null));

        Assert.DoesNotContain(" -z ", args);
        Assert.Contains(" -R ", args);
        Assert.Contains(@"-o C:\cache\setup.exe", args);
    }

    [Fact]
    public void RefreshRequest_IsConditionalOnTheCachedFile()
    {
        var args = string.Join(" ", FileVersionScraper.BuildDownloadArguments(@"C:\cache\setup.exe.refresh", @"C:\cache\setup.exe"));

        Assert.Contains(@"-z C:\cache\setup.exe ", args);
        Assert.Contains(@"-o C:\cache\setup.exe.refresh", args);
    }

    // -- Outcomes ----------------------------------------------------------

    [Fact]
    public void NewRelease_ReplacesTheCacheWithTheDownloadedBody()
    {
        var cache = Cache();
        var refresh = cache + ".refresh";
        File.WriteAllText(refresh, "new payload");

        var outcome = FileVersionScraper.ApplyRefresh(cache, refresh, requestSucceeded: true);

        Assert.Equal(FileVersionScraper.CacheRefresh.Replaced, outcome);
        Assert.Equal("new payload", File.ReadAllText(cache));
        Assert.False(File.Exists(refresh));
    }

    [Fact]
    public void UnchangedRelease_WritesNoSideFileAndKeepsTheCache()
    {
        var cache = Cache();

        var outcome = FileVersionScraper.ApplyRefresh(cache, cache + ".refresh", requestSucceeded: true);

        Assert.Equal(FileVersionScraper.CacheRefresh.Current, outcome);
        Assert.Equal("cached payload", File.ReadAllText(cache));
    }

    [Fact]
    public void EmptySideFile_CountsAsUnchangedAndNeverOverwritesTheCache()
    {
        var cache = Cache();
        var refresh = cache + ".refresh";
        File.WriteAllBytes(refresh, []);

        var outcome = FileVersionScraper.ApplyRefresh(cache, refresh, requestSucceeded: true);

        Assert.Equal(FileVersionScraper.CacheRefresh.Current, outcome);
        Assert.Equal("cached payload", File.ReadAllText(cache));
        Assert.False(File.Exists(refresh));
    }

    [Fact]
    public void FailedRequest_KeepsTheCacheAndDiscardsAnyPartialBody()
    {
        var cache = Cache();
        var refresh = cache + ".refresh";
        File.WriteAllText(refresh, "torn body");

        var outcome = FileVersionScraper.ApplyRefresh(cache, refresh, requestSucceeded: false);

        Assert.Equal(FileVersionScraper.CacheRefresh.Kept, outcome);
        Assert.Equal("cached payload", File.ReadAllText(cache));
        Assert.False(File.Exists(refresh));
    }

    // -- Version resource fixed block --------------------------------------

    [Fact]
    public void FixedBlockVersion_FormatsTheFourProductParts()
    {
        Assert.Equal("15.81.6.0", FileVersionScraper.FixedBlockVersion(15, 81, 6, 0));
    }

    [Fact]
    public void FixedBlockVersion_AnUnsetBlockIsNotAVersion()
    {
        Assert.Equal("", FileVersionScraper.FixedBlockVersion(0, 0, 0, 0));
    }
}
