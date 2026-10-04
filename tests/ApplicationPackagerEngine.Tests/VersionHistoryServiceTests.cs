using ApplicationPackagerEngine.Services;
using System.Data.SQLite;

namespace ApplicationPackagerEngine.Tests;

public class VersionHistoryServiceTests : IDisposable
{
    private readonly VersionHistoryService _svc;
    private readonly string _dbPath;

    public VersionHistoryServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"packr-vhist-{Guid.NewGuid():N}.db");
        _svc = new VersionHistoryService(_dbPath);
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void Record_And_GetHistory_RoundTrips()
    {
        _svc.Record(new HistoryEntry
        {
            AppId = "7zip-x64",
            AppName = "7-Zip (x64)",
            Version = "24.09",
            Operation = "Stage", Status = "Success"
        });

        var history = _svc.GetHistory("7zip-x64", 10);
        Assert.Single(history);
        Assert.Equal("24.09", history[0].Version);
        Assert.Equal("7-Zip (x64)", history[0].AppName);
    }

    [Fact]
    public void GetLastVersion_ReturnsLatest()
    {
        _svc.Record(new HistoryEntry
        {
            AppId = "npp", AppName = "Notepad++", Version = "8.6", Operation = "Stage", Status = "Success"
        });
        _svc.Record(new HistoryEntry
        {
            AppId = "npp", AppName = "Notepad++", Version = "8.7", Operation = "Stage", Status = "Success"
        });

        var last = _svc.GetLastVersion("npp");
        Assert.Equal("8.7", last);
    }

    [Fact]
    public void GetLastVersion_UnknownApp_ReturnsNull()
    {
        var last = _svc.GetLastVersion("nonexistent-app-id");
        Assert.Null(last);
    }

    [Fact]
    public void GetLatestPerApp_ReturnsOnePerApp()
    {
        _svc.Record(new HistoryEntry
        {
            AppId = "app-a", AppName = "App A", Version = "1.0", Operation = "Stage", Status = "Success"
        });
        _svc.Record(new HistoryEntry
        {
            AppId = "app-a", AppName = "App A", Version = "2.0", Operation = "Stage", Status = "Success"
        });
        _svc.Record(new HistoryEntry
        {
            AppId = "app-b", AppName = "App B", Version = "3.0", Operation = "Stage", Status = "Success"
        });

        var latest = _svc.GetLatestPerApp();
        Assert.Equal(2, latest.Count);
    }

    [Fact]
    public void GetHistory_RespectsLimit()
    {
        for (int i = 0; i < 20; i++)
        {
            _svc.Record(new HistoryEntry
            {
                AppId = "bulk", AppName = "Bulk", Version = $"{i}.0", Operation = "Stage", Status = "Success"
            });
        }

        var history = _svc.GetHistory("bulk", 5);
        Assert.Equal(5, history.Count);
    }

    [Fact]
    public void OpeningExistingDatabase_PreservesHistoryAndAllowsNewRecords()
    {
        var existingDbPath = Path.Combine(Path.GetTempPath(), $"packr-vhist-existing-{Guid.NewGuid():N}.db");

        try
        {
            using (var connection = new SQLiteConnection($"Data Source={existingDbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE history (
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
                    INSERT INTO history (
                        app_id, app_name, version, operation, status, timestamp,
                        duration_ms, detection_rule, installer_framework, error_message, metadata
                    ) VALUES (
                        'legacy-app', 'Legacy App', '4.2', 'Stage', 'Success',
                        '2025-12-01T10:15:00.0000000+00:00', 1250, 'file-detection',
                        'MSI', NULL, '{"source":"legacy"}'
                    );
                    """;
                command.ExecuteNonQuery();
            }

            using (var history = new VersionHistoryService(existingDbPath))
            {
                var existing = Assert.Single(history.GetHistory("legacy-app"));
                Assert.Equal("4.2", existing.Version);
                Assert.Equal("{\"source\":\"legacy\"}", existing.Metadata);

                history.Record(new HistoryEntry
                {
                    AppId = "legacy-app",
                    AppName = "Legacy App",
                    Version = "4.3",
                    Operation = "Stage",
                    Status = "Success"
                });

                Assert.Equal(2, history.GetHistory("legacy-app").Count);
            }
        }
        finally
        {
            try { if (File.Exists(existingDbPath)) File.Delete(existingDbPath); } catch { }
        }
    }
}
