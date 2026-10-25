using System.Globalization;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>The columns and rows of each Live view as the page tests use them: the real column headers of the service, and the longest real values of each column.</summary>
internal static class LiveData
{
    public static readonly IReadOnlyDictionary<LiveView, IReadOnlyList<string>> Columns = Enum.GetValues<LiveView>().ToDictionary(v => v, LiveAggregator.Columns);

    /// <summary>The longest values the service writes in each column that is not a name column.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> LongValues = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["Type"] = ["Configuration policy", "Content distribution", "Software update", "Mobile program", "Task sequence", "Other (12)"],
        ["Content type"] = ["Software update package", "OS upgrade package", "Driver package", "Type 257"],
        ["Purpose"] = ["Available", "Required", "Simulate", "Unknown"],
        ["Targeted"] = ["1000000", "100000"],
        ["Success"] = ["1000000"],
        ["Errors"] = ["1000000"],
        ["In progress"] = ["1000000"],
        ["Compliance (%)"] = ["100.0", "99.9"],
        ["Package ID"] = ["MCM00005", "MCM0001C"],
        ["Distribution points"] = ["10000"],
        ["Installed"] = ["10000"],
        ["Failed"] = ["10000"],
        ["Installed (%)"] = ["100.0"],
        ["Site"] = ["MCM"],
        ["Pull DP"] = ["Yes", "No"],
        ["PXE"] = ["Yes", "No"],
        ["Kind"] = ["Windows NT Server", "Windows NT Workstation"],
        ["Item"] = ["Component", "Site system"],
        ["State"] = ["Re-installing", "De-installing", "Installing", "Unknown (99)"],
        ["Last started"] = ["2026-12-28 23:59:59"],
        ["Health"] = ["Not evaluated", "Unhealthy", "Healthy"],
        ["Active"] = ["Inactive", "Unknown", "Active"],
        ["Last active"] = ["2026-12-28 23:59"],
        ["Last DDR"] = ["2026-12-28 23:59"],
        ["Last policy"] = ["2026-12-28 23:59"],
        ["Last inventory"] = ["2026-12-28 23:59"],
        ["Client version"] = ["5.00.9146.1009", "5.00.9141.1011"],
        ["Days since contact"] = ["3650", "146"]
    };

    public const string LongName = "Contoso Line of Business Application for the Finance Department in the North West Region, Building Seven";

    /// <summary>The longest time the page writes in the current culture (alert list, Trends table).</summary>
    public static string LongTime => new DateTime(2026, 12, 28, 23, 59, 59).ToString("g", CultureInfo.CurrentCulture);

    public static string Cell(LiveView view, int column, int row, string? name = null)
    {
        var header = Columns[view][column];
        if (!LongValues.TryGetValue(header, out var values)) return $"{name ?? "Contoso Item"} {row}";
        return values[row % values.Count];
    }

    public static LiveStatus Status(int row) => (LiveStatus)(row % 4);

    public static LiveViewResult Result(LiveView view, int rows, string? name = null, bool failed = false, bool truncated = false, bool skipped = false, string? message = null,
        DateTime? readUtc = null)
    {
        var columns = Columns[view];
        var list = failed || skipped ? [] : Enumerable.Range(0, rows).Select(i => new LiveRow(Key: $"{view}-{i}", Status: Status(i),
            StatusText: LiveText.StatusTexts(view)[i % LiveText.StatusTexts(view).Count], Cells: Enumerable.Range(0, columns.Count).Select(c => Cell(view, c, i, name)).ToList())).ToList();
        var counts = failed || skipped || truncated ? LiveCounts.Unknown(view) : new LiveCounts(LiveMetrics.Of(view).ToDictionary(c => c, c => (double?)CountOf(c, list), StringComparer.Ordinal));
        return new LiveViewResult(View: view, Columns: columns, Rows: list, Counts: counts, Truncated: truncated, Failed: failed, Message: message, ReadUtc: readUtc ?? new DateTime(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc))
        {
            Skipped = skipped,
            QueryCount = skipped ? 0 : view is LiveView.DistributionPoints or LiveView.SiteHealth ? 2 : 1,
            SourceRows = truncated ? rows * 2 : list.Count
        };
    }

    private static double CountOf(string column, IReadOnlyList<LiveRow> rows) => column switch
    {
        LiveMetrics.CompliancePct => 97.5,
        LiveMetrics.DeploymentFailed or LiveMetrics.DPOffline or LiveMetrics.ClientUnhealthy or LiveMetrics.SiteCritical or LiveMetrics.ContentFailedPairs => rows.Count(r => r.Status == LiveStatus.Failed),
        LiveMetrics.DPDegraded or LiveMetrics.SiteWarning => rows.Count(r => r.Status == LiveStatus.Warning),
        _ => rows.Count
    };

    public static LiveSnapshot Snapshot(int rows = 3, IReadOnlySet<LiveView>? failed = null, IReadOnlySet<LiveView>? truncated = null, IReadOnlySet<LiveView>? skipped = null,
        string? name = null, DateTime? endedUtc = null)
    {
        var views = Enum.GetValues<LiveView>().ToDictionary(v => v, v => Result(v, rows, name, failed: failed?.Contains(v) == true, truncated: truncated?.Contains(v) == true,
            skipped: skipped?.Contains(v) == true, message: skipped?.Contains(v) == true ? LiveText.SqlNotSetUp : failed?.Contains(v) == true ? "The SMS Provider did not answer." : null));
        var ended = endedUtc ?? new DateTime(2026, 10, 25, 9, 0, 5, DateTimeKind.Utc);
        return new LiveSnapshot(views, ended.AddSeconds(-5), ended);
    }
}

/// <summary>A Live service that returns a scripted snapshot; it records every call and can wait on a gate.</summary>
internal sealed class FakeLiveService : ISiteHygieneLiveService
{
    public Func<int, LiveSnapshot> Next = _ => LiveData.Snapshot();
    public int RefreshCalls;
    public List<LiveSettings> Settings { get; } = [];
    public TaskCompletionSource? Gate;
    public bool IgnoreCancel;
    public Exception? Failure;
    public string TestResult = "Connected to CM01.contoso.com, database CM_MCM. The read returned 1 row.";
    public int TestCalls;

    public async Task<LiveSnapshot> RefreshAsync(LiveSettings settings, IReadOnlySet<LiveView> views, IProgress<string> progress, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref RefreshCalls);
        lock (Settings) Settings.Add(settings);
        progress.Report("Reading the deployments.");
        if (Gate is { } gate) await (IgnoreCancel ? gate.Task : gate.Task.WaitAsync(ct));
        if (!IgnoreCancel) ct.ThrowIfCancellationRequested();
        if (Failure is { } failure) throw failure;
        return Next(call);
    }

    public Task<string> TestSqlAsync(LiveSettings settings, CancellationToken ct)
    {
        Interlocked.Increment(ref TestCalls);
        lock (Settings) Settings.Add(settings);
        return Task.FromResult(TestResult);
    }
}

internal sealed class FakeMetricsHistory : IMetricsHistoryStore
{
    public List<MetricRow> Appended { get; } = [];
    public bool Exists { get; set; }
    public bool AppendFails;

    /// <summary>Holds Append until it is set; Appending is set when Append starts.</summary>
    public ManualResetEventSlim? AppendGate;
    public readonly ManualResetEventSlim Appending = new();
    public Func<string, int, int, MetricSeries>? SeriesOf;
    public List<(string Column, int Days, int MaxPoints)> SeriesCalls { get; } = [];

    public void Append(MetricRow row)
    {
        Appending.Set();
        AppendGate?.Wait(TimeSpan.FromSeconds(30));
        if (AppendFails) throw new IOException(@"Access to C:\Data\SiteHygiene\metrics-history.csv is denied.");
        lock (Appended) Appended.Add(row);
        Exists = true;
    }

    public IReadOnlyList<MetricRow> Read(int days)
    {
        lock (Appended) return Appended.ToList();
    }

    public MetricSeries Series(string column, int days, int maxPoints)
    {
        lock (SeriesCalls) SeriesCalls.Add((column, days, maxPoints));
        if (SeriesOf is { } of) return of(column, days, maxPoints);
        lock (Appended)
            return new MetricSeries(column, Appended.Where(r => r.Values.TryGetValue(column, out var v) && v is not null).Select(r => (r.TimestampUtc, r.Values[column]!.Value)).ToList(), 0, false)
            {
                SourcePoints = Appended.Count
            };
    }

    public string Path => @"C:\Data\SiteHygiene\metrics-history.csv";

    /// <summary>A series of <paramref name="count"/> points over a day per point, with a gap before each index of <paramref name="gaps"/>.</summary>
    public static MetricSeries Points(string column, int count, IReadOnlyList<int>? gaps = null, bool downsampled = false, int skipped = 0) =>
        new(column, Enumerable.Range(0, count).Select(i => (new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc).AddHours(i * 6), 80 + (i * 7 % 20) + 0.5)).ToList(), skipped, downsampled)
        {
            Gaps = gaps ?? [],
            SourcePoints = downsampled ? count * 10 : count
        };
}

/// <summary>An evaluator with the breach rule of the brief: an alert fires on the crossing into breach and again only after a recovery; a metric that is not known changes nothing.</summary>
internal sealed class FakeAlertEvaluator : IAlertEvaluator
{
    private readonly HashSet<string> _breached = new(StringComparer.Ordinal);
    public int Calls;
    public List<AlertSettings> Settings { get; } = [];

    public IReadOnlyList<AlertEvent> Evaluate(LiveSnapshot snapshot, AlertSettings settings)
    {
        Interlocked.Increment(ref Calls);
        lock (Settings) Settings.Add(settings);
        var events = new List<AlertEvent>();
        void Check(string metric, bool on, LiveView view, Func<LiveCounts, bool?> breach, string message)
        {
            if (!on || !snapshot.Views.TryGetValue(view, out var result) || !result.Complete || breach(result.Counts) is not { } now) return;
            if (now && _breached.Add(metric)) events.Add(new AlertEvent(snapshot.EndedUtc, metric, message));
            else if (!now) _breached.Remove(metric);
        }
        Check(LiveAlertMetrics.SiteCritical, settings.SiteCritical, LiveView.SiteHealth, c => c[LiveMetrics.SiteCritical] is { } v ? v > 0 : null, "A site component or site system is critical.");
        Check(LiveAlertMetrics.DpCritical, settings.DpCritical, LiveView.DistributionPoints, c => c[LiveMetrics.DPOffline] is { } v ? v > 0 : null, "A distribution point is critical.");
        Check(LiveAlertMetrics.ContentFailed, settings.ContentFailed, LiveView.Content, c => c[LiveMetrics.ContentFailedPairs] is { } v ? v > 0 : null, "A DP-content pair failed.");
        Check(LiveAlertMetrics.Compliance, settings.CompliancePctFloor is not null, LiveView.Deployments,
            c => c[LiveMetrics.CompliancePct] is { } v ? v < settings.CompliancePctFloor : null, "Overall deployment compliance is below the floor.");
        return settings.Enabled ? events : [];
    }

    public IReadOnlyCollection<string> Breached => _breached;
}

internal sealed class FakeAlertLog : IAlertLog
{
    public List<AlertEvent> Appended { get; } = [];
    public bool Fails;

    /// <summary>Append returns false, as the real log does when its file cannot be written.</summary>
    public bool NotWritten;

    public bool Append(AlertEvent e)
    {
        if (Fails) throw new IOException("The disk is full.");
        if (NotWritten) return false;
        lock (Appended) Appended.Add(e);
        return true;
    }

    public string Path => @"C:\Data\SiteHygiene\alerts.log";
}

/// <summary>An import with the fill rule of the real one: the plan lists only the values that are empty in the stored settings, and Apply fills only those.</summary>
internal sealed class FakeLegacyImport : ILegacyDashboardImport
{
    public LegacyImportPlan Next = new(SourceFolder: "", SettingKeys: [], HistoryRows: 0, HistoryWillBeCopied: false, Refusal: "No retired Health Dashboard folder was found.");
    public int Plans;
    public List<LegacyImportPlan> Applied { get; } = [];

    /// <summary>The stored values now; null treats every value as empty.</summary>
    public Func<LiveSettingValues>? Read;

    /// <summary>Stores the values Apply fills.</summary>
    public Func<LiveSettingValues, bool>? Fill;

    public LegacyImportPlan Plan()
    {
        Interlocked.Increment(ref Plans);
        if (Next.Refusal is not null) return Next;
        var (values, keys) = LegacyDashboardImport.Fill(Next.Values, Read?.Invoke() ?? LiveSettingValues.Empty);
        return Next with { SettingKeys = keys.Where(Next.SettingKeys.Contains).ToList(), Values = values };
    }

    public LegacyImportResult Apply(LegacyImportPlan plan)
    {
        lock (Applied) Applied.Add(plan);
        var (values, keys) = LegacyDashboardImport.Fill(plan.Values, Read?.Invoke() ?? LiveSettingValues.Empty);
        keys = keys.Where(plan.SettingKeys.Contains).ToList();
        if (keys.Count > 0 && Fill?.Invoke(values) == false) keys = [];
        var result = new LegacyImportResult(keys, plan.HistoryWillBeCopied ? plan.HistoryRows : 0, plan.HistoryWillBeCopied,
            plan.HistoryWillBeCopied ? $"Copied {plan.HistoryRows} history rows." : "Copied no history rows.");
        Next = Next with { HistoryWillBeCopied = false, HistoryRows = 0 };
        return result;
    }

    public static LegacyImportPlan Found(int rows = 42, LiveSettingValues? values = null)
    {
        var v = values ?? new LiveSettingValues(SqlServer: "CM01.contoso.com", TrustSqlCertificate: true, RefreshMinutes: 30, InactiveDays: 21, AlertsEnabled: false, CompliancePctFloor: 85);
        var keys = new List<string>();
        if (v.SqlServer is not null) keys.Add(LiveSettingKeys.SqlServer);
        if (v.TrustSqlCertificate is not null) keys.Add(LiveSettingKeys.TrustSqlCertificate);
        if (v.RefreshMinutes is not null) keys.Add(LiveSettingKeys.RefreshMinutes);
        if (v.InactiveDays is not null) keys.Add(LiveSettingKeys.InactiveDays);
        if (v.AlertsEnabled is not null) keys.Add(LiveSettingKeys.AlertsEnabled);
        if (v.CompliancePctFloor is not null) keys.Add(LiveSettingKeys.CompliancePctFloor);
        return new LegacyImportPlan(SourceFolder: @"C:\Users\labadmin\AppData\Local\AppPackagerSuite\site-hygiene\legacy\mecm-health-dashboard", SettingKeys: keys, HistoryRows: rows,
            HistoryWillBeCopied: rows > 0, Refusal: null)
        {
            HistoryFile = @"C:\Users\labadmin\AppData\Local\AppPackagerSuite\site-hygiene\legacy\mecm-health-dashboard\History\metrics-history.csv",
            SettingsFile = @"C:\Users\labadmin\AppData\Local\AppPackagerSuite\site-hygiene\legacy\mecm-health-dashboard\SiteHygiene.prefs.json",
            Values = v
        };
    }
}

internal sealed class FakeLiveReports : ILiveReportWriter
{
    public List<(string Format, LiveReport Report)> Written { get; } = [];
    public string Folder => @"C:\Data\SiteHygiene\Reports";

    public string WriteCsv(LiveReport report) => Write("csv", report);
    public string WriteHtml(LiveReport report) => Write("html", report);

    private string Write(string format, LiveReport report)
    {
        lock (Written) Written.Add((format, report));
        return $@"C:\Data\SiteHygiene\Reports\site-hygiene-live-{Written.Count}.{format}";
    }
}

/// <summary>The timer waits of the page: each wait is held until the test ticks it.</summary>
internal sealed class FakeLiveClock
{
    private readonly List<(TimeSpan Wait, TaskCompletionSource Done, CancellationToken Token)> _waits = [];

    public List<TimeSpan> Waits { get; } = [];

    public Task Delay(TimeSpan wait, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waits)
        {
            _waits.Add((wait, done, ct));
            Waits.Add(wait);
        }
        return done.Task.WaitAsync(ct);
    }

    /// <summary>The waits that are not ticked and not cancelled.</summary>
    public int Pending
    {
        get
        {
            lock (_waits) return _waits.Count(w => !w.Done.Task.IsCompleted && !w.Token.IsCancellationRequested);
        }
    }

    /// <summary>Ends the oldest wait that is not cancelled; false when none is pending.</summary>
    public bool Tick()
    {
        lock (_waits)
        {
            var wait = _waits.FirstOrDefault(w => !w.Done.Task.IsCompleted && !w.Token.IsCancellationRequested);
            if (wait.Done is null) return false;
            wait.Done.SetResult();
            return true;
        }
    }
}

internal sealed class SiteHygieneLiveFakes
{
    public FakeLiveService Service { get; } = new();
    public FakeMetricsHistory History { get; } = new();
    public FakeAlertEvaluator Alerts { get; } = new();
    public FakeAlertLog AlertLog { get; } = new();
    public FakeLegacyImport Import { get; } = new();
    public FakeLiveReports Reports { get; } = new();
    public FakeLiveClock Clock { get; } = new();
    /// <summary>Every save of the Live options: the stored options and the shared SQL values.</summary>
    public List<(SiteHygieneLiveSettings Live, string? Server, string? Database)> LiveSaves { get; } = [];
    public bool LiveSaveFails;

    /// <summary>Writes a save into the settings the page reads, as the settings file does.</summary>
    public Action<SiteHygieneLiveSettings, string?, string?>? Stored;
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SiteHygieneLiveServices Services() => new(Service, History, Alerts, AlertLog, Import, Reports)
    {
        Delay = Clock.Delay,
        SaveLive = (live, server, database) =>
        {
            lock (LiveSaves) LiveSaves.Add((live, server, database));
            if (LiveSaveFails) return false;
            Stored?.Invoke(live, server, database);
            return true;
        },
        FileExists = Files.Contains
    };
}
