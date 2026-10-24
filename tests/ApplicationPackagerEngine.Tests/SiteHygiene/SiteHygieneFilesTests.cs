using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>The suppression and scan stores, the report writer, the audit log and the registration.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SiteHygieneFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-shfiles-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public SiteHygieneFilesTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task TheSiteHygieneServicesAreRegisteredWithTheDefaultFiles()
    {
        await using var services = new ServiceCollection().AddPackagerServices().BuildServiceProvider();
        var service = services.GetRequiredService<ISiteHygieneService>();
        Assert.IsType<SiteHygieneService>(service);
        Assert.Same(service, services.GetRequiredService<ISiteHygieneService>());
        var folder = Path.Combine(AppDataPaths.Root, "SiteHygiene");
        Assert.Equal(Path.Combine(folder, "site-hygiene-audit.jsonl"), services.GetRequiredService<IHygieneAuditLog>().Path);
        Assert.True(services.GetRequiredService<IHygieneAuditLog>().Probe().Writable);
        Assert.Equal(Path.Combine(folder, "suppressions.json"), services.GetRequiredService<IHygieneSuppressionStore>().Path);
        Assert.Equal(Path.Combine(folder, "last-scan.json"), services.GetRequiredService<IHygieneScanStore>().Path);
        Assert.Equal(Path.Combine(folder, "Reports"), services.GetRequiredService<IHygieneReportWriter>().Folder);
        Assert.Same(services.GetRequiredService<IConfigMgrSession>(), services.GetRequiredService<IConfigMgrSession>());
    }

    [Fact]
    public void SuppressionsAreKeyedByCheckAndObjectAndSurviveACorruptFile()
    {
        var store = new HygieneSuppressionStore(Path.Combine(_root, "s.json"));
        Assert.Empty(store.Load());
        store.Add(new Suppression("app-01", "ScopeId_X/Application_1", "Kept for the lab", new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc)));
        store.Add(new Suppression("APP-01", "ScopeId_X/Application_1", "Replaced " + new string('x', 900), new DateTime(2026, 10, 24, 13, 0, 0, DateTimeKind.Utc)));
        store.Add(new Suppression("APP-05", "ScopeId_X/Application_1", "", new DateTime(2026, 10, 24, 13, 0, 0, DateTimeKind.Utc)));
        var list = store.Load();
        Assert.Equal(2, list.Count);
        var one = list.Single(s => s.CheckId == "APP-01");
        Assert.StartsWith("Replaced ", one.Comment);
        Assert.Equal(HygieneSuppressionStore.MaxComment, one.Comment.Length);
        store.Remove("app-01", "ScopeId_X/Application_1");
        Assert.Equal(["APP-05"], store.Load().Select(s => s.CheckId));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));

        File.WriteAllText(store.Path, "{ not json");
        Assert.Empty(store.Load());
        File.WriteAllText(store.Path, "{\"SchemaVersion\":1,\"Suppressions\":[null,{\"CheckId\":\"DEV-01\",\"ObjectKey\":\"16777222\",\"Comment\":null,\"CreatedUtc\":\"2026-10-24T00:00:00Z\"},{\"CheckId\":\"\",\"ObjectKey\":\"x\"}]}");
        var tolerant = Assert.Single(store.Load());
        Assert.Equal("", tolerant.Comment);
    }

    [Fact]
    public void TheLastScanIsStoredAndReadBackAsAStoredResultWithItsKeys()
    {
        var store = new HygieneScanStore(Path.Combine(_root, "scan.json"));
        Assert.Null(store.Load());
        Assert.Null(store.Keys());
        var (findings, checks) = SiteHygieneParity.Native(SiteHygieneService.ResolveAreas(new HashSet<string>()).ToArray());
        var result = new HygieneScanResult(findings, checks, new DateTime(2026, 10, 24, 18, 56, 0, DateTimeKind.Utc), false, null) { Areas = ["Applications"], SiteCode = "MCM", QueryCount = 57 };
        store.Save(result);
        var back = store.Load()!;
        Assert.True(back.Stored);
        Assert.Equal(0, back.QueryCount);
        Assert.Equal(result.ScannedUtc, back.ScannedUtc);
        Assert.Equal(findings.Select(f => f.Key), back.Findings.Select(f => f.Key));
        Assert.Equal(findings.Select(f => f.FixText), back.Findings.Select(f => f.FixText));
        Assert.Equal(checks.Select(c => c.State), back.Checks.Select(c => c.State));
        Assert.True(store.Keys()!.SetEquals(findings.Select(f => f.Key)));

        File.WriteAllText(store.Path, "[1,2");
        Assert.Null(store.Load());
    }

    private static HygieneReport Report(params string[][] rows) =>
        new("Findings", ["Severity", "Check", "Object", "Evidence"], rows, false, null)
        {
            ChecksNotRun = ["PKG-01: The check did not run: the read of programs failed."],
            Filter = "Text: <b>x</b>"
        };

    [Fact]
    public void ACsvCellThatASpreadsheetReadsAsAFormulaIsGuardedIncludingALeadingLineFeed()
    {
        var writer = new HygieneReportWriter(Path.Combine(_root, "r"));
        var path = writer.WriteCsv(Report(["Error", "APP-01", "=HYPERLINK(\"x\")", "\n=cmd|' /C calc'!A0"], ["Info", "COL-01", "+1", "Plain, \"quoted\""]));
        var text = File.ReadAllText(path);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", text);
        Assert.Contains("\"'\n=cmd|' /C calc'!A0\"", text);
        Assert.Contains("\"'+1\"", text);
        Assert.Contains("\"Plain, \"\"quoted\"\"\"", text);
        Assert.Contains("Did not run: PKG-01", text);
        Assert.StartsWith(Path.Combine(_root, "r", "site-hygiene-findings-"), path);
    }

    [Fact]
    public void HtmlValuesAreEncodedAndATruncatedScanNeedsItsNote()
    {
        var writer = new HygieneReportWriter(Path.Combine(_root, "r"));
        var html = File.ReadAllText(writer.WriteHtml(Report(["Error", "APP-01", "<script>alert(1)</script>", "a & b"])));
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("Filter: Text: &lt;b&gt;x&lt;/b&gt;", html);
        Assert.Contains("Did not run: PKG-01", html);
        Assert.Throws<HygieneServiceException>(() => writer.WriteCsv(Report() with { Truncated = true }));
        Assert.Contains("Note: Reads cut", File.ReadAllText(writer.WriteCsv(Report() with { Truncated = true, Note = "Reads cut at their cap: devices." })));
    }

    [Fact]
    public void TheAuditLogChainsItsLinesAndAHandEditReadsAsNotVerified()
    {
        var path = Path.Combine(_root, "audit.jsonl");
        var log = new HygieneAuditLog(() => path, user: () => "CONTOSO\\labadmin");
        Assert.True(log.Append(new HygieneAuditRecord(default, "", "RemovePackage", "Started", "", "run1", [new HygieneAuditObject("PKG-01", "Package", "MCM0010E", "SHL-Pkg-Empty", "")], "Started.")).Succeeded);
        Assert.True(log.Append(new HygieneAuditRecord(default, "", "RemovePackage", "Finished", "Succeeded", "run1", [new HygieneAuditObject("PKG-01", "Package", "MCM0010E", "SHL-Pkg-Empty", "Done")], "1 done.")).Succeeded);
        var records = log.Read(10);
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.True(r.Verified));
        Assert.Equal("CONTOSO\\labadmin", records[0].User);
        Assert.Equal("MCM0010E", records[1].Objects[0].ObjectKey);

        var lines = File.ReadAllLines(path);
        lines[0] = lines[0].Replace("SHL-Pkg-Empty", "SHL-Pkg-Other", StringComparison.Ordinal);
        File.WriteAllLines(path, lines);
        var edited = log.Read(10);
        Assert.All(edited, r => Assert.False(r.Verified));
        Assert.Equal(AuditVerification.BrokenAt(1), HygieneAuditLog.VerificationText(edited[0]));
    }

    [Fact]
    public void AReadOnlyAuditLogCannotBeWritten()
    {
        var path = Path.Combine(_root, "ro", "audit.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var log = new HygieneAuditLog(() => path);
            var probe = log.Probe();
            Assert.False(probe.Writable);
            Assert.Contains("read-only", probe.Message);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }
}
