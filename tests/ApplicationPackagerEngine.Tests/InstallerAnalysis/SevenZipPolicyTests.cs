using System.Formats.Tar;
using System.IO.Compression;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.InstallerAnalysis.InstallerAnalysisTestFiles;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>
/// The 7-Zip extraction policy on the real 7z.exe of this workstation: the version gate, the size
/// check before extraction, link entries left out, the size watcher and the bounded listing.
/// These tests need 7-Zip and fail with a message when it is absent.
/// </summary>
public sealed class SevenZipPolicyTests : IDisposable
{
    private readonly InstallerAnalysisTempFolder _temp = new();
    private readonly AnalysisSandbox _sandbox;
    private static readonly AnalysisLimits Limits = AnalysisLimits.Default with { Timeout = TimeSpan.FromSeconds(120) };

    public SevenZipPolicyTests() => _sandbox = new AnalysisSandbox(_temp.PathOf("sandbox"));

    public void Dispose()
    {
        _sandbox.ReleaseAll();
        _temp.Dispose();
    }

    private static string RequireSevenZip()
    {
        var exe = ArchiveExtractionService.FindSevenZip();
        Assert.True(exe is not null, "7-Zip is not installed on this machine; install 7-Zip 25.01 or later to run the 7-Zip tests.");
        return exe!;
    }

    private string Target()
    {
        var folder = _temp.PathOf("out-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A copy of cmd.exe named 7z.exe: a real executable whose file version (10.x) is older than 25.01.</summary>
    private string OldSevenZip()
    {
        var folder = Directory.CreateDirectory(_temp.PathOf("old7z")).FullName;
        var exe = Path.Combine(folder, "7z.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe, true);
        return exe;
    }

    private string TarWithLink()
    {
        var path = _temp.PathOf("links.bin");
        using var stream = File.Create(path);
        using var writer = new TarWriter(stream);
        writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "evil") { LinkName = @"C:\Windows" });
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "ok.txt") { DataStream = new MemoryStream("A"u8.ToArray()) });
        return path;
    }

    [Fact]
    public void TheInstalled7ZipVersionIsReadAndMeetsTheExtractionMinimum()
    {
        var exe = RequireSevenZip();

        var version = SevenZipTool.ReadVersion(exe);

        Assert.NotNull(version);
        Assert.True(version >= SevenZipTool.MinimumExtractionVersion, $"7-Zip {version} at {exe} is older than {SevenZipTool.MinimumExtractionVersion}.");
    }

    [Theory]
    [InlineData(24, 9, false)]
    [InlineData(25, 0, false)]
    [InlineData(25, 1, true)]
    [InlineData(26, 4, true)]
    public void TheExtractionMinimumIs25Point01(int major, int minor, bool allowed)
    {
        Assert.Equal(allowed, new Version(major, minor) >= SevenZipTool.MinimumExtractionVersion);
    }

    [Fact]
    public async Task AnOld7ZipIsRefusedForExtractionBeforeAnythingIsWritten()
    {
        var service = new InstallerAnalysisService(_sandbox, OldSevenZip);
        var target = Target();
        var entry = new PayloadEntry("$PLUGINSDIR\\a.dll", 10, false, "File", "");

        var all = await Assert.ThrowsAsync<InstallerAnalysisException>(() => service.ExtractPayloadAsync(Fixture("nsis-mk-lzma-solid-unicode.bin"), null, target, Limits, CancellationToken.None));
        var one = await Assert.ThrowsAsync<InstallerAnalysisException>(() => service.ExtractEntryAsync(Fixture("nsis-mk-lzma-solid-unicode.bin"), entry, _sandbox.NewSession(), Limits, CancellationToken.None));

        Assert.StartsWith("7-Zip 10.", all.Message);
        Assert.Contains("older than 25.01", all.Message);
        Assert.Contains("7-zip.org", all.Message);
        Assert.Equal(all.Message, one.Message);
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public async Task TheListedSizesAreCheckedBeforeExtraction()
    {
        RequireSevenZip();
        var service = new InstallerAnalysisService(_sandbox);
        var target = Target();

        var ex = await Assert.ThrowsAsync<InstallerAnalysisException>(() =>
            service.ExtractPayloadAsync(Fixture("nsis-mk-lzma-solid-unicode.bin"), null, target, Limits with { MaxExtractBytes = 5 }, CancellationToken.None));

        Assert.Equal("The payload is larger than the extraction limit of 5 bytes; nothing was extracted.", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public async Task LinkEntriesAreListedAsLinksAndLeftOutOfExtraction()
    {
        RequireSevenZip();
        var service = new InstallerAnalysisService(_sandbox);
        var path = TarWithLink();

        var payload = await service.ListPayloadAsync(path, Limits, CancellationToken.None);
        var result = await service.ExtractPayloadAsync(path, null, Target(), Limits, CancellationToken.None);

        Assert.Equal("Link; it is not extracted", payload.Single(e => e.Path == "evil").Description);
        Assert.Equal("Link", payload.Single(e => e.Path == "evil").Kind);
        Assert.Equal("File", payload.Single(e => e.Path == "ok.txt").Kind);
        Assert.Equal("", payload.Single(e => e.Path == "ok.txt").Description);
        Assert.Equal(1, result.Files);
        Assert.Contains(result.Skipped, s => s.StartsWith("evil:", StringComparison.Ordinal) && s.Contains("link"));
        Assert.False(File.Exists(Path.Combine(result.Folder, "evil")) || Directory.Exists(Path.Combine(result.Folder, "evil")));
        await Assert.ThrowsAsync<InstallerAnalysisException>(() => service.ExtractEntryAsync(path, payload.Single(e => e.Path == "evil"), _sandbox.NewSession(), Limits, CancellationToken.None));
    }

    [Fact]
    public void TheTarListingDoesNotMarkEveryEntryAsALink()
    {
        var output = new[]
        {
            "----------", "Path = evil", "Mode = lrw-r--r--", "Symbolic Link = C:\\Windows", "Hard Link = ", "",
            "Path = ok.txt", "Mode = -rw-r--r--", "Symbolic Link = ", "Hard Link = ", ""
        };

        var entries = SevenZipTool.ParseTechnicalListing(output, 10);

        Assert.True(entries[0].IsLink);
        Assert.False(entries[1].IsLink);
    }

    /// <summary>A zip of 512 MB of zeros; with <paramref name="declaredSize"/> the local and central headers claim that size instead.</summary>
    private string Zeros(string name, uint? declaredSize = null)
    {
        var path = _temp.PathOf(name);
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry("zeros.dat", CompressionLevel.SmallestSize).Open())
        {
            var block = new byte[1024 * 1024];
            for (var i = 0; i < 512; i++) entry.Write(block);
        }
        if (declaredSize is { } size)
        {
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(0x04034b50u, BitConverter.ToUInt32(bytes, 0));
            BitConverter.GetBytes(size).CopyTo(bytes, 22);
            var central = bytes.Length - 22;
            while (BitConverter.ToUInt32(bytes, central) != 0x02014b50) central--;
            BitConverter.GetBytes(size).CopyTo(bytes, central + 24);
            File.WriteAllBytes(path, bytes);
        }
        return path;
    }

    [Fact]
    public async Task TheSizeWatcherStopsAnExtractionThatPassesTheLimit()
    {
        var exe = RequireSevenZip();
        var path = Zeros("zeros.bin");
        var folder = Directory.CreateDirectory(_temp.PathOf("watch")).FullName;

        var (_, overLimit, timedOut) = await new SevenZipTool(exe).ExtractAsync(path, null, null, folder, 8L * 1024 * 1024, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.True(overLimit);
        Assert.False(timedOut);
    }

    [Fact]
    public async Task AZipThatUnderstatesItsSizeIsCutAtTheDeclaredSizeBy7Zip()
    {
        RequireSevenZip();
        var service = new InstallerAnalysisService(_sandbox);
        var path = Zeros("understated.bin", 1000);

        var result = await service.ExtractPayloadAsync(path, null, Target(), Limits with { MaxExtractBytes = 8L * 1024 * 1024 }, CancellationToken.None);

        Assert.Equal(1, result.Files);
        Assert.Equal(1000, result.Bytes);
        Assert.Contains("7-Zip reported warnings; some entries may be missing.", result.Skipped);
    }

    [Fact]
    public async Task AnEntryWithoutAListedSizeThatPassesTheLimitIsStoppedAndItsPartialFilesAreRemoved()
    {
        RequireSevenZip();
        var service = new InstallerAnalysisService(_sandbox);
        var path = Fixture("nsis-mk-lzma-solid-unicode.bin");
        var target = Target();
        var limits = Limits with { MaxExtractBytes = 20 };
        var uninstaller = (await service.ListPayloadAsync(path, limits, CancellationToken.None)).Single(e => e.Path == "Uninstall.exe");
        var session = _sandbox.NewSession();

        var all = await Assert.ThrowsAsync<InstallerAnalysisException>(() => service.ExtractPayloadAsync(path, null, target, limits, CancellationToken.None));
        var one = await Assert.ThrowsAsync<InstallerAnalysisException>(() => service.ExtractEntryAsync(path, uninstaller, session, limits, CancellationToken.None));

        Assert.Equal(0, uninstaller.Size);
        Assert.Equal("The payload expands beyond the extraction limit; extraction stopped and the partial files were removed.", all.Message);
        Assert.Equal("The entry expands beyond the extraction limit; it was not extracted.", one.Message);
        Assert.Empty(Directory.GetFileSystemEntries(target));
        Assert.Empty(Directory.GetFileSystemEntries(session));
    }

    [Fact]
    public async Task AListingWithAnOverlongNameIsRefused()
    {
        var listing = _temp.PathOf("long-listing.txt");
        File.WriteAllText(listing, "----------\r\nPath = " + new string('a', SevenZipTool.MaxLineChars + 10) + "\r\n\r\n");
        var fake = _temp.PathOf("long7z.cmd");
        File.WriteAllText(fake, "@type \"" + listing + "\"\r\n");
        var tool = new SevenZipTool(fake);

        var ex = await Assert.ThrowsAsync<InstallerAnalysisException>(() => tool.ListAsync(Fixture("nsis-mk-lzma-solid-unicode.bin"), Limits, CancellationToken.None));

        Assert.Equal($"The archive listing has a line longer than {SevenZipTool.MaxLineChars:N0} characters; it was not listed.", ex.Message);
    }

    [Fact]
    public async Task AListingOverTheOutputLimitIsRefused()
    {
        var fake = _temp.PathOf("chatty7z.cmd");
        File.WriteAllText(fake, "@echo off\r\necho ----------\r\n:loop\r\necho Packed Size = 0000000000000000000000000000000000000000000000000000000000000000000000000000\r\ngoto loop\r\n");
        var tool = new SevenZipTool(fake) { MaxOutputChars = 256 * 1024 };

        var ex = await Assert.ThrowsAsync<InstallerAnalysisException>(() => tool.ListAsync(Fixture("nsis-mk-lzma-solid-unicode.bin"), Limits, CancellationToken.None));

        Assert.Equal("The archive listing is larger than the output limit; it was not listed.", ex.Message);
    }
}
