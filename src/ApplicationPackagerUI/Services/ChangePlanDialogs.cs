using System.Windows;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// Shows a change plan, runs the write operation only after the operator confirms it, and
/// reports the result in the same window. View models call this interface so tests run
/// without a window.
/// </summary>
public interface IChangePlanDialogs
{
    ChangePlanOutcome Show(ChangePlan plan, ChangePlanRun run, ChangePlanRun? undo = null);
}

public sealed class ChangePlanDialogs : IChangePlanDialogs
{
    public ChangePlanOutcome Show(ChangePlan plan, ChangePlanRun run, ChangePlanRun? undo = null)
    {
        var viewModel = new ChangePlanViewModel(plan, run, undo);
        var owner = DiscoveryPrompt.UsableOwner(Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow);
        var window = new ChangePlanWindow(viewModel);
        if (owner is not null) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
        return viewModel.Outcome;
    }
}
