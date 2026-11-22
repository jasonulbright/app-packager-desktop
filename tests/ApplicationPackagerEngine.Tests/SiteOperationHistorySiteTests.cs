using System.Data.SQLite;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The SiteOperations schema change of Q-112d: the site_code column, the upgrade of an existing file, and the filter by site.</summary>
public sealed class SiteOperationHistorySiteTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apsiteops-site-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_folder, "sites", "AB1", "results", "history.db");

    public SiteOperationHistorySiteTests() => Directory.CreateDirectory(Path.GetDirectoryName(Db)!);

    public void Dispose()
    {
        SQLiteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private void SeedVersionOneDatabase()
    {
        using var connection = new SQLiteConnection($"Data Source={Db};Version=3;Pooling=False;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE inventory_migrations (migration_id TEXT PRIMARY KEY, completed_at TEXT NOT NULL);
            CREATE TABLE SiteOperations (id INTEGER PRIMARY KEY AUTOINCREMENT, occurred_at TEXT NOT NULL, tool TEXT NOT NULL, action TEXT NOT NULL, target TEXT, outcome TEXT NOT NULL, detail TEXT);
            INSERT INTO inventory_migrations VALUES ('site-operations-v1', '2026-11-01T00:00:00Z');
            INSERT INTO SiteOperations(occurred_at, tool, action, target, outcome, detail) VALUES ('2026-11-20T09:00:00.0000000+00:00', 'DP Content Manager', 'Redistribute', 'old row', 'Succeeded', '{}');
            """;
        command.ExecuteNonQuery();
    }

    private static List<string> Columns(string path)
    {
        using var connection = new SQLiteConnection($"Data Source={path};Version=3;Pooling=False;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(SiteOperations)";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(1));
        return names;
    }

    [Fact]
    public void AnExistingFileGetsTheSiteColumnAndEveryOldRowBelongsToTheSiteOfItsFolder()
    {
        SeedVersionOneDatabase();
        var history = new SiteOperationHistory(Db);

        using (SiteContext.Scope("AB1", _folder))
        {
            var rows = history.Recent(null, 10);
            Assert.Equal(["old row"], rows.Select(r => r.Target));
        }

        Assert.Contains("site_code", Columns(Db));
        using var connection = new SQLiteConnection($"Data Source={Db};Version=3;Pooling=False;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT site_code FROM SiteOperations";
        Assert.Equal("AB1", command.ExecuteScalar());
    }

    [Fact]
    public void TheUpgradeRunsOnceAndAFreshFileHasTheColumnFromTheStart()
    {
        SeedVersionOneDatabase();
        var history = new SiteOperationHistory(Db);
        using (SiteContext.Scope("AB1", _folder)) history.Recent(null, 1);
        using (SiteContext.Scope("CD2", _folder)) history.Recent(null, 1);

        using var connection = new SQLiteConnection($"Data Source={Db};Version=3;Pooling=False;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM inventory_migrations WHERE migration_id = 'site-operations-v2'";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "SELECT site_code FROM SiteOperations";
        Assert.Equal("AB1", command.ExecuteScalar());

        var fresh = new SiteOperationHistory(Path.Combine(_folder, "fresh.db"));
        fresh.Record(new SiteOperationEntry("T", "A", "x", SiteOperationOutcome.Succeeded, new Dictionary<string, int>(), T0));
        Assert.Contains("site_code", Columns(Path.Combine(_folder, "fresh.db")));
    }

    [Fact]
    public void RowsCarryTheSiteCodeAndAFileHoldingTwoSitesListsOnlyTheActiveOne()
    {
        var history = new SiteOperationHistory(Db);
        using (SiteContext.Scope("AB1", _folder))
            history.Record(new SiteOperationEntry("T", "ab1 action", "x", SiteOperationOutcome.Succeeded, new Dictionary<string, int>(), T0));
        using (SiteContext.Scope("CD2", _folder))
            history.Record(new SiteOperationEntry("T", "cd2 action", "x", SiteOperationOutcome.Succeeded, new Dictionary<string, int>(), T0.AddMinutes(1)));

        using (SiteContext.Scope("AB1", _folder)) Assert.Equal(["ab1 action"], history.Recent(null, 10).Select(r => r.Action));
        using (SiteContext.Scope("CD2", _folder)) Assert.Equal(["cd2 action"], history.Recent("T", 10).Select(r => r.Action));
        // The single-site layout lists every row, as before.
        using (SiteContext.Scope(null, _folder)) Assert.Equal(2, history.Recent(null, 10).Count);
    }
}
