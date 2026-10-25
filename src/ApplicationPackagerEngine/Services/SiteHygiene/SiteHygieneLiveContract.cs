using System.Globalization;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>The status of a Live row or view. Unknown is never shown as Ok.</summary>
public enum LiveStatus { Ok, Warning, Failed, Unknown }

/// <summary>The six refreshed Live views. Trends reads the metrics history and is not refreshed.</summary>
public enum LiveView { Deployments, Content, DistributionPoints, ClientHealth, InactiveDevices, SiteHealth }

/// <summary>One grid row: a stable key for selection, the status and its text label, and the cells in column order.</summary>
public sealed record LiveRow(string Key, LiveStatus Status, string StatusText, IReadOnlyList<string> Cells);

/// <summary>The counts of one view by metric column (<see cref="LiveMetrics"/>). A null value is not known, never 0.</summary>
public sealed record LiveCounts(IReadOnlyDictionary<string, double?> Values)
{
    public static LiveCounts Unknown(LiveView view) =>
        new(LiveMetrics.Of(view).ToDictionary(c => c, _ => (double?)null, StringComparer.Ordinal));

    public double? this[string column] => Values.TryGetValue(column, out var v) ? v : null;
}

/// <summary>
/// The result of one view. A failed or cut view has every count null. A view that was not read
/// (no SQL server set up, or not requested) is <see cref="Skipped"/> with a plain message.
/// </summary>
public sealed record LiveViewResult(LiveView View, IReadOnlyList<string> Columns, IReadOnlyList<LiveRow> Rows, LiveCounts Counts, bool Truncated, bool Failed, string? Message, DateTime ReadUtc)
{
    /// <summary>True when the view was not read at all; its rows are empty and its counts are null.</summary>
    public bool Skipped { get; init; }

    /// <summary>The provider or SQL queries the read of this view sent.</summary>
    public int QueryCount { get; init; }

    /// <summary>The rows the source held before the cap; equal to the row count when not cut.</summary>
    public int SourceRows { get; init; }

    /// <summary>True when the counts are known: read, not cut, not failed, not skipped.</summary>
    public bool Complete => !Failed && !Truncated && !Skipped;
}

/// <summary>The results of one refresh. A view that was not requested is absent.</summary>
public sealed record LiveSnapshot(IReadOnlyDictionary<LiveView, LiveViewResult> Views, DateTime StartedUtc, DateTime EndedUtc)
{
    public int QueryCount => Views.Values.Sum(v => v.QueryCount);

    /// <summary>The counts of every metric column; a view that is absent or not complete gives null values.</summary>
    public MetricRow ToMetricRow() =>
        new(EndedUtc, LiveMetrics.Columns.ToDictionary(c => c, c =>
            Views.TryGetValue(LiveMetrics.ViewOf(c), out var v) && v.Complete ? v.Counts[c] : null, StringComparer.Ordinal));
}

/// <summary>One row of the metrics history: one value per metric column; null is an empty cell.</summary>
public sealed record MetricRow(DateTime TimestampUtc, IReadOnlyDictionary<string, double?> Values);

/// <summary>One metric over a period, oldest first. Empty cells are not points; <see cref="Gaps"/> marks the breaks.</summary>
public sealed record MetricSeries(string Column, IReadOnlyList<(DateTime T, double V)> Points, int SkippedLines, bool Downsampled)
{
    /// <summary>The indexes in <see cref="Points"/> before which the line breaks (an empty cell lies between).</summary>
    public IReadOnlyList<int> Gaps { get; init; } = [];

    /// <summary>The history rows in the period before downsampling.</summary>
    public int SourcePoints { get; init; }
}

/// <summary>
/// The Live settings. <see cref="SqlServer"/> and <see cref="SqlDatabase"/> are the Discover settings
/// (DiscoverySqlServer, DiscoverySqlDatabase); empty values are found from the site definition as Discover finds them.
/// </summary>
public sealed record LiveSettings(string SqlServer, string SqlDatabase, bool TrustSqlCertificate, int RefreshMinutes, int InactiveDays, AlertSettings Alerts)
{
    public const int MinRefreshMinutes = 5;
    public const int MaxRefreshMinutes = 24 * 60;
    public const int DefaultRefreshMinutes = 15;
    public const int DefaultInactiveDays = 14;
    public const int MinInactiveDays = 1;
    public const int MaxInactiveDays = 3650;

    public static LiveSettings Default { get; } = new(
        SqlServer: "", SqlDatabase: "", TrustSqlCertificate: false, RefreshMinutes: DefaultRefreshMinutes, InactiveDays: DefaultInactiveDays, Alerts: AlertSettings.Default);

    public static int ClampRefresh(int minutes) => Math.Clamp(minutes, MinRefreshMinutes, MaxRefreshMinutes);

    public static int ClampInactive(int days) => Math.Clamp(days, MinInactiveDays, MaxInactiveDays);
}

/// <summary>The alert thresholds. A null floor turns the compliance alert off.</summary>
public sealed record AlertSettings(bool Enabled, bool SiteCritical, bool DpCritical, bool ContentFailed, int? CompliancePctFloor)
{
    public const int DefaultCompliancePctFloor = 80;

    public static AlertSettings Default { get; } = new(Enabled: true, SiteCritical: true, DpCritical: true, ContentFailed: true, CompliancePctFloor: DefaultCompliancePctFloor);
}

/// <summary>One alert that fired: the time, the metric key (<see cref="LiveAlertMetrics"/>) and a plain message.</summary>
public sealed record AlertEvent(DateTime TimeUtc, string Metric, string Message);

/// <summary>
/// What a dashboard import will copy. <see cref="Refusal"/> holds a plain message when nothing can be
/// imported; then the other values are empty.
/// </summary>
public sealed record LegacyImportPlan(string SourceFolder, IReadOnlyList<string> SettingKeys, int HistoryRows, bool HistoryWillBeCopied, string? Refusal)
{
    /// <summary>The history file the import reads; empty when none.</summary>
    public string HistoryFile { get; init; } = "";

    /// <summary>The settings file the import reads; empty when none.</summary>
    public string SettingsFile { get; init; } = "";

    /// <summary>History lines the tolerant reader skipped.</summary>
    public int SkippedLines { get; init; }

    /// <summary>The values the import will fill, by key; each key is in <see cref="SettingKeys"/>.</summary>
    public LiveSettingValues Values { get; init; } = LiveSettingValues.Empty;

    /// <summary>True when the plan copies nothing.</summary>
    public bool IsEmpty => Refusal is null && SettingKeys.Count == 0 && !HistoryWillBeCopied;
}

/// <summary>What an import changed. A second run of the same plan changes nothing.</summary>
public sealed record LegacyImportResult(IReadOnlyList<string> SettingKeys, int HistoryRows, bool HistoryCopied, string Message);

/// <summary>
/// The stored Live settings as the settings file holds them; null is a value that was never set.
/// The import fills only null values.
/// </summary>
public sealed record LiveSettingValues(string? SqlServer, bool? TrustSqlCertificate, int? RefreshMinutes, int? InactiveDays, bool? AlertsEnabled, int? CompliancePctFloor)
{
    public static LiveSettingValues Empty { get; } = new(SqlServer: null, TrustSqlCertificate: null, RefreshMinutes: null, InactiveDays: null, AlertsEnabled: null, CompliancePctFloor: null);
}

/// <summary>The keys of <see cref="LiveSettingValues"/> as the import plan lists them.</summary>
public static class LiveSettingKeys
{
    public const string SqlServer = "SqlServer";
    public const string TrustSqlCertificate = "TrustSqlCertificate";
    public const string RefreshMinutes = "RefreshMinutes";
    public const string InactiveDays = "InactiveDays";
    public const string AlertsEnabled = "AlertsEnabled";
    public const string CompliancePctFloor = "CompliancePctFloor";
}

/// <summary>One Live grid as an export: the rows shown, in column order, with the status label as the first column.</summary>
public sealed record LiveReport(string View, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, bool Truncated, string? Note)
{
    public string Filter { get; init; } = "";

    /// <summary>The time of the read the rows come from; null for a Trends series.</summary>
    public DateTime? ReadUtc { get; init; }
}

public interface ISiteHygieneLiveService
{
    /// <summary>
    /// Reads every requested view. A failed source marks only its view failed. Never throws for a site
    /// or SQL failure; throws <see cref="OperationCanceledException"/> on <paramref name="ct"/>.
    /// </summary>
    Task<LiveSnapshot> RefreshAsync(LiveSettings settings, IReadOnlySet<LiveView> views, IProgress<string> progress, CancellationToken ct);

    /// <summary>Connects to the site database and runs one SELECT. Returns a plain result message; never throws for a SQL failure.</summary>
    Task<string> TestSqlAsync(LiveSettings settings, CancellationToken ct);
}

public interface IMetricsHistoryStore
{
    /// <summary>Appends one line. A failure to write is logged and swallowed; the refresh result stays valid.</summary>
    void Append(MetricRow row);

    /// <summary>The rows of the last <paramref name="days"/> days, oldest first. Corrupt lines are skipped.</summary>
    IReadOnlyList<MetricRow> Read(int days);

    MetricSeries Series(string column, int days, int maxPoints);

    string Path { get; }

    /// <summary>True when the history file exists and is not empty.</summary>
    bool Exists { get; }
}

/// <summary>Keeps breach state between calls. A metric that is not known neither fires nor clears an alert.</summary>
public interface IAlertEvaluator
{
    IReadOnlyList<AlertEvent> Evaluate(LiveSnapshot snapshot, AlertSettings settings);

    /// <summary>The metric keys in breach after the last evaluation.</summary>
    IReadOnlyCollection<string> Breached { get; }
}

public interface IAlertLog
{
    /// <summary>Writes one line. False when the line was not written; the failure is in the app log.</summary>
    bool Append(AlertEvent e);

    string Path { get; }
}

public interface ILegacyDashboardImport
{
    LegacyImportPlan Plan();

    LegacyImportResult Apply(LegacyImportPlan plan);
}

public interface ILiveReportWriter
{
    string WriteCsv(LiveReport report);

    string WriteHtml(LiveReport report);

    string Folder { get; }
}

/// <summary>The metric columns of the metrics history, in file order, and the view each one comes from.</summary>
public static class LiveMetrics
{
    public const string Timestamp = "Timestamp";
    public const string DeploymentTotal = "DeploymentTotal";
    public const string DeploymentFailed = "DeploymentFailed";
    public const string CompliancePct = "CompliancePct";
    public const string ContentIssues = "ContentIssues";
    public const string ContentFailedPairs = "ContentFailedPairs";
    public const string DPTotal = "DPTotal";
    public const string DPOffline = "DPOffline";
    public const string DPDegraded = "DPDegraded";
    public const string ClientHealthy = "ClientHealthy";
    public const string ClientUnhealthy = "ClientUnhealthy";
    public const string ClientInactive = "ClientInactive";
    public const string InactiveDevices = "InactiveDevices";
    public const string SiteOK = "SiteOK";
    public const string SiteWarning = "SiteWarning";
    public const string SiteCritical = "SiteCritical";

    public static IReadOnlyList<string> Columns { get; } =
    [
        DeploymentTotal, DeploymentFailed, CompliancePct, ContentIssues, ContentFailedPairs, DPTotal, DPOffline, DPDegraded,
        ClientHealthy, ClientUnhealthy, ClientInactive, InactiveDevices, SiteOK, SiteWarning, SiteCritical
    ];

    /// <summary>The header line of the metrics history file.</summary>
    public static IReadOnlyList<string> FileColumns { get; } = [Timestamp, .. Columns];

    public static LiveView ViewOf(string column) => column switch
    {
        DeploymentTotal or DeploymentFailed or CompliancePct => LiveView.Deployments,
        ContentIssues or ContentFailedPairs => LiveView.Content,
        DPTotal or DPOffline or DPDegraded => LiveView.DistributionPoints,
        ClientHealthy or ClientUnhealthy or ClientInactive => LiveView.ClientHealth,
        InactiveDevices => LiveView.InactiveDevices,
        SiteOK or SiteWarning or SiteCritical => LiveView.SiteHealth,
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Not a metric column.")
    };

    public static IReadOnlyList<string> Of(LiveView view) => Columns.Where(c => ViewOf(c) == view).ToList();

    /// <summary>The label of a metric for a chart or a table.</summary>
    public static string Label(string column) => column switch
    {
        DeploymentTotal => "Deployments",
        DeploymentFailed => "Deployments with errors",
        CompliancePct => "Deployment compliance (%)",
        ContentIssues => "Content with issues",
        ContentFailedPairs => "Failed DP-content pairs",
        DPTotal => "Distribution points",
        DPOffline => "Distribution points critical",
        DPDegraded => "Distribution points warning",
        ClientHealthy => "Healthy clients",
        ClientUnhealthy => "Unhealthy clients",
        ClientInactive => "Inactive clients",
        InactiveDevices => "Devices past the inactivity threshold",
        SiteOK => "Site items OK",
        SiteWarning => "Site items warning",
        SiteCritical => "Site items critical",
        _ => column
    };
}

/// <summary>The metric keys of the alerts.</summary>
public static class LiveAlertMetrics
{
    public const string SiteCritical = "SiteCritical";
    public const string DpCritical = "DpCritical";
    public const string ContentFailed = "ContentFailed";
    public const string Compliance = "Compliance";

    public static IReadOnlyList<string> All { get; } = [SiteCritical, DpCritical, ContentFailed, Compliance];
}

/// <summary>The view titles and the plain-text summary of a refresh for Copy summary.</summary>
public static class LiveText
{
    public const string SqlNotSetUp = "SQL is not set up. Enter the SQL Server of the site database in Options, or set the site code and SMS Provider so the SQL Server can be found.";

    public static string Title(LiveView view) => view switch
    {
        LiveView.Deployments => "Deployments",
        LiveView.Content => "Content",
        LiveView.DistributionPoints => "Distribution Points",
        LiveView.ClientHealth => "Client Health",
        LiveView.InactiveDevices => "Inactive Devices",
        LiveView.SiteHealth => "Site Health",
        _ => view.ToString()
    };

    /// <summary>Every status text a row of <paramref name="view"/> can carry, so a grid can size its status column before the rows arrive.</summary>
    public static IReadOnlyList<string> StatusTexts(LiveView view) => view switch
    {
        LiveView.Deployments => ["Errors", "Nothing targeted", "In progress", "Below 95 %", "OK"],
        LiveView.Content => ["Failed", "In progress"],
        LiveView.DistributionPoints => ["OK", "Warning", "Critical", "Unknown", "No status"],
        LiveView.ClientHealth => ["Unhealthy", "Inactive", "Healthy", "Not evaluated"],
        LiveView.InactiveDevices => ["No contact on record", "Over 60 days", "Past the threshold"],
        LiveView.SiteHealth => ["OK", "Warning", "Critical", "Unknown"],
        _ => []
    };

    public static string StatusLabel(LiveStatus status) => status switch
    {
        LiveStatus.Ok => "OK",
        LiveStatus.Warning => "Warning",
        LiveStatus.Failed => "Failed",
        _ => "Unknown"
    };

    /// <summary>A count as text; "not known" for null.</summary>
    public static string Count(double? value) =>
        value is { } v ? v.ToString(v == Math.Floor(v) ? "N0" : "N1", CultureInfo.InvariantCulture) : "not known";

    /// <summary>One line per view in a fixed order; a view that failed, was cut or was skipped says so.</summary>
    public static string Summary(LiveSnapshot snapshot, DateTime localTime)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var text = new StringBuilder();
        text.Append("Site Hygiene Live summary, ").Append(localTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\r\n");
        foreach (var view in Enum.GetValues<LiveView>())
        {
            text.Append(Title(view)).Append(": ");
            if (!snapshot.Views.TryGetValue(view, out var r)) { text.Append("not refreshed.\r\n"); continue; }
            if (r.Skipped) { text.Append("not read. ").Append(r.Message ?? "").Append("\r\n"); continue; }
            if (r.Failed) { text.Append("the read failed. ").Append(r.Message ?? "").Append("\r\n"); continue; }
            var c = r.Counts;
            var line = view switch
            {
                LiveView.Deployments => $"{Count(c[LiveMetrics.DeploymentTotal])} deployments, {Count(c[LiveMetrics.DeploymentFailed])} with errors, compliance {Count(c[LiveMetrics.CompliancePct])} %",
                LiveView.Content => $"{Count(c[LiveMetrics.ContentIssues])} content items with issues, {Count(c[LiveMetrics.ContentFailedPairs])} failed DP-content pairs",
                LiveView.DistributionPoints => $"{Count(c[LiveMetrics.DPTotal])} distribution points, {Count(c[LiveMetrics.DPOffline])} critical, {Count(c[LiveMetrics.DPDegraded])} warning",
                LiveView.ClientHealth => $"{Count(c[LiveMetrics.ClientHealthy])} healthy, {Count(c[LiveMetrics.ClientUnhealthy])} unhealthy, {Count(c[LiveMetrics.ClientInactive])} inactive",
                LiveView.InactiveDevices => $"{Count(c[LiveMetrics.InactiveDevices])} devices past the inactivity threshold",
                LiveView.SiteHealth => $"{Count(c[LiveMetrics.SiteOK])} OK, {Count(c[LiveMetrics.SiteWarning])} warning, {Count(c[LiveMetrics.SiteCritical])} critical",
                _ => ""
            };
            text.Append(line).Append('.');
            if (r.Truncated) text.Append(" The read was cut at its row cap, so the counts are not known.");
            text.Append("\r\n");
        }
        return text.ToString();
    }
}
