using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests.Collections;

/// <summary>The WQL Editor's message line: refusals from the guard before the provider, and no stale refusal after a write.</summary>
public sealed class CollectionWqlEditorMessageTests
{
    private static async Task<(CollectionComplianceViewModel ViewModel, CollectionComplianceHarness Harness)> EditorAsync()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        viewModel.WqlCollection = viewModel.Rows.Single(r => r.CollectionId == "LAB00010");
        await viewModel.WqlRulesLoad;
        return (viewModel, harness);
    }

    [Theory]
    [InlineData("select * from SMS_R_System -- every device")]
    [InlineData("select * from SMS_R_System /* every device */")]
    [InlineData("select * from SMS_R_System\r\nselect * from SMS_R_User")]
    public async Task ACommentOrASecondSelectIsRefusedByThePageWithTheGuardMessage(string wql)
    {
        var (viewModel, harness) = await EditorAsync();
        var expected = WqlGuard.Check(wql);
        Assert.NotNull(expected);
        viewModel.WqlText = wql;
        viewModel.WqlRuleName = "Every device";

        await viewModel.ValidateWqlCommand.ExecuteAsync(null);
        Assert.Equal(expected, viewModel.WqlMessage);
        viewModel.WqlMessage = "";
        await viewModel.PreviewWqlCommand.ExecuteAsync(null);
        Assert.Equal(expected, viewModel.WqlMessage);
        viewModel.WqlMessage = "";
        await viewModel.AddRuleCommand.ExecuteAsync(null);
        Assert.Equal(expected, viewModel.WqlMessage);
        viewModel.SelectedWqlRule = viewModel.WqlRules.Single();
        viewModel.WqlText = wql;
        viewModel.WqlMessage = "";
        await viewModel.UpdateRuleCommand.ExecuteAsync(null);
        Assert.Equal(expected, viewModel.WqlMessage);

        Assert.Equal((0, 0, 0), (harness.Service.ValidateCalls, harness.Service.PreviewCalls, harness.Service.PrepareCalls));
        Assert.Empty(harness.Plans.Shown);
    }

    [Fact]
    public async Task ASuccessfulUpdateClearsAnEarlierRefusal()
    {
        var (viewModel, harness) = await EditorAsync();
        viewModel.SelectedWqlRule = viewModel.WqlRules.Single();
        viewModel.WqlRuleName = "All servers";
        viewModel.WqlText = "select * from SMS_R_System; delete";
        await viewModel.UpdateRuleCommand.ExecuteAsync(null);
        Assert.NotEqual("", viewModel.WqlMessage);

        viewModel.WqlText = CollectionComplianceHarness.ServersWql + " and SMS_R_System.Client = 1";
        await viewModel.UpdateRuleCommand.ExecuteAsync(null);

        Assert.True(harness.Plans.LastOutcome!.Result!.Items.Single().Succeeded);
        Assert.Equal("", viewModel.WqlMessage);
    }

    [Fact]
    public async Task ASuccessfulAddClearsAnEarlierRefusal()
    {
        var (viewModel, harness) = await EditorAsync();
        viewModel.WqlRuleName = "";
        viewModel.WqlText = "select * from SMS_R_System";
        await viewModel.AddRuleCommand.ExecuteAsync(null);
        Assert.Equal(CollectionComplianceViewModel.RuleNameRequired, viewModel.WqlMessage);

        viewModel.WqlRuleName = "Every device";
        await viewModel.AddRuleCommand.ExecuteAsync(null);

        Assert.True(harness.Plans.LastOutcome!.Result!.Items.Single().Succeeded);
        Assert.Equal("", viewModel.WqlMessage);
    }
}
