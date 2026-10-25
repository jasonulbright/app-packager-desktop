using System.Security.Cryptography;
using System.Text;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>The dashboard import over fixture folders laid out as the suite install keeps them, and the Live report writer.</summary>
public sealed class LiveImportAndReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apkg-shimport-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 10, 25, 14, 29, 6, DateTimeKind.Utc);

    public LiveImportAndReportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Suite => Path.Combine(_dir, "AppPackagerSuite", "site-hygiene");
    private string Legacy => Path.Combine(Suite, "legacy", "mecm-health-dashboard");
    private string HistoryPath => Path.Combine(_dir, "data", "metrics-history.csv");

    /// <summary>The suite tool's own settings file as it is on the workstation (no Live keys).</summary>
    private const string SuitePrefs = "{\r\n  \"DarkMode\": true,\r\n  \"SMSProvider\": \"cm01.contoso.com\",\r\n  \"SiteCode\": \"MCM\"\r\n}";

    /// <summary>A Health Dashboard settings file with the keys the suite import reads (Read-SuiteSettings layout).</summary>
    private const string DashboardPrefs = "{\r\n  \"SQLServer\": \"CM01.contoso.com\",\r\n  \"AutoRefreshMinutes\": 30,\r\n  \"InactiveThresholdDays\": 21,\r\n  \"AlertsEnabled\": false,\r\n  \"AlertCompliancePct\": 90,\r\n  \"WindowLeft\": 10\r\n}";

    /// <summary>History as the suite writes it: ConvertTo-Csv quoting, a byte order mark, local times with an offset.</summary>
    private static string SuiteHistory(params string[] times)
    {
        var sb = new StringBuilder("\"Timestamp\",\"DeploymentTotal\",\"DeploymentFailed\",\"CompliancePct\",\"ContentIssues\",\"ContentFailedPairs\",\"DPTotal\",\"DPOffline\",\"DPDegraded\",\"ClientHealthy\",\"ClientUnhealthy\",\"ClientInactive\",\"InactiveDevices\",\"SiteOK\",\"SiteWarning\",\"SiteCritical\"\r\n");
        foreach (var t in times) sb.Append('"').Append(t).Append("\",\"9\",\"0\",\"58.3\",\"0\",\"0\",\"1\",\"0\",\"0\",\"3\",\"0\",\"1\",\"0\",\"84\",\"0\",\"0\"\r\n");
        return sb.ToString();
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(true));
    }

    private sealed class Settings
    {
        public LiveSettingValues Values { get; set; } = LiveSettingValues.Empty;
        public int Fills { get; private set; }
        public bool Fails { get; set; }

        public bool Fill(LiveSettingValues v)
        {
            if (Fails) return false;
            Fills++;
            Values = new LiveSettingValues(
                SqlServer: Values.SqlServer ?? v.SqlServer, TrustSqlCertificate: Values.TrustSqlCertificate ?? v.TrustSqlCertificate,
                RefreshMinutes: Values.RefreshMinutes ?? v.RefreshMinutes, InactiveDays: Values.InactiveDays ?? v.InactiveDays,
                AlertsEnabled: Values.AlertsEnabled ?? v.AlertsEnabled, CompliancePctFloor: Values.CompliancePctFloor ?? v.CompliancePctFloor);
            return true;
        }
    }

    private (LegacyDashboardImport Import, MetricsHistoryStore Store, Settings Settings) Make(Settings? settings = null)
    {
        var s = settings ?? new Settings();
        var store = new MetricsHistoryStore(HistoryPath, utcNow: () => Now);
        return (new LegacyDashboardImport(store, () => s.Values, s.Fill, () => [Suite], utcNow: () => Now), store, s);
    }

    private string Hashes() => string.Join("|", Directory.GetFiles(Path.Combine(_dir, "AppPackagerSuite"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(f => f + "=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));

    [Fact]
    public void TheImportShowsWhatItCopiesFillsOnlyEmptySettingsCopiesTheHistoryAndASecondRunChangesNothing()
    {
        Write(Path.Combine(Suite, LegacyDashboardImport.SuitePrefsFile), SuitePrefs);
        Write(Path.Combine(Legacy, LegacyDashboardImport.DashboardPrefsFile), DashboardPrefs);
        Write(Path.Combine(Legacy, LegacyDashboardImport.HistoryRelative), SuiteHistory("2026-10-20T08:15:00.1234567-04:00", "2026-10-20T08:30:00.0000000-04:00", "2026-01-01T00:00:00.0000000-05:00"));
        var before = Hashes();
        var settings = new Settings { Values = LiveSettingValues.Empty with { InactiveDays = 14 } };
        var (import, store, s) = Make(settings);

        var plan = import.Plan();
        Assert.Null(plan.Refusal);
        Assert.Equal(Path.Combine(Suite, LegacyDashboardImport.LegacyFolder), plan.SourceFolder);
        Assert.Equal([LiveSettingKeys.SqlServer, LiveSettingKeys.RefreshMinutes, LiveSettingKeys.AlertsEnabled, LiveSettingKeys.CompliancePctFloor], plan.SettingKeys);
        Assert.Equal(2, plan.HistoryRows);
        Assert.True(plan.HistoryWillBeCopied);
        Assert.Equal(0, s.Fills);
        Assert.False(store.Exists);

        var result = import.Apply(plan);
        Assert.Equal(plan.SettingKeys, result.SettingKeys);
        Assert.True(result.HistoryCopied);
        Assert.Equal(2, result.HistoryRows);
        Assert.Equal("Filled 4 settings: SqlServer, RefreshMinutes, AlertsEnabled, CompliancePctFloor. Copied 2 history rows.", result.Message);
        Assert.Equal(new LiveSettingValues(SqlServer: "CM01.contoso.com", TrustSqlCertificate: null, RefreshMinutes: 30, InactiveDays: 14, AlertsEnabled: false, CompliancePctFloor: 90), s.Values);
        var rows = store.Read(365);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new DateTime(2026, 10, 20, 12, 15, 0, DateTimeKind.Utc).AddTicks(1234567), rows[0].TimestampUtc);
        Assert.Equal(before, Hashes());

        var again = import.Plan();
        Assert.True(again.IsEmpty);
        var second = import.Apply(plan);
        Assert.Empty(second.SettingKeys);
        Assert.False(second.HistoryCopied);
        Assert.Equal(1, s.Fills);
        Assert.Equal(2, store.Read(365).Count);
        Assert.Equal(before, Hashes());
    }

    [Fact]
    public void ASettingsFileThatCannotBeWrittenFillsNoSettingAndTheResultSaysSo()
    {
        Write(Path.Combine(Legacy, LegacyDashboardImport.DashboardPrefsFile), DashboardPrefs);
        var (import, _, s) = Make(new Settings { Fails = true });
        var plan = import.Plan();
        Assert.Equal(5, plan.SettingKeys.Count);
        var result = import.Apply(plan);
        Assert.Empty(result.SettingKeys);
        Assert.Equal(LegacyDashboardImport.SettingsNotSaved, result.Message);
        Assert.Equal(LiveSettingValues.Empty, s.Values);
        s.Fails = false;
        Assert.Equal(5, import.Apply(import.Plan()).SettingKeys.Count);
    }

    [Fact]
    public void TheSuiteToolsOwnFilesComeFirstAndASiblingDashboardFolderIsFoundToo()
    {
        Write(Path.Combine(Suite, LegacyDashboardImport.SuitePrefsFile), SuitePrefs);
        Write(Path.Combine(Suite, LegacyDashboardImport.HistoryRelative), SuiteHistory("2026-10-24T08:15:00.0000000-04:00"));
        var sibling = Path.Combine(_dir, "AppPackagerSuite", LegacyDashboardImport.SiblingFolder);
        Write(Path.Combine(sibling, LegacyDashboardImport.DashboardPrefsFile), DashboardPrefs);
        var (import, _, _) = Make();
        var plan = import.Plan();
        Assert.Equal(Suite, plan.SourceFolder);
        Assert.Empty(plan.SettingKeys);
        Assert.Equal(1, plan.HistoryRows);

        File.Delete(Path.Combine(Suite, LegacyDashboardImport.HistoryRelative));
        var next = import.Plan();
        Assert.Equal(sibling, next.SourceFolder);
        Assert.Equal(5, next.SettingKeys.Count);
        Assert.False(next.HistoryWillBeCopied);
    }

    [Fact]
    public void AnExistingHistoryIsNeverReplaced()
    {
        Write(Path.Combine(Legacy, LegacyDashboardImport.HistoryRelative), SuiteHistory("2026-10-20T08:15:00.0000000-04:00"));
        var (import, store, _) = Make();
        store.Append(new MetricRow(Now, LiveMetrics.Columns.ToDictionary(c => c, _ => (double?)1, StringComparer.Ordinal)));
        var plan = import.Plan();
        Assert.True(plan.IsEmpty);
        Assert.False(plan.HistoryWillBeCopied);
        Assert.Single(store.Read(365));
    }

    [Fact]
    public void AHistoryWrittenBetweenThePlanAndTheApplyIsKept()
    {
        Write(Path.Combine(Legacy, LegacyDashboardImport.HistoryRelative), SuiteHistory("2026-10-20T08:15:00.0000000-04:00", "2026-10-21T08:15:00.0000000-04:00"));
        var (import, store, _) = Make();
        var plan = import.Plan();
        Assert.True(plan.HistoryWillBeCopied);
        store.Append(new MetricRow(Now, LiveMetrics.Columns.ToDictionary(c => c, _ => (double?)1, StringComparer.Ordinal)));
        var result = import.Apply(plan);
        Assert.False(result.HistoryCopied);
        Assert.Equal(0, result.HistoryRows);
        Assert.Equal(Now, Assert.Single(store.Read(365)).TimestampUtc);
    }

    [Theory]
    [InlineData("prefs-json", "is not valid JSON")]
    [InlineData("prefs-array", "holds no settings object")]
    [InlineData("history-header", "has no Timestamp column")]
    [InlineData("history-unknown", "The history file has no known columns")]
    [InlineData("history-garbage", "No line of the history file")]
    [InlineData("history-size", "is larger than 32 MB")]
    [InlineData("prefs-size", "is larger than 1 MB")]
    public void ACorruptOrOversizedFileIsRefusedAndNothingIsImported(string kind, string text)
    {
        var prefs = Path.Combine(Legacy, LegacyDashboardImport.DashboardPrefsFile);
        var history = Path.Combine(Legacy, LegacyDashboardImport.HistoryRelative);
        Write(prefs, DashboardPrefs);
        Write(history, SuiteHistory("2026-10-20T08:15:00.0000000-04:00"));
        switch (kind)
        {
            case "prefs-json": Write(prefs, "{ \"SQLServer\": "); break;
            case "prefs-array": Write(prefs, "[1,2]"); break;
            case "history-header": Write(history, "a,b,c\r\n1,2,3\r\n"); break;
            case "history-unknown": Write(history, "\"Timestamp\",\"Deployments\",\"Notes\"\r\n\"2026-10-20T08:15:00.0000000-04:00\",\"9\",\"x\"\r\n"); break;
            case "history-garbage": Write(history, MetricsHistoryStore.HeaderLine + "\r\nnot,a,row\r\nx\r\n"); break;
            case "history-size": using (var f = File.Open(history, FileMode.Append)) f.SetLength(LegacyDashboardImport.MaxHistoryBytes + 1); break;
            case "prefs-size": Write(prefs, "{\"SQLServer\":\"" + new string('a', (int)LegacyDashboardImport.MaxPrefsBytes) + "\"}"); break;
        }
        var (import, store, s) = Make();
        var plan = import.Plan();
        Assert.NotNull(plan.Refusal);
        Assert.Contains(text, plan.Refusal);
        Assert.Empty(plan.SettingKeys);
        Assert.Equal(0, plan.HistoryRows);
        Assert.False(plan.HistoryWillBeCopied);
        var result = import.Apply(plan);
        Assert.Equal(plan.Refusal, result.Message);
        Assert.Equal(0, s.Fills);
        Assert.False(store.Exists);
    }

    [Fact]
    public void AHistoryCopyThatFailsAfterTheSettingsWereFilledKeepsTheFilledKeysAndSaysTheHistoryWasNotCopied()
    {
        var history = Path.Combine(Legacy, LegacyDashboardImport.HistoryRelative);
        Write(Path.Combine(Legacy, LegacyDashboardImport.DashboardPrefsFile), DashboardPrefs);
        Write(history, SuiteHistory("2026-10-20T08:15:00.0000000-04:00"));
        var (import, store, s) = Make();
        var plan = import.Plan();
        Assert.True(plan.HistoryWillBeCopied);
        Write(history, "a,b,c\r\n1,2,3\r\n");

        var result = import.Apply(plan);
        Assert.Equal(plan.SettingKeys, result.SettingKeys);
        Assert.Equal(1, s.Fills);
        Assert.False(result.HistoryCopied);
        Assert.StartsWith("Filled 5 settings: ", result.Message);
        Assert.EndsWith("has no Timestamp column. The history was not copied.", result.Message);
        Assert.DoesNotContain("Nothing was imported", result.Message);
        Assert.False(store.Exists);
        Assert.Empty(import.Plan().SettingKeys);
    }

    [Fact]
    public void AHistoryWithExtraUnknownColumnsBesideTheKnownOnesIsImported()
    {
        var history = Path.Combine(Legacy, LegacyDashboardImport.HistoryRelative);
        Write(history, "\"Timestamp\",\"Notes\",\"DeploymentTotal\",\"SiteCritical\"\r\n\"2026-10-20T08:15:00.0000000-04:00\",\"x\",\"9\",\"1\"\r\n");
        var (import, store, _) = Make();
        var plan = import.Plan();
        Assert.Null(plan.Refusal);
        Assert.Equal(1, plan.HistoryRows);
        Assert.True(import.Apply(plan).HistoryCopied);
        var row = Assert.Single(store.Read(365));
        Assert.Equal((9d, 1d), (row.Values[LiveMetrics.DeploymentTotal]!.Value, row.Values[LiveMetrics.SiteCritical]!.Value));
        Assert.Null(row.Values[LiveMetrics.CompliancePct]);
    }

    private static void Junction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint), "The junction was not made.");
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("history")]
    public void AFolderOnThePathOfAnImportedFileThatIsAJunctionIsRefused(string where)
    {
        var elsewhere = Path.Combine(_dir, "elsewhere");
        var link = where == "legacy" ? Path.Combine(Suite, "legacy") : Path.Combine(Legacy, "History");
        if (where == "legacy") Write(Path.Combine(elsewhere, "mecm-health-dashboard", LegacyDashboardImport.DashboardPrefsFile), DashboardPrefs);
        else
        {
            Write(Path.Combine(elsewhere, "metrics-history.csv"), SuiteHistory("2026-10-20T08:15:00.0000000-04:00"));
            Directory.CreateDirectory(Legacy);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Junction(link, elsewhere);
        try
        {
            Assert.True(File.Exists(Path.Combine(Legacy, where == "legacy" ? LegacyDashboardImport.DashboardPrefsFile : LegacyDashboardImport.HistoryRelative)));
            var (import, store, s) = Make();
            var plan = import.Plan();
            Assert.NotNull(plan.Refusal);
            Assert.Contains("is a link. Nothing was imported.", plan.Refusal);
            Assert.Equal(0, s.Fills);
            Assert.False(store.Exists);
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void NoSuiteFolderGivesAnEmptyPlan()
    {
        var (import, _, _) = Make();
        var plan = import.Plan();
        Assert.True(plan.IsEmpty);
        Assert.Equal("", plan.SourceFolder);
        Assert.Equal("Nothing to import. The settings have values and a history exists.", import.Apply(plan).Message);
    }

    [Fact]
    public void ValuesOfAnotherTypeOrOutOfRangeAreNotImportedAndRefreshIsClamped()
    {
        var prefs = Path.Combine(Legacy, LegacyDashboardImport.DashboardPrefsFile);
        Write(prefs, "{\"SQLServer\": 5, \"AutoRefreshMinutes\": 2, \"InactiveThresholdDays\": \"21\", \"AlertsEnabled\": \"yes\", \"AlertCompliancePct\": 150, \"SqlTrustServerCertificate\": true}");
        var values = LegacyDashboardImport.ReadPrefs(prefs);
        Assert.Equal(new LiveSettingValues(SqlServer: null, TrustSqlCertificate: true, RefreshMinutes: LiveSettings.MinRefreshMinutes, InactiveDays: null, AlertsEnabled: null, CompliancePctFloor: null), values);
    }

    // ---- Report writer -------------------------------------------------------------------------

    [Fact]
    public void ALiveReportGuardsFormulasEncodesHtmlAndKeepsTheNotesBesideTheCsv()
    {
        var folder = Path.Combine(_dir, "reports");
        var writer = new LiveReportWriter(new HygieneReportWriter(folder, () => new DateTimeOffset(2026, 10, 25, 10, 29, 6, TimeSpan.FromHours(-4))));
        var report = new LiveReport("Deployments", ["Status", "Name"], [["Failed", "=cmd|' /C calc'!A0"], ["OK", "\n=1+1"], ["OK", "<script>x</script>"]], Truncated: true, Note: "The read was cut at 3 of 9 rows.")
        {
            Filter = "Status: Failed",
            ReadUtc = Now
        };
        var csv = writer.WriteCsv(report);
        Assert.StartsWith("site-hygiene-livedeployments-", Path.GetFileName(csv));
        var text = File.ReadAllText(csv);
        var lines = text.Split("\r\n");
        Assert.Equal("\"Status\",\"Name\"", lines[0].TrimStart('﻿'));
        Assert.Equal("\"Failed\",\"'=cmd|' /C calc'!A0\"", lines[1]);
        Assert.Contains("\r\n\"OK\",\"'\n=1+1\"\r\n", text);
        var about = File.ReadAllText(HygieneReportWriter.AboutPath(csv));
        Assert.Contains("Site Hygiene: Live Deployments.", about);
        Assert.Contains("Read " + Now.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + ".", about);
        Assert.Contains("Filter: Status: Failed", about);
        Assert.Contains("Note: The read was cut at 3 of 9 rows.", about);

        var html = File.ReadAllText(writer.WriteHtml(report));
        Assert.Contains("&lt;script&gt;x&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Equal(folder, writer.Folder);
        Assert.Throws<HygieneServiceException>(() => writer.WriteCsv(report with { Note = null }));
    }
}
