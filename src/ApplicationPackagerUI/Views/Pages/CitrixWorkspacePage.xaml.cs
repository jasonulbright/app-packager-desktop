using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.Views.Pages;

public partial class CitrixWorkspacePage
{
    public CitrixWorkspacePage(CitrixWorkspaceViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
