using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class ImportSiteSettingsWindow
{
    public ImportSiteSettingsViewModel ViewModel { get; }

    public ImportSiteSettingsWindow(ImportSiteSettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += (_, _) => cmbSource.Focus();
    }
}
