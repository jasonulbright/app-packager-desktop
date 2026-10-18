using System.Globalization;
using System.Text.Json;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The detection test history in one JSON file, newest first, bounded to the newest
/// entries. A file that cannot be read or parsed is left in place and the store starts
/// empty; the first write copies it aside under a time-stamped name before replacing it.
/// A write failure keeps the change in memory and is logged, never thrown.
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

    public DetectionHistoryStore(string? folder = null, int capacity = DefaultCapacity, IAppLog? log = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _folder = folder;
        _capacity = capacity;
        _log = log;
    }

    public static string DefaultFolder() => Path.Combine(AppDataPaths.Root, "DetectionTester");

    private string FilePath => Path.Combine(_folder ?? DefaultFolder(), FileName);

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
            Save(entries);
        }
    }

    private List<DetectionHistoryEntry> Loaded()
    {
        if (_entries is not null) return _entries;
        _entries = [];
        string path;
        try
        {
            path = FilePath;
            if (!File.Exists(path)) return _entries;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            _log?.Warning(LogArea, $"The detection history folder could not be resolved: {ex.Message}");
            return _entries;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredHistory>(File.ReadAllText(path), JsonOptions)
                ?? throw new JsonException("The history file is empty.");
            _entries = (stored.Entries ?? [])
                .Where(e => e is not null && e.Detection is not null)
                .Select(e => e!)
                .OrderByDescending(e => e.Time)
                .Take(_capacity)
                .ToList();
        }
        catch (Exception ex) when (IsStorageFailure(ex) || ex is JsonException or NotSupportedException)
        {
            _preserveBeforeWrite = true;
            _log?.Warning(LogArea, $"The detection history file could not be read and is set aside at the next save: {ex.Message}");
        }
        return _entries;
    }

    private void Save(List<DetectionHistoryEntry> entries)
    {
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
            _log?.Warning(LogArea, $"The detection history could not be saved: {ex.Message}");
        }
    }

    private static bool IsStorageFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
