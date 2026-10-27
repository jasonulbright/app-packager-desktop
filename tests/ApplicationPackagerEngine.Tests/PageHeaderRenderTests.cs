using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.AddFlow;
using ApplicationPackagerEngine.Tests.Collections;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerEngine.Tests.Deployments;
using ApplicationPackagerEngine.Tests.DpContent;
using ApplicationPackagerEngine.Tests.InstallerAnalysis;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerEngine.Tests.PivotsScripts;
using ApplicationPackagerEngine.Tests.SiteHygiene;
using ApplicationPackagerUI.Controls;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using Application = System.Windows.Application;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridRow = System.Windows.Controls.DataGridRow;
using Page = System.Windows.Controls.Page;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Every sidebar page in a real window, in both themes and at both sizes: the shared page header shows the title in the
/// primary text color, and the header leaves the rest of the page to the content.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PageHeaderRenderTests : IDisposable
{
    /// <summary>The main window gives this width to the open navigation pane and its border; the page gets the rest.</summary>
    private const double NavigationPaneWidth = 230;

    /// <summary>The height of the window frame the main window keeps around the page.</summary>
    private const double FrameHeight = 48;

    /// <summary>The header never takes more height than this on a 900x600 window.</summary>
    private const double CompactHeaderMaximum = 96;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-header-render-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public PageHeaderRenderTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _savedRoot);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    public static readonly string[] PageNames =
    [
        "App Packager", "Add from web", "Add from file", "Settings", "Installer Analysis", "Detection Tester", "Deployment Helper",
        "Pivots and Scripts", "Collection and Compliance", "Maintenance Windows", "DP Content Manager", "Site Hygiene"
    ];

    public static TheoryData<string, ApplicationTheme, int, int> Cases()
    {
        var data = new TheoryData<string, ApplicationTheme, int, int>();
        foreach (var name in PageNames)
            foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
                foreach (var (width, height) in new[] { (1440, 900), (900, 600) })
                    data.Add(name, theme, width, height);
        return data;
    }

    private static string TitleOf(string name) => name;

    /// <summary>The page and anything that the test must dispose after it.</summary>
    private (Page Page, IDisposable? Owner) Create(string name)
    {
        switch (name)
        {
            case "App Packager":
            {
                Application.Current.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
                var viewModel = (PackagesViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
                viewModel.FilteredPackagers = new(Enumerable.Range(0, 8).Select(i => new PackagerInfo { Uid = $"app{i}", Vendor = "Contoso", Application = $"Contoso Application {i}" }));
                return (new PackagesPage(viewModel), null);
            }
            case "Add from web":
            {
                var settings = new SettingsService();
                settings.ApplyRunOverrides(s => s.DownloadRoot = Path.Combine(_root, "downloads"));
                var viewModel = new AddAppViewModel(settingsService: settings, snackbarService: new FakeSnackbar(), service: new FakeAddService(),
                    results: new AddResultViewModel(new FakeResultActions())) { DefinitionsRoot = Path.Combine(_root, "definitions") };
                return (new AddAppPage(viewModel), null);
            }
            case "Add from file":
            {
                var viewModel = new AddFileViewModel(service: new FakeAddService(), prompts: new FakeAddPrompts(), results: new AddResultViewModel(new FakeResultActions()))
                { DefinitionsRoot = _root };
                return (new AddFilePage(viewModel), null);
            }
            case "Settings":
            {
                // The full Settings view model queries the GitHub API when it is built; the compiled page runs over the sidebar editor alone.
                var page = (SettingsPage)RuntimeHelpers.GetUninitializedObject(typeof(SettingsPage));
                typeof(Page).GetConstructor(Type.EmptyTypes)!.Invoke(page, null);
                page.DataContext = new { Sidebar = new SidebarSettingsEditor(new SidebarService(SidebarRegistry.CreateDefault(), new InMemorySidebarStore(null))) };
                page.InitializeComponent();
                return (page, null);
            }
            case "Installer Analysis":
            {
                var harness = new InstallerAnalysisHarness();
                return (new InstallerAnalysisPage(harness.Create()), harness);
            }
            case "Detection Tester": return (new DetectionTesterPage(new DetectionTesterHarness().Create()), null);
            case "Deployment Helper": return (new DeploymentHelperPage(new DeploymentPageHarness().Create()), null);
            case "Pivots and Scripts": return (new PivotsScriptsPage(new PivotsScriptsHarness().Create()), null);
            case "Collection and Compliance": return (new CollectionCompliancePage(new CollectionComplianceHarness().Create(), new ComplianceHarness().Create()), null);
            case "Maintenance Windows": return (new MaintenanceWindowsPage(new MaintenanceWindowsHarness().Create()), null);
            case "DP Content Manager": return (new DpContentManagerPage(new DpContentPageHarness().Create()), null);
            case "Site Hygiene": return (new SiteHygienePage(new SiteHygienePageHarness().Create()), null);
            default: throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown page.");
        }
    }

    /// <summary>The Settings page binds to members of the full view model, so a binding error is not a defect of that case.</summary>
    private static void WithThemeOnly(ApplicationTheme theme, Action test)
    {
        StaTestHelper.Run(() =>
        {
            Assert.Null(Application.Current);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                application.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = theme });
                application.Resources.MergedDictionaries.Add(new ControlsDictionary());
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(ToolTipStyles, UriKind.Absolute) });
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                test();
            }
            finally
            {
                application.Resources = new ResourceDictionary();
                var statics = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                typeof(Application).GetField("_appInstance", statics)!.SetValue(null, null);
                typeof(Application).GetField("_appCreatedInThisAppDomain", statics)!.SetValue(null, false);
            }
        });
    }

    private static void With(string name, ApplicationTheme theme, Action test)
    {
        if (name == "Settings") WithThemeOnly(theme, test);
        else WithApplication(theme, test);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryPageTitleIsReadableAndTheHeaderStaysSmall(string name, ApplicationTheme theme, int width, int height)
    {
        With(name, theme, () =>
        {
            var (page, owner) = Create(name);
            var window = new Window { Width = width - NavigationPaneWidth, Height = height - FrameHeight, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Settle(window);
                var where = $"{name} {theme} {width}x{height}";
                var header = Descendants(page).OfType<PageHeader>().First();
                Assert.Equal(TitleOf(name), header.Title);
                PageChromeKit.AssertTitle(window, header.TitleElement, where);
                if (height <= 600)
                    Assert.True(header.ActualHeight <= CompactHeaderMaximum, $"{where}: the page header is {header.ActualHeight:0} px tall; the limit is {CompactHeaderMaximum} px.");
            }
            finally
            {
                window.Close();
                owner?.Dispose();
            }
        });
    }
}
