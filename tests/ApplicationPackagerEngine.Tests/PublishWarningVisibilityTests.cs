using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerUI.Controls.Mock;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using CheckBox = System.Windows.Controls.CheckBox;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridRow = System.Windows.Controls.DataGridRow;
using TextBox = System.Windows.Controls.TextBox;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// What the review, the One Click plan and the results say as visible text when it applies: the warnings, the comment of the run, the prompt that
/// can overwrite, the failure messages, and the controls that stay in reach.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PublishWarningVisibilityTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("apd-warnings-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    private static PublishReadiness Ready(bool configMgr = true, bool intune = false, bool wsus = false) =>
        new(new SystemsInUse { ConfigMgr = configMgr, Intune = intune, Wsus = wsus }, true, "MCM", @"\\cm01\share", "t", "c", true, "wsus01");

    private static PublishPlanApp App(ConfigMgrFact site) => new("u1", "App1 (x64)", "1.1", false) { Site = site };

    private static PublishPlan Plan(PublishPlanApp app, PublishReadiness? readiness = null, bool skips = true)
    {
        readiness ??= Ready();
        return PublishPlanner.Plan([app], readiness.Systems, readiness, skips);
    }

    private static string Existing(PublishReviewViewModel model) => model.OtherLines.Single(l => l.Label == "Existing application").Text;

    [Fact]
    public void TheOneSystemReviewKeepsTheWarningsThatItsTableCuts()
    {
        var history = new OneClickHistoryEntry
        {
            Published = new Dictionary<string, OneClickPublishRecord> { ["ConfigMgr"] = new() { Version = "1.0", Unverified = true } }
        };
        var model = new PublishReviewViewModel(Plan(App(ConfigMgrFact.Found("0.9")) with { History = history, ConfigMgrSteps = "distribute content to DPs" }), "");

        Assert.Equal(PublishPlan.ConfigMgrAction, Assert.Single(model.Lines).Change);
        Assert.Contains("App1 (x64): ConfigMgr has 0.9. If the title matches, the run asks before it replaces it.", model.RiskText);
        Assert.Contains("earlier published version cannot be verified", model.RiskText);
        Assert.Contains("Application Already Exists (Overwrite, Skip or Cancel remaining)", model.RiskText);
        Assert.DoesNotContain("Keep", Existing(model));
        Assert.Contains("The run can replace it.", Existing(model));
    }

    [Fact]
    public void ARetryAndAPlanWithNoOlderApplicationSayNothingAboutAReplace()
    {
        var retry = new PublishReviewViewModel(Plan(App(ConfigMgrFact.Found("0.9")), skips: false) with { RetryDestination = "ConfigMgr" }, "");
        Assert.Equal("The retry does not replace an application. It publishes nothing when ConfigMgr has this version.", retry.RiskText);
        Assert.Equal("Keep App1 0.9", Existing(retry));

        Assert.Equal("", new PublishReviewViewModel(Plan(App(ConfigMgrFact.None)), "").RiskText);
        Assert.Contains("Overwrite", new PublishReviewViewModel(Plan(App(ConfigMgrFact.Unreadable("x"))), "").RiskText);
    }

    [Fact]
    public void TheDeploymentColumnNeedsADeployStepAndAScriptDecidesForItself()
    {
        string Deployment(PublishPlanApp app) => Assert.Single(new PublishReviewViewModel(Plan(app), "").Lines).Deployment;
        Assert.Equal("None", Deployment(App(ConfigMgrFact.None) with { ConfigMgrSteps = "distribute content to Deployment DPs" }));
        Assert.Equal("Test deployment", Deployment(App(ConfigMgrFact.None) with { ConfigMgrSteps = "distribute content to DPs, deploy to Test" }));

        var script = App(ConfigMgrFact.None) with { ScriptDecides = true };
        Assert.Equal("Script decides", Deployment(script));
        var model = new PublishReviewViewModel(Plan(script), "");
        Assert.All(model.OtherLines.Where(l => l.Label is "Distribution" or "Supersedence"), l => Assert.Equal("The PS1 packager script decides", l.Text));

        var readiness = Ready(wsus: true);
        var several = new PublishReviewViewModel(Plan(script, readiness), "").SystemCards.Single(c => c.Destination == "ConfigMgr").Text;
        Assert.Contains("the PS1 packager script decides distribution and deployment", several);
        Assert.DoesNotContain("No supersedence", several);
        Assert.DoesNotContain("Keep existing", several);
    }

    [Fact]
    public void SupersedenceFollowsTheModeOfTheApplication()
    {
        var on = App(ConfigMgrFact.None) with { ConfigMgrSteps = PublishPlan.SupersedenceStep };
        var model = new PublishReviewViewModel(Plan(on), "");
        Assert.Equal("On. The run asks before it applies it.", model.OtherLines.Single(l => l.Label == "Supersedence").Text);
        Assert.Equal("Off", new PublishReviewViewModel(Plan(App(ConfigMgrFact.None)), "").OtherLines.Single(l => l.Label == "Supersedence").Text);

        var readiness = Ready(wsus: true);
        Assert.StartsWith("The run asks before it applies supersedence", new PublishReviewViewModel(Plan(on, readiness), "").SystemCards[0].Text);
        Assert.StartsWith("The run can replace", new PublishReviewViewModel(Plan(App(ConfigMgrFact.Found("0.9")), readiness), "").SystemCards[0].Text);
        Assert.StartsWith("Keep existing applications.", new PublishReviewViewModel(Plan(App(ConfigMgrFact.None), readiness), "").SystemCards[0].Text);
    }

    [Fact]
    public void TheKeptNoticeIsSaidOnlyWhenTheRunCreatedTheApplicationBesideTheOlderOne()
    {
        var app = App(ConfigMgrFact.Found("0.9"));
        List<(string, PublishRowResult)> Rows(bool created) =>
        [
            (app.Application, new PublishRowResult("u1", "1.1", [new PublishDestinationResult("ConfigMgr", PublishDestinationKind.Published, "", "") { Created = created }]))
        ];
        var none = new HashSet<string>();
        Assert.Equal("App1 0.9 was kept. Supersedence is off.", PackagesViewModel.KeptNoticeOf(Rows(true), [app], none));
        Assert.Equal("", PackagesViewModel.KeptNoticeOf(Rows(true), [app], new HashSet<string> { "u1" }));
        Assert.Equal("", PackagesViewModel.KeptNoticeOf(Rows(false), [app], none));
        Assert.Equal("", PackagesViewModel.KeptNoticeOf(Rows(true), [app with { ConfigMgrSteps = PublishPlan.SupersedenceStep }], none));
        Assert.Equal("", PackagesViewModel.KeptNoticeOf(Rows(true), [app with { ScriptDecides = true }], none));
        Assert.Equal("", PackagesViewModel.KeptNoticeOf(Rows(true), [App(ConfigMgrFact.None)], none));
    }

    private static string TextOf(Window window, string id) => ById<TextBlock>(window, id).Text;

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheReviewWindowShowsTheWarningsAndTheCommentOnlyWhenTheyApply(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var window = new PublishReviewWindow(new PublishReviewViewModel(Plan(App(ConfigMgrFact.Found("0.9"))), "WO-1"));
            Show(window);
            try
            {
                Settle(window);
                Assert.True(ById<TextBlock>(window, "txtReviewRisk").IsVisible);
                Assert.Contains("Overwrite replaces the application", TextOf(window, "txtReviewRisk"));
                var comment = ById<TextBox>(window, "txtReviewComment");
                Assert.True(comment.IsVisible && comment.IsEnabled);
                Assert.Equal("WO-1", comment.Text);
                // An emptied comment keeps its box, so the operator can type again.
                comment.Text = "";
                Settle(window);
                Assert.True(comment.IsVisible);
                Assert.Equal("", window.ViewModel.Comment);
            }
            finally { window.Close(); }

            var plain = new PublishReviewWindow(new PublishReviewViewModel(Plan(App(ConfigMgrFact.None)), ""));
            Show(plain);
            try
            {
                Settle(plain);
                Assert.False(ById<TextBlock>(plain, "txtReviewRisk").IsVisible);
                Assert.False(ById<TextBox>(plain, "txtReviewComment").IsVisible);
            }
            finally { plain.Close(); }
        });
    }

    [Fact]
    public void TheReviewAndTheResultsFitASmallWorkAreaWithTheirButtons()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var readiness = Ready(intune: true, wsus: true);
            var rows = new List<(string, PublishRowResult)>
            {
                ("App1", new PublishRowResult("u1", "1.1", [new PublishDestinationResult("ConfigMgr", PublishDestinationKind.Failed, "stage", "failed")]))
            };
            (Window Window, string[] Buttons)[] windows =
            [
                (new PublishReviewWindow(new PublishReviewViewModel(Plan(App(ConfigMgrFact.Found("0.9")), readiness), "WO-1")), ["btnReviewBack", "btnReviewConfirm"]),
                (new PublishResultsWindow(new PublishResultsViewModel(rows, false, ["ConfigMgr", "Intune", "WSUS"], offerRetry: true)
                    { ReportPath = "r.md", ShowLogs = _ => { }, ShowHistory = (_, _) => { } }), ["btnResultsHistory", "btnResultsReport", "btnResultsLogs", "btnResultsDone"])
            ];
            foreach (var (window, buttons) in windows)
            {
                Show(window);
                try
                {
                    DialogFrame.FitToWorkArea(window, new Rect(0, 0, 1024, 680));
                    Settle(window);
                    Assert.True(window.ActualWidth <= 1024 && window.ActualHeight <= 680, $"{window.ActualWidth}x{window.ActualHeight}");
                    foreach (var id in buttons) AssertInside(window, ById<ButtonBase>(window, id), window.ActualHeight, id);
                }
                finally { window.Close(); }
            }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheResultsOfSeveralSystemsShowWhatToVerifyAndWhereTheRetryIsAndOpenTheFailureMessages(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var rows = new List<(string, PublishRowResult)>
            {
                ("App1", new PublishRowResult("u1", "1.1",
                [
                    new PublishDestinationResult("ConfigMgr", PublishDestinationKind.Published, "", "") { Created = true },
                    new PublishDestinationResult("Intune", PublishDestinationKind.Failed, "content upload", "403 Forbidden"),
                    new PublishDestinationResult("WSUS", PublishDestinationKind.Failed, "", "server unreachable"),
                ]))
            };
            var model = new PublishResultsViewModel(rows, false, ["ConfigMgr", "Intune", "WSUS"], offerRetry: true)
                { WithoutDeployment = new HashSet<string> { "u1" } };
            var window = new PublishResultsWindow(model);
            Show(window);
            try
            {
                Settle(window);
                // The window of the mock names the failure in its notice and its cells; the row menu opens the message of each system.
                var open = window.BuildRowMenu(model.Lines[0]).Items.OfType<System.Windows.Controls.MenuItem>()
                    .Single(i => System.Windows.Automation.AutomationProperties.GetAutomationId(i) == "miResultsFailureMessages");
                Assert.DoesNotContain("App1, Intune: 403 Forbidden", Descendants(window).OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text));
                open.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                Settle(window);
                var texts = Descendants(window).OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
                Assert.Contains("App1, Intune: 403 Forbidden", texts);
                Assert.Contains("App1, WSUS: server unreachable", texts);
                Assert.Contains("App1, Intune: " + PublishRetry.IntuneText, texts);
                Assert.Equal("Publishing created a ConfigMgr application. No deployment was created.", TextOf(window, "txtResultsDeployment"));
                Assert.True(ById<TextBlock>(window, "txtResultsDeployment").IsVisible);
                var help = ById<TextBlock>(window, "txtResultsRowMenu");
                Assert.True(help.IsVisible);
                Assert.Contains("Apps key or Shift+F10", help.Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TheDetailsColumnWrapsAMessageAndOnlyTheOtherMessagesStandUnderTheTable()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var message = string.Join(" ", Enumerable.Repeat("The installer download failed.", 12));
            var rows = new List<(string, PublishRowResult)>
            {
                ("App1", new PublishRowResult("u1", "1.1", [new PublishDestinationResult("ConfigMgr", PublishDestinationKind.Failed, "stage", message)]))
            };
            var model = new PublishResultsViewModel(rows, false, ["ConfigMgr"]) { IsOneClick = true };
            Assert.Empty(model.FailureLines);
            Assert.Equal("", model.RowMenuHelp);
            var window = new PublishResultsWindow(model);
            Show(window);
            try
            {
                Settle(window);
                var cell = Descendants(window).OfType<TextBlock>().First(t => t.IsVisible && t.Text == message);
                Assert.Equal(TextWrapping.Wrap, cell.TextWrapping);
                var row = Row(ById<DataGrid>(window, "gridResultsMatrix"), 0);
                Assert.True(row.ActualHeight > 40, $"The row is {row.ActualHeight} px high, so the message does not show whole.");
                Assert.True(cell.ActualHeight <= row.ActualHeight);
            }
            finally { window.Close(); }
        });
    }

    private sealed class NoRunner : IOneClickRunner
    {
        public Task<bool> RunAsync(IReadOnlyList<OneClickPlanItem> rows, OneClickRunOptions options, CancellationToken ct) => Task.FromResult(false);
    }

    private OneClickPlanViewModel OneClick(int count, string onExisting = OneClickOnExisting.Skip, string comment = "", bool wsusReady = true,
        bool supersedes = false)
    {
        var apps = Enumerable.Range(0, count).Select(i => (new OneClickApp("a" + i, $"App {i:00}", "Contoso", "2.0", "1.0"),
            (PackagerInfo?)new PackagerInfo { Uid = "a" + i, Application = $"App {i:00}" })).ToList();
        return new OneClickPlanViewModel(new OneClickPlanContext
        {
            Apps = apps,
            Settings = () => new OneClickPlanSettings
            {
                Tracked = apps.Select(a => a.Item1.Key).ToList(),
                DefaultDestinations = new DestinationSet(true, !wsusReady, false),
                Readiness = OneClickPlanner.Readiness(new PublishReadiness(new SystemsInUse { ConfigMgr = true }, true, "MCM", @"\\cm01\share", "", "", false, ""))
            },
            Action = OneClickActionNames.StageAndPackage,
            OnExisting = onExisting,
            Comment = comment,
            Supersedes = _ => supersedes,
            ReportFolder = _folder
        }, new NoRunner());
    }

    [Fact]
    public void AnIncludedRowSaysWhyADestinationIsLeftOut()
    {
        var model = OneClick(1, wsusReady: false);
        var row = Assert.Single(model.Rows);
        Assert.True(row.Include);
        Assert.Contains("WSUS: WSUS is not in use", row.PlannedLine);
        Assert.StartsWith(row.Planned + " \u00b7 ", row.PlannedLine);
        WithApplication(ApplicationTheme.Light, () =>
        {
            var window = new OneClickPlanWindow(model, _folder);
            Show(window);
            try
            {
                Settle(window);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), t => t.IsVisible && t.Text == row.PlannedLine);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheLastOfManyPlanRowsIsInReachAndTheNoteAndTheForceBoxStay(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var model = OneClick(60);
            var window = new OneClickPlanWindow(model, _folder);
            Show(window);
            try
            {
                Settle(window);
                var grid = (DataGrid)window.FindName("gridPlan");
                var scroller = Descendants(grid).OfType<ScrollViewer>().First();
                Assert.True(scroller.ScrollableHeight > 0, "The grid does not scroll.");
                var last = Row(grid, 59);
                Settle(window);
                var inGrid = last.TransformToAncestor(grid).TransformBounds(new Rect(last.RenderSize));
                Assert.True(inGrid.Top >= -0.5 && inGrid.Bottom <= grid.ActualHeight + 0.5, $"The last row is at {inGrid} in a grid {grid.ActualHeight} px high.");
                var foot = ById<ButtonBase>(window, "btnRun").TransformToAncestor(window).Transform(new System.Windows.Point(0, 0)).Y;
                AssertInside(window, grid, foot, "gridPlan");
                AssertInside(window, ById<CheckBox>(window, "chkForce"), foot, "chkForce");
                AssertInside(window, (FrameworkElement)window.FindName("txtFootNote"), foot, "txtFootNote");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ThePlanShowsTheOverwriteTheSupersedenceAndAnOldComment()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var model = OneClick(1, OneClickOnExisting.Overwrite, "WO-9", supersedes: true);
            Assert.Contains("overwrites an existing ConfigMgr application. Run asks nothing about it after it starts.", model.RiskText);
            Assert.Contains("Supersedence is on for App 00.", model.RiskText);
            var window = new OneClickPlanWindow(model, _folder);
            Show(window);
            try
            {
                Settle(window);
                Assert.True(ById<TextBlock>(window, "txtPlanRisk").IsVisible);
                var comment = ById<TextBox>(window, "txtPlanComment");
                Assert.True(comment.IsVisible && comment.IsEnabled);
                Assert.Equal("WO-9", comment.Text);
                Assert.False(ById<TextBlock>(window, "txtPlanSummary").IsVisible);
                model.PlanOnlyCommand.Execute(null);
                Settle(window);
                Assert.True(ById<TextBlock>(window, "txtPlanSummary").IsVisible);
            }
            finally { window.Close(); }

            var plain = new OneClickPlanWindow(OneClick(1), _folder);
            Show(plain);
            try
            {
                Settle(plain);
                // The text of a collapsed notice is not in the visual tree.
                Assert.DoesNotContain(Descendants(plain).OfType<FrameworkElement>(),
                    e => AutomationProperties.GetAutomationId(e) is "txtPlanRisk" or "txtPlanComment" && e.IsVisible);
            }
            finally { plain.Close(); }
        });
    }

    [Fact]
    public void TheDimLayerOfTheMainWindowTakesNoClicks()
    {
        var xaml = System.Xml.Linq.XDocument.Load(Path.Combine(ApplicationPackagerEngine.Tests.Compliance.CatalogServiceTests.RepositoryRoot(),
            "src", "ApplicationPackagerUI", "Views", "Windows", "MainWindow.xaml"));
        var scrim = xaml.Descendants().Single(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "Scrim"));
        Assert.Equal("False", scrim.Attribute("IsHitTestVisible")?.Value);
    }
}
