using ApplicationPackagerEngine.Services;

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
}
