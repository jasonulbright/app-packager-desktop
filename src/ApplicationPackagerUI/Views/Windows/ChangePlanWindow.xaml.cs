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
        // The text box of a plan body takes its own rows, so the window grows to keep the list of changes readable inside a 1366x768 work area.
        if (viewModel.HasBody) Height = Math.Min(BodyHeight, System.Windows.SystemParameters.WorkArea.Height);
        viewModel.CloseRequested += (_, _) => Close();
        ContentRendered += (_, _) => FocusFirstControl();
    }

    public const double BodyHeight = 700;

    /// <summary>Puts the focus on the first plan line, or on the first confirmation box or button when the plan has no line.</summary>
    public void FocusFirstControl()
    {
        if (Helpers.DataGridFocus.FocusFirstCell(gridPlan)) return;
        foreach (var control in new System.Windows.Controls.Control[] { chkDestructive, chkExtra, chkSecond, chkOption, btnConfirm, btnCancel })
            if (control.IsVisible && control.IsEnabled && control.Focus()) return;
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
