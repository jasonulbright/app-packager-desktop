using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests;

/// <summary>A scan service that records the request and answers with what the test set up.</summary>
internal sealed class FakeDiscoveryService : IDiscoveryService
{
    public DiscoveryRequest? Request { get; private set; }
    public int Calls { get; private set; }
    public Func<DiscoveryRequest, CancellationToken, Task<DiscoveryResult>> Answer { get; set; } =
        (_, _) => Task.FromResult(new DiscoveryResult([], 0, null, "No inventory data."));

    public Task<DiscoveryResult> ScanAsync(DiscoveryRequest request, CancellationToken ct)
    {
        Request = request;
        Calls++;
        return Answer(request, ct);
    }
}

internal sealed class FakeDiscoveryPrompt(Action<DiscoveryDialogViewModel> act) : IDiscoveryPrompt
{
    public DiscoveryDialogViewModel? Shown { get; private set; }

    public DiscoveryChoice Show(DiscoveryDialogViewModel viewModel)
    {
        Shown = viewModel;
        act(viewModel);
        return viewModel.Choice;
    }
}

/// <summary>Settings kept as JSON, so each load returns a new object as the real store does.</summary>
internal sealed class InMemorySettings
{
    private string _json;
    public int Saves { get; private set; }

    public InMemorySettings(AppSettings initial) => _json = JsonSerializer.Serialize(initial);

    public AppSettings Load() => JsonSerializer.Deserialize<AppSettings>(_json)!;

    public void Save(AppSettings settings)
    {
        _json = JsonSerializer.Serialize(settings);
        Saves++;
    }
}

public sealed class DiscoveryPreferenceTests
{
    [Fact]
    public void NewSettingsHaveEmptyListsAndNoOverrides()
    {
        var s = new AppSettings();
        Assert.Empty(s.DiscoveryDismissed);
        Assert.Empty(s.DiscoveryEnabled);
        Assert.Equal("", s.DiscoverySqlServer);
        Assert.Equal("", s.DiscoverySqlDatabase);
    }

    [Fact]
    public void EnableRemovesTheIdsFromTheHiddenListWithoutRegardToCaseAndKeepsTheRest()
    {
        var s = new AppSettings { HiddenApplications = ["git", "GrepWin", "gsudo"] };
        var removed = DiscoveryPreferences.Enable(s, ["grepwin", "GSUDO"]);
        Assert.Equal(2, removed);
        Assert.Equal(["git"], s.HiddenApplications);
    }

    [Fact]
    public void EnablingTheLastHiddenIdLeavesAnEmptyArray()
    {
        var s = new AppSettings { HiddenApplications = ["grepwin"] };
        DiscoveryPreferences.Enable(s, ["grepwin"]);
        Assert.NotNull(s.HiddenApplications);
        Assert.Empty(s.HiddenApplications);
    }

    [Fact]
    public void EnableRecordsEachEnabledIdOnceAndLeavesTheDismissedList()
    {
        var s = new AppSettings { HiddenApplications = ["a", "b"], DiscoveryEnabled = ["a"], DiscoveryDismissed = ["b", "c"] };
        DiscoveryPreferences.Enable(s, ["a", "b", "b"]);
        Assert.Equal(["a", "b"], s.DiscoveryEnabled);
        Assert.Equal(["c"], s.DiscoveryDismissed);
    }

    [Fact]
    public void DismissAddsEachIdOnceAndLeavesTheEnabledList()
    {
        var s = new AppSettings { DiscoveryDismissed = ["a"], DiscoveryEnabled = ["b", "c"] };
        var added = DiscoveryPreferences.Dismiss(s, ["A", "b", "b", " "]);
        Assert.Equal(1, added);
        Assert.Equal(["a", "b"], s.DiscoveryDismissed);
        Assert.Equal(["c"], s.DiscoveryEnabled);
    }

    [Fact]
    public void NormalizeTrimsDropsBlanksAndRepeatsAndKeepsAnIdInBothListsDismissed()
    {
        var s = new AppSettings
        {
            DiscoveryDismissed = [" a ", "A", "", "b"],
            DiscoveryEnabled = ["b", "c", "C"],
            DiscoverySqlServer = " cm01 ",
            DiscoverySqlDatabase = null!
        };
        DiscoveryPreferences.Normalize(s);
        Assert.Equal(["a", "b"], s.DiscoveryDismissed);
        Assert.Equal(["c"], s.DiscoveryEnabled);
        Assert.Equal("cm01", s.DiscoverySqlServer);
        Assert.Equal("", s.DiscoverySqlDatabase);
    }

    [Fact]
    public void AnImportedHiddenListLeavesOutTheIdsDiscoverEnabled()
    {
        var s = new AppSettings { DiscoveryEnabled = ["GrepWin"] };
        Assert.Equal(["git"], DiscoveryPreferences.WithoutEnabled(["git", "grepwin"], s));
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DiscoveryPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-discover-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved = [];

    public DiscoveryPersistenceTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, SettingsService.LauncherSiteCodeVariable, SettingsService.LauncherProviderVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, null);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void TheListsAndTheOverridesSurviveASaveAndALoad()
    {
        var service = new SettingsService();
        var s = service.Load();
        s.DiscoveryDismissed = ["msedge"];
        s.DiscoveryEnabled = ["grepwin", "gsudo"];
        s.DiscoverySqlServer = "cm01.contoso.com";
        s.DiscoverySqlDatabase = "CM_MCM";
        service.Save(s);

        var loaded = new SettingsService().Load();
        Assert.Equal(["msedge"], loaded.DiscoveryDismissed);
        Assert.Equal(["grepwin", "gsudo"], loaded.DiscoveryEnabled);
        Assert.Equal("cm01.contoso.com", loaded.DiscoverySqlServer);
        Assert.Equal("CM_MCM", loaded.DiscoverySqlDatabase);
    }

    [Fact]
    public void AFileWithoutTheFieldsLoadsEmptyListsAndAFileWithNullsToo()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsService.SettingsPath)!);
        File.WriteAllText(SettingsService.SettingsPath, """{ "SiteCode": "MCM" }""");
        var plain = new SettingsService().Load();
        Assert.Empty(plain.DiscoveryDismissed);
        Assert.Empty(plain.DiscoveryEnabled);

        File.WriteAllText(SettingsService.SettingsPath,
            """{ "SiteCode": "MCM", "DiscoveryDismissed": null, "DiscoveryEnabled": null, "DiscoverySqlServer": null, "HiddenApplications": null }""");
        var nulls = new SettingsService().Load();
        Assert.Empty(nulls.DiscoveryDismissed);
        Assert.Empty(nulls.DiscoveryEnabled);
        Assert.Empty(nulls.HiddenApplications);
        Assert.Equal("", nulls.DiscoverySqlServer);
    }

    [Fact]
    public void ALoadTrimsTheOverridesAndKeepsTheListsDisjoint()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsService.SettingsPath)!);
        File.WriteAllText(SettingsService.SettingsPath,
            """{ "DiscoveryDismissed": ["a", "a", ""], "DiscoveryEnabled": ["a", "b"], "DiscoverySqlServer": " cm01 ", "DiscoverySqlDatabase": " CM_MCM " }""");
        var s = new SettingsService().Load();
        Assert.Equal(["a"], s.DiscoveryDismissed);
        Assert.Equal(["b"], s.DiscoveryEnabled);
        Assert.Equal("cm01", s.DiscoverySqlServer);
        Assert.Equal("CM_MCM", s.DiscoverySqlDatabase);
    }

    [Fact]
    public void TheFirstRunHiddenListImportDoesNotHideAnApplicationDiscoverEnabled()
    {
        var packagers = Path.Combine(_root, "suite", "Packagers");
        Directory.CreateDirectory(packagers);
        File.WriteAllText(Path.Combine(_root, "suite", "AppPackager.preferences.json"),
            """{ "HiddenApplications": ["git", "grepwin", "gsudo"] }""");

        var service = new SettingsService();
        var s = service.Load();
        s.CompanyName = "";
        s.PackagersRoot = packagers;
        s.DiscoveryEnabled = ["GrepWin"];
        service.Save(s);

        var migrated = new PackagerPreferencesService(service, new PackagerDiscoveryService(service)).MigrateFromPs1IfNeeded();

        Assert.True(migrated);
        Assert.Equal(["git", "gsudo"], new SettingsService().Load().HiddenApplications);
    }

    [Fact]
    public void TheFirstRunImportStillHidesEveryApplicationWhenDiscoverEnabledNone()
    {
        var packagers = Path.Combine(_root, "suite", "Packagers");
        Directory.CreateDirectory(packagers);
        File.WriteAllText(Path.Combine(_root, "suite", "AppPackager.preferences.json"),
            """{ "HiddenApplications": ["git", "grepwin"] }""");

        var service = new SettingsService();
        var s = service.Load();
        s.PackagersRoot = packagers;
        service.Save(s);

        new PackagerPreferencesService(service, new PackagerDiscoveryService(service)).MigrateFromPs1IfNeeded();

        Assert.Equal(["git", "grepwin"], new SettingsService().Load().HiddenApplications);
    }

    [Fact]
    public void TheServiceRegistrationBindsTheInterfaceToTheEngineDiscoveryService()
    {
        using var provider = new ServiceCollection().AddPackagerServices().BuildServiceProvider();
        var service = provider.GetRequiredService<IDiscoveryService>();
        Assert.Equal("ApplicationPackagerEngine.Services.DiscoveryService", service.GetType().FullName);
    }
}

public sealed class DiscoveryDialogViewModelTests
{
    private static DiscoveryCandidate Candidate(string id, int clients, string application = "", string cmName = "") =>
        new(id, cmName.Length > 0 ? cmName : id + " CM", "Vendor " + id, application, clients);

    [Theory]
    [InlineData(1, "Discovered 1 app. Enable console view?")]
    [InlineData(2, "Discovered 2 apps. Enable console view?")]
    [InlineData(16, "Discovered 16 apps. Enable console view?")]
    public void ThePromptReadsTheCount(int count, string expected)
    {
        var vm = new DiscoveryDialogViewModel(Enumerable.Range(0, count).Select(i => Candidate("id" + i, 1, "App " + i)), "status");
        Assert.Equal(expected, vm.PromptText);
    }

    [Fact]
    public void RowsCarryTheApplicationVendorAndClientCountMostClientsFirst()
    {
        var vm = new DiscoveryDialogViewModel([Candidate("grepwin", 1, "grepWin"), Candidate("msedge", 4, "", "Microsoft Edge")],
            "4 clients in inventory.");
        Assert.Equal(["msedge", "grepwin"], vm.Rows.Select(r => r.Id));
        Assert.Equal("Microsoft Edge", vm.Rows[0].Name);
        Assert.Equal("grepWin", vm.Rows[1].Name);
        Assert.Equal(4, vm.Rows[0].ClientCount);
        Assert.Equal("Vendor grepwin", vm.Rows[1].Vendor);
        Assert.Equal("4 clients in inventory.", vm.StatusText);
        Assert.All(vm.Rows, r => Assert.False(r.Selected));
    }

    private static (DiscoveryDialogViewModel Vm, Func<int> Closes) Dialog()
    {
        var vm = new DiscoveryDialogViewModel([Candidate("a", 3, "A"), Candidate("b", 2, "B"), Candidate("c", 1, "C")], "");
        var closes = 0;
        vm.CloseRequested += (_, _) => closes++;
        return (vm, () => closes);
    }

    [Fact]
    public void EnableSelectedReturnsOnlyTheCheckedIds()
    {
        var (vm, closes) = Dialog();
        vm.Rows[1].Selected = true;
        vm.EnableSelectedCommand.Execute(null);
        Assert.Equal(DiscoveryAction.EnableSelected, vm.Choice.Action);
        Assert.Equal(["b"], vm.Choice.Ids);
        Assert.Equal(1, closes());
    }

    [Fact]
    public void EnableAllReturnsEveryIdWhetherCheckedOrNot()
    {
        var (vm, closes) = Dialog();
        vm.Rows[0].Selected = true;
        vm.EnableAllCommand.Execute(null);
        Assert.Equal(DiscoveryAction.EnableAll, vm.Choice.Action);
        Assert.Equal(["a", "b", "c"], vm.Choice.Ids);
        Assert.Equal(1, closes());
    }

    [Fact]
    public void DismissReturnsOnlyTheCheckedIds()
    {
        var (vm, _) = Dialog();
        vm.Rows[0].Selected = true;
        vm.Rows[2].Selected = true;
        vm.DismissSelectedCommand.Execute(null);
        Assert.Equal(DiscoveryAction.Dismiss, vm.Choice.Action);
        Assert.Equal(["a", "c"], vm.Choice.Ids);
    }

    [Fact]
    public void DismissWithNothingCheckedKeepsThePromptOpenChangesNothingAndSaysSo()
    {
        var (vm, closes) = Dialog();
        vm.DismissSelectedCommand.Execute(null);
        Assert.Equal(0, closes());
        Assert.Equal(DiscoveryAction.NotNow, vm.Choice.Action);
        Assert.Equal(DiscoveryDialogViewModel.NothingCheckedToDismiss, vm.NoteText);
        Assert.True(vm.HasNote);
    }

    [Fact]
    public void EnableSelectedWithNothingCheckedKeepsThePromptOpenAndSaysSo()
    {
        var (vm, closes) = Dialog();
        vm.EnableSelectedCommand.Execute(null);
        Assert.Equal(0, closes());
        Assert.Equal(DiscoveryDialogViewModel.NothingCheckedToEnable, vm.NoteText);
    }

    [Fact]
    public void NotNowClosesWithoutAChange()
    {
        var (vm, closes) = Dialog();
        vm.Rows[0].Selected = true;
        vm.NotNowCommand.Execute(null);
        Assert.Equal(1, closes());
        Assert.Equal(DiscoveryAction.NotNow, vm.Choice.Action);
        Assert.Empty(vm.Choice.Ids);
    }
}

public sealed class DiscoveryRunnerTests
{
    private static readonly DiscoveryPackager GrepWin = new("grepwin", "Stefan Kueng", "grepWin", "grepWin", "grepWin*", null, "x64");

    private static DiscoveryRunner Runner(FakeDiscoveryService service, InMemorySettings store,
        Func<CancellationToken, Task<IReadOnlyList<CmSiteDrive>>>? drives = null, string? launcher = null, TimeSpan? limit = null) =>
        new(service, store.Load, store.Save,
            _ => Task.FromResult<IReadOnlyList<DiscoveryPackager>>([GrepWin]),
            drives ?? (_ => Task.FromResult<IReadOnlyList<CmSiteDrive>>([])),
            () => launcher,
            limit);

    private static AppSettings Configured() => new()
    {
        SiteCode = "MCM",
        ProviderMachine = "cm01.contoso.com",
        Systems = new SystemsInUse { ConfigMgr = true },
        HiddenApplications = ["grepwin", "gsudo", "git"],
        DiscoveryDismissed = ["git"]
    };

    [Fact]
    public void DiscoverIsAvailableWithASiteCodeAndNoConsole()
    {
        var (enabled, _) = DiscoveryRunner.Availability(new AppSettings { SiteCode = "MCM", Systems = new SystemsInUse { ConfigMgr = true } });
        Assert.True(enabled);
    }

    [Fact]
    public void DiscoverIsUnavailableWithoutASiteCodeOrBothOverridesAndSaysWhatToSet()
    {
        var (enabled, tip) = DiscoveryRunner.Availability(new AppSettings { SiteCode = "", DiscoverySqlServer = "cm01", Systems = new SystemsInUse { ConfigMgr = true } });
        Assert.False(enabled);
        Assert.Contains("Set the site code, or the SQL server and database", tip);
    }

    [Fact]
    public void DiscoverIsAvailableWithoutASiteCodeWhenBothOverridesAreSet()
    {
        var (enabled, _) = DiscoveryRunner.Availability(new AppSettings
        {
            SiteCode = "", DiscoverySqlServer = "cm01", DiscoverySqlDatabase = "CM_MCM", Systems = new SystemsInUse { ConfigMgr = true }
        });
        Assert.True(enabled);
    }

    [Fact]
    public void DiscoverIsUnavailableWhenConfigMgrIsNotInUse()
    {
        var (enabled, tip) = DiscoveryRunner.Availability(new AppSettings { SiteCode = "MCM", Systems = new SystemsInUse { ConfigMgr = false } });
        Assert.False(enabled);
        Assert.Contains("ConfigMgr is not in use", tip);
    }

    [Fact]
    public void TheStoredProviderWinsTrimmed()
    {
        Assert.Equal("cm01", DiscoveryRunner.ResolveProviderName(" cm01 ", "launcher01", "MCM", [new CmSiteDrive("MCM", "drive01")]));
    }

    [Fact]
    public void TheLauncherProviderComesBeforeTheSiteDrives()
    {
        Assert.Equal("launcher01", DiscoveryRunner.ResolveProviderName("", "launcher01", "MCM", [new CmSiteDrive("MCM", "drive01")]));
    }

    [Fact]
    public void TheDriveNamedForTheSiteCodeComesBeforeTheFirstDrive()
    {
        var drives = new[] { new CmSiteDrive("PS1", "other01"), new CmSiteDrive("mcm", "cm01.contoso.com") };
        Assert.Equal("cm01.contoso.com", DiscoveryRunner.ResolveProviderName("", null, "MCM", drives));
    }

    [Fact]
    public void TheFirstDriveIsUsedWhenNoDriveHasTheSiteCode()
    {
        Assert.Equal("other01", DiscoveryRunner.ResolveProviderName("", null, "MCM", [new CmSiteDrive("PS1", "other01")]));
    }

    [Fact]
    public void TheProviderIsEmptyWhenNothingNamesOne()
    {
        Assert.Equal("", DiscoveryRunner.ResolveProviderName(null, null, "MCM", []));
    }

    [Fact]
    public void TheDriveOutputParsesNameAndRoot()
    {
        var drives = DiscoveryRunner.ParseDrives("noise\r\nDRIVE|MCM|cm01.contoso.com\r\nDRIVE|bad\r\n");
        Assert.Equal([new CmSiteDrive("MCM", "cm01.contoso.com")], drives);
    }

    [Fact]
    public async Task TheDrivesAreReadOnlyWhenNeitherThePreferenceNorTheLauncherNamesAProvider()
    {
        var reads = 0;
        Task<IReadOnlyList<CmSiteDrive>> Drives(CancellationToken _) { reads++; return Task.FromResult<IReadOnlyList<CmSiteDrive>>([new CmSiteDrive("MCM", "cm01")]); }
        var store = new InMemorySettings(Configured());
        var runner = Runner(new FakeDiscoveryService(), store, Drives);

        Assert.Equal("cm01.contoso.com", await runner.ResolveProviderNameAsync(store.Load(), CancellationToken.None));
        Assert.Equal(0, reads);

        var empty = store.Load();
        empty.ProviderMachine = "";
        Assert.Equal("cm01", await runner.ResolveProviderNameAsync(empty, CancellationToken.None));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task AFailedDriveReadGivesAnEmptyProvider()
    {
        var settings = Configured();
        settings.ProviderMachine = "";
        var runner = Runner(new FakeDiscoveryService(), new InMemorySettings(settings), _ => throw new InvalidOperationException("no module"));
        Assert.Equal("", await runner.ResolveProviderNameAsync(settings, CancellationToken.None));
    }

    [Fact]
    public async Task TheScanPassesTheSiteProviderOverridesListsPackagersAndTimeLimit()
    {
        var settings = Configured();
        settings.DiscoverySqlServer = "sql01";
        settings.DiscoverySqlDatabase = "CM_LAB";
        var service = new FakeDiscoveryService();

        await Runner(service, new InMemorySettings(settings)).ScanAsync(CancellationToken.None);

        var request = service.Request!;
        Assert.Equal("MCM", request.SiteCode);
        Assert.Equal("cm01.contoso.com", request.ProviderMachineName);
        Assert.Equal("sql01", request.ServerOverride);
        Assert.Equal("CM_LAB", request.DatabaseOverride);
        Assert.Equal(["grepwin", "gsudo", "git"], request.HiddenIds);
        Assert.Equal(["git"], request.DismissedIds);
        Assert.Equal([GrepWin], request.Packagers);
        Assert.Equal(600, request.TimeoutSeconds);
    }

    [Fact]
    public void TheDefaultTimeLimitIsSixHundredSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(600), Runner(new FakeDiscoveryService(), new InMemorySettings(Configured())).TimeLimit);
    }

    [Fact]
    public async Task TheScanRunsOffTheCallingThread()
    {
        using var release = new ManualResetEventSlim();
        var service = new FakeDiscoveryService
        {
            Answer = (_, _) =>
            {
                release.Wait(TimeSpan.FromSeconds(10));
                return Task.FromResult(new DiscoveryResult([], 1, null, "done"));
            }
        };
        var scan = Runner(service, new InMemorySettings(Configured())).ScanAsync(CancellationToken.None);
        Assert.False(scan.IsCompleted);
        release.Set();
        Assert.True((await scan).Ok);
    }

    [Fact]
    public async Task AScanThatOverrunsTheTimeLimitStopsWithAPlainMessageEvenWhenTheServiceIgnoresItsToken()
    {
        var service = new FakeDiscoveryService { Answer = (_, _) => new TaskCompletionSource<DiscoveryResult>().Task };
        var outcome = await Runner(service, new InMemorySettings(Configured()), limit: TimeSpan.FromMilliseconds(200)).ScanAsync(CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.False(outcome.Cancelled);
        Assert.Contains("did not finish within", outcome.Message);
        Assert.Contains("was stopped", outcome.Message);
    }

    [Fact]
    public async Task ClosingTheWindowStopsARunningScan()
    {
        var started = new TaskCompletionSource();
        var service = new FakeDiscoveryService
        {
            Answer = (_, _) => { started.SetResult(); return new TaskCompletionSource<DiscoveryResult>().Task; }
        };
        using var close = new CancellationTokenSource();
        var scan = Runner(service, new InMemorySettings(Configured())).ScanAsync(close.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        close.Cancel();
        var outcome = await scan.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(outcome.Cancelled);
        Assert.Equal("the query was stopped.", outcome.Message);
    }

    [Fact]
    public async Task ADiscoveryFailureKeepsItsPlainMessage()
    {
        const string message = "The SQL Server denied the Windows login. Ask for db_datareader on the site database.";
        var service = new FakeDiscoveryService { Answer = (_, _) => throw new DiscoveryException(message) };
        var outcome = await Runner(service, new InMemorySettings(Configured())).ScanAsync(CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Equal(message, outcome.Message);
    }

    [Fact]
    public async Task AnUnexpectedFailureShowsNoExceptionText()
    {
        var service = new FakeDiscoveryService { Answer = (_, _) => throw new InvalidOperationException("at System.Data.SqlClient.Secret line 42") };
        var outcome = await Runner(service, new InMemorySettings(Configured())).ScanAsync(CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.DoesNotContain("SqlClient", outcome.Message);
        Assert.DoesNotContain("line 42", outcome.Message);
    }

    [Fact]
    public async Task AnUnavailableDiscoverDoesNotCallTheService()
    {
        var settings = Configured();
        settings.SiteCode = "";
        var service = new FakeDiscoveryService();
        var outcome = await Runner(service, new InMemorySettings(settings)).ScanAsync(CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Equal(0, service.Calls);
        Assert.StartsWith("Set the site code", outcome.Message);
    }

    private static DiscoveryResult TwoCandidates() => new(
        [new DiscoveryCandidate("grepwin", "grepWin", "Stefan Kueng", "grepWin", 1),
         new DiscoveryCandidate("gsudo", "gsudo", "Gerardo Grignoli", "gsudo", 1)],
        4, new DateTime(2026, 10, 17), "Inventory from 4 clients, newest 2026-10-17. 2 candidates.");

    private sealed class Recorder
    {
        public List<string> Log { get; } = [];
        public List<string> Status { get; } = [];
        public int Reloads { get; set; }
        public int SettingsChanges { get; set; }
    }

    private static Task<DiscoveryChoice?> Run(DiscoveryRunner runner, IDiscoveryPrompt prompt, Recorder r) =>
        runner.RunAsync(prompt, r.Log.Add, r.Status.Add, () => { r.Reloads++; return Task.CompletedTask; }, () => r.SettingsChanges++, CancellationToken.None);

    [Fact]
    public async Task NoCandidateShowsTheStatusAndNoPrompt()
    {
        var store = new InMemorySettings(Configured());
        var service = new FakeDiscoveryService { Answer = (_, _) => Task.FromResult(new DiscoveryResult([], 0, null, "No inventory data.")) };
        var prompt = new FakeDiscoveryPrompt(_ => throw new InvalidOperationException("no prompt expected"));
        var r = new Recorder();

        Assert.Null(await Run(Runner(service, store), prompt, r));
        Assert.Null(prompt.Shown);
        Assert.Equal("No inventory data.", r.Status[^1]);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task AFailedScanShowsThePlainMessageAndNoPrompt()
    {
        var service = new FakeDiscoveryService { Answer = (_, _) => throw new DiscoveryException("The SQL Server could not be reached.") };
        var prompt = new FakeDiscoveryPrompt(_ => throw new InvalidOperationException("no prompt expected"));
        var r = new Recorder();

        await Run(Runner(service, new InMemorySettings(Configured())), prompt, r);

        Assert.Equal("Discover unmanaged software: The SQL Server could not be reached.", r.Status[^1]);
        Assert.Contains("Discover unmanaged software: The SQL Server could not be reached.", r.Log);
    }

    [Fact]
    public async Task EnableSelectedSavesTheSettingsReloadsTheGridAndReportsTheCount()
    {
        var store = new InMemorySettings(Configured());
        var service = new FakeDiscoveryService { Answer = (_, _) => Task.FromResult(TwoCandidates()) };
        var prompt = new FakeDiscoveryPrompt(vm => { vm.Rows.Single(x => x.Id == "gsudo").Selected = true; vm.EnableSelectedCommand.Execute(null); });
        var r = new Recorder();

        await Run(Runner(service, store), prompt, r);

        var saved = store.Load();
        Assert.Equal(["grepwin", "git"], saved.HiddenApplications);
        Assert.Equal(["gsudo"], saved.DiscoveryEnabled);
        Assert.Equal(1, r.Reloads);
        Assert.Equal(1, r.SettingsChanges);
        Assert.Equal("1 app(s) added to the grid.", r.Status[^1]);
        Assert.Equal("Discovered 2 apps. Enable console view?", prompt.Shown!.PromptText);
        Assert.Equal(TwoCandidates().StatusText, prompt.Shown.StatusText);
    }

    [Fact]
    public async Task EnableAllEnablesEveryOfferedApplication()
    {
        var store = new InMemorySettings(Configured());
        var service = new FakeDiscoveryService { Answer = (_, _) => Task.FromResult(TwoCandidates()) };
        var prompt = new FakeDiscoveryPrompt(vm => vm.EnableAllCommand.Execute(null));

        await Run(Runner(service, store), prompt, new Recorder());

        Assert.Equal(["git"], store.Load().HiddenApplications);
        Assert.Equal(["grepwin", "gsudo"], store.Load().DiscoveryEnabled);
    }

    [Fact]
    public async Task DismissSavesOnlyTheCheckedApplicationsAndLeavesTheGrid()
    {
        var store = new InMemorySettings(Configured());
        var service = new FakeDiscoveryService { Answer = (_, _) => Task.FromResult(TwoCandidates()) };
        var prompt = new FakeDiscoveryPrompt(vm => { vm.Rows.Single(x => x.Id == "grepwin").Selected = true; vm.DismissSelectedCommand.Execute(null); });
        var r = new Recorder();

        await Run(Runner(service, store), prompt, r);

        var saved = store.Load();
        Assert.Equal(["git", "grepwin"], saved.DiscoveryDismissed);
        Assert.Equal(["grepwin", "gsudo", "git"], saved.HiddenApplications);
        Assert.Equal(0, r.Reloads);
        Assert.Contains(r.Log, line => line.Contains("1 app(s) will not be offered again"));
    }

    [Fact]
    public async Task NotNowChangesNothing()
    {
        var store = new InMemorySettings(Configured());
        var service = new FakeDiscoveryService { Answer = (_, _) => Task.FromResult(TwoCandidates()) };
        var prompt = new FakeDiscoveryPrompt(vm => vm.NotNowCommand.Execute(null));
        var r = new Recorder();

        await Run(Runner(service, store), prompt, r);

        Assert.Equal(0, store.Saves);
        Assert.Equal(0, r.Reloads);
        Assert.Equal(0, r.SettingsChanges);
        Assert.Contains("Discover unmanaged software: no change.", r.Log);
    }

    [Fact]
    public async Task AnUnsavableSettingsFileReportsAPlainMessage()
    {
        var store = new InMemorySettings(Configured());
        var service = new FakeDiscoveryService { Answer = (_, _) => Task.FromResult(TwoCandidates()) };
        var runner = new DiscoveryRunner(service, store.Load, _ => throw new IOException("disk full at C:\\secret"),
            _ => Task.FromResult<IReadOnlyList<DiscoveryPackager>>([]), _ => Task.FromResult<IReadOnlyList<CmSiteDrive>>([]), () => null);
        var prompt = new FakeDiscoveryPrompt(vm => vm.EnableAllCommand.Execute(null));
        var r = new Recorder();

        await Run(runner, prompt, r);

        Assert.Equal("Discover unmanaged software: the settings could not be saved. Nothing changed.", r.Status[^1]);
        Assert.DoesNotContain(r.Log, line => line.Contains("secret"));
        Assert.Equal(0, r.Reloads);
    }

    private static DiscoveryRunner UnreadableRunner(FakeDiscoveryService service, InMemorySettings store) =>
        new(service, store.Load, store.Save,
            _ => Task.FromResult<IReadOnlyList<DiscoveryPackager>>([GrepWin]),
            _ => Task.FromResult<IReadOnlyList<CmSiteDrive>>([]),
            () => null, settingsUnreadable: () => true);

    [Theory]
    [InlineData("enableAll")]
    [InlineData("enableSelected")]
    [InlineData("dismiss")]
    public async Task AnUnreadableSettingsFileChangesNothingAndSaysSo(string button)
    {
        var store = new InMemorySettings(Configured());
        var service = new FakeDiscoveryService { Answer = (_, _) => Task.FromResult(TwoCandidates()) };
        var prompt = new FakeDiscoveryPrompt(vm =>
        {
            vm.Rows[0].Selected = true;
            (button switch { "enableAll" => vm.EnableAllCommand, "enableSelected" => vm.EnableSelectedCommand, _ => vm.DismissSelectedCommand }).Execute(null);
        });
        var r = new Recorder();

        await Run(UnreadableRunner(service, store), prompt, r);

        Assert.Equal(0, store.Saves);
        Assert.Equal(0, r.Reloads);
        Assert.Equal(0, r.SettingsChanges);
        Assert.Equal("Discover unmanaged software: the settings could not be saved. Nothing changed.", r.Status[^1]);
        Assert.Contains("Discover unmanaged software: the settings could not be saved. Nothing changed.", r.Log);
        Assert.DoesNotContain(r.Log, line => line.Contains("added") || line.Contains("offered again"));
    }

    [Fact]
    public async Task TheDrivesAreNotReadWhenBothSqlOverridesAreSet()
    {
        var reads = 0;
        var settings = Configured();
        settings.ProviderMachine = "";
        settings.DiscoverySqlServer = "sql01";
        settings.DiscoverySqlDatabase = "CM_LAB";
        var runner = Runner(new FakeDiscoveryService(), new InMemorySettings(settings),
            _ => { reads++; return Task.FromResult<IReadOnlyList<CmSiteDrive>>([new CmSiteDrive("MCM", "cm01")]); });

        Assert.Equal("", await runner.ResolveProviderNameAsync(settings, CancellationToken.None));
        Assert.Equal(0, reads);

        settings.DiscoverySqlDatabase = " ";
        Assert.Equal("cm01", await runner.ResolveProviderNameAsync(settings, CancellationToken.None));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task APromptFailureReportsAPlainMessageAndChangesNothing()
    {
        var store = new InMemorySettings(Configured());
        var service = new FakeDiscoveryService { Answer = (_, _) => Task.FromResult(TwoCandidates()) };
        var prompt = new FakeDiscoveryPrompt(_ => throw new InvalidOperationException("owner window at C:\\secret"));
        var r = new Recorder();

        Assert.Null(await Run(Runner(service, store), prompt, r));

        Assert.Equal(0, store.Saves);
        Assert.Equal("Discover unmanaged software: the prompt could not be shown. Nothing changed.", r.Status[^1]);
        Assert.Contains("Discover unmanaged software: the prompt could not be shown. Nothing changed.", r.Log);
        Assert.DoesNotContain(r.Log.Concat(r.Status), line => line.Contains("secret"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARefreshFailureAfterTheSaveReportsAPlainMessage(bool reloadFails)
    {
        var store = new InMemorySettings(Configured());
        var service = new FakeDiscoveryService { Answer = (_, _) => Task.FromResult(TwoCandidates()) };
        var prompt = new FakeDiscoveryPrompt(vm => vm.EnableAllCommand.Execute(null));
        var log = new List<string>();
        var status = new List<string>();

        var choice = await Runner(service, store).RunAsync(prompt, log.Add, status.Add,
            () => reloadFails ? throw new IOException("grid at C:\\secret") : Task.CompletedTask,
            () => { if (!reloadFails) throw new InvalidOperationException("page at C:\\secret"); },
            CancellationToken.None);

        Assert.Equal(DiscoveryAction.EnableAll, choice!.Action);
        Assert.Equal(1, store.Saves);
        Assert.Equal("Discover unmanaged software: the choice was saved, but the lists could not be refreshed. Select Catalog on the App Packager page to see them.", status[^1]);
        Assert.Contains(status[^1], log);
        Assert.DoesNotContain(log.Concat(status), line => line.Contains("secret"));
    }

    [Fact]
    public async Task CancellationDuringTheRefreshIsNotReportedAsAFailure()
    {
        var store = new InMemorySettings(Configured());
        var service = new FakeDiscoveryService { Answer = (_, _) => Task.FromResult(TwoCandidates()) };
        var prompt = new FakeDiscoveryPrompt(vm => vm.EnableAllCommand.Execute(null));
        await Assert.ThrowsAsync<OperationCanceledException>(() => Runner(service, store).RunAsync(prompt, _ => { }, _ => { },
            () => throw new OperationCanceledException(), () => { }, CancellationToken.None));
    }
}

public sealed class DiscoveryReloadTests
{
    [Fact]
    public async Task AFailedReloadIsLoggedWithoutTheExceptionText()
    {
        var log = new List<string>();
        await CatalogViewModel.ReloadLoggedAsync(() => throw new IOException("C:\\secret"), log.Add);
        Assert.Equal(["Catalog: the list could not be reloaded. Open Catalog again to retry."], log);
    }

    [Fact]
    public async Task AFailedAsynchronousReloadIsLogged()
    {
        var log = new List<string>();
        await CatalogViewModel.ReloadLoggedAsync(async () => { await Task.Yield(); throw new InvalidOperationException("C:\\secret"); }, log.Add);
        Assert.Single(log);
        Assert.DoesNotContain("secret", log[0]);
    }

    [Fact]
    public async Task ASuccessfulReloadLogsNothing()
    {
        var log = new List<string>();
        await CatalogViewModel.ReloadLoggedAsync(() => Task.CompletedTask, log.Add);
        Assert.Empty(log);
    }
}

public sealed class CatalogSaveTests
{
    private readonly Queue<Task<IReadOnlyList<PackagerInfo>?>> _loads = new();
    private readonly List<string> _saved = [];
    private readonly List<string> _log = [];
    private int _loadCalls;

    private static IReadOnlyList<PackagerInfo> Packagers() =>
    [
        new PackagerInfo { Uid = "git", Vendor = "Git", Application = "Git", Script = "git.json", IsJsonDefined = true },
        new PackagerInfo { Uid = "grepwin", Vendor = "Stefan Kueng", Application = "grepWin", Script = "grepwin.json", IsJsonDefined = true },
        new PackagerInfo { Uid = "gsudo", Vendor = "Gerardo Grignoli", Application = "gsudo", Script = "gsudo.json", IsJsonDefined = true },
    ];

    private CatalogViewModel Page(InMemorySettings store) => new(new CatalogServices(
        store.Load,
        store.Save,
        () => { _loadCalls++; return _loads.Dequeue(); },
        () => Task.CompletedTask,
        _saved.Add,
        _log.Add));

    private TaskCompletionSource<IReadOnlyList<PackagerInfo>?> PendingLoad()
    {
        var load = new TaskCompletionSource<IReadOnlyList<PackagerInfo>?>();
        _loads.Enqueue(load.Task);
        return load;
    }

    private static bool Visible(CatalogViewModel page, string uid) =>
        page.VendorGroups.SelectMany(g => g.Apps).Single(a => a.Uid == uid).IsVisible;

    private static void SetHidden(InMemorySettings store, params string[] hidden)
    {
        var s = store.Load();
        s.HiddenApplications = hidden;
        store.Save(s);
    }

    [Fact]
    public async Task ASaveDuringTheReloadWaitsAndWritesTheReloadedRows()
    {
        var store = new InMemorySettings(new AppSettings { HiddenApplications = ["grepwin", "gsudo"] });
        _loads.Enqueue(Task.FromResult<IReadOnlyList<PackagerInfo>?>(Packagers()));
        var page = Page(store);
        Assert.False(Visible(page, "grepwin"));

        SetHidden(store, "gsudo");
        var reload = PendingLoad();
        page.ReloadAfterSettingsChange();
        var savesBefore = store.Saves;

        var save = page.SaveCommand.ExecuteAsync(null);
        Assert.False(save.IsCompleted);
        Assert.Equal(savesBefore, store.Saves);

        reload.SetResult(Packagers());
        await save;

        Assert.True(Visible(page, "grepwin"));
        Assert.Equal(["gsudo"], store.Load().HiddenApplications);
        Assert.Equal(["Saved. 2 of 3 visible."], _saved);
    }

    [Fact]
    public async Task TwoQuickChangesSaveTheRowsOfTheLastReload()
    {
        var store = new InMemorySettings(new AppSettings { HiddenApplications = ["grepwin", "gsudo"] });
        _loads.Enqueue(Task.FromResult<IReadOnlyList<PackagerInfo>?>(Packagers()));
        var page = Page(store);

        SetHidden(store, "gsudo");
        var first = PendingLoad();
        page.ReloadAfterSettingsChange();
        SetHidden(store);
        var second = PendingLoad();
        page.ReloadAfterSettingsChange();

        var save = page.SaveCommand.ExecuteAsync(null);
        first.SetResult(Packagers());
        Assert.False(save.IsCompleted);

        second.SetResult(Packagers());
        await save;

        Assert.True(Visible(page, "grepwin"));
        Assert.True(Visible(page, "gsudo"));
        Assert.Empty(store.Load().HiddenApplications);
        Assert.Equal(3, _loadCalls);
    }

    [Fact]
    public async Task AReloadThatFailsKeepsTheRowsAndTheSaveProceeds()
    {
        var store = new InMemorySettings(new AppSettings { HiddenApplications = ["grepwin"] });
        _loads.Enqueue(Task.FromResult<IReadOnlyList<PackagerInfo>?>(Packagers()));
        var page = Page(store);

        var reload = PendingLoad();
        page.ReloadAfterSettingsChange();
        var save = page.SaveCommand.ExecuteAsync(null);
        Assert.False(save.IsCompleted);

        reload.SetException(new IOException("C:\\secret"));
        await save.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(Visible(page, "grepwin"));
        Assert.Equal(["grepwin"], store.Load().HiddenApplications);
        Assert.Equal(["Saved. 2 of 3 visible."], _saved);
        Assert.Equal(["Catalog: the list could not be reloaded. Open Catalog again to retry."], _log);
    }

    [Fact]
    public async Task ASaveBeforeTheFirstLoadEndsDoesNotShowEveryApplication()
    {
        var store = new InMemorySettings(new AppSettings { HiddenApplications = ["grepwin"] });
        var initial = PendingLoad();
        var page = Page(store);

        var save = page.SaveCommand.ExecuteAsync(null);
        initial.SetResult(Packagers());
        await save;

        Assert.Equal(["grepwin"], store.Load().HiddenApplications);
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class CatalogWithoutPackagersFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-apps-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved = [];

    public CatalogWithoutPackagersFolderTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, ApplicationPackagerEngine.Services.Workbench.WorkbenchPaths.DataRootVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
        Environment.SetEnvironmentVariable(ApplicationPackagerEngine.Services.Workbench.WorkbenchPaths.DataRootVariable, null);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task ThePageListsTheShippedAndUserDefinitionsWhenNoPackagersFolderResolves()
    {
        var discovery = new PackagerDiscoveryService(new SettingsService());
        Assert.Null(discovery.ResolvePackagersRoot());
        Directory.CreateDirectory(AppDataPaths.UserDefinitionsRoot);
        File.WriteAllText(Path.Combine(AppDataPaths.UserDefinitionsRoot, "labtool.json"),
            """{ "Id": "labtool", "Vendor": "Lab", "Application": "Lab Tool" }""");
        var store = new InMemorySettings(new AppSettings { HiddenApplications = ["labtool"] });
        var shown = new List<string>();

        var page = new CatalogViewModel(new CatalogServices(
            store.Load, store.Save, CatalogViewModel.LoadPackagersFrom(discovery), () => Task.CompletedTask, shown.Add, _ => { }));
        await page.SaveCommand.ExecuteAsync(null);

        var apps = page.VendorGroups.SelectMany(g => g.Apps).ToList();
        Assert.Contains(apps, a => a.Uid == "562e5eb1a64c4669" && a.IsVisible);
        Assert.False(apps.Single(a => a.Uid == "labtool").IsVisible);
        Assert.True(page.TotalCount > 1);
        Assert.Equal(page.TotalCount - 1, page.VisibleCount);
        Assert.Equal(["labtool"], store.Load().HiddenApplications);
    }
}

public sealed class PackagesGridLoadTests
{
    private static List<PackagerInfo> Rows(params string[] ids) =>
        ids.Select(id => new PackagerInfo { Uid = id, Vendor = "V", Application = id, IsJsonDefined = true }).ToList();

    [Fact]
    public async Task WithEveryApplicationHiddenTheGridIsEmptyAndTheLogSaysSoWithoutAnError()
    {
        var load = await PackagesViewModel.ReadGridAsync(() => Task.FromResult(Rows()), () => Task.FromResult(Rows("git", "gsudo")));

        Assert.Empty(load.Rows);
        Assert.Equal("All applications are hidden. Select Catalog to show applications.", load.StatusText);
        Assert.Equal("All applications are hidden. Select Catalog to show applications.", load.LogLine);
    }

    [Fact]
    public async Task WithNoDefinitionAtAllTheLogReportsTheMissingDefinitions()
    {
        var load = await PackagesViewModel.ReadGridAsync(() => Task.FromResult(Rows()), () => Task.FromResult(Rows()));

        Assert.Empty(load.Rows);
        Assert.Equal("No applications found. Check the Definitions folder beside the executable or set a Packagers folder in Settings.", load.StatusText);
        Assert.Equal("ERROR: no definitions beside the executable and no packagers folder.", load.LogLine);
    }

    [Fact]
    public async Task VisibleRowsAreShownWithoutAMessageAndWithoutTheFullRead()
    {
        var fullReads = 0;
        var load = await PackagesViewModel.ReadGridAsync(() => Task.FromResult(Rows("git")), () => { fullReads++; return Task.FromResult(Rows("git", "gsudo")); });

        Assert.Equal(["git"], load.Rows.Select(r => r.Uid));
        Assert.Null(load.StatusText);
        Assert.Null(load.LogLine);
        Assert.Equal(0, fullReads);
    }
}

public sealed class DiscoverySettingsEditorTests
{
    [Fact]
    public void TheFieldsLoadAndSaveTrimmed()
    {
        var editor = new DiscoverySettingsEditor();
        editor.Load(new AppSettings { DiscoverySqlServer = "cm01", DiscoverySqlDatabase = "CM_MCM", DiscoveryDismissed = ["a", "b"] });
        Assert.Equal("cm01", editor.SqlServer);
        Assert.Equal("CM_MCM", editor.SqlDatabase);
        Assert.Equal("2 app(s)", editor.DismissedSummary);

        editor.SqlServer = " sql01\\cm ";
        editor.SqlDatabase = " CM_LAB ";
        var target = new AppSettings { DiscoveryDismissed = ["a", "b"], DiscoveryEnabled = ["c"] };
        editor.ApplyTo(target);
        Assert.Equal("sql01\\cm", target.DiscoverySqlServer);
        Assert.Equal("CM_LAB", target.DiscoverySqlDatabase);
        Assert.Equal(["a", "b"], target.DiscoveryDismissed);
        Assert.Equal(["c"], target.DiscoveryEnabled);
    }

    [Fact]
    public void ClearListEmptiesTheDismissedListOnSaveOnly()
    {
        var editor = new DiscoverySettingsEditor();
        editor.Load(new AppSettings { DiscoveryDismissed = ["a"] });
        Assert.True(editor.ClearDismissedCommand.CanExecute(null));

        editor.ClearDismissedCommand.Execute(null);
        Assert.Equal("0 app(s) after Save", editor.DismissedSummary);
        Assert.False(editor.ClearDismissedCommand.CanExecute(null));

        var target = new AppSettings { DiscoveryDismissed = ["a"], DiscoveryEnabled = ["c"] };
        editor.ApplyTo(target);
        Assert.Empty(target.DiscoveryDismissed);
        Assert.Equal(["c"], target.DiscoveryEnabled);
        Assert.Equal("0 app(s)", editor.DismissedSummary);
    }

    [Fact]
    public void ClearListIsUnavailableWithAnEmptyList()
    {
        var editor = new DiscoverySettingsEditor();
        editor.Load(new AppSettings());
        Assert.False(editor.ClearDismissedCommand.CanExecute(null));
    }
}

public sealed class DiscoveryPackagerSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apkg-discover-defs-" + Guid.NewGuid().ToString("N"));

    public DiscoveryPackagerSourceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private AppDefinition Definition(string id, string vendor, string json)
    {
        var path = Path.Combine(_dir, id + ".json");
        File.WriteAllText(path, json);
        return new AppDefinition { Id = id, Vendor = vendor, Application = id + " app", CMName = "", SourcePath = path };
    }

    [Fact]
    public void ThePackagersCarryTheAddRemoveProgramsTagsOfTheirDefinitionFile()
    {
        var packagers = DiscoveryPackagerSource.FromDefinitions(
        [
            Definition("corretto", "Amazon", """{ "Id": "corretto", "ArpName": "Amazon Corretto*", "ArpVersion": "17.*", "ArpArch": "x64" }"""),
            Definition("portable", "Vendor", """{ "Id": "portable", "ArpName": "none" }"""),
            Definition("untagged", "Vendor", """{ "Id": "untagged" }"""),
            Definition("novendor", "", """{ "Id": "novendor", "ArpName": "x*" }""")
        ]);

        Assert.Equal(["corretto", "portable", "untagged"], packagers.Select(p => p.Id));
        Assert.Equal(new DiscoveryPackager("corretto", "Amazon", "corretto app", "corretto app", "Amazon Corretto*", "17.*", "x64"), packagers[0]);
        Assert.Equal("none", packagers[1].ArpName);
        Assert.Null(packagers[1].ArpVersion);
        Assert.Equal("", packagers[2].ArpName);
    }

    [Fact]
    public void AnUnreadableDefinitionFileGivesEmptyTags()
    {
        var definition = Definition("broken", "Vendor", "{ not json");
        var packager = Assert.Single(DiscoveryPackagerSource.FromDefinitions([definition]));
        Assert.Equal("", packager.ArpName);
    }

    [Fact]
    public void ABrokenDefinitionFileIsSkippedAndTheGoodOnesAreStillOffered()
    {
        var log = new List<string>();
        var packagers = DiscoveryPackagerSource.FromDefinitions(
        [
            Definition("grepwin", "Stefan Kueng", """{ "Id": "grepwin", "ArpName": "grepWin*" }"""),
            Definition("zoom", "Zoom", """{ "Id": "zoom", "ArpName": "Zoom*", "InstallerType": "EXE", "InstallerType": "MSI" }"""),
            Definition("cased", "Vendor", """{ "Id": "cased", "ArpName": "Cased*", "arpname": "Other*" }"""),
            Definition("broken", "Vendor", """{ "Id": "broken", "ArpName": "Broken*" """),
            Definition("gsudo", "Gerardo Grignoli", """{ "Id": "gsudo", "ArpName": "gsudo*" }""")
        ], log.Add);

        Assert.Equal(["", "", ""], packagers.Where(p => p.Id is "zoom" or "cased" or "broken").Select(p => p.ArpName));
        Assert.Equal(
        [
            "Discover unmanaged software: the definition file zoom.json could not be read and is skipped.",
            "Discover unmanaged software: the definition file cased.json could not be read and is skipped.",
            "Discover unmanaged software: the definition file broken.json could not be read and is skipped."
        ], log);

        string[] ids = ["grepwin", "zoom", "cased", "broken", "gsudo"];
        var rows = new[] { "grepWin 2.1", "Zoom Workplace", "Cased Tool", "Broken Tool", "gsudo v2" }
            .Select((name, i) => new InventoryRow(name, "", "1.0", "", new HashSet<int> { i + 1 }))
            .ToList();
        var candidates = DiscoveryMatcher.FindCandidates(new InventorySnapshot(rows, 5, null, []), DiscoveryMatcher.Prepare(packagers, ids, []));
        Assert.Equal(["grepwin", "gsudo"], candidates.Select(c => c.Id).Order());
    }
}
