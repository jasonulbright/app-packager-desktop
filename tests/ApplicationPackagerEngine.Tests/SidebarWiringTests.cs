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
                     typeof(ApplicationPackagerUI.Views.Pages.AddAppPage), typeof(ApplicationPackagerUI.Views.Pages.AddFilePage), typeof(ApplicationPackagerUI.Views.Pages.SettingsPage),
                     typeof(ApplicationPackagerUI.Views.Pages.DetectionTesterPage), typeof(ApplicationPackagerUI.Views.Pages.MaintenanceWindowsPage),
                     typeof(ApplicationPackagerUI.Views.Pages.CollectionCompliancePage), typeof(ApplicationPackagerUI.Views.Pages.InstallerAnalysisPage),
                     typeof(ApplicationPackagerUI.Views.Pages.DeploymentHelperPage), typeof(ApplicationPackagerUI.Views.Pages.DpContentManagerPage),
                     typeof(ApplicationPackagerUI.Views.Pages.PivotsScriptsPage), typeof(ApplicationPackagerUI.Views.Pages.SiteHygienePage) })
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
            Assert.Equal(["TOOLS"], navigation.MenuItems.OfType<ApplicationPackagerUI.Views.Windows.SidebarGroupCaption>().Select(c => c.Text.Text));
            var settings = Assert.IsType<Wpf.Ui.Controls.NavigationViewItem>(Assert.Single(navigation.FooterMenuItems.Cast<object>()));
            Assert.Equal("Settings", settings.Content);
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.SettingsPage), settings.TargetPageType);
            Assert.DoesNotContain(navigation.MenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>(), i => (string)i.Content == "Settings");
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
                provider.GetRequiredService<SidebarService>().Hide(SidebarIds.SiteHygiene);
                Assert.Empty(snackbar.Shown);
                window.Close();
            }

            WriteSettings("{ this is not json");
            using (var provider = MainWindowServices(s => s.AddSingleton<Wpf.Ui.ISnackbarService>(snackbar)))
            {
                var window = provider.GetRequiredService<ApplicationPackagerUI.Views.Windows.MainWindow>();
                provider.GetRequiredService<SidebarService>().Hide(SidebarIds.Workbench);
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
            var first = navigation.MenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>().First();
            Assert.Equal("App Packager", first.Content);
            Assert.Same(first, navigation.SelectedItem);
            Assert.True(first.IsActive);
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.PackagesPage), ShownPage(navigation));
            Assert.DoesNotContain("Applications", Titles(window));
            Assert.Contains("Add from web", Titles(window));
            Assert.Equal("Workbench", Titles(window)[1]);
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
            var items = navigation.MenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>().ToList();
            var packages = items.Single(i => (string)i.Content == "App Packager");
            var tester = items.Single(i => (string)i.Content == "Detection Tester");

            Assert.True(navigation.Navigate(typeof(ApplicationPackagerUI.Views.Pages.DetectionTesterPage)));
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.DetectionTesterPage), ShownPage(navigation));
            Assert.True(tester.IsActive);

            var hide = tester.ContextMenu!.Items.OfType<System.Windows.Controls.MenuItem>().Single(m => (string)m.Header == "Hide Detection Tester");
            hide.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent, hide));
            Assert.DoesNotContain("Detection Tester", Titles(window));
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.PackagesPage), ShownPage(navigation));
            Assert.Same(packages, navigation.SelectedItem);
            Assert.False(tester.IsActive);

            var show = packages.ContextMenu!.Items.OfType<System.Windows.Controls.MenuItem>().Single(m => (string)m.Header == "Show tool");
            var entry = show.Items.OfType<System.Windows.Controls.MenuItem>().Single(m => (string)m.Header == "Detection Tester");
            entry.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent, entry));
            Assert.Equal("Detection Tester", Titles(window)[5]);
            Assert.False(tester.IsActive);
            Assert.True(packages.IsActive);
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.PackagesPage), ShownPage(navigation));
        });
    }

    private static readonly string[] ToolTitles =
    [
        "Installer Analysis", "Detection Tester", "Deployment Helper", "Pivots and Scripts",
        "Collection and Compliance", "Maintenance Windows", "DP Content Manager", "Site Hygiene"
    ];

    private static string[] Titles(ApplicationPackagerUI.Views.Windows.MainWindow window) =>
        window.GetNavigation().MenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>().Select(i => (string)i.Content).ToArray();

    [Fact]
    public void TheMainWindowBuildsItsEntriesFromTheServiceAndFollowsAHide()
    {
        StaTestHelper.Run(() =>
        {
            using var provider = MainWindowServices();

            var window = provider.GetRequiredService<ApplicationPackagerUI.Views.Windows.MainWindow>();
            var items = window.GetNavigation().MenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>().ToList();
            Assert.Equal(["App Packager", "Add from web", "Add from file", "Workbench", .. ToolTitles], items.Select(i => (string)i.Content));
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.PackagesPage), items[0].TargetPageType);
            Assert.Null(items[3].TargetPageType);

            provider.GetRequiredService<SidebarService>().Hide(SidebarIds.SiteHygiene);
            Assert.Equal(11, Titles(window).Length);
            Assert.Equal(12, window.GetNavigation().MenuItems.Count);
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

            var items = window.GetNavigation().MenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>().Skip(4).ToList();
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.InstallerAnalysisPage), items[0].TargetPageType);
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.DetectionTesterPage), items[1].TargetPageType);
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.DeploymentHelperPage), items[2].TargetPageType);
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.PivotsScriptsPage), items[3].TargetPageType);
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.CollectionCompliancePage), items[4].TargetPageType);
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.MaintenanceWindowsPage), items[5].TargetPageType);
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.DpContentManagerPage), items[6].TargetPageType);
            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.SiteHygienePage), items[7].TargetPageType);
            Assert.Equal(8, items.Count);
            Assert.Equal(8, items.Select(i => ((Wpf.Ui.Controls.SymbolIcon)i.Icon!).Symbol).Distinct().Count());

            var dp = items.Single(i => (string)i.Content == "DP Content Manager");
            var menu = dp.ContextMenu!;
            var hide = menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(m => (string)m.Header == "Hide DP Content Manager");
            hide.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent, hide));
            Assert.DoesNotContain("DP Content Manager", Titles(window));
            Assert.Equal(11, Titles(window).Length);

            var first = window.GetNavigation().MenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>().First();
            var show = first.ContextMenu!.Items.OfType<System.Windows.Controls.MenuItem>().Single(m => (string)m.Header == "Show tool");
            var entry = show.Items.OfType<System.Windows.Controls.MenuItem>().Single(m => (string)m.Header == "DP Content Manager");
            entry.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent, entry));
            Assert.Equal(["App Packager", "Add from web", "Add from file", "Workbench", .. ToolTitles], Titles(window));
            window.Close();
        });
    }

    [Theory]
    [InlineData("Add from web", typeof(ApplicationPackagerUI.Views.Pages.AddAppPage))]
    [InlineData("Add from file", typeof(ApplicationPackagerUI.Views.Pages.AddFilePage))]
    public void TheAddEntriesOpenTheirPages(string title, Type page)
    {
        StaTestHelper.Run(() =>
        {
            WriteSettings("{ \"FirstRunCompleted\": true }");
            using var provider = MainWindowServices(AddPlainPages);
            var window = OffScreenMainWindow(provider);
            ApplicationPackagerUI.Services.ApplicationHostService.ShowMainWindow(window);
            var navigation = window.GetNavigation();
            var item = navigation.MenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>().Single(i => (string)i.Content == title);

            typeof(Wpf.Ui.Controls.NavigationViewItem).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(item, null);

            Assert.Equal(page, ShownPage(navigation));
            Assert.True(item.IsActive);
            Assert.Equal(["Hide " + title, "Reset sidebar"], item.ContextMenu!.Items.OfType<System.Windows.Controls.MenuItem>().Select(m => (string)m.Header));
        });
    }

    [Fact]
    public void TheAddFromFilePageShowsItsHeader()
    {
        Compliance.RenderKit.WithApplication(Wpf.Ui.Appearance.ApplicationTheme.Light, () =>
        {
            var viewModel = new ApplicationPackagerUI.ViewModels.Pages.AddFileViewModel(service: new AddFlow.FakeAddService(),
                prompts: new AddFlow.FakeAddPrompts(), results: new ApplicationPackagerUI.ViewModels.Pages.AddResultViewModel(new AddFlow.FakeResultActions()));
            var page = new ApplicationPackagerUI.Views.Pages.AddFilePage(viewModel);
            static IEnumerable<object> All(object node) =>
                node is System.Windows.DependencyObject d ? System.Windows.LogicalTreeHelper.GetChildren(d).Cast<object>().SelectMany(c => All(c).Prepend(c)) : [];
            var header = Assert.Single(All(page).OfType<System.Windows.Controls.TextBlock>(),
                t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == "txtAddFileHeader");
            Assert.Equal("Add from file", header.Text);
        });
    }

    [Fact]
    public void TheDetectionTesterEntryOpensItsPage()
    {
        StaTestHelper.Run(() =>
        {
            WriteSettings("{ \"FirstRunCompleted\": true }");
            using var provider = MainWindowServices(AddPlainPages);
            var window = OffScreenMainWindow(provider);
            ApplicationPackagerUI.Services.ApplicationHostService.ShowMainWindow(window);
            var navigation = window.GetNavigation();
            var item = navigation.MenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>().Single(i => (string)i.Content == "Detection Tester");

            typeof(Wpf.Ui.Controls.NavigationViewItem).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(item, null);

            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.DetectionTesterPage), ShownPage(navigation));
            Assert.True(item.IsActive);
        });
    }

    [Fact]
    public void TheMaintenanceWindowsEntryOpensItsPage()
    {
        StaTestHelper.Run(() =>
        {
            WriteSettings("{ \"FirstRunCompleted\": true }");
            using var provider = MainWindowServices(AddPlainPages);
            var window = OffScreenMainWindow(provider);
            ApplicationPackagerUI.Services.ApplicationHostService.ShowMainWindow(window);
            var navigation = window.GetNavigation();
            var item = navigation.MenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>().Single(i => (string)i.Content == "Maintenance Windows");

            typeof(Wpf.Ui.Controls.NavigationViewItem).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(item, null);

            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.MaintenanceWindowsPage), ShownPage(navigation));
            Assert.True(item.IsActive);
        });
    }

    [Fact]
    public void TheCollectionAndComplianceEntryOpensItsPage()
    {
        StaTestHelper.Run(() =>
        {
            WriteSettings("{ \"FirstRunCompleted\": true }");
            using var provider = MainWindowServices(AddPlainPages);
            var window = OffScreenMainWindow(provider);
            ApplicationPackagerUI.Services.ApplicationHostService.ShowMainWindow(window);
            var navigation = window.GetNavigation();
            var item = navigation.MenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>().Single(i => (string)i.Content == "Collection and Compliance");

            typeof(Wpf.Ui.Controls.NavigationViewItem).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(item, null);

            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.CollectionCompliancePage), ShownPage(navigation));
            Assert.True(item.IsActive);
        });
    }

    [Fact]
    public void TheInstallerAnalysisEntryOpensItsPage()
    {
        StaTestHelper.Run(() =>
        {
            WriteSettings("{ \"FirstRunCompleted\": true }");
            using var provider = MainWindowServices(AddPlainPages);
            var window = OffScreenMainWindow(provider);
            ApplicationPackagerUI.Services.ApplicationHostService.ShowMainWindow(window);
            var navigation = window.GetNavigation();
            var item = navigation.MenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>().Single(i => (string)i.Content == "Installer Analysis");

            typeof(Wpf.Ui.Controls.NavigationViewItem).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(item, null);

            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.InstallerAnalysisPage), ShownPage(navigation));
            Assert.True(item.IsActive);
        });
    }

    [Fact]
    public void TheDeploymentHelperEntryOpensItsPage()
    {
        StaTestHelper.Run(() =>
        {
            WriteSettings("{ \"FirstRunCompleted\": true }");
            using var provider = MainWindowServices(AddPlainPages);
            var window = OffScreenMainWindow(provider);
            ApplicationPackagerUI.Services.ApplicationHostService.ShowMainWindow(window);
            var navigation = window.GetNavigation();
            var item = navigation.MenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>().Single(i => (string)i.Content == "Deployment Helper");

            typeof(Wpf.Ui.Controls.NavigationViewItem).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(item, null);

            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.DeploymentHelperPage), ShownPage(navigation));
            Assert.True(item.IsActive);
        });
    }

    [Fact]
    public void ASavedLayoutWithTheDeploymentHelperEntryMovedAndHiddenKeepsItsPlaceAsAPage()
    {
        WriteSettings("""
            { "FirstRunCompleted": true,
              "Sidebar": { "Order": ["packages", "deployment-helper", "applications"], "Hidden": ["deployment-helper"] } }
            """);
        using var provider = new ServiceCollection().AddPackagerServices().AddSidebar().BuildServiceProvider();
        var service = provider.GetRequiredService<SidebarService>();
        Assert.Equal([SidebarIds.Packages, SidebarIds.AddApp, SidebarIds.AddFile, SidebarIds.Workbench, SidebarIds.DeploymentHelper], SidebarFixtures.Ids(service.All).Take(5));
        Assert.True(service.IsHidden(SidebarIds.DeploymentHelper));
        Assert.True(service.Show(SidebarIds.DeploymentHelper));
        Assert.Equal(ApplicationPackagerUI.Models.SidebarItemKind.Page, service.Items[4].Kind);
        Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.DeploymentHelperPage), service.Items[4].PageType);
    }

    [Fact]
    public void TheDpContentManagerEntryOpensItsPage()
    {
        StaTestHelper.Run(() =>
        {
            WriteSettings("{ \"FirstRunCompleted\": true }");
            using var provider = MainWindowServices(AddPlainPages);
            var window = OffScreenMainWindow(provider);
            ApplicationPackagerUI.Services.ApplicationHostService.ShowMainWindow(window);
            var navigation = window.GetNavigation();
            var item = navigation.MenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>().Single(i => (string)i.Content == "DP Content Manager");

            typeof(Wpf.Ui.Controls.NavigationViewItem).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(item, null);

            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.DpContentManagerPage), ShownPage(navigation));
            Assert.True(item.IsActive);
        });
    }

    [Fact]
    public void ASavedLayoutWithTheDpContentManagerEntryMovedAndHiddenKeepsItsPlaceAsAPage()
    {
        WriteSettings("""
            { "FirstRunCompleted": true,
              "Sidebar": { "Order": ["packages", "dp-content-manager", "applications"], "Hidden": ["dp-content-manager"] } }
            """);
        using var provider = new ServiceCollection().AddPackagerServices().AddSidebar().BuildServiceProvider();
        var service = provider.GetRequiredService<SidebarService>();
        Assert.Equal([SidebarIds.Packages, SidebarIds.AddApp, SidebarIds.AddFile, SidebarIds.Workbench, SidebarIds.DpContentManager], SidebarFixtures.Ids(service.All).Take(5));
        Assert.True(service.IsHidden(SidebarIds.DpContentManager));
        Assert.True(service.Show(SidebarIds.DpContentManager));
        Assert.Equal(ApplicationPackagerUI.Models.SidebarItemKind.Page, service.Items[4].Kind);
        Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.DpContentManagerPage), service.Items[4].PageType);
    }

    [Fact]
    public void ThePivotsAndScriptsEntryOpensItsPage()
    {
        StaTestHelper.Run(() =>
        {
            WriteSettings("{ \"FirstRunCompleted\": true }");
            using var provider = MainWindowServices(AddPlainPages);
            var window = OffScreenMainWindow(provider);
            ApplicationPackagerUI.Services.ApplicationHostService.ShowMainWindow(window);
            var navigation = window.GetNavigation();
            var item = navigation.MenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>().Single(i => (string)i.Content == "Pivots and Scripts");

            typeof(Wpf.Ui.Controls.NavigationViewItem).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(item, null);

            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.PivotsScriptsPage), ShownPage(navigation));
            Assert.True(item.IsActive);
        });
    }

    [Fact]
    public void ASavedLayoutWithThePivotsAndScriptsEntryMovedAndHiddenKeepsItsPlaceAsAPage()
    {
        WriteSettings("""
            { "FirstRunCompleted": true,
              "Sidebar": { "Order": ["packages", "pivots-and-scripts", "applications"], "Hidden": ["pivots-and-scripts"] } }
            """);
        using var provider = new ServiceCollection().AddPackagerServices().AddSidebar().BuildServiceProvider();
        var service = provider.GetRequiredService<SidebarService>();
        Assert.Equal([SidebarIds.Packages, SidebarIds.AddApp, SidebarIds.AddFile, SidebarIds.Workbench, SidebarIds.PivotsScripts], SidebarFixtures.Ids(service.All).Take(5));
        Assert.True(service.IsHidden(SidebarIds.PivotsScripts));
        Assert.True(service.Show(SidebarIds.PivotsScripts));
        Assert.Equal(ApplicationPackagerUI.Models.SidebarItemKind.Page, service.Items[4].Kind);
        Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.PivotsScriptsPage), service.Items[4].PageType);
    }

    [Fact]
    public void ASavedLayoutWithTheMaintenanceWindowsEntryMovedAndHiddenKeepsItsPlaceAsAPage()    {
        WriteSettings("""
            { "FirstRunCompleted": true,
              "Sidebar": { "Order": ["packages", "maintenance-windows", "applications"], "Hidden": ["maintenance-windows"] } }
            """);
        using var provider = new ServiceCollection().AddPackagerServices().AddSidebar().BuildServiceProvider();
        var service = provider.GetRequiredService<SidebarService>();
        Assert.Equal([SidebarIds.Packages, SidebarIds.AddApp, SidebarIds.AddFile, SidebarIds.Workbench, SidebarIds.MaintenanceWindows], SidebarFixtures.Ids(service.All).Take(5));
        Assert.True(service.IsHidden(SidebarIds.MaintenanceWindows));
        Assert.True(service.Show(SidebarIds.MaintenanceWindows));
        Assert.Equal(ApplicationPackagerUI.Models.SidebarItemKind.Page, service.Items[4].Kind);
        Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.MaintenanceWindowsPage), service.Items[4].PageType);
    }

    [Fact]
    public void TheSiteHygieneEntryOpensItsPage()
    {
        StaTestHelper.Run(() =>
        {
            WriteSettings("{ \"FirstRunCompleted\": true }");
            using var provider = MainWindowServices(AddPlainPages);
            var window = OffScreenMainWindow(provider);
            ApplicationPackagerUI.Services.ApplicationHostService.ShowMainWindow(window);
            var navigation = window.GetNavigation();
            var item = navigation.MenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>().Single(i => (string)i.Content == "Site Hygiene");

            typeof(Wpf.Ui.Controls.NavigationViewItem).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(item, null);

            Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.SiteHygienePage), ShownPage(navigation));
            Assert.True(item.IsActive);
        });
    }

    [Fact]
    public void ASavedLayoutWithTheSiteHygieneEntryMovedAndHiddenKeepsItsPlaceAsAPage()
    {
        WriteSettings("""
            { "FirstRunCompleted": true,
              "Sidebar": { "Order": ["packages", "site-hygiene", "applications"], "Hidden": ["site-hygiene"] } }
            """);
        using var provider = new ServiceCollection().AddPackagerServices().AddSidebar().BuildServiceProvider();
        var service = provider.GetRequiredService<SidebarService>();
        Assert.Equal([SidebarIds.Packages, SidebarIds.AddApp, SidebarIds.AddFile, SidebarIds.Workbench, SidebarIds.SiteHygiene], SidebarFixtures.Ids(service.All).Take(5));
        Assert.True(service.IsHidden(SidebarIds.SiteHygiene));
        Assert.True(service.Show(SidebarIds.SiteHygiene));
        Assert.Equal(ApplicationPackagerUI.Models.SidebarItemKind.Page, service.Items[4].Kind);
        Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.SiteHygienePage), service.Items[4].PageType);
        Assert.DoesNotContain(service.All, i => i.Kind == ApplicationPackagerUI.Models.SidebarItemKind.Tool);
    }
}
