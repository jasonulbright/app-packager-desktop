using System.Windows;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.Views.Pages;

/// <summary>Add from web: the wizard that adds an application with a remote version source.</summary>
public partial class AddAppPage
{
    public AddAppPage(AddAppViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private void SaveAndOpenWorkbench_Click(object sender, RoutedEventArgs e) =>
        ((AddAppViewModel)DataContext).SaveAndOpenWorkbenchCommand.Execute(null);
}
