using ApplicationPackagerEngine.Services;
using System.Text.Json;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The Check ConfigMgr result file: the format that APD and the PowerShell app both write.</summary>
public sealed class CheckConfigMgrResultExportTests
{
    private static readonly DateTime At = new(2026, 11, 21, 8, 30, 5, DateTimeKind.Utc);

    private static CheckConfigMgrResultRow Row(string app, string status, string cm = "1.0", string latest = "2.0", string arp = "") =>
        new(app, "Vendor, Inc", "CM " + app, arp, "x64", cm, latest, status, "APD", At);

    [Theory]
    [InlineData("Update available", "UpdateAvailable")]
    [InlineData("Update available (3 CVEs, 1 in KEV)", "UpdateAvailable")]
    [InlineData("Up to date", "UpToDate")]
    [InlineData("Current newer", "UpToDate")]
    [InlineData("Not found in ConfigMgr", "NotFound")]
    [InlineData("Error", null)]
    [InlineData("Cancelled", null)]
    [InlineData("ConfigMgr version retrieved", null)]
    [InlineData("", null)]
    public void MapsTheGridStatus(string grid, string? expected) =>
        Assert.Equal(expected, CheckConfigMgrResultExport.MapStatus(grid));

    [Fact]
    public void TheCsvHasTheHeaderAndQuotesEveryField()
    {
        var csv = CheckConfigMgrResultExport.ToCsv([Row("A \"x\"", "UpdateAvailable", arp: "A* | B*")]);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("\"Application\",\"Vendor\",\"CMName\",\"InstalledSoftwareName\",\"Architecture\",\"ConfigMgrVersion\",\"LatestVersion\",\"Status\",\"Source\",\"CheckedAtUtc\"", lines[0]);
        Assert.Equal("\"A \"\"x\"\"\",\"Vendor, Inc\",\"CM A \"\"x\"\"\",\"A* | B*\",\"x64\",\"1.0\",\"2.0\",\"UpdateAvailable\",\"APD\",\"2026-11-21T08:30:05Z\"", lines[1]);
    }

    [Fact]
    public void TheFilterKeepsOnlyUpdates()
    {
        var rows = new[] { Row("A", "UpdateAvailable"), Row("B", "UpToDate"), Row("C", "NotFound") };
        Assert.Equal(["A"], CheckConfigMgrResultExport.Filter(rows, true).Select(r => r.Application));
        Assert.Equal(3, CheckConfigMgrResultExport.Filter(rows, false).Count);
    }

    [Fact]
    public void TheJsonTwinCarriesTheSameNames()
    {
        using var doc = JsonDocument.Parse(CheckConfigMgrResultExport.ToJson([Row("A", "UpToDate")]));
        var names = doc.RootElement[0].EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(CheckConfigMgrResultExport.Columns, names);
    }

    [Fact]
    public void WriteDefaultOverwritesBothFilesWithABom()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccr-" + Guid.NewGuid().ToString("N"));
        try
        {
            CheckConfigMgrResultExport.WriteDefault([Row("A", "UpdateAvailable"), Row("B", "UpToDate")], dir);
            CheckConfigMgrResultExport.WriteDefault([Row("C", "NotFound")], dir);
            var csv = Path.Combine(dir, "CheckConfigMgrResult.csv");
            var bytes = File.ReadAllBytes(csv);
            Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
            Assert.Equal(2, File.ReadAllLines(csv).Length);
            Assert.Contains("\"C\"", File.ReadAllText(csv));
            Assert.True(File.Exists(Path.Combine(dir, "CheckConfigMgrResult.json")));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
