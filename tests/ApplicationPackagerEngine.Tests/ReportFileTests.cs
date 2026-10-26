using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public sealed class ReportFileTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 1, 37, 5, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-reportfile-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(" \n=cmd|' /C calc'!A0")]
    [InlineData("  \t+1")]
    [InlineData(" \r\n@SUM(A1)")]
    [InlineData("\n-2")]
    [InlineData("   =1")]
    [InlineData(" \tplain text after a tab")]
    public void ACellWithLeadingWhiteSpaceThatAFormulaCanFollowStartsWithAnApostrophe(string value) =>
        Assert.StartsWith("\"'", ReportFile.Cell(value));

    [Theory]
    [InlineData("plain")]
    [InlineData("  two spaces")]
    [InlineData("a = b")]
    public void APlainCellIsOnlyQuoted(string value) => Assert.Equal("\"" + value + "\"", ReportFile.Cell(value));

    [Fact]
    public void ATakenNameGetsTheNextNumber()
    {
        var first = ReportFile.WriteNew(_folder, "report", "csv", "a", Now);
        var second = ReportFile.WriteNew(_folder, "report", "csv", "b", Now);
        Assert.EndsWith("report-20261019-013705.csv", first);
        Assert.EndsWith("report-20261019-013705-2.csv", second);
        Assert.Equal("b", File.ReadAllText(second));
    }

    [Fact]
    public void AFailureDuringTheWriteDeletesThePartialFileAndIsNotTakenForATakenName()
    {
        var calls = 0;
        var error = Assert.Throws<IOException>(() => ReportFile.WriteNew(_folder, "report", "csv", writer =>
        {
            calls++;
            writer.Write(new string('x', 10_000));
            writer.Flush();
            throw new IOException("There is not enough space on the disk.");
        }, Now));

        Assert.Equal("There is not enough space on the disk.", error.Message);
        Assert.Equal(1, calls);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public void TheSearchForAFreeNameEnds()
    {
        for (var n = 1; n <= 3; n++)
            ReportFile.WriteNew(_folder, "report", "csv", "x", Now);

        var error = Assert.Throws<IOException>(() => ReportFile.WriteNew(_folder, "report", "csv", w => w.Write("y"), Now, maxNames: 3));

        Assert.Equal("No free report file name was found.", error.Message);
        Assert.Equal(3, Directory.GetFiles(_folder).Length);
    }
}
