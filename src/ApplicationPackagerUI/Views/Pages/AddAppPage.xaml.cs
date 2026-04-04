using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.Views.Pages;

public partial class AddAppPage
{
    public AddAppPage(AddAppViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
