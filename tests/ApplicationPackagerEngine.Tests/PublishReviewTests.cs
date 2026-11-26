using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Overhaul V1 phase 5a (Q-126): the publish review before the run and the results after it.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PublishReviewTests
{
    private static PublishReadiness Ready(bool configMgr, bool intune, bool wsus, bool creds = true, bool server = true) =>
        new(new SystemsInUse { ConfigMgr = configMgr, Intune = intune, Wsus = wsus }, true, "MCM", "\\\\cm01\\share",
            creds ? "t" : "", creds ? "c" : "", creds, server ? "wsus01" : "");

    private static List<PublishPlanApp> Apps(int count, int hidden = 0) =>
        Enumerable.Range(1, count).Select(i => new PublishPlanApp("u" + i, "App" + i, "1." + i, i <= hidden)).ToList();

    [Fact]
    public void ThePlanHasOneCellForEachApplicationAndSystemInPublishOrder()
    {
        var readiness = Ready(true, true, true);
        var plan = PublishPlanner.Plan(Apps(2), readiness.Systems, readiness);
        Assert.Equal(["ConfigMgr", "Intune", "WSUS"], plan.Systems);
        Assert.Equal(2, plan.Rows.Count);
        Assert.All(plan.Rows, r => Assert.Equal(3, r.Cells.Count));
        Assert.Equal(PublishPlan.ConfigMgrAction, plan.Rows[0].Cells[0].Text);
        Assert.Equal(PublishPlan.IntuneAction, plan.Rows[0].Cells[1].Text);
        Assert.Equal(PublishPlan.WsusAction, plan.Rows[0].Cells[2].Text);
        Assert.Equal(6, plan.Writes);
        Assert.Equal("2 applications, 3 systems: 6 publishes are planned.", plan.CountsText);
        Assert.Equal("Publish 2 applications", plan.ConfirmText);
        Assert.True(plan.CanRun);
    }

    [Fact]
    public void ASingleApplicationAndSystemReadInTheSingular()
    {
        var readiness = Ready(true, false, false);
        var plan = PublishPlanner.Plan(Apps(1), readiness.Systems, readiness);
        Assert.Equal("1 application, 1 system: 1 publish is planned.", plan.CountsText);
        Assert.Equal("Publish 1 application", plan.ConfirmText);
    }

    [Fact]
    public void ASystemThatIsNotReadyIsBlockedWithItsReasonAndTheRunCannotStart()
    {
        var readiness = Ready(true, true, false, creds: false);
        var plan = PublishPlanner.Plan(Apps(1), readiness.Systems, readiness);
        Assert.False(plan.CanRun);
        Assert.Equal(["Intune"], plan.Blocked.Select(b => b.Destination));
        Assert.StartsWith("Blocked: ", plan.Rows[0].Cells[1].Text);
        Assert.Equal(PublishCellKind.Create, plan.Rows[0].Cells[0].Kind);
        Assert.Equal(PublishCellKind.Blocked, plan.Rows[0].Cells[1].Kind);
    }

    [Fact]
    public void NoSystemInUseOrNoApplicationMeansNothingToRun()
    {
        var none = Ready(false, false, false);
        Assert.False(PublishPlanner.Plan(Apps(1), none.Systems, none).CanRun);
        var some = Ready(true, false, false);
        Assert.False(PublishPlanner.Plan([], some.Systems, some).CanRun);
    }

    [Fact]
    public void ACheckedApplicationOutsideTheViewIsListedAndSaid()
    {
        var readiness = Ready(true, false, false);
        var model = new PublishReviewViewModel(PublishPlanner.Plan(Apps(3, hidden: 2), readiness.Systems, readiness), "");
        Assert.Equal(2, model.Plan.HiddenCount);
        Assert.Equal("2 checked applications are outside the current view. They are published too.", model.HiddenText);
        Assert.Equal(["Outside the view", "Outside the view", ""], model.Rows.Select(r => r.Checked));
        var none = new PublishReviewViewModel(PublishPlanner.Plan(Apps(2), readiness.Systems, readiness), "");
        Assert.Equal("", none.HiddenText);
    }

    [Fact]
    public void TheCommentTravelsWithTheConfirmAndBackKeepsNothingRunning()
    {
        var readiness = Ready(true, false, false);
        var model = new PublishReviewViewModel(PublishPlanner.Plan(Apps(1), readiness.Systems, readiness), "WO-1");
        model.Comment = "  WO-2  ";
        model.AcceptCommand.Execute(null);
        Assert.True(model.Accepted);
        Assert.Equal("WO-2", model.Comment);

        var back = new PublishReviewViewModel(PublishPlanner.Plan(Apps(1), readiness.Systems, readiness), "");
        back.BackCommand.Execute(null);
        Assert.False(back.Accepted);
    }

    [Fact]
    public void ABlockedPlanCannotBeConfirmed()
    {
        var readiness = Ready(true, true, false, creds: false);
        var model = new PublishReviewViewModel(PublishPlanner.Plan(Apps(1), readiness.Systems, readiness), "");
        Assert.True(model.HasBlocked);
        Assert.Contains("Intune", model.BlockedText);
        model.AcceptCommand.Execute(null);
        Assert.False(model.Accepted);
    }

    [Fact]
    public void ASystemThatCannotTakeAnApplicationIsNotCountedAsAPublish()
    {
        var readiness = Ready(true, true, true);
        var apps = Apps(1).Select(a => a with { IntuneNotSupported = "a PS1 packager publishes to ConfigMgr only", ConfigMgrSteps = "distribute content to DP Group" });
        var plan = PublishPlanner.Plan(apps, readiness.Systems, readiness);
        Assert.Equal(PublishCellKind.NotSupported, plan.Rows[0].Cells[1].Kind);
        Assert.Equal("Not supported: a PS1 packager publishes to ConfigMgr only", plan.Rows[0].Cells[1].Text);
        Assert.Equal(PublishPlan.ConfigMgrAction + ". Then: distribute content to DP Group", plan.Rows[0].Cells[0].Text);
        Assert.Equal(2, plan.Writes);
        Assert.True(plan.CanRun);

        var intuneOnly = Ready(false, true, false);
        Assert.False(PublishPlanner.Plan(apps, intuneOnly.Systems, intuneOnly).CanRun);
        Assert.NotEqual(plan.Digest, PublishPlanner.Plan(apps.Select(a => a with { ConfigMgrSteps = "" }), readiness.Systems, readiness).Digest);
    }

    [Fact]
    public void ASupersedenceThatWasNotAppliedShowsInTheResultsAndAnUnreachedSystemReadsNotRun()
    {
        var rows = new List<(string, PublishRowResult)>
        {
            ("Notepad++", Row("a", Cell("ConfigMgr", PublishDestinationKind.Published, "", "supersedence not applied: access denied") with { Warning = true })),
            ("Audacity", Row("b"))
        };
        var model = new PublishResultsViewModel(rows, cancelled: false, ["ConfigMgr", "WSUS"]);
        Assert.Equal("Publish completed with issues", model.Headline);
        Assert.Equal(["Published, supersedence not applied", "Not run"], model.Rows[0].Cells);
        Assert.Equal(["Not run", "Not run"], model.Rows[1].Cells);
        Assert.Contains("Notepad++, ConfigMgr: supersedence not applied: access denied", model.Details);
    }

    // The read before the write (phase 5b)

    private static PublishPlan SitePlan(params ConfigMgrFact[] facts)
    {
        var readiness = Ready(true, false, false);
        return PublishPlanner.Plan(Apps(facts.Length).Select((a, i) => a with { Site = facts[i] }), readiness.Systems, readiness);
    }

    [Fact]
    public void TheDigestChangesWhenAnApplicationAppearsInConfigMgrAndNotForTheWordsOfAFailedRead()
    {
        var planned = SitePlan(ConfigMgrFact.None);
        Assert.Equal(planned.Digest, SitePlan(ConfigMgrFact.None).Digest);
        Assert.NotEqual(planned.Digest, SitePlan(ConfigMgrFact.Found("1.1")).Digest);
        Assert.NotEqual(SitePlan(ConfigMgrFact.Found("1.0")).Digest, SitePlan(ConfigMgrFact.Found("1.1")).Digest);
        Assert.Equal(SitePlan(ConfigMgrFact.Unreadable("one")).Digest, SitePlan(ConfigMgrFact.Unreadable("two")).Digest);
        Assert.Empty(PublishPlanner.Changes(planned, SitePlan(ConfigMgrFact.None)));
        Assert.Equal("App1: ConfigMgr has version 1.1.", PublishPlanner.Changes(planned, SitePlan(ConfigMgrFact.Found("1.1")))["u1"]);
        Assert.Equal("App1: ConfigMgr no longer has the application.", PublishPlanner.Changes(SitePlan(ConfigMgrFact.Found("1.1")), planned)["u1"]);
    }

    [Fact]
    public void TheConfigMgrCellSaysWhatTheSiteHas()
    {
        Assert.Equal("ConfigMgr has this version: the run asks before it replaces it", SitePlan(ConfigMgrFact.Found("1.1")).Rows[0].Cells[0].Text);
        Assert.Equal(PublishPlan.ConfigMgrAction + " (ConfigMgr has 0.9)", SitePlan(ConfigMgrFact.Found("0.9")).Rows[0].Cells[0].Text);
        Assert.Equal(PublishPlan.ConfigMgrAction + " (ConfigMgr was not read)", SitePlan(ConfigMgrFact.Unreadable("x")).Rows[0].Cells[0].Text);
    }

    [Fact]
    public async Task AnUnchangedReadConfirmsAtOnce()
    {
        var shows = new List<PublishReviewViewModel>();
        var confirmed = await PublishReviewGate.ConfirmAsync(SitePlan(ConfigMgrFact.None), "WO-1",
            m => { shows.Add(m); m.AcceptCommand.Execute(null); return m.Accepted; },
            _ => Task.FromResult<PublishPlan?>(SitePlan(ConfigMgrFact.None)), _ => { });
        Assert.NotNull(confirmed);
        Assert.Equal("", Assert.Single(shows).ChangedText);
    }

    [Fact]
    public async Task AChangedReadOpensTheReviewAgainWithTheRowMarkedAndBackPublishesNothing()
    {
        var shows = new List<PublishReviewViewModel>();
        var log = new List<string>();
        var confirmed = await PublishReviewGate.ConfirmAsync(SitePlan(ConfigMgrFact.None, ConfigMgrFact.None), "",
            m =>
            {
                shows.Add(m);
                if (shows.Count > 1) return false;
                m.Comment = "WO-2";
                m.AcceptCommand.Execute(null);
                return true;
            },
            _ => Task.FromResult<PublishPlan?>(SitePlan(ConfigMgrFact.None, ConfigMgrFact.Found("1.2"))), log.Add);
        Assert.Null(confirmed);
        Assert.Equal(2, shows.Count);
        Assert.Equal("WO-2", shows[1].Comment);
        Assert.Equal(["", "Changed"], shows[1].Rows.Select(r => r.Checked));
        Assert.Contains("App2: ConfigMgr has version 1.2.", shows[1].ChangedText);
        Assert.StartsWith("Nothing was published.", shows[1].ChangedText);
        Assert.Contains(log, l => l.Contains("Confirmed ") && l.Contains(", read "));
    }

    [Fact]
    public async Task ASecondConfirmOfAnUnreadableSiteRuns()
    {
        var shows = 0;
        var confirmed = await PublishReviewGate.ConfirmAsync(SitePlan(ConfigMgrFact.None), "",
            m => { shows++; m.AcceptCommand.Execute(null); return true; },
            _ => Task.FromResult<PublishPlan?>(SitePlan(ConfigMgrFact.Unreadable("no answer"))), _ => { });
        Assert.NotNull(confirmed);
        Assert.Equal(2, shows);
    }

    [Fact]
    public async Task ACancelledReadPublishesNothing()
    {
        var log = new List<string>();
        var confirmed = await PublishReviewGate.ConfirmAsync(SitePlan(ConfigMgrFact.None), "",
            m => { m.AcceptCommand.Execute(null); return true; }, _ => Task.FromResult<PublishPlan?>(null), log.Add);
        Assert.Null(confirmed);
        Assert.Contains(log, l => l.Contains("was cancelled. Nothing was published."));
    }

    // The results

    private static PublishRowResult Row(string uid, params PublishDestinationResult[] cells) => new(uid, "1.0", cells);

    private static PublishDestinationResult Cell(string d, PublishDestinationKind k, string step = "", string message = "") => new(d, k, step, message);

    [Fact]
    public void TheResultsCountEveryCellAndSayWhatReachedWhere()
    {
        var rows = new List<(string, PublishRowResult)>
        {
            ("Notepad++", Row("a", Cell("ConfigMgr", PublishDestinationKind.Published), Cell("Intune", PublishDestinationKind.Failed, "content upload", "upload failed"))),
            ("Audacity", Row("b", Cell("ConfigMgr", PublishDestinationKind.Published), Cell("Intune", PublishDestinationKind.Published))),
            ("7-Zip", Row("c", Cell("ConfigMgr", PublishDestinationKind.Skipped, "", "kept"), Cell("Intune", PublishDestinationKind.Published)))
        };
        var model = new PublishResultsViewModel(rows, cancelled: false);
        Assert.Equal(["ConfigMgr", "Intune"], model.Systems);
        Assert.Equal(4, model.Published);
        Assert.Equal(1, model.Failed);
        Assert.Equal(1, model.Skipped);
        Assert.Equal("Publish completed with issues", model.Headline);
        Assert.Equal(["Published", "Failed at content upload"], model.Rows[0].Cells);
        Assert.Equal(
            "Notepad++ reached ConfigMgr. It did not reach Intune (Failed at content upload). What reached a system stays published.",
            Assert.Single(model.Notices));
        Assert.Contains("Notepad++, Intune: upload failed", model.Details);
    }

    [Theory]
    [InlineData(1, false, "1 publish completed")]
    [InlineData(2, false, "2 publishes completed")]
    [InlineData(0, false, "Nothing was published")]
    [InlineData(2, true, "Publish was cancelled")]
    public void TheHeadlineIsGeneratedFromTheCounts(int published, bool cancelled, string expected)
    {
        var cells = Enumerable.Range(0, published).Select(i => Cell("ConfigMgr", PublishDestinationKind.Published)).ToArray();
        var rows = published == 0 ? new List<(string, PublishRowResult)>() : cells.Select((c, i) => ("App" + i, Row("u" + i, c))).ToList();
        Assert.Equal(expected, new PublishResultsViewModel(rows, cancelled).Headline);
    }

    // The windows

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheReviewWindowShowsAColumnForEachSystemAndDisablesConfirmWhenBlocked(ApplicationTheme theme)
    {
        RenderKit.WithApplication(theme, () =>
        {
            var readiness = Ready(true, true, false, creds: false);
            var model = new PublishReviewViewModel(PublishPlanner.Plan(Apps(2, hidden: 1), readiness.Systems, readiness), "");
            var window = new PublishReviewWindow(model);
            RenderKit.Show(window);
            try
            {
                window.UpdateLayout();
                var grid = RenderKit.ById<DataGrid>(window, "gridReviewMatrix");
                Assert.Equal(["Application", "Version", "Checked", "ConfigMgr", "Intune"], grid.Columns.Select(c => (string)c.Header));
                var confirm = RenderKit.ById<ButtonBase>(window, "btnReviewConfirm");
                Assert.Equal("Publish 2 applications", (string)confirm.Content);
                Assert.False(confirm.IsEnabled, "Confirm is enabled while a system is blocked.");
                Assert.True(RenderKit.ById<TextBlock>(window, "txtReviewBlocked").IsVisible);
                Assert.True(RenderKit.ById<TextBlock>(window, "txtReviewHidden").IsVisible);
                Assert.True(RenderKit.ById<ButtonBase>(window, "btnReviewBack").IsEnabled);
                Assert.Equal("2 applications, 2 systems: 4 publishes are planned.", RenderKit.ById<TextBlock>(window, "txtReviewCounts").Text);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheReviewConfirmIsEnabledWhenEverySystemIsReady(ApplicationTheme theme)
    {
        RenderKit.WithApplication(theme, () =>
        {
            var readiness = Ready(true, false, true);
            var model = new PublishReviewViewModel(PublishPlanner.Plan(Apps(1), readiness.Systems, readiness), "");
            var window = new PublishReviewWindow(model);
            RenderKit.Show(window);
            try
            {
                window.UpdateLayout();
                Assert.True(RenderKit.ById<ButtonBase>(window, "btnReviewConfirm").IsEnabled);
                Assert.False(RenderKit.ById<TextBlock>(window, "txtReviewBlocked").IsVisible);
                Assert.False(RenderKit.ById<TextBlock>(window, "txtReviewHidden").IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheResultsWindowShowsTheHeadlineTheCellsAndTheNotice(ApplicationTheme theme)
    {
        RenderKit.WithApplication(theme, () =>
        {
            var rows = new List<(string, PublishRowResult)>
            {
                ("Notepad++", Row("a", Cell("ConfigMgr", PublishDestinationKind.Published), Cell("Intune", PublishDestinationKind.Failed, "content upload", "upload failed")))
            };
            var window = new PublishResultsWindow(new PublishResultsViewModel(rows, false));
            RenderKit.Show(window);
            try
            {
                window.UpdateLayout();
                Assert.Equal("Publish completed with issues", RenderKit.ById<TextBlock>(window, "txtResultsHeadline").Text);
                Assert.Equal("Published 1", RenderKit.ById<TextBlock>(window, "txtResultsPublished").Text);
                Assert.Equal("Failed 1", RenderKit.ById<TextBlock>(window, "txtResultsFailed").Text);
                var grid = RenderKit.ById<DataGrid>(window, "gridResultsMatrix");
                Assert.Equal(["Application", "ConfigMgr", "Intune"], grid.Columns.Select(c => (string)c.Header));
                var texts = RenderKit.Descendants(window).OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert.Contains("Failed at content upload", texts);
                Assert.Contains(texts, t => t.StartsWith("Notepad++ reached ConfigMgr.", StringComparison.Ordinal));
                Assert.True(RenderKit.ById<ButtonBase>(window, "btnResultsDone").IsEnabled);
            }
            finally { window.Close(); }
        });
    }
}
