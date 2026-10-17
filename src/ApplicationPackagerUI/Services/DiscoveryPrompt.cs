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
    public DiscoveryChoice Show(DiscoveryDialogViewModel viewModel)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        var window = new DiscoveryWindow(viewModel);
        if (owner is not null && owner.IsVisible) window.Owner = owner;
        window.ShowDialog();
        return viewModel.Choice;
    }
}
