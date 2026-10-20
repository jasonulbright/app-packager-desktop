using System.Text;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.InstallerScripts;
using static ApplicationPackagerEngine.Tests.InstallerAnalysis.InstallerAnalysisTestFiles;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>The string scan memory bound, the shared NSIS registry view, the CSV formula guard and the device names.</summary>
public sealed class ScannerAndGuardTests : IDisposable
{
    private readonly InstallerAnalysisTempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheStringScanTracksOnlyTheStringsItKeeps()
    {
        var path = _temp.PathOf("words.bin");
        var random = new Random(11);
        using (var stream = File.Create(path))
        {
            var word = new byte[11];
            for (var i = 0; i < 400_000; i++)
            {
                for (var c = 0; c < 10; c++) word[c] = (byte)('a' + random.Next(26));
                word[10] = 0;
                stream.Write(word);
            }
        }

        var results = InstallerStringScanner.Scan(path, 6, 100, long.MaxValue, CancellationToken.None, out var tracked);

        Assert.Equal(50, results.Count);
        Assert.True(tracked <= 100, "tracked " + tracked);
    }

    [Fact]
    public void TheStringScanStillReportsEachKeptStringOnce()
    {
        var path = _temp.PathOf("repeat.bin");
        var text = Encoding.ASCII.GetBytes("https://contoso.example/setup\0");
        File.WriteAllBytes(path, Enumerable.Repeat(text, 50).SelectMany(b => b).ToArray());

        var results = InstallerStringScanner.Scan(path, 6, 100, long.MaxValue, CancellationToken.None, out _);

        Assert.Single(results);
        Assert.Equal("Url", results[0].Category);
    }

    [Theory]
    [InlineData(new[] { 512, 256, -1 }, "32")]
    [InlineData(new[] { 256, -1 }, "")]
    [InlineData(new[] { 512, -1, -1 }, "32")]
    [InlineData(new[] { 256, 512, -1 }, "64")]
    [InlineData(new[] { 256, 0 }, "")]
    [InlineData(new[] { 512 }, "32")]
    public void TheNsisRegistryViewFollowsSetRegViewAndLastUsed(int[] sequence, string expected)
    {
        var view = new NsisRegView();
        foreach (var step in sequence)
        {
            if (step < 0) view.LastUsed();
            else view.Set(step);
        }

        Assert.Equal(expected, view.Current);
    }

    [Fact]
    public void RegistryRowsOfAScriptThatSetsTheViewIn64BitInitAreIn64BitView()
    {
        var script = NsisScriptReader.Read(Fixture("nsis-key-init.bin"));
        var (rows, note) = NsisRegistryReader.Read(Fixture("nsis-key-init.bin"), 1000);

        var arp = rows.Where(r => r.Key.Contains(@"\Uninstall\", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(arp);
        Assert.Equal("64", script.RegistryView);
        Assert.All(arp, r => Assert.Equal("64", r.View));
        Assert.Contains("SetRegView 64", note);
    }

    [Fact]
    public void RegistryRowsOfAScriptWithoutSetRegViewKeepAnEmptyView()
    {
        var (rows, note) = NsisRegistryReader.Read(Fixture("nsis-u-pf-hklm-wow.bin"), 1000);

        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal("", r.View));
        Assert.Equal("", note);
    }

    [Theory]
    [InlineData("\n=HYPERLINK(\"x\")")]
    [InlineData("\r=1+1")]
    [InlineData("\t=1+1")]
    [InlineData(" =1+1")]
    [InlineData("  @SUM(A1)")]
    [InlineData("=1+1")]
    [InlineData("-2")]
    public void ACellAFormulaReaderWouldEvaluateStartsWithAnApostrophe(string value)
    {
        Assert.StartsWith("\"'", ReportFile.Cell(value));
    }

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData(" text", "\" text\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    public void AnOrdinaryCellIsOnlyQuoted(string value, string expected)
    {
        Assert.Equal(expected, ReportFile.Cell(value));
    }

    [Theory]
    [InlineData("COM¹")]
    [InlineData("COM².txt")]
    [InlineData("dir\\COM³.log")]
    [InlineData("LPT¹")]
    [InlineData("LPT².txt")]
    [InlineData("LPT³.dat")]
    public void SuperscriptDeviceNamesAreRefused(string name)
    {
        Assert.Null(SandboxPath.Normalize(name, out var reason));
        Assert.Equal("The entry name is a device name.", reason);
    }

    [Fact]
    public void TheDeviceNameListHoldsNoTwoCharacterMojibake()
    {
        Assert.NotNull(SandboxPath.Normalize("COMÂ¹.txt", out _));
    }
}
