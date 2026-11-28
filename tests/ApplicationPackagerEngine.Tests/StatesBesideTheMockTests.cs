using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerEngine.Services.Supersedence;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridRow = System.Windows.Controls.DataGridRow;
using MenuItem = System.Windows.Controls.MenuItem;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The states that no mock draws of the One Click plan, the results and the review: every extra line of the plan at once, long messages in a
/// results table, what the row menu has, and the Intune review.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class StatesBesideTheMockTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("apd-states-").FullName;

    public void Dispose() => TestCleanup.DeleteDirectory(_folder);

    private sealed class NoRunner : IOneClickRunner
    {
        public Task<bool> RunAsync(IReadOnlyList<OneClickPlanItem> rows, OneClickRunOptions options, CancellationToken ct) => Task.FromResult(false);
    }

    private static List<PackagerInfo> Rows(int count) =>
        Enumerable.Range(0, count).Select(i => new PackagerInfo { Uid = "a" + i, Application = $"App {i:00}", IsJsonDefined = true }).ToList();

    private OneClickPlanViewModel Plan(List<PackagerInfo> rows, Func<OneClickPlanSettings>? settings = null, Func<PackagerInfo, bool>? supersedes = null) =>
        new(new OneClickPlanContext
        {
            Apps = rows.Select(p => (new OneClickApp(p.Uid, p.Application, "Contoso", "2.0", "1.0"), (PackagerInfo?)p)).ToList(),
            Settings = settings ?? (() => new OneClickPlanSettings
            {
                Tracked = rows.Select(p => p.Uid).ToList(),
                // WSUS is approved for a group, and Intune is not ready: the plan has a scope line and a blocking line.
                DefaultDestinations = new DestinationSet(true, true, true),
                Readiness = OneClickPlanner.Readiness(new PublishReadiness(new SystemsInUse { ConfigMgr = true, Wsus = true, Intune = true }, true, "MCM",
                    @"\\cm01\share", "", "", false, "wsus01")),
                Scope = new OneClickScopeSettings(WsusApprovalGroup: "Pilot")
            }),
            Action = OneClickActionNames.StageAndPackage,
            OnExisting = OneClickOnExisting.Overwrite,
            Comment = "WO-9",
            Supersedes = supersedes ?? (_ => true),
            ReportFolder = _folder
        }, new NoRunner());

    private static int WholeRows(DataGrid grid)
    {
        var view = Descendants(grid).OfType<ScrollContentPresenter>().First();
        return Descendants(grid).OfType<DataGridRow>().Where(r => r.IsVisible).Count(r =>
        {
            var at = r.TransformToAncestor(view).TransformBounds(new Rect(r.RenderSize));
            return at.Top >= -0.5 && at.Bottom <= view.ActualHeight + 0.5;
        });
    }

    // A width of zero keeps the size that the window opens with.
    [Theory]
    [InlineData(ApplicationTheme.Light, 0, 0)]
    [InlineData(ApplicationTheme.Dark, 0, 0)]
    [InlineData(ApplicationTheme.Light, 1100, 480)]
    [InlineData(ApplicationTheme.Dark, 1100, 480)]
    public void ThePlanWithEveryExtraLineAtOnceKeepsRowsInView(ApplicationTheme theme, int width, int height)
    {
        WithApplication(theme, () =>
        {
            var model = Plan(Rows(60));
            var window = new OneClickPlanWindow(model, _folder);
            if (width > 0)
            {
                window.Width = width;
                window.Height = height;
            }
            Show(window);
            try
            {
                Settle(window);
                model.PlanOnlyCommand.Execute(null);
                Settle(window);
                Settle(window);
                foreach (var id in new[] { "txtPlanRisk", "txtPlanBlocking", "txtPlanSummary" }) Assert.True(ById<TextBlock>(window, id).IsVisible, id);
                Assert.True(((FrameworkElement)window.FindName("txtPlanComment")).IsVisible, "The comment is not visible.");

                var grid = (DataGrid)window.FindName("gridPlan");
                var host = (Border)window.FindName("BodyHost");
                var scrolls = host.Child == window.BodyScroll;
                if (scrolls) window.BodyScroll.ScrollToEnd();
                Settle(window);
                var rows = WholeRows(grid);
                Assert.True(rows >= 3, $"{rows} whole rows show in a grid {grid.ActualHeight:0} px high.");
                var inBody = grid.TransformToAncestor(host).TransformBounds(new Rect(grid.RenderSize));
                Assert.True(inBody.Top >= -0.5 && inBody.Bottom <= host.ActualHeight + 0.5, $"The grid is at {inBody} in a body {host.ActualHeight:0} px high.");
                var foot = ById<ButtonBase>(window, "btnRun");
                AssertInside(window, foot, window.ActualHeight, "btnRun");
                Assert.True(window.ActualHeight <= Math.Max(SystemParameters.WorkArea.Height, window.MinHeight) + 0.5, $"The window is {window.ActualHeight:0} px high.");
                // The window took the room of the work area before the body scrolls.
                if (scrolls) Assert.True(window.ActualHeight >= SystemParameters.WorkArea.Height - 1, $"The body scrolls in a window {window.ActualHeight:0} px high.");
                // The whole risk text is in the layout: no scroll area of its own cuts it.
                var risk = ById<TextBlock>(window, "txtPlanRisk");
                Assert.Null(System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(risk));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TheRiskTextStartsWithTheScopeAndCountsALongListOfApplications()
    {
        var many = Plan(Rows(8));
        Assert.StartsWith("Scope. ", many.RiskText);
        Assert.Contains("Supersedence is on for 8 applications.", many.RiskText);
        Assert.DoesNotContain("App 07", many.RiskText);
        Assert.Contains("App 07", many.RiskDetail);

        var few = Plan(Rows(3));
        Assert.Contains("Supersedence is on for App 00, App 01, App 02.", few.RiskText);
        Assert.Null(few.RiskDetail);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ThePlanOpensNoLargerThanTheWorkArea(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var window = new OneClickPlanWindow(Plan(Rows(3)), _folder)
            {
                Width = SystemParameters.WorkArea.Width + 600,
                Height = SystemParameters.WorkArea.Height + 300
            };
            Show(window);
            try
            {
                Settle(window);
                Assert.True(window.ActualWidth <= SystemParameters.WorkArea.Width + 0.5, $"The window is {window.ActualWidth:0} px wide.");
                Assert.True(window.ActualHeight <= SystemParameters.WorkArea.Height + 0.5, $"The window is {window.ActualHeight:0} px high.");
                AssertInside(window, ById<ButtonBase>(window, "btnRun"), window.ActualHeight, "btnRun");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void AReplanReadsTheSettingsOnceAndEachDefinitionIsReadWhenThePlanIsBuilt()
    {
        var rows = Rows(6);
        int loads = 0, definitions = 0;
        var settings = new AppSettings { OneClick = new() { TrackedApplications = rows.Select(r => r.Uid).ToArray() } };
        var sources = new OneClickPlanSources(() => { loads++; return settings; }, true, rows, p => { definitions++; return p.Uid; });
        Assert.Equal((0, 6), (loads, definitions));

        var model = Plan(rows, sources.PlanSettings, sources.Supersedes);
        Assert.Equal((1, 6), (loads, definitions));
        Assert.Equal(6, model.Rows.Count);
        var replans = model.RefreshCount;
        foreach (var row in model.Rows.Take(3)) row.Include = !row.Include;
        model.Rows[3].ConfigMgr = !model.Rows[3].ConfigMgr;

        Assert.Equal(replans + 4, model.RefreshCount);
        Assert.Equal((5, 6), (loads, definitions));
    }

    [Fact]
    public void TheSupersedenceOfARowFollowsTheLastReadOfTheSettings()
    {
        var row = Rows(1)[0];
        var settings = new AppSettings { Supersedence = new() };
        var sources = new OneClickPlanSources(() => settings, true, [row], _ => "id");
        sources.PlanSettings();

        Assert.Equal(SupersedenceSettingsEditor.EffectiveMode(settings.Supersedence, "id") != SupersedenceMode.Off, sources.Supersedes(row));
        // A PS1 packager decides by its own script, so the plan says nothing about it.
        Assert.False(sources.Supersedes(new PackagerInfo { Uid = "ps1", Application = "Script" }));
    }

    private static readonly string LongMessage = string.Join(" ", Enumerable.Repeat("The destination kept the older version of this application.", 8));

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ALongMessageOfAnyStateShowsWholeAlsoAfterASort(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            (string Name, PublishDestinationResult Cell)[] states =
            [
                ("Alpha", new("ConfigMgr", PublishDestinationKind.Published, "", "Warning. " + LongMessage) { Warning = true }),
                ("Bravo", new("ConfigMgr", PublishDestinationKind.Blocked, "", "Blocked. " + LongMessage)),
                ("Charlie", new("ConfigMgr", PublishDestinationKind.NotSupported, "", "Not supported. " + LongMessage)),
                ("Delta", new("ConfigMgr", PublishDestinationKind.Skipped, "", "Skipped. " + LongMessage)),
                ("Echo", new("ConfigMgr", PublishDestinationKind.Published, "", "") { Created = true }),
            ];
            var rows = states.Select(s => (s.Name, new PublishRowResult(s.Name, "1.0", [s.Cell]))).ToList();
            var model = new PublishResultsViewModel(rows, false, ["ConfigMgr"]);
            var window = new PublishResultsWindow(model);
            Show(window);
            try
            {
                Settle(window);
                var grid = ById<DataGrid>(window, "gridResultsMatrix");
                void AssertWhole(string when)
                {
                    for (var i = 0; i < grid.Items.Count; i++)
                    {
                        var row = Row(grid, i);
                        Settle(window);
                        var line = (ResultLine)row.Item;
                        Assert.True(double.IsNaN(row.Height), $"{when}: the row of {line.Application} has a fixed height of {row.Height} px.");
                        var words = Descendants(row).OfType<TextBlock>().Single(t => t.Text == line.Result);
                        Assert.True(System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(words) is null && words.ActualHeight <= row.ActualHeight,
                            $"{when}: the message of {line.Application} is {words.ActualHeight:0} px high in a row of {row.ActualHeight:0} px.");
                        if (line.Application != "Echo") Assert.True(row.ActualHeight > 40, $"{when}: the row of {line.Application} is {row.ActualHeight:0} px high.");
                        if (i == 0) Assert.True(row.ActualHeight >= 36 - 0.5, $"{when}: the first row is {row.ActualHeight:0} px high.");
                    }
                }
                AssertWhole("as opened");
                grid.Items.SortDescriptions.Add(new SortDescription("Application", ListSortDirection.Descending));
                Settle(window);
                Assert.Equal("Echo", ((ResultLine)grid.Items[0]).Application);
                AssertWhole("sorted");
                grid.Items.SortDescriptions.Clear();
                Settle(window);
                AssertWhole("sorted back");
            }
            finally { window.Close(); }
        });
    }

    private static PublishResultsViewModel Results(PublishDestinationKind intune, bool offerRetry, bool several = true)
    {
        List<PublishDestinationResult> cells = [new("ConfigMgr", PublishDestinationKind.Published, "", "") { Created = true }];
        if (several) cells.Add(new("Intune", intune, "content upload", "The upload of the content failed."));
        else cells = [new("ConfigMgr", intune, "stage", "The stage failed.")];
        return new PublishResultsViewModel([("App1", new PublishRowResult("u1", "1.1", cells))], false, several ? ["ConfigMgr", "Intune"] : ["ConfigMgr"], offerRetry);
    }

    [Theory]
    // A failed destination that has no retry.
    [InlineData(PublishDestinationKind.Failed, true, true, "Review failed application and Show failure messages are")]
    // Nothing failed, and a destination is blocked.
    [InlineData(PublishDestinationKind.Blocked, true, true, "Show failure messages is")]
    // One system, with no retry.
    [InlineData(PublishDestinationKind.Failed, false, false, "Review failed application is")]
    public void TheResultsSayWhatTheRowMenuHas(PublishDestinationKind kind, bool offerRetry, bool several, string start)
    {
        var model = Results(kind, offerRetry, several);
        Assert.StartsWith(start + " in the menu of the result row.", model.RowMenuHelp);
        Assert.Equal(model.RowMenuHelp, model.RowMenuTip);
        WithApplication(ApplicationTheme.Light, () =>
        {
            var window = new PublishResultsWindow(model);
            Show(window);
            try
            {
                Settle(window);
                var help = ById<TextBlock>(window, "txtResultsRowMenu");
                Assert.True(help.IsVisible);
                Assert.Equal(model.RowMenuHelp, help.Text);
                var ids = window.BuildRowMenu(model.Lines[0]).Items.OfType<MenuItem>().Select(AutomationProperties.GetAutomationId).ToList();
                Assert.Equal(start.Contains("Review"), ids.Contains("miResultsReview"));
                Assert.Equal(start.Contains("Show failure"), ids.Contains("miResultsFailureMessages"));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TheResultsThatTheMockDrawsSayTheRowMenuInTheTooltipAndTheHelpTextOfTheTable()
    {
        var model = Results(PublishDestinationKind.Failed, offerRetry: false);
        Assert.Equal("", model.RowMenuHelp);
        Assert.StartsWith("Review failed application and Show failure messages are", model.RowMenuTip);
        var success = new PublishResultsViewModel([("App1", new PublishRowResult("u1", "1.1", [new("ConfigMgr", PublishDestinationKind.Published, "", "")]))], false);
        Assert.Equal("", success.RowMenuTip);
        WithApplication(ApplicationTheme.Light, () =>
        {
            var window = new PublishResultsWindow(model);
            Show(window);
            try
            {
                Settle(window);
                var grid = ById<DataGrid>(window, "gridResultsMatrix");
                Assert.Equal(model.RowMenuTip, grid.ToolTip);
                Assert.Equal(model.RowMenuTip, AutomationProperties.GetHelpText(grid));
                Assert.DoesNotContain(Descendants(window).OfType<TextBlock>(), t => t.IsVisible && t.Text == model.RowMenuTip);
            }
            finally { window.Close(); }
        });
    }

    private static PublishPlan ReviewPlan(bool configMgr, bool intune, bool wsus)
    {
        var readiness = new PublishReadiness(new SystemsInUse { ConfigMgr = configMgr, Intune = intune, Wsus = wsus }, true, "MCM", @"\\cm01\share", "t", "c",
            true, "wsus01");
        return PublishPlanner.Plan([new PublishPlanApp("u1", "App1 (x64)", "1.1", false) { Site = ConfigMgrFact.None }], readiness.Systems, readiness);
    }

    [Fact]
    public void TheReviewShowsTheIntuneReviewAsTextWhereTheRunCanAskIt()
    {
        Assert.Contains(PublishReviewViewModel.IntuneReviewText, new PublishReviewViewModel(ReviewPlan(false, true, false), "").RiskText);
        Assert.Contains(PublishReviewViewModel.IntuneReviewText, new PublishReviewViewModel(ReviewPlan(true, true, false), "").RiskText);
        Assert.Equal("", new PublishReviewViewModel(ReviewPlan(true, false, false), "").RiskText);
        // The review of all three systems with no warning is the window of the mock: the tooltip of its button has the prompt.
        var three = new PublishReviewViewModel(ReviewPlan(true, true, true), "");
        Assert.Equal("", three.RiskText);
        Assert.Contains("Intune review", three.ConfirmToolTip);
        WithApplication(ApplicationTheme.Light, () =>
        {
            var window = new PublishReviewWindow(new PublishReviewViewModel(ReviewPlan(false, true, false), ""));
            Show(window);
            try
            {
                Settle(window);
                var text = ById<TextBlock>(window, "txtReviewRisk");
                Assert.True(text.IsVisible);
                Assert.Contains("Intune review", text.Text);
            }
            finally { window.Close(); }
        });
    }
}
