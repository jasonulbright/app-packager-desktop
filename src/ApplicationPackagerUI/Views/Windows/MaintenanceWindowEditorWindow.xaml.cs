using System.ComponentModel;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class MaintenanceWindowEditorWindow
{
    private readonly MaintenanceWindowEditorViewModel _viewModel;

    public MaintenanceWindowEditorWindow(MaintenanceWindowEditorViewModel viewModel)
    {
        _viewModel = viewModel;
        viewModel.FitTo(System.Windows.SystemParameters.WorkArea.Width, System.Windows.SystemParameters.WorkArea.Height);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
    }

    // Tab moves keyboard focus onto an item without selecting it, so a keyboard user would
    // see a focused collection that Save reports as not selected.
    private void OnCollectionItemFocused(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBoxItem { IsSelected: false } item) item.IsSelected = true;
    }

    // A close during the save would drop the editor while its plan still runs and lose the
    // message a failed write shows; the close is refused until the save ends.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsSaving) e.Cancel = true;
        base.OnClosing(e);
    }
}
