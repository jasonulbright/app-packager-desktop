using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A URL captured from a vendor page or feed reaches curl as one argument after
/// --url. A URL that could end its argument or read as an option is refused
/// before curl starts, so it cannot add an output file or a switch.
/// </summary>
public sealed class CurlArgumentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-curl-args-" + Guid.NewGuid().ToString("N"));

    public CurlArgumentTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static Dictionary<string, byte[]> Files() => new()
    {
        ["setup.exe"] = "MZ payload"u8.ToArray(),
        ["evil"] = "@echo pwned"u8.ToArray()
    };

    [Fact]
    public async Task AQuotedUrlCannotWriteASecondFile()
    {
        using var server = new LoopbackFileServer(Files());
        var evil = Path.Combine(_dir, "a.bat");
        var hostile = $"{server.Url("setup.exe")}\" -o \"{evil}\" \"{server.Url("evil")}";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DownloadService().DownloadAsync(hostile, Path.Combine(_dir, "setup.exe"), retryCount: 0));

        Assert.False(File.Exists(evil));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task AQuotedUrlCannotWriteASecondFileThroughTheSharedCache()
    {
        using var server = new LoopbackFileServer(Files());
        var evil = Path.Combine(_dir, "b.bat");
        var hostile = $"{server.Url("setup.exe")}\" -o \"{evil}\" \"{server.Url("evil")}";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SharedDownloadCache.FetchAsync(new DownloadService(), hostile, Path.Combine(_dir, "out", "setup.exe"), Path.Combine(_dir, "_cache")));

        Assert.False(File.Exists(evil));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task AJsonFeedDownloadUrlWithAQuoteDoesNotReachCurl()
    {
        var evil = Path.Combine(_dir, "c.bat");
        using var feed = new LoopbackFileServer(new Dictionary<string, byte[]>());
        var hostile = $"{feed.Url("setup.exe")}\" -o \"{evil}\" \"{feed.Url("evil")}";
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { version = "1.0.0", url = hostile });
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["feed.json"] = json });
        var config = new VersionScraperConfig
        {
            Type = "JsonQuery",
            Url = server.Url("feed.json"),
            Pattern = "version",
            DownloadUrlQuery = "url"
        };
        var (version, url) = await new JsonQueryScraper().GetVersionAndDownloadUrlAsync(config);
        Assert.Equal("1.0.0", version);
        Assert.Equal(hostile, url);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DownloadService().DownloadAsync(url!, Path.Combine(_dir, "setup.exe"), retryCount: 0));
        Assert.False(File.Exists(evil));
        Assert.Empty(feed.Requests);
    }

    [Theory]
    [InlineData("-o C:\\x.bat")]
    [InlineData("--output=C:\\x.bat")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://vendor.example/setup.exe")]
    [InlineData("vendor.example/setup.exe")]
    [InlineData("https://vendor.example/a b.exe")]
    [InlineData("https://vendor.example/a\tb.exe")]
    [InlineData("https://vendor.example/a\u0000b.exe")]
    [InlineData("https://vendor.example/a\"b.exe")]
    [InlineData("")]
    public void AUrlThatIsNotAPlainHttpUrlIsRefused(string url)
    {
        Assert.Throws<InvalidOperationException>(() => CurlProcess.ValidateUrl(url));
        Assert.Throws<InvalidOperationException>(() => CurlProcess.BuildStartInfo(["-L"], url, false));
    }

    [Fact]
    public void EveryValueIsItsOwnArgumentAndTheUrlFollowsUrlSwitch()
    {
        var info = CurlProcess.BuildStartInfo(
            ["-L", .. CurlHeaders.Arguments(["Referer: https://a.example/x y"]), "-o", @"C:\dir with space\it's.exe"],
            "https://vendor.example/setup.exe?a=1&b=2", readConfigFromStdin: true);

        Assert.Equal("", info.Arguments);
        Assert.Equal(
            ["-L", "--compressed", "-H", "Referer: https://a.example/x y", "-o", @"C:\dir with space\it's.exe",
             "--globoff", "-K", "-", "--url", "https://vendor.example/setup.exe?a=1&b=2"],
            info.ArgumentList);
    }

    [Fact]
    public async Task AnOutputPathWithSpacesAndAnApostropheDownloads()
    {
        using var server = new LoopbackFileServer(Files());
        var target = Path.Combine(_dir, "with space", "it's here", "setup.exe");

        await new DownloadService().DownloadAsync(server.Url("setup.exe"), target, retryCount: 0);

        Assert.Equal("MZ payload"u8.ToArray(), File.ReadAllBytes(target));
    }
}
