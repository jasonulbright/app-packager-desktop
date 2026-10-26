using System.ComponentModel;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class DeploymentTemplateEditorWindow
{
    private readonly DeploymentTemplateEditorViewModel _viewModel;

    public DeploymentTemplateEditorWindow(DeploymentTemplateEditorViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += (_, _) => txtName.Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsBusy) e.Cancel = true;
        base.OnClosing(e);
    }
}
