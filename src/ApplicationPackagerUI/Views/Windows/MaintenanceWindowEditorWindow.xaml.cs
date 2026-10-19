using System.ComponentModel;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class MaintenanceWindowEditorWindow
{
    private readonly MaintenanceWindowEditorViewModel _viewModel;

    public MaintenanceWindowEditorWindow(MaintenanceWindowEditorViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
    }

    // A close during the save would drop the editor while its plan still runs and lose the
    // message a failed write shows; the close is refused until the save ends.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsSaving) e.Cancel = true;
        base.OnClosing(e);
    }
}
