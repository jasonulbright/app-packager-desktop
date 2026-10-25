using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;
using VM = ApplicationPackagerUI.ViewModels.Pages.SiteHygieneViewModel;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>The Live tab without a window, over fake Live services: refresh, cancel, timer, filters, alerts, Trends, import and choices.</summary>
public sealed class SiteHygieneLiveTests
{
    private static async Task WaitUntil(Func<bool> condition, string? what = null)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), what ?? "The condition did not hold in time.");
    }

    private static SiteHygieneLiveFakes Live(SiteHygienePageHarness h) => h.Live!;

    private static LiveViewChoice ViewChoice(LiveView view) => SiteHygieneLiveLabels.Views.Single(v => v.View == view);

    private static LiveViewChoice Pane(LivePane pane) => SiteHygieneLiveLabels.Views.Single(v => v.Pane == pane);

    private static async Task<(SiteHygienePageHarness H, VM Vm)> RefreshedAsync(Action<SiteHygienePageHarness>? setup = null)
    {
        var h = new SiteHygienePageHarness();
        setup?.Invoke(h);
        var vm = await h.OpenAsync();
        await vm.RefreshLiveAsync();
        return (h, vm);
    }

    [Fact]
    public async Task NothingRefreshesAndNoTimerArmsBeforeTheFirstRefreshAll()
    {
        var h = new SiteHygienePageHarness();
        var vm = h.Create();
        Assert.Equal(0, Live(h).Service.RefreshCalls);
        await vm.OnNavigatedToAsync();
        await vm.OnNavigatedFromAsync();
        await vm.OnNavigatedToAsync();
        Assert.Equal(0, Live(h).Service.RefreshCalls);
        Assert.Empty(Live(h).Clock.Waits);
        Assert.False(vm.IsAutoRefreshArmed);
        Assert.Equal(VM.AutoRefreshWaiting, vm.AutoRefreshText);
        Assert.Equal(VM.LiveNotRefreshed, vm.LiveSummaryLine);
        Assert.Empty(Live(h).History.Appended);
        Assert.Equal(VM.LiveViewNotRead, vm.LiveViewMessage);
    }

    [Fact]
    public async Task RefreshAllReadsEveryViewOnceShowsTheRowsAndAppendsOneMetricsLine()
    {
        var (h, vm) = await RefreshedAsync();
        var live = Live(h);
        Assert.Equal(1, live.Service.RefreshCalls);
        Assert.Equal(LiveData.Columns[LiveView.Deployments], vm.LiveColumns);
        Assert.Equal(3, vm.LiveViewRows.Count);
        Assert.Equal("3 rows", vm.LiveRowCount);
        Assert.StartsWith("✓ ", vm.LiveViewRows[0].StatusCell);
        Assert.Contains("Deployments 3 rows", vm.LiveSummaryLine);
        Assert.StartsWith("Last refresh ", vm.LiveSummaryLine);
        var line = Assert.Single(live.History.Appended);
        Assert.Equal(LiveMetrics.Columns, line.Values.Keys);
        Assert.Equal(97.5, line.Values[LiveMetrics.CompliancePct]);
        Assert.Contains("8 queries", vm.StatusMessage);
        Assert.False(vm.IsRefreshing);
        var settings = Assert.Single(live.Service.Settings);
        Assert.Equal((LiveSettings.DefaultRefreshMinutes, LiveSettings.DefaultInactiveDays, true), (settings.RefreshMinutes, settings.InactiveDays, settings.Alerts.Enabled));
    }

    [Fact]
    public async Task ASecondRefreshWhileOneRunsIsRefusedWithAPlainMessage()
    {
        var h = new SiteHygienePageHarness();
        var gate = Live(h).Service.Gate = new TaskCompletionSource();
        var vm = await h.OpenAsync();
        var first = vm.RefreshLiveAsync();
        await WaitUntil(() => Live(h).Service.RefreshCalls == 1);
        var second = vm.RefreshLiveAsync();
        Assert.Same(first, second);
        Assert.Equal(VM.RefreshRunning, vm.StatusMessage);
        await vm.RefreshAllCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromMilliseconds(50)).ContinueWith(_ => { });
        Assert.Equal(1, Live(h).Service.RefreshCalls);
        gate.SetResult();
        await first;
        Assert.Equal(1, Live(h).Service.RefreshCalls);
        Assert.Single(Live(h).History.Appended);
    }

    [Fact]
    public async Task CancelEndsTheRefreshAtOnceAndKeepsTheEarlierResult()
    {
        var (h, vm) = await RefreshedAsync();
        var earlier = vm.LastSnapshot;
        var gate = Live(h).Service.Gate = new TaskCompletionSource();
        Live(h).Service.Next = _ => LiveData.Snapshot(rows: 9);
        var run = vm.RefreshLiveAsync();
        await WaitUntil(() => Live(h).Service.RefreshCalls == 2);
        Assert.True(vm.CancelRefreshCommand.CanExecute(null));
        vm.CancelRefreshCommand.Execute(null);
        await run;
        Assert.False(vm.IsRefreshing);
        Assert.Equal(VM.RefreshCancelled, vm.StatusMessage);
        Assert.Same(earlier, vm.LastSnapshot);
        Assert.Equal(3, vm.LiveViewRows.Count);
        Assert.Single(Live(h).History.Appended);
        gate.SetResult();
    }

    [Fact]
    public async Task AResultThatArrivesAfterCancelIsDiscarded()
    {
        var (h, vm) = await RefreshedAsync();
        var earlier = vm.LastSnapshot;
        var service = Live(h).Service;
        var gate = service.Gate = new TaskCompletionSource();
        service.IgnoreCancel = true;
        service.Next = _ => LiveData.Snapshot(rows: 9);
        var run = vm.RefreshLiveAsync();
        await WaitUntil(() => service.RefreshCalls == 2);
        vm.CancelRefreshCommand.Execute(null);
        await run;
        gate.SetResult();
        await Task.Delay(100);
        Assert.Same(earlier, vm.LastSnapshot);
        Assert.Equal(3, vm.LiveViewRows.Count);
        Assert.Single(Live(h).History.Appended);
        Assert.Equal(VM.RefreshCancelled, vm.StatusMessage);
    }

    [Fact]
    public async Task ACancelAfterTheResultShowsChangesNothingAndTheAlertsOfTheRefreshShow()
    {
        var h = new SiteHygienePageHarness();
        var gate = Live(h).History.AppendGate = new ManualResetEventSlim();
        var vm = await h.OpenAsync();
        var run = vm.RefreshLiveAsync();
        await WaitUntil(() => Live(h).History.Appending.IsSet);
        vm.CancelRefreshCommand.Execute(null);
        gate.Set();
        await run;
        Assert.NotNull(vm.LastSnapshot);
        Assert.NotEmpty(vm.AlertRows);
        Assert.Equal(Live(h).AlertLog.Appended.Count, vm.AlertRows.Count);
        Assert.StartsWith("The refresh ended: 6 views read", vm.StatusMessage);
    }

    [Fact]
    public async Task AFailedViewIsMarkedFailedItsCountsAreEmptyCellsAndTheOtherViewsShow()
    {
        var (h, vm) = await RefreshedAsync(h => h.Live!.Service.Next = _ => LiveData.Snapshot(failed: new HashSet<LiveView> { LiveView.Content }));
        Assert.Contains("5 views read, 1 failed (Content)", vm.StatusMessage);
        Assert.Contains("Content failed", vm.LiveSummaryLine);
        Assert.Equal(3, vm.LiveViewRows.Count);
        vm.SelectedLiveView = ViewChoice(LiveView.Content);
        Assert.Empty(vm.LiveViewRows);
        Assert.StartsWith("The read of this view failed. The SMS Provider did not answer.", vm.LiveViewMessage);
        var line = Assert.Single(Live(h).History.Appended);
        Assert.Null(line.Values[LiveMetrics.ContentIssues]);
        Assert.Null(line.Values[LiveMetrics.ContentFailedPairs]);
        Assert.NotNull(line.Values[LiveMetrics.DeploymentTotal]);
        Assert.True(vm.IsAutoRefreshArmed);
    }

    [Fact]
    public async Task ATruncatedViewSaysItsCountsAreNotKnownAndWritesEmptyCells()
    {
        var (h, vm) = await RefreshedAsync(h => h.Live!.Service.Next = _ => LiveData.Snapshot(rows: 5, truncated: new HashSet<LiveView> { LiveView.Deployments }));
        Assert.Equal("The read was cut at 5 rows of 10, so the counts of this view are not known.", vm.LiveViewMessage);
        Assert.Contains("Deployments cut at 5 rows", vm.LiveSummaryLine);
        var line = Assert.Single(Live(h).History.Appended);
        Assert.Null(line.Values[LiveMetrics.DeploymentTotal]);
        Assert.Null(line.Values[LiveMetrics.CompliancePct]);
        vm.CopyLiveSummaryCommand.Execute(null);
        var text = Assert.Single(h.Dialogs.Clipboard);
        Assert.Contains("not known", text);
        Assert.Contains("cut at its row cap", text);
    }

    [Fact]
    public async Task SqlNotSetUpShowsItsPlainMessageAndTheOtherViewsWork()
    {
        var (h, vm) = await RefreshedAsync(h => h.Live!.Service.Next = _ =>
            LiveData.Snapshot(skipped: new HashSet<LiveView> { LiveView.ClientHealth, LiveView.InactiveDevices }));
        Assert.StartsWith("The refresh ended: 4 views read, 2 not set up, ", vm.StatusMessage);
        vm.SelectedLiveView = ViewChoice(LiveView.ClientHealth);
        Assert.Equal(LiveText.SqlNotSetUp, vm.LiveViewMessage);
        Assert.Empty(vm.LiveViewRows);
        Assert.Contains("Client Health not set up", vm.LiveSummaryLine);
        Assert.Contains("Inactive Devices not set up", vm.LiveSummaryLine);
        vm.SelectedLiveView = ViewChoice(LiveView.SiteHealth);
        Assert.Equal(3, vm.LiveViewRows.Count);
        var line = Assert.Single(Live(h).History.Appended);
        Assert.Null(line.Values[LiveMetrics.ClientHealthy]);
        Assert.Null(line.Values[LiveMetrics.InactiveDevices]);
        Assert.Equal(0, vm.RefreshFailures);
        Assert.True(vm.IsAutoRefreshArmed);
    }

    [Fact]
    public async Task TheTimerArmsAfterTheFirstSuccessPausesStopsOnLeaveAndNeverStartsAfterClose()
    {
        var (h, vm) = await RefreshedAsync();
        var clock = Live(h).Clock;
        await WaitUntil(() => clock.Pending == 1);
        Assert.Equal(TimeSpan.FromMinutes(LiveSettings.DefaultRefreshMinutes), clock.Waits[^1]);
        Assert.Equal("Auto-refresh every 15 minutes.", vm.AutoRefreshText);

        vm.PauseAutoRefreshCommand.Execute(null);
        Assert.False(vm.IsAutoRefreshArmed);
        await WaitUntil(() => clock.Pending == 0);
        Assert.Equal(VM.AutoRefreshPausedText, vm.AutoRefreshText);
        Assert.Equal("Resume auto-refresh", vm.PauseText);
        vm.PauseAutoRefreshCommand.Execute(null);
        await WaitUntil(() => clock.Pending == 1);

        await vm.OnNavigatedFromAsync();
        Assert.False(vm.IsAutoRefreshArmed);
        await WaitUntil(() => clock.Pending == 0);
        Assert.False(clock.Tick());
        Assert.Equal(1, Live(h).Service.RefreshCalls);

        await vm.OnNavigatedToAsync();
        await WaitUntil(() => clock.Pending == 1);
        Assert.True(clock.Tick());
        await WaitUntil(() => Live(h).Service.RefreshCalls == 2 && Live(h).History.Appended.Count == 2);
        await WaitUntil(() => clock.Pending == 1);

        vm.OnAppClosing();
        await WaitUntil(() => clock.Pending == 0);
        await vm.OnNavigatedToAsync();
        Assert.False(vm.IsAutoRefreshArmed);
        Assert.Equal(0, clock.Pending);
        Assert.Equal(2, Live(h).Service.RefreshCalls);
    }

    [Fact]
    public async Task ATickWhileARefreshRunsStartsNoSecondRefresh()
    {
        var (h, vm) = await RefreshedAsync();
        var clock = Live(h).Clock;
        await WaitUntil(() => clock.Pending == 1);
        var gate = Live(h).Service.Gate = new TaskCompletionSource();
        var manual = vm.RefreshLiveAsync();
        await WaitUntil(() => Live(h).Service.RefreshCalls == 2);
        Assert.True(clock.Tick());
        await Task.Delay(100);
        Assert.Equal(2, Live(h).Service.RefreshCalls);
        gate.SetResult();
        await manual;
        await WaitUntil(() => clock.Pending == 1);
        Assert.Equal(2, Live(h).Service.RefreshCalls);
    }

    [Fact]
    public async Task TheTimerWaitsLongerAfterFailuresInARowAndSaysSoUntilARefreshSucceeds()
    {
        var (h, vm) = await RefreshedAsync();
        var clock = Live(h).Clock;
        var service = Live(h).Service;
        service.Failure = new InvalidOperationException("secret text of the failure");
        for (var failures = 1; failures <= 4; failures++)
        {
            await WaitUntil(() => clock.Pending == 1);
            Assert.True(clock.Tick());
            await WaitUntil(() => vm.RefreshFailures == failures, $"failure {failures}");
        }
        await WaitUntil(() => clock.Pending == 1);
        Assert.Equal([15, 15, 30, 60, 120], clock.Waits.Select(w => (int)w.TotalMinutes));
        Assert.Equal("The refresh failed 4 times in a row, so auto-refresh waits 2 hours before the next refresh.", vm.AutoRefreshText);
        Assert.Equal(VM.RefreshFailed, vm.StatusMessage);
        Assert.DoesNotContain("secret", vm.StatusMessage);

        service.Failure = null;
        Assert.True(clock.Tick());
        await WaitUntil(() => vm.RefreshFailures == 0 && clock.Pending == 1);
        Assert.Equal(15, (int)clock.Waits[^1].TotalMinutes);
        Assert.Equal("Auto-refresh every 15 minutes.", vm.AutoRefreshText);
        Assert.Equal(TimeSpan.FromHours(2), VM.AutoRefreshWait(15, 4));
        Assert.Equal(TimeSpan.FromHours(2), VM.AutoRefreshWait(15, 40));
        Assert.Equal(TimeSpan.FromHours(24), VM.AutoRefreshWait(1440, 9));
        Assert.Equal(TimeSpan.FromMinutes(5), VM.AutoRefreshWait(1, 0));
    }

    [Fact]
    public async Task AnIntervalChangeRestartsTheArmedTimerWithTheNewWait()
    {
        var (h, vm) = await RefreshedAsync();
        var clock = Live(h).Clock;
        await WaitUntil(() => clock.Pending == 1);
        vm.RefreshMinutes = 60;
        await WaitUntil(() => clock.Pending == 1 && clock.Waits.Count == 2);
        Assert.Equal(TimeSpan.FromMinutes(60), clock.Waits[^1]);
        Assert.True(await vm.SaveAsync());
        Assert.Equal(60, h.Settings.SiteHygieneLive!.RefreshMinutes);
    }

    [Fact]
    public async Task TheFiltersCountTheRowsAndTheSelectionIsKeptByKeyAcrossFilterSortAndRefresh()
    {
        var (h, vm) = await RefreshedAsync(h => h.Live!.Service.Next = _ => LiveData.Snapshot(rows: 12));
        Assert.Equal("12 rows", vm.LiveRowCount);
        vm.LiveStatusFilter = "Failed";
        Assert.All(vm.LiveViewRows, r => Assert.Equal(LiveStatus.Failed, r.Status));
        Assert.Equal("3 of 12 rows", vm.LiveRowCount);
        vm.LiveStatusFilter = "Not known";
        Assert.All(vm.LiveViewRows, r => Assert.StartsWith("? ", r.StatusCell));
        vm.LiveStatusFilter = SiteHygieneLiveLabels.AllText;
        vm.LiveFilterText = "Contoso Item 1";
        vm.FlushFilters();
        Assert.Equal(["Deployments-1", "Deployments-10", "Deployments-11"], vm.LiveViewRows.Select(r => r.Row.Key));

        vm.LiveFilterText = "";
        vm.FlushFilters();
        var picked = vm.LiveViewRows.Single(r => r.Row.Key == "Deployments-7");
        vm.SelectedLiveRow = picked;
        vm.Sort(VM.LiveSortKey(LiveView.Deployments), SiteHygieneLiveLabels.StatusColumn);
        Assert.Equal("DEPLOYMENTS-7", vm.SelectedLiveRow?.Key);
        Assert.Equal(LiveStatus.Ok, vm.LiveViewRows[0].Status);
        vm.Sort(VM.LiveSortKey(LiveView.Deployments), SiteHygieneLiveLabels.StatusColumn);
        Assert.Equal(LiveStatus.Failed, vm.LiveViewRows[0].Status);
        vm.LiveStatusFilter = "Warning";
        Assert.Null(vm.SelectedLiveRow);
        vm.LiveStatusFilter = SiteHygieneLiveLabels.AllText;
        Assert.Equal("DEPLOYMENTS-7", vm.SelectedLiveRow?.Key);
        await vm.RefreshLiveAsync();
        Assert.Equal("DEPLOYMENTS-7", vm.SelectedLiveRow?.Key);
        Assert.NotSame(picked, vm.SelectedLiveRow);

        vm.SelectedLiveView = ViewChoice(LiveView.Content);
        Assert.Null(vm.SelectedLiveRow);
        vm.SelectedLiveView = ViewChoice(LiveView.Deployments);
        Assert.Equal("DEPLOYMENTS-7", vm.SelectedLiveRow?.Key);
    }

    [Fact]
    public void EveryColumnHeaderOfTheServiceHasAWidthKindAndOnlyNameColumnsTakeTheFreeWidth()
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { "Name", "Collection", "Device", "Server", "Operating system" };
        var headers = Enum.GetValues<LiveView>().SelectMany(LiveAggregator.Columns).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(SiteHygieneLiveLabels.StatusHeader, headers);
        foreach (var header in headers)
        {
            var kind = SiteHygieneLiveLabels.KindOf(header);
            if (names.Contains(header)) Assert.True(kind == LiveColumnKind.Name, $"{header} is {kind}.");
            else
            {
                Assert.True(kind != LiveColumnKind.Name, $"{header} has no width kind, so it takes the free width.");
                Assert.True(LiveData.LongValues.ContainsKey(header), $"{header} has no longest real value for the fit tests.");
            }
        }
        Assert.Empty(SiteHygieneLiveLabels.FixedHeaders.Where(h => !headers.Contains(h)));
        Assert.Empty(LiveData.LongValues.Keys.Where(h => !headers.Contains(h)));
    }

    [Fact]
    public async Task ANumericColumnSortsByItsNumberNotItsText()
    {
        var (h, vm) = await RefreshedAsync(h => h.Live!.Service.Next = _ =>
        {
            var columns = LiveData.Columns[LiveView.DistributionPoints];
            var rows = new[] { "9", "1,200", "80" }.Select((n, i) => new LiveRow($"dp{i}", LiveStatus.Ok, "OK", [$"DP{i}", "MCM", "No", n, "0"])).ToList();
            var dp = new LiveViewResult(LiveView.DistributionPoints, columns, rows, LiveCounts.Unknown(LiveView.DistributionPoints), false, false, null, DateTime.UtcNow);
            var snapshot = LiveData.Snapshot();
            return snapshot with { Views = new Dictionary<LiveView, LiveViewResult>(snapshot.Views) { [LiveView.DistributionPoints] = dp } };
        });
        vm.SelectedLiveView = ViewChoice(LiveView.DistributionPoints);
        vm.Sort(VM.LiveSortKey(LiveView.DistributionPoints), LiveGridRow.CellMember(3));
        Assert.Equal(["9", "80", "1,200"], vm.LiveViewRows.Select(r => r.Cells[3]));
    }

    [Fact]
    public async Task AnAlertFiresOnTheCrossingAgainOnlyAfterARecoveryAndAFailedViewNeitherRaisesNorClearsIt()
    {
        var critical = LiveData.Snapshot(rows: 3);
        LiveSnapshot With(LiveViewResult dp) => critical with { Views = new Dictionary<LiveView, LiveViewResult>(critical.Views) { [LiveView.DistributionPoints] = dp } };
        var ok = With(LiveData.Result(LiveView.DistributionPoints, 2));
        var failed = With(LiveData.Result(LiveView.DistributionPoints, 0, failed: true));
        var script = new Queue<LiveSnapshot>([critical, critical, failed, critical, ok, critical]);
        var (h, vm) = await RefreshedAsync(h => h.Live!.Service.Next = _ => script.Dequeue());
        var log = Live(h).AlertLog;
        var dpAlerts = () => log.Appended.Count(a => a.Metric == LiveAlertMetrics.DpCritical);
        Assert.Equal(1, dpAlerts());
        Assert.True(vm.HasAlertBanner);
        var first = vm.UnseenAlerts;
        Assert.Contains("new alert", vm.AlertBannerText);

        await vm.RefreshLiveAsync();
        await vm.RefreshLiveAsync();
        await vm.RefreshLiveAsync();
        Assert.Equal(1, dpAlerts());
        Assert.Equal(first, vm.UnseenAlerts);

        await vm.RefreshLiveAsync();
        Assert.Equal(1, dpAlerts());
        await vm.RefreshLiveAsync();
        Assert.Equal(2, dpAlerts());
        Assert.Equal(log.Appended.Count, vm.AlertRows.Count);
        Assert.Equal(LiveAlertMetrics.DpCritical, vm.AlertRows[0].Alert.Metric);

        vm.MarkAlertsSeenCommand.Execute(null);
        Assert.False(vm.HasAlertBanner);
        Assert.All(vm.AlertRows, a => Assert.Equal("Seen", a.State));
        vm.ShowAlertsCommand.Execute(null);
        Assert.Equal(VM.LiveTab, vm.SelectedTabIndex);
        Assert.True(vm.IsAlertsPane);
        Assert.Equal($"{vm.AlertRows.Count} alerts, 0 new", vm.LiveRowCount);
    }

    [Fact]
    public async Task AlertsThatAreOffAreNeitherShownNorLogged()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.OpenAsync();
        vm.AlertsEnabled = false;
        await vm.RefreshLiveAsync();
        Assert.Empty(Live(h).AlertLog.Appended);
        Assert.Empty(vm.AlertRows);
        Assert.False(vm.HasAlertBanner);
        Assert.False(Assert.Single(Live(h).Alerts.Settings).Enabled);
    }

    [Fact]
    public async Task AnAlertLogThatCannotBeWrittenStillShowsTheAlertAndSaysSo()
    {
        var (h, vm) = await RefreshedAsync(h => h.Live!.AlertLog.Fails = true);
        Assert.NotEmpty(vm.AlertRows);
        Assert.EndsWith(VM.AlertLogFailed, vm.StatusMessage);
    }

    [Fact]
    public async Task AMetricsLineThatCannotBeWrittenKeepsTheResultAndSaysSo()
    {
        var (h, vm) = await RefreshedAsync(h => h.Live!.History.AppendFails = true);
        Assert.Equal(3, vm.LiveViewRows.Count);
        Assert.Contains(VM.HistoryWriteFailed.Trim(), vm.StatusMessage);
    }

    [Fact]
    public async Task TheAlertsLogOpensWhenItExistsAndOtherwiseSaysNoAlertWasLogged()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.OpenAsync();
        vm.OpenAlertsLogCommand.Execute(null);
        Assert.Equal(VM.NoAlertLog, vm.StatusMessage);
        Assert.Empty(h.Dialogs.OpenedFiles);
        Live(h).Files.Add(Live(h).AlertLog.Path);
        vm.OpenAlertsLogCommand.Execute(null);
        Assert.Equal([Live(h).AlertLog.Path], h.Dialogs.OpenedFiles);
    }

    [Fact]
    public async Task TrendsReadTheSeriesOfTheMetricAndPeriodAndBreakTheLineAtAGap()
    {
        var h = new SiteHygienePageHarness();
        Live(h).History.SeriesOf = (column, days, max) => FakeMetricsHistory.Points(column, 6, gaps: [3], downsampled: true, skipped: 2);
        var vm = await h.OpenAsync();
        vm.SelectedTabIndex = VM.LiveTab;
        vm.SelectedLiveView = Pane(LivePane.Trends);
        await vm.TrendTask;
        Assert.Equal((LiveMetrics.CompliancePct, VM.DefaultTrendDays, VM.MaxTrendPoints), Live(h).History.SeriesCalls[^1]);
        Assert.Equal(6, vm.TrendRows.Count);
        Assert.Equal([false, false, false, true, false, false], vm.TrendRows.Select(r => r.AfterGap));
        Assert.Equal(TrendTableRow.GapText, vm.TrendRows[3].Gap);
        Assert.Contains("6 points from 60 history lines: the chart shows the mean of each time bucket.", vm.TrendNote);
        Assert.Contains("The line breaks at 1 gap", vm.TrendNote);
        Assert.Contains("2 lines of the history file could not be read", vm.TrendNote);
        Assert.Equal(VM.TrendHint, vm.TrendReadout);

        vm.TrendSelectedIndex = 3;
        Assert.Same(vm.TrendRows[3], vm.SelectedTrendRow);
        Assert.Contains("The value before this point is not known.", vm.TrendReadout);
        vm.SelectedTrendRow = vm.TrendRows[1];
        Assert.Equal(1, vm.TrendSelectedIndex);

        vm.TrendDays = 7;
        vm.SelectedTrendMetric = SiteHygieneLiveLabels.Metrics.Single(m => m.Column == LiveMetrics.DPOffline);
        await vm.TrendTask;
        Assert.Equal((LiveMetrics.DPOffline, 7, VM.MaxTrendPoints), Live(h).History.SeriesCalls[^1]);

        await vm.ExportCsvCommand.ExecuteAsync(null);
        var (format, report) = Assert.Single(Live(h).Reports.Written);
        Assert.Equal("csv", format);
        Assert.Equal(["Time", LiveMetrics.Label(LiveMetrics.DPOffline), "Note"], report.Columns);
        Assert.Equal(6, report.Rows.Count);
        Assert.Equal("Period: last 7 days.", report.Filter);
    }

    [Fact]
    public async Task AnEmptyHistoryShowsWhyTheChartIsEmpty()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.OpenAsync();
        vm.SelectedTabIndex = VM.LiveTab;
        vm.SelectedLiveView = Pane(LivePane.Trends);
        await vm.TrendTask;
        Assert.Empty(vm.TrendRows);
        Assert.StartsWith("No value of this metric in the last 30 days.", vm.TrendNote);
        await vm.ExportHtmlCommand.ExecuteAsync(null);
        Assert.Equal(VM.NothingToExport, vm.StatusMessage);
        await vm.RefreshLiveAsync();
        await vm.TrendTask;
        Assert.Single(vm.TrendRows);
    }

    [Fact]
    public async Task ExportWritesTheLiveRowsTheFiltersShowThroughTheLiveWriter()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.OpenAsync();
        vm.SelectedTabIndex = VM.LiveTab;
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(VM.LiveNotRefreshed, vm.StatusMessage);
        Live(h).Service.Next = _ => LiveData.Snapshot(rows: 8, truncated: new HashSet<LiveView> { LiveView.Deployments });
        await vm.RefreshLiveAsync();
        vm.LiveStatusFilter = "Failed";
        await vm.ExportHtmlCommand.ExecuteAsync(null);
        var (format, report) = Assert.Single(Live(h).Reports.Written);
        Assert.Equal("html", format);
        Assert.Equal("Deployments", report.View);
        Assert.Equal(["Status", .. LiveData.Columns[LiveView.Deployments]], report.Columns);
        Assert.Equal(2, report.Rows.Count);
        Assert.All(report.Rows, r => Assert.StartsWith("✖ ", r[0]));
        Assert.True(report.Truncated);
        Assert.Contains("counts of this view are not known", report.Note);
        Assert.Equal("Status: Failed.", report.Filter);
        Assert.Empty(h.Reports.Written);
        Assert.Contains("site-hygiene-live-1.html", vm.StatusMessage);
    }

    [Fact]
    public async Task TheFirstVisitWithoutHistoryOffersTheImportAndCopiesNothingByItself()
    {
        var h = new SiteHygienePageHarness();
        Live(h).Import.Next = FakeLegacyImport.Found();
        var vm = await h.OpenAsync();
        Assert.True(vm.HasImportOffer);
        Assert.Contains("mecm-health-dashboard", vm.ImportOffer);
        Assert.Empty(Live(h).Import.Applied);
        Assert.Empty(h.Dialogs.Imports);

        vm.DismissImportOfferCommand.Execute(null);
        Assert.False(vm.HasImportOffer);
        Assert.True(await vm.SaveAsync());
        Assert.True(h.Saved!.ImportOfferDismissed);
        var again = new SiteHygienePageHarness { Saved = h.Saved };
        again.Live!.Import.Next = FakeLegacyImport.Found();
        var vm2 = await again.OpenAsync();
        Assert.False(vm2.HasImportOffer);
        Assert.Equal(0, again.Live.Import.Plans);
    }

    [Fact]
    public async Task NoOfferShowsWhenTheHistoryExistsOrTheFolderIsRefused()
    {
        var h = new SiteHygienePageHarness();
        Live(h).History.Exists = true;
        Live(h).Import.Next = FakeLegacyImport.Found();
        var vm = await h.OpenAsync();
        Assert.False(vm.HasImportOffer);
        Assert.Equal(0, Live(h).Import.Plans);

        var refused = new SiteHygienePageHarness();
        refused.Live!.Import.Next = new LegacyImportPlan("C:\\old", [], 0, false, "The history file is larger than 16 MB, so it is not imported.");
        var vm2 = await refused.OpenAsync();
        Assert.False(vm2.HasImportOffer);
        await vm2.ImportDashboardCommand.ExecuteAsync(null);
        Assert.Equal("The history file is larger than 16 MB, so it is not imported.", vm2.StatusMessage);
        Assert.Empty(refused.Dialogs.Imports);
        Assert.Empty(refused.Live.Import.Applied);
    }

    [Fact]
    public async Task TheImportShowsItsPlanFillsOnlyValuesThatWereNeverSetAndASecondRunChangesNothing()
    {
        var h = new SiteHygienePageHarness();
        h.Settings.SiteHygieneLive = new SiteHygieneLiveSettings { InactiveDays = 45 };
        Live(h).Import.Next = FakeLegacyImport.Found();
        var vm = await h.OpenAsync();
        Assert.True(vm.HasImportOffer);

        h.Dialogs.ConfirmImport = false;
        await vm.ImportDashboardCommand.ExecuteAsync(null);
        Assert.Equal(VM.NotConfirmed, vm.StatusMessage);
        Assert.Empty(Live(h).Import.Applied);
        Assert.Equal("", vm.LiveSqlServer);

        // A value chosen on the page before the import is written first, so the plan keeps it.
        vm.RefreshMinutes = 60;
        h.Dialogs.ConfirmImport = true;
        await vm.ImportDashboardCommand.ExecuteAsync(null);
        var plan = h.Dialogs.Imports[^1].Plan;
        Assert.Contains("Metrics history: 42 rows from ", plan);
        Assert.Contains("Setting SQL server: CM01.contoso.com", plan);
        Assert.Contains("Setting Alerts: Off", plan);
        Assert.DoesNotContain("Inactivity threshold", plan);
        Assert.DoesNotContain("Refresh interval", plan);
        Assert.Single(Live(h).Import.Applied);
        Assert.Equal(("CM01.contoso.com", true, 60, 45, false, 85, true), (vm.LiveSqlServer, vm.TrustSqlCertificate, vm.RefreshMinutes, vm.InactiveDays, vm.AlertsEnabled, vm.CompliancePctFloor, vm.AlertCompliance));
        Assert.Contains("Copied 42 history rows.", vm.StatusMessage);
        Assert.Contains("Settings filled: SQL server, Trust the SQL server certificate, Alerts, Compliance floor.", vm.StatusMessage);
        Assert.False(vm.HasImportOffer);
        var stored = h.Settings.SiteHygieneLive!;
        Assert.Equal(("CM01.contoso.com", 60, 45, true, false, 85), (h.Settings.DiscoverySqlServer, stored.RefreshMinutes, stored.InactiveDays, stored.TrustSqlCertificate, stored.AlertsEnabled, stored.CompliancePctFloor));
        Assert.True(h.Saved!.ImportOfferDismissed);

        var saves = Live(h).LiveSaves.Count;
        var dialogs = h.Dialogs.Imports.Count;
        await vm.ImportDashboardCommand.ExecuteAsync(null);
        Assert.Equal(VM.ImportNothing, vm.StatusMessage);
        Assert.Equal(dialogs, h.Dialogs.Imports.Count);
        Assert.Single(Live(h).Import.Applied);
        Assert.Equal(saves, Live(h).LiveSaves.Count);
    }

    [Fact]
    public async Task AnImportDoesNotStartWhileThePageValuesCannotBeSavedSoNoReloadDropsThem()
    {
        var h = new SiteHygienePageHarness();
        Live(h).Import.Next = FakeLegacyImport.Found();
        var vm = await h.OpenAsync();
        Live(h).LiveSaveFails = true;
        vm.RefreshMinutes = 60;
        var plans = Live(h).Import.Plans;
        await vm.ImportDashboardCommand.ExecuteAsync(null);
        Assert.Equal(VM.ImportNotSaved, vm.StatusMessage);
        Assert.Equal(plans, Live(h).Import.Plans);
        Assert.Empty(h.Dialogs.Imports);
        Assert.Empty(Live(h).Import.Applied);
        Assert.Equal(60, vm.RefreshMinutes);

        Live(h).LiveSaveFails = false;
        await vm.ImportDashboardCommand.ExecuteAsync(null);
        Assert.Single(Live(h).Import.Applied);
        Assert.Equal((60, (int?)60), (vm.RefreshMinutes, h.Settings.SiteHygieneLive!.RefreshMinutes));
    }

    [Fact]
    public async Task TheLiveChoicesRoundTripAndValuesNeverSetStayNull()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.OpenAsync();
        vm.SelectedTabIndex = VM.LiveTab;
        vm.SelectedLiveView = ViewChoice(LiveView.SiteHealth);
        vm.LiveStatusFilter = "Warning";
        vm.LiveFilterText = "SMS_";
        vm.FlushFilters();
        vm.InactiveDays = 90;
        vm.AlertDpCritical = false;
        vm.TrendDays = 180;
        vm.LiveSqlServer = "sql01.contoso.com";
        vm.LiveSqlDatabase = "CM_MCM";
        Assert.True(await vm.SaveAsync());
        var saved = h.Saved!;
        Assert.Equal((SiteHygieneSettings.LiveTabLayout, VM.LiveTab, "SiteHealth", "Warning", "SMS_", 180), (saved.TabLayout, saved.SelectedTab, saved.LiveView, saved.LiveStatus, saved.LiveText, saved.TrendDays));
        var (live, server, database) = Live(h).LiveSaves[^1];
        Assert.Equal(("sql01.contoso.com", "CM_MCM"), (server, database));
        Assert.Equal((90, (int?)null, (bool?)null), (live.InactiveDays, live.RefreshMinutes, live.TrustSqlCertificate));
        Assert.Equal(((bool?)false, (bool?)null), (live.AlertDpCritical, live.AlertsEnabled));
        Assert.Same(live, h.Settings.SiteHygieneLive);

        var again = new SiteHygienePageHarness
        {
            Saved = saved,
            Settings = new AppSettings { SiteCode = "MCM", ProviderMachine = "cm01.contoso.com", DiscoverySqlServer = "sql01.contoso.com", DiscoverySqlDatabase = "CM_MCM", SiteHygieneLive = live }
        };
        var vm2 = await again.OpenAsync();
        Assert.Equal((VM.LiveTab, LiveView.SiteHealth, "Warning", "SMS_", 90, false, 180), (vm2.SelectedTabIndex, vm2.SelectedLiveView.View, vm2.LiveStatusFilter, vm2.LiveFilterText, vm2.InactiveDays, vm2.AlertDpCritical, vm2.TrendDays));
        Assert.Equal(("sql01.contoso.com", "CM_MCM"), (vm2.LiveSqlServer, vm2.LiveSqlDatabase));
        Assert.True(await vm2.SaveAsync());
        Assert.Empty(again.Live!.LiveSaves);
    }

    [Fact]
    public async Task ASettingsFileWithoutLiveOptionsOpensWithTheDefaultsAndWritesNothing()
    {
        var h = new SiteHygienePageHarness { Saved = new SiteHygieneSettings { SelectedTab = 2 } };
        Assert.Null(h.Settings.SiteHygieneLive);
        var vm = await h.OpenAsync();
        Assert.Equal(LiveSettings.Default, vm.CurrentLiveSettings());
        Assert.True(await vm.SaveAsync());
        Assert.Empty(Live(h).LiveSaves);
        Assert.Null(h.Settings.SiteHygieneLive);
    }

    [Theory]
    [InlineData(0, null, VM.FindingsTab)]
    [InlineData(1, null, VM.RelationsTab)]
    [InlineData(2, null, VM.SummaryTab)]
    [InlineData(3, null, VM.OptionsTab)]
    [InlineData(4, null, VM.HistoryTab)]
    [InlineData(9, null, VM.HistoryTab)]
    [InlineData(-1, null, VM.FindingsTab)]
    [InlineData(2, SiteHygieneSettings.LiveTabLayout, VM.LiveTab)]
    [InlineData(3, SiteHygieneSettings.LiveTabLayout, VM.SummaryTab)]
    [InlineData(5, SiteHygieneSettings.LiveTabLayout, VM.OptionsTab)]
    [InlineData(9, SiteHygieneSettings.LiveTabLayout, VM.OptionsTab)]
    public async Task ASavedTabIndexOfAnOlderFileOpensTheSameTabAsBefore(int saved, int? layout, int tab)
    {
        var h = new SiteHygienePageHarness { Saved = new SiteHygieneSettings { SelectedTab = saved, TabLayout = layout } };
        var vm = await h.OpenAsync();
        Assert.Equal(tab, vm.SelectedTabIndex);
    }

    [Fact]
    public async Task AStoredValueOutOfRangeIsClampedToTheNearestChoice()
    {
        var h = new SiteHygienePageHarness { Saved = new SiteHygieneSettings { LiveView = "Nope", LiveStatus = "Bad", TrendDays = 3 } };
        h.Settings.SiteHygieneLive = new SiteHygieneLiveSettings { RefreshMinutes = 1, InactiveDays = 5000, CompliancePctFloor = 101 };
        var vm = await h.OpenAsync();
        Assert.Equal((5, 365, 99, SiteHygieneLiveLabels.Views[0], SiteHygieneLiveLabels.AllText, VM.DefaultTrendDays), (vm.RefreshMinutes, vm.InactiveDays, vm.CompliancePctFloor, vm.SelectedLiveView, vm.LiveStatusFilter, vm.TrendDays));
    }

    [Fact]
    public async Task TestSqlSavesTheSqlValuesAndShowsThePlainResultOfTheService()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.OpenAsync();
        vm.LiveSqlServer = " cm01.contoso.com ";
        vm.TrustSqlCertificate = true;
        await vm.TestSqlCommand.ExecuteAsync(null);
        Assert.Equal(Live(h).Service.TestResult, vm.SqlTestResult);
        var settings = Live(h).Service.Settings[^1];
        Assert.Equal(("cm01.contoso.com", "", true), (settings.SqlServer, settings.SqlDatabase, settings.TrustSqlCertificate));
        var (live, server, database) = Live(h).LiveSaves[^1];
        Assert.Equal(("cm01.contoso.com", "", (bool?)true), (server, database, live.TrustSqlCertificate));
        Assert.Equal(0, Live(h).Service.RefreshCalls);
    }

    [Fact]
    public async Task ASqlValueThatCannotBeSavedSaysSoAndIsSavedAtTheNextSave()
    {
        var h = new SiteHygienePageHarness();
        var vm = await h.OpenAsync();
        Live(h).LiveSaveFails = true;
        vm.LiveSqlDatabase = "CM_MCM";
        await vm.SaveAsync();
        Assert.Equal(VM.LiveSaveFailed, vm.StatusMessage);
        Live(h).LiveSaveFails = false;
        var saves = Live(h).LiveSaves.Count;
        await vm.SaveAsync();
        Assert.Equal(saves + 1, Live(h).LiveSaves.Count);
        await vm.SaveAsync();
        Assert.Equal(saves + 1, Live(h).LiveSaves.Count);
    }

    [Fact]
    public async Task WithoutTheLiveServicesRefreshAllIsDisabledAndEveryCommandSaysSo()
    {
        var h = new SiteHygienePageHarness { Live = null };
        var vm = await h.OpenAsync();
        Assert.False(vm.HasLive);
        Assert.False(vm.RefreshAllCommand.CanExecute(null));
        Assert.False(vm.TestSqlCommand.CanExecute(null));
        await vm.RefreshLiveAsync();
        Assert.Equal(VM.LiveNotSetUp, vm.StatusMessage);
        Assert.Equal(VM.LiveNotSetUp, vm.LiveViewMessage);
        vm.SelectedTabIndex = VM.LiveTab;
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(VM.LiveNotSetUp, vm.StatusMessage);
    }

    [Fact]
    public async Task ARefreshNeedsTheSiteSettingsAndDiscardsAResultWhenTheSiteChanged()
    {
        var h = new SiteHygienePageHarness { Settings = new AppSettings { SiteCode = "", ProviderMachine = "" } };
        var vm = await h.OpenAsync();
        await vm.RefreshLiveAsync();
        Assert.Equal(0, Live(h).Service.RefreshCalls);
        Assert.Equal(vm.ConnectionMessage, vm.StatusMessage);

        var moved = new SiteHygienePageHarness();
        var gate = moved.Live!.Service.Gate = new TaskCompletionSource();
        var vm2 = await moved.OpenAsync();
        var run = vm2.RefreshLiveAsync();
        await WaitUntil(() => moved.Live.Service.RefreshCalls == 1);
        moved.Settings = new AppSettings { SiteCode = "LAB", ProviderMachine = "cm02.contoso.com" };
        gate.SetResult();
        await run;
        Assert.Equal(VM.LiveSiteChanged, vm2.StatusMessage);
        Assert.Null(vm2.LastSnapshot);
        Assert.Empty(moved.Live.History.Appended);
    }

    [Fact]
    public async Task LeavingThePageDuringARefreshLetsItEndAndArmsNoTimerUntilTheReturn()
    {
        var h = new SiteHygienePageHarness();
        var gate = Live(h).Service.Gate = new TaskCompletionSource();
        var vm = await h.OpenAsync();
        var run = vm.RefreshLiveAsync();
        await WaitUntil(() => Live(h).Service.RefreshCalls == 1);
        await vm.OnNavigatedFromAsync();
        gate.SetResult();
        await run;
        Assert.NotNull(vm.LastSnapshot);
        Assert.False(vm.IsAutoRefreshArmed);
        Assert.Equal(VM.AutoRefreshStopped, vm.AutoRefreshText);
        await vm.OnNavigatedToAsync();
        await WaitUntil(() => vm.IsAutoRefreshArmed && Live(h).Clock.Pending == 1);
    }
}
