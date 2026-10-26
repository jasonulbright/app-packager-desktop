using System.Diagnostics;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.InstallerScripts;
using static ApplicationPackagerEngine.Tests.InstallerAnalysis.InstallerAnalysisTestFiles;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>Length fields read from a file never size an allocation before they are checked.</summary>
public sealed class LengthFieldTests : IDisposable
{
    private readonly InstallerAnalysisTempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// An NSIS stub: "MZ", padding to 512, the 28-byte firstheader with the given header length,
    /// then a solid LZMA stream (properties 0x5D, dictionary 64 KB) of <paramref name="zeroBytes"/> zeros.
    /// </summary>
    private string CraftedNsis(string name, uint headerLength, int zeroBytes)
    {
        var bytes = new byte[512 + 28 + 5 + zeroBytes];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        var at = 512;
        BitConverter.GetBytes(0u).CopyTo(bytes, at);
        BitConverter.GetBytes(0xDEADBEEFu).CopyTo(bytes, at + 4);
        "NullsoftInst"u8.ToArray().CopyTo(bytes, at + 8);
        BitConverter.GetBytes(headerLength).CopyTo(bytes, at + 20);
        BitConverter.GetBytes((uint)(bytes.Length - 512)).CopyTo(bytes, at + 24);
        var data = at + 28;
        bytes[data] = 0x5D;
        bytes[data + 1] = 0;
        bytes[data + 2] = 0;
        bytes[data + 3] = 1;
        bytes[data + 4] = 0;
        var path = _temp.PathOf(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void AnNsisHeaderLengthNear2GigabytesIsRefusedBeforeAnyLargeAllocation()
    {
        var path = CraftedNsis("huge-header.exe", 0x7FFFFF00, 16 * 1024 * 1024);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();

        var table = NsisScriptReader.ReadHeaderTable(path);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Null(table.Header);
        Assert.Equal("The NSIS header length field is larger than the 64 MB limit; the header was not decoded.", table.Note);
        Assert.True(allocated < 200L * 1024 * 1024, "allocated " + allocated);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "took " + clock.Elapsed);
    }

    [Fact]
    public void AnNsisHeaderLengthAboveInt32IsRefusedWithTheSameNote()
    {
        var path = CraftedNsis("negative-header.exe", 0xFFFFFF00, 4096);

        var table = NsisScriptReader.ReadHeaderTable(path);

        Assert.Null(table.Header);
        Assert.Equal("The NSIS header length field is larger than the 64 MB limit; the header was not decoded.", table.Note);
    }

    [Fact]
    public async Task TheAnalysisOfAnNsisFileWithAHugeHeaderLengthGivesAPlainNote()
    {
        var path = CraftedNsis("huge-analysis.exe", 0x7FFFFF00, 16 * 1024 * 1024);
        var sandbox = new AnalysisSandbox(_temp.PathOf("sandbox"));
        var service = new InstallerAnalysisService(sandbox, () => null);

        var report = await service.AnalyzeAsync(path, AnalysisLimits.Default with { Timeout = TimeSpan.FromSeconds(60) }, new Progress<string>(), CancellationToken.None);

        Assert.Equal("NSIS", report.Type);
        Assert.Contains("The NSIS header length field is larger than the 64 MB limit; the header was not decoded.", report.Notes);
    }

    [Fact]
    public void TheLzmaDecoderRefusesAnOutputSizeOverItsLimit()
    {
        var input = new byte[64];
        Assert.Throws<InvalidDataException>(() => LzmaDecoder.Decode(input, 0, input.Length, 0x5D, 1 << 16, LzmaDecoder.MaxOutputBytes + 1, out _));
    }

    [Fact]
    public void TheLzmaDecoderGrowsItsBufferWithTheOutputInsteadOfAllocatingTheBudget()
    {
        // A range-coder stream of zeros after the leading zero byte decodes literal zeros until the input ends.
        var input = new byte[4096];
        var before = GC.GetAllocatedBytesForCurrentThread();

        var output = LzmaDecoder.Decode(input, 0, input.Length, 0x5D, 1 << 16, 160 << 20, out var corrupted);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(corrupted);
        Assert.True(output.Length < 160 << 20);
        Assert.True(allocated < 64L * 1024 * 1024, "allocated " + allocated);
    }

    [Fact]
    public void BitFlipsInTheNsisHeaderLengthFieldOfARealInstallerGiveANoteOrAHeader()
    {
        var original = File.ReadAllBytes(Fixture("nsis-mk-lzma-solid-unicode.bin"));
        var at = NsisScriptReader.FindFirstHeader(original);
        Assert.True(at >= 0);
        for (var bit = 0; bit < 32; bit++)
        {
            var copy = (byte[])original.Clone();
            var field = BitConverter.ToUInt32(copy, at + 20) ^ (1u << bit);
            BitConverter.GetBytes(field).CopyTo(copy, at + 20);
            var path = _temp.PathOf($"flip-{bit}.exe");
            File.WriteAllBytes(path, copy);
            var before = GC.GetAllocatedBytesForCurrentThread();

            var table = NsisScriptReader.ReadHeaderTable(path);

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(table.Header is not null || table.Note.Length > 0, "bit " + bit);
            Assert.True(allocated < 300L * 1024 * 1024, $"bit {bit} allocated {allocated}");
        }
    }

    [Fact]
    public void AnMsiStringLongerThanTheFieldLimitIsCutWithoutReadingItWhole()
    {
        var msi = _temp.PathOf("long.msi");
        var value = new string('x', 2_000_000);
        using (var b = new TestMsiBuilder(msi))
            b.Property("ProductName", value);

        using var db = MsiDatabase.Open(msi);
        string? text = null;
        long allocated = 0;
        db.Query("SELECT `Value` FROM `Property`", r =>
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            text = r.String(1, maxChars: 100);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            return false;
        }, 10, CancellationToken.None);

        Assert.Equal(new string('x', 100), text);
        Assert.True(allocated < 256 * 1024, "allocated " + allocated);
    }

    [Fact]
    public void AnMsiStringUnderTheFieldLimitIsReadWhole()
    {
        var msi = _temp.PathOf("short.msi");
        using (var b = new TestMsiBuilder(msi))
            b.Property("ProductName", "Contoso Tool");

        using var db = MsiDatabase.Open(msi);
        string? text = null;
        db.Query("SELECT `Value` FROM `Property`", r => { text = r.String(1, maxChars: 100); return false; }, 10, CancellationToken.None);

        Assert.Equal("Contoso Tool", text);
    }
}
