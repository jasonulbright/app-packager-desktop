using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;

namespace ApplicationPackagerEngine.Tests.AddFromInstaller;

/// <summary>The download of Add from web is bounded and leaves no folder behind once the installer is not needed.</summary>
public sealed class AddFromWebFetchTests : IDisposable
{
    private readonly AddFromInstallerTestBed _bed = new();
    private static readonly IProgress<string> NoProgress = new Progress<string>();

    public void Dispose() => _bed.Dispose();

    private string DownloadRoot => _bed.PathOf("downloads");

    private string FetchFolders => Path.Combine(DownloadRoot, "AddFromWeb");

    private int FolderCount => Directory.Exists(FetchFolders) ? Directory.GetDirectories(FetchFolders).Length : 0;

    private DownloadRequest Request(LoopbackFileServer server, string file) => new(Url: server.Url(file), FileName: null, DownloadRoot: DownloadRoot);

    [Fact]
    public async Task ADiscardedDownload_LeavesNoFolder_AndAFileOutsideTheFetchFoldersStays()
    {
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["contoso.msi"] = File.ReadAllBytes(_bed.Msi()) });
        var service = _bed.Real();

        var path = await service.FetchInstallerAsync(Request(server, "contoso.msi"), NoProgress, CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(1, FolderCount);
        service.DiscardFetched(path);
        Assert.Equal(0, FolderCount);

        var outside = _bed.PathOf("keep.msi");
        File.WriteAllText(outside, "keep");
        service.DiscardFetched(outside);
        Assert.True(File.Exists(outside));
        var inRoot = Path.Combine(DownloadRoot, "AddFromWeb", "notahexfolder", "x.msi");
        Directory.CreateDirectory(Path.GetDirectoryName(inRoot)!);
        File.WriteAllText(inRoot, "keep");
        service.DiscardFetched(inRoot);
        Assert.True(File.Exists(inRoot));
        service.DiscardFetched("");
    }

    [Fact]
    public async Task AFailedDownload_LeavesNoFolder()
    {
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]>());

        await Assert.ThrowsAnyAsync<Exception>(() => _bed.Real().FetchInstallerAsync(Request(server, "missing.msi"), NoProgress, CancellationToken.None));

        Assert.Equal(0, FolderCount);
    }

    [Fact]
    public async Task AFileThatIsNotAnInstaller_IsRefusedAndLeavesNoFolder()
    {
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["contoso.msi"] = "<html>not an installer</html>"u8.ToArray() });

        var ex = await Assert.ThrowsAsync<AddFromInstallerRefusedException>(() =>
            _bed.Real().FetchInstallerAsync(Request(server, "contoso.msi"), NoProgress, CancellationToken.None));

        Assert.Contains("not an installer", ex.Message);
        Assert.Equal(0, FolderCount);
    }

    [Fact]
    public async Task ACancelledDownload_LeavesNoFolder()
    {
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["contoso.msi"] = File.ReadAllBytes(_bed.Msi()) });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _bed.Real().FetchInstallerAsync(Request(server, "contoso.msi"), NoProgress, cancelled.Token));

        Assert.Equal(0, FolderCount);
    }

    [Fact]
    public async Task ADownloadAboveTheAnalysisFileLimit_IsRefusedAndLeavesNoFolder()
    {
        var bytes = File.ReadAllBytes(_bed.Msi());
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["contoso.msi"] = bytes });
        var service = _bed.Real(AnalysisLimits.Default with { MaxFileBytes = bytes.Length - 1 });

        var ex = await Assert.ThrowsAsync<AddFromInstallerRefusedException>(() =>
            service.FetchInstallerAsync(Request(server, "contoso.msi"), NoProgress, CancellationToken.None));

        Assert.Contains("larger than", ex.Message);
        Assert.Equal(0, FolderCount);
    }

    [Fact]
    public async Task ADownloadAtTheLimit_IsAccepted()
    {
        var bytes = File.ReadAllBytes(_bed.Msi());
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["contoso.msi"] = bytes });
        var service = _bed.Real(AnalysisLimits.Default with { MaxFileBytes = bytes.Length });

        var path = await service.FetchInstallerAsync(Request(server, "contoso.msi"), NoProgress, CancellationToken.None);

        Assert.Equal(bytes.Length, new FileInfo(path).Length);
    }

    [Fact]
    public async Task TheDownloadServiceStopsATransferAboveItsLimit()
    {
        using var server = new SlowLoopbackServer();
        var target = _bed.PathOf("payload.bin");

        var ex = await Assert.ThrowsAsync<DownloadTooLargeException>(() =>
            new DownloadService().DownloadAsync(server.Url, target, retryCount: 0, maxBytes: 1000));

        Assert.Contains("1000", ex.Message);
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_bed.Root, "payload.bin*"));
    }

    [Fact]
    public async Task ARefreshAboveTheLimit_IsRefusedAndNeverReplacesTheCachedFile()
    {
        var body = new byte[4096];
        body[0] = (byte)'M'; body[1] = (byte)'Z';
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["setup.exe"] = body });
        var cacheRoot = _bed.PathOf("cache");
        Directory.CreateDirectory(cacheRoot);
        var url = server.Url("setup.exe");
        var cached = Path.Combine(cacheRoot, SharedDownloadCache.Key(url, "setup.exe"));
        File.WriteAllBytes(cached, [(byte)'M', (byte)'Z', 1, 2]);
        Directory.CreateDirectory(_bed.PathOf("out"));
        var copy = Path.Combine(_bed.PathOf("out"), "setup.exe");

        await Assert.ThrowsAsync<DownloadTooLargeException>(() =>
            SharedDownloadCache.FetchAsync(new DownloadService(), url, copy, cacheRoot, maxBytes: 1024));

        Assert.Equal(4, new FileInfo(cached).Length);
        Assert.False(File.Exists(copy));
        Assert.Empty(Directory.GetFiles(cacheRoot, "*.refresh"));
    }

    [Fact]
    public async Task ADiscardNamesOnlyAFolderTheServiceFetched()
    {
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["contoso.msi"] = File.ReadAllBytes(_bed.Msi()) });
        var fetched = await _bed.Real().FetchInstallerAsync(Request(server, "contoso.msi"), NoProgress, CancellationToken.None);
        var lookalike = Path.Combine(FetchFolders, "0123abcd", "contoso.msi");
        Directory.CreateDirectory(Path.GetDirectoryName(lookalike)!);
        File.WriteAllText(lookalike, "keep");

        _bed.Real().DiscardFetched(fetched);
        _bed.Real().DiscardFetched(lookalike);

        Assert.True(File.Exists(fetched));
        Assert.True(File.Exists(lookalike));
    }

    [Fact]
    public async Task ADiscardThatCannotDeleteTheFolder_CanBeRetried()
    {
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["contoso.msi"] = File.ReadAllBytes(_bed.Msi()) });
        var service = _bed.Real();
        var fetched = await service.FetchInstallerAsync(Request(server, "contoso.msi"), NoProgress, CancellationToken.None);

        using (new FileStream(fetched, FileMode.Open, FileAccess.Read, FileShare.None))
            service.DiscardFetched(fetched);
        Assert.True(File.Exists(fetched));

        service.DiscardFetched(fetched);
        Assert.Equal(0, FolderCount);
    }
}
