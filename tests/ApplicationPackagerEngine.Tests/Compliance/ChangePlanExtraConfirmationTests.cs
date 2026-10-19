using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Compliance;

public sealed class ChangePlanExtraConfirmationTests
{
    private static ChangePlan Plan(string? extra, bool destructive = false) =>
        new("Enable remediation", "Enable remediation", [new ChangePlanItem("Enable", "Baseline A on Servers (LAB00010)", "Remediates.")], destructive)
        {
            ExtraConfirmation = extra
        };

    [Fact]
    public async Task APlanWithAnExtraConfirmationRunsOnlyAfterItsOwnCheckBox()
    {
        var runs = 0;
        var viewModel = new ChangePlanViewModel(Plan("I understand that remediation changes registry values on 3 devices."), (c, p, ct) =>
        {
            runs++;
            return Task.FromResult(new ChangePlanResult([]));
        });

        Assert.True(viewModel.HasExtraConfirmation);
        Assert.Equal("I understand that remediation changes registry values on 3 devices.", viewModel.ExtraConfirmationText);
        Assert.False(viewModel.ConfirmCommand.CanExecute(null));
        await viewModel.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(0, runs);

        viewModel.ExtraAcknowledged = true;
        Assert.True(viewModel.ConfirmCommand.CanExecute(null));
        await viewModel.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(1, runs);
    }

    [Fact]
    public void ADestructivePlanWithAnExtraConfirmationNeedsBothCheckBoxes()
    {
        var viewModel = new ChangePlanViewModel(Plan("Remediation text.", destructive: true), (c, p, ct) => Task.FromResult(new ChangePlanResult([])));
        viewModel.ExtraAcknowledged = true;
        Assert.False(viewModel.ConfirmCommand.CanExecute(null));
        viewModel.DestructiveAcknowledged = true;
        Assert.True(viewModel.ConfirmCommand.CanExecute(null));
        viewModel.ExtraAcknowledged = false;
        Assert.False(viewModel.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public void APlanWithoutAnExtraConfirmationShowsNoSecondCheckBox()
    {
        var viewModel = new ChangePlanViewModel(Plan(null), (c, p, ct) => Task.FromResult(new ChangePlanResult([])));
        Assert.False(viewModel.HasExtraConfirmation);
        Assert.True(viewModel.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheConfirmedPlanKeepsTheExtraConfirmationText()
    {
        ConfirmedChangePlan? confirmed = null;
        var viewModel = new ChangePlanViewModel(Plan("Remediation text."), (c, p, ct) =>
        {
            confirmed = c;
            return Task.FromResult(new ChangePlanResult([]));
        }) { ExtraAcknowledged = true };
        await viewModel.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal("Remediation text.", confirmed!.Plan.ExtraConfirmation);
    }

    [Fact]
    public async Task AnInterimStepShowsAsTheRunningTextAndIsNoResultRow()
    {
        var item = new ChangePlanItem("Deploy", "Entry", null);
        var plan = new ChangePlan("Deploy", "Deploy", [item]);
        var viewModel = new ChangePlanViewModel(plan, (c, p, ct) =>
        {
            p.Report(new ChangePlanItemResult(item, false, "Build: running the builder.") { Interim = true });
            p.Report(new ChangePlanItemResult(item, true, "Deployed."));
            return Task.FromResult(new ChangePlanResult([new ChangePlanItemResult(item, true, "Deployed.")]));
        });
        var steps = new List<string>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChangePlanViewModel.StepText) && viewModel.StepText.Length > 0) steps.Add(viewModel.StepText);
        };
        // The test runner's context runs posts on pool threads, where a step post can race the end of the run.
        await Task.Run(() => viewModel.ConfirmCommand.ExecuteAsync(null));
        Assert.Contains("Build: running the builder.", steps);
        Assert.Single(viewModel.ResultRows);
        Assert.Equal("", viewModel.StepText);
    }

    [Fact]
    public async Task ACancelKeepsOnlyTheFinalReportsAsRows()
    {
        var item = new ChangePlanItem("Deploy", "Entry", null);
        var plan = new ChangePlan("Deploy", "Deploy", [item]);
        var viewModel = new ChangePlanViewModel(plan, (c, p, ct) =>
        {
            p.Report(new ChangePlanItemResult(item, false, "Build: running the builder.") { Interim = true });
            throw new OperationCanceledException();
        });
        await viewModel.ConfirmCommand.ExecuteAsync(null);
        var row = Assert.Single(viewModel.ResultRows);
        Assert.Equal(ChangePlanViewModel.CancelledReason, row.Message);
    }
}
