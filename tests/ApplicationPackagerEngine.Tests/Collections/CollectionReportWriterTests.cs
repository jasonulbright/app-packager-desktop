using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.DetectionTester;

namespace ApplicationPackagerEngine.Tests.Collections;

public sealed class CollectionReportWriterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-collreports-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 30, 5, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private CollectionReportWriter Writer(string? folder = null) => new(folder ?? _folder, () => Now);

    private static CollectionRow Row(string name = "Workstations", string comment = "c") =>
        new("MCM00010", name, @"Clients\Windows", "SMS00001", "All Systems", 12, false, "Periodic", comment, 1, 2, 0, 1);

    [Fact]
    public void TheCsvHasAHeaderAndOneQuotedRowPerCollection()
    {
        var path = Writer().WriteCsv([Row(), Row("All Systems") with { CollectionId = "SMS00001", IsBuiltIn = true }]);
        Assert.Equal(Path.Combine(_folder, "collections-20261019-093005.csv"), path);
        var lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("\"Collection ID\",\"Name\",\"Folder\"", lines[0]);
        Assert.Equal("\"MCM00010\",\"Workstations\",\"Clients\\Windows\",\"All Systems\",\"SMS00001\",\"12\",\"No\",\"Periodic\",\"1\",\"2\",\"0\",\"1\",\"c\"", lines[1]);
        Assert.Contains("\"Yes\"", lines[2]);
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
        Assert.Contains("Collections: 1.", html);
    }

    [Fact]
    public void ASecondReportInTheSameSecondGetsANewName()
    {
        var first = Writer().WriteHtml([Row()]);
        var second = Writer().WriteHtml([Row()]);
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
            var error = Assert.Throws<CollectionServiceException>(() => Writer(link).WriteCsv([Row()]));
            Assert.Equal(CollectionReportWriter.LinkMessage, error.Message);
            Assert.Empty(Directory.GetFiles(target));
        }
        finally
        {
            Directory.Delete(link);
        }
    }
}
