using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Compliance;

/// <summary>The plan window offers Undo for a result that changed ConfigMgr only in part, as its summary says.</summary>
public sealed class CompliancePartialUndoTests
{
    private static ChangePlan Plan() => new("Deploy", "Deploy", [new ChangePlanItem("Deploy", "Entry 1.0.0", null)], hasUndo: true);

    [Fact]
    public async Task AResultMadeOnlyInPartEnablesUndo()
    {
        var undone = 0;
        var vm = new ChangePlanViewModel(Plan(),
            (c, _, _) => Task.FromResult(new ChangePlanResult([new ChangePlanItemResult(c.Plan.Items[0], false, "Cancelled after the build started.") { PartialChange = true }])),
            (c, _, _) =>
            {
                undone++;
                return Task.FromResult(new ChangePlanResult([new ChangePlanItemResult(c.Plan.Items[0], true, "Removed.")]));
            });
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.EndsWith("Undo can reverse it.", vm.ResultText);
        Assert.True(vm.ShowUndo);
        Assert.True(vm.UndoCommand.CanExecute(null));
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(1, undone);
    }

    [Fact]
    public async Task AFailedResultWithoutAPartialChangeKeepsUndoDisabled()
    {
        var vm = new ChangePlanViewModel(Plan(),
            (c, _, _) => Task.FromResult(new ChangePlanResult([new ChangePlanItemResult(c.Plan.Items[0], false, "Failed.")])),
            (_, _, _) => throw new InvalidOperationException("Undo must not run."));
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task ACancelledCatalogDeployIsUndoneFromThePlanWindow()
    {
        var harness = new ComplianceHarness();
        harness.Service.InConfirmedRun = null;
        harness.Service.DeployGate = new TaskCompletionSource();
        var change = ComplianceRequests.Deploy(harness.Autorun, new Dictionary<string, string> { ["DetectOnly"] = "True" }, ["LAB00010"], false);
        var plan = new CompliancePlan("Catalog deploy", "Deploy Autorun Disable", "Deploy detect only",
            [CompliancePlanEntry.Write(new ChangePlanItem("Deploy", "Autorun Disable 1.0.0", null), change)]);
        var writer = harness.Writer();
        var vm = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan), writer.UndoFor(plan));

        var confirm = vm.ConfirmCommand.ExecuteAsync(null);
        while (vm.StepText.Length == 0) await Task.Delay(1);
        vm.RequestCancel();
        await confirm;
        Assert.Equal(1, vm.Outcome.Result!.PartialCount);
        Assert.EndsWith("Undo can reverse it.", vm.ResultText);
        Assert.True(vm.UndoCommand.CanExecute(null));

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(ComplianceChangeKind.RemoveCatalogObjects, harness.Service.Applied[^1].Single().Kind);
        Assert.Equal("Undo: 1 changed, 0 failed.", vm.ResultText);
    }

    private static (ComplianceHarness Harness, StubComplianceSite Site) RedeployOnStub(params string[] failures)
    {
        var site = new StubComplianceSite(StubComplianceSite.NewState(
            items: [StubComplianceSite.Ci("16780001", "Lab CI", 4, StubComplianceSite.RegistrySetting("RegSetting_a", "EnableMulticast"))],
            baselines: [StubComplianceSite.CiBaseline("16780002", "Lab Baseline", "", "16780001")],
            assignments: [StubComplianceSite.Deployment(16777270, "16780002", "MCM00080")]));
        site.Fail(failures);
        return (new ComplianceHarness { RealService = ComplianceDeploymentTests.Service(site) }, site);
    }

    private static async Task RedeployAll(ComplianceHarness harness)
    {
        var viewModel = await harness.OpenAsync(ComplianceViewModel.LiveTab);
        await viewModel.Live.LoadTask;
        viewModel.Live.SetSelectedDeployments(viewModel.Live.Deployments);
        await viewModel.Live.RedeployCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task ARedeployThatKeepsBothDeploymentsIsUndoneFromThePlanWindow()
    {
        var (harness, site) = RedeployOnStub("remove:16777270");
        harness.Plans.RunUndo = true;

        await RedeployAll(harness);

        var dialog = harness.Plans.LastViewModel!;
        Assert.Contains("Undo removes the new one.", Assert.Single(dialog.Outcome.Result!.Items).Message);
        Assert.True(dialog.ShowUndo);
        Assert.Equal("Undo: 1 changed, 0 failed.", dialog.ResultText);
        Assert.NotNull(site.Assignment(16777270));
        Assert.Null(site.Assignment(16777300));
    }

    [Fact]
    public async Task AFullRedeployShowsNoUndoInThePlanWindow()
    {
        var (harness, site) = RedeployOnStub();

        await RedeployAll(harness);

        var dialog = harness.Plans.LastViewModel!;
        Assert.True(Assert.Single(dialog.Outcome.Result!.Items).Succeeded);
        Assert.False(dialog.ShowUndo);
        Assert.False(dialog.UndoCommand.CanExecute(null));
        Assert.Null(site.Assignment(16777270));
        Assert.NotNull(site.Assignment(16777300));
    }
}
