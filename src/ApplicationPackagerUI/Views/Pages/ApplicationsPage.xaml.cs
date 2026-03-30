using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.Views.Pages;

public partial class ApplicationsPage
{
    public ApplicationsPage(ApplicationsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
