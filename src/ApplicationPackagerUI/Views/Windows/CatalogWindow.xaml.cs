using System.Windows;
using System.Windows.Input;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

/// <summary>Shows and hides applications in the App Packager grid; opened by the Catalog button.</summary>
public partial class CatalogWindow
{
    public CatalogWindow(CatalogViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => Keyboard.Focus(txtCatalogSearch);
    }

    public CatalogViewModel ViewModel => (CatalogViewModel)DataContext;

    private void VendorCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox cb && cb.Tag is VendorGroup group)
        {
            // The click has already toggled IsChecked, so IsChecked holds the new state.
            var newState = cb.IsChecked == true;
            foreach (var app in group.Apps)
                app.IsVisible = newState;
            group.UpdateGroupState();
            ViewModel.UpdateCountsPublic();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
