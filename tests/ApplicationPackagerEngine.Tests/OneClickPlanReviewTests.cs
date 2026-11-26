using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Overhaul V1 phase 6 (Q-126): the One Click plan header, the source of each row, the comment and the results.</summary>
public sealed class OneClickPlanReviewTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("apd-oneclick-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class FakeRunner(Action<IReadOnlyList<OneClickPlanItem>> run) : IOneClickRunner
    {
        public string Comment { get; private set; } = "";

        public Task<bool> RunAsync(IReadOnlyList<OneClickPlanItem> rows, OneClickRunOptions options, CancellationToken ct)
        {
            Comment = options.Comment;
            run(rows);
            return Task.FromResult(false);
        }
    }

    private OneClickPlanViewModel Model(FakeRunner runner, string action = OneClickActionNames.StageAndPackage)
    {
        var settings = new OneClickPlanSettings
        {
            DefaultDestinations = new DestinationSet(true, false, false),
            Destinations = new Dictionary<string, DestinationSet> { ["b"] = new DestinationSet(true, true, false) }
        };
        return new OneClickPlanViewModel(new OneClickPlanContext
        {
            Apps = [(new OneClickApp("a", "Alpha", "Contoso", "2.0", "1.0"), null), (new OneClickApp("b", "Bravo", "Contoso", "3.0", "1.0"), null)],
            Settings = () => settings,
            Action = action,
            Comment = "WO-1",
            SiteText = "ConfigMgr site MCM",
            SystemsText = "In use: ConfigMgr.",
            ReportFolder = _folder
        }, runner);
    }

    [Fact]
    public void TheFlowFollowsTheSavedAction()
    {
        Assert.Equal("Check Latest, then Check ConfigMgr, then Check CVEs", OneClickPlanViewModel.Flow(OneClickActionNames.Report));
        Assert.EndsWith("then Stage", OneClickPlanViewModel.Flow(OneClickActionNames.Stage));
        Assert.EndsWith("then Publish", OneClickPlanViewModel.Flow(OneClickActionNames.StageAndPackage));
    }

    [Fact]
    public void TheSystemsLineNamesWhatIsInUseAndWhatIsNot()
    {
        Assert.Equal("In use: ConfigMgr, WSUS. Not in use: Intune.", PackagesViewModel.SystemsLine(new SystemsInUse { ConfigMgr = true, Wsus = true, Intune = false }));
        Assert.Equal("In use: none. Not in use: ConfigMgr, Intune, WSUS.", PackagesViewModel.SystemsLine(null));
    }

    [Fact]
    public void EachRowSaysWhereItsDestinationsComeFromAndTheHeaderCounts()
    {
        var model = Model(new FakeRunner(_ => { }));
        Assert.Equal(["Default", "Saved for this application"], model.Rows.Select(r => r.SourceText));
        Assert.EndsWith("ConfigMgr site MCM.", model.HeaderText);

        model.Rows[0].Wsus = true;
        Assert.Equal("Changed for this run", model.Rows[0].SourceText);
        model.Rows[0].Wsus = false;
        Assert.Equal("Default", model.Rows[0].SourceText);
    }

    [Fact]
    public async Task TheRunTakesTheCommentOfThePlanAndGivesResultsAndTheFailedRow()
    {
        var runner = new FakeRunner(rows =>
        {
            rows[0].Cells.Add(OneClickRowRunner.ResultCell("ConfigMgr", InventoryOutcome.Succeeded, "published Alpha", "Alpha"));
            rows[0].Outcome = OneClickOutcomes.Published;
            rows[1].Cells.Add(OneClickRowRunner.ResultCell("ConfigMgr", InventoryOutcome.Failed, "access denied", ""));
            rows[1].Outcome = OneClickOutcomes.Failed;
        });
        var model = Model(runner);
        foreach (var row in model.Rows) row.Include = true;
        model.Comment = "  WO-2 ";
        var ready = 0;
        model.ResultsReady += (_, _) => ready++;

        await model.RunCommand.ExecuteAsync(null);

        Assert.Equal("WO-2", runner.Comment);
        Assert.Equal(1, ready);
        Assert.True(model.HasResults);
        Assert.True(model.HasFailure);
        Assert.Equal("Bravo", model.FirstFailed!.Application);
        var results = model.BuildResults();
        Assert.Equal(1, results.Published);
        Assert.Equal(1, results.Failed);
        Assert.Equal("Publish completed with issues", results.Headline);
    }

    [Theory]
    [InlineData(InventoryOutcome.Succeeded, "Published")]
    [InlineData(InventoryOutcome.AlreadyPresent, "Published")]
    [InlineData(InventoryOutcome.PublishedSupersedenceNotApplied, "Published, supersedence not applied")]
    [InlineData(InventoryOutcome.Partial, "Failed at content")]
    [InlineData(InventoryOutcome.Skipped, "Skipped")]
    [InlineData(InventoryOutcome.Refused, "Not supported")]
    [InlineData(InventoryOutcome.Failed, "Failed")]
    public void TheResultsCellFollowsTheRecordedOutcome(InventoryOutcome outcome, string text)
    {
        Assert.Equal(text, OneClickRowRunner.ResultCell("ConfigMgr", outcome, "m", "id").Text);
    }
}
