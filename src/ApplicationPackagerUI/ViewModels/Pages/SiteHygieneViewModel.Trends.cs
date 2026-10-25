using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>The Trends chart and table over the metrics history, and the in-app alert list with its banner.</summary>
public sealed partial class SiteHygieneViewModel
{
    public const int DefaultTrendDays = 30;

    /// <summary>The index of the metric the chart shows first: the deployment compliance.</summary>
    public const int DefaultTrendMetric = 2;

    public const string TrendHint = "Select a point with the arrow keys or the pointer to read its value.";
    public const string TrendReadFailed = "The metrics history could not be read. The log has the details.";

    private bool _trendsStale = true;
    private int _trendGeneration;
    private Task _trendTask = Task.CompletedTask;

    [ObservableProperty] private int _trendDays = DefaultTrendDays;
    [ObservableProperty] private TrendMetricChoice _selectedTrendMetric = SiteHygieneLiveLabels.Metrics[DefaultTrendMetric];

    /// <summary>The points of the chart and the rows of the table, oldest first; the chart and the table show the same list.</summary>
    [ObservableProperty] private IReadOnlyList<TrendTableRow> _trendRows = [];

    [ObservableProperty] private int _trendSelectedIndex = -1;
    [ObservableProperty] private TrendTableRow? _selectedTrendRow;
    [ObservableProperty] private string _trendReadout = TrendHint;

    /// <summary>The count of points, the downsampling and the skipped lines of the series shown.</summary>
    [ObservableProperty] private string _trendNote = "";

    [ObservableProperty] private bool _isTrendLoading;

    /// <summary>The running read of the series, or a completed task.</summary>
    public Task TrendTask => _trendTask;

    partial void OnTrendDaysChanged(int value)
    {
        SaveLater();
        if (IsTrendsPane && _active) _ = LoadTrendsAsync();
    }

    partial void OnSelectedTrendMetricChanged(TrendMetricChoice value)
    {
        SaveLater();
        if (IsTrendsPane && _active) _ = LoadTrendsAsync();
    }

    partial void OnTrendSelectedIndexChanged(int value)
    {
        SelectedTrendRow = value >= 0 && value < TrendRows.Count ? TrendRows[value] : null;
        TrendReadout = SelectedTrendRow is { } row ? $"{SelectedTrendMetric.Title}: {row.ValueText} on {row.Time}." + (row.AfterGap ? " The value before this point is not known." : "") : TrendHint;
    }

    partial void OnSelectedTrendRowChanged(TrendTableRow? value)
    {
        if (value is not null) TrendSelectedIndex = value.Index;
    }

    /// <summary>Reads the series of the metric and period off the UI thread; a read that a newer read replaced is discarded.</summary>
    public Task LoadTrendsAsync()
    {
        var task = RunTrendsAsync();
        _trendTask = task;
        return task;
    }

    private async Task RunTrendsAsync()
    {
        if (Services.Live is not { } live)
        {
            TrendNote = LiveNotSetUp;
            return;
        }
        var mine = ++_trendGeneration;
        var column = SelectedTrendMetric.Column;
        var days = TrendDays;
        IsTrendLoading = true;
        try
        {
            var series = await Task.Run(() => live.History.Series(column, days, MaxTrendPoints));
            if (mine != _trendGeneration) return;
            var gaps = series.Gaps.ToHashSet();
            var rows = series.Points.Select((p, i) => new TrendTableRow(Index: i, TimeUtc: p.T, Value: p.V, AfterGap: gaps.Contains(i))).ToList();
            _trendsStale = false;
            TrendSelectedIndex = -1;
            TrendRows = rows;
            TrendNote = SeriesNote(series, days);
        }
        catch (Exception ex)
        {
            if (mine != _trendGeneration) return;
            TrendRows = [];
            TrendNote = TrendReadFailed;
            Services.Log.Warning(LogArea, $"The metrics history could not be read ({ex.GetType().Name}).");
        }
        finally
        {
            if (mine == _trendGeneration) IsTrendLoading = false;
        }
    }

    /// <summary>The count of points and of history lines, the downsampling, the gaps and the lines that could not be read.</summary>
    public static string SeriesNote(MetricSeries series, int days)
    {
        var parts = new List<string>();
        if (series.Points.Count == 0) parts.Add($"No value of this metric in the last {SiteHygieneLabels.Count(days, "day")}. Each completed refresh adds one line to the metrics history.");
        else if (series.Downsampled)
            parts.Add($"{SiteHygieneLabels.Count(series.Points.Count, "point")} from {SiteHygieneLabels.Count(Math.Max(series.SourcePoints, series.Points.Count), "history line")}: the chart shows the mean of each time bucket.");
        else parts.Add($"{SiteHygieneLabels.Count(series.Points.Count, "point")} in the last {SiteHygieneLabels.Count(days, "day")}.");
        if (series.Gaps.Count > 0) parts.Add($"The line breaks at {SiteHygieneLabels.Count(series.Gaps.Count, "gap")}, where a refresh did not read the value.");
        if (series.SkippedLines > 0) parts.Add($"{SiteHygieneLabels.Count(series.SkippedLines, "line")} of the history file could not be read and {(series.SkippedLines == 1 ? "was" : "were")} skipped.");
        return string.Join(" ", parts);
    }

    private (LiveReport? Report, string? Problem) TrendReport()
    {
        if (TrendRows.Count == 0) return (null, NothingToExport);
        var title = SelectedTrendMetric.Title;
        return (new LiveReport($"Trends: {title}", ["Time", title, "Note"], TrendRows.Select(r => (IReadOnlyList<string>)[r.Time, r.ValueText, r.Gap]).ToList(), false,
            TrendNote.Length > 0 ? TrendNote : null) { Filter = $"Period: last {SiteHygieneLabels.Count(TrendDays, "day")}." }, null);
    }

    public System.Collections.ObjectModel.ObservableCollection<LiveAlertRow> AlertRows { get; } = [];

    [ObservableProperty] private LiveAlertRow? _selectedAlert;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlertBanner))]
    private int _unseenAlerts;

    public bool HasAlertBanner => UnseenAlerts > 0;

    [ObservableProperty] private string _alertBannerText = "";

    private void AddAlerts(IReadOnlyList<AlertEvent> events)
    {
        if (events.Count == 0) return;
        foreach (var alert in events.OrderBy(e => e.TimeUtc)) AlertRows.Insert(0, new LiveAlertRow(alert));
        while (AlertRows.Count > MaxAlertRows) AlertRows.RemoveAt(AlertRows.Count - 1);
        UnseenAlerts = AlertRows.Count(a => !a.Seen);
        var latest = AlertRows[0];
        AlertBannerText = $"{SiteHygieneLabels.Count(UnseenAlerts, "new alert")}. Latest, {latest.Time}: {latest.Message}";
        if (IsAlertsPane) LiveRowCount = AlertCount();
    }

    private string AlertCount() => $"{SiteHygieneLabels.Count(AlertRows.Count, "alert")}, {Number(UnseenAlerts)} new";

    [RelayCommand]
    private void MarkAlertsSeen()
    {
        foreach (var alert in AlertRows) alert.Seen = true;
        UnseenAlerts = 0;
        AlertBannerText = "";
        if (IsAlertsPane) LiveRowCount = AlertCount();
    }

    [RelayCommand]
    private void ShowAlerts()
    {
        SelectedTabIndex = LiveTab;
        SelectedLiveView = SiteHygieneLiveLabels.Views.Single(v => v.Pane == LivePane.Alerts);
    }

    [RelayCommand]
    private void OpenAlertsLog()
    {
        if (Services.Live is not { } live)
        {
            StatusMessage = LiveNotSetUp;
            return;
        }
        var path = live.AlertLog.Path;
        if (path.Length == 0 || !live.FileExists(path))
        {
            StatusMessage = NoAlertLog;
            return;
        }
        Services.Dialogs.OpenFile(path);
    }
}
