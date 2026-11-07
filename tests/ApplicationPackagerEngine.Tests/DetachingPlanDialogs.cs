using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Confirms the plan, asks the run to stop and force closes the window while the run still works.</summary>
internal sealed class DetachingPlanDialogs(Func<bool> started) : IChangePlanDialogs
{
    public ChangePlanViewModel? Dialog;

    /// <summary>Answers the first plan itself, so a test can leave an earlier run behind before the one that detaches.</summary>
    public IChangePlanDialogs? Before;

    public ChangePlanOutcome Show(ChangePlan plan, ChangePlanRun run, ChangePlanRun? undo = null)
    {
        if (Before is { } first)
        {
            Before = null;
            return first.Show(plan, run, undo);
        }
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
