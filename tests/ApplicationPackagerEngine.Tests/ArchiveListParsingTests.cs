using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// "7z l" prints entry names in a fixed column. In the listing of a PE file
/// some rows end before that column.
/// </summary>
public sealed class ArchiveListParsingTests
{
    private const string Rule = "------------------- ----- ------------ ------------  ------------------------";

    private static string Row(string name) =>
        "2026-01-15 10:00:00 ....A        12345         6789  " + name;

    [Fact]
    public void NamesAreReadFromTheNameColumn()
    {
        var output = string.Join("\n", "Type = zip", Rule, Row("setup.exe"), Row("docs\\readme.txt"), Rule, "2 files");

        Assert.Equal(["setup.exe", "docs\\readme.txt"], ArchiveExtractionService.ParseListEntries(output));
    }

    [Theory]
    [InlineData(26)]
    [InlineData(40)]
    [InlineData(53)]
    public void ARowShorterThanTheNameColumn_IsSkipped(int length)
    {
        var output = string.Join("\n", Rule, new string('x', length), Row(".text"), Rule);

        Assert.Equal([".text"], ArchiveExtractionService.ParseListEntries(output));
    }

    [Fact]
    public void RowsOutsideTheRuledBlock_AreIgnored()
    {
        var output = string.Join("\n", Row("header-noise.exe"), Rule, Row("a.dll"), Rule, Row("footer-noise.exe"));

        Assert.Equal(["a.dll"], ArchiveExtractionService.ParseListEntries(output));
    }
}
