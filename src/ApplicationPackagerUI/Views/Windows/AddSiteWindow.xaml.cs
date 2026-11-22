using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class AddSiteWindow
{
    public AddSiteViewModel ViewModel { get; }

    public AddSiteWindow(AddSiteViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += (_, _) => txtSiteCode.Focus();
    }
}
