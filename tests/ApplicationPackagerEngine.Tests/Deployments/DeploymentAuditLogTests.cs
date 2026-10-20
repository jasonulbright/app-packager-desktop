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

    internal static DeploymentAuditRecord Record(string result = "Success", string id = "MCM20015") =>
        new(default, "", "Package", "Pkg", "Run", "Pilot", "MCM00098", 3, "Available", "Install", null, id, result)
        {
            Event = "Create", DeploymentUniqueID = id, ObjectId = "MCM0001B", ProgramName = "Run", AvailableDateTime = new DateTime(2026, 10, 20, 10, 0, 0), TimeBasedOn = "LocalTime"
        };

    [Fact]
    public void AnAppendWritesOneCompactLineWithTheSuiteFieldsFirstAndTheOwnFieldsAfter()
    {
        var log = Log();
        Assert.True(log.Append(Record()).Succeeded);

        var line = Assert.Single(File.ReadAllLines(LogPath));
        Assert.StartsWith("{\"Timestamp\":\"2026-10-20T09:30:05\",\"User\":\"CONTOSO\\\\Admin\",\"DeploymentType\":\"Package\",\"ApplicationName\":\"Pkg\",\"ApplicationVersion\":\"Run\",\"CollectionName\":\"Pilot\",\"CollectionID\":\"MCM00098\",\"MemberCount\":3,\"DeployPurpose\":\"Available\",\"DeployAction\":\"Install\",\"DeadlineDateTime\":null,\"DeploymentID\":\"MCM20015\",\"Result\":\"Success\",\"Event\":\"Create\"", line);
        Assert.Contains("\"ObjectId\":\"MCM0001B\",\"AvailableDateTime\":\"2026-10-20T10:00:00\",\"Message\":\"\",\"Source\":\"Application Packager\"", line);
        Assert.EndsWith("\r\n", File.ReadAllText(LogPath));
    }

    [Fact]
    public void AnAppendNeverRewritesWhatIsInTheFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        var before = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"Timestamp\":\"2026-01-01T00:00:00\",\"Result\":\"Success\",\"PlanName\":\"Rings\"}\r\n")).ToArray();
        File.WriteAllBytes(LogPath, before);

        Assert.True(Log().Append(Record()).Succeeded);

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
        Assert.Equal("16777327", records[0].DeploymentID);
        Assert.Equal(12, records[0].MemberCount);
        Assert.Equal("Create", records[0].Event);
        Assert.Equal(new DateTime(2026, 9, 1, 10, 0, 0), records[0].Timestamp);
        Assert.Equal([new("PlanName", "Workstations"), new("RingIndex", "1")], records[0].Extra);
        Assert.Equal("", records[1].DeploymentID);
        Assert.Equal("Failed: x", records[1].Result);
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
            Assert.False(write.Succeeded);
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
            Assert.True(Log().Append(Record()).Succeeded);
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
            Assert.True(reservation.Append(Record()).Succeeded);
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
        Assert.False(reservation.Append(Record()).Succeeded);
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
        Assert.False(Log().Append(Record()).Succeeded);
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

    // ---- Chain ---------------------------------------------------------------------------------

    [Fact]
    public void EachRecordCarriesAChainOverItsFieldsAndTheLineBeforeAndReadsAsVerified()
    {
        var log = Log();
        Assert.True(log.Append(Record(id: "MCM20015")).Succeeded);
        Assert.True(log.Append(Record(id: "MCM20016") with { CreatedUtc = new DateTime(2026, 10, 20, 13, 30, 5, DateTimeKind.Utc) }).Succeeded);

        var lines = File.ReadAllLines(LogPath);
        Assert.All(lines, l => Assert.Matches("""
            ,"Chain":"[0-9a-f]{64}"}$
            """.Trim(), l));
        Assert.Contains("\"CreatedUtc\":\"2026-10-20T13:30:05Z\"", lines[1]);
        Assert.True(DeploymentAuditLog.ChainMatches(lines[0], DeploymentAuditLog.ChainStart));
        Assert.True(DeploymentAuditLog.ChainMatches(lines[1], DeploymentAuditLog.LineHash(lines[0])));
        var records = log.Read(10);
        Assert.All(records, r => Assert.True(r.Verified));
        Assert.Equal(new DateTime(2026, 10, 20, 13, 30, 5, DateTimeKind.Utc), records[1].CreatedUtc);
        Assert.DoesNotContain(records[1].Extra, p => p.Key == "Chain");
    }

    private const string V = AuditVerification.Verified;
    private static string Broken(int line) => $"Not verified (chain broken at line {line})";

    /// <summary>Five chained records MCM20011 to MCM20015; returns their lines.</summary>
    private List<string> FiveRecords(DeploymentAuditLog log)
    {
        for (var i = 11; i <= 15; i++) Assert.True(log.Append(Record(id: $"MCM200{i}")).Succeeded);
        return File.ReadAllLines(LogPath).ToList();
    }

    private static string[] Texts(DeploymentAuditLog log) => log.Read(100).Select(AuditVerification.Text).ToArray();

    /// <summary>The line a forger writes with a Chain recomputed over the given lines before it.</summary>
    private string ChainedAfter(IEnumerable<string> before, DeploymentAuditRecord record)
    {
        var path = Path.Combine(_folder, "forge", Guid.NewGuid().ToString("N") + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, before);
        Assert.True(Log(path).Append(record).Succeeded);
        return File.ReadAllLines(path).Last();
    }

    [Fact]
    public void AnEditedMiddleLineBreaksTheChainFromThatLineOnward()
    {
        var log = Log();
        var lines = FiveRecords(log);
        lines[2] = lines[2].Replace("\"CollectionName\":\"Pilot\"", "\"CollectionName\":\"Other\"", StringComparison.Ordinal);
        File.WriteAllLines(LogPath, lines);

        var records = log.Read(100);

        Assert.Equal([V, V, Broken(3), Broken(3), Broken(3)], records.Select(AuditVerification.Text));
        Assert.Equal([true, true, false, false, false], records.Select(r => r.Verified == true));
        Assert.All(records.Skip(2), r => Assert.Null(DeploymentChangeBuilder.RemoveFromAudit(r)));
    }

    [Fact]
    public void ADeletedLineBreaksTheChainAtTheLineThatFollowedIt()
    {
        var log = Log();
        var lines = FiveRecords(log);
        lines.RemoveAt(1);
        File.WriteAllLines(LogPath, lines);

        Assert.Equal([V, Broken(2), Broken(2), Broken(2)], Texts(log));
    }

    [Fact]
    public void AnInsertedLineWithItsOwnRecomputedChainBreaksTheChainAtTheLineAfterIt()
    {
        var log = Log();
        var lines = FiveRecords(log);
        lines.Insert(2, ChainedAfter(lines.Take(2), Record(id: "MCM20038")));
        File.WriteAllLines(LogPath, lines);

        Assert.Equal([V, V, V, Broken(4), Broken(4), Broken(4)], Texts(log));
    }

    [Fact]
    public void AnInsertedLineWithoutAChainReadsAsNoChainAndBreaksTheChainAtTheLineAfterIt()
    {
        var log = Log();
        var lines = FiveRecords(log);
        lines.Insert(2, """{"Timestamp":"2026-10-20T08:00:00","User":"CONTOSO\\Admin","DeploymentType":"Package","Event":"Create","Result":"Success","Source":"Application Packager","DeploymentUniqueID":"MCM20038"}""");
        File.WriteAllLines(LogPath, lines);

        Assert.Equal([V, V, AuditVerification.NoChain, Broken(4), Broken(4), Broken(4)], Texts(log));
    }

    [Fact]
    public void TwoSwappedLinesBreakTheChainAtTheFirstOfThem()
    {
        var log = Log();
        var lines = FiveRecords(log);
        (lines[1], lines[2]) = (lines[2], lines[1]);
        File.WriteAllLines(LogPath, lines);

        Assert.Equal([V, Broken(2), Broken(2), Broken(2), Broken(2)], Texts(log));
    }

    [Fact]
    public void ATruncatedTailLeavesTheRecordsBeforeItVerifiedAndAPartLineIsSkipped()
    {
        var log = Log();
        var lines = FiveRecords(log);
        File.WriteAllLines(LogPath, lines.Take(3));
        Assert.Equal([V, V, V], Texts(log));

        File.WriteAllText(LogPath, string.Join("\r\n", lines.Take(3)) + "\r\n" + lines[3][..40]);
        Assert.Equal([V, V, V], Texts(log));
        Assert.Equal(1, log.SkippedLines);
    }

    [Fact]
    public void ARecordAppendedWithAValidChainAfterABreakStaysNotVerified()
    {
        var log = Log();
        var lines = FiveRecords(log);
        lines[1] = lines[1].Replace("\"MemberCount\":3", "\"MemberCount\":4", StringComparison.Ordinal);
        File.WriteAllLines(LogPath, lines);
        Assert.True(log.Append(Record(id: "MCM20016")).Succeeded);
        File.AppendAllText(LogPath, ChainedAfter(File.ReadAllLines(LogPath), Record(id: "MCM20017")) + "\r\n");

        Assert.True(DeploymentAuditLog.ChainMatches(File.ReadAllLines(LogPath).Last(), DeploymentAuditLog.LineHash(File.ReadAllLines(LogPath)[^2])));
        Assert.Equal([V, Broken(2), Broken(2), Broken(2), Broken(2), Broken(2), Broken(2)], Texts(log));
    }

    [Fact]
    public void ACorruptLinePresentWhenTheNextRecordWasWrittenIsSkippedAndBreaksNothing()
    {
        var log = Log();
        log.Append(Record(id: "MCM20011"));
        File.AppendAllText(LogPath, "{\"Timestamp\":\"2026-10-20T09:00:00\",\"Res\r\n");
        log.Append(Record(id: "MCM20012"));

        Assert.Equal([V, V], Texts(log));
        Assert.Equal(1, log.SkippedLines);
    }

    [Fact]
    public void AChainedLineDamagedIntoTextThatIsNotJsonIsSkippedAndBreaksTheChainAtItsLine()
    {
        var log = Log();
        var lines = FiveRecords(log);
        lines[2] = lines[2].Replace("\"DeploymentType\":\"Package\"", "\"DeploymentType\":Package\"", StringComparison.Ordinal);
        File.WriteAllLines(LogPath, lines);

        Assert.Equal([V, V, Broken(3), Broken(3)], Texts(log));
        Assert.Equal(1, log.SkippedLines);
    }

    [Fact]
    public void LinesWithoutAChainBeforeTheFirstChainedLineAreTheAnchorsPastAndBreakNothing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        const string old = """{"Timestamp":"2026-10-01T10:00:00","User":"LAB\\admin","DeploymentType":"Package","Result":"Success","Event":"Create","Source":"Application Packager","DeploymentUniqueID":"MCM20001"}""";
        File.WriteAllText(LogPath, old + "\r\n" + old.Replace("MCM20001", "MCM20002", StringComparison.Ordinal) + "\r\n");
        var log = Log();
        log.Append(Record(id: "MCM20011"));
        log.Append(Record(id: "MCM20012"));
        File.AppendAllText(LogPath, old.Replace("MCM20001", "MCM20003", StringComparison.Ordinal) + "\r\n");
        log.Append(Record(id: "MCM20013"));

        Assert.Equal([AuditVerification.BeforeChain, AuditVerification.BeforeChain, V, V, AuditVerification.NoChain, V], Texts(log));
    }

    [Fact]
    public void TheLineNumbersOfACutWindowCountTheLinesBeforeIt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllLines(LogPath, Enumerable.Range(0, 300).Select(i => "{\"Pad\":\"" + new string('x', 60_000) + "\"}"));
        var log = Log();
        var lines = FiveRecords(log);
        lines[303] = lines[303].Replace("\"MemberCount\":3", "\"MemberCount\":4", StringComparison.Ordinal);
        File.WriteAllLines(LogPath, lines);

        var texts = Texts(log);

        Assert.Equal([V, V, Broken(304), Broken(304)], texts[^4..]);
    }

    [Fact]
    public void ASuiteLineBetweenRecordsIsPartOfTheChain()
    {
        var log = Log();
        log.Append(Record(id: "MCM20015"));
        File.AppendAllText(LogPath, "{\"Timestamp\":\"2026-10-20T09:00:00\",\"Result\":\"Success\",\"PlanName\":\"Rings\"}\r\n");
        log.Append(Record(id: "MCM20016"));

        var records = log.Read(10);
        Assert.Equal([true, false, true], records.Select(r => r.Verified == true));

        var lines = File.ReadAllLines(LogPath);
        File.WriteAllLines(LogPath, [lines[0], lines[1].Replace("Rings", "Other", StringComparison.Ordinal), lines[2]]);
        Assert.False(log.Read(10)[2].Verified);
    }

    [Fact]
    public void ARecordNotReadFromALogIsNeitherVerifiedNorRefused()
    {
        Assert.Null(Record().Verified);
        Assert.NotNull(DeploymentChangeBuilder.RemoveFromAudit(Record() with { ObjectId = "MCM0001B", CollectionID = "MCM00098" }));
    }

    private static void Junction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link));
    }
}
