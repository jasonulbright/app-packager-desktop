using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.Collections;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using static ApplicationPackagerEngine.Tests.Compliance.StubComplianceSite;

namespace ApplicationPackagerEngine.Tests.Compliance;

/// <summary>
/// The page writer against the real service and its scripts on the stub site: the second
/// confirmation of a plan reaches every change that turns remediation on or restores it.
/// </summary>
public sealed class ComplianceWriterServiceTests
{
    private const string RemediatingId = "16777271";
    private const long Remediating = 16777271;

    private static StubComplianceSite Site() => new(NewState(
        items: [Ci("16780001", "Lab CI", 4, RegistrySetting("RegSetting_a", "EnableMulticast"))],
        baselines: [CiBaseline("16780002", "Lab Baseline", "Disables LLMNR.", "16780001"), CiBaseline("16780003", "Hand made", "No tag here")],
        assignments:
        [
            Deployment(16777270, "16780002", "MCM00080"),
            Deployment(Remediating, "16780003", "MCM00081", enforcement: true)
        ]));

    private static ComplianceWriter Writer(ComplianceService service) => new(service, new RecordingSiteHistory(), new RecordingMaintenanceLog(), () => "MCM");

    private static async Task<BaselineDeploymentRow> Row(ComplianceService service, string id) =>
        (await service.ListDeploymentsAsync(null, CancellationToken.None)).Single(d => d.DeploymentId == id);

    private static ComplianceChange Request(ComplianceChangeKind kind, BaselineDeploymentRow row, bool? remediate = null, string? to = null) =>
        new(kind, null, new Dictionary<string, string>(), [], row, remediate, to, null, null, null, [], []);

    /// <summary>A plan as the page builds it: the second check box when a change turns remediation on or keeps it on.</summary>
    private static CompliancePlan Plan(IReadOnlyList<ComplianceChange> prepared, bool hasUndo = true) =>
        new("Test", "Test plan", "Apply", prepared.Select(c => CompliancePlanEntry.Write(new ChangePlanItem(c.Kind.ToString(), c.DeploymentBefore?.DeploymentId ?? c.CatalogId ?? "", null), c)),
            isDestructive: true, hasUndo: hasUndo,
            extraConfirmation: prepared.Any(ComplianceWriter.NeedsRemediationConfirmation) ? "I understand that remediation changes registry values." : null);

    private static async Task<(ChangePlanViewModel Dialog, CompliancePlan Plan)> RunAsync(ComplianceService service, ComplianceChange request, bool hasUndo = true)
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var plan = Plan([await service.PrepareAsync(request, CancellationToken.None)], hasUndo);
        var writer = Writer(service);
        var dialog = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan), hasUndo ? writer.UndoFor(plan) : null) { DestructiveAcknowledged = true, ExtraAcknowledged = true };
        await dialog.ConfirmCommand.ExecuteAsync(null);
        return (dialog, plan);
    }

    private static void AssertChanged(ChangePlanViewModel dialog)
    {
        var row = Assert.Single(dialog.ResultRows);
        Assert.True(row.Succeeded, row.Message);
    }

    [WindowFact]
    public async Task EnableRemediationReachesConfigMgrWithTheSecondConfirmation()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site);
        var (dialog, _) = await RunAsync(service, Request(ComplianceChangeKind.SetRemediation, await Row(service, "16777270"), remediate: true));
        AssertChanged(dialog);
        Assert.True((bool)site.Assignment(16777270)!["Enforcement"]!);
    }

    [WindowFact]
    public async Task RedeployOfARemediatingDeploymentKeepsRemediation()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site);
        var (dialog, _) = await RunAsync(service, Request(ComplianceChangeKind.Redeploy, await Row(service, RemediatingId)), hasUndo: false);
        AssertChanged(dialog);
        Assert.Null(site.Assignment(Remediating));
        Assert.True((bool)site.Assignment(16777300)!["Enforcement"]!);
    }

    [WindowFact]
    public async Task ReassignOfARemediatingDeploymentAndItsUndoReachConfigMgr()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site);
        var (dialog, _) = await RunAsync(service, Request(ComplianceChangeKind.Reassign, await Row(service, RemediatingId), to: "MCM00080"));
        AssertChanged(dialog);
        Assert.Equal("MCM00080", (string)site.Assignment(16777300)!["CollectionID"]!);

        await dialog.UndoCommand.ExecuteAsync(null);
        AssertChanged(dialog);
        var back = site.Assignment(16777301)!;
        Assert.Equal(("MCM00081", true), ((string)back["CollectionID"]!, (bool)back["Enforcement"]!));
    }

    /// <summary>Shows the plan as the page does, with the check boxes the plan has, and presses Undo in the plan window.</summary>
    private static async Task<(ChangePlanOutcome Outcome, CompliancePlan Plan)> RunWithUndoAsync(ComplianceService service, ComplianceChange request)
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var plan = Plan([await service.PrepareAsync(request, CancellationToken.None)]);
        var writer = Writer(service);
        var dialogs = new ScriptedPlanDialogs { RunUndo = true };
        return (dialogs.Show(plan.Plan, writer.RunFor(plan), writer.UndoFor(plan)), plan);
    }

    private static void AssertUndoSkippedToThePage(ChangePlanOutcome outcome, CompliancePlan plan)
    {
        Assert.True(Assert.Single(outcome.Result!.Items).Succeeded);
        var undo = Assert.Single(outcome.UndoResult!.Items);
        Assert.True(undo.Skipped, undo.Message);
        Assert.Equal(ComplianceWriter.UndoNeedsPageText, undo.Message);
        Assert.NotNull(plan.Remaining(plan.UndoOutcomes));
    }

    [WindowFact]
    public async Task UndoInThePlanWindowOfDisableRemediationDoesNotTurnRemediationOnWithoutTheCheck()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site);
        var (outcome, plan) = await RunWithUndoAsync(service, Request(ComplianceChangeKind.SetRemediation, await Row(service, RemediatingId), remediate: false));
        Assert.Null(plan.Plan.ExtraConfirmation);
        AssertUndoSkippedToThePage(outcome, plan);
        Assert.False((bool)site.Assignment(Remediating)!["Enforcement"]!);
    }

    [WindowFact]
    public async Task UndoInThePlanWindowOfADeleteOfARemediatingDeploymentCreatesNothingWithoutTheCheck()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site);
        var (outcome, plan) = await RunWithUndoAsync(service, Request(ComplianceChangeKind.DeleteDeployment, await Row(service, RemediatingId)));
        Assert.Null(plan.Plan.ExtraConfirmation);
        AssertUndoSkippedToThePage(outcome, plan);
        Assert.Null(site.Assignment(Remediating));
        Assert.Null(site.Assignment(16777300));
    }

    [WindowFact]
    public async Task UndoInThePlanWindowOfAReassignWhosePlanHadTheCheckMovesTheRemediatingDeploymentBack()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site);
        var (outcome, plan) = await RunWithUndoAsync(service, Request(ComplianceChangeKind.Reassign, await Row(service, RemediatingId), to: "MCM00080"));
        Assert.NotNull(plan.Plan.ExtraConfirmation);
        var undo = Assert.Single(outcome.UndoResult!.Items);
        Assert.True(undo.Succeeded, undo.Message);
        var back = site.Assignment(16777301)!;
        Assert.Equal(("MCM00081", true), ((string)back["CollectionID"]!, (bool)back["Enforcement"]!));
    }

    [WindowFact]
    public async Task UndoInThePlanWindowOfDetectOnlyChangesNeedsNoCheck()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site);
        var (outcome, _) = await RunWithUndoAsync(service, Request(ComplianceChangeKind.DeleteDeployment, await Row(service, "16777270")));
        var undo = Assert.Single(outcome.UndoResult!.Items);
        Assert.True(undo.Succeeded, undo.Message);
        Assert.False((bool)site.Assignment(16777300)!["Enforcement"]!);
    }

    [WindowFact]
    public async Task TheUndoPlanOfThePageAsksTheSecondConfirmationAndPassesItOn()
    {
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site);
        var (_, plan) = await RunAsync(service, Request(ComplianceChangeKind.DeleteDeployment, await Row(service, RemediatingId)));
        var undo = plan.UndoChanges().Select(u => u.Undo).ToList();
        Assert.NotNull(ComplianceLiveViewModel.RemediationConfirmation(undo));

        var undoPlan = Plan(undo);
        var writer = Writer(service);
        var dialog = new ChangePlanViewModel(undoPlan.Plan, writer.RunFor(undoPlan), writer.UndoFor(undoPlan)) { DestructiveAcknowledged = true, ExtraAcknowledged = true };
        await dialog.ConfirmCommand.ExecuteAsync(null);
        AssertChanged(dialog);
        Assert.True((bool)site.Assignment(16777300)!["Enforcement"]!);
    }

    [WindowFact]
    public async Task ARemediatingDeployReachesTheBuilderWithTheSecondConfirmation()
    {
        var site = ComplianceCatalogDeployTests.Site();
        var builder = new ComplianceCatalogDeployTests.SiteBuilder(site);
        var service = ComplianceDeploymentTests.Service(site, builder);
        var request = new ComplianceChange(ComplianceChangeKind.CatalogDeploy, "llmnr-disable",
            new Dictionary<string, string> { ["CollectionName"] = "CCM Test 1", ["DetectOnly"] = "False" }, [], null, true, null, null, null, null, [], []);
        var (dialog, _) = await RunAsync(service, request);
        AssertChanged(dialog);
        Assert.Equal(1, builder.Runs);
        Assert.DoesNotContain("-DetectOnly", builder.Arguments!);
    }

    [WindowFact]
    public async Task APlanThatTurnsRemediationOnWithoutTheSecondCheckBoxDoesNotRun()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var site = Site();
        var service = ComplianceDeploymentTests.Service(site);
        var prepared = await service.PrepareAsync(Request(ComplianceChangeKind.SetRemediation, await Row(service, "16777270"), remediate: true), CancellationToken.None);
        var plan = new CompliancePlan("Test", "Test plan", "Apply", [CompliancePlanEntry.Write(new ChangePlanItem("Enable", "16777270", null), prepared)]);
        var writer = Writer(service);
        var dialog = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan), writer.UndoFor(plan));
        await dialog.ConfirmCommand.ExecuteAsync(null);

        Assert.DoesNotContain(dialog.ResultRows, r => r.Succeeded);
        Assert.NotEqual(ChangePlanViewModel.FailedText, dialog.ResultText);
        Assert.Equal(ComplianceWriter.NotAcknowledgedText, Assert.Single(dialog.ResultRows).Message);
        Assert.False((bool)site.Assignment(16777270)!["Enforcement"]!);
    }
}
