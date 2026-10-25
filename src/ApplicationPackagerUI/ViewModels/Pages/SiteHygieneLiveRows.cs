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

/// <summary>How a Live column is sized: a name takes the free width, the other kinds have a width that holds their longest value.</summary>
public enum LiveColumnKind
{
    Name,
    Text,
    MediumText,
    LongText,
    Count,
    Time,
    Short
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
    public static LiveColumnKind KindOf(string header) => Kinds.TryGetValue(header.Trim(), out var kind) ? kind : LiveColumnKind.Name;

    /// <summary>The column headers of the Live service (LiveAggregator) that hold values of a known length; Name, Collection, Device, Server and Operating system are names.</summary>
    private static readonly Dictionary<string, LiveColumnKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Type"] = LiveColumnKind.MediumText,
        ["Content type"] = LiveColumnKind.LongText,
        ["Kind"] = LiveColumnKind.LongText,
        ["Purpose"] = LiveColumnKind.Short,
        ["Site"] = LiveColumnKind.Short,
        ["Pull DP"] = LiveColumnKind.Short,
        ["PXE"] = LiveColumnKind.Short,
        ["Targeted"] = LiveColumnKind.Count,
        ["Success"] = LiveColumnKind.Count,
        ["Errors"] = LiveColumnKind.Count,
        ["In progress"] = LiveColumnKind.Count,
        ["Compliance (%)"] = LiveColumnKind.Count,
        ["Distribution points"] = LiveColumnKind.Count,
        ["Installed"] = LiveColumnKind.Count,
        ["Failed"] = LiveColumnKind.Count,
        ["Installed (%)"] = LiveColumnKind.Count,
        ["Days since contact"] = LiveColumnKind.Count,
        ["Package ID"] = LiveColumnKind.Text,
        ["Item"] = LiveColumnKind.Text,
        ["State"] = LiveColumnKind.Text,
        ["Health"] = LiveColumnKind.Text,
        ["Active"] = LiveColumnKind.Short,
        ["Client version"] = LiveColumnKind.Text,
        ["Last started"] = LiveColumnKind.LongText,
        ["Last active"] = LiveColumnKind.Time,
        ["Last DDR"] = LiveColumnKind.Time,
        ["Last policy"] = LiveColumnKind.Time,
        ["Last inventory"] = LiveColumnKind.Time
    };

    /// <summary>The headers <see cref="KindOf"/> sizes as a fixed kind.</summary>
    public static IReadOnlyCollection<string> FixedHeaders => Kinds.Keys;

    /// <summary>The width of a column of a fixed kind, which is also its least width, so a narrow window never cuts its longest value; a name has a star width and only a least width.</summary>
    public static (double Width, double MinWidth) Width(LiveColumnKind kind) => kind switch
    {
        LiveColumnKind.Count => (80, 80),
        LiveColumnKind.Short => (84, 84),
        LiveColumnKind.Text => (124, 124),
        LiveColumnKind.MediumText => (160, 160),
        LiveColumnKind.LongText => (192, 192),
        LiveColumnKind.Time => (136, 136),
        _ => (0, 100)
    };

    /// <summary>The width of the status column: it holds the glyph and the longest status label.</summary>
    public const double StatusWidth = 132;

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
            if (text.Length > 0 && double.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var n)) _numbers[i] = n;
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
