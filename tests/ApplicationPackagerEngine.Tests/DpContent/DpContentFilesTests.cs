using System.Diagnostics;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.DpContent;

/// <summary>The DP content report writer and audit log on real files.</summary>
public sealed class DpContentFilesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-dpcfiles-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 21, 5, 9, TimeSpan.Zero);

    public DpContentFilesTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private DpContentReportWriter Writer(string? folder = null) => new(folder ?? Path.Combine(_folder, "Reports"), () => Now);

    private static DpContentReport Report(params string[][] rows) => new("Status Issues", ["DP", "Content", "State"], rows, false, null);

    [Theory]
    [InlineData("=cmd|' /C calc'!A0")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tx")]
    [InlineData("\nx")]
    [InlineData("\rx")]
    [InlineData("  =1")]
    public void ACsvCellThatStartsAFormulaIsPrefixed(string text)
    {
        var csv = File.ReadAllText(Writer().WriteCsv(Report(["CM01", text, "Installed"])));
        Assert.Contains("\"'" + text.Replace("\"", "\"\"") + "\"", csv);
    }

    [Fact]
    public void TheCsvQuotesEveryCellAndHasABom()
    {
        var path = Writer().WriteCsv(Report(["CM01.CONTOSO.COM", "a \"quoted\", name", "Install failed"]));
        Assert.Equal(Path.Combine(_folder, "Reports", "dp-content-statusissues-20261023-210509.csv"), path);
        var lines = File.ReadAllLines(path);
        Assert.StartsWith("\"DP content: ", lines[0], StringComparison.Ordinal);
        Assert.Equal("\"DP\",\"Content\",\"State\"", lines[1]);
        Assert.Equal("\"CM01.CONTOSO.COM\",\"a \"\"quoted\"\", name\",\"Install failed\"", lines[2]);
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(path)[..3]);
        var second = Writer().WriteCsv(Report(["x", "y", "z"]));
        Assert.EndsWith("-2.csv", second);
    }

    [Fact]
    public void TheHtmlEncodesValuesColumnsAndTheNote()
    {
        var report = new DpContentReport("DPs <b>", ["<th>", "Name"], [["<script>alert(1)</script>", "a & b"]], true, "Truncated: showing 3 of 7 <rows>.");
        var html = File.ReadAllText(Writer().WriteHtml(report));
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("<th>&lt;th&gt;</th>", html);
        Assert.Contains("a &amp; b", html);
        Assert.Contains("Truncated: showing 3 of 7 &lt;rows&gt;.", html);
        Assert.Contains("default-src 'none'", html);
    }

    [Fact]
    public void ATruncatedReportWithoutANoteIsRefused()
    {
        var report = new DpContentReport("DPs", ["DP"], [["CM01"]], true, null);
        var ex = Assert.Throws<DpContentServiceException>(() => Writer().WriteCsv(report));
        Assert.Contains("does not say so", ex.Message);
        Assert.Throws<DpContentServiceException>(() => Writer().WriteHtml(report));
        Assert.False(Directory.Exists(Path.Combine(_folder, "Reports")) && Directory.EnumerateFiles(Path.Combine(_folder, "Reports")).Any());
    }

    [Fact]
    public void TheNoteIsTheLastCsvRow()
    {
        var report = new DpContentReport("DPs", ["DP", "Total"], [["CM01", "6"]], true, "Truncated: showing 3 of 7 status rows.");
        var lines = File.ReadAllLines(Writer().WriteCsv(report));
        Assert.Equal("\"Note: Truncated: showing 3 of 7 status rows.\",\"\"", lines[^1]);
    }

    [Fact]
    public void AReportsFolderThatIsAJunctionIsRefused()
    {
        var target = Path.Combine(_folder, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_folder, "Reports");
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!) p.WaitForExit();
        Assert.True(Directory.Exists(link));
        var ex = Assert.Throws<DpContentServiceException>(() => Writer(link).WriteCsv(Report(["a", "b", "c"])));
        Assert.Equal(DpContentReportWriter.LinkMessage, ex.Message);
        Assert.Empty(Directory.EnumerateFiles(target));
    }

    // ---- Audit log --------------------------------------------------------------------------

    private DpContentAuditLog Log(string? path = null) => new(() => path ?? Path.Combine(_folder, "DpContent", "dp-content-audit.jsonl"), clock: () => new DateTime(2026, 10, 23, 19, 30, 0), user: () => @"CONTOSO\labadmin");

    private static DpContentAuditRecord Record(string phase, string result = "", params DpContentAuditPair[] pairs) =>
        new(default, "", "Remove", phase, result, "run1", 1, pairs, "Removing 1 pair (part 1 of 1).");

    private static readonly DpContentAuditPair OnePair = new("MCM000A6", "DPCM-Test-1", "CM01.CONTOSO.COM", StubDpContentSite.Nal, 0, "");

    [Fact]
    public void RecordsAreAppendedChainedAndReadBackVerified()
    {
        var log = Log();
        Assert.True(log.Append(Record("Started", "", OnePair)).Succeeded);
        Assert.True(log.Append(Record("Finished", "Requested", OnePair with { Result = "Requested" })).Succeeded);
        var lines = File.ReadAllLines(log.Path);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.Matches("\"Chain\":\"[0-9a-f]{64}\"}$", l));
        Assert.Contains("\"User\":\"CONTOSO\\\\labadmin\"", lines[0]);
        var records = log.Read(10);
        Assert.Equal(["Started", "Finished"], records.Select(r => r.Phase));
        Assert.All(records, r => Assert.True(r.Verified));
        Assert.Equal(StubDpContentSite.Nal, records[0].Pairs.Single().NalPath);
        Assert.Equal(0, records[0].Pairs.Single().StateBefore);
        Assert.Equal("Requested", records[1].Pairs.Single().Result);
        Assert.Equal(AuditVerification.Verified, DpContentAuditLog.VerificationText(records[0]));
    }

    [Fact]
    public void AHandEditBreaksTheChainFromThatLineOnAndACorruptLineIsSkipped()
    {
        var log = Log();
        for (var i = 0; i < 3; i++) log.Append(Record("Started", "", OnePair));
        var lines = File.ReadAllLines(log.Path).ToList();
        lines[1] = lines[1].Replace("DPCM-Test-1", "DPCM-Test-X", StringComparison.Ordinal);
        lines.Insert(2, "{not json");
        File.WriteAllLines(log.Path, lines);
        var records = log.Read(10);
        Assert.Equal(3, records.Count);
        Assert.Equal(1, log.SkippedLines);
        Assert.True(records[0].Verified);
        Assert.False(records[1].Verified);
        Assert.False(records[2].Verified);
        Assert.Equal(AuditVerification.BrokenAt(2), DpContentAuditLog.VerificationText(records[2]));
    }

    [Fact]
    public void AReaderThatHoldsTheFileWithoutWriteSharingMakesTheProbeFail()
    {
        var log = Log();
        log.Append(Record("Started", "", OnePair));
        using (new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var probe = log.Probe();
            Assert.False(probe.Writable);
            Assert.Contains("another program has it open", probe.Message);
            Assert.False(log.Append(Record("Finished", "Requested", OnePair)).Succeeded);
        }
        Assert.True(log.Probe().Writable);
    }

    [Fact]
    public void AReservationHeldFromTheProbeKeepsLaterAppendsWorking()
    {
        var log = Log();
        using var reservation = log.Reserve();
        Assert.True(reservation.Probe.Writable);
        Assert.Throws<IOException>(() => new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.Read).Dispose());
        Assert.True(reservation.Append(Record("Started", "", OnePair)).Succeeded);
        Assert.True(reservation.Append(Record("Finished", "Requested", OnePair)).Succeeded);
    }

    [Fact]
    public void AReadOnlyLogBlocksTheAppendWithAPlainMessage()
    {
        var log = Log();
        log.Append(Record("Started", "", OnePair));
        File.SetAttributes(log.Path, FileAttributes.ReadOnly);
        try
        {
            var probe = log.Probe();
            Assert.False(probe.Writable);
            Assert.StartsWith("The DP content audit log cannot be written: the file or folder is read-only or access is denied", probe.Message);
        }
        finally
        {
            File.SetAttributes(log.Path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void ALogFolderThatIsAJunctionIsRefused()
    {
        var target = Path.Combine(_folder, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_folder, "linked");
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!) p.WaitForExit();
        var probe = Log(Path.Combine(link, "dp-content-audit.jsonl")).Probe();
        Assert.False(probe.Writable);
        Assert.Contains("junction or symbolic link", probe.Message);
        Assert.Empty(Directory.EnumerateFiles(target));
    }

    [Fact]
    public void ARelativePathIsRefused()
    {
        var probe = Log("dp-content-audit.jsonl").Probe();
        Assert.False(probe.Writable);
        Assert.StartsWith("The DP content audit log path must be a full path", probe.Message);
    }

    [Fact]
    public void TheDefaultPathIsUnderTheDataRoot()
    {
        Assert.Equal(Path.Combine(AppDataPaths.Root, "DpContent", "dp-content-audit.jsonl"), DpContentAuditLog.DefaultPath());
        Assert.Equal(Path.Combine(AppDataPaths.Root, "DpContent", "Reports"), DpContentReportWriter.DefaultFolder());
    }

    [Fact]
    public void TheDeploymentLogStillReadsAndChainsThroughTheSharedFile()
    {
        var path = Path.Combine(_folder, "deployment-audit.jsonl");
        var log = new DeploymentAuditLog(() => path);
        var record = new DeploymentAuditRecord(new DateTime(2026, 10, 23, 10, 0, 0), @"CONTOSO\labadmin", "Package", "P", "1", "C", "MCM00098", 0, "Available", "Install", null, "MCM20002", "Success");
        Assert.True(log.Append(record).Succeeded);
        Assert.True(log.Append(record with { Event = "Undo" }).Succeeded);
        var read = log.Read(10);
        Assert.Equal(2, read.Count);
        Assert.All(read, r => Assert.True(r.Verified));
        var lines = File.ReadAllLines(path);
        Assert.True(DeploymentAuditLog.ChainMatches(lines[0], DeploymentAuditLog.ChainStart));
        Assert.True(DeploymentAuditLog.ChainMatches(lines[1], DeploymentAuditLog.LineHash(lines[0])));
    }
}
