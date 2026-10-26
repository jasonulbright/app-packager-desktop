using System.Text;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using VM = ApplicationPackagerUI.ViewModels.Pages.SiteHygieneViewModel;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>
/// The Live tab over the real Live service with the stub site running the real script, the real metrics history,
/// alert evaluator, alerts log, dashboard import and report writer in a temporary folder. The settings file is the
/// <see cref="AppSettings"/> of the harness, which the Live save and the import fill write as the desktop does.
/// </summary>
internal sealed class LiveRealPage : IDisposable
{
    public LiveRealPage(JsonObject? state = null, int? maxDeployments = null, bool sql = false)
    {
        Directory.CreateDirectory(Folder);
        Site = new StubHygieneSite(state ?? LiveFixtures.State());
        Sql = new FakeSqlHost { ClientsSet = sql ? ClientRows : "@()", SiteLine = """{"t":"error","stage":"site"}""" };
        Service = new SiteHygieneLiveService(Site, Sql.Runner, connection: () => new ConfigMgrConnection("MCM", "CM01"))
        {
            MaxDeployments = maxDeployments ?? SiteHygieneLiveService.DefaultMaxDeployments
        };
        History = new MetricsHistoryStore(Path.Combine(Folder, MetricsHistoryStore.FileName));
        AlertLog = new AlertLog(Path.Combine(Folder, AlertLog.FileName));
        Reports = new LiveReportWriter(new HygieneReportWriter(Path.Combine(Folder, "Reports")));
        Import = new LegacyDashboardImport(History, () => SiteHygieneLivePreferences.Values(Harness.Settings), values =>
        {
            SiteHygieneLivePreferences.FillValues(Harness.Settings, values);
            return true;
        }, suiteFolders: () => [SuiteFolder]);
        Harness = new SiteHygienePageHarness
        {
            Settings = new AppSettings { SiteCode = "MCM", ProviderMachine = "CM01", DiscoverySqlServer = sql ? "CM01.contoso.com" : "", DiscoverySqlDatabase = sql ? "CM_MCM" : "" }
        };
        Harness.RealLive = new SiteHygieneLiveServices(Service, History, Alerts, AlertLog, Import, Reports)
        {
            Delay = Clock.Delay,
            SaveLive = (live, server, database) =>
            {
                Harness.Settings.SiteHygieneLive = live;
                if (server is not null) Harness.Settings.DiscoverySqlServer = server;
                if (database is not null) Harness.Settings.DiscoverySqlDatabase = database;
                return true;
            }
        };
    }

    public const string ClientRows = """
        @(
            ,@([int]16777219, 'CM01', [int]1, [int]1, 'Active/Pass', [datetime]'2026-10-25T14:07:34', [datetime]'2026-10-22T10:18:44', [datetime]'2026-10-19T08:20:53', [datetime]'2026-10-25T14:07:34', [datetime]'2026-10-19T09:27:10', [datetime]'2026-10-23T17:27:05', '5.00.9146.1009', 'Microsoft Windows NT Server 10.0')
            ,@([int]16777222, 'CLIENT02', [int]1, [int]0, 'Inactive/Pass', [datetime]'2026-10-13T12:55:25', [datetime]'2026-10-12T04:40:06', [datetime]'2026-10-12T08:25:53', [datetime]'2026-10-13T12:55:25', [datetime]'2026-10-12T10:43:47', [datetime]'2026-10-01T06:11:51', '5.00.9141.1011', 'Microsoft Windows NT Workstation 10.0')
        )
        """;

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "apkg-shlivepage-" + Guid.NewGuid().ToString("N"));
    public string SuiteFolder => Path.Combine(Folder, "AppPackagerSuite", "site-hygiene");
    public StubHygieneSite Site { get; }
    public FakeSqlHost Sql { get; }
    public SiteHygieneLiveService Service { get; }
    public MetricsHistoryStore History { get; }
    public AlertEvaluator Alerts { get; } = new();
    public AlertLog AlertLog { get; }
    public LegacyDashboardImport Import { get; }
    public LiveReportWriter Reports { get; }
    public FakeLiveClock Clock { get; } = new();
    public SiteHygienePageHarness Harness { get; }

    public async Task<VM> OpenAsync()
    {
        var vm = await Harness.OpenAsync();
        vm.SelectedTabIndex = VM.LiveTab;
        return vm;
    }

    /// <summary>The cells of the history lines after the header, by column.</summary>
    public List<Dictionary<string, string>> HistoryLines()
    {
        var lines = File.ReadAllLines(History.Path);
        Assert.Equal(MetricsHistoryStore.HeaderLine, lines[0]);
        var header = lines[0].Split(',');
        return lines.Skip(1).Select(l => header.Zip(l.Split(',')).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal)).ToList();
    }

    public void Dispose()
    {
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { }
    }
}

public sealed class SiteHygieneLivePageRealServiceTests
{
    private static LiveViewChoice View(LiveView view) => SiteHygieneLiveLabels.Views.Single(v => v.View == view);

    private static readonly string[] DeploymentCells = [LiveMetrics.DeploymentTotal, LiveMetrics.DeploymentFailed, LiveMetrics.CompliancePct];

    [Fact]
    public async Task ARefreshReadsEveryViewAppendsOneHistoryLineAndCopiesAndExportsWhatItRead()
    {
        using var p = new LiveRealPage(sql: true);
        var vm = await p.OpenAsync();
        await vm.RefreshLiveAsync();

        var snapshot = vm.LastSnapshot!;
        Assert.All(Enum.GetValues<LiveView>(), v => Assert.True(snapshot.Views[v].Complete, $"{v}: {snapshot.Views[v].Message}"));
        Assert.StartsWith("The refresh ended: 6 views read, 10 queries.", vm.StatusMessage);
        var line = Assert.Single(p.HistoryLines());
        Assert.Equal(("9", "0", "58.3"), (line[LiveMetrics.DeploymentTotal], line[LiveMetrics.DeploymentFailed], line[LiveMetrics.CompliancePct]));
        Assert.Equal(("0", "0", "1", "2", "0", "1"), (line[LiveMetrics.ContentIssues], line[LiveMetrics.ContentFailedPairs], line[LiveMetrics.DPTotal], line[LiveMetrics.ClientHealthy],
            line[LiveMetrics.ClientUnhealthy], line[LiveMetrics.ClientInactive]));
        Assert.Equal("0", line[LiveMetrics.InactiveDevices]);
        // The first refresh of the session sets the starting state: the compliance below the floor raises no alert.
        Assert.Empty(vm.AlertRows);
        Assert.Equal([LiveAlertMetrics.Compliance], p.Alerts.Breached);
        Assert.False(File.Exists(p.AlertLog.Path));

        vm.CopyLiveSummaryCommand.Execute(null);
        var summary = p.Harness.Dialogs.Clipboard[^1];
        Assert.Equal(LiveText.Summary(snapshot, snapshot.EndedUtc.ToLocalTime()), summary);
        Assert.Contains("Deployments: 9 deployments, 0 with errors, compliance 58.3 %.", summary);
        Assert.Contains("Client Health: 2 healthy, 0 unhealthy, 1 inactive.", summary);

        vm.SelectedLiveView = View(LiveView.Deployments);
        vm.LiveStatusFilter = "Warning";
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.StartsWith("Saved the CSV report to ", vm.StatusMessage);
        var csv = Directory.GetFiles(Path.Combine(p.Folder, "Reports"), "*.csv").Single();
        var rows = File.ReadAllLines(csv);
        Assert.Equal("\"Status\",\"Name\",\"Type\",\"Collection\",\"Purpose\",\"Targeted\",\"Success\",\"Errors\",\"In progress\",\"Compliance (%)\"", rows[0].TrimStart('﻿'));
        Assert.Equal(vm.LiveViewRows.Count + 1, rows.Length);
        Assert.All(rows.Skip(1), r => Assert.StartsWith("\"▲ ", r));
        Assert.Contains("Filter: Status: Warning", File.ReadAllText(HygieneReportWriter.AboutPath(csv)));
        await vm.ExportHtmlCommand.ExecuteAsync(null);
        Assert.Contains("CMCO-CANARY-SWU-Group", File.ReadAllText(Directory.GetFiles(Path.Combine(p.Folder, "Reports"), "*.html").Single()));
    }

    [Fact]
    public async Task AFailedViewWritesEmptyCellsNeverZeroAndShowsItsMessageWhileTheOtherViewsShowTheirRows()
    {
        using var p = new LiveRealPage();
        p.Site.Fail("query:SMS_DistributionDPStatus");
        var vm = await p.OpenAsync();
        await vm.RefreshLiveAsync();

        Assert.Contains("1 failed (Content)", vm.StatusMessage);
        Assert.Contains("2 not set up", vm.StatusMessage);
        vm.SelectedLiveView = View(LiveView.Content);
        Assert.Equal("The read of this view failed. The read of Content failed. The log has the kind of failure.", vm.LiveViewMessage);
        Assert.Empty(vm.LiveViewRows);
        vm.SelectedLiveView = View(LiveView.ClientHealth);
        Assert.StartsWith(LiveText.SqlNotSetUp, vm.LiveViewMessage);
        vm.SelectedLiveView = View(LiveView.Deployments);
        Assert.Equal(9, vm.LiveViewRows.Count);
        Assert.Contains("Content failed", vm.LiveSummaryLine);

        var line = Assert.Single(p.HistoryLines());
        foreach (var column in LiveMetrics.Columns)
        {
            var view = LiveMetrics.ViewOf(column);
            if (view is LiveView.Content or LiveView.ClientHealth or LiveView.InactiveDevices) Assert.True(line[column] == "", $"{column} is \"{line[column]}\".");
            else Assert.NotEqual("", line[column]);
        }
        Assert.Empty(p.History.Series(LiveMetrics.ContentFailedPairs, 30, 100).Points);
        Assert.Single(p.History.Series(LiveMetrics.DeploymentTotal, 30, 100).Points);
    }

    [Fact]
    public async Task AReadCutAtItsCapShowsItsRowsAndLeavesItsCountsNotKnown()
    {
        using var p = new LiveRealPage(maxDeployments: 3);
        var vm = await p.OpenAsync();
        await vm.RefreshLiveAsync();

        vm.SelectedLiveView = View(LiveView.Deployments);
        Assert.Equal(3, vm.LiveViewRows.Count);
        Assert.Equal("The read was cut at 3 of 9 rows. The counts of this view are not known.", vm.LiveViewMessage);
        Assert.Contains("Deployments cut at 3 rows", vm.LiveSummaryLine);
        var line = Assert.Single(p.HistoryLines());
        Assert.All(DeploymentCells, c => Assert.Equal("", line[c]));
        Assert.Equal("0", line[LiveMetrics.ContentIssues]);
        vm.CopyLiveSummaryCommand.Execute(null);
        Assert.Contains("Deployments: not known deployments, not known with errors, compliance not known %. The read was cut at its row cap, so the counts are not known.", p.Harness.Dialogs.Clipboard[^1]);
        Assert.DoesNotContain("Deployments: 0", p.Harness.Dialogs.Clipboard[^1]);
    }

    [Fact]
    public async Task AnAlertFiresOnceOnTheBreachIsNeitherRaisedNorClearedByAFailedViewAndFiresAgainAfterARecovery()
    {
        using var p = new LiveRealPage();
        // The stub site replaces its state after each run, so every change goes through its current state.
        JsonObject Executive() => p.Site.Class("SMS_ComponentSummarizer").OfType<JsonObject>()
            .First(c => (string?)c["ComponentName"] == "SMS_EXECUTIVE" && (string?)c["TallyInterval"] == "0001128000100008");
        var vm = await p.OpenAsync();
        vm.AlertCompliance = false;
        await vm.RefreshLiveAsync();
        Assert.Empty(vm.AlertRows);

        Executive()["Status"] = 2;
        await vm.RefreshLiveAsync();
        var alert = Assert.Single(vm.AlertRows);
        Assert.Equal(LiveAlertMetrics.SiteCritical, alert.Alert.Metric);
        Assert.Equal("Site health: 1 component or site system is critical.", alert.Message);
        Assert.True(vm.HasAlertBanner);
        Assert.Single(File.ReadAllLines(p.AlertLog.Path));
        Assert.Contains("\tSiteCritical\t", File.ReadAllText(p.AlertLog.Path));

        await vm.RefreshLiveAsync();
        Assert.Single(vm.AlertRows);

        p.Site.Fail("query:SMS_ComponentSummarizer");
        await vm.RefreshLiveAsync();
        Assert.True(vm.LastSnapshot!.Views[LiveView.SiteHealth].Failed);
        Assert.Single(vm.AlertRows);
        Assert.Equal([LiveAlertMetrics.SiteCritical], p.Alerts.Breached);

        p.Site.State["Options"]!["Fail"] = new JsonArray();
        Executive()["Status"] = 0;
        await vm.RefreshLiveAsync();
        Assert.Empty(p.Alerts.Breached);
        Assert.Single(vm.AlertRows);

        Executive()["Status"] = 2;
        await vm.RefreshLiveAsync();
        Assert.Equal(2, vm.AlertRows.Count);
        Assert.Equal(2, File.ReadAllLines(p.AlertLog.Path).Length);
        Assert.Equal(6, p.HistoryLines().Count);
        Assert.Equal(["0", "1", "1", "", "0", "1"], p.HistoryLines().Select(l => l[LiveMetrics.SiteCritical]));
    }

    private const string DashboardPrefs = "{\r\n  \"SQLServer\": \"CM01.contoso.com\",\r\n  \"AutoRefreshMinutes\": 30,\r\n  \"InactiveThresholdDays\": 21,\r\n  \"AlertsEnabled\": false,\r\n  \"AlertCompliancePct\": 90\r\n}";

    private static string SuiteHistory(params string[] times)
    {
        var sb = new StringBuilder("\"Timestamp\",\"DeploymentTotal\",\"DeploymentFailed\",\"CompliancePct\",\"ContentIssues\",\"ContentFailedPairs\",\"DPTotal\",\"DPOffline\",\"DPDegraded\",\"ClientHealthy\",\"ClientUnhealthy\",\"ClientInactive\",\"InactiveDevices\",\"SiteOK\",\"SiteWarning\",\"SiteCritical\"\r\n");
        foreach (var t in times) sb.Append('"').Append(t).Append("\",\"9\",\"0\",\"58.3\",\"0\",\"0\",\"1\",\"0\",\"0\",\"3\",\"0\",\"1\",\"\",\"84\",\"0\",\"0\"\r\n");
        sb.Append("not a line\r\n");
        return sb.ToString();
    }

    [Fact]
    public async Task TheImportPlanShowsWhatTheRealImportCopiesTheApplyFillsOnlyEmptyValuesAndASecondImportChangesNothing()
    {
        using var p = new LiveRealPage();
        var legacy = Path.Combine(p.SuiteFolder, LegacyDashboardImport.LegacyFolder);
        Directory.CreateDirectory(Path.Combine(legacy, "History"));
        File.WriteAllText(Path.Combine(legacy, LegacyDashboardImport.DashboardPrefsFile), DashboardPrefs, new UTF8Encoding(true));
        var now = DateTimeOffset.Now;
        File.WriteAllText(Path.Combine(legacy, LegacyDashboardImport.HistoryRelative), SuiteHistory(now.AddDays(-2).ToString("o"), now.AddDays(-1).ToString("o")), new UTF8Encoding(true));
        p.Harness.Settings.SiteHygieneLive = new SiteHygieneLiveSettings { InactiveDays = 45 };
        var vm = await p.OpenAsync();
        Assert.True(vm.HasImportOffer);
        Assert.Contains(legacy, vm.ImportOffer);
        Assert.False(p.History.Exists);

        vm.AlertsEnabled = !vm.AlertsEnabled;
        await vm.ImportDashboardCommand.ExecuteAsync(null);
        var plan = p.Harness.Dialogs.Imports.Single().Plan;
        Assert.Contains("Metrics history: 2 rows.", plan);
        Assert.Contains("The history file has 1 line that cannot be read. The import skips them.", plan);
        Assert.Contains("Setting SQL server: CM01.contoso.com", plan);
        Assert.Contains("Setting Refresh interval: 30 minutes", plan);
        Assert.Contains("Setting Compliance floor: 90 %", plan);
        Assert.DoesNotContain("Inactivity threshold", plan);
        Assert.DoesNotContain("Setting Alerts", plan);

        var stored = p.Harness.Settings.SiteHygieneLive!;
        Assert.Equal(("CM01.contoso.com", (int?)30, (int?)45, (int?)90, (bool?)false), (p.Harness.Settings.DiscoverySqlServer, stored.RefreshMinutes, stored.InactiveDays, stored.CompliancePctFloor, stored.AlertsEnabled));
        Assert.Equal(("CM01.contoso.com", 30, 45, 90, false), (vm.LiveSqlServer, vm.RefreshMinutes, vm.InactiveDays, vm.CompliancePctFloor, vm.AlertsEnabled));
        Assert.Contains("Copied 2 history rows.", vm.StatusMessage);
        Assert.Equal(2, p.History.Read(30).Count);
        Assert.False(vm.HasImportOffer);
        Assert.True(p.Harness.Saved!.ImportOfferDismissed);

        var history = File.ReadAllBytes(p.History.Path);
        var settings = System.Text.Json.JsonSerializer.Serialize(p.Harness.Settings);
        await vm.ImportDashboardCommand.ExecuteAsync(null);
        Assert.Equal(VM.ImportNothing, vm.StatusMessage);
        Assert.Single(p.Harness.Dialogs.Imports);
        Assert.Equal(history, File.ReadAllBytes(p.History.Path));
        Assert.Equal(settings, System.Text.Json.JsonSerializer.Serialize(p.Harness.Settings));
        Assert.True(p.Import.Plan().IsEmpty);

        vm.SelectedLiveView = SiteHygieneLiveLabels.Views.Single(v => v.Pane == LivePane.Trends);
        vm.SelectedTrendMetric = SiteHygieneLiveLabels.Metrics.Single(m => m.Column == LiveMetrics.DeploymentTotal);
        await vm.TrendTask;
        Assert.Equal(2, vm.TrendRows.Count);
    }
}
