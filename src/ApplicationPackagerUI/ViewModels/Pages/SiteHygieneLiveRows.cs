using System.Globalization;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>What the Live tab shows for a choice of the view selector.</summary>
public enum LivePane
{
    Grid,
    Trends,
    Alerts
}

/// <summary>One entry of the Live view selector: a refreshed view, the Trends chart or the alert list.</summary>
public sealed record LiveViewChoice(string Id, string Title, LiveView? View, LivePane Pane)
{
    public override string ToString() => Title;
}

/// <summary>A metric of the Trends chart.</summary>
public sealed record TrendMetricChoice(string Column, string Title)
{
    public override string ToString() => Title;
}

/// <summary>How a Live column is sized: a name shares the free width and wraps, a value has the width of its widest value on one line.</summary>
public enum LiveColumnKind
{
    Name,
    Value
}

/// <summary>The words, glyphs and column sizes of the Live tab.</summary>
public static class SiteHygieneLiveLabels
{
    public const string StatusHeader = "Status";
    public const string StatusColumn = "Status";
    public const string AllText = SiteHygieneViewModel.AllText;
    public const string NotKnown = "Not known";

    public static readonly IReadOnlyList<LiveViewChoice> Views =
    [
        .. Enum.GetValues<LiveView>().Select(v => new LiveViewChoice(v.ToString(), LiveText.Title(v), v, LivePane.Grid)),
        new LiveViewChoice("Trends", "Trends", null, LivePane.Trends),
        new LiveViewChoice("Alerts", "Alerts", null, LivePane.Alerts)
    ];

    public static readonly IReadOnlyList<string> StatusChoices = [AllText, "OK", "Warning", "Failed", NotKnown];

    public static readonly IReadOnlyList<TrendMetricChoice> Metrics = LiveMetrics.Columns.Select(c => new TrendMetricChoice(c, LiveMetrics.Label(c))).ToList();

    /// <summary>The word of a status; the filter compares with it.</summary>
    public static string StatusWord(LiveStatus status) => status switch
    {
        LiveStatus.Ok => "OK",
        LiveStatus.Warning => "Warning",
        LiveStatus.Failed => "Failed",
        _ => NotKnown
    };

    /// <summary>The glyph helps the eye; the word after it carries the meaning.</summary>
    public static string Glyph(LiveStatus status) => status switch
    {
        LiveStatus.Ok => "✓",
        LiveStatus.Warning => "▲",
        LiveStatus.Failed => "✖",
        _ => "?"
    };

    /// <summary>The status cell: the glyph and the text label of the row, or the status word when the row has no label.</summary>
    public static string StatusCell(LiveStatus status, string? text) => $"{Glyph(status)} {(string.IsNullOrWhiteSpace(text) ? StatusWord(status) : text.Trim())}";

    /// <summary>The sort rank of a status: the worst first in a descending sort.</summary>
    public static int Rank(LiveStatus status) => status switch
    {
        LiveStatus.Failed => 3,
        LiveStatus.Warning => 2,
        LiveStatus.Unknown => 1,
        _ => 0
    };

    /// <summary>The kind of a column by its header; a header that is not listed is a name.</summary>
    public static LiveColumnKind KindOf(string header) => ValueHeaders.Contains(header.Trim()) ? LiveColumnKind.Value : LiveColumnKind.Name;

    /// <summary>The column headers of the Live service (LiveAggregator) that hold values of a bounded length; Name, Collection, Device, Server and Operating system are names.</summary>
    private static readonly HashSet<string> ValueHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Type", "Content type", "Kind", "Purpose", "Site", "Pull DP", "PXE", "Targeted", "Success", "Errors", "In progress", "Compliance (%)",
        "Distribution points", "Installed", "Failed", "Installed (%)", "Days since contact", "Package ID", "Item", "State", "Health", "Active",
        "Client version", "Last started", "Last active", "Last DDR", "Last policy", "Last inventory"
    };

    /// <summary>
    /// A name column whose values are identifiers: component names, server and device names, deployment, collection and
    /// content names with underscores, versions or IDs, and operating system versions. Such a value has no line break point
    /// inside a word, so the column is never narrower than its widest word, and the grid scrolls sideways instead.
    /// Package ID and Client version are value columns and have the width of their widest value already.
    /// </summary>
    public static bool HoldsIdentifiers(LiveView? view, string header) => view is { } v && IdentifierColumns.Contains((v, header.Trim()));

    private static readonly HashSet<(LiveView View, string Header)> IdentifierColumns =
    [
        (LiveView.Deployments, "Name"), (LiveView.Deployments, "Collection"),
        (LiveView.Content, "Name"),
        (LiveView.DistributionPoints, "Name"),
        (LiveView.SiteHealth, "Name"), (LiveView.SiteHealth, "Server"),
        (LiveView.ClientHealth, "Device"),
        (LiveView.InactiveDevices, "Device"), (LiveView.InactiveDevices, "Operating system")
    ];

    /// <summary>The headers <see cref="KindOf"/> sizes as values.</summary>
    public static IReadOnlyCollection<string> FixedHeaders => ValueHeaders;

    public static string Time(DateTime utc) => SiteHygieneLabels.Time(utc);

    public static string Number(int value) => SiteHygieneLabels.Number(value);

    /// <summary>A value of the Trends table: the number, or "Not known" for an empty cell.</summary>
    public static string Value(double value) =>
        double.IsNaN(value) ? NotKnown : value.ToString(value == Math.Floor(value) ? "N0" : "N1", CultureInfo.CurrentCulture);

    public static string AlertMetric(string metric) => metric switch
    {
        LiveAlertMetrics.SiteCritical => "Site component or site system critical",
        LiveAlertMetrics.DpCritical => "Distribution point critical",
        LiveAlertMetrics.ContentFailed => "Failed DP-content pair",
        LiveAlertMetrics.Compliance => "Deployment compliance below the floor",
        _ => metric
    };
}

/// <summary>One row of a Live grid. The cells are padded to the columns of the view, so every column binding has a value.</summary>
public sealed class LiveGridRow
{
    private readonly double?[] _numbers;

    public LiveGridRow(LiveRow row, int columns)
    {
        Row = row;
        Key = row.Key.ToUpperInvariant();
        StatusCell = SiteHygieneLiveLabels.StatusCell(row.Status, row.StatusText);
        var cells = new string[Math.Max(columns, row.Cells.Count)];
        for (var i = 0; i < cells.Length; i++) cells[i] = i < row.Cells.Count ? row.Cells[i] ?? "" : "";
        Cells = cells;
        _numbers = new double?[cells.Length];
        for (var i = 0; i < cells.Length; i++)
        {
            var text = cells[i].Trim().TrimEnd('%').Trim();
            if (text.Length > 0 && double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) _numbers[i] = n;
        }
    }

    public LiveRow Row { get; }
    public string Key { get; }
    public LiveStatus Status => Row.Status;
    public string StatusCell { get; }
    public IReadOnlyList<string> Cells { get; }

    /// <summary>The sort member of cell <paramref name="index"/>; the status column sorts by <see cref="SiteHygieneLiveLabels.StatusColumn"/>.</summary>
    public static string CellMember(int index) => "C" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The value a column sorts by: the status rank, the number of a numeric cell, the text otherwise; a number sorts before text.</summary>
    public IComparable SortKey(string column)
    {
        if (column == SiteHygieneLiveLabels.StatusColumn) return SiteHygieneLiveLabels.Rank(Status);
        if (column.Length > 1 && column[0] == 'C' && int.TryParse(column.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < Cells.Count)
            return _numbers[i] is { } n ? (0, n, "") : (1, 0d, Cells[i].ToUpperInvariant());
        return "";
    }

    /// <summary>True when <paramref name="other"/> shows the same key, status and cells.</summary>
    public bool SameAs(LiveGridRow other) =>
        string.Equals(Key, other.Key, StringComparison.Ordinal) && string.Equals(StatusCell, other.StatusCell, StringComparison.Ordinal)
        && Cells.SequenceEqual(other.Cells, StringComparer.Ordinal);

    public bool Matches(string text) =>
        StatusCell.Contains(text, StringComparison.OrdinalIgnoreCase) || Cells.Any(c => c.Contains(text, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => $"{StatusCell} {string.Join(" ", Cells.Take(2))}";
}

/// <summary>One alert in the alert list, newest first.</summary>
public sealed partial class LiveAlertRow(AlertEvent alert) : ObservableObject
{
    public AlertEvent Alert { get; } = alert;
    public string Time => SiteHygieneLabels.Time(Alert.TimeUtc);
    public string Metric => SiteHygieneLiveLabels.AlertMetric(Alert.Metric);
    public string Message => Alert.Message;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State))]
    private bool _seen;

    public string State => Seen ? "Seen" : "▲ New";

    public override string ToString() => $"{Time}, {Metric}, {Message}";
}

/// <summary>One point of the Trends table, oldest first. A point after an empty cell of the history says so, as the line breaks there.</summary>
public sealed record TrendTableRow(int Index, DateTime TimeUtc, double Value, bool AfterGap)
{
    public const string GapText = "Not known before this point";

    public string Time => SiteHygieneLabels.Time(TimeUtc);
    public string ValueText => SiteHygieneLiveLabels.Value(Value);
    public string Gap => AfterGap ? GapText : "";
}
