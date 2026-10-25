using System.Globalization;
using System.Text.Json;
using ApplicationPackagerEngine.Services.Logging;
using Microsoft.Win32;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Imports the settings and the metrics history of the suite Site Hygiene tool or a retired Health Dashboard.
/// The source is the first folder with something to copy, in this order for each suite
/// Site Hygiene folder: the folder itself (SiteHygiene.prefs.json, History\metrics-history.csv), its
/// legacy\mecm-health-dashboard folder, and a sibling mecm-health-dashboard folder (MECMHealthDash.prefs.json,
/// History\metrics-history.csv). The old files are only read. A setting is filled only when it has no value;
/// the history is copied only when no history exists. A second run changes nothing. A file that is larger than
/// its cap, is a link, or cannot be read is refused with a plain message and nothing is imported.
/// </summary>
public sealed class LegacyDashboardImport : ILegacyDashboardImport
{
    public const string SuitePrefsFile = "SiteHygiene.prefs.json";
    public const string DashboardPrefsFile = "MECMHealthDash.prefs.json";
    public const string HistoryRelative = @"History\metrics-history.csv";
    public const string LegacyFolder = @"legacy\mecm-health-dashboard";
    public const string SiblingFolder = "mecm-health-dashboard";
    public const long MaxPrefsBytes = 1024 * 1024;
    public const long MaxHistoryBytes = MetricsHistoryStore.MaxReadBytes;

    private readonly Func<IReadOnlyList<string>> _suiteFolders;
    private readonly MetricsHistoryStore _history;
    private readonly Func<LiveSettingValues> _read;
    private readonly Func<LiveSettingValues, bool> _fill;
    private readonly IAppLog? _log;
    private readonly Func<DateTime> _utcNow;

    public const string SettingsNotSaved = "The settings file could not be written, so no setting was filled.";

    /// <param name="suiteFolders">The suite Site Hygiene folders to look in, in order; null uses <see cref="SuiteFolders"/>.</param>
    /// <param name="read">The stored Live settings now.</param>
    /// <param name="fill">Stores each non-null value of its argument; the import passes only values that were null. False when nothing was written.</param>
    public LegacyDashboardImport(MetricsHistoryStore history, Func<LiveSettingValues> read, Func<LiveSettingValues, bool> fill,
        Func<IReadOnlyList<string>>? suiteFolders = null, IAppLog? log = null, Func<DateTime>? utcNow = null)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _fill = fill ?? throw new ArgumentNullException(nameof(fill));
        _suiteFolders = suiteFolders ?? SuiteFolders;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AppPackagerSuite";

    /// <summary>The site-hygiene folder of the per-user suite install: the registered install folder, then %LOCALAPPDATA%\AppPackagerSuite.</summary>
    public static IReadOnlyList<string> SuiteFolders()
    {
        var roots = new List<string>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is string registered && registered.Trim().Length > 0) roots.Add(registered.Trim());
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppPackagerSuite"));
        var folders = new List<string>();
        foreach (var root in roots)
        {
            try
            {
                var folder = Path.GetFullPath(Path.Combine(root, "site-hygiene"));
                if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) folders.Add(folder);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }
        return folders;
    }

    /// <summary>
    /// The source folders in search order, each with its settings file name and the folder up to which every
    /// folder on the path of a file must not be a link: the suite folder for the suite and legacy folders, the
    /// sibling folder for the sibling.
    /// </summary>
    internal IEnumerable<(string Folder, string PrefsName, string Root)> Candidates()
    {
        foreach (var suite in _suiteFolders())
        {
            yield return (suite, SuitePrefsFile, suite);
            yield return (Path.Combine(suite, LegacyFolder), DashboardPrefsFile, suite);
            var parent = Path.GetDirectoryName(suite.TrimEnd('\\', '/'));
            if (!string.IsNullOrEmpty(parent))
            {
                var sibling = Path.Combine(parent, SiblingFolder);
                yield return (sibling, DashboardPrefsFile, sibling);
            }
        }
    }

    private sealed class RefusedException(string message) : Exception(message);

    /// <summary>
    /// Refuses a file that is a link, lies in a folder that is a link between the file and <paramref name="root"/>
    /// (the root included), or is larger than <paramref name="cap"/>. A junction anywhere on that path leads the read
    /// to files outside the suite folder.
    /// </summary>
    internal static void CheckFile(string path, long cap, string what, string? root = null)
    {
        var info = new FileInfo(path);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new RefusedException($"The {what} {path} is a link. Nothing was imported.");
        if (root is not null)
        {
            var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var folder = info.Directory;
            while (folder is not null)
            {
                var full = Path.TrimEndingDirectorySeparator(folder.FullName);
                if (!full.StartsWith(top, StringComparison.OrdinalIgnoreCase)) break;
                if (folder.Exists && folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new RefusedException($"The folder {full} of the {what} is a link. Nothing was imported.");
                if (full.Length == top.Length) break;
                folder = folder.Parent;
            }
        }
        if (info.Length > cap) throw new RefusedException($"The {what} {path} is larger than {(cap / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MB. Nothing was imported.");
    }

    /// <summary>The settings a suite or dashboard settings file holds; a value that is missing, null or of another type is not imported.</summary>
    internal static LiveSettingValues ReadPrefs(string path, string? linkRoot = null)
    {
        CheckFile(path, MaxPrefsBytes, "settings file", linkRoot);
        JsonDocument doc;
        try
        {
            // Windows PowerShell 5.1 writes UTF-8 with a byte order mark, which the JSON reader rejects.
            var bytes = File.ReadAllBytes(path).AsMemory();
            if (bytes.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) bytes = bytes[3..];
            doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 16 });
        }
        catch (JsonException)
        {
            throw new RefusedException($"The settings file {path} is not valid JSON. Nothing was imported.");
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new RefusedException($"The settings file {path} holds no settings object. Nothing was imported.");
            var root = doc.RootElement;
            JsonElement? Get(string name)
            {
                foreach (var p in root.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
                return null;
            }
            string? Text(string name) => Get(name) is { ValueKind: JsonValueKind.String } v && v.GetString() is { } s && s.Trim().Length > 0 && s.Trim().Length <= 255 ? s.Trim() : null;
            bool? Bool(string name) => Get(name) is { } v && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
            int? Int(string name, int min, int max) => Get(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var n) && n >= min && n <= max ? n : null;
            return new LiveSettingValues(
                SqlServer: Text("SQLServer"),
                TrustSqlCertificate: Bool("SqlTrustServerCertificate"),
                RefreshMinutes: Int("AutoRefreshMinutes", 1, 100_000) is { } m ? LiveSettings.ClampRefresh(m) : null,
                InactiveDays: Int("InactiveThresholdDays", 1, 100_000) is { } d ? LiveSettings.ClampInactive(d) : null,
                AlertsEnabled: Bool("AlertsEnabled"),
                CompliancePctFloor: Int("AlertCompliancePct", 0, 100));
        }
    }

    /// <summary>The values of <paramref name="source"/> whose stored value is null now, and their keys.</summary>
    internal static (LiveSettingValues Values, List<string> Keys) Fill(LiveSettingValues source, LiveSettingValues current)
    {
        var keys = new List<string>();
        T? Pick<T>(T? from, T? now, string key) where T : class
        {
            if (from is null || now is not null) return null;
            keys.Add(key);
            return from;
        }
        T? PickValue<T>(T? from, T? now, string key) where T : struct
        {
            if (from is null || now is not null) return null;
            keys.Add(key);
            return from;
        }
        var values = new LiveSettingValues(
            SqlServer: Pick(source.SqlServer, string.IsNullOrWhiteSpace(current.SqlServer) ? null : current.SqlServer, LiveSettingKeys.SqlServer),
            TrustSqlCertificate: PickValue(source.TrustSqlCertificate, current.TrustSqlCertificate, LiveSettingKeys.TrustSqlCertificate),
            RefreshMinutes: PickValue(source.RefreshMinutes, current.RefreshMinutes, LiveSettingKeys.RefreshMinutes),
            InactiveDays: PickValue(source.InactiveDays, current.InactiveDays, LiveSettingKeys.InactiveDays),
            AlertsEnabled: PickValue(source.AlertsEnabled, current.AlertsEnabled, LiveSettingKeys.AlertsEnabled),
            CompliancePctFloor: PickValue(source.CompliancePctFloor, current.CompliancePctFloor, LiveSettingKeys.CompliancePctFloor));
        return (values, keys);
    }

    private (List<MetricRow> Rows, int Skipped) ReadHistory(string path, string root)
    {
        CheckFile(path, MaxHistoryBytes, "history file", root);
        try
        {
            var (rows, skipped, _) = MetricsHistoryStore.ReadFile(path, MaxHistoryBytes);
            var cutoff = _utcNow().AddDays(-MetricsHistoryStore.KeepDays);
            return (rows.Where(r => r.TimestampUtc >= cutoff).ToList(), skipped);
        }
        catch (InvalidDataException ex) when (ex.Message == MetricsHistoryStore.NoKnownColumns)
        {
            throw new RefusedException($"The history file has no known columns: {path}. Nothing was imported.");
        }
        catch (InvalidDataException)
        {
            throw new RefusedException($"The history file {path} has no Timestamp column. Nothing was imported.");
        }
    }

    /// <summary>The first source that copies something; else the first source found, with nothing to copy; else an empty plan.</summary>
    public LegacyImportPlan Plan()
    {
        LegacyImportPlan? empty = null;
        foreach (var (folder, prefsName, root) in Candidates())
        {
            string prefs, history;
            try
            {
                prefs = Path.Combine(folder, prefsName);
                history = Path.Combine(folder, HistoryRelative);
                if (!File.Exists(prefs) && !File.Exists(history)) continue;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            try
            {
                var keys = new List<string>();
                var values = LiveSettingValues.Empty;
                if (File.Exists(prefs)) (values, keys) = Fill(ReadPrefs(prefs, root), _read());
                var rows = 0;
                var skipped = 0;
                var copy = false;
                if (File.Exists(history))
                {
                    var read = ReadHistory(history, root);
                    skipped = read.Skipped;
                    rows = read.Rows.Count;
                    if (rows == 0 && skipped > 0) throw new RefusedException($"No line of the history file {history} could be read. Nothing was imported.");
                    copy = rows > 0 && !_history.Exists;
                }
                var plan = new LegacyImportPlan(folder, keys, copy ? rows : 0, copy, null)
                {
                    SettingsFile = File.Exists(prefs) ? prefs : "",
                    HistoryFile = File.Exists(history) ? history : "",
                    SkippedLines = skipped,
                    Values = values
                };
                if (!plan.IsEmpty) return plan;
                empty ??= plan;
            }
            catch (RefusedException ex)
            {
                _log?.Warning(SiteHygieneService.LogArea, "The dashboard import was refused: " + ex.Message);
                return new LegacyImportPlan(folder, [], 0, false, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var message = $"The files in {folder} could not be read. Nothing was imported.";
                _log?.Warning(SiteHygieneService.LogArea, $"The dashboard import was refused: the files could not be read ({ex.GetType().Name}).");
                return new LegacyImportPlan(folder, [], 0, false, message);
            }
        }
        return empty ?? new LegacyImportPlan("", [], 0, false, null);
    }

    public LegacyImportResult Apply(LegacyImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Refusal is not null) return new LegacyImportResult([], 0, false, plan.Refusal);
        if (plan.IsEmpty) return new LegacyImportResult([], 0, false, "Nothing to import. The settings have values and a history exists.");
        List<string> keys;
        bool notSaved;
        try
        {
            var (values, found) = Fill(plan.Values, _read());
            keys = found.Where(plan.SettingKeys.Contains).ToList();
            notSaved = keys.Count > 0 && !_fill(Only(values, keys));
            if (notSaved)
            {
                _log?.Warning(SiteHygieneService.LogArea, "The dashboard import could not write the settings file.");
                keys = [];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Warning(SiteHygieneService.LogArea, $"The dashboard import failed ({ex.GetType().Name}).");
            return new LegacyImportResult([], 0, false, $"The files in {plan.SourceFolder} could not be read. Nothing was imported.");
        }

        // The settings are stored from here on, so a failed history copy keeps them in the result.
        var copied = false;
        var rows = 0;
        string? historyProblem = null;
        if (plan.HistoryWillBeCopied && plan.HistoryFile.Length > 0 && !_history.Exists)
        {
            try
            {
                var read = ReadHistory(plan.HistoryFile, RootOf(plan.SourceFolder));
                if (read.Rows.Count > 0 && _history.ImportIfEmpty(read.Rows))
                {
                    copied = true;
                    rows = read.Rows.Count;
                }
                else historyProblem = "The history was not copied: a history exists now, or it could not be written.";
            }
            catch (RefusedException ex)
            {
                historyProblem = ex.Message.Replace(" Nothing was imported.", " The history was not copied.", StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Warning(SiteHygieneService.LogArea, $"The dashboard history could not be copied ({ex.GetType().Name}).");
                historyProblem = $"The history file {plan.HistoryFile} could not be read. The history was not copied.";
            }
        }
        var parts = new List<string>();
        if (keys.Count > 0) parts.Add($"Filled {keys.Count.ToString(CultureInfo.InvariantCulture)} settings: {string.Join(", ", keys)}.");
        if (copied) parts.Add($"Copied {rows.ToString("N0", CultureInfo.InvariantCulture)} history rows.");
        if (notSaved) parts.Add(SettingsNotSaved);
        if (historyProblem is not null) parts.Add(historyProblem);
        if (parts.Count == 0) parts.Add("Nothing changed. The settings already have values and a history exists.");
        _log?.Info(SiteHygieneService.LogArea, $"Dashboard import: {keys.Count.ToString(CultureInfo.InvariantCulture)} settings, {rows.ToString(CultureInfo.InvariantCulture)} history rows.");
        return new LegacyImportResult(keys, rows, copied, string.Join(" ", parts));
    }

    /// <summary>The link-check root of a source folder of a plan; the folder itself when no candidate names it.</summary>
    private string RootOf(string folder)
    {
        foreach (var (candidate, _, root) in Candidates())
            if (string.Equals(Path.TrimEndingDirectorySeparator(candidate), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase)) return root;
        return folder;
    }

    private static LiveSettingValues Only(LiveSettingValues v, List<string> keys) => new(
        SqlServer: keys.Contains(LiveSettingKeys.SqlServer) ? v.SqlServer : null,
        TrustSqlCertificate: keys.Contains(LiveSettingKeys.TrustSqlCertificate) ? v.TrustSqlCertificate : null,
        RefreshMinutes: keys.Contains(LiveSettingKeys.RefreshMinutes) ? v.RefreshMinutes : null,
        InactiveDays: keys.Contains(LiveSettingKeys.InactiveDays) ? v.InactiveDays : null,
        AlertsEnabled: keys.Contains(LiveSettingKeys.AlertsEnabled) ? v.AlertsEnabled : null,
        CompliancePctFloor: keys.Contains(LiveSettingKeys.CompliancePctFloor) ? v.CompliancePctFloor : null);
}
