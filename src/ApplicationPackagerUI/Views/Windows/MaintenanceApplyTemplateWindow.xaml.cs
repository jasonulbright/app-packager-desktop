using System.ComponentModel;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class MaintenanceApplyTemplateWindow
{
    private readonly MaintenanceApplyTemplateViewModel _viewModel;

    public MaintenanceApplyTemplateWindow(MaintenanceApplyTemplateViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsApplying) e.Cancel = true;
        base.OnClosing(e);
    }
}
