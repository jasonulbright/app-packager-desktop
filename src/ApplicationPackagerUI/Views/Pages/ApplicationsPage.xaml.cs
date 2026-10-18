using System.Windows;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.Views.Pages;

public partial class ApplicationsPage
{
    public ApplicationsPage(ApplicationsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private void VendorCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox cb && cb.Tag is VendorGroup group)
        {
            // The click has already toggled IsChecked, so IsChecked holds the new state.
            var newState = cb.IsChecked == true;
            foreach (var app in group.Apps)
                app.IsVisible = newState;
            group.UpdateGroupState();

            var vm = (ApplicationsViewModel)DataContext!;
            vm.UpdateCountsPublic();
        }
    }
}
