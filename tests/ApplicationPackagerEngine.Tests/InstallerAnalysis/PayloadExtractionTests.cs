using System.Diagnostics;
using System.IO.Compression;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.InstallerAnalysis.InstallerAnalysisTestFiles;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>Payload listing and extraction on real archives, including hostile ones.</summary>
public sealed class PayloadExtractionTests : IDisposable
{
    private readonly InstallerAnalysisTempFolder _temp = new();
    private readonly AnalysisSandbox _sandbox;
    private readonly InstallerAnalysisService _service;
    private readonly InstallerAnalysisService _noSevenZip;

    public PayloadExtractionTests()
    {
        _sandbox = new AnalysisSandbox(_temp.PathOf("sandbox"));
        _service = new InstallerAnalysisService(_sandbox);
        _noSevenZip = new InstallerAnalysisService(_sandbox, () => null);
    }

    public void Dispose()
    {
        _sandbox.ReleaseAll();
        _temp.Dispose();
    }

    private static readonly AnalysisLimits Limits = AnalysisLimits.Default;

    private string Target()
    {
        var folder = _temp.PathOf("out-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public async Task AnMsiListsItsStreamsAndExtractsACabinetWithout7Zip()
    {
        var cab = _temp.PathOf("payload.bin");
        File.WriteAllBytes(cab, Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray());
        var msi = _temp.PathOf("streams.msi");
        using (var b = new TestMsiBuilder(msi))
            b.Property("ProductCode", "{DDDDDDDD-1111-2222-3333-444444444444}").Stream("cab1.cab", cab).Stream("Binary.Helper", cab).Template("x64;1033");

        var payload = await _noSevenZip.ListPayloadAsync(msi, Limits, CancellationToken.None);

        var cabinet = payload.Single(e => e.Path == "cab1.cab");
        Assert.Equal("InnerInstaller", cabinet.Kind);
        Assert.Equal("CAB", cabinet.InnerType);
        Assert.Equal("MSI Media-table cabinet", cabinet.Description);
        Assert.Equal(5000, cabinet.Size);
        Assert.Equal("File", payload.Single(e => e.Path == "Binary.Helper").Kind);

        var session = _sandbox.NewSession();
        var extracted = await _noSevenZip.ExtractEntryAsync(msi, cabinet, session, Limits, CancellationToken.None);
        Assert.StartsWith(session, extracted);
        Assert.Equal(File.ReadAllBytes(cab), File.ReadAllBytes(extracted));
    }

    [Fact]
    public async Task AZipPackageListsAndExtractsWithout7ZipAndMarksInnerInstallers()
    {
        var path = WriteZip(_temp.PathOf("bundle.zip"), ("setup/inner.msi", Text("msi")), ("readme.txt", Text("hello")), ("tools/app.exe", Text("MZ")));

        var payload = await _noSevenZip.ListPayloadAsync(path, Limits, CancellationToken.None);

        Assert.Equal("MSI", payload.Single(e => e.Path == "setup/inner.msi").InnerType);
        Assert.Equal("EXE", payload.Single(e => e.Path == "tools/app.exe").InnerType);
        Assert.Equal("File", payload.Single(e => e.Path == "readme.txt").Kind);
        var extracted = await _noSevenZip.ExtractEntryAsync(path, payload.Single(e => e.Path == "setup/inner.msi"), _sandbox.NewSession(), Limits, CancellationToken.None);
        Assert.Equal("msi", File.ReadAllText(extracted));
        Assert.Equal("inner.msi", Path.GetFileName(extracted));
    }

    [Fact]
    public async Task HostileEntryNamesAreSkippedAndNothingLandsOutsideTheTarget()
    {
        var hostile = _temp.PathOf("hostile.zip");
        using (var stream = File.Create(hostile))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { @"..\evil1.txt", "../evil2.txt", @"a\..\..\evil3.txt", @"C:\evil4.txt", "/evil5.txt", @"\\server\share\evil6.txt",
                                         "CON.txt", "dir/AUX", "dir/name.", "dir/space ", "dir/stream.txt:ads", "good/ok.txt" })
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write("x");
            }
            var link = zip.CreateEntry("link");
            link.ExternalAttributes = 0xA1FF << 16;
            using (var w = new StreamWriter(link.Open())) w.Write(@"C:\Windows");
        }
        var target = Target();

        var result = await _noSevenZip.ExtractPayloadAsync(hostile, null, target, Limits, CancellationToken.None);

        Assert.Equal(1, result.Files);
        Assert.Equal(12, result.Skipped.Count);
        Assert.Contains(result.Skipped, s => s.StartsWith("link:") && s.Contains("symbolic link"));
        Assert.Contains(result.Skipped, s => s.StartsWith("CON.txt:") && s.Contains("device name"));
        Assert.Contains(result.Skipped, s => s.StartsWith("dir/name.:") && s.Contains("ends with a dot"));
        Assert.True(File.Exists(Path.Combine(result.Folder, "good", "ok.txt")));
        var written = Directory.GetFiles(_temp.Root, "*", SearchOption.AllDirectories).Where(f => !f.StartsWith(_temp.PathOf("sandbox"))).ToList();
        Assert.Equal(2, written.Count);
        Assert.DoesNotContain(written, f => Path.GetFileName(f).StartsWith("evil"));
        Assert.False(File.Exists(@"C:\evil4.txt"));
    }

    [Fact]
    public async Task ATraversalEntryIsRefusedForSingleExtraction()
    {
        var path = WriteZip(_temp.PathOf("trav.zip"), (@"..\..\escape.msi", Text("x")));
        var entry = (await _noSevenZip.ListPayloadAsync(path, Limits, CancellationToken.None)).Single();

        var ex = await Assert.ThrowsAsync<InstallerAnalysisException>(() =>
            _noSevenZip.ExtractEntryAsync(path, entry, _sandbox.NewSession(), Limits, CancellationToken.None));

        Assert.Equal("The entry name leaves the extraction folder. It was not extracted.", ex.Message);
    }

    [Fact]
    public async Task AHighRatioEntryIsStoppedAndLeavesNoPartialFile()
    {
        var path = _temp.PathOf("bomb.zip");
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry("zeros.msi", CompressionLevel.SmallestSize).Open())
        {
            var block = new byte[1024 * 1024];
            for (var i = 0; i < 64; i++) entry.Write(block);
        }
        var limits = Limits with { MaxCompressionRatio = 20 };
        var entryInfo = (await _noSevenZip.ListPayloadAsync(path, limits, CancellationToken.None)).Single();
        var session = _sandbox.NewSession();

        var ex = await Assert.ThrowsAsync<InstallerAnalysisException>(() => _noSevenZip.ExtractEntryAsync(path, entryInfo, session, limits, CancellationToken.None));

        Assert.Equal("The entry expands far beyond its compressed size; it was not extracted.", ex.Message);
        Assert.Empty(Directory.GetFiles(session, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AnEntryOverTheExtractLimitIsRefused()
    {
        var path = WriteZip(_temp.PathOf("big.zip"), ("big.msi", new byte[200_000]));
        var limits = Limits with { MaxExtractBytes = 100_000 };
        var target = Target();

        var ex = await Assert.ThrowsAsync<InstallerAnalysisException>(() => _noSevenZip.ExtractPayloadAsync(path, null, target, limits, CancellationToken.None));

        Assert.Equal("The entry is larger than the extraction limit; it was not extracted.", ex.Message);
        Assert.Empty(Directory.GetFiles(target, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AnArchiveWithMoreEntriesThanTheLimitIsNotListed()
    {
        var path = _temp.PathOf("many.zip");
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            for (var i = 0; i <= 100_000; i++) zip.CreateEntry("f" + i, CompressionLevel.NoCompression);

        var ex = await Assert.ThrowsAsync<InstallerAnalysisException>(() => _noSevenZip.ListPayloadAsync(path, Limits, CancellationToken.None));

        Assert.Equal("The archive has more than 100,000 entries; it was not listed.", ex.Message);
    }

    [Fact]
    public async Task TheSandboxAndLinkedFoldersAreRefusedAsExtractionTargets()
    {
        var path = WriteZip(_temp.PathOf("p.zip"), ("a.txt", Text("a")));
        var inside = _sandbox.NewSession();
        var real = Target();
        var link = _temp.PathOf("link-target");
        Junction(link, real);

        var e1 = await Assert.ThrowsAsync<InstallerAnalysisException>(() => _noSevenZip.ExtractPayloadAsync(path, null, inside, Limits, CancellationToken.None));
        var e2 = await Assert.ThrowsAsync<InstallerAnalysisException>(() => _noSevenZip.ExtractPayloadAsync(path, null, link, Limits, CancellationToken.None));

        Assert.Equal("The analysis sandbox cannot be the target folder; choose another folder.", e1.Message);
        Assert.Equal("The target folder is a junction or symbolic link; choose another folder.", e2.Message);
        Assert.Empty(Directory.GetFileSystemEntries(real));
    }

    [Fact]
    public async Task SingleExtractionWritesOnlyIntoTheSandbox()
    {
        var path = WriteZip(_temp.PathOf("q.zip"), ("a.msi", Text("a")));
        var entry = (await _noSevenZip.ListPayloadAsync(path, Limits, CancellationToken.None)).Single();

        var ex = await Assert.ThrowsAsync<InstallerAnalysisException>(() => _noSevenZip.ExtractEntryAsync(path, entry, Target(), Limits, CancellationToken.None));

        Assert.Equal("Entries are extracted only into the analysis sandbox.", ex.Message);
    }

    [Fact]
    public async Task Without7ZipAnExecutablePayloadGivesAPlainMessage()
    {
        var ex = await Assert.ThrowsAsync<InstallerAnalysisException>(() =>
            _noSevenZip.ListPayloadAsync(Fixture("nsis-mk-lzma-solid-unicode.bin"), Limits, CancellationToken.None));

        Assert.StartsWith("7-Zip is not installed;", ex.Message);
    }

    [Fact]
    public async Task A7ZipRunPastTheTimeLimitEndsWithAPlainMessage()
    {
        var fake = _temp.PathOf("fake7z.cmd");
        File.WriteAllText(fake, "@ping -n 30 127.0.0.1 >nul\r\n");
        var service = new InstallerAnalysisService(_sandbox, () => fake);
        var limits = Limits with { Timeout = TimeSpan.FromSeconds(2) };
        var clock = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<InstallerAnalysisException>(() => service.ListPayloadAsync(Fixture("nsis-mk-lzma-solid-unicode.bin"), limits, CancellationToken.None));

        Assert.Contains("time limit", ex.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task With7ZipAnNsisInstallerListsAndExtractsItsPayload()
    {
        var sevenZip = ArchiveExtractionService.FindSevenZip();
        var fallback = await _noSevenZip.ListPayloadAsync(SevenZipMsi, Limits, CancellationToken.None);
        Assert.NotEmpty(fallback);
        Assert.True(sevenZip is not null, "7-Zip is not installed on this machine; install 7-Zip 25.01 or later to run this test.");

        var payload = await _service.ListPayloadAsync(Fixture("nsis-mk-lzma-solid-unicode.bin"), Limits, CancellationToken.None);
        var file = payload.First(e => !e.IsDirectory);
        var extracted = await _service.ExtractEntryAsync(Fixture("nsis-mk-lzma-solid-unicode.bin"), file, _sandbox.NewSession(), Limits, CancellationToken.None);

        Assert.True(File.Exists(extracted));
        Assert.StartsWith(_sandbox.Root, extracted);
        var target = Target();
        var all = await _service.ExtractPayloadAsync(Fixture("nsis-mk-lzma-solid-unicode.bin"), null, target, Limits, CancellationToken.None);
        Assert.Equal(payload.Count(e => !e.IsDirectory), all.Files);
    }

    [Fact]
    public void TheTechnicalListingParserKeepsKnownFieldsAndIgnoresUnknownLines()
    {
        var output = new[]
        {
            "7-Zip 26.04 (x64)", "", "Scanning the drive for archives:", "Listing archive: x.exe", "--", "Path = x.exe", "Type = Nsis", "",
            "----------", "Path = $PLUGINSDIR\\a.dll", "Size = 1234", "Packed Size = ", "Some Future Field = 7", "Attributes = A", "",
            "Path = sub", "Folder = +", "", "WARNING: something odd", "Path = link.lnk", "Size = x", "Attributes = A L", ""
        };

        var entries = SevenZipTool.ParseTechnicalListing(output, 10);

        Assert.Equal(3, entries.Count);
        Assert.Equal(new SevenZipEntry(@"$PLUGINSDIR\a.dll", 1234, false, false), entries[0]);
        Assert.True(entries[1].IsDirectory);
        Assert.True(entries[2].IsLink);
        Assert.Equal(0, entries[2].Size);
    }
}
