using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests.Compliance;

/// <summary>The Undo of a catalog deploy whose objects ConfigMgr does not show yet stays offered and names no object count.</summary>
public sealed class ComplianceCatalogUndoTests
{
    public const string NothingFound = "ConfigMgr holds no object from the cancelled run now. Check ConfigMgr, and try Undo again when an object appears.";

    [Fact]
    public void TheUndoOfARunWithoutKnownObjectsNamesNoCount()
    {
        var harness = new ComplianceHarness();
        var undo = ComplianceRequests.Deploy(harness.Autorun, new Dictionary<string, string>(), ["LAB00010"], false) with
        {
            Kind = ComplianceChangeKind.RemoveCatalogObjects, CreatedIds = []
        };
        var text = ComplianceShared.UndoDetail(null, undo);
        Assert.DoesNotContain("0 objects", text);
        Assert.Contains("by their names", text);
        Assert.Contains("(2 objects)", ComplianceShared.UndoDetail(null, undo with { CreatedIds = ["Baseline:1", "ConfigurationItem:2"] }));
    }

    [Fact]
    public async Task UndoLastChangeStaysOfferedAfterANothingFoundRefusal()
    {
        var harness = new ComplianceHarness();
        var viewModel = await harness.OpenAsync();
        viewModel.Catalog.SelectedRow = viewModel.Catalog.Rows.Single(r => r.Entry.Id == "autorun-disable");
        harness.Dialogs.Deploy = f =>
        {
            f.Field("CollectionName")!.Value = "Servers";
            FakeComplianceDialogs.Run(f.DeployCommand.ExecuteAsync(null));
        };
        await viewModel.Catalog.DeployCommand.ExecuteAsync(null);
        Assert.True(viewModel.Shared.HasUndo);

        harness.Service.Fail = c => c.Kind == ComplianceChangeKind.RemoveCatalogObjects ? NothingFound : null;
        await viewModel.Shared.UndoLastCommand.ExecuteAsync(null);
        Assert.Contains(viewModel.Shared.LastResults, r => r.Message.Contains(NothingFound));
        Assert.True(viewModel.Shared.HasUndo);
        Assert.True(viewModel.Shared.UndoLastCommand.CanExecute(null));

        harness.Service.Fail = null;
        await viewModel.Shared.UndoLastCommand.ExecuteAsync(null);
        Assert.False(viewModel.Shared.HasUndo);
    }
}
