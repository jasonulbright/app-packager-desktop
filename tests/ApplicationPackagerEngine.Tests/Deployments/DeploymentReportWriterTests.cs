using System.Text.Json;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments;

public sealed class DeploymentReportWriterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-dhreports-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 30, 5, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private DeploymentReportWriter Writer(string? folder = null) => new(folder ?? _folder, () => Now);

    private static DeploymentAuditRecord Parse(string json) => JsonSerializer.Deserialize<DeploymentAuditRecord>(json)!;

    [Fact]
    public void TheCsvHasTheUnionOfAllRecordPropertiesInFirstSeenOrder()
    {
        var rows = new[]
        {
            Parse("{\"Timestamp\":\"2026-09-01T10:00:00\",\"DeploymentType\":\"Application\",\"DeploymentID\":16777327,\"Result\":\"Success\"}"),
            Parse("{\"Timestamp\":\"2026-09-02T10:00:00\",\"DeploymentType\":\"SUG\",\"Result\":\"Success\",\"PlanName\":\"Rings\",\"RingIndex\":2}"),
            Parse("{\"Timestamp\":\"2026-09-03T10:00:00\",\"Event\":\"RemoveDeployment\",\"Undoes\":\"MCM20015\",\"Result\":\"Success\"}")
        };

        var path = Writer().WriteCsv(rows);

        Assert.Equal(Path.Combine(_folder, "deployment-history-20261020-093005.csv"), path);
        var lines = File.ReadAllLines(path);
        Assert.Equal("\"Timestamp\",\"DeploymentType\",\"DeploymentID\",\"Result\",\"PlanName\",\"RingIndex\",\"Event\",\"Undoes\"", lines[0]);
        Assert.Equal("\"2026-09-01T10:00:00\",\"Application\",\"16777327\",\"Success\",\"\",\"\",\"\",\"\"", lines[1]);
        Assert.Equal("\"2026-09-02T10:00:00\",\"SUG\",\"\",\"Success\",\"Rings\",\"2\",\"\",\"\"", lines[2]);
        Assert.Equal("\"2026-09-03T10:00:00\",\"\",\"\",\"Success\",\"\",\"\",\"RemoveDeployment\",\"MCM20015\"", lines[3]);
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(path)[..3]);
    }

    [Theory]
    [InlineData("=cmd|' /C calc'!A0")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tx")]
    [InlineData("\nx")]
    [InlineData("\rx")]
    [InlineData("  =1")]
    public void ACellThatStartsAFormulaIsPrefixed(string text)
    {
        var record = new DeploymentAuditRecord { ApplicationName = text, Result = "Success" };
        var csv = File.ReadAllText(Writer().WriteCsv([record]));
        Assert.Contains("\"'" + text.Replace("\"", "\"\"") + "\"", csv);
    }

    [Fact]
    public void TheHtmlEncodesEveryValueAndEveryColumnName()
    {
        var record = Parse("{\"ApplicationName\":\"<script>alert(1)</script>\",\"Result\":\"Failed: a & \\\"b\\\"\",\"<b>\":\"x\"}");

        var html = File.ReadAllText(Writer().WriteHtml([record]));

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("Failed: a &amp; &quot;b&quot;", html);
        Assert.Contains("&lt;b&gt;", html);
        Assert.Contains("default-src 'none'", html);
        Assert.Contains("Records: 1.", html);
    }

    [Fact]
    public void AReportsFolderThatIsAJunctionIsRefused()
    {
        var target = Path.Combine(_folder, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_folder, "reports");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using (var process = System.Diagnostics.Process.Start(psi)!) process.WaitForExit();

        var error = Assert.Throws<DeploymentServiceException>(() => Writer(link).WriteCsv([]));

        Assert.Equal(DeploymentReportWriter.LinkMessage, error.Message);
        Assert.Empty(Directory.GetFiles(target));
    }

    [Fact]
    public void TwoReportsInTheSameSecondGetTwoFiles()
    {
        var first = Writer().WriteHtml([]);
        var second = Writer().WriteHtml([]);
        Assert.NotEqual(first, second);
        Assert.EndsWith("-2.html", second);
    }

    [Fact]
    public void TheDefaultFolderIsUnderTheDataRoot() =>
        Assert.Equal(Path.Combine(AppDataPaths.Root, "Deployments", "Reports"), DeploymentReportWriter.DefaultFolder());
}
