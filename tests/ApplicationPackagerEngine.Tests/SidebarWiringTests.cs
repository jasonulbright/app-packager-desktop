using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SidebarWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-sidebar-wiring-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public SidebarWiringTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _savedRoot);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void TheRegistrationGivesOneServiceOverTheDefaultRegistryAndTheSettingsFile()
    {
        using var provider = new ServiceCollection().AddPackagerServices().AddSidebar().BuildServiceProvider();
        var service = provider.GetRequiredService<SidebarService>();
        Assert.Same(service, provider.GetRequiredService<SidebarService>());
        Assert.Equal(SidebarFixtures.Ids(SidebarRegistry.CreateDefault().Items), SidebarFixtures.Ids(service.Items));
        Assert.IsType<SettingsSidebarLayoutStore>(provider.GetRequiredService<ISidebarLayoutStore>());
    }

    private sealed class RecordingSnackbar : Wpf.Ui.ISnackbarService
    {
        public List<(string Title, string Message, Wpf.Ui.Controls.ControlAppearance Appearance)> Shown { get; } = [];
        public TimeSpan DefaultTimeOut { get; set; } = TimeSpan.FromSeconds(5);
        public void SetSnackbarPresenter(Wpf.Ui.Controls.SnackbarPresenter contentPresenter) { }
        public Wpf.Ui.Controls.SnackbarPresenter? GetSnackbarPresenter() => null;
        public void Show(string title, string message, Wpf.Ui.Controls.ControlAppearance appearance, Wpf.Ui.Controls.IconElement? icon, TimeSpan timeout) =>
            Shown.Add((title, message, appearance));
    }

    /// <summary>A plain page per page type, so navigation needs none of the real pages' services.</summary>
    private static void AddPlainPages(IServiceCollection services)
    {
        foreach (var type in new[] { typeof(ApplicationPackagerUI.Views.Pages.PackagesPage), typeof(ApplicationPackagerUI.Views.Pages.ApplicationsPage),
                     typeof(ApplicationPackagerUI.Views.Pages.AddAppPage), typeof(ApplicationPackagerUI.Views.Pages.SettingsPage) })
            services.AddSingleton(type, _ => new System.Windows.Controls.Page { Tag = type });
    }

    private static ServiceProvider MainWindowServices(Action<IServiceCollection>? replace = null)
    {
        var services = new ServiceCollection().AddPackagerServices().AddSidebar();
        Wpf.Ui.DependencyInjection.ServiceCollectionExtensions.AddNavigationViewPageProvider(services);
        services.AddSingleton<ApplicationPackagerUI.ViewModels.Windows.MainWindowViewModel>();
        services.AddSingleton<Wpf.Ui.INavigationService, Wpf.Ui.NavigationService>();
        services.AddSingleton<Wpf.Ui.ISnackbarService, Wpf.Ui.SnackbarService>();
        services.AddSingleton<Wpf.Ui.IContentDialogService, Wpf.Ui.ContentDialogService>();
        services.AddSingleton<ApplicationPackagerUI.Views.Windows.MainWindow>();
        replace?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static void WriteSettings(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsService.SettingsPath)!);
        File.WriteAllText(SettingsService.SettingsPath, json);
    }

    private static Type? ShownPage(Wpf.Ui.Controls.NavigationView navigation)
    {
        StaTestHelper.Flush();
        var presenter = navigation.Template.FindName("PART_NavigationViewContentPresenter", navigation) as System.Windows.Controls.ContentControl;
        return (presenter?.Content as System.Windows.Controls.Page)?.Tag as Type;
    }

    /// <summary>
    /// The real main window placed off screen for its first show. The window stays open at the end: closing it
    /// runs the Packages view model's discovery stop, which needs the full page services.
    /// </summary>
    private static ApplicationPackagerUI.Views.Windows.MainWindow OffScreenMainWindow(ServiceProvider provider)
    {
        var window = provider.GetRequiredService<ApplicationPackagerUI.Views.Windows.MainWindow>();
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        return window;
    }

    [Fact]
    public void TheMainWindowTakesItsEntriesFromTheRegistryAndKeepsSettingsAloneInTheFooter()
    {
        StaTestHelper.Run(() =>
        {
            using var provider = MainWindowServices();
            var window = provider.GetRequiredService<ApplicationPackagerUI.Views.Windows.MainWindow>();
            var navigation = window.GetNavigation();
            var registry = provider.GetRequiredService<SidebarRegistry>();
            Assert.Equal(registry.Items.Select(i => i.Title), Titles(window));
            var settings = Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(Assert.Single(navigation.FooterMenuItems.Cast<object>()));
            Assert.Equal("Settings", settings.Content);
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.SettingsPage), settings.TargetPageType);
            Assert.DoesNotContain(navigation.MenuItems.Cast<Wpf.Ui.Controls.NavigationViewItem>(), i => (string)i.Content == "Settings");
            window.Close();
        });
    }

    [Fact]
    public void AnUnsavedSidebarChangeShowsTheCautionSnackbarAndASavedOneShowsNothing()
    {
        StaTestHelper.Run(() =>
        {
            var snackbar = new RecordingSnackbar();
            WriteSettings("{ \"FirstRunCompleted\": true }");
            using (var provider = MainWindowServices(s => s.AddSingleton<Wpf.Ui.ISnackbarService>(snackbar)))
            {
                var window = provider.GetRequiredService<ApplicationPackagerUI.Views.Windows.MainWindow>();
                provider.GetRequiredService<SidebarService>().Hide(SidebarIds.AddApp);
                Assert.Empty(snackbar.Shown);
                window.Close();
            }

            WriteSettings("{ this is not json");
            using (var provider = MainWindowServices(s => s.AddSingleton<Wpf.Ui.ISnackbarService>(snackbar)))
            {
                var window = provider.GetRequiredService<ApplicationPackagerUI.Views.Windows.MainWindow>();
                provider.GetRequiredService<SidebarService>().Hide(SidebarIds.Applications);
                var shown = Assert.Single(snackbar.Shown);
                Assert.Equal("Sidebar", shown.Title);
                Assert.Equal("The sidebar layout could not be saved. The change lasts until the application closes.", shown.Message);
                Assert.Equal(Wpf.Ui.Controls.ControlAppearance.Caution, shown.Appearance);
                Assert.Equal("{ this is not json", File.ReadAllText(SettingsService.SettingsPath));
                window.Close();
            }
        });
    }

    [Fact]
    public void StartupShowsPackagesWhateverTheSavedLayout()
    {
        StaTestHelper.Run(() =>
        {
            WriteSettings("""
                { "FirstRunCompleted": true,
                  "Sidebar": { "Order": ["workbench", "add-app", "applications", "packages"], "Hidden": ["packages", "applications"] } }
                """);
            using var provider = MainWindowServices(AddPlainPages);
            var window = OffScreenMainWindow(provider);
            ApplicationPackagerUI.Services.ApplicationHostService.ShowMainWindow(window);
            var navigation = window.GetNavigation();
            var first = (Wpf.Ui.Controls.NavigationViewItem)navigation.MenuItems[0]!;
            Assert.Equal("Packages", first.Content);
            Assert.Same(first, navigation.SelectedItem);
            Assert.True(first.IsActive);
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.PackagesPage), ShownPage(navigation));
            Assert.DoesNotContain("Applications", Titles(window));
            Assert.Equal("Application Workbench", Titles(window)[1]);
        });
    }

    [Fact]
    public void HidingTheShownPageInTheMainWindowOpensPackagesAndShowingItAgainLeavesNoHighlight()
    {
        StaTestHelper.Run(() =>
        {
            WriteSettings("{ \"FirstRunCompleted\": true }");
            using var provider = MainWindowServices(AddPlainPages);
            var window = OffScreenMainWindow(provider);
            ApplicationPackagerUI.Services.ApplicationHostService.ShowMainWindow(window);
            var navigation = window.GetNavigation();
            var items = navigation.MenuItems.Cast<Wpf.Ui.Controls.NavigationViewItem>().ToList();
            var packages = items.Single(i => (string)i.Content == "Packages");
            var applications = items.Single(i => (string)i.Content == "Applications");

            Assert.True(navigation.Navigate(typeof(ApplicationPackagerUI.Views.Pages.ApplicationsPage)));
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.ApplicationsPage), ShownPage(navigation));
            Assert.True(applications.IsActive);

            var hide = applications.ContextMenu!.Items.OfType<System.Windows.Controls.MenuItem>().Single(m => (string)m.Header == "Hide Applications");
            hide.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent, hide));
            Assert.DoesNotContain("Applications", Titles(window));
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.PackagesPage), ShownPage(navigation));
            Assert.Same(packages, navigation.SelectedItem);
            Assert.False(applications.IsActive);

            var show = packages.ContextMenu!.Items.OfType<System.Windows.Controls.MenuItem>().Single(m => (string)m.Header == "Show tool");
            var entry = show.Items.OfType<System.Windows.Controls.MenuItem>().Single(m => (string)m.Header == "Applications");
            entry.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent, entry));
            Assert.Equal("Applications", Titles(window)[1]);
            Assert.False(applications.IsActive);
            Assert.True(packages.IsActive);
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.PackagesPage), ShownPage(navigation));
        });
    }

    private static readonly string[] ToolTitles =
    [
        "Detection Tester", "Maintenance Windows", "Collection and Compliance", "Installer Analysis",
        "Deployment Helper", "DP Content Manager", "Pivots and Scripts", "Site Hygiene"
    ];

    private static string[] Titles(ApplicationPackagerUI.Views.Windows.MainWindow window) =>
        window.GetNavigation().MenuItems.Cast<Wpf.Ui.Controls.NavigationViewItem>().Select(i => (string)i.Content).ToArray();

    [Fact]
    public void TheMainWindowBuildsItsEntriesFromTheServiceAndFollowsAHide()
    {
        StaTestHelper.Run(() =>
        {
            using var provider = MainWindowServices();

            var window = provider.GetRequiredService<ApplicationPackagerUI.Views.Windows.MainWindow>();
            var items = window.GetNavigation().MenuItems.Cast<Wpf.Ui.Controls.NavigationViewItem>().ToList();
            Assert.Equal(["Packages", "Applications", "Add App", "Application Workbench", .. ToolTitles], items.Select(i => (string)i.Content));
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.PackagesPage), items[0].TargetPageType);
            Assert.Null(items[3].TargetPageType);

            provider.GetRequiredService<SidebarService>().Hide(SidebarIds.AddApp);
            Assert.Equal(11, window.GetNavigation().MenuItems.Count);
            window.Close();
        });
    }

    [Fact]
    public void TheMainWindowShowsTheEightToolEntriesHidesOneAndShowsItAgain()
    {
        StaTestHelper.Run(() =>
        {
            using var provider = MainWindowServices();
            var window = provider.GetRequiredService<ApplicationPackagerUI.Views.Windows.MainWindow>();
            Assert.Equal(ToolTitles, Titles(window).Skip(4));

            var items = window.GetNavigation().MenuItems.Cast<Wpf.Ui.Controls.NavigationViewItem>().Skip(4).ToList();
            Assert.All(items, i => Assert.Null(i.TargetPageType));
            Assert.Equal(8, items.Select(i => ((Wpf.Ui.Controls.SymbolIcon)i.Icon!).Symbol).Distinct().Count());

            var dp = items.Single(i => (string)i.Content == "DP Content Manager");
            var menu = dp.ContextMenu!;
            var hide = menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(m => (string)m.Header == "Hide DP Content Manager");
            hide.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent, hide));
            Assert.DoesNotContain("DP Content Manager", Titles(window));
            Assert.Equal(11, Titles(window).Length);

            var first = (Wpf.Ui.Controls.NavigationViewItem)window.GetNavigation().MenuItems[0]!;
            var show = first.ContextMenu!.Items.OfType<System.Windows.Controls.MenuItem>().Single(m => (string)m.Header == "Show tool");
            var entry = show.Items.OfType<System.Windows.Controls.MenuItem>().Single(m => (string)m.Header == "DP Content Manager");
            entry.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent, entry));
            Assert.Equal(["Packages", "Applications", "Add App", "Application Workbench", .. ToolTitles], Titles(window));
            window.Close();
        });
    }

    [Fact]
    public void AToolEntryClickRunsTheSuiteLauncherWithTheDesktopSiteValues()
    {
        StaTestHelper.Run(() =>
        {
            var files = new FakeSuiteFileSystem();
            var environment = new FakeSuiteEnvironment();
            var starter = new FakeToolProcessStarter();
            var root = Path.Combine(environment.LocalApplicationData, "AppPackagerSuite");
            files.Add(Path.Combine(root, "app-packager-suite", "start-suite.ps1"))
                .Add(environment.WindowsPowerShellPath)
                .Add(Path.Combine(root, "site-hygiene", "start-sitehygiene.ps1"));
            using var provider = MainWindowServices(s =>
            {
                s.AddSingleton<ISuiteFileSystem>(files);
                s.AddSingleton<ISuiteEnvironment>(environment);
                s.AddSingleton<IToolProcessStarter>(starter);
            });
            var settings = provider.GetRequiredService<SettingsService>();
            var saved = settings.Load();
            saved.SiteCode = "LAB";
            saved.ProviderMachine = "cm01.lab.local";
            settings.SaveOperatorEdit(saved);

            var window = provider.GetRequiredService<ApplicationPackagerUI.Views.Windows.MainWindow>();
            var item = window.GetNavigation().MenuItems.Cast<Wpf.Ui.Controls.NavigationViewItem>().Single(i => (string)i.Content == "Site Hygiene");
            item.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, item));
            item.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, item));

            var info = Assert.Single(starter.Started);
            Assert.Equal(Path.Combine(root, "site-hygiene"), info.WorkingDirectory);
            Assert.Equal("LAB", info.Environment[SettingsService.LauncherSiteCodeVariable]);
            Assert.Equal("cm01.lab.local", info.Environment[SettingsService.LauncherProviderVariable]);
            Assert.Equal(1, starter.Processes[0].FrontRequests);
            window.Close();
        });
    }

    [Fact]
    public void TheSuiteLauncherIsRegisteredOverTheRealSeams()
    {
        using var provider = new ServiceCollection().AddPackagerServices().AddSidebar().BuildServiceProvider();
        Assert.Same(provider.GetRequiredService<SuiteToolLauncher>(), provider.GetRequiredService<SuiteToolLauncher>());
        Assert.IsType<SuiteFileSystem>(provider.GetRequiredService<ISuiteFileSystem>());
        Assert.IsType<SuiteEnvironment>(provider.GetRequiredService<ISuiteEnvironment>());
        Assert.IsType<ToolProcessStarter>(provider.GetRequiredService<IToolProcessStarter>());
    }
}
