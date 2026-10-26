using System.Data.SQLite;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Tests;

public sealed class SiteOperationHistoryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apsiteops-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_folder, "history.db");

    public SiteOperationHistoryTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        SQLiteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    private static SiteOperationEntry Entry(string tool, string action, DateTimeOffset at, SiteOperationOutcome outcome = SiteOperationOutcome.Succeeded) =>
        new(tool, action, "Collection SMS00001", outcome, new Dictionary<string, int> { ["changed"] = 2, ["failed"] = 0 }, at);

    [Fact]
    public void RecordAndRecentReturnTheNewestFirstForOneTool()
    {
        var history = new SiteOperationHistory(DbPath);
        history.Record(Entry("Maintenance Windows", "Add", T0));
        history.Record(Entry("Maintenance Windows", "Remove", T0.AddMinutes(1), SiteOperationOutcome.Failed));
        history.Record(Entry("DP Content Manager", "Redistribute", T0.AddMinutes(2)));

        var recent = history.Recent("Maintenance Windows", 10);
        Assert.Equal(["Remove", "Add"], recent.Select(r => r.Action));
        Assert.Equal(SiteOperationOutcome.Failed, recent[0].Outcome);
        Assert.Equal(T0.AddMinutes(1), recent[0].Time);
        Assert.Equal(TimeSpan.Zero, recent[0].Time.Offset);
        Assert.Equal("Collection SMS00001", recent[0].Target);
        Assert.Equal(2, recent[0].Counts["changed"]);
        Assert.True(recent[0].Id > 0);
    }

    [Fact]
    public void RecentWithoutAToolReturnsEveryToolAndHonoursTheCount()
    {
        var history = new SiteOperationHistory(DbPath);
        for (var i = 0; i < 5; i++) history.Record(Entry("T" + i, "A", T0.AddMinutes(i)));
        Assert.Equal(["T4", "T3"], history.Recent(null, 2).Select(r => r.Tool));
        Assert.Empty(history.Recent("T0", 0));
    }

    [Fact]
    public void ANonUtcTimeIsStoredAsUtc()
    {
        var history = new SiteOperationHistory(DbPath);
        var local = new DateTimeOffset(2026, 10, 18, 11, 0, 0, TimeSpan.FromHours(2));
        history.Record(Entry("T", "A", local));
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero), history.Recent("T", 1)[0].Time);
    }

    [Fact]
    public void DetailHoldsCountsOnlyAsJson()
    {
        var history = new SiteOperationHistory(DbPath);
        history.Record(Entry("T", "A", T0));
        using var connection = new SQLiteConnection($"Data Source={DbPath};Pooling=False;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT detail FROM SiteOperations";
        Assert.Equal("{\"changed\":2,\"failed\":0}", (string)command.ExecuteScalar()!);
    }

    [Fact]
    public void ASecretInTheTargetIsMasked()
    {
        var history = new SiteOperationHistory(DbPath);
        history.Record(new SiteOperationEntry("T", "A", "password=abc123", SiteOperationOutcome.Succeeded, new Dictionary<string, int>(), T0));
        Assert.DoesNotContain("abc123", history.Recent("T", 1)[0].Target);
    }

    [Theory]
    [InlineData("", "A")]
    [InlineData("T", " ")]
    public void ToolAndActionAreRequired(string tool, string action)
    {
        var history = new SiteOperationHistory(DbPath);
        Assert.Throws<ArgumentException>(() => history.Record(new SiteOperationEntry(tool, action, "", SiteOperationOutcome.Succeeded, new Dictionary<string, int>(), T0)));
    }

    [Fact]
    public void TheMigrationLeavesAnExistingInventoryDatabaseIntactAndRunsOnce()
    {
        var inventory = new ApplicationInventoryService(DbPath);
        inventory.BeginOperation(new InventoryOperation("op-1", "Stage", "uid-1", null, null, T0));
        var tablesBefore = Tables();
        var schemaBefore = InventorySchema();

        var history = new SiteOperationHistory(DbPath);
        history.Record(Entry("T", "A", T0));
        new SiteOperationHistory(DbPath).Record(Entry("T", "B", T0));

        Assert.Equal(tablesBefore.Append("SiteOperations").Order(StringComparer.Ordinal), Tables().Order(StringComparer.Ordinal));
        Assert.Equal(schemaBefore, InventorySchema());
        Assert.Single(inventory.GetOperations("uid-1", 10));
        Assert.Equal(2, history.Recent("T", 10).Count);
        Assert.Equal(1L, Scalar($"SELECT COUNT(*) FROM inventory_migrations WHERE migration_id = '{SiteOperationHistory.MigrationId}'"));

        // The inventory service still opens the database after the migration.
        Assert.Single(new ApplicationInventoryService(DbPath).GetOperations("uid-1", 10));
    }

    [Fact]
    public void AFreshDatabaseGetsTheTableOnFirstUseWithoutTheInventoryTables()
    {
        var history = new SiteOperationHistory(DbPath);
        Assert.False(File.Exists(DbPath));
        Assert.Empty(history.Recent(null, 1));
        var tables = Tables();
        Assert.Contains("SiteOperations", tables);
        Assert.DoesNotContain("inventory_operations", tables);
    }

    private sealed class RecordingLog : IAppLog
    {
        public List<AppLogEntry> Entries { get; } = [];
        public string? SessionFile => null;
        public AppLogLevel MinimumLevel { get; set; }
        public event EventHandler<AppLogEntry>? Written;
        public void RegisterSecret(string value) { }
        public void Write(AppLogLevel level, string area, string message)
        {
            var entry = new AppLogEntry(DateTimeOffset.UtcNow, level, area, message);
            lock (Entries) Entries.Add(entry);
            Written?.Invoke(this, entry);
        }
        public IReadOnlyList<AppLogEntry> Tail() => Entries;
    }

    private static readonly TimeSpan ShortBusy = TimeSpan.FromMilliseconds(100);

    private SQLiteConnection HoldExclusiveLock()
    {
        var connection = new SQLiteConnection($"Data Source={DbPath};Pooling=False;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "BEGIN EXCLUSIVE;";
        command.ExecuteNonQuery();
        return connection;
    }

    [Fact]
    public void TheServiceIsMadeWithoutTouchingALockedDatabaseAndRecordNeverThrows()
    {
        new SiteOperationHistory(DbPath).Record(Entry("T", "Before", T0));
        var log = new RecordingLog();
        SiteOperationHistory history;
        using (HoldExclusiveLock())
        {
            history = new SiteOperationHistory(DbPath, log, ShortBusy);
            Assert.Null(history.Record(Entry("T", "Locked", T0.AddMinutes(1))));
            Assert.Empty(history.Recent("T", 10));
        }

        var entry = Assert.Single(log.Entries, e => e.Message.Contains("could not record", StringComparison.Ordinal));
        Assert.Equal(AppLogLevel.Warning, entry.Level);
        Assert.Equal(SiteOperationHistory.LogArea, entry.Area);
        Assert.Contains("T Locked", entry.Message, StringComparison.Ordinal);
        Assert.NotNull(history.Record(Entry("T", "After", T0.AddMinutes(2))));
        Assert.Equal(["After", "Before"], history.Recent("T", 10).Select(r => r.Action));
    }

    [Fact]
    public void AFreshLockedDatabaseMigratesOnTheFirstRecordAfterTheLockEnds()
    {
        SQLiteConnection.CreateFile(DbPath);
        var log = new RecordingLog();
        var history = new SiteOperationHistory(DbPath, log, ShortBusy);
        using (HoldExclusiveLock())
            Assert.Null(history.Record(Entry("T", "Locked", T0)));
        Assert.NotNull(history.Record(Entry("T", "After", T0)));
        Assert.Equal(["After"], history.Recent("T", 10).Select(r => r.Action));
    }

    [Fact]
    public void AReadOnlyDatabaseFileLogsAndKeepsReading()
    {
        new SiteOperationHistory(DbPath).Record(Entry("T", "Before", T0));
        SQLiteConnection.ClearAllPools();
        File.SetAttributes(DbPath, FileAttributes.ReadOnly);
        try
        {
            var log = new RecordingLog();
            var history = new SiteOperationHistory(DbPath, log, ShortBusy);
            Assert.Null(history.Record(Entry("T", "ReadOnly", T0.AddMinutes(1))));
            Assert.Contains(log.Entries, e => e.Level == AppLogLevel.Warning);
            Assert.Equal(["Before"], history.Recent("T", 10).Select(r => r.Action));
        }
        finally
        {
            File.SetAttributes(DbPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public void AReadOnlyDatabaseWithoutTheTableResolvesAndReturnsNothing()
    {
        SQLiteConnection.CreateFile(DbPath);
        File.SetAttributes(DbPath, FileAttributes.ReadOnly);
        try
        {
            var log = new RecordingLog();
            var history = new SiteOperationHistory(DbPath, log, ShortBusy);
            Assert.Null(history.Record(Entry("T", "A", T0)));
            Assert.Empty(history.Recent(null, 10));
            Assert.Contains(log.Entries, e => e.Level == AppLogLevel.Warning);
        }
        finally
        {
            File.SetAttributes(DbPath, FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData("Changed")]
    [InlineData("1changed")]
    [InlineData("changed count")]
    [InlineData("")]
    [InlineData("password=abc")]
    [InlineData("a23456789012345678901234567890123")]
    public void ACountsKeyOutsideThePatternIsRejectedWhenTheEntryIsMade(string key)
    {
        var failure = Assert.Throws<ArgumentException>(() =>
            new SiteOperationEntry("T", "A", "", SiteOperationOutcome.Succeeded, new Dictionary<string, int> { [key] = 1 }, T0));
        Assert.Contains("count", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("failed_2")]
    [InlineData("a2345678901234567890123456789012")]
    public void ACountsKeyInsideThePatternIsAccepted(string key)
    {
        var entry = new SiteOperationEntry("T", "A", "", SiteOperationOutcome.Succeeded, new Dictionary<string, int> { [key] = 1 }, T0);
        Assert.Equal(1, entry.Counts[key]);
    }

    private List<string> Tables()
    {
        using var connection = new SQLiteConnection($"Data Source={DbPath};Pooling=False;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name <> 'sqlite_sequence' ORDER BY name";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private string InventorySchema()
    {
        using var connection = new SQLiteConnection($"Data Source={DbPath};Pooling=False;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(sql, ';') FROM (SELECT sql FROM sqlite_master WHERE name LIKE 'inventory%' OR name LIKE '%_inventory' ORDER BY name)";
        return Convert.ToString(command.ExecuteScalar()) ?? "";
    }

    private long Scalar(string sql)
    {
        using var connection = new SQLiteConnection($"Data Source={DbPath};Pooling=False;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
