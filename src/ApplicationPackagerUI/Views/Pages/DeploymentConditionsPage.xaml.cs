using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.Views.Pages;

public partial class DeploymentConditionsPage
{
    public DeploymentConditionsPage(DeploymentConditionsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
