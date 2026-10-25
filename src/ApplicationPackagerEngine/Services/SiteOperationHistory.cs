using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

public enum SiteOperationOutcome
{
    Succeeded,
    PartlySucceeded,
    Failed,
    Cancelled,

    /// <summary>Nothing was changed and nothing failed: every object was skipped, for example because its finding no longer held.</summary>
    Skipped
}

/// <summary>
/// One site operation to record. The detail is counts only (for example changed 3,
/// failed 1), so no object payload, script text or secret can reach the history. Count
/// names are short lower-case identifiers; any other name is refused when the entry is made.
/// </summary>
public sealed partial record SiteOperationEntry(
    string Tool,
    string Action,
    string Target,
    SiteOperationOutcome Outcome,
    IReadOnlyDictionary<string, int> Counts,
    DateTimeOffset? Time = null)
{
    public string Tool { get; init; } = Required(Tool, "A tool name is required.", nameof(Tool));
    public string Action { get; init; } = Required(Action, "An action is required.", nameof(Action));
    public IReadOnlyDictionary<string, int> Counts { get; init; } = CheckedCounts(Counts);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex CountName();

    private static string Required(string value, string message, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(message, name) : value;

    private static IReadOnlyDictionary<string, int> CheckedCounts(IReadOnlyDictionary<string, int>? counts)
    {
        var copy = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, value) in counts ?? new Dictionary<string, int>())
        {
            if (name is null || !CountName().IsMatch(name))
                throw new ArgumentException($"The count name '{name}' is not allowed. A count name starts with a lower-case letter and holds only lower-case letters, digits and underscores, at most 32 characters.", nameof(Counts));
            copy[name] = value;
        }
        return copy;
    }
}

public sealed record SiteOperation(
    long Id,
    DateTimeOffset Time,
    string Tool,
    string Action,
    string Target,
    SiteOperationOutcome Outcome,
    IReadOnlyDictionary<string, int> Counts);

/// <summary>Site-wide operations of the ConfigMgr tools, not keyed on an application.</summary>
public interface ISiteOperationHistory
{
    /// <summary>
    /// Stores the entry and returns its id, or null when the history could not be written.
    /// It never throws for a database problem: the operation it records already happened.
    /// </summary>
    long? Record(SiteOperationEntry entry);

    /// <summary>The newest operations first; a null tool returns every tool. Empty when the history cannot be read.</summary>
    IReadOnlyList<SiteOperation> Recent(string? tool, int count);
}

/// <summary>
/// The SiteOperations table in history.db. The migration ledger is the inventory's
/// inventory_migrations table, created here with the same definition when it is absent,
/// so the inventory tables keep their schema and rows. The migration runs on first use,
/// not on construction, and is tried again on each use until it succeeds.
/// </summary>
public sealed class SiteOperationHistory : ISiteOperationHistory
{
    public const string MigrationId = "site-operations-v1";
    public const string LogArea = "History";

    private static readonly TimeSpan DefaultBusyTimeout = TimeSpan.FromSeconds(30);

    private readonly string _dbPath;
    private readonly IAppLog? _log;
    private readonly TimeSpan _busyTimeout;
    private readonly object _migrationGate = new();
    private bool _migrated;

    public SiteOperationHistory(string? dbPath = null, IAppLog? log = null)
        : this(dbPath, log, DefaultBusyTimeout)
    {
    }

    internal SiteOperationHistory(string? dbPath, IAppLog? log, TimeSpan busyTimeout)
    {
        _dbPath = Path.GetFullPath(dbPath ?? Path.Combine(AppDataPaths.LegacyRoot, "history.db"));
        _log = log;
        _busyTimeout = busyTimeout;
    }

    public long? Record(SiteOperationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            EnsureMigrated();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO SiteOperations(occurred_at, tool, action, target, outcome, detail)
                VALUES($at, $tool, $action, $target, $outcome, $detail);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$at", (entry.Time ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$tool", entry.Tool.Trim());
            command.Parameters.AddWithValue("$action", entry.Action.Trim());
            command.Parameters.AddWithValue("$target", SecretMask.Apply(entry.Target ?? "").Trim());
            command.Parameters.AddWithValue("$outcome", entry.Outcome.ToString());
            command.Parameters.AddWithValue("$detail", JsonSerializer.Serialize(entry.Counts));
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            _log?.Warning(LogArea, $"The site operation history could not record {entry.Tool.Trim()} {entry.Action.Trim()} ({entry.Outcome}): {ex.Message}");
            return null;
        }
    }

    public IReadOnlyList<SiteOperation> Recent(string? tool, int count)
    {
        if (count <= 0) return [];
        try
        {
            try { EnsureMigrated(); }
            catch (Exception ex) when (IsStorageFailure(ex)) { }

            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = tool is null
                ? "SELECT id, occurred_at, tool, action, target, outcome, detail FROM SiteOperations ORDER BY occurred_at DESC, id DESC LIMIT $count"
                : "SELECT id, occurred_at, tool, action, target, outcome, detail FROM SiteOperations WHERE tool = $tool ORDER BY occurred_at DESC, id DESC LIMIT $count";
            command.Parameters.AddWithValue("$count", count);
            if (tool is not null) command.Parameters.AddWithValue("$tool", tool.Trim());
            using var reader = command.ExecuteReader();
            var rows = new List<SiteOperation>();
            while (reader.Read())
            {
                rows.Add(new SiteOperation(
                    reader.GetInt64(0),
                    DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime(),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                    Enum.TryParse<SiteOperationOutcome>(reader.GetString(5), out var outcome) ? outcome : SiteOperationOutcome.Failed,
                    ReadCounts(reader.IsDBNull(6) ? null : reader.GetString(6))));
            }
            return rows;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            _log?.Warning(LogArea, $"The site operation history could not be read: {ex.Message}");
            return [];
        }
    }

    private static bool IsStorageFailure(Exception ex) =>
        ex is SQLiteException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException;

    private static Dictionary<string, int> ReadCounts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private void EnsureMigrated()
    {
        lock (_migrationGate)
        {
            if (_migrated) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            using var connection = Open();
            // A read first: a database that is already migrated needs no write lock, so a
            // read-only file still serves Recent.
            if (!IsMigrated(connection, null)) Migrate(connection);
            _migrated = true;
        }
    }

    private static bool IsMigrated(SQLiteConnection connection, SQLiteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'inventory_migrations'";
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return false;
        command.CommandText = "SELECT COUNT(*) FROM inventory_migrations WHERE migration_id = $id";
        command.Parameters.AddWithValue("$id", MigrationId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static void Migrate(SQLiteConnection connection)
    {
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        using (var ledger = connection.CreateCommand())
        {
            ledger.Transaction = transaction;
            ledger.CommandText = """
                CREATE TABLE IF NOT EXISTS inventory_migrations (
                    migration_id TEXT PRIMARY KEY,
                    completed_at TEXT NOT NULL
                );
                """;
            ledger.ExecuteNonQuery();
        }
        if (IsMigrated(connection, transaction))
        {
            transaction.Commit();
            return;
        }

        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS SiteOperations (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    occurred_at TEXT NOT NULL,
                    tool TEXT NOT NULL,
                    action TEXT NOT NULL,
                    target TEXT,
                    outcome TEXT NOT NULL,
                    detail TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_site_operations_tool_time ON SiteOperations(tool, occurred_at);
                INSERT INTO inventory_migrations(migration_id, completed_at) VALUES($id, $at);
                """;
            create.Parameters.AddWithValue("$id", MigrationId);
            create.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            create.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private SQLiteConnection Open()
    {
        var connection = new SQLiteConnection($"Data Source={_dbPath};Version=3;Pooling=False;");
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout = {(long)_busyTimeout.TotalMilliseconds};");
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
