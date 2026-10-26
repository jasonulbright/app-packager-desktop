using System.Diagnostics;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using static ApplicationPackagerEngine.Tests.InstallerAnalysis.InstallerAnalysisTestFiles;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>Session owners, the clean at start through the real sandbox, and the bounded stop at exit.</summary>
public sealed class InstallerAnalysisSessionTests : IDisposable
{
    private readonly InstallerAnalysisTempFolder _temp = new();
    private readonly RecordingAnalysisLog _log = new();

    public void Dispose() => _temp.Dispose();

    private static void Age(string folder, TimeSpan age)
    {
        var then = DateTime.UtcNow - age;
        Directory.SetCreationTimeUtc(folder, then);
        Directory.SetLastWriteTimeUtc(folder, then);
    }

    private async Task RestartAsync(string root, Func<int, long, bool> isAlive)
    {
        var lifetime = new InstallerAnalysisSandboxLifetime(new AnalysisSandbox(root), _log, isAlive);
        await lifetime.StartAsync(CancellationToken.None);
        await lifetime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AtStartACrashedSessionIsRemovedAndASessionOfAnotherRunningInstanceIsKeptWhateverItsAge()
    {
        var root = _temp.PathOf("Temp");
        var crashedRun = new AnalysisSandbox(root);
        var crashed = crashedRun.NewSession();
        File.WriteAllText(Path.Combine(crashed, "left.bin"), "x");
        InstallerAnalysisSessions.WriteOwner(crashed, processId: 111, startTicks: 1);
        var live = crashedRun.NewSession();
        InstallerAnalysisSessions.WriteOwner(live, processId: 222, startTicks: 2);
        Age(live, TimeSpan.FromHours(5));
        var unmarkedOld = crashedRun.NewSession();
        Age(unmarkedOld, TimeSpan.FromHours(2));
        var unmarkedYoung = crashedRun.NewSession();
        var notASession = Directory.CreateDirectory(Path.Combine(root, "keep-me")).FullName;
        Age(notASession, TimeSpan.FromHours(5));

        await RestartAsync(root, (pid, ticks) => pid == 222 && ticks == 2);

        Assert.False(Directory.Exists(crashed));
        Assert.True(Directory.Exists(live));
        Assert.False(Directory.Exists(unmarkedOld));
        Assert.True(Directory.Exists(unmarkedYoung));
        Assert.True(Directory.Exists(notASession));
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task ASessionThePageCreatedSurvivesARestartWhileThisProcessRunsAndGoesOnceTheOwnerIsGone()
    {
        var root = _temp.PathOf("Temp");
        var sandbox = new AnalysisSandbox(root);
        var installer = WriteZip(_temp.PathOf("bundle.zip"), ("app/contoso.exe", Text("MZ contoso")));
        var viewModel = new InstallerAnalysisViewModel(new InstallerAnalysisPageServices(
            Analysis: new InstallerAnalysisService(sandbox, () => null), Sandbox: sandbox, Reports: new FakeInstallerReportWriter(_temp.PathOf("reports")),
            Prompts: new FakeInstallerAnalysisPrompts(), LoadPreferences: () => null, SavePreferences: _ => { }, Log: _log, Limits: AnalysisLimits.Default,
            SandboxRoot: () => sandbox.Root));
        await viewModel.OnNavigatedToAsync();
        await viewModel.OpenAsync(installer);
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.PayloadTab;
        await Wait.UntilAsync(() => viewModel.PayloadRows.Count > 0 && !viewModel.IsBusy, "the payload");
        await viewModel.ExtractToTempCommand.ExecuteAsync(viewModel.PayloadRows.Single(r => r.Name == "contoso.exe"));
        var session = Assert.Single(Directory.GetDirectories(root));
        Age(session, TimeSpan.FromHours(3));

        await RestartAsync(root, InstallerAnalysisSessions.IsAlive);
        Assert.True(Directory.Exists(session));
        Assert.Single(Directory.GetFiles(session, "contoso.exe", SearchOption.AllDirectories));

        await RestartAsync(root, (_, _) => false);
        Assert.False(Directory.Exists(session));
    }

    [Fact]
    public void TheOwnerOfThisProcessIsAliveAndAnUnknownOrRestartedProcessIsNot()
    {
        using var self = Process.GetCurrentProcess();
        var ticks = self.StartTime.ToUniversalTime().Ticks;
        Assert.True(InstallerAnalysisSessions.IsAlive(Environment.ProcessId, ticks));
        Assert.False(InstallerAnalysisSessions.IsAlive(Environment.ProcessId, ticks - TimeSpan.TicksPerHour));
        Assert.False(InstallerAnalysisSessions.IsAlive(int.MaxValue - 7, ticks));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a marker")]
    [InlineData("12 abc")]
    [InlineData("-1 5")]
    public void AnUnreadableOwnerFileGivesNoOwner(string content)
    {
        var session = Directory.CreateDirectory(_temp.PathOf("s")).FullName;
        File.WriteAllText(Path.Combine(session, InstallerAnalysisSessions.OwnerFileName), content);
        Assert.Null(InstallerAnalysisSessions.ReadOwner(session));
    }

    [Fact]
    public void TheStopAtExitRunsOffTheDispatcherAndFinishes()
    {
        StaTestHelper.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            var released = false;
            var finished = HostShutdown.StopAndWait(async ct =>
            {
                await Task.Delay(20, ct);
                released = true;
            }, TimeSpan.FromSeconds(10));
            Assert.True(finished);
            Assert.True(released);
        });
    }

    [Fact]
    public void AStopThatNeverEndsIsAbandonedAfterTheTimeout()
    {
        var clock = Stopwatch.StartNew();
        CancellationToken seen = default;
        using var started = new ManualResetEventSlim();
        var finished = HostShutdown.StopAndWait(ct =>
        {
            seen = ct;
            started.Set();
            return Task.Delay(Timeout.Infinite, CancellationToken.None);
        }, TimeSpan.FromMilliseconds(200));
        Assert.False(finished);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"The stop took {clock.Elapsed}.");
        Assert.True(started.Wait(TimeSpan.FromSeconds(30)), "The stop did not start.");
        Assert.True(seen.IsCancellationRequested);
        Assert.False(HostShutdown.StopAndWait(_ => throw new InvalidOperationException(), TimeSpan.FromSeconds(1)));
    }
}
