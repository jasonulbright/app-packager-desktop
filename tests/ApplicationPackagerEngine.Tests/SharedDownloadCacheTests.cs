using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A fixed-name download is fetched once into the shared cache root, keyed by URL and
/// file name, and each download root receives a copy that carries the server's
/// Last-Modified time. A later request is conditional on that time.
/// </summary>
public sealed class SharedDownloadCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-shared-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>Serves one body with a Last-Modified time and answers If-Modified-Since with 304.</summary>
    private sealed class ConditionalServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;
        public byte[] Body { get; set; } = Encoding.ASCII.GetBytes("MZ payload one");
        public DateTimeOffset LastModified { get; set; } = new(2026, 5, 4, 3, 2, 1, TimeSpan.Zero);
        public ConcurrentQueue<string> Requests { get; } = new();
        public int BodiesSent;
        /// <summary>A status other than 200 or 304 answered to every request while set.</summary>
        public int? FailStatus { get; set; }
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        public string Url { get; }

        public ConditionalServer()
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/setup.exe";
            _serving = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception) { return; }
                using (client)
                {
                    try
                    {
                        using var stream = client.GetStream();
                        var buffer = new byte[8192];
                        var head = new StringBuilder();
                        while (!head.ToString().Contains("\r\n\r\n"))
                        {
                            var read = await stream.ReadAsync(buffer, _stop.Token);
                            if (read == 0) break;
                            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        }
                        var text = head.ToString();
                        Requests.Enqueue(text);
                        var since = text.Split("\r\n").FirstOrDefault(l => l.StartsWith("If-Modified-Since:", StringComparison.OrdinalIgnoreCase));
                        var lastModified = LastModified.ToString("r", CultureInfo.InvariantCulture);
                        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, _stop.Token);
                        if (FailStatus is int status)
                        {
                            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Refused\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                        }
                        else if (since is not null && DateTimeOffset.Parse(since["If-Modified-Since:".Length..].Trim(), CultureInfo.InvariantCulture) >= LastModified)
                        {
                            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 304 Not Modified\r\nLast-Modified: {lastModified}\r\nConnection: close\r\n\r\n"), _stop.Token);
                        }
                        else
                        {
                            Interlocked.Increment(ref BodiesSent);
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                                $"HTTP/1.1 200 OK\r\nContent-Length: {Body.Length}\r\nLast-Modified: {lastModified}\r\nConnection: close\r\n\r\n"), _stop.Token);
                            await stream.WriteAsync(Body, _stop.Token);
                        }
                        await stream.FlushAsync(_stop.Token);
                    }
                    catch (Exception)
                    {
                        // A client that hangs up early is not a test failure.
                    }
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch (Exception) { }
            try { _serving.Wait(TimeSpan.FromSeconds(10)); } catch (Exception) { }
            _stop.Dispose();
        }
    }

    [Fact]
    public void TheKeyCarriesSixteenHexDigitsOfTheUrlDigestAndTheFileName()
    {
        var key = SharedDownloadCache.Key("https://vendor.example/setup.exe", "setup.exe");
        Assert.Matches("^[0-9a-f]{16}-setup.exe$", key);
        Assert.NotEqual(key, SharedDownloadCache.Key("https://other.example/setup.exe", "setup.exe"));
        Assert.Equal(key, SharedDownloadCache.Key("https://vendor.example/setup.exe", "setup.exe"));
    }

    [Fact]
    public void TheRefreshRequestIsConditionalOnTheCachedFileAndKeepsTheRemoteTime()
    {
        var args = string.Join(" ", SharedDownloadCache.RefreshArguments(@"C:\c\k-s.exe", @"C:\c\k-s.exe.refresh"));
        Assert.Contains(@" -R -z C:\c\k-s.exe ", args);
        Assert.Contains(@"-o C:\c\k-s.exe.refresh", args);
    }

    [Fact]
    public async Task TwoDownloadRootsShareOneDownloadAndBothCopiesCarryTheServerTime()
    {
        using var server = new ConditionalServer();
        var downloadRoot = Path.Combine(_root, "ap");
        var cacheRoot = WorkbenchPaths.GetSharedDownloadCacheRoot(downloadRoot);
        var profileRoot = WorkbenchPaths.GetProfileDownloadRoot(downloadRoot, "pilot01");
        Assert.Equal(cacheRoot, WorkbenchPaths.GetSharedDownloadCacheRoot(profileRoot));

        var first = Path.Combine(downloadRoot, "App-x64", "1.0", "setup.exe");
        var second = Path.Combine(profileRoot, "App-x64", "1.0", "setup.exe");
        var lines = new List<string>();
        await SharedDownloadCache.FetchAsync(new DownloadService(), server.Url, first, cacheRoot, onProgress: lines.Add);
        await SharedDownloadCache.FetchAsync(new DownloadService(), server.Url, second, cacheRoot, onProgress: lines.Add);

        Assert.Equal(1, server.BodiesSent);
        Assert.Contains("Cached download is current.", lines);
        Assert.Equal(server.Body, File.ReadAllBytes(first));
        Assert.Equal(server.Body, File.ReadAllBytes(second));
        Assert.Equal(server.LastModified.UtcDateTime, File.GetLastWriteTimeUtc(first));
        Assert.Equal(server.LastModified.UtcDateTime, File.GetLastWriteTimeUtc(second));
        Assert.Single(Directory.GetFiles(cacheRoot));
    }

    [Fact]
    public async Task ANewerReleaseReplacesTheCacheAndTheCopy()
    {
        using var server = new ConditionalServer();
        var cacheRoot = Path.Combine(_root, "_cache");
        var target = Path.Combine(_root, "a", "setup.exe");
        await SharedDownloadCache.FetchAsync(new DownloadService(), server.Url, target, cacheRoot);

        server.Body = Encoding.ASCII.GetBytes("MZ payload two");
        server.LastModified = server.LastModified.AddDays(3);
        var lines = new List<string>();
        await SharedDownloadCache.FetchAsync(new DownloadService(), server.Url, target, cacheRoot, onProgress: lines.Add);

        Assert.Contains("Cached download replaced by a newer release.", lines);
        Assert.Equal(server.Body, File.ReadAllBytes(target));
        Assert.Equal(server.LastModified.UtcDateTime, File.GetLastWriteTimeUtc(target));
    }

    [Fact]
    public async Task AFailedRefreshFailsWhenTheVersionCameFromTheVersionSource()
    {
        using var server = new ConditionalServer();
        var cacheRoot = Path.Combine(_root, "_cache");
        await SharedDownloadCache.FetchAsync(new DownloadService(), server.Url, Path.Combine(_root, "1.0", "setup.exe"), cacheRoot);
        server.FailStatus = 403;
        var target = Path.Combine(_root, "2.0", "setup.exe");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SharedDownloadCache.FetchAsync(new DownloadService(), server.Url, target, cacheRoot, keepCachedOnFailedRefresh: false));

        Assert.Contains(server.Url, ex.Message);
        Assert.Contains("The cached copy is dated 2026-05-04 03:02 UTC (", ex.Message);
        Assert.Contains(" days old)", ex.Message);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task AFailedRefreshKeepsTheCacheWhenTheVersionIsReadFromTheFile()
    {
        using var server = new ConditionalServer();
        var cacheRoot = Path.Combine(_root, "_cache");
        await SharedDownloadCache.FetchAsync(new DownloadService(), server.Url, Path.Combine(_root, "a", "setup.exe"), cacheRoot);
        server.FailStatus = 403;
        var target = Path.Combine(_root, "b", "setup.exe");
        var lines = new List<string>();

        await SharedDownloadCache.FetchAsync(new DownloadService(), server.Url, target, cacheRoot, onProgress: lines.Add);

        Assert.Equal(server.Body, File.ReadAllBytes(target));
        Assert.Contains(lines, l => l.StartsWith("Cached download kept; refresh failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStageWhoseScraperNamedTheVersionFailsWhenTheRefreshFails()
    {
        using var server = new ConditionalServer();
        var downloadRoot = Path.Combine(_root, "dl");
        var def = new AppDefinition
        {
            Id = "fixedname", Vendor = "Contoso", Application = "Fixed", Architecture = "x64", InstallerType = "EXE",
            InstallArgs = "/S",
            VersionScraper = new VersionScraperConfig { Type = "HttpRegex", Url = "http://127.0.0.1:9/never", Pattern = "(x)" },
            DownloadUrl = server.Url,
            Detection = new DetectionConfig { Type = "File", FilePath = @"C:\Program Files\Fixed", FileName = "fixed.exe", PropertyType = "Existence" }
        };
        var staging = new StagingService(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(),
            new WrapperGeneratorService(), new ManifestService());
        await staging.StageAsync(def, downloadRoot, knownVersion: "1.0");
        Assert.Single(Directory.GetFiles(WorkbenchPaths.GetSharedDownloadCacheRoot(downloadRoot), "*-setup.exe"));
        server.FailStatus = 403;

        var second = await staging.StageAsync(def, downloadRoot, knownVersion: "2.0");

        Assert.False(second.Success);
        Assert.Contains($"Refresh of the cached download failed for {server.Url}", second.ErrorMessage);
        Assert.False(File.Exists(Path.Combine(downloadRoot, "Fixed-x64", "2.0", "setup.exe")));
    }

    [Fact]
    public async Task TwoConcurrentStagesOfOneUrlDownloadItOnce()
    {
        using var server = new ConditionalServer { Delay = TimeSpan.FromMilliseconds(600) };
        var cacheRoot = Path.Combine(_root, "_cache");
        var first = Path.Combine(_root, "p1", "setup.exe");
        var second = Path.Combine(_root, "p2", "setup.exe");

        await Task.WhenAll(
            SharedDownloadCache.FetchAsync(new DownloadService(), server.Url, first, cacheRoot),
            SharedDownloadCache.FetchAsync(new DownloadService(), server.Url, second, cacheRoot));

        Assert.Equal(1, server.BodiesSent);
        Assert.Equal(server.Body, File.ReadAllBytes(first));
        Assert.Equal(server.Body, File.ReadAllBytes(second));
        Assert.Single(Directory.GetFiles(cacheRoot));
    }

    [Fact]
    public void ACopyTakesTheCachedFilesTimeNotTheCopyTime()
    {
        Directory.CreateDirectory(_root);
        var cached = Path.Combine(_root, "cached.exe");
        File.WriteAllText(cached, "x");
        var stamp = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(cached, stamp);

        var copy = Path.Combine(_root, "sub", "copy.exe");
        SharedDownloadCache.CopyWithTimestamp(cached, copy);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(copy));
    }

    [Theory]
    [InlineData("https://vendor.example/latest/setup.exe", null, true)]
    [InlineData("https://vendor.example/{version}/setup.exe", null, false)]
    [InlineData("https://vendor.example/setup-{display_version}.exe", null, false)]
    [InlineData("https://vendor.example/latest/setup.exe", "$.assets[0].url", false)]
    public void OnlyAFixedNameUrlUsesTheSharedCache(string url, string? query, bool expected)
    {
        var def = new AppDefinition { DownloadUrl = url, VersionScraper = new VersionScraperConfig { DownloadUrlQuery = query } };
        Assert.Equal(expected, StagingService.IsFixedNameDownload(def));
    }

    [Fact]
    public async Task AFreshFetchWaitsForTheStageThatHoldsTheEntryLockAndRefillsTheCache()
    {
        using var server = new ConditionalServer();
        var cacheRoot = Path.Combine(_root, "_cache");
        var first = Path.Combine(_root, "a", "setup.exe");
        var cached = await SharedDownloadCache.FetchAsync(new DownloadService(), server.Url, first, cacheRoot);
        server.Body = Encoding.ASCII.GetBytes("MZ payload two");
        var holder = await SharedDownloadCache.AcquireEntryLockAsync(cached, CancellationToken.None);
        var target = Path.Combine(_root, "b", "setup.exe");

        var refetch = SharedDownloadCache.RefetchAsync(new DownloadService(), server.Url, target, cacheRoot, path =>
        {
            Assert.False(File.Exists(cached), "Fresh bytes must pass verification before they enter the shared cache.");
            Assert.Equal(Encoding.ASCII.GetBytes("MZ payload two"), File.ReadAllBytes(path));
        });
        await Task.Delay(500);

        Assert.False(refetch.IsCompleted, "The purge and the fresh download must wait for the entry lock.");
        Assert.Equal(Encoding.ASCII.GetBytes("MZ payload one"), File.ReadAllBytes(cached));

        await holder.DisposeAsync();
        await refetch;

        Assert.Equal(server.Body, File.ReadAllBytes(target));
        Assert.Equal(server.Body, File.ReadAllBytes(cached));
    }

    [Fact]
    public async Task AFreshFetchThatFailsTheCheckLeavesNoEntryInTheCache()
    {
        using var server = new ConditionalServer();
        var cacheRoot = Path.Combine(_root, "_cache");
        var target = Path.Combine(_root, "a", "setup.exe");
        var cached = Path.Combine(cacheRoot, SharedDownloadCache.Key(server.Url, "setup.exe"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SharedDownloadCache.RefetchAsync(new DownloadService(), server.Url, target, cacheRoot,
                _ =>
                {
                    Assert.False(File.Exists(cached), "Unverified bytes must not be published to the shared cache.");
                    throw new InvalidOperationException("hash mismatch");
                }));

        Assert.Equal("hash mismatch", ex.Message);
        Assert.False(File.Exists(cached), "A replacement that fails the check must not stay in the shared cache.");
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(cached + ".lock"));
    }

    [Fact]
    public async Task AFreshFetchReplacesAnEntryThatAppearsBeforePublication()
    {
        using var server = new ConditionalServer();
        var cacheRoot = Path.Combine(_root, "_cache");
        var target = Path.Combine(_root, "a", "setup.exe");
        var cached = Path.Combine(cacheRoot, SharedDownloadCache.Key(server.Url, "setup.exe"));

        await SharedDownloadCache.RefetchAsync(new DownloadService(), server.Url, target, cacheRoot, path =>
        {
            Assert.False(File.Exists(cached), "The replacement must be verified before it is published.");
            Assert.Equal(server.Body, File.ReadAllBytes(path));
            File.WriteAllText(cached, "stale concurrent cache entry");
        });

        Assert.Equal(server.Body, File.ReadAllBytes(target));
        Assert.Equal(server.Body, File.ReadAllBytes(cached));
    }
}
