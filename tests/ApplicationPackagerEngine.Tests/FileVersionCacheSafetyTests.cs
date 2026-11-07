using System.Security.Cryptography;
using System.Text;
using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The version cache of the FileVersion scraper holds a whole file or no file. A download that ends early
/// leaves nothing at the cache path, and the fallback name of a cache file is the same in every process.
/// </summary>
public sealed class FileVersionCacheSafetyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apd-fvcache-" + Guid.NewGuid().ToString("N"));

    public FileVersionCacheSafetyTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    // The runner stands for curl: it writes the file named after -o.
    private static string OutFile(IReadOnlyList<string> args) => args[args.ToList().IndexOf("-o") + 1];

    [Fact]
    public async Task AFirstDownloadThatEndsWithAnErrorAfterWritingBytes_LeavesNoFileAtTheCachePath()
    {
        var cache = Path.Combine(_folder, "setup.exe");

        await Assert.ThrowsAsync<OperationCanceledException>(() => FileVersionScraper.DownloadAsync("https://example.test/setup.exe", cache,
            (args, _, _) =>
            {
                File.WriteAllBytes(OutFile(args), [1, 2, 3]);
                throw new OperationCanceledException();
            }, CancellationToken.None));

        Assert.False(File.Exists(cache));
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task AFirstDownloadThatFails_LeavesNoFile()
    {
        var cache = Path.Combine(_folder, "setup.exe");

        await FileVersionScraper.DownloadAsync("https://example.test/setup.exe", cache,
            (args, _, _) => { File.WriteAllBytes(OutFile(args), [1, 2, 3]); return Task.FromResult(false); }, CancellationToken.None);

        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task AFirstDownloadThatSucceeds_LeavesTheWholeFileAtTheCachePathOnly()
    {
        var cache = Path.Combine(_folder, "setup.exe");

        await FileVersionScraper.DownloadAsync("https://example.test/setup.exe", cache,
            (args, _, _) => { File.WriteAllBytes(OutFile(args), [1, 2, 3]); return Task.FromResult(true); }, CancellationToken.None);

        Assert.Equal([cache], Directory.GetFiles(_folder));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(cache));
    }

    [Fact]
    public void TheFallbackNameOfACacheFileComesFromTheUrlAndIsTheSameInEveryProcess()
    {
        const string url = "https://example.test/download?id=7";
        var expected = "download-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..8].ToLowerInvariant() + ".tmp";

        Assert.Equal(expected, FileVersionScraper.ResolveFileName(url));
    }
}
