using System.Xml.Linq;
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

    private static XDocument Markup(params string[] relative)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        string Full(DirectoryInfo f) => Path.Combine([f.FullName, "src", "ApplicationPackagerUI", .. relative]);
        while (folder is not null && !File.Exists(Full(folder))) folder = folder.Parent;
        Assert.NotNull(folder);
        return XDocument.Load(Full(folder));
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

    [Fact]
    public void TheMainWindowMarkupDeclaresNoEntriesAndKeepsSettingsInTheFooter()
    {
        var doc = Markup("Views", "Windows", "MainWindow.xaml");
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "NavigationView.MenuItems");
        var footer = doc.Descendants().Single(e => e.Name.LocalName == "NavigationView.FooterMenuItems");
        var settings = Assert.Single(footer.Elements());
        Assert.Equal("Settings", (string?)settings.Attribute("Content"));
        Assert.Equal("{x:Type pages:SettingsPage}", (string?)settings.Attribute("TargetPageType"));
    }

    [Fact]
    public void TheSettingsPageHasASidebarSectionBoundToTheEditor()
    {
        var doc = Markup("Views", "Pages", "SettingsPage.xaml");
        Assert.Contains(doc.Descendants(), e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == "Sidebar");
        var list = doc.Descendants().Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "lstSidebar");
        Assert.Equal("{Binding Sidebar.Rows}", (string?)list.Attribute("ItemsSource"));
        var template = list.ToString();
        Assert.Contains("{Binding Shown", template);
        Assert.Contains("{Binding CanHide}", template);
        Assert.Contains("{Binding MoveUpCommand}", template);
        Assert.Contains("{Binding MoveDownCommand}", template);
        var reset = doc.Descendants().Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "btnResetSidebar");
        Assert.Equal("{Binding Sidebar.ResetCommand}", (string?)reset.Attribute("Command"));
    }

    [Fact]
    public void EveryIconOnTheSettingsPageNamesAnExistingSymbol()
    {
        var text = Markup("Views", "Pages", "SettingsPage.xaml").ToString();
        var names = System.Text.RegularExpressions.Regex.Matches(text, @"\{ui:SymbolIcon (\w+)\}").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Contains("ArrowReset24", names);
        Assert.All(names, n => Assert.True(Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(n, out _), n));
    }

    [Theory]
    [InlineData(Wpf.Ui.Appearance.ApplicationTheme.Light)]
    [InlineData(Wpf.Ui.Appearance.ApplicationTheme.Dark)]
    public void TheSidebarSectionRendersOneRowPerEntryWithThePinnedRowLocked(Wpf.Ui.Appearance.ApplicationTheme theme)
    {
        var list = Markup("Views", "Pages", "SettingsPage.xaml").Descendants()
            .Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "lstSidebar").ToString();
        var host =
            "<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:ui=\"http://schemas.lepo.co/wpfui/2022/xaml\">"
            + "<Grid.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>"
            + $"<ui:ThemesDictionary Theme=\"{theme}\" /><ui:ControlsDictionary />"
            + "</ResourceDictionary.MergedDictionaries></ResourceDictionary></Grid.Resources>"
            + list + "</Grid>";
        StaTestHelper.Run(() =>
        {
            var service = new SidebarService(SidebarFixtures.Registry(), new InMemorySidebarStore());
            var grid = (System.Windows.Controls.Grid)System.Windows.Markup.XamlReader.Parse(host);
            grid.DataContext = new { Sidebar = new ApplicationPackagerUI.ViewModels.Pages.SidebarSettingsEditor(service) };
            grid.Measure(new System.Windows.Size(700, 600));
            grid.Arrange(new System.Windows.Rect(0, 0, 700, 600));
            grid.UpdateLayout();

            var boxes = Descendants(grid).OfType<System.Windows.Controls.CheckBox>().ToList();
            Assert.Equal(["Home", "Alpha", "Bravo", "Window", "Tool"], boxes.Select(b => (string)b.Content));
            Assert.False(boxes[0].IsEnabled);
            Assert.All(boxes, b => Assert.True(b.IsChecked));
            Assert.True(boxes[1].IsEnabled);

            boxes[2].IsChecked = false;
            Assert.True(service.IsHidden("b"));
        });
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

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}
