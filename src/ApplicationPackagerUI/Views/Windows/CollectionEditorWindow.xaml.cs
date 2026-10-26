using System.ComponentModel;
using System.Windows.Controls;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class CollectionEditorWindow
{
    private readonly CollectionEditorViewModel _viewModel;

    public CollectionEditorWindow(CollectionEditorViewModel viewModel)
    {
        _viewModel = viewModel;
        viewModel.FitTo(System.Windows.SystemParameters.WorkArea.Width, System.Windows.SystemParameters.WorkArea.Height);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += (_, _) =>
        {
            txtName.Focus();
            if (_viewModel.SelectedLimiting is { } selected) lstLimiting.ScrollIntoView(selected);
        };
    }

    // The list shows the filtered choices only; a filter that hides the selected collection
    // clears the list's selection but keeps the collection the dialog writes.
    private void OnLimitingSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is CollectionChoiceRow row) _viewModel.SelectedLimiting = row;
    }

    // Tab moves keyboard focus onto an item without selecting it, so a keyboard user would see a
    // focused collection that Save reports as not selected.
    private void OnLimitingItemFocused(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is ListBoxItem { IsSelected: false } item) item.IsSelected = true;
    }

    // A close during the save would drop the dialog while its plan still runs.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsSaving) e.Cancel = true;
        base.OnClosing(e);
    }
}
