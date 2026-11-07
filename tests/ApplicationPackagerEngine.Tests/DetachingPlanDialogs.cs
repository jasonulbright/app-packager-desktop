using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Confirms the plan, asks the run to stop and force closes the window while the run still works.</summary>
internal sealed class DetachingPlanDialogs(Func<bool> started) : IChangePlanDialogs
{
    public ChangePlanViewModel? Dialog;

    public ChangePlanOutcome Show(ChangePlan plan, ChangePlanRun run, ChangePlanRun? undo = null)
    {
        var dialog = new ChangePlanViewModel(plan, run, undo, TimeSpan.Zero) { DestructiveAcknowledged = true, ExtraAcknowledged = true };
        Dialog = dialog;
        _ = dialog.ConfirmCommand.ExecuteAsync(null);
        Assert.True(SpinWait.SpinUntil(started, TimeSpan.FromSeconds(10)));
        dialog.RequestCancel();
        Assert.True(SpinWait.SpinUntil(() => dialog.ShowForceClose, TimeSpan.FromSeconds(10)));
        dialog.ForceCloseCommand.Execute(null);
        return dialog.Outcome;
    }
}
