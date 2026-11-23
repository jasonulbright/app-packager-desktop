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

    /// <summary>Adds the site_code column (Q-112d). Rows written before it belong to the site whose folder holds the database.</summary>
    public const string SiteColumnMigrationId = "site-operations-v2";
    public const string LogArea = "History";

    private static readonly TimeSpan DefaultBusyTimeout = TimeSpan.FromSeconds(30);

    private readonly string? _fixedDbPath;
    private readonly IAppLog? _log;
    private readonly TimeSpan _busyTimeout;
    private readonly object _migrationGate = new();
    private string? _migratedPath;

    /// <summary>The database in use now: the path given to the constructor, else results\history.db of the active site, else the single-site file.</summary>
    private string DbPath => _fixedDbPath ?? SiteHistoryDatabase.DefaultPath();

    /// <summary>The site code that rows are written with and read by: the active site, or empty when the single-site layout is in use.</summary>
    private static string CurrentSiteCode() =>
        SiteContext.ActiveFolderName is { } f && SiteDataPaths.TryParseFolderName(f, out var parsed) && parsed is not null ? parsed.SiteCode : "";

    public SiteOperationHistory(string? dbPath = null, IAppLog? log = null)
        : this(dbPath, log, DefaultBusyTimeout)
    {
    }

    internal SiteOperationHistory(string? dbPath, IAppLog? log, TimeSpan busyTimeout)
    {
        _fixedDbPath = dbPath is null ? null : Path.GetFullPath(dbPath);
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
                INSERT INTO SiteOperations(occurred_at, tool, action, target, outcome, detail, site_code)
                VALUES($at, $tool, $action, $target, $outcome, $detail, $site);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$site", CurrentSiteCode());
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
            // With a site active only that site's rows are listed, even when the file holds rows of another site.
            var site = CurrentSiteCode();
            var conditions = new List<string>();
            // Rows with no site code come from the single-site file; the folder of a site holds its own file, so they are its rows.
            if (site.Length > 0) conditions.Add("(site_code = $site OR site_code = '')");
            if (tool is not null) conditions.Add("tool = $tool");
            command.CommandText = "SELECT id, occurred_at, tool, action, target, outcome, detail FROM SiteOperations"
                + (conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "")
                + " ORDER BY occurred_at DESC, id DESC LIMIT $count";
            command.Parameters.AddWithValue("$count", count);
            if (site.Length > 0) command.Parameters.AddWithValue("$site", site);
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
            var path = DbPath;
            if (string.Equals(_migratedPath, path, StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var connection = Open();
            // A read first: a database that is already migrated needs no write lock, so a
            // read-only file still serves Recent.
            if (!IsMigrated(connection, null, MigrationId)) Migrate(connection);
            if (!IsMigrated(connection, null, SiteColumnMigrationId)) AddSiteColumn(connection);
            _migratedPath = path;
        }
    }

    private static bool IsMigrated(SQLiteConnection connection, SQLiteTransaction? transaction, string migrationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'inventory_migrations'";
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return false;
        command.CommandText = "SELECT COUNT(*) FROM inventory_migrations WHERE migration_id = $id";
        command.Parameters.AddWithValue("$id", migrationId);
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
        if (IsMigrated(connection, transaction, MigrationId))
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

    /// <summary>
    /// Adds site_code and gives every earlier row the site code of this database: the folder of a site holds its own file, so each
    /// row in it belongs to that site. In the single-site file the column stays empty and no filter applies.
    /// </summary>
    private static void AddSiteColumn(SQLiteConnection connection)
    {
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        if (IsMigrated(connection, transaction, SiteColumnMigrationId))
        {
            transaction.Commit();
            return;
        }
        var hasColumn = false;
        using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = "PRAGMA table_info(SiteOperations)";
            using var reader = info.ExecuteReader();
            while (reader.Read())
                if (string.Equals(reader.GetString(1), "site_code", StringComparison.OrdinalIgnoreCase)) hasColumn = true;
        }
        using (var alter = connection.CreateCommand())
        {
            alter.Transaction = transaction;
            alter.CommandText = (hasColumn ? "" : "ALTER TABLE SiteOperations ADD COLUMN site_code TEXT NOT NULL DEFAULT '';")
                + "UPDATE SiteOperations SET site_code = $site WHERE site_code = '';"
                + "CREATE INDEX IF NOT EXISTS idx_site_operations_site_time ON SiteOperations(site_code, occurred_at);"
                + "INSERT INTO inventory_migrations(migration_id, completed_at) VALUES($id, $at);";
            alter.Parameters.AddWithValue("$site", CurrentSiteCode());
            alter.Parameters.AddWithValue("$id", SiteColumnMigrationId);
            alter.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            alter.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private SQLiteConnection Open()
    {
        var connection = new SQLiteConnection($"Data Source={DbPath};Version=3;Pooling=False;");
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
