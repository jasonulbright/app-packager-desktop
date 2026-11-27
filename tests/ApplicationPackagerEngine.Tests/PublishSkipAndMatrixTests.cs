using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The Skip cells of the publish plan, and the review and results windows around their matrices.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PublishSkipAndMatrixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apd-report-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestCleanup.DeleteDirectory(_root);

    private static PublishReadiness Ready(bool configMgr, bool intune, bool wsus) =>
        new(new SystemsInUse { ConfigMgr = configMgr, Intune = intune, Wsus = wsus }, true, "MCM", @"\\cm01\share", "t", "c", true, "wsus01");

    private static OneClickHistoryEntry History(string destination, string published = "", string refused = "", string build = "") => new()
    {
        Published = published.Length == 0 ? new Dictionary<string, OneClickPublishRecord>()
            : new Dictionary<string, OneClickPublishRecord> { [destination] = new() { Version = published } },
        NotSupported = refused.Length == 0 ? new Dictionary<string, OneClickRefusalRecord>()
            : new Dictionary<string, OneClickRefusalRecord> { [destination] = new() { Version = refused } },
        PublishedBuild = build.Length == 0 ? new Dictionary<string, string>() : new Dictionary<string, string> { [destination] = build }
    };

    private static PublishPlanApp App(ConfigMgrFact? site = null, OneClickHistoryEntry? history = null, bool stale = false, string buildKey = "") =>
        new("a", "Alpha", "2.0", false) { Site = site ?? ConfigMgrFact.None, History = history, BuildStale = stale, BuildKey = buildKey };

    [Theory]
    [InlineData("WSUS", "2.0", "", "Skip: version already published")]
    [InlineData("Intune", "2.0", "", "Skip: version already published")]
    [InlineData("WSUS", "3.0", "", "Skip: published 3.0 is newer than 2.0")]
    [InlineData("Intune", "1.0", "", null)]
    [InlineData("WSUS", "", "2.0", "Skip: this version was refused before")]
    [InlineData("WSUS", "", "1.0", null)]
    public void WsusAndIntuneSkipWhatTheirRecordsHold(string system, string published, string refused, string? skip)
    {
        var readiness = Ready(false, system == "Intune", system == "WSUS");
        var cell = PublishPlanner.Plan([App(history: History(system, published, refused))], readiness.Systems, readiness).Rows[0].Cells[0];
        Assert.Equal(skip is null ? PublishCellKind.Create : PublishCellKind.Skip, cell.Kind);
        if (skip is not null) Assert.Equal(skip, cell.Text);
    }

    [Fact]
    public void ConfigMgrSkipsByWhatTheSiteHasAndNotByARecordOfAnApplicationThatIsGone()
    {
        var readiness = Ready(true, false, false);
        PublishPlanCell Cell(PublishPlanApp app) => PublishPlanner.Plan([app], readiness.Systems, readiness).Rows[0].Cells[0];

        Assert.Equal("Skip: version already published", Cell(App(ConfigMgrFact.Found("2.0"))).Text);
        Assert.Equal("Skip: site version 3.0 is newer than 2.0", Cell(App(ConfigMgrFact.Found("3.0"))).Text);
        // The record says 2.0 was published, and the read of the site finds nothing: someone removed it.
        Assert.Equal(PublishCellKind.Create, Cell(App(ConfigMgrFact.None, History("ConfigMgr", "2.0"))).Kind);
        // Another build of the same version is offered; the run still asks before it replaces the application.
        Assert.Equal(PublishCellKind.Create, Cell(App(ConfigMgrFact.Found("2.0"), stale: true)).Kind);
        Assert.Equal(PublishCellKind.Create, Cell(App(ConfigMgrFact.Found("2.0"), History("ConfigMgr", "2.0", build: "old"), buildKey: "new")).Kind);
        Assert.Equal(PublishCellKind.Create, Cell(App(ConfigMgrFact.Unreadable("offline"), History("ConfigMgr", "2.0"))).Kind);
        // A retry has its own rules.
        Assert.Equal(PublishCellKind.Create, PublishPlanner.Plan([App(ConfigMgrFact.Found("3.0"))], readiness.Systems, readiness, skips: false).Rows[0].Cells[0].Kind);
    }

    [Fact]
    public void ASkipCountsInTheTextsAndInTheDigestAndAPlanOfSkipsCannotRun()
    {
        var readiness = Ready(false, false, true);
        var fresh = PublishPlanner.Plan([App()], readiness.Systems, readiness);
        var held = PublishPlanner.Plan([App(history: History("WSUS", "2.0"))], readiness.Systems, readiness);

        Assert.False(held.CanRun);
        Assert.Equal("1 application, 1 system: 0 publishes are planned. 1 skipped.", held.CountsText);
        Assert.NotEqual(fresh.Digest, held.Digest);
        Assert.Contains("the planned actions are now: WSUS, Skip: version already published", PublishPlanner.Changes(fresh, held)["a"]);
        Assert.StartsWith("Nothing to publish", new PublishReviewViewModel(held, "").NothingText);
    }

    [Fact]
    public void TheReviewNamesThePromptsThatCanStillOpen()
    {
        var readiness = Ready(true, true, false);
        var text = new PublishReviewViewModel(PublishPlanner.Plan([App()], readiness.Systems, readiness), "").MayAskText;
        Assert.StartsWith("The run may ask: Application Already Exists", text);
        Assert.Contains("supersedence", text);
        Assert.Contains("Intune review", text);

        var wsus = Ready(false, false, true);
        Assert.Equal("", new PublishReviewViewModel(PublishPlanner.Plan([App()], wsus.Systems, wsus), "").MayAskText);
    }

    private static List<(string, PublishRowResult)> Results(PublishDestinationKind second) =>
    [
        ("Notepad++", new PublishRowResult("n", "8.0",
        [
            new PublishDestinationResult("ConfigMgr", PublishDestinationKind.Published, "", "") { Created = true },
            new PublishDestinationResult("Intune", second, second == PublishDestinationKind.Failed ? "content upload" : "", "message")
        ]))
    ];

    [Fact]
    public void TheNoDeploymentNoticeComesFromTheFactsOfTheRun()
    {
        var rows = Results(PublishDestinationKind.Published);
        Assert.Equal("", new PublishResultsViewModel(rows, false).DeploymentNotice);
        Assert.Equal("Publishing created a ConfigMgr application. No deployment was created.",
            new PublishResultsViewModel(rows, false) { WithoutDeployment = new HashSet<string> { "n" } }.DeploymentNotice);
        var kept = new List<(string, PublishRowResult)>
        {
            ("Notepad++", new PublishRowResult("n", "8.0", [new PublishDestinationResult("ConfigMgr", PublishDestinationKind.Published, "", "")]))
        };
        Assert.Equal("", new PublishResultsViewModel(kept, false) { WithoutDeployment = new HashSet<string> { "n" } }.DeploymentNotice);
    }

    [Fact]
    public void AGridRunWritesAMarkdownAndAJsonReport()
    {
        var path = PublishReportWriter.Write(_root, new DateTime(2026, 11, 26, 14, 0, 0), "Site MCM", "Publish completed with issues", false,
            Results(PublishDestinationKind.Failed));

        var text = File.ReadAllText(path);
        Assert.Contains("# Publish All report", text);
        Assert.Contains("| Notepad++ | 8.0 | Intune | Failed at content upload | message |", text);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".json")));
        Assert.Equal("Publish completed with issues", json.RootElement.GetProperty("Result").GetString());
    }

    private static void AssertFits(Window window, string where)
    {
        RenderKit.Settle(window);
        var content = (FrameworkElement)window.Content;
        foreach (var button in RenderKit.Descendants(window).OfType<System.Windows.Controls.Button>().Where(b => b.IsVisible && AutomationProperties.GetAutomationId(b).StartsWith("btn", StringComparison.Ordinal)))
        {
            var bounds = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
            Assert.True(bounds.Bottom <= content.ActualHeight + 0.5 && bounds.Right <= content.ActualWidth + 0.5,
                $"{where}: {AutomationProperties.GetAutomationId(button)} is at {bounds} in {content.ActualWidth:0}x{content.ActualHeight:0}.");
        }
        PageChromeKit.AssertNothingClipped(content, where);
    }

    private static string CellName(DataGrid grid, int row, int column)
    {
        grid.UpdateLayout();
        var container = (System.Windows.Controls.DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(row);
        var cell = RenderKit.Descendants(container).OfType<DataGridCell>().Single(c => c.Column.DisplayIndex == column);
        return UIElementAutomationPeer.CreatePeerForElement(cell).GetName();
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 1190, 540)]
    [InlineData(ApplicationTheme.Dark, 1190, 540)]
    [InlineData(ApplicationTheme.Light, 900, 480)]
    [InlineData(ApplicationTheme.Dark, 900, 480)]
    public void TheReviewFitsWithItsCardsAndEachMatrixCellHasAName(ApplicationTheme theme, int width, int height)
    {
        RenderKit.WithApplication(theme, () =>
        {
            var readiness = Ready(true, true, true);
            var plan = PublishPlanner.Plan([App()], readiness.Systems, readiness) with
            {
                Site = "MCM|cm01.contoso.com",
                Cards = [new PublishCard("ConfigMgr", [new("Scope", "application only, no distribution, no deployment")]), new PublishCard("WSUS", [new("Scope", "published, not approved")])]
            };
            var window = new PublishReviewWindow(new PublishReviewViewModel(plan, "")) { Width = width, Height = height };
            RenderKit.Show(window);
            try
            {
                AssertFits(window, $"review {theme} {width}x{height}");
                Assert.Equal("Review 1 application", RenderKit.ById<TextBlock>(window, "txtReviewHeading").Text);
                Assert.Contains("Site MCM", RenderKit.ById<TextBlock>(window, "txtReviewSite").Text);
                Assert.True(RenderKit.ById<ScrollViewer>(window, "pnlReviewCards").IsVisible);
                var grid = RenderKit.ById<DataGrid>(window, "gridReviewMatrix");
                Assert.Equal("Alpha, Intune, Create Win32 app", CellName(grid, 0, 4));
                var confirm = RenderKit.ById<Wpf.Ui.Controls.Button>(window, "btnReviewConfirm");
                // The write creates objects, so the button is Primary and not Danger.
                Assert.Equal(Wpf.Ui.Controls.ControlAppearance.Primary, confirm.Appearance);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheResultsOfARunInWhichEverySystemFailedFitAndNameEachCell(ApplicationTheme theme)
    {
        RenderKit.WithApplication(theme, () =>
        {
            var rows = new List<(string, PublishRowResult)>
            {
                ("Notepad++", new PublishRowResult("n", "8.0",
                [
                    new PublishDestinationResult("ConfigMgr", PublishDestinationKind.Failed, "", "share refused"),
                    new PublishDestinationResult("WSUS", PublishDestinationKind.Failed, "", "server unreachable")
                ]))
            };
            var window = new PublishResultsWindow(new PublishResultsViewModel(rows, false, offerRetry: true)) { Width = 900, Height = 480 };
            RenderKit.Show(window);
            try
            {
                AssertFits(window, $"results, all failed, {theme}");
                Assert.Equal("Publish completed with issues", RenderKit.ById<TextBlock>(window, "txtResultsHeadline").Text);
                Assert.Equal("Published 0", RenderKit.ById<TextBlock>(window, "txtResultsPublished").Text);
                Assert.Equal("Failed 2", RenderKit.ById<TextBlock>(window, "txtResultsFailed").Text);
                Assert.Equal("Notepad++, WSUS, Failed", CellName(RenderKit.ById<DataGrid>(window, "gridResultsMatrix"), 0, 2));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, PublishDestinationKind.Published, "2 publishes completed")]
    [InlineData(ApplicationTheme.Dark, PublishDestinationKind.Failed, "Publish completed with issues")]
    public void TheResultsFitWithTheirFooterAndEachCellHasAName(ApplicationTheme theme, PublishDestinationKind second, string headline)
    {
        RenderKit.WithApplication(theme, () =>
        {
            var viewModel = new PublishResultsViewModel(Results(second), false, offerRetry: true)
            {
                SiteLine = "Site MCM", ReportPath = "report.md", ShowLogs = _ => { }, ShowHistory = (_, _) => { },
                WithoutDeployment = new HashSet<string> { "n" }
            };
            var window = new PublishResultsWindow(viewModel) { Width = 900, Height = 480 };
            RenderKit.Show(window);
            try
            {
                AssertFits(window, $"results {theme} {second}");
                Assert.Equal(headline, RenderKit.ById<TextBlock>(window, "txtResultsHeadline").Text);
                foreach (var id in new[] { "btnResultsHistory", "btnResultsReport", "btnResultsLogs", "btnResultsDone" })
                    Assert.True(RenderKit.ById<ButtonBase>(window, id).IsVisible, id);
                var failed = second == PublishDestinationKind.Failed;
                Assert.Equal(failed, RenderKit.ById<ButtonBase>(window, "btnResultsReview").IsVisible);
                Assert.True(RenderKit.ById<TextBlock>(window, "txtResultsDeployment").IsVisible);
                var grid = RenderKit.ById<DataGrid>(window, "gridResultsMatrix");
                Assert.Equal(failed ? "Notepad++, Intune, Failed at content upload" : "Notepad++, Intune, Published", CellName(grid, 0, 2));
                if (!failed) return;
                var closed = false;
                window.Closed += (_, _) => closed = true;
                RenderKit.ById<ButtonBase>(window, "btnResultsReview").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.True(closed);
                Assert.Equal("n", viewModel.ReviewUid);
            }
            finally { window.Close(); }
        });
    }
}
