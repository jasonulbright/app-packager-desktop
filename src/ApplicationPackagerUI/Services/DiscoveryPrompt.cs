using System.Windows;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;

namespace ApplicationPackagerUI.Services;

/// <summary>Shows the Discover unmanaged software prompt and returns the operator's choice.</summary>
public interface IDiscoveryPrompt
{
    DiscoveryChoice Show(DiscoveryDialogViewModel viewModel);
}

public sealed class DiscoveryPrompt : IDiscoveryPrompt
{
    /// <summary>
    /// The owner for the prompt, or null to show it without one. A modal prompt owned by a
    /// minimized window is not on screen while the owner stays minimized, and the modal loop
    /// blocks the rest of the application; a minimized owner is restored first and dropped
    /// when it stays minimized.
    /// </summary>
    public static Window? UsableOwner(Window? candidate)
    {
        if (candidate is null || !candidate.IsVisible) return null;
        if (candidate.WindowState == WindowState.Minimized) candidate.WindowState = WindowState.Normal;
        return candidate.WindowState == WindowState.Minimized ? null : candidate;
    }

    public DiscoveryChoice Show(DiscoveryDialogViewModel viewModel)
    {
        var owner = UsableOwner(Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow);
        var window = new DiscoveryWindow(viewModel);
        if (owner is not null) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
        return viewModel.Choice;
    }
}
