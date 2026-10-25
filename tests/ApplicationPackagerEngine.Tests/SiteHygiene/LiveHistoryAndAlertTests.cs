using System.Diagnostics;
using System.Text;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>The metrics history file, the alert evaluator and the alerts log.</summary>
public sealed class LiveHistoryAndAlertTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apkg-shlive-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 10, 25, 14, 29, 6, DateTimeKind.Utc);

    public LiveHistoryAndAlertTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string File1 => Path.Combine(_dir, "metrics-history.csv");

    private MetricsHistoryStore Store(DateTime? now = null) => new(File1, utcNow: () => now ?? Now);

    internal static LiveViewResult View(LiveView view, bool failed = false, bool truncated = false, params (string Column, double? Value)[] counts)
    {
        var values = LiveMetrics.Of(view).ToDictionary(c => c, c => counts.FirstOrDefault(x => x.Column == c).Value, StringComparer.Ordinal);
        if (failed || truncated) foreach (var k in values.Keys.ToList()) values[k] = null;
        return new LiveViewResult(view, [], [], new LiveCounts(values), truncated, failed, null, Now);
    }

    internal static LiveSnapshot Snapshot(DateTime ended, params LiveViewResult[] views) =>
        new(views.ToDictionary(v => v.View), ended.AddSeconds(-5), ended);

    private static MetricRow Row(DateTime t, double? deployments = 9, double? compliance = 58.3) =>
        new(t, LiveMetrics.Columns.ToDictionary(c => c, c => c switch
        {
            LiveMetrics.DeploymentTotal => deployments,
            LiveMetrics.CompliancePct => compliance,
            LiveMetrics.SiteOK => 16,
            _ => (double?)0
        }, StringComparer.Ordinal));

    private static string RunPowerShell(string script)
    {
        var path = Path.Combine(Path.GetTempPath(), "apkg-shps-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(path, script, new UTF8Encoding(true));
        try
        {
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.True(err.Result.Trim().Length == 0, err.Result);
            return output;
        }
        finally { File.Delete(path); }
    }

    // ---- Metrics history -------------------------------------------------------------------------

    [Fact]
    public void AnAppendWritesTheSourceHeaderOnceAndOneLineThatImportCsvReadsWithEmptyCellsForUnknownCounts()
    {
        var store = Store();
        Assert.False(store.Exists);
        var snapshot = Snapshot(Now,
            View(LiveView.Deployments, counts: [(LiveMetrics.DeploymentTotal, 9), (LiveMetrics.DeploymentFailed, 0), (LiveMetrics.CompliancePct, 58.3)]),
            View(LiveView.Content, failed: true),
            View(LiveView.SiteHealth, counts: [(LiveMetrics.SiteOK, 84), (LiveMetrics.SiteWarning, 0), (LiveMetrics.SiteCritical, 0)]));
        store.Append(snapshot.ToMetricRow());
        store.Append(snapshot.ToMetricRow() with { TimestampUtc = Now.AddMinutes(15) });
        Assert.True(store.Exists);
        var lines = File.ReadAllLines(File1);
        Assert.Equal(3, lines.Length);
        Assert.Equal("Timestamp,DeploymentTotal,DeploymentFailed,CompliancePct,ContentIssues,ContentFailedPairs,DPTotal,DPOffline,DPDegraded,ClientHealthy,ClientUnhealthy,ClientInactive,InactiveDevices,SiteOK,SiteWarning,SiteCritical", lines[0]);
        Assert.Equal("2026-10-25T14:29:06.0000000Z,9,0,58.3,,,,,,,,,,84,0,0", lines[1]);

        var output = RunPowerShell("$r = @(Import-Csv -LiteralPath '" + File1 + "'); $r.Count; $r[0].DeploymentTotal; '[' + $r[0].ContentIssues + ']'; $r[1].SiteOK; ($r[0].PSObject.Properties | ForEach-Object Name) -join ','");
        var o = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["2", "9", "[]", "84", string.Join(",", LiveMetrics.FileColumns)], o);

        var read = store.Read(30);
        Assert.Equal(2, read.Count);
        Assert.Null(read[0].Values[LiveMetrics.ContentIssues]);
        Assert.Equal(0, read[0].Values[LiveMetrics.DeploymentFailed]);
        Assert.Equal(Now, read[0].TimestampUtc);
    }

    [Fact]
    public void ACorruptLineIsSkippedAndCountedAndNeverFatal()
    {
        var store = Store();
        store.Append(Row(Now.AddHours(-2)));
        File.AppendAllText(File1, "garbage\r\nnot-a-time,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15\r\n2026-10-25T13:00:00Z,x,0,0,0,0,0,0,0,0,0,0,0,0,0,0\r\n2026-10-25T13:10:00Z,1,2\r\n\r\n" + MetricsHistoryStore.HeaderLine + "\r\n");
        store.Append(Row(Now.AddHours(-1)));
        File.AppendAllText(File1, "2026-10-25T14:00:00Z,1,2,3");
        var rows = store.Read(30);
        Assert.Equal(2, rows.Count);
        Assert.Equal(5, store.ReadCounted(30).Skipped);
        var series = store.Series(LiveMetrics.DeploymentTotal, 30, 500);
        Assert.Equal(5, series.SkippedLines);
        Assert.Equal(2, series.Points.Count);
    }

    [Fact]
    public void TheSuiteHistoryFileWithQuotedCellsAByteOrderMarkAndLocalOffsetsReads()
    {
        var text = "\"Timestamp\",\"DeploymentTotal\",\"DeploymentFailed\",\"CompliancePct\",\"ContentIssues\",\"ContentFailedPairs\",\"DPTotal\",\"DPOffline\",\"DPDegraded\",\"ClientHealthy\",\"ClientUnhealthy\",\"ClientInactive\",\"InactiveDevices\",\"SiteOK\",\"SiteWarning\",\"SiteCritical\"\r\n" +
                   "\"2026-10-20T08:15:00.1234567-04:00\",\"9\",\"1\",\"58.3\",\"0\",\"0\",\"1\",\"0\",\"0\",\"\",\"\",\"\",\"\",\"84\",\"0\",\"0\"\r\n";
        File.WriteAllText(File1, text, new UTF8Encoding(true));
        var rows = Store().Read(30);
        var row = Assert.Single(rows);
        Assert.Equal(new DateTime(2026, 10, 20, 12, 15, 0, DateTimeKind.Utc).AddTicks(1234567), row.TimestampUtc);
        Assert.Equal(58.3, row.Values[LiveMetrics.CompliancePct]);
        Assert.Null(row.Values[LiveMetrics.ClientHealthy]);
    }

    [Fact]
    public async Task ConcurrentAppendsFromTwoStoresKeepEveryWholeLineAndOneHeader()
    {
        var a = Store();
        var b = Store();
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            var store = i % 2 == 0 ? a : b;
            for (var n = 0; n < 50; n++) store.Append(Row(Now.AddSeconds(i * 1000 + n), deployments: i * 1000 + n));
        })).ToArray();
        await Task.WhenAll(tasks);
        var lines = File.ReadAllLines(File1);
        Assert.Equal(1, lines.Count(l => l.StartsWith("Timestamp,", StringComparison.Ordinal)));
        Assert.Equal(401, lines.Length);
        var rows = a.Read(30);
        Assert.Equal(400, rows.Count);
        Assert.Equal(0, a.ReadCounted(30).Skipped);
        Assert.Equal(400, rows.Select(r => r.Values[LiveMetrics.DeploymentTotal]).Distinct().Count());
    }

    [Fact]
    public void ThePruneDropsRowsOlderThan180DaysAtMostOnceADayAndKeepsTheRest()
    {
        File.WriteAllText(File1, MetricsHistoryStore.HeaderLine + "\r\n" + MetricsHistoryStore.Format(Row(Now.AddDays(-200))) + "\r\n" + MetricsHistoryStore.Format(Row(Now.AddDays(-10))) + "\r\n");
        var store = Store();
        store.Append(Row(Now));
        var lines = File.ReadAllLines(File1);
        Assert.Equal(3, lines.Length);
        Assert.DoesNotContain(lines, l => l.StartsWith(Now.AddDays(-200).ToString("yyyy-MM-dd"), StringComparison.Ordinal));

        File.AppendAllText(File1, MetricsHistoryStore.Format(Row(Now.AddDays(-300))) + "\r\n");
        var written = File.GetLastWriteTimeUtc(File1);
        store.Append(Row(Now.AddMinutes(15)));
        Assert.Equal(5, File.ReadAllLines(File1).Length);

        var nextDay = Store(Now.AddDays(1).AddMinutes(1));
        File.WriteAllText(File1, MetricsHistoryStore.HeaderLine + "\r\n" + MetricsHistoryStore.Format(Row(Now.AddDays(-10))) + "\r\n");
        nextDay.Append(Row(Now.AddDays(1)));
        Assert.Equal(3, File.ReadAllLines(File1).Length);
        Assert.True(written <= File.GetLastWriteTimeUtc(File1));
    }

    [Fact]
    public void AnAppendWithinADayOfThePruneDoesNotRewriteTheFileEvenWhenItsFirstLineIsOld()
    {
        var store = Store();
        store.Append(Row(Now));
        var old = MetricsHistoryStore.Format(Row(Now.AddDays(-300)));
        File.WriteAllText(File1, MetricsHistoryStore.HeaderLine + "\r\n" + old + "\r\n");
        store.Append(Row(Now.AddMinutes(15)));
        var lines = File.ReadAllLines(File1);
        Assert.Equal(3, lines.Length);
        Assert.Equal(old, lines[1]);
    }

    [Fact]
    public void AnImportIntoAnExistingHistoryWritesNothing()
    {
        var store = Store();
        store.Append(Row(Now));
        var before = File.ReadAllBytes(File1);
        Assert.False(store.ImportIfEmpty([Row(Now.AddDays(-2)), Row(Now.AddDays(-1))]));
        Assert.Equal(before, File.ReadAllBytes(File1));
    }

    [Fact]
    public void AReadOfAFileOverTheCapTakesTheNewestRowsOnly()
    {
        var sb = new StringBuilder(MetricsHistoryStore.HeaderLine + "\r\n");
        for (var i = 0; i < 2000; i++) sb.Append(MetricsHistoryStore.Format(Row(Now.AddMinutes(i - 2000), deployments: i))).Append("\r\n");
        File.WriteAllText(File1, sb.ToString());
        var (rows, skipped, cut) = MetricsHistoryStore.ReadFile(File1, 10_000);
        Assert.True(cut);
        Assert.Equal(0, skipped);
        Assert.InRange(rows.Count, 100, 250);
        Assert.Equal(1999, rows.Max(r => r.Values[LiveMetrics.DeploymentTotal]!.Value));
        Assert.Equal(1999 - rows.Count + 1, rows.Min(r => r.Values[LiveMetrics.DeploymentTotal]!.Value));
    }

    [Fact]
    public void ASeriesBreaksAtEmptyCellsKeepsThePeriodAndDownsamplesByBucket()
    {
        var store = Store();
        var rows = new List<MetricRow>();
        for (var i = 0; i < 10; i++) rows.Add(Row(Now.AddDays(-40 + i), deployments: i == 4 || i == 5 ? null : i));
        foreach (var r in rows) store.Append(r);
        var series = store.Series(LiveMetrics.DeploymentTotal, 90, 500);
        Assert.Equal([0, 1, 2, 3, 6, 7, 8, 9], series.Points.Select(p => p.V));
        Assert.Equal([4], series.Gaps);
        Assert.False(series.Downsampled);
        Assert.Equal(8, series.SourcePoints);
        Assert.Empty(store.Series(LiveMetrics.DeploymentTotal, 7, 500).Points);

        var many = Enumerable.Range(0, 1000).Select(i => Row(Now.AddMinutes(i), deployments: i)).ToList();
        var down = MetricsHistoryStore.Build(LiveMetrics.DeploymentTotal, many, 0, 100);
        Assert.True(down.Downsampled);
        Assert.Equal(100, down.Points.Count);
        Assert.Equal(4.5, down.Points[0].V);
        Assert.Equal(1000, down.SourcePoints);
        Assert.Empty(down.Gaps);

        var alternating = Enumerable.Range(0, 1000).Select(i => Row(Now.AddMinutes(i), deployments: i % 2 == 0 ? i : null)).ToList();
        var sparse = MetricsHistoryStore.Build(LiveMetrics.DeploymentTotal, alternating, 0, 100);
        Assert.True(sparse.Points.Count <= 100);
        Assert.NotEmpty(sparse.Gaps);
    }

    [Fact]
    public void ADownsampledBucketWithAnEmptyCellBetweenKnownValuesBreaksTheLineInsteadOfAveragingAcrossIt()
    {
        // Two split buckets would pass the cap of 100 points with buckets of 10 rows, so the buckets hold 20 rows:
        // row 15 is empty inside the first bucket and row 30 inside the second.
        var rows = Enumerable.Range(0, 1000).Select(i => Row(Now.AddMinutes(i), deployments: i is 15 or 30 ? null : i)).ToList();
        var series = MetricsHistoryStore.Build(LiveMetrics.DeploymentTotal, rows, 0, 100);
        Assert.True(series.Downsampled);
        Assert.Equal([7, 17.5, 24.5, 35, 49.5], series.Points.Take(5).Select(p => p.V));
        Assert.Equal([1, 3], series.Gaps);
        Assert.Equal(998, series.SourcePoints);
        Assert.Equal(52, series.Points.Count);

        // An empty cell at the start of a bucket breaks the line from the bucket before it.
        var edge = Enumerable.Range(0, 1000).Select(i => Row(Now.AddMinutes(i), deployments: i == 10 ? null : i)).ToList();
        var split = MetricsHistoryStore.Build(LiveMetrics.DeploymentTotal, edge, 0, 100);
        Assert.Equal([4.5, 15], split.Points.Take(2).Select(p => p.V));
        Assert.Equal([1], split.Gaps);
        Assert.Equal(100, split.Points.Count);
    }

    // ---- Alerts --------------------------------------------------------------------------------

    private static LiveSnapshot Site(double? critical, bool failed = false, int minute = 0) =>
        Snapshot(Now.AddMinutes(minute), View(LiveView.SiteHealth, failed, counts: [(LiveMetrics.SiteOK, 10), (LiveMetrics.SiteWarning, 0), (LiveMetrics.SiteCritical, critical)]));

    [Fact]
    public void AnAlertFiresOnTheCrossingIntoBreachAndAgainOnlyAfterARecovery()
    {
        var alerts = new AlertEvaluator();
        var on = AlertSettings.Default;
        Assert.Empty(alerts.Evaluate(Site(0), on));
        var first = Assert.Single(alerts.Evaluate(Site(2, minute: 15), on));
        Assert.Equal((LiveAlertMetrics.SiteCritical, "Site health: 2 components or site systems are critical.", Now.AddMinutes(15)), (first.Metric, first.Message, first.TimeUtc));
        Assert.Equal([LiveAlertMetrics.SiteCritical], alerts.Breached);
        Assert.Empty(alerts.Evaluate(Site(3), on));
        Assert.Empty(alerts.Evaluate(Site(0), on));
        Assert.Empty(alerts.Breached);
        Assert.Single(alerts.Evaluate(Site(1), on));
    }

    [Fact]
    public void AnUnknownMetricNeitherFiresNorClearsItsAlert()
    {
        var alerts = new AlertEvaluator();
        var on = AlertSettings.Default;
        Assert.Empty(alerts.Evaluate(Site(0), on));
        Assert.Single(alerts.Evaluate(Site(1), on));
        Assert.Empty(alerts.Evaluate(Site(null, failed: true), on));
        Assert.Empty(alerts.Evaluate(Snapshot(Now), on));
        Assert.Equal([LiveAlertMetrics.SiteCritical], alerts.Breached);
        Assert.Empty(alerts.Evaluate(Site(1), on));
        Assert.Empty(alerts.Evaluate(Snapshot(Now, View(LiveView.SiteHealth, truncated: true)), on));
        Assert.Empty(alerts.Evaluate(Site(1), on));

        // A failed first read sets no starting state: the first known value does.
        var fresh = new AlertEvaluator();
        Assert.Empty(fresh.Evaluate(Site(null, failed: true), on));
        Assert.Empty(fresh.Evaluate(Site(1), on));
        Assert.Equal([LiveAlertMetrics.SiteCritical], fresh.Breached);
        Assert.Empty(fresh.Evaluate(Site(0), on));
        Assert.Single(fresh.Evaluate(Site(1), on));
    }

    [Fact]
    public void ABreachThatIsThereAtTheFirstEvaluationOfASessionFiresNoAlertAndOnlyALaterCrossingDoes()
    {
        var snapshot = Snapshot(Now,
            View(LiveView.Deployments, counts: [(LiveMetrics.DeploymentTotal, 9), (LiveMetrics.DeploymentFailed, 0), (LiveMetrics.CompliancePct, 58.3)]),
            View(LiveView.SiteHealth, counts: [(LiveMetrics.SiteOK, 10), (LiveMetrics.SiteWarning, 0), (LiveMetrics.SiteCritical, 2)]));
        // Each app start makes a new evaluator; the same breach at each start raises nothing.
        for (var start = 0; start < 3; start++)
        {
            var alerts = new AlertEvaluator();
            Assert.Empty(alerts.Evaluate(snapshot, AlertSettings.Default));
            Assert.Equal([LiveAlertMetrics.SiteCritical, LiveAlertMetrics.Compliance], alerts.Breached.Order(StringComparer.Ordinal).Reverse());
            Assert.Empty(alerts.Evaluate(snapshot, AlertSettings.Default));
        }
        var session = new AlertEvaluator();
        Assert.Empty(session.Evaluate(Site(0), AlertSettings.Default));
        Assert.Single(session.Evaluate(Site(2), AlertSettings.Default));
    }

    [Fact]
    public void AlertsTurnedOffKeepTheBreachUntilARecoveryIsSeenSoABreachThatComesBackWhileTheyAreOffFiresWhenTheyAreOnAgain()
    {
        var alerts = new AlertEvaluator();
        var on = AlertSettings.Default;
        var off = on with { Enabled = false };
        Assert.Empty(alerts.Evaluate(Site(0), on));
        Assert.Single(alerts.Evaluate(Site(1), on));
        Assert.Empty(alerts.Evaluate(Site(1), off));
        Assert.Equal([LiveAlertMetrics.SiteCritical], alerts.Breached);
        Assert.Empty(alerts.Evaluate(Site(0), off));
        Assert.Empty(alerts.Breached);
        Assert.Empty(alerts.Evaluate(Site(3), off));
        var again = Assert.Single(alerts.Evaluate(Site(3), on));
        Assert.Equal(LiveAlertMetrics.SiteCritical, again.Metric);
        Assert.Empty(alerts.Evaluate(Site(3), on));
    }

    /// <summary>A view that is not complete counts as not known even when its result carries counts.</summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void CountsOfAViewThatIsNotCompleteReachNeitherTheHistoryNorTheAlerts(bool failed, bool truncated, bool skipped)
    {
        var counts = new LiveCounts(new Dictionary<string, double?> { [LiveMetrics.SiteOK] = 10, [LiveMetrics.SiteWarning] = 0, [LiveMetrics.SiteCritical] = 3 });
        var view = new LiveViewResult(LiveView.SiteHealth, [], [], counts, truncated, failed, null, Now) { Skipped = skipped };
        var snapshot = Snapshot(Now, view);
        Assert.All(LiveMetrics.Of(LiveView.SiteHealth), c => Assert.Null(snapshot.ToMetricRow().Values[c]));

        var alerts = new AlertEvaluator();
        Assert.Empty(alerts.Evaluate(snapshot, AlertSettings.Default));
        Assert.Empty(alerts.Breached);
        Assert.Empty(alerts.Evaluate(Site(0), AlertSettings.Default));
        Assert.Single(alerts.Evaluate(Site(1), AlertSettings.Default));
        Assert.Empty(alerts.Evaluate(Snapshot(Now, view with { Counts = new LiveCounts(new Dictionary<string, double?> { [LiveMetrics.SiteCritical] = 0 }) }), AlertSettings.Default));
        Assert.Equal([LiveAlertMetrics.SiteCritical], alerts.Breached);
    }

    [Fact]
    public void EachRuleFiresOnItsOwnMetricAndTheComplianceFloorSkipsUnknownCompliance()
    {
        var alerts = new AlertEvaluator();
        var on = AlertSettings.Default;
        var snapshot = Snapshot(Now,
            View(LiveView.Deployments, counts: [(LiveMetrics.DeploymentTotal, 9), (LiveMetrics.DeploymentFailed, 0), (LiveMetrics.CompliancePct, 58.3)]),
            View(LiveView.Content, counts: [(LiveMetrics.ContentIssues, 2), (LiveMetrics.ContentFailedPairs, 3)]),
            View(LiveView.DistributionPoints, counts: [(LiveMetrics.DPTotal, 1), (LiveMetrics.DPOffline, 1), (LiveMetrics.DPDegraded, 0)]));
        var healthy = Snapshot(Now,
            View(LiveView.Deployments, counts: [(LiveMetrics.DeploymentTotal, 9), (LiveMetrics.DeploymentFailed, 0), (LiveMetrics.CompliancePct, 100)]),
            View(LiveView.Content, counts: [(LiveMetrics.ContentIssues, 0), (LiveMetrics.ContentFailedPairs, 0)]),
            View(LiveView.DistributionPoints, counts: [(LiveMetrics.DPTotal, 1), (LiveMetrics.DPOffline, 0), (LiveMetrics.DPDegraded, 0)]));
        Assert.Empty(alerts.Evaluate(healthy, on));
        var events = alerts.Evaluate(snapshot, on);
        Assert.Equal([LiveAlertMetrics.DpCritical, LiveAlertMetrics.ContentFailed, LiveAlertMetrics.Compliance], events.Select(e => e.Metric));
        Assert.Equal("Deployments: overall compliance 58.3 % is below the 80 % floor.", events[2].Message);
        Assert.Equal("Content: 3 DP-content pairs failed.", events[1].Message);

        var nothingTargeted = new AlertEvaluator();
        Assert.Empty(nothingTargeted.Evaluate(healthy, on));
        Assert.Empty(nothingTargeted.Evaluate(Snapshot(Now, View(LiveView.Deployments, counts: [(LiveMetrics.DeploymentTotal, 9), (LiveMetrics.DeploymentFailed, 0), (LiveMetrics.CompliancePct, null)])), on));
        var lowFloor = new AlertEvaluator();
        Assert.Empty(lowFloor.Evaluate(healthy, on));
        Assert.Empty(lowFloor.Evaluate(snapshot, on with { CompliancePctFloor = 50, ContentFailed = false, DpCritical = false }));
    }

    [Fact]
    public void DisabledAlertsFireNothingAndARuleTurnedOffKeepsItsBreachWhileTheValueIsUnknownOrStillBreached()
    {
        var alerts = new AlertEvaluator();
        Assert.Empty(alerts.Evaluate(Site(1), AlertSettings.Default with { Enabled = false }));
        Assert.Single(alerts.Evaluate(Site(1), AlertSettings.Default));
        Assert.Empty(alerts.Evaluate(Site(1), AlertSettings.Default with { SiteCritical = false }));
        Assert.Equal([LiveAlertMetrics.SiteCritical], alerts.Breached);
        Assert.Empty(alerts.Evaluate(Site(1), AlertSettings.Default));
        Assert.Empty(alerts.Evaluate(Site(null, failed: true), AlertSettings.Default with { SiteCritical = false }));
        Assert.Equal([LiveAlertMetrics.SiteCritical], alerts.Breached);
        Assert.Empty(alerts.Evaluate(Site(0), AlertSettings.Default with { SiteCritical = false }));
        Assert.Empty(alerts.Breached);
        Assert.Single(alerts.Evaluate(Site(1), AlertSettings.Default));
    }

    [Fact]
    public void ALatchedBreachSurvivesAlertsTurnedOffWhileItsViewFailsAndDoesNotFireAgainWhenTheyAreOnAgain()
    {
        var alerts = new AlertEvaluator();
        var on = AlertSettings.Default;
        var off = on with { Enabled = false };
        Assert.Empty(alerts.Evaluate(Site(0), on));
        Assert.Single(alerts.Evaluate(Site(2), on));
        Assert.Empty(alerts.Evaluate(Site(null, failed: true), off));
        Assert.Empty(alerts.Evaluate(Snapshot(Now), off));
        Assert.Equal([LiveAlertMetrics.SiteCritical], alerts.Breached);
        Assert.Empty(alerts.Evaluate(Site(2), on));
        Assert.Empty(alerts.Evaluate(Site(0), on));
        Assert.Single(alerts.Evaluate(Site(1), on));

        // The compliance rule turned off by an empty floor has no threshold to see a recovery with, so its breach stays.
        var floor = new AlertEvaluator();
        var deployments = (double pct) => Snapshot(Now, View(LiveView.Deployments, counts: [(LiveMetrics.DeploymentTotal, 9), (LiveMetrics.DeploymentFailed, 0), (LiveMetrics.CompliancePct, pct)]));
        Assert.Empty(floor.Evaluate(deployments(100), on));
        Assert.Single(floor.Evaluate(deployments(50), on));
        Assert.Empty(floor.Evaluate(deployments(100), on with { CompliancePctFloor = null }));
        Assert.Equal([LiveAlertMetrics.Compliance], floor.Breached);
        Assert.Empty(floor.Evaluate(deployments(50), on));
    }

    // ---- Metrics history: damaged and foreign files ----------------------------------------------

    [Fact]
    public void AnAppendAfterALastLineWithoutALineEndStartsANewLineSoNoRefreshIsLost()
    {
        var store = Store();
        store.Append(Row(Now.AddHours(-2)));
        File.AppendAllText(File1, "2026-10-25T13:00:00Z,9,0,5");
        store.Append(Row(Now.AddHours(-1), deployments: 7));
        var (rows, skipped) = store.ReadCounted(30);
        Assert.Equal(2, rows.Count);
        Assert.Equal(7, rows[^1].Values[LiveMetrics.DeploymentTotal]);
        Assert.Equal(1, skipped);
        Assert.EndsWith("\r\n", File.ReadAllText(File1));
    }

    [Theory]
    [InlineData("Timestamp,DeploymentFailed,DeploymentTotal,CompliancePct,ContentIssues,ContentFailedPairs,DPTotal,DPOffline,DPDegraded,ClientHealthy,ClientUnhealthy,ClientInactive,InactiveDevices,SiteOK,SiteWarning,SiteCritical")]
    [InlineData("Timestamp,DeploymentTotal,DeploymentFailed")]
    [InlineData("\"Timestamp\",\"DeploymentTotal\",\"DeploymentFailed\",\"CompliancePct\",\"ContentIssues\",\"ContentFailedPairs\",\"DPTotal\",\"DPOffline\",\"DPDegraded\",\"ClientHealthy\",\"ClientUnhealthy\",\"ClientInactive\",\"InactiveDevices\",\"SiteOK\",\"SiteWarning\",\"SiteCritical\"")]
    public void AnAppendToAFileWithAnotherHeaderWritesNothing(string header)
    {
        File.WriteAllText(File1, header + "\r\n2026-10-25T13:00:00Z,1,2\r\n");
        var before = File.ReadAllBytes(File1);
        Store().Append(Row(Now, deployments: 5));
        Assert.Equal(before, File.ReadAllBytes(File1));
    }

    [Fact]
    public void AHeaderWithATimestampButNoKnownMetricColumnIsRefusedByTheReaderAndExtraColumnsBesideKnownOnesRead()
    {
        File.WriteAllText(File1, "Timestamp,Deployments,Notes\r\n2026-10-25T13:00:00Z,9,x\r\n2026-10-25T13:15:00Z,9,y\r\n");
        var ex = Assert.Throws<InvalidDataException>(() => MetricsHistoryStore.ReadFile(File1, MetricsHistoryStore.MaxReadBytes));
        Assert.Equal(MetricsHistoryStore.NoKnownColumns, ex.Message);
        Assert.Null(MetricsHistoryStore.HeaderMap("Timestamp,Deployments,Notes"));
        Assert.Equal((0, 0), (Store().ReadCounted(30).Rows.Count, Store().ReadCounted(30).Skipped));
        Assert.Empty(Store().Series(LiveMetrics.DeploymentTotal, 30, 100).Points);

        File.WriteAllText(File1, "Timestamp,Notes,DeploymentTotal\r\n2026-10-25T13:00:00Z,x,9\r\n");
        var row = Assert.Single(Store().Read(30));
        Assert.Equal(9, row.Values[LiveMetrics.DeploymentTotal]);
        Assert.Null(row.Values[LiveMetrics.SiteOK]);
    }

    [Fact]
    public void AnAppendToTheOwnHeaderWithAByteOrderMarkAddsTheLine()
    {
        File.WriteAllText(File1, MetricsHistoryStore.HeaderLine + "\r\n", new UTF8Encoding(true));
        var store = Store();
        store.Append(Row(Now, deployments: 5));
        Assert.Equal(5, Assert.Single(store.Read(30)).Values[LiveMetrics.DeploymentTotal]);

        File.WriteAllText(File1, MetricsHistoryStore.HeaderLine);
        store.Append(Row(Now, deployments: 6));
        Assert.Equal(6, Assert.Single(store.Read(30)).Values[LiveMetrics.DeploymentTotal]);
    }

    [Fact]
    public async Task ConcurrentSeriesCallsEachReportTheSkippedLinesOfTheirOwnRead()
    {
        var store = Store();
        var clean = MetricsHistoryStore.HeaderLine + "\r\n" + MetricsHistoryStore.Format(Row(Now.AddHours(-1))) + "\r\n";
        var damaged = clean + MetricsHistoryStore.Format(Row(Now.AddMinutes(-30))) + "\r\n" + string.Concat(Enumerable.Repeat("bad\r\n", 5));
        File.WriteAllText(File1, clean);
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                var temp = File1 + "." + i + ".tmp";
                File.WriteAllText(temp, i % 2 == 0 ? damaged : clean);
                try { File.Move(temp, File1, overwrite: true); }
                catch (IOException) { File.Delete(temp); }
                catch (UnauthorizedAccessException) { File.Delete(temp); }
            }
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var wrong = 0;
            for (var n = 0; n < 300; n++)
            {
                var series = store.Series(LiveMetrics.DeploymentTotal, 30, 100);
                if (series.SourcePoints == 0) continue;
                if ((series.SourcePoints, series.SkippedLines) is not ((1, 0) or (2, 5))) wrong++;
            }
            return wrong;
        })).ToArray();
        var counts = await Task.WhenAll(readers);
        stop.Cancel();
        await writer;
        Assert.Equal(0, counts.Sum());
    }

    [Fact]
    public void TheAlertsLogSaysWhenItsLineWasNotWritten()
    {
        var path = Path.Combine(_dir, "alerts.log");
        Directory.CreateDirectory(path);
        Assert.False(new AlertLog(path).Append(new AlertEvent(Now, LiveAlertMetrics.SiteCritical, "x")));
        Assert.True(new AlertLog(Path.Combine(_dir, "ok", "alerts.log")).Append(new AlertEvent(Now, LiveAlertMetrics.SiteCritical, "x")));
    }

    [Fact]
    public void TheAlertsLogWritesOneCleanLinePerAlertAndRollsOverAtItsCap()
    {
        var path = Path.Combine(_dir, "alerts.log");
        var log = new AlertLog(path);
        log.Append(new AlertEvent(Now, LiveAlertMetrics.SiteCritical, "Site health: 2 critical.\r\nInjected\tline"));
        Assert.Equal("2026-10-25T14:29:06Z\tSiteCritical\tSite health: 2 critical.  Injected line", Assert.Single(File.ReadAllLines(path)));

        File.WriteAllText(path, new string('x', (int)AlertLog.MaxBytes));
        log.Append(new AlertEvent(Now, LiveAlertMetrics.Compliance, "next"));
        Assert.Equal("2026-10-25T14:29:06Z\tCompliance\tnext", Assert.Single(File.ReadAllLines(path)));
        Assert.Equal(AlertLog.MaxBytes, new FileInfo(AlertLog.OldPath(path)).Length);
    }
}
