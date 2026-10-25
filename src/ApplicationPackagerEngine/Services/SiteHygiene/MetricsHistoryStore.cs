using System.Globalization;
using System.Text;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// An exclusive lock between processes on a file beside the data file. Appends and rewrites hold it, so two
/// app instances never interleave a line or lose each other's rows; readers do not take it, because a
/// rewrite replaces the data file in one move.
/// </summary>
internal static class LiveFileLock
{
    public static FileStream? Acquire(string dataPath, TimeSpan wait)
    {
        var lockPath = dataPath + ".lock";
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(25);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}

/// <summary>
/// The metrics history in &lt;data root&gt;\SiteHygiene\metrics-history.csv: a header line with the source
/// columns, then one line of counts per completed refresh. An empty cell is a count that was not known, never
/// zero. An append writes one whole line under the lock; the prune to <see cref="KeepDays"/> days runs at most
/// once per <see cref="PruneEvery"/> and only when the oldest line is past the cut-off, through a temporary file
/// and one move. A read takes at most <see cref="MaxReadBytes"/> from the end of the file, skips and counts a
/// line it cannot read, and reads quoted and unquoted cells, so the files the suite tool wrote read too.
/// </summary>
public sealed class MetricsHistoryStore : IMetricsHistoryStore
{
    public const string FileName = "metrics-history.csv";
    public const int KeepDays = 180;
    public const long MaxReadBytes = 32L * 1024 * 1024;
    public static readonly TimeSpan PruneEvery = TimeSpan.FromDays(1);
    public static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

    private readonly string? _path;
    private readonly IAppLog? _log;
    private readonly Func<DateTime> _utcNow;
    private readonly Lock _gate = new();
    private DateTime? _lastPrune;

    public MetricsHistoryStore(string? path = null, IAppLog? log = null, Func<DateTime>? utcNow = null)
    {
        _path = path;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string Path => _path ?? System.IO.Path.Combine(HygieneFolders.Root(), FileName);

    public bool Exists
    {
        get
        {
            try { var info = new FileInfo(Path); return info.Exists && info.Length > 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        }
    }

    public static string HeaderLine => string.Join(",", LiveMetrics.FileColumns);

    internal static string Format(MetricRow row)
    {
        var sb = new StringBuilder();
        sb.Append(DateTime.SpecifyKind(row.TimestampUtc, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture));
        foreach (var column in LiveMetrics.Columns)
        {
            sb.Append(',');
            if (row.Values.TryGetValue(column, out var v) && v is { } d && double.IsFinite(d)) sb.Append(d.ToString("0.###", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private bool IsLinked(string path)
    {
        var folder = System.IO.Path.GetDirectoryName(path)!;
        if (DataFolderLinks.HasLink(folder, DataFolderLinks.ContainerOf(folder, _path is null))) return true;
        return File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
    }

    public void Append(MetricRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var path = Path;
        lock (_gate)
        {
            try
            {
                if (IsLinked(path))
                {
                    _log?.Warning(SiteHygieneService.LogArea, "The metrics history folder or file is a link; the refresh was not recorded.");
                    return;
                }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                using var held = LiveFileLock.Acquire(path, LockWait);
                if (held is null)
                {
                    _log?.Warning(SiteHygieneService.LogArea, "The metrics history file is locked by another program; the refresh was not recorded.");
                    return;
                }
                using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                {
                    var prefix = "";
                    if (stream.Length == 0) prefix = HeaderLine + "\r\n";
                    else
                    {
                        if (!HasOwnHeader(stream))
                        {
                            _log?.Warning(SiteHygieneService.LogArea, "The metrics history file has other columns than this version writes; the refresh was not recorded.");
                            return;
                        }
                        // A line cut by a full disk or a killed process has no line end; the new line must not join it.
                        stream.Seek(-1, SeekOrigin.End);
                        if (stream.ReadByte() != '\n') prefix = "\r\n";
                    }
                    stream.Seek(0, SeekOrigin.End);
                    var bytes = Encoding.UTF8.GetBytes(prefix + Format(row) + "\r\n");
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
                PruneIfDue(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Warning(SiteHygieneService.LogArea, $"The metrics history could not be written ({ex.GetType().Name}); the refresh was not recorded.");
            }
        }
    }

    /// <summary>
    /// True when the first line of <paramref name="stream"/> is <see cref="HeaderLine"/>, with or without a byte
    /// order mark. A file with other columns or another column order would get rows its header does not describe.
    /// </summary>
    private static bool HasOwnHeader(FileStream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var limit = Encoding.UTF8.GetByteCount(HeaderLine) + 8;
        var buffer = new byte[Math.Min(limit, stream.Length)];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }
        var text = Encoding.UTF8.GetString(buffer, 0, read).TrimStart('﻿');
        var end = text.IndexOfAny(['\r', '\n']);
        // A file cut right after the header has no line end; the append then adds the line end first.
        if (end < 0) return read == stream.Length && string.Equals(text, HeaderLine, StringComparison.Ordinal);
        return string.Equals(text[..end], HeaderLine, StringComparison.Ordinal);
    }

    /// <summary>Called under the lock. Reads only the first data line unless the prune is due and needed.</summary>
    private void PruneIfDue(string path)
    {
        var now = _utcNow();
        if (_lastPrune is { } last && now - last < PruneEvery) return;
        _lastPrune = now;
        var cutoff = now.AddDays(-KeepDays);
        string? first;
        string? header;
        using (var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8))
        {
            header = reader.ReadLine();
            first = reader.ReadLine();
        }
        if (header is null || first is null) return;
        var map = HeaderMap(header);
        if (map is null) return;
        if (ParseLine(first, map) is { } row && row.TimestampUtc >= cutoff) return;

        var kept = new StringBuilder();
        kept.Append(HeaderLine).Append("\r\n");
        var dropped = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8).Skip(1))
        {
            if (line.Length == 0) continue;
            var parsed = ParseLine(line, map);
            if (parsed is null || parsed.TimestampUtc < cutoff) { dropped++; continue; }
            kept.Append(Format(parsed)).Append("\r\n");
        }
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(temp, kept.ToString(), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            _log?.Info(SiteHygieneService.LogArea, $"The metrics history was pruned to {KeepDays} days; {dropped.ToString(CultureInfo.InvariantCulture)} lines were removed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _log?.Warning(SiteHygieneService.LogArea, $"The metrics history could not be pruned ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Writes <paramref name="rows"/> as the whole history when no history exists: a temporary file, then one
    /// move that fails when a history file appeared meanwhile. False when a history exists or the write failed;
    /// then nothing changed.
    /// </summary>
    internal bool ImportIfEmpty(IReadOnlyList<MetricRow> rows)
    {
        var path = Path;
        lock (_gate)
        {
            var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                if (IsLinked(path) || Exists) return false;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                using var held = LiveFileLock.Acquire(path, LockWait);
                if (held is null || Exists) return false;
                var text = new StringBuilder();
                text.Append(HeaderLine).Append("\r\n");
                foreach (var row in rows.OrderBy(r => r.TimestampUtc)) text.Append(Format(row)).Append("\r\n");
                File.WriteAllText(temp, text.ToString(), new UTF8Encoding(false));
                // Under the lock no append can add a line; only an empty file is removed before the move.
                if (File.Exists(path) && new FileInfo(path).Length == 0) File.Delete(path);
                File.Move(temp, path, overwrite: false);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Warning(SiteHygieneService.LogArea, $"The imported metrics history could not be written ({ex.GetType().Name}).");
                return false;
            }
            finally
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    // ---- Reading -------------------------------------------------------------------------------

    /// <summary>The cells of one CSV line; quoted cells may hold commas and doubled quotes.</summary>
    internal static List<string> Cells(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    /// <summary>The index of each column in a header line; null when the line has no Timestamp column.</summary>
    internal static Dictionary<string, int>? HeaderMap(string header)
    {
        var cells = Cells(header.TrimStart('﻿'));
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < cells.Count; i++) map.TryAdd(cells[i].Trim(), i);
        return map.ContainsKey(LiveMetrics.Timestamp) ? map : null;
    }

    internal static bool TryTime(string text, out DateTime utc)
    {
        if (DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t))
        {
            utc = t.UtcDateTime;
            return true;
        }
        utc = default;
        return false;
    }

    /// <summary>One data line; null when the line is not a row of this file.</summary>
    internal static MetricRow? ParseLine(string line, Dictionary<string, int> map)
    {
        var cells = Cells(line);
        if (cells.Count < map.Count) return null;
        if (!TryTime(cells[map[LiveMetrics.Timestamp]], out var time)) return null;
        var values = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach (var column in LiveMetrics.Columns)
        {
            double? v = null;
            if (map.TryGetValue(column, out var i))
            {
                var text = cells[i].Trim();
                if (text.Length > 0)
                {
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d)) return null;
                    v = d;
                }
            }
            values[column] = v;
        }
        return new MetricRow(time, values);
    }

    /// <summary>
    /// The rows of a history file, oldest first, with the lines that could not be read. A file larger than
    /// <paramref name="maxBytes"/> is read from its last <paramref name="maxBytes"/> bytes (the newest rows).
    /// A repeated header line is not counted as skipped.
    /// </summary>
    internal static (List<MetricRow> Rows, int Skipped, bool Cut) ReadFile(string path, long maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var header = reader.ReadLine();
        if (header is null) return ([], 0, false);
        var map = HeaderMap(header) ?? throw new InvalidDataException("The file has no Timestamp column.");
        var cut = false;
        if (stream.Length > maxBytes)
        {
            stream.Seek(-maxBytes, SeekOrigin.End);
            reader.DiscardBufferedData();
            reader.ReadLine();
            cut = true;
        }
        var rows = new List<MetricRow>();
        var skipped = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Trim().Length == 0) continue;
            if (HeaderMap(line) is not null) continue;
            if (ParseLine(line, map) is { } row) rows.Add(row);
            else skipped++;
        }
        rows.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        return (rows, skipped, cut);
    }

    public IReadOnlyList<MetricRow> Read(int days) => ReadCounted(days).Rows;

    /// <summary>The rows of the last <paramref name="days"/> days and the lines of this read that could not be read.</summary>
    public (IReadOnlyList<MetricRow> Rows, int Skipped) ReadCounted(int days)
    {
        var path = Path;
        try
        {
            if (!File.Exists(path) || IsLinked(path)) return ([], 0);
            var (rows, skipped, _) = ReadFile(path, MaxReadBytes);
            if (skipped > 0) _log?.Warning(SiteHygieneService.LogArea, $"The metrics history has {skipped.ToString(CultureInfo.InvariantCulture)} lines that could not be read; they were skipped.");
            var cutoff = _utcNow().AddDays(-Math.Max(0, days));
            return (rows.Where(r => r.TimestampUtc >= cutoff).ToList(), skipped);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log?.Warning(SiteHygieneService.LogArea, $"The metrics history could not be read ({ex.GetType().Name}).");
            return ([], 0);
        }
    }

    public MetricSeries Series(string column, int days, int maxPoints)
    {
        _ = LiveMetrics.ViewOf(column);
        var (rows, skipped) = ReadCounted(days);
        return Build(column, rows, skipped, maxPoints);
    }

    /// <summary>
    /// The points of one column, oldest first; an empty cell breaks the line. Above <paramref name="maxPoints"/>
    /// rows the rows are cut into buckets of equal size in file order: a bucket with values is drawn as the mean
    /// time and the mean value of its values, and a bucket with only empty cells breaks the line. An empty cell
    /// inside a bucket that also has values does not break the line.
    /// </summary>
    internal static MetricSeries Build(string column, IReadOnlyList<MetricRow> rows, int skipped, int maxPoints)
    {
        maxPoints = Math.Max(2, maxPoints);
        var bucket = rows.Count <= maxPoints ? 1 : (rows.Count + maxPoints - 1) / maxPoints;
        var points = new List<(DateTime T, double V)>();
        var gaps = new List<int>();
        var total = 0;
        var broken = false;
        for (var i = 0; i < rows.Count; i += bucket)
        {
            double sumT = 0, sumV = 0;
            var n = 0;
            for (var j = i; j < Math.Min(i + bucket, rows.Count); j++)
            {
                if (!rows[j].Values.TryGetValue(column, out var v) || v is not { } d) continue;
                sumT += rows[j].TimestampUtc.Ticks;
                sumV += d;
                n++;
            }
            total += n;
            if (n == 0) { broken = points.Count > 0; continue; }
            if (broken) { gaps.Add(points.Count); broken = false; }
            points.Add((new DateTime((long)(sumT / n), DateTimeKind.Utc), sumV / n));
        }
        return new MetricSeries(column, points, skipped, bucket > 1) { Gaps = gaps, SourcePoints = total };
    }
}
