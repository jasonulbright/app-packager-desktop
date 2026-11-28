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
        // The detection editor and the result panel are shared with other pages; on this page they take the styles of the mock.
        Helpers.MockControlStyles.Apply(DetectionEditor, this);
        Helpers.MockControlStyles.Apply(ResultPanel, this);
    }

    private void SaveAndOpenWorkbench_Click(object sender, RoutedEventArgs e) =>
        ((AddAppViewModel)DataContext).SaveAndOpenWorkbenchCommand.Execute(null);
}
