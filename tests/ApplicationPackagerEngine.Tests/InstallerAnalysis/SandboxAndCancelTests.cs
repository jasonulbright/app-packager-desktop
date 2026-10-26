using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.InstallerScripts;
using static ApplicationPackagerEngine.Tests.InstallerAnalysis.InstallerAnalysisTestFiles;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>Sandbox failures as notes, cancellation inside the readers, the payload folder and the hash errors.</summary>
public sealed class SandboxAndCancelTests : IDisposable
{
    private readonly InstallerAnalysisTempFolder _temp = new();
    private readonly AnalysisSandbox _sandbox;
    private readonly InstallerAnalysisService _service;
    private static readonly AnalysisLimits Limits = AnalysisLimits.Default with { Timeout = TimeSpan.FromSeconds(60) };

    public SandboxAndCancelTests()
    {
        _sandbox = new AnalysisSandbox(_temp.PathOf("sandbox"));
        _service = new InstallerAnalysisService(_sandbox, () => null);
    }

    public void Dispose()
    {
        _sandbox.ReleaseAll();
        _temp.Dispose();
    }

    private string Target()
    {
        var folder = _temp.PathOf("out-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public async Task ASandboxThatCannotBeCreatedIsAPlainNoteAndTheAnalysisCompletes()
    {
        var blocked = _temp.PathOf("not-a-folder");
        File.WriteAllText(blocked, "x");
        var service = new InstallerAnalysisService(new AnalysisSandbox(Path.Combine(blocked, "Temp")), () => null);
        var path = WriteZip(_temp.PathOf("patch.zip"), ("update.msp", Text("not a patch")), ("readme.txt", Text("r")));

        var report = await service.AnalyzeAsync(path, Limits, new Progress<string>(), CancellationToken.None);

        Assert.Equal("ZIP", report.Type);
        Assert.Contains("The analysis sandbox could not be created; the post-patch target was not read.", report.Notes);
    }

    [Fact]
    public async Task AnAnalysisThatListsThePayloadReportsTheStepAndReturnsTheList()
    {
        var path = WriteZip(_temp.PathOf("bundle.zip"), ("setup/inner.msi", Text("m")), ("readme.txt", Text("r")));
        var steps = new List<string>();

        var report = await _service.AnalyzeAsync(path, Limits, new SyncProgress(steps.Add), CancellationToken.None);

        Assert.Contains("Listing the payload to find an inner patch", steps);
        Assert.NotNull(report.Payload);
        Assert.Contains(report.Payload!, e => e.Path == "setup/inner.msi" && e.InnerType == "MSI");
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (this) report(value);
        }
    }

    [Fact]
    public void TheNsisReaderStopsOnACancelledToken()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => NsisScriptReader.Read(Fixture("nsis-mk-lzma-solid-unicode.bin"), cancelled.Token));
    }

    [Fact]
    public void TheInnoReaderStopsOnACancelledToken()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => InnoSetupReader.Read(Fixture("inno-admin-670.bin"), cancelled.Token));
    }

    [Fact]
    public void TheLzmaDecoderStopsOnACancelledToken()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var input = new byte[1 << 20];

        Assert.ThrowsAny<OperationCanceledException>(() => LzmaDecoder.Decode(input, 0, input.Length, 0x5D, 1 << 16, 64 << 20, out _, cancelled.Token));
    }

    [Fact]
    public async Task ACancelledAnalysisOfAnNsisFileEndsAsCancelled()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.AnalyzeAsync(Fixture("nsis-mk-lzma-solid-unicode.bin"), Limits, new Progress<string>(), cancelled.Token));
    }

    [Fact]
    public async Task ATargetFolderBehindAJunctionAnywhereOnItsPathIsRefused()
    {
        var path = WriteZip(_temp.PathOf("p.zip"), ("a.txt", Text("a")));
        var real = Target();
        Directory.CreateDirectory(Path.Combine(real, "sub"));
        var link = _temp.PathOf("link-" + Guid.NewGuid().ToString("N")[..6]);
        Junction(link, real);

        var ex = await Assert.ThrowsAsync<InstallerAnalysisException>(() => _service.ExtractPayloadAsync(path, null, Path.Combine(link, "sub"), Limits, CancellationToken.None));

        Assert.Equal("A folder on the path of the target folder is a junction or symbolic link; choose another folder.", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(real, "sub")));
    }

    [Fact]
    public void TheNewPayloadFolderIsCreatedAtomicallyWithASuffixWhenTheNameIsTaken()
    {
        var target = Target();
        Directory.CreateDirectory(Path.Combine(target, "setup payload"));

        var folder = InstallerAnalysisService.CreatePayloadFolder(target, "setup");

        Assert.Equal(Path.Combine(target, "setup payload (2)"), folder);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public async Task ConcurrentPayloadExtractionsToOneFolderGetSeparateSubfolders()
    {
        var path = WriteZip(_temp.PathOf("setup.zip"), ("a.txt", Text("a")), ("b.txt", Text("b")));
        var target = Target();

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => _service.ExtractPayloadAsync(path, null, target, Limits, CancellationToken.None))));

        Assert.Equal(12, results.Select(r => r.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(results, r => Assert.Equal(2, r.Files));
    }

    [Fact]
    public async Task AHashOfALockedFileIsAPlainMessage()
    {
        var path = _temp.PathOf("locked.bin");
        File.WriteAllBytes(path, new byte[4096]);
        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var ex = await Assert.ThrowsAsync<InstallerAnalysisException>(() => _service.ComputeSha256Async(path, CancellationToken.None));

        Assert.Equal("The file could not be read to compute its SHA-256; another program may hold it open.", ex.Message);
    }
}
