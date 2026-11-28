using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridRow = System.Windows.Controls.DataGridRow;
using MenuItem = System.Windows.Controls.MenuItem;
using UserControl = System.Windows.Controls.UserControl;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Overhaul V1 phase 7d (Q-126): the first-start checklist, its life and the scan record of each site.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SetupChecklistTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apd-checklist-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved;
    private ConfigMgrConnectionState _connection = ConfigMgrConnectionState.Unknown;
    private bool _busy;

    public SetupChecklistTests()
    {
        _saved = new[] { AppDataPaths.DataRootVariable, SettingsService.LauncherSiteCodeVariable, SettingsService.LauncherProviderVariable }
            .ToDictionary(n => n, Environment.GetEnvironmentVariable);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, null);
        Directory.CreateDirectory(AppDataPaths.Root);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        TestCleanup.DeleteDirectory(_root);
    }

    private SetupChecklistViewModel Checklist(SettingsService service) =>
        new(service, new DiscoveryScanStore(), () => _connection, () => _busy, () => Task.CompletedTask, () => Task.CompletedTask);

    private static void Configure(SettingsService service, string site, string provider)
    {
        var s = service.Load();
        s.SiteCode = site;
        s.ProviderMachine = provider;
        s.SiteCodeSaved = true;
        s.FileShareRoot = @"\\cm01.contoso.com\content$";
        service.Save(s);
    }

    private static void Scanned(string site, string provider, int matched) =>
        new DiscoveryScanStore().Record(new DiscoveryRunReport(site, provider, matched, DiscoveryAction.EnableSelected));

    private static SetupStepItem Step(SetupChecklistViewModel vm, SetupStepKey key) => vm.Steps.Single(s => s.Key == key);

    private static AppSettings Fresh() => new() { SiteCodeSaved = false, Systems = new SystemsInUse() };

    [Fact]
    public void ANewInstallHasOneStepDoneAndTheExampleValuesDoNotCount()
    {
        var steps = SetupChecklistRules.Steps(Fresh(), ConfigMgrConnectionState.Unknown, null);
        Assert.Equal([SetupStepKey.Systems, SetupStepKey.Site, SetupStepKey.Folders, SetupStepKey.Discovery], steps.Select(s => s.Key));
        Assert.Equal([true, false, false, false], steps.Select(s => s.IsDone));
        Assert.False(steps[1].OffersTest);
        Assert.False(SetupChecklistRules.SettingsComplete(Fresh()));
    }

    [Fact]
    public void AVisibleLineNamesTheStepMenuActionsInEveryStateThatNoMockDraws()
    {
        using var scope = SiteContext.Scope(null, _root);
        File.WriteAllText(SettingsService.SettingsPath,
            """{ "FirstRunCompleted": false, "Systems": { "ConfigMgr": true, "Intune": false, "Wsus": false }, "SiteCode": "P01", "SiteCodeSaved": true }""");
        var service = new SettingsService();
        var vm = Checklist(service);
        var told = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.MenuHint)) told++; };
        vm.Refresh();
        // The systems are chosen, the site is next and the folders are open: the window of the mock, which has no such line.
        Assert.Equal("", vm.MenuHint);
        Assert.True(told > 0);

        _connection = ConfigMgrConnectionState.Failed;
        vm.Refresh();
        Assert.StartsWith("More step actions: Open Setup, Test Connection.", vm.MenuHint);
        Assert.Contains("Shift+F10", vm.MenuHint);

        // A step beyond the mock is complete: the connection is verified.
        _connection = ConfigMgrConnectionState.Connected;
        vm.Refresh();
        Assert.StartsWith("More step actions: Open Setup.", vm.MenuHint);

        // The folders are set and the connection is not verified yet.
        _connection = ConfigMgrConnectionState.Unknown;
        var settings = service.Load();
        settings.DownloadRoot = Path.Combine(_root, "stage");
        settings.FileShareRoot = @"\\cm01.contoso.com\content$";
        service.Save(settings);
        vm.Refresh();
        Assert.Contains("Test Connection", vm.MenuHint);

        // The setup is done and the discovery is next: the state behind the other windows of the mocks.
        _connection = ConfigMgrConnectionState.Connected;
        vm.Refresh();
        Assert.Equal(4, vm.ShownSteps.Count);
        Assert.Equal("", vm.MenuHint);

        // Every step is done: the line names the actions of the finished steps.
        Scanned("P01", settings.ProviderMachine, 1);
        vm.Show();
        Assert.StartsWith("More step actions: Open Setup, Scan Again.", vm.MenuHint);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheStepMenuLineShowsBesideTheStepsOfAFailedConnection(ApplicationTheme theme)
    {
        RenderKit.WithApplication(theme, () =>
        {
            using var scope = SiteContext.Scope(null, _root);
            var (page, window) = ChecklistWindow(DesignPageWidth, DesignPageHeight);
            try
            {
                var panel = RenderKit.ById<UserControl>(page, "pnlSetupChecklist");
                var hint = RenderKit.ById<TextBlock>(panel, "txtSetupMenuHint");
                Assert.True(hint.IsVisible);
                Assert.Contains("Test Connection", hint.Text);
                AssertInsideOf(StepScroller(panel), hint, $"setup checklist {theme}");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TheSystemsSiteAndFolderStepsFollowTheSettingsAndTheConnectionTest()
    {
        var s = Fresh();
        s.SiteCode = "P01";
        s.SiteCodeSaved = true;
        s.FileShareRoot = @"\\cm01.contoso.com\content$";
        Assert.True(SetupChecklistRules.SettingsComplete(s));

        var tested = SetupChecklistRules.Steps(s, ConfigMgrConnectionState.Unknown, null);
        Assert.True(tested[1].OffersTest);
        Assert.False(tested[1].IsDone);
        Assert.True(tested[2].IsDone);
        Assert.True(SetupChecklistRules.Steps(s, ConfigMgrConnectionState.Connected, null)[1].IsDone);
        var failed = SetupChecklistRules.Steps(s, ConfigMgrConnectionState.Failed, null)[1];
        Assert.False(failed.IsDone);
        Assert.True(failed.Failed);

        s.Systems = new SystemsInUse { ConfigMgr = false };
        Assert.False(SetupChecklistRules.Steps(s, ConfigMgrConnectionState.Unknown, null)[0].IsDone);
        s.Systems = new SystemsInUse { ConfigMgr = false, Intune = true };
        var intune = SetupChecklistRules.Steps(s, ConfigMgrConnectionState.Unknown, null);
        Assert.Equal([SetupStepState.Complete, SetupStepState.NotNeeded, SetupStepState.Complete, SetupStepState.NotNeeded], intune.Select(x => x.State));
    }

    [Fact]
    public void TheDiscoveryStepFollowsTheScanRecordAndSaysWhyAScanCannotRun()
    {
        var s = Fresh();
        s.SiteCode = "";
        var blocked = SetupChecklistRules.Steps(s, ConfigMgrConnectionState.Unknown, null)[3];
        Assert.False(blocked.IsDone);
        Assert.StartsWith("Set the site code", blocked.Detail);

        s.SiteCode = "P01";
        var done = SetupChecklistRules.Steps(s, ConfigMgrConnectionState.Unknown, new DiscoveryScanRecord(DateTime.UtcNow, 1, "NotNow", false))[3];
        Assert.Equal((SetupStepState.Complete, "1 product matched."), (done.State, done.Detail));
        var skipped = SetupChecklistRules.Steps(s, ConfigMgrConnectionState.Unknown, new DiscoveryScanRecord(DateTime.UtcNow, null, null, true))[3];
        Assert.Equal(SetupStepState.Skipped, skipped.State);
        Assert.True(skipped.OffersScan);
    }

    [Fact]
    public void TheScanRecordBelongsToOneSiteAndASkipNeverReplacesAResult()
    {
        using var scope = SiteContext.Scope(null, _root);
        var store = new DiscoveryScanStore();
        store.Skip("P01", "cm01");
        Assert.True(store.Find("p01", " CM01 ")!.Skipped);
        Assert.Null(store.Find("P02", "cm02"));

        Scanned("P01", "cm01", 7);
        store.Skip("P01", "cm01");
        var record = store.Find("P01", "cm01")!;
        Assert.Equal((false, (int?)7, "EnableSelected"), (record.Skipped, record.Matched, record.Choice));
        Assert.Null(store.Find("P02", "cm02"));
    }

    [Fact]
    public void WithASiteActiveTheRecordIsInTheStateFolderOfThatSite()
    {
        using (SiteContext.Scope("AB1", _root))
        {
            Scanned("AB1", "cm.ab1", 3);
            Assert.EndsWith(Path.Combine("sites", "AB1", "state", DiscoveryScanStore.FileName), DiscoveryScanStore.ActivePath);
            Assert.True(File.Exists(DiscoveryScanStore.ActivePath));
        }
        using (SiteContext.Scope("CD2", _root))
            Assert.Null(new DiscoveryScanStore().Find("CD2", "cm.cd2"));
    }

    [Fact]
    public void ANewInstallFinishesTheStepsAndTheChecklistClosesThenANewSiteOpensTheScanAgain()
    {
        using var scope = SiteContext.Scope(null, _root);
        var service = new SettingsService();
        var vm = Checklist(service);
        vm.Refresh();
        Assert.True(vm.IsDue && vm.IsOpen);
        // The panel lists the discovery step once the three steps of a setup are done.
        Assert.Equal("1 of 3 complete", vm.ProgressText);
        Assert.Equal([SetupStepKey.Systems, SetupStepKey.Site, SetupStepKey.Folders], vm.ShownSteps.Select(s => s.Key));
        Assert.Equal("Connect a site to check for updates", vm.StripTitle);
        Assert.Equal(SetupChecklistRules.InProgress, service.Load().SetupChecklist);

        // The Setup window marks the first run as completed on Save; the checklist stays until its steps are done.
        Configure(service, "P01", "cm01.contoso.com");
        var saved = service.Load();
        saved.FirstRunCompleted = true;
        service.Save(saved);
        _connection = ConfigMgrConnectionState.Connected;
        vm.Refresh();
        Assert.True(vm.IsOpen);
        Assert.Equal("3 of 4 complete", vm.ProgressText);
        Assert.Equal(SetupStepKey.Discovery, vm.ShownSteps[^1].Key);
        Assert.Equal("Finish the setup checklist", vm.StripTitle);

        Scanned("P01", "cm01.contoso.com", 5);
        vm.Refresh();
        Assert.False(vm.IsDue);
        Assert.False(vm.IsOpen);
        Assert.Equal("5 products matched.", Step(vm, SetupStepKey.Discovery).Detail);
        Assert.Equal(SetupChecklistRules.Finished, service.Load().SetupChecklist);
        Assert.True(service.Load().FirstRunCompleted);

        // A later start with the site out of reach opens nothing.
        _connection = ConfigMgrConnectionState.Failed;
        var later = Checklist(new SettingsService());
        later.Refresh();
        Assert.False(later.IsDue);

        Configure(service, "P02", "cm02.contoso.com");
        later.Refresh();
        Assert.True(later.IsDue && later.IsOpen);
        Assert.Equal("To do", Step(later, SetupStepKey.Discovery).StateWord);

        // Skip for Now hides this site only.
        later.CloseCommand.Execute(null);
        Assert.True(later.IsDue);
        Assert.False(later.IsOpen);
        Configure(service, "P03", "cm03.contoso.com");
        later.Refresh();
        Assert.True(later.IsOpen);

        Configure(service, "P01", "cm01.contoso.com");
        later.Refresh();
        Assert.False(later.IsDue);
    }

    [Theory]
    [InlineData("""{ "SiteCode": "P01", "SiteCodeSaved": true, "FileShareRoot": "\\\\cm01\\content$", "FirstRunCompleted": true }""")]
    // A file written before the first-run flag existed.
    [InlineData("""{ "SiteCode": "P01", "FileShareRoot": "\\\\cm01\\content$" }""")]
    // Set up in Settings after the Setup window was skipped.
    [InlineData("""{ "SiteCode": "P01", "SiteCodeSaved": true, "FileShareRoot": "\\\\cm01\\content$", "FirstRunCompleted": false }""")]
    // A completed install with nothing set stays as it is.
    [InlineData("""{ "FirstRunCompleted": true }""")]
    public void AnInstallThatWasSetUpBeforeNeverSeesTheChecklistUntilItOpensIt(string json)
    {
        using var scope = SiteContext.Scope(null, _root);
        File.WriteAllText(SettingsService.SettingsPath, json);
        var service = new SettingsService();
        var vm = Checklist(service);
        vm.Refresh();
        Assert.False(vm.IsDue);
        Assert.False(vm.IsOpen);
        Assert.Null(service.Load().SetupChecklist);

        vm.Show();
        Assert.True(vm.IsOpen && vm.OpenedByUser);
        Assert.Equal("Close", vm.CloseText);
        vm.CloseCommand.Execute(null);
        Assert.False(vm.IsOpen);
        Assert.Null(service.Load().SetupChecklist);
    }

    [Fact]
    public void SkipForNowIsNotStoredAndDoNotShowAgainIs()
    {
        using var scope = SiteContext.Scope(null, _root);
        var service = new SettingsService();
        var vm = Checklist(service);
        vm.Refresh();
        vm.CloseCommand.Execute(null);
        Assert.True(vm.IsDue);
        Assert.False(vm.IsOpen);
        // The first save of a site does not open the skipped checklist again in the same run.
        Configure(service, "P01", "cm01");
        vm.Refresh();
        Assert.False(vm.IsOpen);

        var next = Checklist(new SettingsService());
        next.Refresh();
        Assert.True(next.IsOpen);

        next.DoNotShowAgainCommand.Execute(null);
        Assert.False(next.IsDue);
        Assert.False(next.IsOpen);
        var after = Checklist(new SettingsService());
        after.Refresh();
        Assert.False(after.IsDue);
        Assert.Equal(SetupChecklistRules.Dismissed, service.Load().SetupChecklist);
    }

    [Fact]
    public void SkipScanCompletesTheStepAndALaterScanShowsItsResult()
    {
        using var scope = SiteContext.Scope(null, _root);
        var service = new SettingsService();
        Configure(service, "P01", "cm01.contoso.com");
        var vm = Checklist(service);
        vm.Refresh();
        vm.SkipScanCommand.Execute(null);
        Assert.Equal("Skipped", Step(vm, SetupStepKey.Discovery).StateWord);
        // The site is not tested, so the panel still lists the three steps of the setup, and two of them are done.
        Assert.Equal("2 of 3 complete", vm.ProgressText);
        _connection = ConfigMgrConnectionState.Connected;
        vm.Refresh();
        Assert.Equal("4 of 4 complete", vm.ProgressText);

        var settings = new SettingsService().Load();
        Scanned(settings.SiteCode, settings.ProviderMachine, 2);
        vm.Refresh();
        Assert.Equal("2 products matched.", Step(vm, SetupStepKey.Discovery).Detail);
        Assert.Equal("Scan Again", Step(vm, SetupStepKey.Discovery).ScanText);
    }

    [Fact]
    public void ARunTurnsOffTheButtonsThatStartWork()
    {
        using var scope = SiteContext.Scope(null, _root);
        var vm = Checklist(new SettingsService());
        vm.Refresh();
        Assert.True(vm.CanOpenSetup && vm.ScanCommand.CanExecute(null));
        _busy = true;
        vm.RefreshBusy();
        Assert.False(vm.CanOpenSetup);
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.False(vm.TestConnectionCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheResultHookReportsTheScanAndThenTheChoiceAndNeverAFailedScan()
    {
        var settings = new AppSettings { SiteCode = "MCM", ProviderMachine = "cm01", Systems = new SystemsInUse(), HiddenApplications = ["grepwin"] };
        var store = new InMemorySettings(settings);
        var service = new FakeDiscoveryService
        {
            Answer = (_, _) => Task.FromResult(new DiscoveryResult([new DiscoveryCandidate("grepwin", "grepWin", "Stefan Kueng", "grepWin")], "Discovered 1 app."))
        };
        var runner = new DiscoveryRunner(service, store.Load, store.Save,
            _ => Task.FromResult<IReadOnlyList<DiscoveryPackager>>([]), _ => Task.FromResult<IReadOnlyList<CmSiteDrive>>([]), () => null);
        var reports = new List<DiscoveryRunReport>();
        runner.ResultHook = reports.Add;

        await runner.RunAsync(new FakeDiscoveryPrompt(p => p.EnableAllCommand.Execute(null)), _ => { }, _ => { }, () => Task.CompletedTask, () => { }, CancellationToken.None);
        Assert.Equal([new DiscoveryRunReport("MCM", "cm01", 1, null), new DiscoveryRunReport("MCM", "cm01", 1, DiscoveryAction.EnableAll)], reports);

        reports.Clear();
        service.Answer = (_, _) => throw new DiscoveryException("The SQL Server could not be reached.");
        await runner.RunAsync(new FakeDiscoveryPrompt(_ => { }), _ => { }, _ => { }, () => Task.CompletedTask, () => { }, CancellationToken.None);
        Assert.Empty(reports);
    }

    private sealed class Host : IPublishActionHost, ISettingsReload
    {
        public void RefreshPublishActionState() { }
        public void AppendLog(string message) { }
        public void ReloadFromStore() { }
    }

    [Fact]
    public void SetupOpenedAgainKeepsTheSavedOneClickDestinationsWhileTheSystemsStay()
    {
        using var scope = SiteContext.Scope(null, _root);
        File.WriteAllText(SettingsService.SettingsPath,
            """{ "FirstRunCompleted": true, "Systems": { "ConfigMgr": false, "Intune": true, "Wsus": false }, "OneClick": { "DefaultDestinations": { "ConfigMgr": false, "Wsus": false, "Intune": false } } }""");
        var host = new Host();
        var before = new SettingsService().Load().OneClick.DefaultDestinations;

        new SetupWindowViewModel(new SettingsService(), host, host).SaveCommand.Execute(null);

        Assert.Equal(before, new SettingsService().Load().OneClick.DefaultDestinations);
    }

    [Fact]
    public void SetupOpenedAgainBeforeTheFirstRunIsCompletedKeepsTheDestinationsSavedInSettings()
    {
        using var scope = SiteContext.Scope(null, _root);
        File.WriteAllText(SettingsService.SettingsPath,
            """{ "FirstRunCompleted": false, "Systems": { "ConfigMgr": false, "Intune": true, "Wsus": false }, "OneClick": { "DefaultDestinations": { "ConfigMgr": false, "Wsus": false, "Intune": false } } }""");
        var host = new Host();
        var before = new SettingsService().Load().OneClick.DefaultDestinations;

        new SetupWindowViewModel(new SettingsService(), host, host).SaveCommand.Execute(null);

        Assert.Equal(before, new SettingsService().Load().OneClick.DefaultDestinations);

        // A change of the systems sets the destinations to the new systems.
        new SetupWindowViewModel(new SettingsService(), host, host) { UseWsus = true }.SaveCommand.Execute(null);
        Assert.Equal(new DestinationSet(false, true, true), new SettingsService().Load().OneClick.DefaultDestinations);
    }

    /// <summary>The destinations that One Click uses: the stored default, else the deployment target.</summary>
    private static DestinationSet Effective(AppSettings s) => s.OneClick.DefaultDestinations ?? DestinationSet.FromTarget(s.Intune.DeploymentTarget);

    [Fact]
    public void SetupWithNoStoredDestinationKeepsTheEffectiveDestinationsAfterTheFirstRun()
    {
        using var scope = SiteContext.Scope(null, _root);
        File.WriteAllText(SettingsService.SettingsPath, """{ "FirstRunCompleted": true, "Systems": { "ConfigMgr": false, "Intune": true, "Wsus": false } }""");
        var host = new Host();
        var before = Effective(new SettingsService().Load());

        new SetupWindowViewModel(new SettingsService(), host, host).SaveCommand.Execute(null);

        Assert.Equal(before, Effective(new SettingsService().Load()));
    }

    [Fact]
    public void SetupSavedUnchangedKeepsAConfigMgrOnlyTargetOfAnOlderFile()
    {
        using var scope = SiteContext.Scope(null, _root);
        // No Systems and no One Click default: the tenant ID puts Intune in use, and the target alone says that One Click publishes to ConfigMgr.
        File.WriteAllText(SettingsService.SettingsPath, """{ "Intune": { "DeploymentTarget": "ConfigMgr", "TenantId": "contoso" } }""");
        var loaded = new SettingsService().Load();
        Assert.True(loaded.Systems!.ConfigMgr && loaded.Systems.Intune);
        var host = new Host();

        new SetupWindowViewModel(new SettingsService(), host, host).SaveCommand.Execute(null);

        var saved = new SettingsService().Load();
        Assert.Equal(new DestinationSet(true, false, false), Effective(saved));
        Assert.Equal(DeploymentTargets.Mecm, saved.Intune.DeploymentTarget);
    }

    [Fact]
    public void DontShowThisAgainWithSkipStopsAChecklistThatOutlivedASavedSetup()
    {
        using var scope = SiteContext.Scope(null, _root);
        // A saved setup completed the first run; the checklist is still in progress.
        File.WriteAllText(SettingsService.SettingsPath, """{ "FirstRunCompleted": true, "SetupChecklist": "Open" }""");
        var service = new SettingsService();
        var checklist = Checklist(service);
        checklist.Refresh();
        Assert.True(checklist.IsDue);
        var host = new Host();
        var setup = new SetupWindowViewModel(service, host, host) { DontShowAgain = true };
        setup.SkipCommand.Execute(null);

        Assert.True(setup.StopsTheChecklist(saved: false));
        Assert.False(setup.StopsTheChecklist(saved: true));
        Assert.False(new SetupWindowViewModel(service, host, host).StopsTheChecklist(saved: false));

        checklist.Dismiss();
        Assert.False(checklist.IsDue);
        Assert.Equal(SetupChecklistRules.Dismissed, service.Load().SetupChecklist);
    }

    [Fact]
    public void DismissLeavesAnInstallWithoutAChecklistWithoutOne()
    {
        using var scope = SiteContext.Scope(null, _root);
        File.WriteAllText(SettingsService.SettingsPath, """{ "FirstRunCompleted": true }""");
        var service = new SettingsService();
        Checklist(service).Dismiss();
        Assert.Null(service.Load().SetupChecklist);
    }

    [Theory]
    [InlineData(false, null, false, true)]
    [InlineData(true, "Dismissed", false, true)]
    [InlineData(true, "Open", false, true)]
    [InlineData(true, "Done", false, true)]
    // An install that never got the checklist tests its site code as before.
    [InlineData(true, null, false, false)]
    [InlineData(false, "Open", true, false)]
    public void ASiteCodeThatWasNeverSavedIsAnExampleOnANewInstallOnly(bool firstRunCompleted, string? checklist, bool saved, bool example) =>
        Assert.Equal(example, SetupChecklistRules.IsExampleSite(new AppSettings { FirstRunCompleted = firstRunCompleted, SetupChecklist = checklist, SiteCodeSaved = saved }));

    [Fact]
    public void ADismissedNewInstallStillHasAnExampleSiteAndALauncherSiteIsNot()
    {
        using var scope = SiteContext.Scope(null, _root);
        var service = new SettingsService();
        var checklist = Checklist(service);
        checklist.Refresh();
        checklist.Dismiss();
        Assert.True(SetupChecklistRules.IsExampleSite(service.Load()));

        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, "P01");
        Assert.False(SetupChecklistRules.IsExampleSite(service.Load()));
    }

    [Fact]
    public void SetupWithoutConfigMgrSavesTheDownloadRoot()
    {
        using var scope = SiteContext.Scope(null, _root);
        var host = new Host();
        var folder = Path.Combine(_root, "stage");

        new SetupWindowViewModel(new SettingsService(), host, host) { UseConfigMgr = false, UseIntune = true, DownloadRoot = $" {folder} " }.SaveCommand.Execute(null);

        Assert.Equal(folder, new SettingsService().Load().DownloadRoot);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheDownloadRootFieldShowsInSetupWithoutConfigMgr(ApplicationTheme theme)
    {
        RenderKit.WithApplication(theme, () =>
        {
            using var scope = SiteContext.Scope(null, _root);
            var host = new Host();
            var viewModel = new SetupWindowViewModel(new SettingsService(), host, host) { UseConfigMgr = false, UseIntune = true, CurrentStep = 2 };
            var window = new SetupWindow(viewModel);
            RenderKit.Show(window);
            try
            {
                RenderKit.Settle(window);
                Assert.True(RenderKit.ById<FrameworkElement>((FrameworkElement)window.Content, "txtSetupDownloadRoot").IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TheMainWindowOpensSetupOnlyWhenTheUserAsksForIt()
    {
        var source = File.ReadAllText(Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Views", "Windows", "MainWindow.xaml.cs"));
        var loaded = source[source.IndexOf("private void FluentWindow_Loaded", StringComparison.Ordinal)..];
        Assert.DoesNotContain("SetupWindow", loaded);
        Assert.DoesNotContain("OpenSetup()", loaded);
        Assert.Contains("public void OpenSetup()", source);
    }

    // The page area of a maximized window on a 1080p display (1920 x 1032): less the navigation pane, and less the title bar (48), the context
    // strip (49) and the site footer (40).
    private const double DesignPageWidth = 1920 - 230, DesignPageHeight = 1032 - 48 - 49 - 40;

    private (PackagesPage Page, Window Window) ChecklistWindow(double width, double height)
    {
        // A failed connection and folders that are not set give the longest step texts and the most buttons.
        File.WriteAllText(SettingsService.SettingsPath,
            """{ "FirstRunCompleted": false, "Systems": { "ConfigMgr": true, "Intune": true, "Wsus": true }, "SiteCode": "P01", "SiteCodeSaved": true }""");
        _connection = ConfigMgrConnectionState.Failed;
        var page = AppPackagerFitTests.Create(12, out var viewModel);
        viewModel.Checklist = Checklist(new SettingsService());
        viewModel.Checklist.Refresh();
        // Create builds the page before the checklist exists, so the page is built again.
        page = new PackagesPage(viewModel);
        var window = new Window { Width = width, Height = height, WindowStyle = WindowStyle.None, Content = page };
        RenderKit.Show(window);
        RenderKit.Settle(window);
        return (page, window);
    }

    private static ScrollViewer StepScroller(UserControl panel) =>
        RenderKit.Descendants(panel).OfType<ScrollViewer>().First(s => RenderKit.Descendants(s).OfType<ItemsControl>()
            .Any(i => AutomationProperties.GetAutomationId(i) == "lstSetupSteps"));

    private static void AssertInsideOf(FrameworkElement container, FrameworkElement element, string where)
    {
        var bounds = element.TransformToAncestor(container).TransformBounds(new Rect(element.RenderSize));
        Assert.True(bounds.Top >= -0.5 && bounds.Bottom <= container.ActualHeight + 0.5 && bounds.Left >= -0.5 && bounds.Right <= container.ActualWidth + 0.5,
            $"{where}: {AutomationProperties.GetAutomationId(element)} is at {bounds} in {container.ActualWidth:0}x{container.ActualHeight:0}.");
    }

    /// <summary>The menu of a step, filled as a right click or the menu key fills it. It has the actions that the step has beside its button.</summary>
    private static List<MenuItem> StepMenu(UserControl panel, string key)
    {
        var title = RenderKit.ById<TextBlock>(panel, "txtSetupStep" + key);
        DependencyObject node = title;
        while (node is not UserControl { ContextMenu: not null }) node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        var step = (UserControl)node;
        typeof(ApplicationPackagerUI.Controls.SetupChecklistPanel)
            .GetMethod("Step_ContextMenuOpening", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(panel, [step, null]);
        return step.ContextMenu!.Items.OfType<MenuItem>().ToList();
    }

    private static List<string> Ids(IEnumerable<MenuItem> items) => items.Select(AutomationProperties.GetAutomationId).ToList();

    /// <summary>The button of each step that has one: the buttons of the list, in order.</summary>
    private static List<System.Windows.Controls.Button> StepButtons(ScrollViewer scroller) =>
        RenderKit.Descendants(scroller).OfType<System.Windows.Controls.Button>().Where(b => b.IsVisible).ToList();

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void AtTheDesignWindowTheStepsAndTheirButtonsShowWithoutScrolling(ApplicationTheme theme)
    {
        RenderKit.WithApplication(theme, () =>
        {
            using var scope = SiteContext.Scope(null, _root);
            var (page, window) = ChecklistWindow(DesignPageWidth, DesignPageHeight);
            try
            {
                var where = $"setup checklist {theme} 1920x1032";
                var panel = RenderKit.ById<UserControl>(page, "pnlSetupChecklist");
                Assert.True(panel.IsVisible);
                AssertInsideOf(page, panel, where);
                var scroller = StepScroller(panel);
                Assert.True(scroller.ScrollableHeight < 0.5, $"{where}: the steps need {scroller.ScrollableHeight:0} px more than the {scroller.ViewportHeight:0} px they have.");
                foreach (var key in new[] { "Systems", "Site", "Folders" })
                    AssertInsideOf(scroller, RenderKit.ById<TextBlock>(panel, "txtSetupStep" + key), where);
                var buttons = StepButtons(scroller);
                Assert.Equal(2, buttons.Count);
                foreach (var button in buttons) AssertInsideOf(scroller, button, where);
                // The failed connection test stays reachable from the menu of its step, with the Setup window.
                var site = StepMenu(panel, "Site");
                Assert.Contains("btnSetupTestConnection", Ids(site));
                Assert.Contains("btnSetupOpenWindowSite", Ids(site));
                Assert.Contains("btnSetupDismiss", Ids(site));
                var skip = RenderKit.ById<ButtonBase>(page, "btnSetupClose");
                Assert.True(skip.IsVisible);
                AssertInsideOf(page, skip, where);
                PageChromeKit.AssertNothingClipped(panel, where);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void InASmallWindowTheStepsScrollAndSkipStaysInTheWindow()
    {
        RenderKit.WithApplication(ApplicationTheme.Light, () =>
        {
            using var scope = SiteContext.Scope(null, _root);
            var (page, window) = ChecklistWindow(1440 - 230, 480);
            try
            {
                var panel = RenderKit.ById<UserControl>(page, "pnlSetupChecklist");
                var scroller = StepScroller(panel);
                Assert.Equal(ScrollBarVisibility.Auto, scroller.VerticalScrollBarVisibility);
                Assert.True(scroller.ScrollableHeight > 0, "The steps do not scroll in a window that is too low for them.");
                AssertInsideOf(page, RenderKit.ById<ButtonBase>(page, "btnSetupClose"), "small window");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheKeyboardReachesEveryStepAndTheHelpTextNamesTheMenuKeys(ApplicationTheme theme)
    {
        RenderKit.WithApplication(theme, () =>
        {
            using var scope = SiteContext.Scope(null, _root);
            var (page, window) = ChecklistWindow(DesignPageWidth, DesignPageHeight);
            try
            {
                var panel = RenderKit.ById<UserControl>(page, "pnlSetupChecklist");
                var steps = RenderKit.Descendants(panel).OfType<UserControl>().Where(s => s.ContextMenu is not null).ToList();
                // A failed connection and no folders: the first step is done and has no button, and it takes the focus too.
                Assert.Equal(3, steps.Count);
                foreach (var step in steps)
                {
                    Assert.True(step.Focusable && step.IsTabStop && step.IsVisible);
                    Assert.NotNull(step.FocusVisualStyle);
                    Assert.Contains("Apps key or Shift+F10", AutomationProperties.GetHelpText(step));
                    Assert.StartsWith("Step ", UIElementAutomationPeer.CreatePeerForElement(step).GetName());
                }
                Assert.True(steps[0].MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next)));
                Assert.Contains("btnSetupOpenWindowSystems", Ids(StepMenu(panel, "Systems")));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ARunShowsCancelAlsoWithTheChecklistOpenAndShowDetailsOpensThePane()
    {
        File.WriteAllText(SettingsService.SettingsPath, "{ \"FirstRunCompleted\": false }");
        RenderKit.WithApplication(ApplicationTheme.Light, () =>
        {
            using var scope = SiteContext.Scope(null, _root);
            var page = AppPackagerFitTests.Create(12, out var viewModel);
            viewModel.Checklist = Checklist(new SettingsService());
            viewModel.Checklist.Refresh();
            page = new PackagesPage(viewModel);
            var window = new Window { Width = 1690, Height = 984, WindowStyle = WindowStyle.None, Content = page };
            RenderKit.Show(window);
            try
            {
                RenderKit.Settle(window);
                var panel = RenderKit.ById<UserControl>(page, "pnlSetupChecklist");
                var bar = (Border)page.FindName("LogBar");
                Assert.True(panel.IsVisible);
                Assert.False(bar.IsVisible);

                viewModel.ShowProgress = true;
                RenderKit.Settle(window);
                Assert.True(panel.IsVisible && bar.IsVisible, "A run with the checklist open hides the progress.");
                Assert.True(RenderKit.Descendants(bar).OfType<System.Windows.Controls.ProgressBar>().Single().IsVisible);
                viewModel.ShowProgress = false;
                RenderKit.Settle(window);
                Assert.False(bar.IsVisible);

                // Cancel follows the run state. The field is set, because the change handler needs the services of the real view model.
                typeof(PackagesViewModel).GetField("_isOperationRunning", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(viewModel, true);
                typeof(CommunityToolkit.Mvvm.ComponentModel.ObservableObject)
                    .GetMethod("OnPropertyChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                        [typeof(string)])!.Invoke(viewModel, [nameof(PackagesViewModel.IsOperationRunning)]);
                RenderKit.Settle(window);
                var cancel = RenderKit.ById<ButtonBase>(page, "btnCancelOperation");
                Assert.True(bar.IsVisible && cancel.IsVisible, "A run with the checklist open has no Cancel.");

                page.ShowDetails();
                RenderKit.Settle(window);
                Assert.False(panel.IsVisible);
                Assert.True(RenderKit.ById<Border>(page, "pnlDetailPane").IsVisible);
            }
            finally { window.Close(); }
        });
    }

    /// <summary>The sizes of the fit test of the page: a new install keeps whole grid rows beside the panel and after the panel closed.</summary>
    [Theory]
    [MemberData(nameof(AppPackagerFitTests.Sizes), MemberType = typeof(AppPackagerFitTests))]
    public void ANewInstallKeepsWholeGridRowsWithTheChecklistOpenAndClosed(ApplicationTheme theme, int width, int height)
    {
        File.WriteAllText(SettingsService.SettingsPath, "{ \"FirstRunCompleted\": false }");
        RenderKit.WithApplication(theme, () =>
        {
            using var scope = SiteContext.Scope(null, _root);
            var page = AppPackagerFitTests.Create(12, out var viewModel);
            viewModel.Checklist = Checklist(new SettingsService());
            viewModel.Checklist.Refresh();
            // Create builds the page before the checklist exists, so the page is built again.
            page = new PackagesPage(viewModel);
            var window = new Window { Width = width - 230, Height = height - 48, WindowStyle = WindowStyle.None, Content = page };
            RenderKit.Show(window);
            try
            {
                RenderKit.Settle(window);
                var grid = (DataGrid)page.FindName("PackagerGrid");
                var panel = RenderKit.ById<UserControl>(page, "pnlSetupChecklist");
                var needed = height >= 1000 ? AppPackagerFitTests.MinimumWholeRows : 5;

                void Check(string state)
                {
                    var whole = AppPackagerFitTests.WholeRows(grid);
                    Assert.True(whole >= needed, $"{theme} {width}x{height}, {state}: the grid shows {whole} whole rows (needs {needed}) and is {grid.ActualHeight:0} px tall.");
                }

                // A window narrower than the pane limit starts without the panel; a wider one starts with it.
                var narrow = width < PackagesPage.DetailPaneMinimumWindowWidth;
                Assert.Equal(!narrow, panel.IsVisible);
                Check("at the start");

                if (!narrow)
                {
                    viewModel.Checklist.CloseCommand.Execute(null);
                    RenderKit.Settle(window);
                    Assert.False(panel.IsVisible);
                    Check("with the checklist closed");
                }
                // More opens the checklist again, in a narrow window too.
                var open = page.BuildMoreMenu().Items.OfType<MenuItem>().Single(i => AutomationProperties.GetAutomationId(i) == "miSetupChecklist");
                Assert.True(open.IsEnabled);
                open.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                RenderKit.Settle(window);
                Assert.True(panel.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void WithAnEmptySettingsFileTheGridShowsBesideTheChecklistAndSkipKeepsItUsable(ApplicationTheme theme)
    {
        File.WriteAllText(SettingsService.SettingsPath, "{ \"FirstRunCompleted\": false }");
        RenderKit.WithApplication(theme, () =>
        {
            using var scope = SiteContext.Scope(null, _root);
            System.Windows.Application.Current.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
            var viewModel = (PackagesViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
            viewModel.FilteredPackagers = new(Enumerable.Range(0, 8).Select(i => new PackagerInfo { Uid = $"app{i}", Vendor = "Contoso", Application = $"Contoso Application {i}" }));
            viewModel.IsDetailPaneOpen = true;
            viewModel.Checklist = Checklist(new SettingsService());
            viewModel.Checklist.Refresh();
            var page = new PackagesPage(viewModel);
            var window = new Window { Width = 1500, Height = 800, WindowStyle = WindowStyle.None, Content = page };
            RenderKit.Show(window);
            try
            {
                RenderKit.Settle(window);
                var panel = RenderKit.ById<UserControl>(page, "pnlSetupChecklist");
                var grid = (DataGrid)page.FindName("PackagerGrid");
                Assert.True(panel.IsVisible);
                Assert.False(RenderKit.ById<Border>(page, "pnlDetailPane").IsVisible);
                Assert.NotEmpty(RenderKit.Descendants(grid).OfType<DataGridRow>());
                PageChromeKit.AssertNothingClipped(panel, $"setup checklist {theme}");
                Assert.Equal("1 of 3 complete", RenderKit.ById<TextBlock>(panel, "txtSetupProgress").Text);

                var titles = new[] { "Systems", "Site", "Folders" }
                    .Select(key => UIElementAutomationPeer.CreatePeerForElement(RenderKit.ById<TextBlock>(panel, "txtSetupStep" + key)).GetName()).ToList();
                Assert.Equal(
                    ["Step 1 of 3, Choose systems: Complete", "Step 2 of 3, Connect to your site: To do", "Step 3 of 3, Set content folders: To do"], titles);

                // More, Setup needs no stored setting.
                var setup = page.BuildMoreMenu().Items.OfType<MenuItem>().Single(i => AutomationProperties.GetAutomationId(i) == "miSetup");
                Assert.True(setup.IsEnabled);

                RenderKit.ById<ButtonBase>(page, "btnSetupClose").Command.Execute(null);
                RenderKit.Settle(window);
                Assert.False(panel.IsVisible);
                Assert.True(grid.IsVisible && grid.IsEnabled);
                Assert.True(RenderKit.ById<Border>(page, "pnlDetailPane").IsVisible);

                page.BuildMoreMenu().Items.OfType<MenuItem>().Single(i => AutomationProperties.GetAutomationId(i) == "miSetupChecklist")
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                RenderKit.Settle(window);
                Assert.True(panel.IsVisible);
            }
            finally { window.Close(); }
        });
    }
}
