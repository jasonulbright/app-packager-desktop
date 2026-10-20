using System.Text;
using System.Text.Json;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments;

public sealed class DeploymentAuditLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-dhaudit-" + Guid.NewGuid().ToString("N"));

    private string LogPath => Path.Combine(_folder, "logs", "deployment-audit.jsonl");

    public void Dispose()
    {
        foreach (var file in Directory.Exists(_folder) ? Directory.GetFiles(_folder, "*", SearchOption.AllDirectories) : [])
            File.SetAttributes(file, FileAttributes.Normal);
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private DeploymentAuditLog Log(string? path = null) =>
        new(() => path ?? LogPath, clock: () => new DateTime(2026, 10, 20, 9, 30, 5), user: () => @"CONTOSO\Admin");

    internal static DeploymentAuditRecord Record(string result = "Success", string id = "MCM20015") => new()
    {
        DeploymentType = "Package", ApplicationName = "Pkg", ApplicationVersion = "Run", CollectionName = "Pilot", CollectionID = "MCM00098",
        MemberCount = JsonSerializer.SerializeToElement(3), DeployPurpose = "Available", DeployAction = "Install", DeadlineDateTime = null,
        DeploymentID = JsonSerializer.SerializeToElement(id), Result = result, Event = "Create", DeploymentUniqueID = id, ObjectID = "MCM0001B",
        ProgramName = "Run", AvailableDateTime = "2026-10-20T10:00:00", TimeBasedOn = "LocalTime"
    };

    [Fact]
    public void AnAppendWritesOneCompactLineWithTheSuiteFieldsFirstAndTheOwnFieldsAfter()
    {
        var log = Log();
        Assert.True(log.Append(Record()).Written);

        var line = Assert.Single(File.ReadAllLines(LogPath));
        Assert.StartsWith("{\"Timestamp\":\"2026-10-20T09:30:05\",\"User\":\"CONTOSO\\\\Admin\",\"DeploymentType\":\"Package\",\"ApplicationName\":\"Pkg\",\"ApplicationVersion\":\"Run\",\"CollectionName\":\"Pilot\",\"CollectionID\":\"MCM00098\",\"MemberCount\":3,\"DeployPurpose\":\"Available\",\"DeployAction\":\"Install\",\"DeadlineDateTime\":null,\"DeploymentID\":\"MCM20015\",\"Result\":\"Success\",\"Event\":\"Create\"", line);
        Assert.Contains("\"Source\":\"Application Packager\"", line);
        Assert.EndsWith("\r\n", File.ReadAllText(LogPath));
    }

    [Fact]
    public void AnAppendNeverRewritesWhatIsInTheFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        var before = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"Timestamp\":\"2026-01-01T00:00:00\",\"Result\":\"Success\",\"PlanName\":\"Rings\"}\r\n")).ToArray();
        File.WriteAllBytes(LogPath, before);

        Assert.True(Log().Append(Record()).Written);

        var after = File.ReadAllBytes(LogPath);
        Assert.Equal(before, after[..before.Length]);
        Assert.Equal(2, File.ReadAllLines(LogPath).Length);
    }

    [Fact]
    public void ALastLineWithoutALineBreakGetsOneBeforeTheNewRecord()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, "{\"Result\":\"Success\"}");

        Log().Append(Record());

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(2, lines.Length);
        Assert.Equal("{\"Result\":\"Success\"}", lines[0]);
    }

    [Fact]
    public void ReadSkipsACorruptLineAndKeepsTheFieldsOfOtherTools()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath,
            "{\"Timestamp\":\"2026-09-01T10:00:00\",\"DeploymentType\":\"Application\",\"DeploymentID\":16777327,\"MemberCount\":12,\"Result\":\"Success\",\"PlanName\":\"Workstations\",\"RingIndex\":1}\r\n" +
            "{not json\r\n\r\n" +
            "{\"Timestamp\":\"2026-09-02T10:00:00\",\"DeploymentType\":\"SUG\",\"DeploymentID\":null,\"Result\":\"Failed: x\"}\r\n",
            new UTF8Encoding(true));
        var log = Log();

        var records = log.Read(100);

        Assert.Equal(2, records.Count);
        Assert.Equal(1, log.SkippedLines);
        Assert.Equal("16777327", DeploymentAuditLog.Text(records[0].DeploymentID));
        Assert.Equal("Workstations", records[0].Extra!["PlanName"].GetString());
        Assert.Equal("", DeploymentAuditLog.Text(records[1].DeploymentID));
    }

    [Fact]
    public void ReadReturnsTheLastRecordsOldestFirst()
    {
        var log = Log();
        for (var i = 0; i < 10; i++) log.Append(Record(id: "MCM2000" + i));

        var records = log.Read(3);

        Assert.Equal(["MCM20007", "MCM20008", "MCM20009"], records.Select(r => r.DeploymentUniqueID));
    }

    [Fact]
    public void AMissingLogReadsAsEmpty() => Assert.Empty(Log().Read(10));

    [Fact]
    public void TheProbeCreatesTheFolderAndPassesOnAWritableLog()
    {
        var probe = Log().Probe();
        Assert.True(probe.Writable, probe.Message);
        Assert.True(Directory.Exists(Path.GetDirectoryName(LogPath)));
    }

    // Measured on Windows 11 with .NET 10: a reader that shares only Read refuses every writer, so
    // both the probe and the append fail; a reader that shares ReadWrite lets both run.
    [Fact]
    public void AReaderThatSharesOnlyReadMakesTheProbeAndTheAppendFail()
    {
        Log().Probe();
        using (new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var probe = Log().Probe();
            Assert.False(probe.Writable);
            Assert.Contains("another program has it open", probe.Message);
            var write = Log().Append(Record());
            Assert.False(write.Written);
            Assert.DoesNotContain("process", write.Message);
        }
        Assert.Equal(0, new FileInfo(LogPath).Length);
    }

    [Fact]
    public void AReaderThatSharesReadWriteLetsTheProbeAndTheAppendRun()
    {
        Log().Probe();
        using (new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.True(Log().Probe().Writable);
            Assert.True(Log().Append(Record()).Written);
        }
    }

    [Fact]
    public void AReservationKeepsTheAppendPossibleWhenAReaderComesAfterTheProbe()
    {
        var log = Log();
        using (var reservation = log.Reserve())
        {
            Assert.True(reservation.Probe.Writable);
            Assert.Throws<IOException>(() => new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.Read).Dispose());
            Assert.Empty(log.Read(10));
            Assert.True(reservation.Append(Record()).Written);
        }
        Assert.Single(log.Read(10));
        using var reader = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    [Fact]
    public void AReservationOnALogInUseReportsTheProbeAndRefusesTheAppend()
    {
        Log().Probe();
        using var reader = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reservation = Log().Reserve();

        Assert.False(reservation.Probe.Writable);
        Assert.False(reservation.Append(Record()).Written);
    }

    [Fact]
    public void AReadOnlyLogFailsTheProbe()
    {
        Log().Append(Record());
        File.SetAttributes(LogPath, FileAttributes.ReadOnly);

        var probe = Log().Probe();

        Assert.False(probe.Writable);
        Assert.Contains("read-only or access is denied", probe.Message);
    }

    [Fact]
    public void ALogFolderThatIsAJunctionIsRefused()
    {
        var target = Path.Combine(_folder, "target");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(_folder);
        Junction(Path.Combine(_folder, "logs"), target);

        var probe = Log().Probe();

        Assert.False(probe.Writable);
        Assert.Contains("junction or symbolic link", probe.Message);
        Assert.False(Log().Append(Record()).Written);
        Assert.Empty(Directory.GetFiles(target));
    }

    [Theory]
    [InlineData("relative\\audit.jsonl")]
    [InlineData("C:")]
    [InlineData("C:\\")]
    public void APathThatIsNotAFullFilePathIsRefused(string path)
    {
        var probe = Log(path).Probe();
        Assert.False(probe.Writable);
        Assert.Contains("full path", probe.Message);
    }

    [Fact]
    public void AnEmptyOverrideUsesTheDefaultUnderTheDataRoot()
    {
        var log = new DeploymentAuditLog(() => "  ");
        Assert.Equal(Path.Combine(AppDataPaths.Root, "Deployments", "deployment-audit.jsonl"), log.Path);
        Assert.Equal(@"\\server\share\dh\audit.jsonl", new DeploymentAuditLog(() => @"\\server\share\dh\audit.jsonl").Path);
    }

    private static void Junction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link));
    }
}
