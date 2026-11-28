using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using Point = System.Windows.Point;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Settings is a tree of Title Case pages with one compact page per group (Q-24). Every setting that the old single page had stays on a page,
/// and no page is longer than one screen at the 1920x1032 work area or clips sideways at 1024x720.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SettingsTreeTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Mock = "clr-namespace:ApplicationPackagerUI.Controls.Mock";

    /// <summary>The 1920x1032 work area of a 1080p display (D-20 of the suite) and the clipping check size.</summary>
    private static readonly (int Width, int Height)[] Sizes = [(1920, 1032), (1024, 720)];

    private const double NavigationPaneWidth = 230;
    private const double FrameHeight = 48;

    /// <summary>The binding paths of the single-page Settings before the rebuild (release 2026.1116.46). Each stays reachable on a page.</summary>
    private const string BindingsBeforeTheRebuild = "AppVersion Application AutoDistribute BrowseCommand BrowseDownloadRootCommand BrowsePackagersRootCommand CadenceDisplay CanHide CanMoveDown CanMoveUp ChangeThemeCommand CheckForUpdatesAtStartup CompanyName ConfigMgr ConfigMgrVersionRule ConsoleStatus ContentFallback ContentLayout ContentPrepIdle ContentPrepStatus CreateTestCollectionIfMissing CurrentTheme CwaPreview CwaSwitches.Components.AM CwaSwitches.Components.BCR_Client CwaSwitches.Components.CitrixEnterpriseBrowser CwaSwitches.Components.Customize CwaSwitches.Components.DesktopViewer CwaSwitches.Components.ICA_Client CwaSwitches.Components.ReceiverInside CwaSwitches.Components.SSON CwaSwitches.Components.SelfService CwaSwitches.Components.USB CwaSwitches.Components.WebHelper CwaSwitches.Hdx.BidirectionalContentRedirection CwaSwitches.Hdx.ClientHostedAppsUrl CwaSwitches.Hdx.DisableSetting CwaSwitches.Hdx.LegacyFileTypeIcons CwaSwitches.Hdx.LocalAppAccess CwaSwitches.Installation.AppProtection CwaSwitches.Installation.CleanInstall CwaSwitches.Installation.ClientName CwaSwitches.Installation.EnableSSON CwaSwitches.Installation.ForceInstall CwaSwitches.Installation.IncludeSSON CwaSwitches.Installation.SelfServiceMode CwaSwitches.Installation.SessionPreLaunch CwaSwitches.Plugins.EPAClient CwaSwitches.Plugins.MSTeamsPlugin CwaSwitches.Plugins.SessionRecording CwaSwitches.Plugins.SessionRecordingSkipUpgrade CwaSwitches.Plugins.UberAgent CwaSwitches.Plugins.UberAgentSkipUpgrade CwaSwitches.Plugins.WebExPlugin CwaSwitches.Plugins.ZoomPlugin CwaSwitches.Store.Name CwaSwitches.Store.Url CwaSwitches.StorePolicy.AllowAddStore CwaSwitches.StorePolicy.AllowSavePwd CwaSwitches.UpdateAndTelemetry.AURolloutPriority CwaSwitches.UpdateAndTelemetry.AutoUpdateCheck CwaSwitches.UpdateAndTelemetry.AutoUpdateStream CwaSwitches.UpdateAndTelemetry.EnableCEIP CwaSwitches.UpdateAndTelemetry.EnableTracing DeployToTestCollection DiscardChangesCommand Discovery.ClearDismissedCommand Discovery.DismissedSummary Discovery.SqlDatabase Discovery.SqlServer DownloadContentPrepCommand DownloadIconPackCommand DownloadRoot DpGroupName EstimatedRuntimeMins FileShareRoot GitHubApiIdle GitHubApiStatus GitHubToken GitHubTokenHint GroupAutomationName GroupCaption HasStoredGitHubToken HasStoredNvdApiKey Header Hint IconPackIdle IconPackStatus InstallIconPackFromFileCommand InstallParameterGroups InstallParameterSummary Intune IntuneClientId IntuneClientSecret IntuneMinimumWindowsRelease IntuneSecretHint IntuneTenantId IntuneVersionRule IntuneWindowsReleaseChoices IsGroupStart Label LocalSourceRows LookupCvesAfterCheckMecm M365Channel MaximumRuntimeMins MoveDownCommand MoveUpCommand NvdApiKey NvdApiKeyHint OneClickAction OneClickCadenceDays OneClickDefaultConfigMgr OneClickDefaultIntune OneClickDefaultWsus OneClickForce OneClickOnExisting OneClickRows OneClickTrackedCount OpenReleasesCommand PackagersRoot Parameters Path PreviewCwaCommand ProviderMachine RefreshGitHubApiStatusCommand RefreshOneClickRowsCommand RemoveGitHubToken RemoveNvdApiKey SaveSettingsCommand SevenZipStatus Shown SideFileRows Sidebar.ResetCommand Sidebar.Rows Signing.CertificateDetail Signing.Certificates Signing.RefreshCertificatesCommand Signing.RequireDeployment Signing.RequireDetection Signing.RequireRequirements Signing.SelectedCertificate Signing.SignDeployment Signing.SignDetection Signing.SignRequirements Signing.StoreLocation Signing.StoreLocationOptions Signing.TestConfigurationCommand Signing.TestIdle Signing.TestResult Signing.TimestampRequired Signing.TimestampServer SiteCode SkipConfigReview SlowNetworkDeploymentMode SubHeader SupersedenceMode SupersedenceModeChoices TestCollectionIsUser TestCollectionName TestDeploymentAvailable Title TitleOptions.AppendArchitectureToTitle TitleOptions.IncludeVersionInTitle TitleOptions.RemoveVersionFromTitle TitleOptions.RemoveVersionFromTitleAvailable TrackAllOneClickCommand TrackNoneOneClickCommand Tracked UseConfigMgr UseIntune UseWsus Values Vendor VersionRuleChoices Wsus Wsus.ApprovalGroup Wsus.Busy Wsus.CancelCommand Wsus.CertificateText Wsus.Classification Wsus.ClassificationOptions Wsus.CreateSelfSignedCommand Wsus.DeclineSuperseded Wsus.ExportCertificateCommand Wsus.Groups Wsus.ImportPfxCommand Wsus.LoadGroupsCommand Wsus.Port Wsus.ServerName Wsus.StatusText Wsus.TestConnectionCommand Wsus.UseSsl";

    private static string PagePath() => Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Views", "Pages", "SettingsPage.xaml");

    private static XDocument Page() => XDocument.Load(PagePath(), LoadOptions.SetLineInfo);

    private static List<XElement> Leaves() => Page().Descendants(Presentation + "TreeViewItem").Where(i => i.Attribute("Tag") is not null).ToList();

    public static TheoryData<string, ApplicationTheme, int, int> Cases()
    {
        var data = new TheoryData<string, ApplicationTheme, int, int>();
        foreach (var leaf in Leaves())
            foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
                foreach (var (width, height) in Sizes)
                    data.Add((string)leaf.Attribute("Tag")!, theme, width, height);
        return data;
    }

    [Fact]
    public void EveryLeafOfTheTreeHasOnePageAndEveryPageHasALeaf()
    {
        var tags = Leaves().Select(l => (string)l.Attribute("Tag")!).ToList();
        var pages = Page().Descendants().Select(e => (string?)e.Attribute(Xaml + "Name")).Where(n => n?.StartsWith("page_") == true).Select(n => n!["page_".Length..]).ToList();

        Assert.Equal(tags.Count, tags.Distinct().Count());
        Assert.Equal(tags.OrderBy(x => x), pages.OrderBy(x => x));
        Assert.True(tags.Count >= 20);
    }

    [Fact]
    public void EveryNameOfTheTreeIsInTitleCase()
    {
        var problems = Page().Descendants(Presentation + "TreeViewItem").Select(i => (string)i.Attribute("Header")!)
            // The approved mock writes this name in sentence case.
            .Where(h => h != "Systems in use")
            .Select(h => (Heading: h, Problem: SettingsHeadingsAndGitHubTokenTests.TitleCaseProblem(h))).Where(x => x.Problem is not null)
            .Select(x => $"'{x.Heading}': {x.Problem}").ToList();

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public void EveryPageStartsWithItsNameInTheTree()
    {
        var wrong = new List<string>();
        var page = Page();
        foreach (var leaf in Leaves())
        {
            var id = (string)leaf.Attribute("Tag")!;
            var root = page.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "page_" + id);
            var first = root.Descendants(Mock + "TrackedText").First(t => ((string?)t.Attribute("Style"))?.Contains("SectionHeader") == true);
            // The two pages of the approved mock carry the headings of the mock; the list keeps its own names.
            var expected = id switch { "configmgr" => "ConfigMgr connection", "supersedence" => "Application settings", _ => (string)leaf.Attribute("Header")! };
            if ((string?)first.Attribute("Text") != expected)
                wrong.Add($"{id}: '{(string?)first.Attribute("Text")}' vs '{(string)leaf.Attribute("Header")!}'");
        }

        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    [Fact]
    public void EverySettingOfTheOldPageIsStillBound()
    {
        var now = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(PagePath()), @"\{Binding ([A-Za-z0-9_.]+)")
            .Select(m => m.Groups[1].Value).ToHashSet();

        var missing = BindingsBeforeTheRebuild.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(b => !now.Contains(b)).ToList();

        Assert.True(missing.Count == 0, "These settings lost their place: " + string.Join(", ", missing));
    }

    [Fact]
    public void NoElementOfTheTreeSetsAFontSizeAboveTheHeaderSize()
    {
        var big = Page().Descendants().Where(e => double.TryParse((string?)e.Attribute("FontSize"), out var size) && size > 16).Select(e => e.Name.LocalName).ToList();

        Assert.Empty(big);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void APageFitsTheWorkAreaAndNeverClipsSideways(string id, ApplicationTheme theme, int width, int height)
    {
        PageHeaderRenderTests.With("Settings", theme, () =>
        {
            using var host = new PageHeaderRenderTests();
            var (page, _) = host.Create("Settings");
            var settings = (SettingsPage)page;
            settings.SelectPage(id);
            var window = new Window { Width = width - NavigationPaneWidth, Height = height - FrameHeight, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Settle(window);
                var where = $"{id} {theme} {width}x{height}";
                var element = (FrameworkElement)settings.FindName("page_" + id);
                Assert.True(element.IsVisible, where + ": the page is not shown");
                if (element is ScrollViewer scroll)
                {
                    Assert.True(scroll.ScrollableWidth < 0.5, $"{where}: the page is {scroll.ExtentWidth:0} px wide in a {scroll.ViewportWidth:0} px area");
                    if (height >= 1000)
                        Assert.True(scroll.ScrollableHeight < 0.5, $"{where}: the page is {scroll.ExtentHeight:0} px tall in a {scroll.ViewportHeight:0} px area");
                }

                var outside = Descendants(element).OfType<FrameworkElement>()
                    .Where(e => e.IsVisible && e.ActualWidth > 0 && !InsideAGrid(e) && e.TranslatePoint(new Point(e.ActualWidth, 0), window).X > window.ActualWidth + 0.5)
                    .Select(e => e is TextBlock tb ? "TextBlock \"" + tb.Text + "\"" : e.GetType().Name).Distinct().ToList();
                Assert.True(outside.Count == 0, where + ": these elements leave the window on the right: " + string.Join(", ", outside));

                Save(window, id, theme, width, height);
            }
            finally { window.Close(); }
        });
    }

    /// <summary>A data grid scrolls its own columns and a text box clips its own text and placeholder, so neither adds to the page width.</summary>
    private static bool InsideAGrid(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is DataGrid or System.Windows.Controls.TextBox) return true;
        return element is DataGrid;
    }

    /// <summary>A capture run: with APD_SETTINGS_SHOTS set to a folder, each page is saved as a PNG per theme and size (D-21 of the suite).</summary>
    private static void Save(Window window, string id, ApplicationTheme theme, int width, int height)
    {
        var folder = Environment.GetEnvironmentVariable("APD_SETTINGS_SHOTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, $"settings-{id}-{theme.ToString().ToLowerInvariant()}-{width}x{height}.png"));
        encoder.Save(stream);
    }
}
