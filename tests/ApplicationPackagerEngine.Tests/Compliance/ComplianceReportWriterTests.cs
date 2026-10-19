using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.DetectionTester;

namespace ApplicationPackagerEngine.Tests.Compliance;

public sealed class ComplianceReportWriterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-compreports-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 30, 5, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private ComplianceReportWriter Writer(string? folder = null) => new(folder ?? _folder, () => Now);

    private static ComplianceRow Row(string baseline = "LLMNR", string collection = "Pilot") =>
        new(baseline, collection, 3, 1, 0, 2, new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc), true);

    [Fact]
    public void TheComplianceCsvHasAHeaderAndOneQuotedRowPerDeployment()
    {
        var path = Writer().WriteCsv([Row()]);

        Assert.Equal(Path.Combine(_folder, "compliance-status-20261019-093005.csv"), path);
        var lines = File.ReadAllLines(path);
        Assert.Equal("\"Baseline\",\"Collection\",\"Compliant\",\"Non-compliant\",\"Error\",\"Unknown\",\"Last evaluated (UTC)\",\"Stale\"", lines[0]);
        Assert.Equal("\"LLMNR\",\"Pilot\",\"3\",\"1\",\"0\",\"2\",\"2026-10-10 08:00\",\"Yes\"", lines[1]);
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(path)[..3]);
    }

    [Fact]
    public void TheDriftCsvNamesTheVersions()
    {
        var lines = File.ReadAllLines(Writer().WriteCsv([new DriftRow("Hand made", "16780003", null, null, null, "Untagged"), new DriftRow("LLMNR", "1", "llmnr-disable", "1.0.0", "1.1.0", "Behind")]));

        Assert.Equal("\"Hand made\",\"16780003\",\"\",\"\",\"\",\"Untagged\"", lines[1]);
        Assert.Equal("\"LLMNR\",\"1\",\"llmnr-disable\",\"1.0.0\",\"1.1.0\",\"Behind\"", lines[2]);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tx")]
    public void ACellThatStartsAFormulaIsPrefixed(string text)
    {
        var csv = File.ReadAllText(Writer().WriteCsv([Row(text, text)]));
        Assert.Equal(2, csv.Split("\"'" + text.Replace("\"", "\"\"") + "\"").Length - 1);
    }

    [Fact]
    public void TheHtmlEncodesEveryValue()
    {
        var html = File.ReadAllText(Writer().WriteHtml([Row("<script>alert(1)</script>", "a & \"b\"")]));

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("a &amp; &quot;b&quot;", html);
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("Deployments: 1.", html);
    }

    [Fact]
    public void TheDriftHtmlIsItsOwnFile()
    {
        var path = Writer().WriteHtml([new DriftRow("<b>", "1", "x", "1.0.0", "1.0.0", "Current")]);

        Assert.StartsWith("compliance-drift-", Path.GetFileName(path));
        Assert.Contains("&lt;b&gt;", File.ReadAllText(path));
    }

    [Fact]
    public void AReportsFolderThatIsAJunctionIsRefused()
    {
        var target = Path.Combine(_folder, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_folder, "link");
        Junctions.Create(link, target);
        try
        {
            var error = Assert.Throws<ComplianceServiceException>(() => Writer(link).WriteCsv([Row()]));
            Assert.Equal(ComplianceReportWriter.LinkMessage, error.Message);
            Assert.Empty(Directory.GetFiles(target));
        }
        finally
        {
            Directory.Delete(link);
        }
    }
}
