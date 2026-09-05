using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.Views.Pages;

public partial class SettingsPage
{
    private readonly SettingsViewModel _viewModel;

    public SettingsPage(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is built before the application list finishes loading, so the
        // One Click grid is filled when the page is shown and refilled when that list
        // changed size; an unchanged list keeps unsaved edits.
        Loaded += (_, _) =>
        {
            if (_viewModel.OneClickRows.Count != _viewModel.PackagerCount)
                _viewModel.RefreshOneClickRows();
        };
    }
}
