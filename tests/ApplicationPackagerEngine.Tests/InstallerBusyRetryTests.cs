using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A bootstrapper layout step that lands while another Windows Installer
/// operation holds the mutex exits 1618. The staging run waits and tries
/// again instead of failing the stage on a transient condition.
/// </summary>
public sealed class InstallerBusyRetryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-busy-" + Guid.NewGuid().ToString("N"));

    public InstallerBusyRetryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NoDelay = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task BusyThenFree_ReturnsTheFinalExitCode()
    {
        var marker = Path.Combine(_dir, "second-run");
        var log = new List<string>();
        // First run leaves the marker and reports busy; every later run finds it and succeeds.
        var script = $"/c if exist \"{marker}\" (exit 0) else (echo x> \"{marker}\" & exit {StagingService.InstallerBusyExitCode})";

        var exit = await StagingService.RunInstallerProcessAsync("cmd.exe", script, Timeout, CancellationToken.None,
            log.Add, busyRetries: 6, busyDelay: NoDelay);

        Assert.Equal(0, exit);
        Assert.Single(log);
        Assert.Contains("retry 1 of 6", log[0]);
    }

    [Fact]
    public async Task StillBusyAfterTheLastRetry_ReturnsBusy()
    {
        var log = new List<string>();

        var exit = await StagingService.RunInstallerProcessAsync("cmd.exe", $"/c exit {StagingService.InstallerBusyExitCode}",
            Timeout, CancellationToken.None, log.Add, busyRetries: 2, busyDelay: NoDelay);

        Assert.Equal(StagingService.InstallerBusyExitCode, exit);
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public async Task OtherFailures_AreNotRetried()
    {
        var log = new List<string>();

        var exit = await StagingService.RunInstallerProcessAsync("cmd.exe", "/c exit 2002",
            Timeout, CancellationToken.None, log.Add, busyRetries: 6, busyDelay: NoDelay);

        Assert.Equal(2002, exit);
        Assert.Empty(log);
    }
}
