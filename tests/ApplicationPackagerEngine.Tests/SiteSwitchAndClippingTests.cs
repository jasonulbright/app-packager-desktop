using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerEngine.Tests.Deployments;
using ApplicationPackagerEngine.Tests.Wsus;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using Brushes = System.Windows.Media.Brushes;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using CheckBox = System.Windows.Controls.CheckBox;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;

namespace ApplicationPackagerEngine.Tests;

/// <summary>What follows a switch of site, and the columns and cards that cut their content.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SiteSwitchAndClippingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apd-switch-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved = [];

    public SiteSwitchAndClippingTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, SettingsService.LauncherSiteCodeVariable, SettingsService.LauncherProviderVariable, SiteDataPaths.AppSiteVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, null);
        Environment.SetEnvironmentVariable(SiteDataPaths.AppSiteVariable, null);
        Directory.CreateDirectory(AppDataPaths.Root);
    }

    public void Dispose()
    {
        SiteContext.Deactivate();
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        TestCleanup.DeleteDirectory(_root);
    }

    private static void Save(SettingsService service, string code, bool configMgr, bool wsus)
    {
        var s = service.Load();
        s.SiteCode = code;
        s.ProviderMachine = $"cm.{code.ToLowerInvariant()}.example.com";
        s.FileShareRoot = $@"\\files.{code.ToLowerInvariant()}.example.com\content$";
        s.Systems = new SystemsInUse { ConfigMgr = configMgr, Wsus = wsus };
        s.Wsus.ServerName = "wsus01.contoso.com";
        service.SaveOperatorEdit(s);
    }

    private static PackagesViewModel Packages(SettingsService settings, PublishRunKit.Dialogs dialogs, params PackagerInfo[] rows)
    {
        var viewModel = PublishRunKit.Create(settings, dialogs, rows);
        PublishRunKit.Set(viewModel, "_siteState", new SiteStateService(settings.Load, () => false));
        return viewModel;
    }

    [Fact]
    public void AfterASwitchEveryTextOfThePageNamesTheNewSiteAndNoRowStaysChecked()
    {
        using var scope = SiteContext.Scope(null);
        var settings = new SettingsService();
        Save(settings, "AB1", configMgr: true, wsus: false);
        var manager = new SiteManager(() => null);
        Assert.True(manager.AddSite("CD2", "cm.cd2.example.com", null, _ => throw new InvalidOperationException("No code is reused.")).Succeeded);
        Save(settings, "CD2", configMgr: true, wsus: false);
        var row = new PackagerInfo { Uid = "a", Application = "Alpha", CMName = "Alpha", LatestVersion = "2.0", CurrentVersion = "1.0", IsSelected = true };
        var viewModel = Packages(settings, new PublishRunKit.Dialogs(), row);
        viewModel.RefreshPublishActionState();
        viewModel.StatusText = "Published to site CD2.";
        Assert.Contains("Site CD2", viewModel.PublishScopeText);
        Assert.Contains("Site CD2", viewModel.DetailPublishingText);

        Assert.True(manager.Switch("AB1").Succeeded);
        viewModel.FollowSiteChange();

        foreach (var text in new[] { viewModel.PublishScopeText, viewModel.DetailPublishingText })
        {
            Assert.Contains("Site AB1", text);
            Assert.DoesNotContain("CD2", text);
        }
        foreach (var text in new[] { viewModel.StatusText, viewModel.PublishAllToolTip, viewModel.CheckMecmToolTip, viewModel.WsusUpdatesToolTip, viewModel.DiscoverToolTip })
            Assert.DoesNotContain("CD2", text ?? "");
        Assert.False(row.IsSelected, "A row checked in the other site stays checked.");
        Assert.Equal("", row.CurrentVersion);
        Assert.True(viewModel.ShowConfigMgrIndicator);

        // A site with ConfigMgr off hides the ConfigMgr mark before the status line.
        Save(settings, "AB1", configMgr: false, wsus: true);
        viewModel.FollowSiteChange();
        Assert.False(viewModel.ShowConfigMgrIndicator);
        Assert.DoesNotContain("ConfigMgr \u00b7", viewModel.PublishScopeText);
    }

    [Fact]
    public void TheMainWindowFollowsASwitchBeforeItLoadsTheNewList()
    {
        var source = File.ReadAllText(Path.Combine(CatalogRoot(), "src", "ApplicationPackagerUI", "Views", "Windows", "MainWindow.xaml.cs"));
        var follow = source.IndexOf("packages.FollowSiteChange();", StringComparison.Ordinal);
        var load = source.IndexOf("packages.LoadPackagersCommand.Execute(null);", StringComparison.Ordinal);
        Assert.True(follow > 0 && load > follow);
        Assert.Contains("Windows.OfType<Window>().Where(ShowsOneSite)", source);
        Assert.Contains("?.FollowSiteChange();", source);
        Assert.Contains("RenewSitePages();", source);
        Assert.Contains("RootNavigation.SetServiceProvider(_sitePages);", source);
        var app = File.ReadAllText(Path.Combine(CatalogRoot(), "src", "ApplicationPackagerUI", "App.xaml.cs"));
        Assert.Contains("services.AddSingleton(sp => new SitePages(sp, registrations));", app);
        Assert.Contains("window is OneClickPlanWindow or CveWindow or CatalogWindow or ConvertFromCmWindow", source);
    }

    private static string CatalogRoot() => Compliance.CatalogServiceTests.RepositoryRoot();

    [Fact]
    public async Task TheSiteCannotChangeWhileAPublishIsReviewed()
    {
        using var scope = SiteContext.Scope(null);
        var settings = new SettingsService();
        Save(settings, "AB1", configMgr: false, wsus: true);
        string? during = null;
        var dialogs = new PublishRunKit.Dialogs();
        dialogs.Answers.Enqueue(_ =>
        {
            during = new SiteManager().Refusal() is null ? null : string.Join("; ", SiteRunGuard.ActiveRuns());
            return false;
        });
        var row = new PackagerInfo { Uid = "a", Application = "Alpha", LatestVersion = "2.0" };
        var viewModel = Packages(settings, dialogs, row);

        await (Task)typeof(PackagesViewModel).GetMethod("PackageCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, [new List<PackagerInfo> { row }, null])!;

        Assert.NotNull(during);
        Assert.Contains("a publish is being reviewed or run", during);
        Assert.DoesNotContain("a publish is being reviewed or run", SiteRunGuard.ActiveRuns());
    }

    private sealed class CountingRunner : IOneClickRunner
    {
        public int Runs { get; private set; }

        public Task<bool> RunAsync(IReadOnlyList<OneClickPlanItem> rows, OneClickRunOptions options, CancellationToken ct)
        {
            Runs++;
            return Task.FromResult(false);
        }
    }

    [Fact]
    public async Task AOneClickPlanOfAnotherSiteRunsNothing()
    {
        var site = "AB1|CM.AB1.EXAMPLE.COM";
        var runner = new CountingRunner();
        var viewModel = new OneClickPlanViewModel(new OneClickPlanContext
        {
            Apps = [(new OneClickApp("a", "Alpha", "Contoso", "2.0"), (PackagerInfo?)new PackagerInfo { Uid = "a", Application = "Alpha" })],
            Settings = () => new OneClickPlanSettings
            {
                Tracked = ["a"],
                DefaultDestinations = new DestinationSet(true, false, false),
                Readiness = OneClickPlanner.Readiness(new PublishReadiness(new SystemsInUse { ConfigMgr = true }, true, "AB1", @"\\cm\share", "", "", false, "")),
                Scope = new OneClickScopeSettings()
            },
            Action = OneClickActionNames.StageAndPackage,
            PlannedSite = site,
            CurrentSite = () => site,
            ReportFolder = Path.Combine(_root, "reports")
        }, runner);

        site = "CD2|CM.CD2.EXAMPLE.COM";
        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal(0, runner.Runs);
        Assert.Equal(OneClickPlanViewModel.SiteChangedText, viewModel.SummaryText);
        Assert.False(viewModel.IsRunView);
    }

    private static ServiceCollection ToolRegistrations()
    {
        var services = new ServiceCollection();
        services.AddPackagerServices().AddSidebar();
        services.AddSingleton<Wpf.Ui.IContentDialogService, Wpf.Ui.ContentDialogService>();
        services.AddSingleton<IChangePlanDialogs, ChangePlanDialogs>();
        services.AddDetectionTesterPage();
        services.AddMaintenanceWindowsPage();
        services.AddCollectionCompliancePage();
        services.AddDeploymentHelperPage();
        services.AddDpContentManagerPage();
        services.AddPivotsScriptsPage();
        services.AddSiteHygienePage();
        return services;
    }

    [Fact]
    public void EveryToolViewModelIsBuiltNewAfterASwitchAndTheOldSetSavesNothing()
    {
        using var scope = SiteContext.Scope(null);
        var registrations = ToolRegistrations();
        using var provider = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });
        var pages = new SitePages(provider, registrations);
        // Every type of the set has a registration, or the switch would keep its one instance.
        Assert.All(SitePages.Types, type => Assert.True(pages.Holds(type), type.Name + " has no registration."));
        Type[] viewModels =
        [
            typeof(DeploymentHelperViewModel), typeof(ComplianceViewModel), typeof(CollectionComplianceViewModel), typeof(DpContentManagerViewModel),
            typeof(MaintenanceWindowsViewModel), typeof(PivotsScriptsViewModel), typeof(SiteHygieneViewModel), typeof(DetectionTesterViewModel)
        ];
        var first = viewModels.Select(type => pages.GetService(type)!).ToList();
        Assert.All(viewModels.Zip(first), pair => Assert.Same(pair.Second, pages.GetService(pair.First)));
        var choices = (PivotsScriptsPageServices)pages.GetService(typeof(PivotsScriptsPageServices))!;
        var settings = provider.GetRequiredService<SettingsService>();

        pages.Retire();

        Assert.Null(pages.Existing<PivotsScriptsViewModel>());
        Assert.All(viewModels.Zip(first), pair => Assert.NotSame(pair.Second, pages.GetService(pair.First)));
        // A late save of the old set writes nothing into the site that is active now.
        Assert.False(choices.SavePreferences(new PivotsScriptsSettings()));
        Assert.Null(settings.Load().PivotsScripts);
        Assert.True(((PivotsScriptsPageServices)pages.GetService(typeof(PivotsScriptsPageServices))!).SavePreferences(new PivotsScriptsSettings()));
    }

    [Fact]
    public void ANavigationAfterASwitchShowsANewPage()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var root = new ServiceCollection().AddSingleton(typeof(PackagesPage), _ => new System.Windows.Controls.Page()).BuildServiceProvider();
            var tool = new ServiceDescriptor(typeof(DetectionTesterPage), _ => new System.Windows.Controls.Page(), ServiceLifetime.Singleton);
            var pages = new SitePages(root, [tool]);
            var navigation = new Wpf.Ui.Controls.NavigationView();
            navigation.SetServiceProvider(pages);
            navigation.MenuItems.Add(new Wpf.Ui.Controls.NavigationViewItem("Start", typeof(PackagesPage)));
            navigation.MenuItems.Add(new Wpf.Ui.Controls.NavigationViewItem("Tool", typeof(DetectionTesterPage)));
            object? shown = null;
            navigation.Navigated += (_, e) => shown = e.Page;
            var window = new Window { Width = 800, Height = 600, Content = navigation };
            Show(window);
            try
            {
                Settle(window);
                Assert.True(navigation.Navigate(typeof(DetectionTesterPage)));
                Settle(window);
                var first = shown;
                Assert.NotNull(first);

                pages.Retire();
                navigation.Navigate(typeof(PackagesPage));
                Settle(window);
                navigation.Navigate(typeof(DetectionTesterPage));
                Settle(window);

                Assert.NotSame(first, shown);
                Assert.Same(shown, pages.GetService(typeof(DetectionTesterPage)));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task TheCatalogDropsTheOtherSitesBoxesAndSavesOnlyAfterAReadOfTheNewSite()
    {
        var hidden = new[] { "a" };
        IReadOnlyList<PackagerInfo>? list = [new() { Uid = "a", Application = "Alpha", Vendor = "V" }, new() { Uid = "b", Application = "Beta", Vendor = "V" }];
        var saved = new List<string[]>();
        var cautions = new List<string>();
        var catalog = new CatalogViewModel(new CatalogServices(
            LoadSettings: () => new AppSettings { HiddenApplications = hidden },
            SaveSettings: s => saved.Add(s.HiddenApplications),
            LoadPackagers: () => Task.FromResult(list),
            RefreshGrid: () => Task.CompletedTask,
            ShowSaved: _ => { },
            Log: _ => { }) { ShowCaution = cautions.Add });
        await catalog.WaitForReloadAsync();
        Assert.Equal(1, catalog.VisibleCount);

        // The new site hides Beta; its list cannot be read yet.
        hidden = ["b"];
        var answer = list;
        list = null;
        catalog.FollowSiteChange();
        Assert.Empty(catalog.VendorGroups);
        Assert.False(catalog.HasUnsavedChanges);
        await catalog.SaveCommand.ExecuteAsync(null);
        Assert.Empty(saved);
        Assert.Equal(CatalogViewModel.NotReadMessage, cautions.Last());

        list = answer;
        catalog.ReloadAfterSettingsChange();
        await catalog.SaveCommand.ExecuteAsync(null);
        Assert.Equal(["b"], Assert.Single(saved));
    }

    [Fact]
    public void ASwitchLeavesNoCheckResultToExport()
    {
        using var scope = SiteContext.Scope(null);
        var settings = new SettingsService();
        Save(settings, "AB1", configMgr: true, wsus: false);
        var viewModel = Packages(settings, new PublishRunKit.Dialogs());
        var field = typeof(PackagesViewModel).GetField("_lastCheckResult", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var row = Array.CreateInstance(field.FieldType.GetGenericArguments()[0], 1);
        row.SetValue(RuntimeHelpers.GetUninitializedObject(field.FieldType.GetGenericArguments()[0]), 0);
        field.SetValue(viewModel, row);
        Assert.True(viewModel.ExportCheckResultsCommand.CanExecute(null));

        viewModel.FollowSiteChange();

        Assert.False(viewModel.ExportCheckResultsCommand.CanExecute(null));
        var source = File.ReadAllText(Path.Combine(CatalogRoot(), "src", "ApplicationPackagerUI", "ViewModels", "Pages", "SettingsViewModel.cs"));
        Assert.Contains("_packagesViewModel.ForgetSiteResults();", source);
    }

    [Fact]
    public void AWaitForResultsAndABackgroundWriteRefuseASwitch()
    {
        static void Holds(Action<bool> set, string reason)
        {
            set(true);
            Assert.Contains(reason, SiteRunGuard.ActiveRuns());
            set(false);
            Assert.DoesNotContain(reason, SiteRunGuard.ActiveRuns());
        }

        var pivots = new PivotsScripts.PivotsScriptsHarness().Create();
        Holds(value => pivots.IsWaiting = value, "Pivots and Scripts waits for the results of a run");
        Action<bool> Writing(object viewModel) => value => viewModel.GetType()
            .GetMethod("SetWriting", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, [value]);
        Holds(Writing(new MaintenanceWindows.MaintenanceWindowsHarness().Create()), "a maintenance window change is being written");
        Holds(Writing(new Collections.CollectionComplianceHarness().Create()), "a collection change is being written");
        // Every start and end of a write goes through the setter.
        foreach (var file in new[] { "MaintenanceWindowsViewModel.cs", "CollectionComplianceViewModel.cs" })
        {
            var source = File.ReadAllText(Path.Combine(CatalogRoot(), "src", "ApplicationPackagerUI", "ViewModels", "Pages", file));
            Assert.DoesNotContain(" _writing = true;", source);
            Assert.DoesNotContain(" _writing = false;", source);
        }
    }

    private static double TextWidth(TextBlock block, string text) => new FormattedText(text, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
        new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, Brushes.Black,
        VisualTreeHelper.GetDpi(block).PixelsPerDip).Width;

    private static void AssertWholeText(DataGrid grid, TextBlock block, FrameworkElement holder, string where)
    {
        var left = block.TranslatePoint(new System.Windows.Point(0, 0), holder).X;
        Assert.True(block.ActualWidth + 0.5 >= TextWidth(block, block.Text) && left >= -0.5 && left + block.ActualWidth <= holder.ActualWidth + 0.5,
            $"{where}: '{block.Text}' needs {TextWidth(block, block.Text):0} px, has {block.ActualWidth:0} at {left:0} in {holder.ActualWidth:0}.");
    }

    private static void AssertHeader(DataGrid grid, DataGridColumn column, string where)
    {
        var header = Descendants(grid).OfType<DataGridColumnHeader>().Single(h => ReferenceEquals(h.Column, column));
        var text = Descendants(header).OfType<TextBlock>().First(t => t.Text == (string)column.Header);
        AssertWholeText(grid, text, header, where);
    }

    private static void AssertCells(DataGrid grid, string headerText, string value, string where)
    {
        var column = grid.Columns.Single(c => c.Header as string == headerText);
        grid.ScrollIntoView(grid.Items[0], column);
        grid.UpdateLayout();
        var cells = Descendants(grid).OfType<DataGridCell>().Where(c => ReferenceEquals(c.Column, column)).ToList();
        Assert.NotEmpty(cells);
        foreach (var cell in cells)
        {
            var text = Descendants(cell).OfType<TextBlock>().Single();
            Assert.Equal(value, text.Text);
            AssertWholeText(grid, text, cell, where);
        }
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheOneClickPlanShowsTheWholeHeadersAndBoxes(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var viewModel = new OneClickPlanViewModel(new OneClickPlanContext
            {
                Apps = [(new OneClickApp("a", "Audacity", "Contoso", "3.7"), (PackagerInfo?)new PackagerInfo { Uid = "a", Application = "Audacity" })],
                Settings = () => new OneClickPlanSettings
                {
                    Tracked = ["a"],
                    DefaultDestinations = new DestinationSet(true, true, true),
                    Readiness = OneClickPlanner.Readiness(new PublishReadiness(new SystemsInUse { ConfigMgr = true }, true, "MCM", @"\\cm01\share", "", "", false, "")),
                    Scope = new OneClickScopeSettings()
                },
                Action = OneClickActionNames.StageAndPackage,
                ReportFolder = Path.Combine(_root, "reports")
            }, new CountingRunner());
            var window = new OneClickPlanWindow(viewModel, Path.Combine(_root, "reports"));
            Show(window);
            try
            {
                Settle(window);
                Settle(window);
                var grid = (DataGrid)window.FindName("gridPlan");
                foreach (var header in new[] { "Include", "ConfigMgr", "WSUS", "Intune" })
                {
                    var column = grid.Columns.Single(c => c.Header as string == header);
                    AssertHeader(grid, column, $"{header} header, {theme}");
                    var cell = Descendants(grid).OfType<DataGridCell>().Single(c => ReferenceEquals(c.Column, column));
                    var box = Descendants(cell).OfType<CheckBox>().Single();
                    var left = box.TranslatePoint(new System.Windows.Point(0, 0), cell).X;
                    Assert.True(left >= -0.5 && left + box.ActualWidth <= cell.ActualWidth + 0.5,
                        $"{header} box, {theme}: {box.ActualWidth:0} px at {left:0} in a cell of {cell.ActualWidth:0} px.");
                }
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheCveAndWsusWindowsShowTheWholeSeverityAndCreationTime(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            const string Severity = "10.0 CRITICAL", Created = "2026-11-27 12:58:48";
            var cves = new CveWindowViewModel(new PackagerInfo { Uid = "a", Application = "Contoso", Vendor = "Contoso" }, null);
            cves.Rows.Add(new CveWindowRow("CVE-2026-1000", Severity, "50.0%", "Yes", "1.2", "2026-09-01", "2026-09-02", "A flaw.", []));
            var packages = (PackagesViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
            var cveWindow = new CveWindow(cves, packages);
            Show(cveWindow);
            try
            {
                Settle(cveWindow);
                AssertCells((DataGrid)cveWindow.FindName("CveGrid"), "CVSS", Severity, $"CVE window, {theme}");
            }
            finally { cveWindow.Close(); }

            var client = new FakeWsusAdminClient
            {
                Updates = [new WsusPublishedUpdate(Guid.NewGuid().ToString(), "Contoso 1", "Update", "AppPackager:catalog:x/default", "1.0", DateTimeOffset.Now,
                    Created, "", false, false, false)]
            };
            var viewModel = new WsusUpdatesViewModel(client, new WsusSettings { ServerName = "wsus01" }, new FakeWsusPrompts());
            var window = new WsusUpdatesWindow(viewModel, _ => { });
            Show(window);
            try
            {
                StaTestHelper.PumpUntil(() => viewModel.Updates.Count > 0);
                Settle(window);
                Settle(window);
                var grid = (DataGrid)window.FindName("gridUpdates");
                AssertCells(grid, "Created", viewModel.Updates[0].CreatedText, $"WSUS updates, {theme}");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheDetailPaneLinkAndTheConfigMgrMarkFollowTheirSettings(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var page = AppPackagerFitTests.Create(4, out var viewModel);
            viewModel.IsDetailPaneOpen = true;
            var window = new Window { Width = 1920 - 230, Height = 1032 - 48, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Settle(window);
                var link = ById<ButtonBase>(page, "btnDetailSystems");
                var text = Descendants(link).OfType<TextBlock>().First(t => t.Text.Length > 0);
                var left = text.TranslatePoint(new System.Windows.Point(0, 0), link).X;
                Assert.True(left >= 4 && link.ActualWidth - left - text.ActualWidth >= 4,
                    $"The text of the link is {left:0} px and {link.ActualWidth - left - text.ActualWidth:0} px from its border.");

                var mark = (TextBlock)page.FindName("txtConfigMgrMark");
                viewModel.ShowConfigMgrIndicator = true;
                Settle(window);
                Assert.True(mark.IsVisible);
                viewModel.ShowConfigMgrIndicator = false;
                Settle(window);
                Assert.False(mark.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 1690.0)]
    [InlineData(ApplicationTheme.Dark, 1200.0)]
    public void TheScrollBarOfTheDeployTabDoesNotLieOverACard(ApplicationTheme theme, double width)
    {
        WithApplication(theme, () =>
        {
            var viewModel = new DeploymentPageHarness().Create();
            var page = new DeploymentHelperPage(viewModel);
            var window = new Window { Width = width, Height = 600, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Run(page.OnNavigatedToAsync());
                Settle(window);
                Settle(window);
                var scroll = (ScrollViewer)page.FindName("scrollDeployTab");
                var bar = Descendants(scroll).OfType<ScrollBar>().Single(b => b.Orientation == System.Windows.Controls.Orientation.Vertical && ReferenceEquals(b.TemplatedParent, scroll));
                Assert.True(bar.IsVisible, "The page does not scroll, so the test shows nothing.");
                var barLeft = bar.TranslatePoint(new System.Windows.Point(0, 0), scroll).X;
                foreach (var name in new[] { "pnlCollections", "pnlSettings" })
                {
                    var card = (FrameworkElement)page.FindName(name);
                    var right = card.TranslatePoint(new System.Windows.Point(card.ActualWidth, 0), scroll).X;
                    Assert.True(right <= barLeft + 0.5, $"{name} ends at {right:0} and the scroll bar starts at {barLeft:0}.");
                }
            }
            finally { window.Close(); }
        });
    }
}
