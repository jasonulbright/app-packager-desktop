using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.DetectionTester;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DetectionReportWriterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-dtreports-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 14, 5, 9, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private DetectionReportWriter Writer() => new(_folder, () => Now);

    private static DetectionHistoryEntry Entry(string description, string summary = "ok", bool negative = false, bool passed = true,
        string? script = null) => new(
        Guid.NewGuid(), Now, script is null ? "RegistryKey" : "Script", description, negative, passed, summary,
        new StageDetection { Type = script is null ? "RegistryKey" : "Script", RegistryKeyRelative = "SOFTWARE\\X", ScriptText = script });

    [Fact]
    public void TheCsvFileIsTimeStampedUnderTheFolderAndHoldsAHeaderAndOneRowPerEntry()
    {
        var path = Writer().WriteCsv([Entry("first"), Entry("second", negative: true, passed: false)]);

        Assert.Equal(Path.Combine(_folder, "detection-history-20261018-140509.csv"), path);
        var lines = File.ReadAllLines(path);
        Assert.Equal("\"Time\",\"Kind\",\"Description\",\"Mode\",\"Result\",\"Summary\"", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"Negative\",\"Failed\"", lines[2]);
        Assert.Contains("\"Positive\",\"Passed\"", lines[1]);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tcmd")]
    [InlineData("\rcmd")]
    public void ACellThatStartsAFormulaIsNeutralized(string hostile)
    {
        var path = Writer().WriteCsv([Entry(hostile, summary: hostile)]);

        var row = File.ReadAllText(path).Split('\n')[1];
        Assert.DoesNotContain(",\"" + hostile[0], row);
        Assert.Contains(",\"'" + hostile[0], row);
    }

    [Fact]
    public void QuotesAndSeparatorsStayInsideTheirCell()
    {
        var path = Writer().WriteCsv([Entry("a \"quoted\", value\nnext line")]);

        var text = File.ReadAllText(path);
        Assert.Contains("\"a \"\"quoted\"\", value\nnext line\"", text);
    }

    [Fact]
    public void TheHtmlEncodesEveryValueAndCarriesNoScriptText()
    {
        var path = Writer().WriteHtml([
            Entry("<script>alert(1)</script>", summary: "a & b \"c\""),
            Entry("script clause", script: "Write-Output 'SECRET-BODY'")
        ]);

        Assert.Equal(Path.Combine(_folder, "detection-history-20261018-140509.html"), path);
        var html = File.ReadAllText(path);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("a &amp; b &quot;c&quot;", html);
        Assert.DoesNotContain("SECRET-BODY", html);
        Assert.Contains("Content-Security-Policy", html);
    }

    [Fact]
    public void TwoReportsInTheSameSecondGetDistinctNames()
    {
        var first = Writer().WriteCsv([Entry("a")]);
        var second = Writer().WriteCsv([Entry("b")]);

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void AnEmptyHistoryWritesOnlyTheHeader()
    {
        var path = Writer().WriteCsv([]);

        Assert.Single(File.ReadAllLines(path));
    }

    [Fact]
    public void TheDefaultFolderIsTheReportsFolderUnderTheDetectionTesterFolder() =>
        Assert.Equal(Path.Combine(AppDataPaths.Root, "DetectionTester", "Reports"), DetectionReportWriter.DefaultFolder());
}
