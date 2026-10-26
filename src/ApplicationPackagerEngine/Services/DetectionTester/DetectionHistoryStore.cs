using System.Globalization;
using System.Text.Json;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The detection test history in one JSON file, newest first, bounded to the newest
/// entries. A file that cannot be parsed is left in place and the store starts empty; the
/// first write copies it aside under a time-stamped name before replacing it. A write
/// failure keeps the change in memory and is logged, never thrown.
/// </summary>
public sealed class DetectionHistoryStore : IDetectionHistoryStore
{
    public const string FileName = "history.json";
    public const int DefaultCapacity = 500;
    public const string LogArea = "DetectionTester";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string? _folder;
    private readonly int _capacity;
    private readonly IAppLog? _log;
    private readonly object _gate = new();
    private List<DetectionHistoryEntry>? _entries;
    private bool _preserveBeforeWrite;
    private bool _readPending;

    private const int ReadAttempts = 5;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(100);

    public DetectionHistoryStore(string? folder = null, int capacity = DefaultCapacity, IAppLog? log = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _folder = folder;
        _capacity = capacity;
        _log = log;
    }

    public static string DefaultFolder() => DetectionTesterFolders.Root();

    /// <summary>The history file; a folder that is a junction or symbolic link throws <see cref="DetectionFolderRefusedException"/>.</summary>
    private string FilePath
    {
        get
        {
            var folder = _folder ?? DefaultFolder();
            if (DetectionTesterFolders.HasLink(folder, DetectionTesterFolders.ContainerOf(folder, _folder is null)))
                throw new DetectionFolderRefusedException(DetectionFolderRefusedException.HistoryMessage);
            return Path.Combine(folder, FileName);
        }
    }

    private sealed record StoredHistory(int Version, List<DetectionHistoryEntry?>? Entries);

    public IReadOnlyList<DetectionHistoryEntry> All()
    {
        lock (_gate) return Loaded().ToList();
    }

    public void Add(DetectionHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(entry.Detection);
        lock (_gate)
        {
            var entries = Loaded();
            entries.Insert(0, entry);
            if (entries.Count > _capacity) entries.RemoveRange(_capacity, entries.Count - _capacity);
            Save(entries);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            var entries = Loaded();
            entries.Clear();
            _readPending = false;
            Save(entries);
        }
    }

    /// <summary>
    /// The entries in memory, read from the file on first use. A file that cannot be
    /// opened (a sharing violation, a denied read) is not corrupt: it is retried at every
    /// call until it is read, the entries added meanwhile are merged into it, and nothing
    /// is written over it before then.
    /// </summary>
    private List<DetectionHistoryEntry> Loaded()
    {
        if (_entries is not null && !_readPending) return _entries;
        _entries ??= [];
        string path;
        try
        {
            path = FilePath;
            if (!File.Exists(path))
            {
                _readPending = false;
                return _entries;
            }
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            _log?.Warning(LogArea, $"The detection history folder could not be resolved ({ex.GetType().Name}).");
            return _entries;
        }

        string text;
        try
        {
            text = ReadWithRetry(path);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            if (!_readPending)
                _log?.Warning(LogArea, $"The detection history file could not be opened; it is not changed until it can be read ({ex.GetType().Name}).");
            _readPending = true;
            return _entries;
        }

        var added = _entries;
        _readPending = false;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredHistory>(text, JsonOptions)
                ?? throw new JsonException("The history file is empty.");
            var known = added.Select(e => e.Id).ToHashSet();
            _entries = added
                .Concat((stored.Entries ?? []).Where(e => e is not null && e.Detection is not null && !known.Contains(e.Id)).Select(e => e!))
                .OrderByDescending(e => e.Time)
                .Take(_capacity)
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            _preserveBeforeWrite = true;
            _log?.Warning(LogArea, $"The detection history file could not be parsed and is set aside at the next save ({ex.GetType().Name}).");
        }
        return _entries;
    }

    private static string ReadWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < ReadAttempts)
            {
                Thread.Sleep(ReadRetryDelay);
            }
        }
    }

    private void Save(List<DetectionHistoryEntry> entries)
    {
        if (_readPending)
        {
            _log?.Warning(LogArea, "The detection history is kept in memory until the history file can be read.");
            return;
        }
        try
        {
            var path = FilePath;
            if (_preserveBeforeWrite && File.Exists(path))
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var copy = $"{path}.unreadable-{stamp}";
                for (var n = 2; File.Exists(copy); n++) copy = $"{path}.unreadable-{stamp}-{n}";
                File.Copy(path, copy);
            }
            _preserveBeforeWrite = false;
            var stored = new StoredHistory(1, entries.Cast<DetectionHistoryEntry?>().ToList());
            WorkbenchJson.WriteText(path, JsonSerializer.Serialize(stored, JsonOptions));
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            _log?.Warning(LogArea, $"The detection history could not be saved ({ex.GetType().Name}).");
        }
    }

    private static bool IsStorageFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
