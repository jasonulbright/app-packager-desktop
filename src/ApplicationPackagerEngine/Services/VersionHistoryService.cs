using System.Data.SQLite;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// SQLite-backed version history tracking every Check Latest, Stage, Package, Distribute, Deploy operation.
/// Database location: %LOCALAPPDATA%\SRLPackr\history.db
/// </summary>
public sealed class VersionHistoryService : IDisposable
{
    private readonly SQLiteConnection _db;

    public VersionHistoryService(string? dbPath = null)
    {
        dbPath ??= Path.Combine(AppDataPaths.LegacyRoot, "history.db");

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        _db = new SQLiteConnection($"Data Source={dbPath}");
        _db.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                app_id TEXT NOT NULL,
                app_name TEXT NOT NULL,
                version TEXT NOT NULL,
                operation TEXT NOT NULL,
                status TEXT NOT NULL,
                timestamp TEXT NOT NULL DEFAULT (datetime('now')),
                duration_ms INTEGER,
                detection_rule TEXT,
                installer_framework TEXT,
                error_message TEXT,
                metadata TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_history_app ON history(app_id);
            CREATE INDEX IF NOT EXISTS idx_history_ts ON history(timestamp);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Records an operation in the version history.
    /// </summary>
    public void Record(HistoryEntry entry)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO history (app_id, app_name, version, operation, status, timestamp, duration_ms, detection_rule, installer_framework, error_message, metadata)
            VALUES ($appId, $appName, $version, $operation, $status, $timestamp, $durationMs, $detectionRule, $framework, $errorMessage, $metadata)
            """;
        cmd.Parameters.AddWithValue("$appId", entry.AppId);
        cmd.Parameters.AddWithValue("$appName", entry.AppName);
        cmd.Parameters.AddWithValue("$version", entry.Version);
        cmd.Parameters.AddWithValue("$operation", entry.Operation);
        cmd.Parameters.AddWithValue("$status", entry.Status);
        cmd.Parameters.AddWithValue("$timestamp", entry.Timestamp.ToString("o"));
        cmd.Parameters.AddWithValue("$durationMs", (object?)entry.DurationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$detectionRule", (object?)entry.DetectionRule ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$framework", (object?)entry.InstallerFramework ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$errorMessage", (object?)entry.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$metadata", (object?)entry.Metadata ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Gets the full history for a specific app, newest first.
    /// </summary>
    public List<HistoryEntry> GetHistory(string appId, int limit = 100)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT * FROM history WHERE app_id = $appId ORDER BY timestamp DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$appId", appId);
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadEntries(cmd);
    }

    /// <summary>
    /// Gets the most recent operation for each app.
    /// </summary>
    public List<HistoryEntry> GetLatestPerApp()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT h.* FROM history h
            INNER JOIN (SELECT app_id, MAX(timestamp) as max_ts FROM history GROUP BY app_id) latest
            ON h.app_id = latest.app_id AND h.timestamp = latest.max_ts
            ORDER BY h.app_name
            """;
        return ReadEntries(cmd);
    }

    /// <summary>
    /// Gets all operations within a time range.
    /// </summary>
    public List<HistoryEntry> GetRange(DateTimeOffset from, DateTimeOffset to)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT * FROM history WHERE timestamp BETWEEN $from AND $to ORDER BY timestamp DESC";
        cmd.Parameters.AddWithValue("$from", from.ToString("o"));
        cmd.Parameters.AddWithValue("$to", to.ToString("o"));
        return ReadEntries(cmd);
    }

    /// <summary>
    /// Gets the last recorded version for an app (any successful operation).
    /// </summary>
    public string? GetLastVersion(string appId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT version FROM history WHERE app_id = $appId AND status = 'Success' ORDER BY timestamp DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$appId", appId);
        return cmd.ExecuteScalar() as string;
    }

    private List<HistoryEntry> ReadEntries(SQLiteCommand cmd)
    {
        var entries = new List<HistoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new HistoryEntry
            {
                Id = reader.GetInt64(0),
                AppId = reader.GetString(1),
                AppName = reader.GetString(2),
                Version = reader.GetString(3),
                Operation = reader.GetString(4),
                Status = reader.GetString(5),
                Timestamp = DateTimeOffset.Parse(reader.GetString(6)),
                DurationMs = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                DetectionRule = reader.IsDBNull(8) ? null : reader.GetString(8),
                InstallerFramework = reader.IsDBNull(9) ? null : reader.GetString(9),
                ErrorMessage = reader.IsDBNull(10) ? null : reader.GetString(10),
                Metadata = reader.IsDBNull(11) ? null : reader.GetString(11)
            });
        }
        return entries;
    }

    public void Dispose() => _db.Dispose();
}

/// <summary>
/// A single version history record.
/// </summary>
public sealed class HistoryEntry
{
    public long Id { get; init; }
    public string AppId { get; init; } = "";
    public string AppName { get; init; } = "";
    public string Version { get; init; } = "";
    /// <summary>CheckLatest, CheckMECM, Stage, Package, Distribute, Deploy, Install, Detect, Uninstall</summary>
    public string Operation { get; init; } = "";
    /// <summary>Success, Failed, Skipped, Cancelled</summary>
    public string Status { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public long? DurationMs { get; init; }
    /// <summary>JSON representation of the detection rule applied (for audit trail)</summary>
    public string? DetectionRule { get; init; }
    public string? InstallerFramework { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>Arbitrary JSON metadata (content path, manifest data, etc.)</summary>
    public string? Metadata { get; init; }
}
