using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>What the Live tab reads and writes. The Live views write nothing to the site.</summary>
public sealed record SiteHygieneLiveServices(
    ISiteHygieneLiveService Live,
    IMetricsHistoryStore History,
    IAlertEvaluator Alerts,
    IAlertLog AlertLog,
    ILegacyDashboardImport Import,
    ILiveReportWriter Reports)
{
    /// <summary>The wait of the auto-refresh timer; a test replaces it to control the time.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>
    /// Saves the Live and Alerts options (<see cref="AppSettings.SiteHygieneLive"/>) with the SQL server and database that
    /// the page shares with Discover; a null SQL value keeps the stored value. False when nothing was written.
    /// </summary>
    public Func<SiteHygieneLiveSettings, string?, string?, bool> SaveLive { get; init; } = (_, _, _) => false;

    public Func<string, bool> FileExists { get; init; } = File.Exists;
}

/// <summary>
/// The Live tab: Refresh all reads every view off the UI thread, one refresh at a time. The auto-refresh timer arms only
/// after the first successful refresh of the session, never overlaps a refresh, stops when the operator leaves the page
/// or the app closes, and waits longer after failures in a row. A completed refresh appends one metrics history line and
/// evaluates the alerts. Nothing runs before the first Refresh all.
/// </summary>
public sealed partial class SiteHygieneViewModel
{
    public const string RefreshRunning = "A refresh is running. Wait until it ends, or select Cancel refresh.";
    public const string RefreshCancelling = "Cancelling the refresh.";
    public const string RefreshCancelled = "The refresh was cancelled. The earlier result stays.";
    public const string RefreshStillEnding = "The previous refresh is still ending. Select Refresh all again when it ends.";
    public const string RefreshFailed = "The refresh could not read the site. The earlier result stays. The log has the details.";
    public const string LiveSiteChanged = "The settings changed during the refresh, so its result was discarded. Select Refresh all again.";
    public const string LiveNotSetUp = "The Live views are not available in this build.";
    public const string LiveNotRefreshed = "No Live refresh yet. Select Refresh all to read the site.";
    public const string LiveViewNotRead = "This view was not read yet. Select Refresh all.";
    public const string AutoRefreshWaiting = "Auto-refresh starts after the first successful refresh.";
    public const string AutoRefreshPausedText = "Auto-refresh is paused.";
    public const string AutoRefreshStopped = "Auto-refresh stops while the page is not shown.";
    public const string HistoryWriteFailed = " The metrics history line could not be written. The log has the details.";
    public const string AlertLogFailed = " An alert could not be written to the alerts log. The log has the details.";
    public const string NoAlertLog = "No alert was logged yet.";

    /// <summary>The most alerts the list keeps; older alerts stay in the alerts log.</summary>
    public const int MaxAlertRows = 500;

    /// <summary>The most points the Trends chart draws; a longer series is downsampled by bucket.</summary>
    public const int MaxTrendPoints = 400;

    /// <summary>After this many failed refreshes in a row the timer doubles its wait, up to <see cref="MaxBackoffFactor"/> times the interval.</summary>
    public const int BackoffAfter = 2;

    public const int MaxBackoffFactor = 8;

    public static readonly IReadOnlyList<int> RefreshChoices = [5, 10, 15, 30, 60, 120, 240, 480, 720, 1440];
    public static readonly IReadOnlyList<int> InactiveChoices = [7, 14, 21, 30, 45, 60, 90, 180, 365];
    public static readonly IReadOnlyList<int> FloorChoices = [50, 60, 70, 75, 80, 85, 90, 95, 98, 99];
    public static readonly IReadOnlyList<int> TrendPeriods = [7, 30, 90, 180];

    private static readonly IReadOnlySet<LiveView> AllLiveViews = Enum.GetValues<LiveView>().ToHashSet();

    private readonly Dictionary<LiveView, LiveViewResult> _liveResults = [];
    private readonly Dictionary<LiveView, IReadOnlyList<LiveGridRow>> _liveAll = [];
    private readonly Dictionary<LiveView, string> _liveSelected = [];
    private LiveSnapshot? _lastSnapshot;
    private CancellationTokenSource? _refreshCts;
    private Task? _refreshing;
    private int _refreshGeneration;
    private bool _refreshShown;
    private bool _hadLiveSuccess;
    private int _refreshFailures;
    private CancellationTokenSource? _timerCts;
    private Task _timerTask = Task.CompletedTask;
    private bool _closing;

    public IReadOnlyList<LiveViewChoice> LiveViews => SiteHygieneLiveLabels.Views;
    public IReadOnlyList<string> LiveStatusChoices => SiteHygieneLiveLabels.StatusChoices;
    public IReadOnlyList<int> RefreshIntervals => RefreshChoices;
    public IReadOnlyList<int> InactiveDayChoices => InactiveChoices;
    public IReadOnlyList<int> ComplianceFloors => FloorChoices;
    public IReadOnlyList<int> TrendDayChoices => TrendPeriods;
    public IReadOnlyList<TrendMetricChoice> TrendMetrics => SiteHygieneLiveLabels.Metrics;

    /// <summary>True when the build registers the Live services.</summary>
    public bool HasLive => Services.Live is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGridPane), nameof(IsTrendsPane), nameof(IsNotTrendsPane), nameof(IsAlertsPane), nameof(ShowsLiveViewMessage))]
    private LiveViewChoice _selectedLiveView = SiteHygieneLiveLabels.Views[0];

    public bool IsGridPane => SelectedLiveView.Pane == LivePane.Grid;
    public bool IsTrendsPane => SelectedLiveView.Pane == LivePane.Trends;
    public bool IsNotTrendsPane => !IsTrendsPane;
    public bool IsAlertsPane => SelectedLiveView.Pane == LivePane.Alerts;

    [ObservableProperty] private string _liveStatusFilter = SiteHygieneLiveLabels.AllText;
    [ObservableProperty] private string _liveFilterText = "";

    /// <summary>The column headers of the view shown, after the status column; the view builds the grid columns from them.</summary>
    [ObservableProperty] private IReadOnlyList<string> _liveColumns = [];

    [ObservableProperty] private IReadOnlyList<LiveGridRow> _liveViewRows = [];

    /// <summary>Raised just before <see cref="LiveViewRows"/> gets another list, so the view can keep the scroll position and the focus of the grid.</summary>
    public event EventHandler? LiveRowsReplacing;

    /// <summary>The view whose rows <see cref="LiveViewRows"/> holds; a switch to another view keeps no scroll position.</summary>
    private LiveView? _rowsView;
    [ObservableProperty] private LiveGridRow? _selectedLiveRow;
    [ObservableProperty] private string _liveRowCount = "";

    /// <summary>Why the view shows no or not all rows: not read, failed, not set up, or cut at its row cap; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLiveViewMessage), nameof(ShowsLiveViewMessage))]
    private string _liveViewMessage = "";

    public bool HasLiveViewMessage => LiveViewMessage.Length > 0;

    public bool ShowsLiveViewMessage => HasLiveViewMessage && IsGridPane;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelRefreshCommand))]
    private bool _isRefreshing;

    [ObservableProperty] private string _refreshProgress = "";

    /// <summary>The time of the last refresh and the state of each view.</summary>
    [ObservableProperty] private string _liveSummaryLine = LiveNotRefreshed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseText))]
    private bool _autoRefreshPaused;

    public string PauseText => AutoRefreshPaused ? "Resume auto-refresh" : "Pause auto-refresh";

    [ObservableProperty] private string _autoRefreshText = AutoRefreshWaiting;

    /// <summary>True while the auto-refresh timer waits for its next refresh.</summary>
    public bool IsAutoRefreshArmed => _timerCts is not null;

    /// <summary>The running timer loop, or a completed task.</summary>
    public Task AutoRefreshTask => _timerTask;

    /// <summary>The running refresh, or a completed task.</summary>
    public Task RefreshTask => _refreshing ?? Task.CompletedTask;

    public LiveSnapshot? LastSnapshot => _lastSnapshot;

    /// <summary>The failed refreshes in a row; a successful refresh sets it to zero.</summary>
    public int RefreshFailures => _refreshFailures;

    partial void OnSelectedLiveViewChanged(LiveViewChoice value)
    {
        SaveLater();
        ApplyLiveFilter();
        if (value.Pane == LivePane.Trends && _active) _ = LoadTrendsAsync();
    }

    partial void OnLiveStatusFilterChanged(string value) => Filtered(ApplyLiveFilter);
    partial void OnLiveFilterTextChanged(string value) => Debounce(LiveGridKey, ApplyLiveFilter);

    partial void OnSelectedLiveRowChanged(LiveGridRow? value)
    {
        if (_restoring || SelectedLiveView.View is not { } view) return;
        if (value is null) _liveSelected.Remove(view);
        else _liveSelected[view] = value.Key;
    }

    partial void OnAutoRefreshPausedChanged(bool value)
    {
        if (value) StopTimer();
        else ArmTimer();
        ShowAutoRefresh();
    }

    /// <summary>The key of the Live grid in the text filter delays.</summary>
    public const string LiveGridKey = "live";

    /// <summary>The sort key of a Live view grid.</summary>
    public static string LiveSortKey(LiveView view) => "live-" + view;

    /// <summary>The settings a refresh reads with: the shared SQL server and database and the Live options of the page.</summary>
    public LiveSettings CurrentLiveSettings() => new(SqlServer: (LiveSqlServer ?? "").Trim(), SqlDatabase: (LiveSqlDatabase ?? "").Trim(), TrustSqlCertificate: TrustSqlCertificate,
        RefreshMinutes: LiveSettings.ClampRefresh(RefreshMinutes), InactiveDays: LiveSettings.ClampInactive(InactiveDays), Alerts: CurrentAlertSettings());

    public AlertSettings CurrentAlertSettings() => new(Enabled: AlertsEnabled, SiteCritical: AlertSiteCritical, DpCritical: AlertDpCritical, ContentFailed: AlertContentFailed,
        CompliancePctFloor: AlertCompliance ? CompliancePctFloor : null);

    private bool CanRefreshAll() => HasLive;

    [RelayCommand(CanExecute = nameof(CanRefreshAll))]
    private Task RefreshAll() => RefreshLiveAsync();

    private bool CanCancelRefresh() => IsRefreshing;

    [RelayCommand(CanExecute = nameof(CanCancelRefresh))]
    private void CancelRefresh()
    {
        if (_refreshCts is not { } cts || _refreshShown) return;
        _refreshGeneration++;
        // The wait of the refresh can end inside Cancel and set its own message, so this message comes first.
        StatusMessage = RefreshCancelling;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    [RelayCommand]
    private void PauseAutoRefresh() => AutoRefreshPaused = !AutoRefreshPaused;

    /// <summary>Refreshes every view. A request while a refresh runs is refused with a plain message and returns the running refresh.</summary>
    public Task RefreshLiveAsync()
    {
        if (_refreshing is { IsCompleted: false } running)
        {
            StatusMessage = RefreshRunning;
            return running;
        }
        var task = RunRefreshAsync();
        _refreshing = task.IsCompleted ? null : task;
        return task;
    }

    /// <summary>
    /// The service read that Cancel or leaving the app stopped waiting for while it still ran; no refresh starts until it
    /// ends, so two reads never share the ConfigMgr session.
    /// </summary>
    private Task? _abandoned;

    /// <summary>True while a cancelled read still runs in the background.</summary>
    public bool IsPreviousRefreshEnding => _abandoned is { IsCompleted: false };

    /// <summary>
    /// One refresh, manual or timed. A read is a failure for the backoff of the timer: a refresh that read some view
    /// sets the count to zero, a failed one adds one; a cancelled or refused refresh does not change it.
    /// </summary>
    private async Task<bool?> RunRefreshAsync()
    {
        var outcome = await RefreshOnceAsync();
        if (outcome == true) _refreshFailures = 0;
        else if (outcome == false) _refreshFailures++;
        if (outcome is not null)
        {
            OnPropertyChanged(nameof(RefreshFailures));
            ShowAutoRefresh();
        }
        return outcome;
    }

    /// <summary>The outcome of one refresh: true when a view that ran was read, false on a failure, null when cancelled or refused.</summary>
    private async Task<bool?> RefreshOnceAsync()
    {
        if (Services.Live is not { } live)
        {
            StatusMessage = LiveNotSetUp;
            return null;
        }
        if (IsPreviousRefreshEnding)
        {
            StatusMessage = RefreshStillEnding;
            return null;
        }
        if (ConnectionProblem() is { } problem)
        {
            StatusMessage = problem;
            return false;
        }
        var settings = CurrentLiveSettings();
        var mine = ++_refreshGeneration;
        var site = SiteKey();
        // Not disposed: a service call abandoned at Cancel can still read the token.
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        _refreshShown = false;
        IsRefreshing = true;
        RefreshProgress = "Starting the refresh.";
        var progress = new Progress<string>(text =>
        {
            if (mine == _refreshGeneration && IsRefreshing) RefreshProgress = SecretMask.Apply(text);
        });
        // The grid rows of every view are built in the same background task, so a large result does not hold the UI thread.
        var work = Task.Run(async () =>
        {
            var read = await live.Live.RefreshAsync(settings, AllLiveViews, progress, cts.Token).ConfigureAwait(false);
            return (Snapshot: read, Rows: GridRows(read));
        }, cts.Token);
        try
        {
            // The wait ends on Cancel even when the service is inside a read; the late result is then discarded.
            var (snapshot, rows) = await work.WaitAsync(cts.Token);
            if (cts.IsCancellationRequested || mine != _refreshGeneration)
            {
                StatusMessage = RefreshCancelled;
                return null;
            }
            if (!string.Equals(SiteKey(), site, StringComparison.Ordinal) || CurrentLiveSettings() != settings)
            {
                StatusMessage = LiveSiteChanged;
                return null;
            }
            // From here the result is shown and recorded, so Cancel no longer applies to this refresh.
            _refreshShown = true;
            ShowSnapshot(snapshot, rows);
            var (events, note) = await Task.Run(() => Record(live, snapshot, settings.Alerts));
            AddAlerts(events);
            var failed = snapshot.Views.Values.Where(v => v.Failed).ToList();
            var skipped = snapshot.Views.Values.Count(v => v.Skipped);
            var ran = snapshot.Views.Count - skipped;
            StatusMessage = $"The refresh ended: {SiteHygieneLabels.Count(ran - failed.Count, "view")} read"
                + (failed.Count > 0 ? $", {Number(failed.Count)} failed ({string.Join(", ", failed.Select(v => LiveText.Title(v.View)))})" : "")
                + (skipped > 0 ? $", {Number(skipped)} not set up" : "")
                + $", {SiteHygieneLabels.Count(snapshot.QueryCount, "query", "queries")}." + note;
            Services.Log.Info(LogArea, $"Live refresh: views={snapshot.Views.Count} failed={failed.Count} queries={snapshot.QueryCount} alerts={events.Count}.");
            if (IsTrendsPane && _active) _ = LoadTrendsAsync();
            else _trendsStale = true;
            return ran > 0 && failed.Count < ran;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = RefreshCancelled;
            return null;
        }
        catch (Exception ex)
        {
            if (mine != _refreshGeneration)
            {
                StatusMessage = RefreshCancelled;
                return null;
            }
            StatusMessage = PlainMessage(ex, RefreshFailed);
            Services.Log.Warning(LogArea, $"The Live refresh failed ({FailureName(ex)}).");
            return false;
        }
        finally
        {
            if (!work.IsCompleted)
            {
                _abandoned = work;
                _ = work.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            if (ReferenceEquals(_refreshCts, cts)) _refreshCts = null;
            IsRefreshing = false;
            RefreshProgress = "";
        }
    }

    /// <summary>The grid rows of each view of <paramref name="snapshot"/>; runs off the UI thread.</summary>
    private static Dictionary<LiveView, IReadOnlyList<LiveGridRow>> GridRows(LiveSnapshot snapshot) =>
        snapshot.Views.ToDictionary(v => v.Key, v => (IReadOnlyList<LiveGridRow>)v.Value.Rows.Select(r => new LiveGridRow(r, v.Value.Columns.Count)).ToList());

    private static string Number(int value) => SiteHygieneLabels.Number(value);

    /// <summary>Appends the metrics line and evaluates the alerts off the UI thread; returns the alerts to show and a note on what could not be written.</summary>
    private (IReadOnlyList<AlertEvent> Events, string Note) Record(SiteHygieneLiveServices live, LiveSnapshot snapshot, AlertSettings alerts)
    {
        var note = "";
        try { live.History.Append(snapshot.ToMetricRow()); }
        catch (Exception ex)
        {
            note += HistoryWriteFailed;
            Services.Log.Warning(LogArea, $"The metrics history line could not be written ({ex.GetType().Name}).");
        }
        IReadOnlyList<AlertEvent> events;
        try { events = live.Alerts.Evaluate(snapshot, alerts); }
        catch (Exception ex)
        {
            Services.Log.Warning(LogArea, $"The alerts could not be evaluated ({ex.GetType().Name}).");
            return ([], note);
        }
        if (!alerts.Enabled) return ([], note);
        var logged = true;
        foreach (var alert in events)
        {
            try
            {
                if (!live.AlertLog.Append(alert)) logged = false;
            }
            catch (Exception ex)
            {
                logged = false;
                Services.Log.Warning(LogArea, $"An alert could not be written to the alerts log ({ex.GetType().Name}).");
            }
        }
        return (events, logged ? note : note + AlertLogFailed);
    }

    private void ShowSnapshot(LiveSnapshot snapshot, IReadOnlyDictionary<LiveView, IReadOnlyList<LiveGridRow>> rows)
    {
        foreach (var (view, result) in snapshot.Views)
        {
            _liveResults[view] = result;
            _liveAll[view] = rows[view];
        }
        _lastSnapshot = snapshot;
        var ran = snapshot.Views.Values.Count(v => !v.Skipped);
        if (ran > 0 && snapshot.Views.Values.Any(v => !v.Skipped && !v.Failed))
        {
            _hadLiveSuccess = true;
            ArmTimer();
        }
        ShowAutoRefresh();
        ApplyLiveFilter();
        ShowLiveSummary();
        OnPropertyChanged(nameof(LastSnapshot));
    }

    /// <summary>The time of the last refresh and the state of each view: its row count, cut, failed or not read.</summary>
    private void ShowLiveSummary()
    {
        if (_lastSnapshot is not { } snapshot)
        {
            LiveSummaryLine = LiveNotRefreshed;
            return;
        }
        var parts = Enum.GetValues<LiveView>().Select(view => $"{LiveText.Title(view)} {ViewState(view)}");
        LiveSummaryLine = $"Last refresh {SiteHygieneLabels.Time(snapshot.EndedUtc)}: {string.Join(", ", parts)}.";
    }

    private string ViewState(LiveView view) => !_liveResults.TryGetValue(view, out var r) ? "not read"
        : r.Skipped ? "not set up"
        : r.Failed ? "failed"
        : r.Truncated ? $"cut at {SiteHygieneLabels.Count(r.Rows.Count, "row")}"
        : SiteHygieneLabels.Count(r.Rows.Count, "row");

    private void ApplyLiveFilter()
    {
        if (SelectedLiveView.View is not { } view)
        {
            if (IsAlertsPane) LiveRowCount = AlertCount();
            return;
        }
        var key = LiveSortKey(view);
        if (!_liveResults.TryGetValue(view, out var result))
        {
            SetColumns([]);
            _restoring = true;
            try
            {
                LiveViewRows = [];
                SelectedLiveRow = null;
            }
            finally { _restoring = false; }
            _rowsView = null;
            LiveRowCount = "";
            LiveViewMessage = HasLive ? LiveViewNotRead : LiveNotSetUp;
            return;
        }
        SetColumns(result.Columns);
        var text = LiveFilterText.Trim();
        var status = LiveStatusFilter;
        var all = _liveAll[view];
        var rows = all.Where(r => status == SiteHygieneLiveLabels.AllText || SiteHygieneLiveLabels.StatusWord(r.Status) == status)
            .Where(r => text.Length == 0 || r.Matches(text));
        var list = (_sorts.ContainsKey(key) ? Sorted(key, rows) : rows).ToList();
        // A list with the same rows in the same order keeps the shown list, so the grid keeps its scroll position, selection and focus.
        var current = LiveViewRows;
        var same = current.Count == list.Count && !current.Where((r, i) => !r.SameAs(list[i])).Any();
        var shown = same ? current : list;
        _restoring = true;
        try
        {
            if (!same)
            {
                if (_rowsView == view) LiveRowsReplacing?.Invoke(this, EventArgs.Empty);
                LiveViewRows = list;
            }
            _rowsView = view;
            SelectedLiveRow = _liveSelected.TryGetValue(view, out var selected) ? shown.FirstOrDefault(r => r.Key == selected) : null;
        }
        finally { _restoring = false; }
        LiveRowCount = shown.Count == all.Count ? SiteHygieneLabels.Count(all.Count, "row") : $"{Number(shown.Count)} of {SiteHygieneLabels.Count(all.Count, "row")}";
        LiveViewMessage = ViewMessage(result);
    }

    /// <summary>The plain message of a view that was not set up, failed or was cut; a cut view says that its counts are not known.</summary>
    public static string ViewMessage(LiveViewResult result)
    {
        var message = SecretMask.Apply(result.Message ?? "");
        if (result.Skipped) return message.Length > 0 ? message : "This view was not read.";
        if (result.Failed) return "The read of this view failed. " + (message.Length > 0 ? message : "The log has the details.");
        if (result.Truncated)
            return $"The read was cut at {SiteHygieneLabels.Count(result.Rows.Count, "row")}"
                + (result.SourceRows > result.Rows.Count ? $" of {Number(result.SourceRows)}" : "") + ", so the counts of this view are not known." + (message.Length > 0 ? " " + message : "");
        return message;
    }

    private void SetColumns(IReadOnlyList<string> columns)
    {
        if (!LiveColumns.SequenceEqual(columns, StringComparer.Ordinal)) LiveColumns = columns.ToList();
    }

    [RelayCommand]
    private void CopyLiveSummary()
    {
        if (_lastSnapshot is not { } snapshot)
        {
            StatusMessage = LiveNotRefreshed;
            return;
        }
        Services.Dialogs.SetClipboard(LiveText.Summary(snapshot, snapshot.EndedUtc.ToLocalTime()));
        StatusMessage = "Copied the summary of the last refresh.";
    }

    /// <summary>Arms the timer when a refresh of this session succeeded, the page shows, the timer is not paused and no timer runs.</summary>
    private void ArmTimer()
    {
        if (!_hadLiveSuccess || AutoRefreshPaused || !_active || _closing || _timerCts is not null || Services.Live is not { } live) return;
        var cts = new CancellationTokenSource();
        _timerCts = cts;
        _timerTask = TimerLoopAsync(live, cts);
        OnPropertyChanged(nameof(IsAutoRefreshArmed));
        ShowAutoRefresh();
    }

    private void StopTimer()
    {
        if (_timerCts is not { } cts) return;
        _timerCts = null;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        OnPropertyChanged(nameof(IsAutoRefreshArmed));
        ShowAutoRefresh();
    }

    /// <summary>The wait before the next automatic refresh: the interval, doubled for each failure in a row from <see cref="BackoffAfter"/> on, at most <see cref="MaxBackoffFactor"/> times and 24 hours.</summary>
    public static TimeSpan AutoRefreshWait(int minutes, int failures)
    {
        var factor = failures < BackoffAfter ? 1 : Math.Min(MaxBackoffFactor, 1 << Math.Min(30, failures - BackoffAfter + 1));
        return TimeSpan.FromMinutes(Math.Min(LiveSettings.MaxRefreshMinutes, (double)LiveSettings.ClampRefresh(minutes) * factor));
    }

    private async Task TimerLoopAsync(SiteHygieneLiveServices live, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var wait = AutoRefreshWait(RefreshMinutes, _refreshFailures);
                try { await live.Delay(wait, token); }
                catch (OperationCanceledException) { return; }
                if (token.IsCancellationRequested) return;
                // A refresh that runs at the tick counts as this tick: the timer never starts a second one.
                if (_refreshing is { IsCompleted: false } running)
                {
                    await running;
                    continue;
                }
                var task = RunRefreshAsync();
                _refreshing = task.IsCompleted ? null : task;
                await task;
            }
        }
        finally
        {
            if (ReferenceEquals(_timerCts, cts))
            {
                _timerCts = null;
                OnPropertyChanged(nameof(IsAutoRefreshArmed));
            }
            cts.Dispose();
        }
    }

    private void ShowAutoRefresh()
    {
        if (!_hadLiveSuccess)
        {
            AutoRefreshText = AutoRefreshWaiting;
            return;
        }
        if (AutoRefreshPaused)
        {
            AutoRefreshText = AutoRefreshPausedText;
            return;
        }
        if (!_active)
        {
            AutoRefreshText = AutoRefreshStopped;
            return;
        }
        var wait = AutoRefreshWait(RefreshMinutes, _refreshFailures);
        AutoRefreshText = _refreshFailures >= BackoffAfter
            ? $"The refresh failed {SiteHygieneLabels.Count(_refreshFailures, "time")} in a row, so auto-refresh waits {Minutes(wait)} before the next refresh."
            : $"Auto-refresh every {Minutes(wait)}.";
    }

    private static string Minutes(TimeSpan wait) => wait.TotalMinutes >= 120 && wait.TotalMinutes % 60 == 0
        ? SiteHygieneLabels.Count((int)wait.TotalHours, "hour")
        : SiteHygieneLabels.Count((int)wait.TotalMinutes, "minute");

    /// <summary>Stops the timer and the refresh; nothing starts after this.</summary>
    public void OnAppClosing()
    {
        _closing = true;
        StopTimer();
        if (_refreshCts is { } cts)
        {
            _refreshGeneration++;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>The rows of the Live view shown as an export; null with the reason when the view has nothing.</summary>
    public (LiveReport? Report, string? Problem) LiveReportNow()
    {
        FlushFilters();
        if (IsTrendsPane) return TrendReport();
        if (IsAlertsPane)
            return AlertRows.Count == 0 ? (null, "The alert list is empty.")
                : (new LiveReport("Alerts", ["Time", "Metric", "Message", "State"], AlertRows.Select(r => (IReadOnlyList<string>)[r.Time, r.Metric, r.Message, r.Seen ? "Seen" : "New"]).ToList(),
                    false, AlertRows.Count >= MaxAlertRows ? $"The list keeps the newest {SiteHygieneLabels.Count(MaxAlertRows, "alert")}. Older alerts are in the alerts log." : null), null);
        if (SelectedLiveView.View is not { } view || !_liveResults.TryGetValue(view, out var result)) return (null, LiveNotRefreshed);
        var note = ViewMessage(result);
        return (new LiveReport(LiveText.Title(view), [SiteHygieneLiveLabels.StatusHeader, .. result.Columns],
            LiveViewRows.Select(r => (IReadOnlyList<string>)[r.StatusCell, .. r.Cells.Take(result.Columns.Count)]).ToList(), result.Truncated, note.Length > 0 ? note : null)
        {
            Filter = FilterText((LiveFilterText, "Text"), (LiveStatusFilter == SiteHygieneLiveLabels.AllText ? "" : LiveStatusFilter, "Status")),
            ReadUtc = result.ReadUtc
        }, null);
    }

    private async Task ExportLiveAsync(string format)
    {
        if (Services.Live is not { } live)
        {
            StatusMessage = LiveNotSetUp;
            return;
        }
        var (report, problem) = LiveReportNow();
        if (report is null)
        {
            StatusMessage = problem ?? NothingToExport;
            return;
        }
        if (report.Rows.Count == 0)
        {
            StatusMessage = NothingToExport;
            return;
        }
        string path;
        try { path = await Task.Run(() => format == "CSV" ? live.Reports.WriteCsv(report) : live.Reports.WriteHtml(report)); }
        catch (Exception ex)
        {
            StatusMessage = ReportFailed(format);
            Services.Log.Warning(LogArea, $"The Live {format} report could not be written ({ex.GetType().Name}).");
            return;
        }
        StatusMessage = $"Saved the {format} report to {path}: {SiteHygieneLabels.Count(report.Rows.Count, "row")}." + (report.Filter.Length > 0 ? " Filter: " + report.Filter : " No filter.")
            + (report.Note is { } note ? " " + note : "")
            + NotesNote(format, path);
        Services.Log.Info(LogArea, $"Saved a Live {format} report with {SiteHygieneLabels.Count(report.Rows.Count, "row")}.");
        LastReportFolder = Path.GetDirectoryName(path) is { Length: > 0 } folder ? folder : null;
    }

    private void ApplyLivePreferences(SiteHygieneSettings saved)
    {
        SelectedLiveView = SiteHygieneLiveLabels.Views.FirstOrDefault(v => string.Equals(v.Id, saved.LiveView, StringComparison.Ordinal)) ?? SiteHygieneLiveLabels.Views[0];
        LiveStatusFilter = SiteHygieneLiveLabels.StatusChoices.Contains(saved.LiveStatus ?? "") ? saved.LiveStatus! : SiteHygieneLiveLabels.AllText;
        LiveFilterText = saved.LiveText ?? "";
        TrendDays = TrendPeriods.Contains(saved.TrendDays ?? 0) ? saved.TrendDays!.Value : DefaultTrendDays;
        SelectedTrendMetric = SiteHygieneLiveLabels.Metrics.FirstOrDefault(m => m.Column == saved.TrendMetric) ?? SiteHygieneLiveLabels.Metrics[DefaultTrendMetric];
        ImportOfferDismissed = saved.ImportOfferDismissed;
    }

    private void WriteLivePreferences(SiteHygieneSettings settings)
    {
        settings.LiveView = SelectedLiveView.Id;
        settings.LiveStatus = LiveStatusFilter;
        settings.LiveText = LiveFilterText;
        settings.TrendDays = TrendDays;
        settings.TrendMetric = SelectedTrendMetric.Column;
        settings.ImportOfferDismissed = ImportOfferDismissed;
    }
}
