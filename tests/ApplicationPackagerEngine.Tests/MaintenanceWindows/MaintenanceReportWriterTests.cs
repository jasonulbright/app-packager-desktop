using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.DetectionTester;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class MaintenanceReportWriterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-mwreports-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 30, 5, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private MaintenanceReportWriter Writer() => new(_folder, () => Now);

    internal static MaintenanceWindowInfo Row(string collection = "Servers", string window = "Patch night", bool enabled = true,
        string type = "SoftwareUpdates", bool utc = false, DateTime? next = null) => new(
        "MCM00042", collection, window, "{ID}", type, enabled,
        new MaintenanceSchedule(MaintenanceScheduleKind.PatchTuesday, new DateTime(2026, 11, 3, 22, 30, 0), TimeSpan.FromMinutes(150), utc, 1,
            null, null, null, 3, null),
        "Occurs 3 day(s) after the Second Tuesday of every 1 months effective 11/3/2026 10:30 PM",
        next ?? new DateTime(2026, 11, 13, 22, 30, 0), "comment");

    [Fact]
    public void TheCsvHasAHeaderAndOneQuotedRowPerWindow()
    {
        var path = Writer().WriteCsv([Row(), Row(window: "Second", enabled: false, type: "Any", utc: true)]);
        Assert.Equal(Path.Combine(_folder, "maintenance-windows-20261019-093005.csv"), path);
        var lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        Assert.Equal("\"Status\",\"Collection\",\"Collection ID\",\"Window\",\"Type\",\"Recurrence\",\"Schedule\",\"Duration\",\"UTC\",\"Next occurrence\"", lines[0]);
        Assert.Equal("\"Enabled\",\"Servers\",\"MCM00042\",\"Patch night\",\"Software updates\",\"Patch Tuesday\","
            + "\"Occurs 3 day(s) after the Second Tuesday of every 1 months effective 11/3/2026 10:30 PM\",\"2h 30m\",\"No\",\"2026-11-13 22:30\"", lines[1]);
        Assert.StartsWith("\"Disabled\",\"Servers\",\"MCM00042\",\"Second\",\"Any\",", lines[2]);
        Assert.Contains("\"Yes\"", lines[2]);
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(path)[..3]);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"x\")")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tx")]
    public void ACellThatAStartsAFormulaIsPrefixed(string name)
    {
        var text = File.ReadAllText(Writer().WriteCsv([Row(collection: name)]));
        Assert.Contains("\"'" + name.Replace("\"", "\"\"") + "\"", text);
    }

    [Fact]
    public void TheHtmlEncodesEveryValue()
    {
        var html = File.ReadAllText(Writer().WriteHtml([Row(collection: "<script>alert(1)</script>", window: "a & \"b\"")]));
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("a &amp; &quot;b&quot;", html);
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("Windows: 1.", html);
    }

    [Fact]
    public void ANoNextOccurrenceIsAnEmptyCell()
    {
        var row = Row() with { NextOccurrence = null };
        Assert.EndsWith(",\"\"", File.ReadAllLines(Writer().WriteCsv([row]))[1]);
    }

    [Fact]
    public void ASecondReportInTheSameSecondGetsANumber()
    {
        var first = Writer().WriteHtml([Row()]);
        var second = Writer().WriteHtml([Row()]);
        Assert.NotEqual(first, second);
        Assert.EndsWith("-2.html", second);
    }

    [Fact]
    public void AReportsFolderThatIsAJunctionIsRefused()
    {
        var target = _folder + "-target";
        Directory.CreateDirectory(target);
        Junctions.Create(_folder, target);
        try
        {
            Assert.Equal(MaintenanceReportWriter.LinkMessage, Assert.Throws<MaintenanceWindowException>(() => Writer().WriteCsv([Row()])).Message);
            Assert.Throws<MaintenanceWindowException>(() => Writer().WriteHtml([Row()]));
            Assert.Empty(Directory.GetFiles(target));
        }
        finally
        {
            Directory.Delete(_folder);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void TheDefaultFolderIsRefusedWhenTheMaintenanceWindowsFolderIsAJunction()
    {
        var saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);
        var target = _folder + "-target";
        Directory.CreateDirectory(target);
        try
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _folder);
            var reports = MaintenanceReportWriter.DefaultFolder();
            Assert.Equal(Path.Combine(AppDataPaths.Root, "MaintenanceWindows", "Reports"), reports, ignoreCase: true);
            Directory.CreateDirectory(AppDataPaths.Root);
            Junctions.Create(Path.Combine(AppDataPaths.Root, "MaintenanceWindows"), target);
            Assert.Throws<MaintenanceWindowException>(() => new MaintenanceReportWriter(clock: () => Now).WriteCsv([Row()]));
            Assert.Empty(Directory.GetFileSystemEntries(target));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, saved);
            var link = Path.Combine(Path.Combine(_folder, "Users"), System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, "MaintenanceWindows");
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(target, recursive: true);
        }
    }
}
