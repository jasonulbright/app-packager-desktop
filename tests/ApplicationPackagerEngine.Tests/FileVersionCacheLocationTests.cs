using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The FileVersion scraper cache lives below the per-user data root, follows
/// APP_PACKAGER_DATA_ROOT, fails with a clear message when its folder is
/// unusable, and stays within a size limit.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public class FileVersionCacheLocationTests : IDisposable
{
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "packr-fvcache-" + Guid.NewGuid().ToString("N"));

    public FileVersionCacheLocationTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        try { Directory.Delete(_root, true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void CachePath_SitsBelowTheDataRoot_AndNotInTemp()
    {
        var dir = FileVersionScraper.CacheDir;

        Assert.True(AppDataPaths.IsWithin(dir, AppDataPaths.Root));
        Assert.Equal("FileVersionCache", Path.GetFileName(dir));
        Assert.False(dir.Contains("SRLPackr-FileVersionCache", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Override_MovesTheCache()
    {
        var first = FileVersionScraper.CacheDir;
        var other = Path.Combine(_root, "other");
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, other);

        var second = FileVersionScraper.CacheDir;

        Assert.NotEqual(first, second);
        Assert.True(AppDataPaths.IsWithin(second, other));
    }

    [Fact]
    public void DefaultRoot_IsThePerUserAppDataFolder()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, null);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.True(AppDataPaths.IsWithin(FileVersionScraper.CacheDir, Path.Combine(local, "AppPackager")));
    }

    [Fact]
    public void EnsureCacheDir_CreatesTheFolder()
    {
        var dir = FileVersionScraper.EnsureCacheDir();

        Assert.True(Directory.Exists(dir));
        Assert.Equal(FileVersionScraper.CacheDir, dir);
    }

    [Fact]
    public void UnusableFolder_FailsWithAClearMessage()
    {
        // A file where the folder belongs makes the folder impossible to create.
        Directory.CreateDirectory(Path.GetDirectoryName(FileVersionScraper.CacheDir)!);
        File.WriteAllText(FileVersionScraper.CacheDir, "not a folder");

        var ex = Assert.Throws<IOException>(FileVersionScraper.EnsureCacheDir);

        Assert.Contains("FileVersion cache folder cannot be used", ex.Message);
        Assert.Contains(FileVersionScraper.CacheDir, ex.Message);
    }

    [Fact]
    public async Task GetLatestVersion_WithUnusableFolder_FailsWithTheSameMessage()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileVersionScraper.CacheDir)!);
        File.WriteAllText(FileVersionScraper.CacheDir, "not a folder");
        var config = new ApplicationPackagerEngine.Models.VersionScraperConfig
        {
            Type = "FileVersion",
            Url = "https://example.invalid/setup.exe",
            Pattern = "FileVersion"
        };

        var ex = await Assert.ThrowsAsync<IOException>(() => new FileVersionScraper().GetLatestVersionAsync(config));

        Assert.Contains("FileVersion cache folder cannot be used", ex.Message);
    }

    [Fact]
    public void TrimCache_DeletesTheOldestFilesFirst_AndKeepsTheNamedFile()
    {
        var dir = FileVersionScraper.EnsureCacheDir();
        var old = Path.Combine(dir, "old.exe");
        var mid = Path.Combine(dir, "mid.exe");
        var fresh = Path.Combine(dir, "fresh.exe");
        foreach (var (path, age) in new[] { (old, 3), (mid, 2), (fresh, 1) })
        {
            File.WriteAllBytes(path, new byte[100]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-age));
        }

        var deleted = FileVersionScraper.TrimCache(dir, maxBytes: 250, keepPath: old);

        Assert.Equal(1, deleted);
        Assert.True(File.Exists(old));
        Assert.False(File.Exists(mid));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void TrimCache_LeavesAFolderWithinTheLimitAlone()
    {
        var dir = FileVersionScraper.EnsureCacheDir();
        File.WriteAllBytes(Path.Combine(dir, "a.exe"), new byte[100]);

        Assert.Equal(0, FileVersionScraper.TrimCache(dir, FileVersionScraper.MaxCacheBytes));
        Assert.True(File.Exists(Path.Combine(dir, "a.exe")));
    }
}
