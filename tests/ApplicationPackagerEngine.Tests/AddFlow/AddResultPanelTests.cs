using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests.AddFlow;

public sealed class AddResultPanelTests
{
    private static AddedDefinition Added(bool needsReview, string? reason = null) =>
        new(DefinitionId: "contoso0001", DefinitionPath: @"C:\defs\contoso.json", DisplayName: "Contoso App", NeedsReview: needsReview, ReviewReason: reason, Warnings: []);

    [Fact]
    public async Task TheResultSaysWhatWasAddedAndSelectsTheRow()
    {
        var actions = new FakeResultActions();
        var panel = new AddResultViewModel(actions);

        await panel.ShowAsync(Added(needsReview: false));

        Assert.True(panel.IsVisible);
        Assert.Contains("Contoso App", panel.Summary);
        Assert.Equal(@"C:\defs\contoso.json", panel.DefinitionPath);
        Assert.Equal(["contoso0001"], actions.Selected);
    }

    [Fact]
    public async Task HighConfidenceMakesDoneTheDefaultButton()
    {
        var panel = new AddResultViewModel(new FakeResultActions());
        await panel.ShowAsync(Added(needsReview: false));
        Assert.True(panel.IsDoneDefault);
        Assert.False(panel.IsWorkbenchDefault);
        Assert.Equal("", panel.ReasonText);
    }

    [Fact]
    public async Task NeedsReviewMakesOpenInWorkbenchTheDefaultAndShowsTheReason()
    {
        var panel = new AddResultViewModel(new FakeResultActions());
        await panel.ShowAsync(Added(needsReview: true, reason: "No silent switch could be derived."));
        Assert.True(panel.IsWorkbenchDefault);
        Assert.False(panel.IsDoneDefault);
        Assert.Equal("No silent switch could be derived.", panel.ReasonText);

        await panel.ShowAsync(Added(needsReview: true));
        Assert.Equal("Detection needs review.", panel.ReasonText);
    }

    [Fact]
    public async Task StageIsNeverStartedOnItsOwn()
    {
        var actions = new FakeResultActions();
        var panel = new AddResultViewModel(actions);
        await panel.ShowAsync(Added(needsReview: false));
        await panel.ShowAsync(Added(needsReview: true));
        Assert.Empty(actions.Staged);

        await panel.StageNowCommand.ExecuteAsync(null);
        Assert.Equal(["contoso0001"], actions.Staged);
        Assert.Equal("Staged.", panel.StageStatus);
    }

    [Fact]
    public async Task OpenInWorkbenchOpensTheNewDefinitionAndDoneClosesThePanel()
    {
        var actions = new FakeResultActions();
        var panel = new AddResultViewModel(actions);
        var closed = 0;
        panel.Closed += (_, _) => closed++;
        await panel.ShowAsync(Added(needsReview: true));

        panel.OpenInWorkbenchCommand.Execute(null);
        Assert.Equal(["contoso0001"], actions.Opened);

        panel.DoneCommand.Execute(null);
        Assert.False(panel.IsVisible);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task AGridThatCannotSelectTheRow_StillShowsThePanelWithAStatusLine()
    {
        var actions = new FakeResultActions { SelectFails = new InvalidOperationException("The grid is not loaded.") };
        var panel = new AddResultViewModel(actions);

        await panel.ShowAsync(Added(needsReview: true));

        Assert.True(panel.IsVisible);
        Assert.Contains("The grid is not loaded.", panel.StageStatus);
        Assert.Contains("was saved", panel.StageStatus);
        Assert.True(panel.IsWorkbenchDefault);
    }
}
