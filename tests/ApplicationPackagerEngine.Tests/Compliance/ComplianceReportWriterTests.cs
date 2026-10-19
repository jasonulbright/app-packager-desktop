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

    private static readonly DriftRow[] Drift =
        [new("Hand made", "16780003", null, null, null, "Untagged"), new("LLMNR", "1", "llmnr-disable", "1.0.0", "1.1.0", "Behind")];

    [Fact]
    public void TheCsvHoldsADriftSectionAndAComplianceSectionOfQuotedRows()
    {
        var path = Writer().WriteCsv(Drift, [Row()]);

        Assert.Equal(Path.Combine(_folder, "compliance-20261019-093005.csv"), path);
        var lines = File.ReadAllLines(path);
        Assert.Equal("\"Baseline drift\"", lines[0]);
        Assert.Equal("\"Baseline\",\"Baseline ID\",\"Catalog entry\",\"Deployed version\",\"Catalog version\",\"State\"", lines[1]);
        Assert.Equal("\"Hand made\",\"16780003\",\"\",\"\",\"\",\"Untagged\"", lines[2]);
        Assert.Equal("\"LLMNR\",\"1\",\"llmnr-disable\",\"1.0.0\",\"1.1.0\",\"Behind\"", lines[3]);
        Assert.Equal("", lines[4]);
        Assert.Equal("\"Baseline compliance\"", lines[5]);
        Assert.Equal("\"Baseline\",\"Collection\",\"Compliant\",\"Non-compliant\",\"Error\",\"Unknown\",\"Last evaluated (UTC)\",\"Stale\"", lines[6]);
        Assert.Equal("\"LLMNR\",\"Pilot\",\"3\",\"1\",\"0\",\"2\",\"2026-10-10 08:00\",\"Yes\"", lines[7]);
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(path)[..3]);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tx")]
    public void ACellThatStartsAFormulaIsPrefixed(string text)
    {
        var csv = File.ReadAllText(Writer().WriteCsv([new DriftRow(text, "1", null, null, null, "Untagged")], [Row(text, text)]));
        Assert.Equal(3, csv.Split("\"'" + text.Replace("\"", "\"\"") + "\"").Length - 1);
    }

    [Fact]
    public void TheHtmlEncodesEveryValueInBothTables()
    {
        var html = File.ReadAllText(Writer().WriteHtml([new DriftRow("<b>", "1", "x", "1.0.0", "1.0.0", "Current")], [Row("<script>alert(1)</script>", "a & \"b\"")]));

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("a &amp; &quot;b&quot;", html);
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("Baselines: 1.", html);
        Assert.Contains("Deployments: 1.", html);
    }

    private static ComplianceRow Unsummarized(string baseline, string collection) =>
        new(baseline, collection, 0, 0, 0, 0, null, true, CountsKnown: false);

    [Fact]
    public void TheCsvWritesUnknownCountsAndANeverEvaluatedDeploymentAsWords()
    {
        var path = Writer().WriteCsv([], [Unsummarized("=cmd|' /C calc'!A0", "@SUM(1)"), new ComplianceRow("Known", "Pilot", 0, 0, 0, 0, new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc), false)]);

        var lines = File.ReadAllLines(path);
        Assert.Equal("\"'=cmd|' /C calc'!A0\",\"'@SUM(1)\",\"unknown\",\"unknown\",\"unknown\",\"unknown\",\"never\",\"Yes\"", lines[5]);
        Assert.Equal("\"Known\",\"Pilot\",\"0\",\"0\",\"0\",\"0\",\"2026-10-10 08:00\",\"No\"", lines[6]);
    }

    [Fact]
    public void TheCsvWritesNeverForADeploymentWithKnownCountsAndNoSummaryTime()
    {
        var lines = File.ReadAllLines(Writer().WriteCsv([], [new ComplianceRow("B", "C", 2, 1, 0, 0, null, true)]));
        Assert.Equal("\"B\",\"C\",\"2\",\"1\",\"0\",\"0\",\"never\",\"Yes\"", lines[5]);
    }

    [Fact]
    public void TheHtmlWritesUnknownCountsAndNeverAndStillEncodesHostileNames()
    {
        var html = File.ReadAllText(Writer().WriteHtml([], [Unsummarized("<img src=x onerror=alert(1)>", "never & \"unknown\"")]));

        Assert.DoesNotContain("<img", html);
        Assert.Contains("<td>&lt;img src=x onerror=alert(1)&gt;</td><td>never &amp; &quot;unknown&quot;</td><td>unknown</td><td>unknown</td><td>unknown</td><td>unknown</td><td>never</td><td>Yes</td>", html);
        Assert.DoesNotContain("<td>0</td>", html);
    }

    [Fact]
    public void ASecondReportInTheSameSecondGetsANewName()
    {
        var first = Writer().WriteHtml([], []);
        var second = Writer().WriteHtml([], []);
        Assert.NotEqual(first, second);
        Assert.EndsWith("-2.html", second);
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
            var error = Assert.Throws<ComplianceServiceException>(() => Writer(link).WriteCsv([], [Row()]));
            Assert.Equal(ComplianceReportWriter.LinkMessage, error.Message);
            Assert.Empty(Directory.GetFiles(target));
        }
        finally
        {
            Directory.Delete(link);
        }
    }
}
