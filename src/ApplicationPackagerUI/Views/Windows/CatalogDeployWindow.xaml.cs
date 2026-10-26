using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class CatalogDeployWindow
{
    private readonly CatalogDeployViewModel _viewModel;

    public CatalogDeployWindow(CatalogDeployViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += (_, _) => MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    // A close during the plan would drop the form while its run still works.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsBusy) e.Cancel = true;
        base.OnClosing(e);
    }
}
