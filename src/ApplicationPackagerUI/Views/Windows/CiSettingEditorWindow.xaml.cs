using System.ComponentModel;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class CiSettingEditorWindow
{
    private readonly CiSettingEditorViewModel _viewModel;

    public CiSettingEditorWindow(CiSettingEditorViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += (_, _) =>
        {
            if (viewModel.CanChooseKind) cboKind.Focus();
            else txtName.Focus();
        };
    }

    // A close during the plan would drop the dialog while its run still works.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsBusy) e.Cancel = true;
        base.OnClosing(e);
    }
}
