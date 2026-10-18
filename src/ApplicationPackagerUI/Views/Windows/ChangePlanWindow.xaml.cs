using System.ComponentModel;
using ApplicationPackagerUI.ViewModels.Windows;
using Wpf.Ui.Controls;

namespace ApplicationPackagerUI.Views.Windows;

public partial class ChangePlanWindow
{
    private readonly ChangePlanViewModel _viewModel;

    public ChangePlanWindow(ChangePlanViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        btnConfirm.Appearance = viewModel.IsDestructive ? ControlAppearance.Danger : ControlAppearance.Primary;
        viewModel.CloseRequested += (_, _) => Close();
    }

    // Closing the window while a run is in progress would hide its result; the close turns
    // into a cancel request and the window stays open until the run reports or the operator
    // chooses to close without waiting.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_viewModel.CanClose)
        {
            e.Cancel = true;
            _viewModel.RequestCancel();
        }
        base.OnClosing(e);
    }
}
