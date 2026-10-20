using System.Diagnostics;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>
/// The entry count and the central directory size of a zip are checked from the file's own
/// records before any ZipArchive reads the central directory.
/// </summary>
public sealed class ZipDirectoryTests : IDisposable
{
    private readonly InstallerAnalysisTempFolder _temp = new();
    private readonly AnalysisSandbox _sandbox;
    private readonly InstallerAnalysisService _service;

    public ZipDirectoryTests()
    {
        _sandbox = new AnalysisSandbox(_temp.PathOf("sandbox"));
        _service = new InstallerAnalysisService(_sandbox, () => null);
    }

    public void Dispose()
    {
        _sandbox.ReleaseAll();
        _temp.Dispose();
    }

    private static readonly AnalysisLimits Limits = AnalysisLimits.Default with { Timeout = TimeSpan.FromSeconds(60) };

    private static void CentralRecord(BinaryWriter w, string name)
    {
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
        w.Write(0x02014b50u);
        w.Write((ushort)20);
        w.Write((ushort)20);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write(0u);
        w.Write(0u);
        w.Write(0u);
        w.Write(0u);
        w.Write((ushort)nameBytes.Length);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write(0u);
        w.Write(0u);
        w.Write(nameBytes);
    }

    private static void ClassicEnd(BinaryWriter w, ushort count, uint size, uint offset)
    {
        w.Write(0x06054b50u);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write(count);
        w.Write(count);
        w.Write(size);
        w.Write(offset);
        w.Write((ushort)0);
    }

    /// <summary>A zip whose end record declares one entry while the central directory holds <paramref name="records"/> real records.</summary>
    private string LyingZip(string name, int records)
    {
        var path = _temp.PathOf(name);
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        for (var i = 0; i < records; i++) CentralRecord(w, "a");
        ClassicEnd(w, 1, 47, 0);
        return path;
    }

    /// <summary>
    /// A sparse zip64 file: the central directory of 30 million 46-byte records starts with one
    /// real record and reads as zeros after it; the zip64 end record declares the full count and size.
    /// </summary>
    private string ThirtyMillionEntries(string name)
    {
        const long count = 30_000_000;
        const long size = count * 46;
        var path = _temp.PathOf(name);
        File.WriteAllBytes(path, []);
        using (var p = Process.Start(new ProcessStartInfo("fsutil.exe", $"sparse setflag \"{path}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            p.WaitForExit(10_000);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
        using var w = new BinaryWriter(stream);
        CentralRecord(w, "");
        stream.SetLength(size);
        stream.Position = size;
        w.Write(0x06064b50u);
        w.Write(44UL);
        w.Write((ushort)45);
        w.Write((ushort)45);
        w.Write(0u);
        w.Write(0u);
        w.Write((ulong)count);
        w.Write((ulong)count);
        w.Write((ulong)size);
        w.Write(0UL);
        w.Write(0x07064b50u);
        w.Write(0u);
        w.Write((ulong)size);
        w.Write(1u);
        ClassicEnd(w, 0xFFFF, 0xFFFFFFFF, 0xFFFFFFFF);
        return path;
    }

    [Theory]
    [InlineData("big.msix")]
    [InlineData("big.nupkg")]
    public async Task AZip64PackageDeclaringThirtyMillionEntriesIsRefusedWithAPlainMessage(string name)
    {
        var path = ThirtyMillionEntries(name);

        var analyze = await Assert.ThrowsAsync<InstallerAnalysisException>(() => _service.AnalyzeAsync(path, Limits, new Progress<string>(), CancellationToken.None));
        var list = await Assert.ThrowsAsync<InstallerAnalysisException>(() => _service.ListPayloadAsync(path, Limits, CancellationToken.None));

        Assert.Equal("The archive has more than 100,000 entries; it was not read.", analyze.Message);
        Assert.Equal("The archive has more than 100,000 entries; it was not listed.", list.Message);
    }

    [Fact]
    public async Task AZipWhoseEndRecordUnderstatesTheCentralDirectoryIsRefusedBeforeItIsLoaded()
    {
        var path = LyingZip("lying.nupkg", 100_002);

        var list = await Assert.ThrowsAsync<InstallerAnalysisException>(() => _service.ListPayloadAsync(path, Limits, CancellationToken.None));

        Assert.Equal("The archive has more than 100,000 entries; it was not listed.", list.Message);
    }

    [Fact]
    public void TheScannerDoesNotMaterializeTheNamesOfAnOversizedCentralDirectory()
    {
        var path = LyingZip("lying2.nupkg", 300_000);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var engine = InstallerSignatureScanner.Identify(path);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.NotEqual("NuGet", engine);
        Assert.True(allocated < 24L * 1024 * 1024, "allocated " + allocated);
    }

    [Fact]
    public void TheCheckReadsTheCountAndSizeOfAnOrdinaryZip()
    {
        var path = InstallerAnalysisTestFiles.WriteZip(_temp.PathOf("ok.zip"), ("a.txt", InstallerAnalysisTestFiles.Text("a")), ("b/c.txt", InstallerAnalysisTestFiles.Text("c")));
        using var stream = File.OpenRead(path);

        var directory = ZipCentralDirectory.Check(stream, 10, "listed");

        Assert.NotNull(directory);
        Assert.Equal(2, directory!.Entries);
        Assert.True(directory.Bytes > 0);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void TheCheckRefusesACountOverTheLimit()
    {
        var path = InstallerAnalysisTestFiles.WriteZip(_temp.PathOf("three.zip"), ("a", [1]), ("b", [2]), ("c", [3]));
        using var stream = File.OpenRead(path);

        var ex = Assert.Throws<InstallerAnalysisException>(() => ZipCentralDirectory.Check(stream, 2, "listed"));

        Assert.Equal("The archive has more than 2 entries; it was not listed.", ex.Message);
    }

    [Fact]
    public void AFileWithoutAnEndRecordIsNotAZip()
    {
        var path = _temp.PathOf("plain.bin");
        File.WriteAllBytes(path, new byte[1000]);
        using var stream = File.OpenRead(path);

        Assert.Null(ZipCentralDirectory.Check(stream, 10, "listed"));
    }
}
