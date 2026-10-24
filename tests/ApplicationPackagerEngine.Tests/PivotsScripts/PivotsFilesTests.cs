using System.Diagnostics;
using System.Net.Security;
using System.Text.Json;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>The report writer, the audit log, the session store, the library, the result parsing and the administration service client.</summary>
public sealed class PivotsFilesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-psqfiles-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 5, 9, TimeSpan.Zero);

    public PivotsFilesTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private PivotsReportWriter Writer(string? folder = null) => new(folder ?? Path.Combine(_folder, "Reports"), () => Now);

    private static PivotsReport Report(params string[][] rows) => new("Results", ["TargetDevice", "Name", "Status"], rows, false, null);

    // ---- Reports ------------------------------------------------------------------------------

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
        var csv = File.ReadAllText(Writer().WriteCsv(Report(["CLIENT01", text, "Running"])));
        Assert.Contains("\"'" + text + "\"", csv);
    }

    [Fact]
    public void TheCsvStartsWithTheColumnNamesAndTheFileBesideItNamesTheViewTheFilterAndTheNote()
    {
        var path = Writer().WriteCsv(Report(["CLIENT01", "a \"quoted\", name", "Running"]) with { Filter = "Text: spool", Partial = true, Note = "Partial: 1 of 2 devices answered." });
        Assert.Equal(Path.Combine(_folder, "Reports", "pivots-results-20261024-090509.csv"), path);
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Equal("\"TargetDevice\",\"Name\",\"Status\"", lines[0]);
        Assert.Equal("\"CLIENT01\",\"a \"\"quoted\"\", name\",\"Running\"", lines[1]);
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(path)[..3]);
        var about = File.ReadAllText(PivotsReportWriter.AboutPath(path));
        Assert.Equal(Path.Combine(_folder, "Reports", "pivots-results-20261024-090509.about.txt"), PivotsReportWriter.AboutPath(path));
        Assert.Contains("Pivots and Scripts: Results.", about);
        Assert.Contains("Generated 2026-10-24 09:05:09 +00:00.", about);
        Assert.Contains("Rows: 1.", about);
        Assert.Contains("Filter: Text: spool", about);
        Assert.Contains("Note: Partial: 1 of 2 devices answered.", about);
        Assert.Contains("Data file: pivots-results-20261024-090509.csv", about);
    }

    [Fact]
    public void ImportCsvReadsTheExportAsATableWithTheColumnNamesAsTheHeader()
    {
        var path = Writer().WriteCsv(Report(["CLIENT01", "Spooler", "Running"], ["CLIENT02", "=1", "Stopped"]) with { Filter = "Text: s" });
        var script = $"$rows = @(Import-Csv -LiteralPath '{path.Replace("'", "''", StringComparison.Ordinal)}'); \"$($rows.Count)|$($rows[0].TargetDevice)|$($rows[1].Name)|$(($rows[0].PSObject.Properties.Name) -join ',')\"";
        var start = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        Assert.Equal("2|CLIENT01|'=1|TargetDevice,Name,Status", output);
    }

    [Fact]
    public void TheJsonKeepsValuesAsTextAndMakesColumnNamesUnique()
    {
        var report = new PivotsReport("Results", ["Name", "Name", ""], [["<script>", "=1", "x"]], true, "Partial: 1 of 2 devices answered.");
        using var doc = JsonDocument.Parse(File.ReadAllText(Writer().WriteJson(report)));
        var row = doc.RootElement.GetProperty("Rows")[0];
        Assert.Equal("<script>", row.GetProperty("Name").GetString());
        Assert.Equal("=1", row.GetProperty("Name 2").GetString());
        Assert.Equal("x", row.GetProperty("Column").GetString());
        Assert.True(doc.RootElement.GetProperty("Partial").GetBoolean());
        Assert.Equal("Partial: 1 of 2 devices answered.", doc.RootElement.GetProperty("Note").GetString());
    }

    [Fact]
    public void APartialReportWithoutANoteIsRefused()
    {
        var ex = Assert.Throws<PivotsServiceException>(() => Writer().WriteCsv(Report(["a", "b", "c"]) with { Partial = true }));
        Assert.Contains("partial", ex.Message);
        Assert.Throws<PivotsServiceException>(() => Writer().WriteJson(Report(["a", "b", "c"]) with { Partial = true }));
    }

    [Fact]
    public void AReportsFolderThatIsAJunctionIsRefused()
    {
        var target = Path.Combine(_folder, "elsewhere");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_folder, "Reports");
        Junction(link, target);
        try
        {
            var ex = Assert.Throws<PivotsServiceException>(() => Writer(link).WriteCsv(Report(["a", "b", "c"])));
            Assert.Equal(PivotsReportWriter.LinkMessage, ex.Message);
            Assert.Empty(Directory.GetFiles(target));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    private static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "The junction could not be created.");
    }

    // ---- Audit log ----------------------------------------------------------------------------

    private static PivotsAuditRecord Record(string phase, string result = "") =>
        new(new DateTime(2026, 10, 24, 5, 0, 0), "CONTOSO\\LabAdmin", "RunPivot", phase, result, "run1", "Collection All Workstations (MCM00015): 2 members", 2, null, "abc", "16784552", "Sending to 2 devices.");

    [Fact]
    public void TheAuditLogChainsItsRecordsAndAHandEditBreaksTheChain()
    {
        var path = Path.Combine(_folder, "audit.jsonl");
        var log = new PivotsAuditLog(() => path);
        Assert.True(log.Append(Record("Started")).Succeeded);
        Assert.True(log.Append(Record("Finished", "Succeeded")).Succeeded);
        var read = log.Read(10);
        Assert.Equal(2, read.Count);
        Assert.All(read, r => Assert.Equal(AuditVerification.Verified, PivotsAuditLog.VerificationText(r)));
        Assert.Equal("16784552", read[1].OperationId);
        Assert.Equal(2, read[1].DeviceCount);
        var lines = File.ReadAllLines(path);
        lines[0] = lines[0].Replace("\"DeviceCount\":2", "\"DeviceCount\":1", StringComparison.Ordinal);
        File.WriteAllLines(path, lines);
        Assert.All(log.Read(10), r => Assert.False(r.Verified));
    }

    [Fact]
    public void AReadOnlyAuditLogFailsTheProbe()
    {
        var path = Path.Combine(_folder, "ro.jsonl");
        File.WriteAllText(path, "");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var log = new PivotsAuditLog(() => path);
            Assert.False(log.Probe().Writable);
            Assert.False(log.Append(Record("Started")).Succeeded);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // ---- Session store ------------------------------------------------------------------------

    private static PivotsSessionRecord Session(string id) => new(id, new DateTime(2026, 10, 24, 9, 0, 0, DateTimeKind.Utc), "CONTOSO\\LabAdmin", "MCM", "cm01.contoso.com",
        PivotsChangeKind.RunPivot, "Device CLIENT01", null, [new ResolvedDevice(16777221, "CLIENT01", true)], "OS", null, null, "hash", []);

    [Fact]
    public void ASessionIsSavedOnceUpdatedAndReadBack()
    {
        var store = new PivotsSessionStore(Path.Combine(_folder, "Sessions"));
        var id = PivotsSessionStore.NewId();
        store.Save(Session(id));
        Assert.Throws<PivotsServiceException>(() => store.Save(Session(id)));
        store.Update(Session(id) with { Status = "Completed", OperationId = "16784551", Results = [new DeviceResult(16777221, "CLIENT01", PivotsDeviceStates.Completed, "<result/>", "") { Rows = [new Dictionary<string, string> { ["Caption"] = "Windows" }] }] });
        var read = store.Read(id)!;
        Assert.Equal("Completed", read.Status);
        Assert.Equal("Windows", read.Results.Single().Rows.Single()["Caption"]);
        Assert.Equal(PivotsChangeKind.RunPivot, store.List(10).Single().Kind);
        Assert.Null(store.Read("..\\..\\x"));
        Assert.Throws<PivotsServiceException>(() => store.Save(Session("..\\evil")));
    }

    [Fact]
    public void TheRawResponsesOfASessionAreBounded()
    {
        var big = new string('x', 1024 * 1024);
        var record = Session(PivotsSessionStore.NewId()) with { Raw = Enumerable.Range(0, 6).Select(i => new PivotsRawRecord(DateTime.UtcNow, "Status", 200, big)).ToList() };
        var bounded = PivotsSessionStore.Bounded(record);
        Assert.Equal(5, bounded.Raw.Count);
        Assert.Contains("were not kept", bounded.Raw[^1].Body);
    }

    // ---- Library ------------------------------------------------------------------------------

    [Fact]
    public void TheBuiltInLibraryHoldsSevenQueriesAndFourScriptsThatPassTheirHashCheck()
    {
        var items = new PivotsLibrary().Load(null);
        Assert.Equal(7, items.Count(i => i.Kind == PivotsLibrary.Query));
        Assert.Equal(4, items.Count(i => i.Kind == PivotsLibrary.Script));
        Assert.All(items, i => Assert.Equal(i.Hash, PivotsLibrary.HashOf(i.Text)));
        Assert.Contains(items, i => i.Name == "Free disk" && i.Text.StartsWith("Disk | project Device, Name, FreeSpace", StringComparison.Ordinal));
        Assert.Equal(PivotsLibrary.HashOf("a\nb"), PivotsLibrary.HashOf("a\r\nb"));
    }

    [Fact]
    public void AUserLibraryFolderAddsItsFilesAndLeavesOutLargeOnes()
    {
        var folder = Path.Combine(_folder, "lib");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Mine.kql"), "Service | where Name == 'Spooler'");
        File.WriteAllText(Path.Combine(folder, "Mine.ps1"), "Get-Date");
        File.WriteAllText(Path.Combine(folder, "Big.cmpivot"), new string('x', PivotsLibrary.MaxFileBytes + 1));
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "x");
        var items = new PivotsLibrary().Load(folder).Where(i => !i.BuiltIn).ToList();
        Assert.Equal(["Mine", "Mine"], items.Select(i => i.Name));
        Assert.Equal([PivotsLibrary.Query, PivotsLibrary.Script], items.Select(i => i.Kind));
        Assert.Equal(11, new PivotsLibrary().Load(Path.Combine(_folder, "missing")).Count);
    }

    // ---- Result parsing -----------------------------------------------------------------------

    [Fact]
    public void PivotRowsComeFromTheResultAttributes()
    {
        var (rows, more, ok) = PivotsResults.PivotRows("<result ResultCode=\"0\" moreResults=\"True\"><e _i=\"0\" Caption=\"Windows &amp; more\" Size=\"1\" /><e _i=\"1\" Caption=\"=cmd\" /></result>");
        Assert.True(ok);
        Assert.True(more);
        Assert.Equal(["Windows & more", "=cmd"], rows.Select(r => r["Caption"]));
        Assert.False(rows[0].ContainsKey("_i"));
    }

    [Fact]
    public void APivotResultWithADtdIsNotRead()
    {
        var (rows, _, ok) = PivotsResults.PivotRows("<!DOCTYPE r [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><result><e a=\"&x;\" /></result>");
        Assert.False(ok);
        Assert.Empty(rows);
    }

    [Theory]
    [InlineData("[\\\"a\\\"]", "[\"a\"]")]
    [InlineData("{\\\"Label\\\":\\\"psq-device\\\",\\\"Count\\\":3}", "{\"Label\":\"psq-device\",\"Count\":3}")]
    [InlineData("about to fail", "about to fail")]
    [InlineData("{\"Plain\":1}", "{\"Plain\":1}")]
    [InlineData("{\\\"broken", "{\"broken")]
    public void ScriptOutputWithEscapedQuotesIsReadAsTheScriptWroteIt(string stored, string expected) =>
        Assert.Equal(expected, PivotsResults.ScriptOutputText(stored));

    [Fact]
    public void ScriptRowsSplitJsonAndKeepOtherTextAsOutput()
    {
        Assert.Equal("3", PivotsResults.ScriptRows("{\"Label\":\"x\",\"Count\":3}").Single()["Count"]);
        Assert.Equal(2, PivotsResults.ScriptRows("[{\"a\":1},{\"a\":2}]").Count);
        Assert.Equal("about to fail", PivotsResults.ScriptRows("about to fail").Single()["Output"]);
        Assert.Equal(PivotsResults.MaxValueLength + 3, PivotsResults.ScriptRows(new string('y', PivotsResults.MaxValueLength + 10)).Single()["Output"].Length);
    }

    [Fact]
    public void TheOperationIdIsReadWithAndWithoutTheValueWrapper()
    {
        Assert.Equal("16784551", PivotsScriptsService.PivotOperationId("{\"value\":{\"ScriptParams\":\"x\",\"OperationId\":16784551}}"));
        Assert.Equal("16784552", PivotsScriptsService.PivotOperationId("{\"ScriptParams\":\"x\",\"OperationId\":16784552}"));
        Assert.Null(PivotsScriptsService.PivotOperationId("{\"OperationId\":\"x\"}"));
        Assert.Null(PivotsScriptsService.PivotOperationId("<html>"));
    }

    // ---- Administration service client --------------------------------------------------------

    [Theory]
    [InlineData("cm01.contoso.com", true)]
    [InlineData("192.168.50.20", true)]
    [InlineData("https://cm01.contoso.com", false)]
    [InlineData("cm01.contoso.com/AdminService", false)]
    [InlineData("cm01.contoso.com:8443", false)]
    [InlineData("user@cm01", false)]
    [InlineData("cm01 contoso", false)]
    public void TheProviderMustBeAHostName(string host, bool valid) => Assert.Equal(valid, AdminServiceClient.IsHostName(host));

    [Fact]
    public async Task AProviderThatIsNotAHostNameSendsNothing()
    {
        var handler = new StubAdminService(_ => [], () => []);
        using var client = new AdminServiceClient(() => "https://evil.example/", handler: handler);
        var ex = await Assert.ThrowsAsync<PivotsServiceException>(() => client.SendAsync(HttpMethod.Get, "v1.0/", null, CancellationToken.None));
        Assert.Contains("not a host name", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void CertificateErrorsNameWhatToFix()
    {
        var tls = new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established, see inner exception.", new System.Security.Authentication.AuthenticationException("x"));
        Assert.Contains("does not carry the name cm01", AdminServiceClient.Message(tls, "cm01", SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Contains("SMS Issuing root", AdminServiceClient.Message(tls, "cm01.contoso.com", SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Contains("could not be found", AdminServiceClient.Message(new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known."), "nosuch", SslPolicyErrors.None));
    }

    [Fact]
    public void ANextLinkToAnotherHostIsNotFollowed()
    {
        using var h = new PivotsHarness();
        Assert.Equal("v1.0/SMS_CMPivotStatus?$skip=2", h.Service.NextPath("https://cm01.contoso.com/AdminService/v1.0/SMS_CMPivotStatus?$skip=2"));
        Assert.Null(h.Service.NextPath("https://evil.example/AdminService/v1.0/SMS_CMPivotStatus"));
        Assert.Null(h.Service.NextPath("http://cm01.contoso.com/AdminService/v1.0/SMS_CMPivotStatus"));
    }
}
