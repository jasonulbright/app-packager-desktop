using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.Text.Json;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

public enum SiteOperationOutcome
{
    Succeeded,
    PartlySucceeded,
    Failed,
    Cancelled
}

/// <summary>
/// One site operation to record. The detail is counts only (for example changed 3,
/// failed 1), so no object payload, script text or secret can reach the history.
/// </summary>
public sealed record SiteOperationEntry(
    string Tool,
    string Action,
    string Target,
    SiteOperationOutcome Outcome,
    IReadOnlyDictionary<string, int> Counts,
    DateTimeOffset? Time = null);

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
    long Record(SiteOperationEntry entry);

    /// <summary>The newest operations first; a null tool returns every tool.</summary>
    IReadOnlyList<SiteOperation> Recent(string? tool, int count);
}

/// <summary>
/// The SiteOperations table in history.db. The migration ledger is the inventory's
/// inventory_migrations table, created here with the same definition when it is absent,
/// so the inventory tables keep their schema and rows.
/// </summary>
public sealed class SiteOperationHistory : ISiteOperationHistory
{
    public const string MigrationId = "site-operations-v1";

    private readonly string _dbPath;

    public SiteOperationHistory(string? dbPath = null)
    {
        _dbPath = Path.GetFullPath(dbPath ?? Path.Combine(AppDataPaths.LegacyRoot, "history.db"));
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        Migrate();
    }

    public long Record(SiteOperationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Tool)) throw new ArgumentException("A tool name is required.", nameof(entry));
        if (string.IsNullOrWhiteSpace(entry.Action)) throw new ArgumentException("An action is required.", nameof(entry));

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
        command.Parameters.AddWithValue("$detail", JsonSerializer.Serialize(entry.Counts ?? new Dictionary<string, int>()));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<SiteOperation> Recent(string? tool, int count)
    {
        if (count <= 0) return [];
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

    private static Dictionary<string, int> ReadCounts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private void Migrate()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        using (var ledger = connection.CreateCommand())
        {
            ledger.Transaction = transaction;
            ledger.CommandText = """
                CREATE TABLE IF NOT EXISTS inventory_migrations (
                    migration_id TEXT PRIMARY KEY,
                    completed_at TEXT NOT NULL
                );
                SELECT COUNT(*) FROM inventory_migrations WHERE migration_id = $id;
                """;
            ledger.Parameters.AddWithValue("$id", MigrationId);
            if (Convert.ToInt64(ledger.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                transaction.Commit();
                return;
            }
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
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 30000;";
        command.ExecuteNonQuery();
        return connection;
    }
}
