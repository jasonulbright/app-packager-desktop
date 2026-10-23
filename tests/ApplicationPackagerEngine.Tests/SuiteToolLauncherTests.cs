using System.Data.SQLite;
using System.Diagnostics;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests;

internal sealed class FakeSuiteFileSystem : ISuiteFileSystem
{
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _texts = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Reads { get; } = [];

    /// <summary>When set, every existence probe waits for it, up to five seconds.</summary>
    public ManualResetEventSlim? ProbeGate { get; set; }

    public List<int> ProbeThreads { get; } = [];

    public FakeSuiteFileSystem Add(string path, string? text = null)
    {
        _files.Add(Path.GetFullPath(path));
        if (text is not null) _texts[Path.GetFullPath(path)] = text;
        return this;
    }

    public bool FileExists(string path)
    {
        ProbeGate?.Wait(TimeSpan.FromSeconds(5));
        lock (ProbeThreads) ProbeThreads.Add(Environment.CurrentManagedThreadId);
        return _files.Contains(Path.GetFullPath(path));
    }

    public string? ReadText(string path)
    {
        Reads.Add(path);
        return _texts.TryGetValue(Path.GetFullPath(path), out var text) ? text : null;
    }
}

internal sealed class FakeSuiteEnvironment : ISuiteEnvironment
{
    public string LocalApplicationData { get; set; } = @"C:\Users\op\AppData\Local";
    public string WindowsPowerShellPath { get; set; } = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
    public string? Registered { get; set; }
    public string? RegisteredInstallLocation() => Registered;
}

internal sealed class FakeToolProcess : IToolProcess, IDisposable
{
    public required int Id { get; init; }
    public bool HasExited { get; set; }
    public int FrontRequests { get; private set; }
    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;

    /// <summary>The answers of successive BringToFront calls; the last one repeats.</summary>
    public Queue<bool> FrontResults { get; } = new();

    /// <summary>The exit code the process stops with at once, or null while it keeps running.</summary>
    public int? ExitsAtOnceWith { get; set; }
    public List<TimeSpan> ExitWaits { get; } = [];

    public bool BringToFront()
    {
        FrontRequests++;
        if (FrontResults.Count > 1) return FrontResults.Dequeue();
        return FrontResults.Count == 0 || FrontResults.Peek();
    }

    public Task<int?> WaitForExitAsync(TimeSpan limit)
    {
        ExitWaits.Add(limit);
        if (ExitsAtOnceWith is not null) HasExited = true;
        return Task.FromResult(ExitsAtOnceWith);
    }
}

internal sealed class FakeToolProcessStarter : IToolProcessStarter
{
    private int _nextId = 1000;
    public List<ProcessStartInfo> Started { get; } = [];
    public List<FakeToolProcess> Processes { get; } = [];
    public Exception? Failure { get; set; }
    public int? NextExitsAtOnceWith { get; set; }

    public IToolProcess Start(ProcessStartInfo startInfo)
    {
        if (Failure is not null) throw Failure;
        Started.Add(startInfo);
        var process = new FakeToolProcess { Id = _nextId++, ExitsAtOnceWith = NextExitsAtOnceWith };
        Processes.Add(process);
        return process;
    }
}

internal sealed class RecordingSuiteLog : IAppLog
{
    public List<AppLogEntry> Entries { get; } = [];
    public string? SessionFile => null;
    public AppLogLevel MinimumLevel { get; set; } = AppLogLevel.Debug;
    public event EventHandler<AppLogEntry>? Written;
    public void RegisterSecret(string value) { }
    public void Write(AppLogLevel level, string area, string message)
    {
        var entry = new AppLogEntry(DateTimeOffset.UtcNow, level, area, message);
        lock (Entries) Entries.Add(entry);
        Written?.Invoke(this, entry);
    }
    public IReadOnlyList<AppLogEntry> Tail() { lock (Entries) return Entries.ToList(); }
}

public sealed class SuiteToolLauncherTests
{
    private const string Local = @"C:\Users\op\AppData\Local";
    private const string Install = Local + @"\AppPackagerSuite";
    private const string Launcher = Install + @"\app-packager-suite\start-suite.ps1";
    private const string Settings = Install + @"\app-packager-suite\suite.settings.json";
    // A tool record for the launcher tests only; no sidebar entry or page starts it.
    private static readonly SuiteTool Helper = new("deployment-helper", "Deployment Helper", "deployment-helper", "start-deploymenthelper.ps1", "Rocket24");

    private readonly FakeSuiteFileSystem _files = new();
    private readonly FakeSuiteEnvironment _environment = new();
    private readonly FakeToolProcessStarter _starter = new();
    private readonly RecordingSuiteLog _log = new();

    private readonly List<TimeSpan> _delays = [];

    private SuiteToolLauncher Launcher_() => new(_files, _environment, _starter, _log, new SuiteToolLaunchTiming
    {
        Delay = d => { _delays.Add(d); return Task.CompletedTask; }
    });

    private void InstallSuite(string root = Install, string? settings = null)
    {
        _files.Add(Path.Combine(root, "app-packager-suite", "start-suite.ps1"));
        if (settings is not null) _files.Add(Path.Combine(root, "app-packager-suite", "suite.settings.json"), settings);
        _files.Add(_environment.WindowsPowerShellPath);
    }

    private void InstallTool(SuiteTool tool, string root = Install) => _files.Add(Path.Combine(root, tool.Folder, tool.EntryScript));

    [Fact]
    public void TheThreeSidebarToolsNameTheSuiteFoldersAndEntryScripts()
    {
        Assert.DoesNotContain(SuiteTools.All, t => t.Id == SidebarIds.DeploymentHelper || t.Folder == "deployment-helper");
        Assert.Null(typeof(SuiteTools).GetProperty("DeploymentHelper"));
        Assert.DoesNotContain(SuiteTools.All, t => t.Id == SidebarIds.CollectionCompliance);
        Assert.DoesNotContain(SuiteTools.All, t => t.Id == SidebarIds.InstallerAnalysis || t.Folder == "installer-analysis");
        Assert.DoesNotContain(SuiteTools.All, t => t.Folder == "collection-and-compliance-manager");
        Assert.Null(typeof(SuiteTools).GetProperty("CollectionAndCompliance"));
        Assert.Equal(
        [
            ("dp-content-manager", "dp-content-manager", "start-dpcontentmgr.ps1"),
            ("pivots-and-scripts", "pivots-and-scripts", "start-pivotsandscripts.ps1"),
            ("site-hygiene", "site-hygiene", "start-sitehygiene.ps1")
        ], SuiteTools.All.Select(t => (t.Id, t.Folder, t.EntryScript)));
        Assert.DoesNotContain(SuiteTools.All, t => t.Folder == "collection-manager");
        Assert.DoesNotContain(SuiteTools.All, t => t.Id == SidebarIds.DetectionTester);
        Assert.DoesNotContain(SuiteTools.All, t => t.Id == SidebarIds.MaintenanceWindows);
        Assert.DoesNotContain(SuiteTools.All, t => t.Folder == "maintenance-window-manager");
    }

    [Fact]
    public void AnInstalledToolResolvesUnderTheDefaultInstallFolder()
    {
        InstallSuite();
        InstallTool(Helper);
        var resolution = Launcher_().Resolve(Helper);
        Assert.Null(resolution.Message);
        Assert.Equal(Install + @"\deployment-helper", resolution.Location!.ToolFolder);
        Assert.Equal(Install + @"\deployment-helper\start-deploymenthelper.ps1", resolution.Location.EntryScript);
    }

    [Fact]
    public void TheRegisteredInstallLocationComesBeforeTheDefaultFolder()
    {
        _environment.Registered = @"D:\Tools\Suite";
        InstallSuite(@"D:\Tools\Suite");
        InstallSuite();
        InstallTool(Helper, @"D:\Tools\Suite");
        InstallTool(Helper);
        Assert.Equal([@"D:\Tools\Suite", Install], Launcher_().SuiteRoots());
        Assert.Equal(@"D:\Tools\Suite\deployment-helper", Launcher_().Resolve(Helper).Location!.ToolFolder);
    }

    [Fact]
    public void ARegisteredLocationWithoutTheSuiteFallsBackToTheDefaultFolder()
    {
        _environment.Registered = @"D:\Gone";
        InstallSuite();
        InstallTool(Helper);
        Assert.Equal(Install + @"\deployment-helper", Launcher_().Resolve(Helper).Location!.ToolFolder);
    }

    [Fact]
    public void AMissingSuiteNamesTheSuiteAndEveryFolderSearched()
    {
        _environment.Registered = @"D:\Gone";
        var resolution = Launcher_().Resolve(Helper);
        Assert.Null(resolution.Location);
        Assert.Equal(
            @"Deployment Helper needs AppPackager Suite, which is not installed. Looked for app-packager-suite\start-suite.ps1 in: D:\Gone; " + Install + ".",
            resolution.Message);
    }

    [Fact]
    public void AMissingToolNamesTheToolAndWhereItLooked()
    {
        InstallSuite();
        var resolution = Launcher_().Resolve(Helper);
        Assert.Null(resolution.Location);
        Assert.Equal(
            @"Deployment Helper is not in the AppPackager Suite install. Looked for start-deploymenthelper.ps1 in: " + Install + @"\deployment-helper.",
            resolution.Message);
    }

    [Fact]
    public void ToolsRootFromTheSuiteSettingsIsSearchedBeforeTheInstallFolder()
    {
        InstallSuite(settings: """{ "DarkMode": true, "ToolsRoot": "E:\\dev", "SiteCode": "" }""");
        InstallTool(Helper, @"E:\dev");
        InstallTool(Helper);
        Assert.Equal(@"E:\dev\deployment-helper", Launcher_().Resolve(Helper).Location!.ToolFolder);
    }

    [Fact]
    public void AToolMissingUnderToolsRootIsFoundInTheInstallFolderAndTheMessageListsBoth()
    {
        InstallSuite(settings: "\uFEFF{ \"ToolsRoot\": \"E:\\\\dev\" }");
        InstallTool(Helper);
        Assert.Equal(Install + @"\deployment-helper", Launcher_().Resolve(Helper).Location!.ToolFolder);

        var site = SuiteTools.All.Single(t => t.Id == "site-hygiene");
        Assert.Equal(
            @"Site Hygiene is not in the AppPackager Suite install. Looked for start-sitehygiene.ps1 in: E:\dev\site-hygiene; " + Install + @"\site-hygiene.",
            Launcher_().Resolve(site).Message);
    }

    [Fact]
    public void ARelativeToolsRootIsTakenFromTheSuiteLauncherFolder()
    {
        InstallSuite(settings: """{ "ToolsRoot": "..\\side" }""");
        InstallTool(Helper, Install + @"\side");
        Assert.Equal(Install + @"\side\deployment-helper", Launcher_().Resolve(Helper).Location!.ToolFolder);
    }

    [Theory]
    [InlineData("""{ "ToolsRoot": "" }""")]
    [InlineData("""{ "ToolsRoot": null }""")]
    [InlineData("""{ "ToolsRoot": 5 }""")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "ToolsRoot": "E:\\bad|name" }""")]
    public void AnEmptyOrUnreadableToolsRootLeavesTheInstallFolder(string settings)
    {
        InstallSuite(settings: settings);
        InstallTool(Helper);
        Assert.Equal(Install + @"\deployment-helper", Launcher_().Resolve(Helper).Location!.ToolFolder);
    }

    [Fact]
    public void TheStartInfoRunsWindowsPowerShellOnTheEntryScriptInTheToolFolder()
    {
        var location = new SuiteToolLocation(@"C:\S\deployment-helper", @"C:\S\deployment-helper\start-deploymenthelper.ps1");
        var info = Launcher_().BuildStartInfo(location, "MCM", "cm01.contoso.com");
        Assert.Equal(_environment.WindowsPowerShellPath, info.FileName);
        Assert.Equal(["-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File", location.EntryScript], info.ArgumentList);
        Assert.Equal("", info.Arguments);
        Assert.Equal(location.ToolFolder, info.WorkingDirectory);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Equal("MCM", info.Environment[SettingsService.LauncherSiteCodeVariable]);
        Assert.Equal("cm01.contoso.com", info.Environment[SettingsService.LauncherProviderVariable]);
    }

    [Fact]
    public void SiteValuesTravelOnlyAsTrimmedEnvironmentVariables()
    {
        var location = new SuiteToolLocation(@"C:\S\t", @"C:\S\t\start.ps1");
        const string site = "M'C\"M; $(x)";
        const string provider = "cm01'; Remove-Item C:\\ -Recurse #";
        var info = Launcher_().BuildStartInfo(location, "  " + site + " ", provider + "\t");
        Assert.Equal(site, info.Environment[SettingsService.LauncherSiteCodeVariable]);
        Assert.Equal(provider, info.Environment[SettingsService.LauncherProviderVariable]);
        Assert.DoesNotContain(info.ArgumentList, a => a.Contains("M'C", StringComparison.Ordinal) || a.Contains("cm01", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEnvironmentVariableNamesAreTheOnesTheSuiteReads()
    {
        Assert.Equal("SUITE_CM_SITECODE", SettingsService.LauncherSiteCodeVariable);
        Assert.Equal("SUITE_CM_PROVIDER", SettingsService.LauncherProviderVariable);
    }

    [Fact]
    public async Task LaunchStartsTheToolWithTheSiteValuesAndReportsStarted()
    {
        InstallSuite();
        InstallTool(Helper);
        var result = await Launcher_().LaunchAsync(Helper, "MCM", "cm01");
        Assert.Equal(SuiteToolLaunchOutcome.Started, result.Outcome);
        Assert.Null(result.Message);
        var info = Assert.Single(_starter.Started);
        Assert.Equal(Install + @"\deployment-helper\start-deploymenthelper.ps1", info.ArgumentList[^1]);
        Assert.Equal("MCM", info.Environment[SettingsService.LauncherSiteCodeVariable]);
    }

    [Fact]
    public async Task ASecondLaunchOfARunningToolBringsItToTheFrontInsteadOfStartingAnother()
    {
        InstallSuite();
        InstallTool(Helper);
        var launcher = Launcher_();
        await launcher.LaunchAsync(Helper, "MCM", "cm01");
        var result = await launcher.LaunchAsync(Helper, "MCM", "cm01");
        Assert.Equal(SuiteToolLaunchOutcome.BroughtToFront, result.Outcome);
        Assert.Single(_starter.Started);
        Assert.Equal(1, _starter.Processes[0].FrontRequests);
        Assert.Equal(_starter.Processes[0].Id, launcher.RunningProcessId(Helper));
    }

    [Fact]
    public async Task AToolWhoseProcessExitedIsForgottenAndStartsAgain()
    {
        InstallSuite();
        InstallTool(Helper);
        var launcher = Launcher_();
        await launcher.LaunchAsync(Helper, "MCM", "cm01");
        _starter.Processes[0].HasExited = true;
        Assert.Null(launcher.RunningProcessId(Helper));
        Assert.Equal(SuiteToolLaunchOutcome.Started, (await launcher.LaunchAsync(Helper, "MCM", "cm01")).Outcome);
        Assert.Equal(2, _starter.Started.Count);
        Assert.Equal(0, _starter.Processes[0].FrontRequests);
        Assert.Equal(_starter.Processes[1].Id, launcher.RunningProcessId(Helper));
    }

    [Fact]
    public async Task EachToolHasItsOwnSingleInstance()
    {
        InstallSuite();
        var site = SuiteTools.All.Single(t => t.Id == "site-hygiene");
        InstallTool(Helper);
        InstallTool(site);
        var launcher = Launcher_();
        await launcher.LaunchAsync(Helper, "", "");
        Assert.Equal(SuiteToolLaunchOutcome.Started, (await launcher.LaunchAsync(site, "", "")).Outcome);
        Assert.Equal(2, _starter.Started.Count);
    }

    [Fact]
    public async Task AMissingToolStartsNothingAndReturnsThePlainMessage()
    {
        InstallSuite();
        var result = await Launcher_().LaunchAsync(Helper, "MCM", "cm01");
        Assert.Equal(SuiteToolLaunchOutcome.Failed, result.Outcome);
        Assert.StartsWith("Deployment Helper is not in the AppPackager Suite install.", result.Message);
        Assert.Empty(_starter.Started);
    }

    [Fact]
    public async Task AMissingWindowsPowerShellStartsNothingAndSaysWhereItLooked()
    {
        _files.Add(Launcher);
        InstallTool(Helper);
        var result = await Launcher_().LaunchAsync(Helper, "MCM", "cm01");
        Assert.Equal(SuiteToolLaunchOutcome.Failed, result.Outcome);
        Assert.Equal($"Deployment Helper needs Windows PowerShell 5.1, which was not found at {_environment.WindowsPowerShellPath}.", result.Message);
        Assert.Empty(_starter.Started);
    }

    [Fact]
    public async Task AStartFailureBecomesAPlainMessageAndNothingIsTracked()
    {
        InstallSuite();
        InstallTool(Helper);
        _starter.Failure = new System.ComponentModel.Win32Exception(267, @"The directory name is invalid: \\fs01\tools password=hunter2");
        var launcher = Launcher_();
        var result = await launcher.LaunchAsync(Helper, "MCM", "cm01");
        Assert.Equal(SuiteToolLaunchOutcome.Failed, result.Outcome);
        Assert.Equal("Deployment Helper could not be started. The log has the details.", result.Message);
        Assert.Null(launcher.RunningProcessId(Helper));

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(AppLogLevel.Error, entry.Level);
        Assert.Contains(@"\\fs01\tools", entry.Message);
        Assert.Contains("Deployment Helper", entry.Message);
        Assert.DoesNotContain("hunter2", entry.Message);
    }

    [Fact]
    public async Task AToolThatExitsDuringTheWatchIsRemovedAndDisposedWhateverTheExitCode()
    {
        InstallSuite();
        InstallTool(Helper);
        var launcher = Launcher_();
        foreach (var code in new[] { 0, 3 })
        {
            _starter.NextExitsAtOnceWith = code;
            await launcher.LaunchAsync(Helper, "MCM", "cm01");
            var process = _starter.Processes[^1];
            Assert.True(process.Disposed, $"exit code {code}");
            process.HasExited = false;
            Assert.Null(launcher.RunningProcessId(Helper));
        }
        Assert.Equal(2, _starter.Started.Count);
    }

    [Fact]
    public async Task ABlockedPathProbeLeavesTheCallerFreeAndThePendingTaskFinishesLater()
    {
        InstallSuite();
        InstallTool(Helper);
        using var gate = new ManualResetEventSlim(false);
        _files.ProbeGate = gate;
        var launcher = Launcher_();

        var task = launcher.LaunchAsync(Helper, "MCM", "cm01");

        Assert.False(task.IsCompleted);
        gate.Set();
        Assert.Equal(SuiteToolLaunchOutcome.Started, (await task).Outcome);
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, _files.ProbeThreads);
        Assert.Single(_starter.Started);
    }

    [Fact]
    public async Task ABlockedSettingsReadLeavesTheCallerFree()
    {
        InstallSuite();
        InstallTool(Helper);
        using var gate = new ManualResetEventSlim(false);
        var readThread = 0;
        var launcher = Launcher_();

        var task = launcher.LaunchAsync(Helper, () =>
        {
            gate.Wait(TimeSpan.FromSeconds(5));
            readThread = Environment.CurrentManagedThreadId;
            return new AppSettings { SiteCode = "LAB", SiteCodeSaved = true, ProviderMachine = "cm01" };
        });

        Assert.False(task.IsCompleted);
        gate.Set();
        Assert.Equal(SuiteToolLaunchOutcome.Started, (await task).Outcome);
        Assert.NotEqual(Environment.CurrentManagedThreadId, readThread);
        Assert.Equal("LAB", Assert.Single(_starter.Started).Environment[SettingsService.LauncherSiteCodeVariable]);
    }

    [Fact]
    public async Task TwoQuickClicksDuringABlockedResolutionStartOneProcess()
    {
        InstallSuite();
        InstallTool(Helper);
        using var gate = new ManualResetEventSlim(false);
        _files.ProbeGate = gate;
        var launcher = Launcher_();

        var first = launcher.LaunchAsync(Helper, "MCM", "cm01");
        var second = launcher.LaunchAsync(Helper, "MCM", "cm01");

        Assert.False(first.IsCompleted);
        Assert.True(second.IsCompleted);
        Assert.Equal(SuiteToolLaunchOutcome.StillStarting, (await second).Outcome);
        gate.Set();
        Assert.Equal(SuiteToolLaunchOutcome.Started, (await first).Outcome);
        Assert.Single(_starter.Started);
        Assert.Equal(_starter.Processes[0].Id, launcher.RunningProcessId(Helper));
    }

    [Fact]
    public async Task AFailedResolutionClearsThePendingLaunch()
    {
        InstallSuite();
        var launcher = Launcher_();
        Assert.Equal(SuiteToolLaunchOutcome.Failed, (await launcher.LaunchAsync(Helper, "MCM", "cm01")).Outcome);
        InstallTool(Helper);
        Assert.Equal(SuiteToolLaunchOutcome.Started, (await launcher.LaunchAsync(Helper, "MCM", "cm01")).Outcome);
    }

    [Fact]
    public async Task AToolThatExitsAtOnceWithANonZeroCodeIsAPlainFailure()
    {
        InstallSuite();
        InstallTool(Helper);
        _starter.NextExitsAtOnceWith = 1;
        var launcher = Launcher_();
        var result = await launcher.LaunchAsync(Helper, "MCM", "cm01");
        Assert.Equal(SuiteToolLaunchOutcome.Failed, result.Outcome);
        Assert.Equal("Deployment Helper stopped right after it started (exit code 1).", result.Message);
        Assert.Equal([TimeSpan.FromSeconds(2)], _starter.Processes[0].ExitWaits);
        Assert.Null(launcher.RunningProcessId(Helper));
    }

    [Fact]
    public async Task AToolThatExitsAtOnceWithCodeZeroIsReportedAsStarted()
    {
        InstallSuite();
        InstallTool(Helper);
        _starter.NextExitsAtOnceWith = 0;
        var result = await Launcher_().LaunchAsync(Helper, "MCM", "cm01");
        Assert.Equal(SuiteToolLaunchOutcome.Started, result.Outcome);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task AToolThatKeepsRunningThroughTheWatchIsStarted()
    {
        InstallSuite();
        InstallTool(Helper);
        var launcher = Launcher_();
        var result = await launcher.LaunchAsync(Helper, "MCM", "cm01");
        Assert.Equal(SuiteToolLaunchOutcome.Started, result.Outcome);
        Assert.Equal([TimeSpan.FromSeconds(2)], _starter.Processes[0].ExitWaits);
        Assert.Equal(_starter.Processes[0].Id, launcher.RunningProcessId(Helper));
    }

    [Fact]
    public async Task TheStartWatchIsInjectable()
    {
        InstallSuite();
        InstallTool(Helper);
        var launcher = new SuiteToolLauncher(_files, _environment, _starter, _log, new SuiteToolLaunchTiming { StartWatch = TimeSpan.FromMilliseconds(10) });
        await launcher.LaunchAsync(Helper, "MCM", "cm01");
        Assert.Equal([TimeSpan.FromMilliseconds(10)], _starter.Processes[0].ExitWaits);
    }

    [Fact]
    public async Task AWindowThatAppearsOnALaterAttemptIsBroughtToFront()
    {
        InstallSuite();
        InstallTool(Helper);
        var launcher = Launcher_();
        await launcher.LaunchAsync(Helper, "MCM", "cm01");
        foreach (var answer in new[] { false, true }) _starter.Processes[0].FrontResults.Enqueue(answer);

        var result = await launcher.LaunchAsync(Helper, "MCM", "cm01");

        Assert.Equal(SuiteToolLaunchOutcome.BroughtToFront, result.Outcome);
        Assert.Equal(2, _starter.Processes[0].FrontRequests);
        Assert.Equal([TimeSpan.FromSeconds(0.75)], _delays);
    }

    [Fact]
    public async Task AToolWithNoWindowAfterEveryAttemptIsStillStarting()
    {
        InstallSuite();
        InstallTool(Helper);
        var launcher = Launcher_();
        await launcher.LaunchAsync(Helper, "MCM", "cm01");
        _starter.Processes[0].FrontResults.Enqueue(false);

        var result = await launcher.LaunchAsync(Helper, "MCM", "cm01");

        Assert.Equal(SuiteToolLaunchOutcome.StillStarting, result.Outcome);
        Assert.Equal("Deployment Helper is still starting. Its window is not open yet.", result.Message);
        Assert.Equal(3, _starter.Processes[0].FrontRequests);
        Assert.Equal([TimeSpan.FromSeconds(0.75), TimeSpan.FromSeconds(0.75)], _delays);
        Assert.Single(_starter.Started);
    }

    [Fact]
    public async Task TheFrontAttemptsAreInjectable()
    {
        InstallSuite();
        InstallTool(Helper);
        var delays = new List<TimeSpan>();
        var launcher = new SuiteToolLauncher(_files, _environment, _starter, _log, new SuiteToolLaunchTiming
        {
            FrontAttempts = 5,
            FrontRetryInterval = TimeSpan.FromMilliseconds(1),
            Delay = d => { delays.Add(d); return Task.CompletedTask; }
        });
        await launcher.LaunchAsync(Helper, "MCM", "cm01");
        _starter.Processes[0].FrontResults.Enqueue(false);
        Assert.Equal(SuiteToolLaunchOutcome.StillStarting, (await launcher.LaunchAsync(Helper, "MCM", "cm01")).Outcome);
        Assert.Equal(5, _starter.Processes[0].FrontRequests);
        Assert.Equal(4, delays.Count);
    }

    [Fact]
    public void TheDefaultTimingWatchesTwoSecondsAndTriesTheWindowThreeTimesOverOneAndAHalfSeconds()
    {
        var timing = new SuiteToolLaunchTiming();
        Assert.Equal(TimeSpan.FromSeconds(2), timing.StartWatch);
        Assert.Equal(3, timing.FrontAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1.5), timing.FrontRetryInterval * (timing.FrontAttempts - 1));
    }

    [Fact]
    public async Task TheLauncherOnlyReadsTheSuiteSettingsFile()
    {
        InstallSuite(settings: """{ "ToolsRoot": "" }""");
        InstallTool(Helper);
        await Launcher_().LaunchAsync(Helper, "MCM", "cm01");
        Assert.Equal([Settings], _files.Reads);
    }

    [Fact]
    public void ConstructionTouchesNothing()
    {
        _ = Launcher_();
        Assert.Empty(_files.Reads);
        Assert.Empty(_starter.Started);
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SuiteToolLauncherEnvironmentTests : IDisposable
{
    private readonly string? _site = Environment.GetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable);
    private readonly string? _provider = Environment.GetEnvironmentVariable(SettingsService.LauncherProviderVariable);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, _site);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, _provider);
    }

    private static (SuiteToolLauncher Launcher, FakeToolProcessStarter Starter) InstalledLauncher()
    {
        var files = new FakeSuiteFileSystem();
        var environment = new FakeSuiteEnvironment();
        var starter = new FakeToolProcessStarter();
        var root = Path.Combine(environment.LocalApplicationData, "AppPackagerSuite");
        files.Add(Path.Combine(root, "app-packager-suite", "start-suite.ps1"))
            .Add(environment.WindowsPowerShellPath)
            .Add(Path.Combine(root, "deployment-helper", "start-deploymenthelper.ps1"));
        return (new SuiteToolLauncher(files, environment, starter, new RecordingSuiteLog()), starter);
    }

    // A tool record for the launcher tests only; no sidebar entry or page starts it.
    private static readonly SuiteTool Helper = new("deployment-helper", "Deployment Helper", "deployment-helper", "start-deploymenthelper.ps1", "Rocket24");

    [Fact]
    public async Task ASiteCodeTheOperatorNeverSavedIsNotPassed()
    {
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, null);
        var (launcher, starter) = InstalledLauncher();
        var settings = new AppSettings { SiteCode = "MCM", SiteCodeSaved = false, ProviderMachine = "cm01.contoso.com" };
        Assert.Equal(SuiteToolLaunchOutcome.Started, (await launcher.LaunchAsync(Helper, settings)).Outcome);
        var info = Assert.Single(starter.Started);
        Assert.False(info.Environment.ContainsKey(SettingsService.LauncherSiteCodeVariable));
        Assert.Equal("cm01.contoso.com", info.Environment[SettingsService.LauncherProviderVariable]);
    }

    [Fact]
    public async Task ASavedSiteCodeIsPassedAndAnEmptyProviderIsNot()
    {
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, "old.contoso.com");
        var (launcher, starter) = InstalledLauncher();
        await launcher.LaunchAsync(Helper, new AppSettings { SiteCode = "LAB", SiteCodeSaved = true, ProviderMachine = " " });
        var info = Assert.Single(starter.Started);
        Assert.Equal("LAB", info.Environment[SettingsService.LauncherSiteCodeVariable]);
        Assert.False(info.Environment.ContainsKey(SettingsService.LauncherProviderVariable));
    }

    [Fact]
    public async Task AnUnsavedSiteCodeKeepsTheValueTheSuiteLauncherGaveTheDesktop()
    {
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, "SUI");
        var (launcher, starter) = InstalledLauncher();
        await launcher.LaunchAsync(Helper, new AppSettings { SiteCode = "SUI", SiteCodeSaved = false });
        Assert.Equal("SUI", Assert.Single(starter.Started).Environment[SettingsService.LauncherSiteCodeVariable]);
    }

    [Fact]
    public async Task ASavedSiteCodeReplacesTheValueTheSuiteLauncherGaveTheDesktop()
    {
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, "SUI");
        var (launcher, starter) = InstalledLauncher();
        await launcher.LaunchAsync(Helper, new AppSettings { SiteCode = "LAB", SiteCodeSaved = true });
        Assert.Equal("LAB", Assert.Single(starter.Started).Environment[SettingsService.LauncherSiteCodeVariable]);
    }

    [Fact]
    public void EmptyDesktopValuesRemoveTheVariablesTheDesktopInherited()
    {
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, "OLD");
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, "old.contoso.com");
        var launcher = new SuiteToolLauncher(new FakeSuiteFileSystem(), new FakeSuiteEnvironment(), new FakeToolProcessStarter(), new RecordingSuiteLog());
        var info = launcher.BuildStartInfo(new SuiteToolLocation(@"C:\S\t", @"C:\S\t\start.ps1"), " ", null);
        Assert.False(info.Environment.ContainsKey(SettingsService.LauncherSiteCodeVariable));
        Assert.False(info.Environment.ContainsKey(SettingsService.LauncherProviderVariable));
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SuiteToolLauncherRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-suitelauncher-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public SuiteToolLauncherRegistrationTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        SQLiteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TheApplicationServicesBuildOneLauncher()
    {
        using var services = new ServiceCollection().AddPackagerServices().AddSidebar()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });
        var launcher = services.GetRequiredService<SuiteToolLauncher>();
        Assert.Same(launcher, services.GetRequiredService<SuiteToolLauncher>());
    }
}

public sealed class ToolWindowActivationTests
{
    private sealed class FakeWindows : IToolWindows
    {
        public Dictionary<IntPtr, int> Owners { get; } = [];
        public HashSet<IntPtr> Minimized { get; } = [];
        public List<string> Calls { get; } = [];

        public int OwnerProcessId(IntPtr window) => Owners.TryGetValue(window, out var id) ? id : 0;
        public bool IsMinimized(IntPtr window) => Minimized.Contains(window);
        public void Restore(IntPtr window) => Calls.Add("restore " + window);
        public bool Activate(IntPtr window) { Calls.Add("activate " + window); return true; }
    }

    private static readonly IntPtr Window = new(0x1234);

    [Fact]
    public void AWindowOwnedByTheOriginalProcessIsActivated()
    {
        var windows = new FakeWindows();
        windows.Owners[Window] = 4242;
        Assert.True(ToolWindowActivation.BringToFront(4242, Window, () => false, windows));
        Assert.Equal(["activate 4660"], windows.Calls);
    }

    [Fact]
    public void AMinimizedWindowIsRestoredFirst()
    {
        var windows = new FakeWindows();
        windows.Owners[Window] = 4242;
        windows.Minimized.Add(Window);
        Assert.True(ToolWindowActivation.BringToFront(4242, Window, () => false, windows));
        Assert.Equal(["restore 4660", "activate 4660"], windows.Calls);
    }

    [Fact]
    public void AWindowOwnedByAnotherProcessIsNeverTouched()
    {
        var windows = new FakeWindows();
        windows.Owners[Window] = 5151;
        windows.Minimized.Add(Window);
        Assert.False(ToolWindowActivation.BringToFront(4242, Window, () => false, windows));
        Assert.Empty(windows.Calls);
    }

    [Fact]
    public void NoWindowOrAnExitedProcessActivatesNothing()
    {
        var windows = new FakeWindows();
        windows.Owners[Window] = 4242;
        Assert.False(ToolWindowActivation.BringToFront(4242, IntPtr.Zero, () => false, windows));
        Assert.False(ToolWindowActivation.BringToFront(4242, Window, () => true, windows));
        Assert.Empty(windows.Calls);
    }
}

public sealed class SuiteToolRegistryTests
{
    private static readonly string[] ToolIds =
    [
        "detection-tester", "maintenance-windows", "collection-and-compliance", "installer-analysis",
        "deployment-helper", "dp-content-manager", "pivots-and-scripts", "site-hygiene"
    ];

    [Fact]
    public void TheEightToolEntriesFollowTheWorkbenchInTheDefaultOrder()
    {
        var registry = SidebarRegistry.CreateDefault();
        Assert.Equal([SidebarIds.Packages, SidebarIds.Applications, SidebarIds.AddApp, SidebarIds.Workbench, .. ToolIds], SidebarFixtures.Ids(registry.Items));
        Assert.Equal(
        [
            "Detection Tester", "Maintenance Windows", "Collection and Compliance", "Installer Analysis",
            "Deployment Helper", "DP Content Manager", "Pivots and Scripts", "Site Hygiene"
        ], registry.Items.Skip(4).Select(i => i.Title));
    }

    [Fact]
    public void TheDetectionTesterEntryIsAMovablePageWithItsSavedId()
    {
        var item = SidebarRegistry.CreateDefault().Find("detection-tester")!;
        Assert.Equal(SidebarIds.DetectionTester, item.Id);
        Assert.Equal(SidebarItemKind.Page, item.Kind);
        Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.DetectionTesterPage), item.PageType);
        Assert.Null(item.Activate);
        Assert.False(item.IsPinned);
        Assert.Equal("Beaker24", item.Symbol);
    }

    [Fact]
    public void TheMaintenanceWindowsEntryIsAMovablePageWithItsSavedIdAndIcon()
    {
        var registry = SidebarRegistry.CreateDefault();
        var item = registry.Find("maintenance-windows")!;
        Assert.Equal(SidebarIds.MaintenanceWindows, item.Id);
        Assert.Equal("Maintenance Windows", item.Title);
        Assert.Equal(SidebarItemKind.Page, item.Kind);
        Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.MaintenanceWindowsPage), item.PageType);
        Assert.Null(item.Activate);
        Assert.False(item.IsPinned);
        Assert.Equal("CalendarClock24", item.Symbol);
        Assert.Equal(5, registry.Items.ToList().IndexOf(item));
    }

    [Fact]
    public void TheCollectionAndComplianceEntryIsAMovablePageWithItsSavedIdIconAndPlace()
    {
        var registry = SidebarRegistry.CreateDefault();
        var item = registry.Find("collection-and-compliance")!;
        Assert.Equal(SidebarIds.CollectionCompliance, item.Id);
        Assert.Equal("Collection and Compliance", item.Title);
        Assert.Equal(SidebarItemKind.Page, item.Kind);
        Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.CollectionCompliancePage), item.PageType);
        Assert.Null(item.Activate);
        Assert.False(item.IsPinned);
        Assert.Equal("ShieldCheckmark24", item.Symbol);
        Assert.Equal(6, registry.Items.ToList().IndexOf(item));
        Assert.DoesNotContain(registry.Items, i => i.Title == "Collection Manager");
    }

    [Fact]
    public void TheInstallerAnalysisEntryIsAMovablePageWithItsSavedIdIconAndPlace()
    {
        var registry = SidebarRegistry.CreateDefault();
        var item = registry.Find("installer-analysis")!;
        Assert.Equal(SidebarIds.InstallerAnalysis, item.Id);
        Assert.Equal("Installer Analysis", item.Title);
        Assert.Equal(SidebarItemKind.Page, item.Kind);
        Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.InstallerAnalysisPage), item.PageType);
        Assert.Null(item.Activate);
        Assert.False(item.IsPinned);
        Assert.Equal("BoxSearch24", item.Symbol);
        Assert.Equal(7, registry.Items.ToList().IndexOf(item));
    }

    [Fact]
    public void ASavedLayoutWithTheInstallerAnalysisEntryHiddenAndMovedKeepsItAsAPage()
    {
        var layout = new SidebarLayout
        {
            Order = [SidebarIds.Packages, "installer-analysis", SidebarIds.Applications, SidebarIds.AddApp, SidebarIds.Workbench, SidebarIds.DetectionTester,
                "maintenance-windows", "collection-and-compliance", "deployment-helper", "dp-content-manager", "pivots-and-scripts", "site-hygiene"],
            Hidden = ["installer-analysis"]
        };
        var service = new SidebarService(SidebarRegistry.CreateDefault(), new InMemorySidebarStore(layout));
        Assert.Equal([SidebarIds.Packages, "installer-analysis", SidebarIds.Applications], SidebarFixtures.Ids(service.All).Take(3));
        Assert.True(service.IsHidden("installer-analysis"));
        Assert.True(service.Show("installer-analysis"));
        Assert.Equal(SidebarItemKind.Page, service.Items[1].Kind);
    }

    [Fact]
    public void TheDeploymentHelperEntryIsAMovablePageWithItsSavedIdAndIcon()
    {
        var registry = SidebarRegistry.CreateDefault();
        var item = registry.Find("deployment-helper")!;
        Assert.Equal(SidebarIds.DeploymentHelper, item.Id);
        Assert.Equal(SidebarItemKind.Page, item.Kind);
        Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.DeploymentHelperPage), item.PageType);
        Assert.Null(item.Activate);
        Assert.False(item.IsPinned);
        Assert.Equal("Rocket24", item.Symbol);
        Assert.Equal(8, registry.Items.ToList().IndexOf(item));
    }

    [Fact]
    public void EveryOtherToolEntryIsAMovableToolWithAnAction()
    {
        var tools = SidebarRegistry.CreateDefault().Items.Skip(9).ToList();
        Assert.Equal(3, tools.Count);
        foreach (var item in tools)
        {
            Assert.Equal(SidebarItemKind.Tool, item.Kind);
            Assert.False(item.IsPinned);
            Assert.NotNull(item.Activate);
            Assert.Null(item.PageType);
        }
        Assert.Equal(SuiteTools.All.Select(t => t.Id), tools.Select(t => t.Id));
    }

    [Fact]
    public void ASavedLayoutWithTheDetectionTesterHiddenAndMovedKeepsItsPlace()
    {
        var layout = new SidebarLayout
        {
            Order = [SidebarIds.Packages, "site-hygiene", SidebarIds.DetectionTester, SidebarIds.Applications, SidebarIds.AddApp, SidebarIds.Workbench,
                "maintenance-windows", "collection-and-compliance", "installer-analysis", "deployment-helper", "dp-content-manager", "pivots-and-scripts"],
            Hidden = [SidebarIds.DetectionTester]
        };
        var service = new SidebarService(SidebarRegistry.CreateDefault(), new InMemorySidebarStore(layout));
        Assert.Equal([SidebarIds.Packages, "site-hygiene", SidebarIds.DetectionTester, SidebarIds.Applications], SidebarFixtures.Ids(service.All).Take(4));
        Assert.True(service.IsHidden(SidebarIds.DetectionTester));
        Assert.True(service.Show(SidebarIds.DetectionTester));
        Assert.Equal(SidebarIds.DetectionTester, service.Items[2].Id);
    }

    [Fact]
    public void OnlyPackagesIsPinned()
    {
        var registry = SidebarRegistry.CreateDefault();
        Assert.Equal([SidebarIds.Packages], registry.Items.Where(i => i.IsPinned).Select(i => i.Id));
        Assert.Equal(SidebarIds.Packages, registry.Items[0].Id);
    }

    [Fact]
    public void EveryEntryHasAUniqueIdTitleAndExistingIcon()
    {
        var items = SidebarRegistry.CreateDefault().Items;
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(items.Count, items.Select(i => i.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(items.Count, items.Select(i => i.Symbol).Distinct(StringComparer.Ordinal).Count());
        Assert.All(items, i => Assert.True(Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(i.Symbol, out _), i.Symbol));
    }

    [Fact]
    public void ToolEntriesMoveHideAndShowThroughTheService()
    {
        var service = new SidebarService(SidebarRegistry.CreateDefault(), new InMemorySidebarStore());
        Assert.True(service.Hide("site-hygiene"));
        Assert.DoesNotContain(service.Items, i => i.Id == "site-hygiene");
        Assert.True(service.Show("site-hygiene"));
        Assert.Equal("site-hygiene", service.Items[^1].Id);
        Assert.True(service.Move("detection-tester", 1));
        Assert.Equal("detection-tester", service.Items[1].Id);
        Assert.Equal(SidebarIds.Packages, service.Items[0].Id);
    }

    [Fact]
    public void ASavedLayoutFromBeforeTheToolsAppendsThemShownAtTheEnd()
    {
        var layout = new SidebarLayout { Order = [SidebarIds.Packages, SidebarIds.Workbench, SidebarIds.Applications, SidebarIds.AddApp], Hidden = [SidebarIds.AddApp] };
        var service = new SidebarService(SidebarRegistry.CreateDefault(), new InMemorySidebarStore(layout));
        Assert.Equal([SidebarIds.Packages, SidebarIds.Workbench, SidebarIds.Applications, .. ToolIds], SidebarFixtures.Ids(service.Items));
    }

    [Fact]
    public void EachToolEntryMatchesItsSuiteTool()
    {
        var registry = SidebarRegistry.CreateDefault();
        foreach (var tool in SuiteTools.All)
        {
            var item = registry.Find(tool.Id)!;
            Assert.Equal(tool.Title, item.Title);
            Assert.Equal(tool.Symbol, item.Symbol);
        }
    }
}
