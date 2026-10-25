using System.Globalization;
using System.Text;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The alert rules of the source: a critical site component or site system, a critical distribution point, a
/// failed DP-content pair, and overall deployment compliance below the floor. An alert fires when its metric
/// crosses into breach on a refresh and fires again only after the metric recovers and breaches again. A
/// metric that is not known (its view failed, was cut, was skipped or was not refreshed) neither fires nor
/// clears its alert. The breach state lives only in this instance: the first known value of each metric sets
/// its starting state and fires nothing, so a breach that was there before the app started raises no alert
/// again at each start. While a rule or the alerts are off nothing fires and a new breach is not recorded, but
/// a recorded breach stays until a known value shows the recovery: turning alerts on again during the same
/// breach fires nothing, and turning them on during a breach that started while they were off fires at the
/// next known value. The first known value sets the starting state also while alerts are off. Thread-safe.
/// </summary>
public sealed class AlertEvaluator : IAlertEvaluator
{
    private readonly Dictionary<string, bool> _breached = new(StringComparer.Ordinal);
    private readonly HashSet<string> _started = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public IReadOnlyCollection<string> Breached
    {
        get { lock (_gate) return _breached.Where(p => p.Value).Select(p => p.Key).ToList(); }
    }

    private static double? Known(LiveSnapshot snapshot, string column) =>
        snapshot.Views.TryGetValue(LiveMetrics.ViewOf(column), out var v) && v.Complete ? v.Counts[column] : null;

    private static string N(double v) => v.ToString(v == Math.Floor(v) ? "N0" : "N1", CultureInfo.InvariantCulture);

    public IReadOnlyList<AlertEvent> Evaluate(LiveSnapshot snapshot, AlertSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        var events = new List<AlertEvent>();
        // Without a floor the compliance rule has no breach test, so its recorded breach stays while the floor is off.
        var rules = new (string Key, bool On, double? Value, Func<double, bool>? Breach, Func<double, string> Message)[]
        {
            (LiveAlertMetrics.SiteCritical, settings.SiteCritical, Known(snapshot, LiveMetrics.SiteCritical), v => v > 0,
                v => $"Site health: {LiveText.Count(v, "component or site system", "components or site systems")} {LiveText.Verb(v, "is", "are")} critical."),
            (LiveAlertMetrics.DpCritical, settings.DpCritical, Known(snapshot, LiveMetrics.DPOffline), v => v > 0,
                v => $"Distribution points: {N(v)} {LiveText.Verb(v, "is", "are")} critical."),
            (LiveAlertMetrics.ContentFailed, settings.ContentFailed, Known(snapshot, LiveMetrics.ContentFailedPairs), v => v > 0,
                v => $"Content: {LiveText.Count(v, "DP-content pair")} failed."),
            (LiveAlertMetrics.Compliance, settings.CompliancePctFloor is not null, Known(snapshot, LiveMetrics.CompliancePct),
                settings.CompliancePctFloor is { } floor ? (Func<double, bool>)(v => v < floor) : null,
                v => $"Deployments: overall compliance {N(v)} % is below the {N(settings.CompliancePctFloor!.Value)} % floor.")
        };
        lock (_gate)
        {
            foreach (var rule in rules)
            {
                if (!settings.Enabled || !rule.On)
                {
                    if (rule.Value is not { } seen) continue;
                    _started.Add(rule.Key);
                    if (rule.Breach is { } off && !off(seen)) _breached.Remove(rule.Key);
                    continue;
                }
                if (rule.Value is not { } value) continue;
                var first = _started.Add(rule.Key);
                var now = rule.Breach!(value);
                var was = _breached.TryGetValue(rule.Key, out var w) && w;
                if (now && !was && !first) events.Add(new AlertEvent(snapshot.EndedUtc, rule.Key, rule.Message(value)));
                _breached[rule.Key] = now;
            }
        }
        return events;
    }
}

/// <summary>
/// The alerts log in &lt;data root&gt;\SiteHygiene\alerts.log: one line per alert, "time (UTC), metric,
/// message", tab separated, control characters replaced. Above <see cref="MaxBytes"/> the file moves to
/// alerts.1.log (replacing the older one) before the next line. A failure to write is logged and returns false.
/// </summary>
public sealed class AlertLog(string? path = null, IAppLog? log = null) : IAlertLog
{
    public const string FileName = "alerts.log";
    public const long MaxBytes = 1024 * 1024;
    public const int MaxMessage = 500;

    private readonly Lock _gate = new();

    public string Path => path ?? System.IO.Path.Combine(HygieneFolders.Root(), FileName);

    public static string OldPath(string current) => System.IO.Path.ChangeExtension(current, ".1.log");

    internal static string Clean(string? text, int max)
    {
        var sb = new StringBuilder();
        foreach (var ch in (text ?? "").Take(max)) sb.Append(char.IsControl(ch) ? ' ' : ch);
        return sb.ToString();
    }

    public static string Line(AlertEvent e) =>
        DateTime.SpecifyKind(e.TimeUtc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "\t" + Clean(e.Metric, 40) + "\t" + Clean(e.Message, MaxMessage);

    public bool Append(AlertEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var file = Path;
        lock (_gate)
        {
            try
            {
                var folder = System.IO.Path.GetDirectoryName(file)!;
                if (DataFolderLinks.HasLink(folder, DataFolderLinks.ContainerOf(folder, path is null)) || (File.Exists(file) && File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)))
                {
                    log?.Warning(SiteHygieneService.LogArea, "The alerts log folder or file is a link; the alert was not logged.");
                    return false;
                }
                Directory.CreateDirectory(folder);
                using var held = LiveFileLock.Acquire(file, MetricsHistoryStore.LockWait);
                if (held is null)
                {
                    log?.Warning(SiteHygieneService.LogArea, "The alerts log is locked by another program; the alert was not logged.");
                    return false;
                }
                if (File.Exists(file) && new FileInfo(file).Length >= MaxBytes) File.Move(file, OldPath(file), overwrite: true);
                using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);
                var bytes = Encoding.UTF8.GetBytes(Line(e) + "\r\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Warning(SiteHygieneService.LogArea, $"The alerts log could not be written ({ex.GetType().Name}).");
                return false;
            }
        }
    }
}
