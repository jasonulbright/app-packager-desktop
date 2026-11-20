using System.Windows;
using System.Windows.Controls;
using TreeViewItem = System.Windows.Controls.TreeViewItem;
using Microsoft.Extensions.DependencyInjection;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.Views.Pages;

public partial class SettingsPage
{
    private readonly SettingsViewModel _viewModel = null!;

    /// <summary>The page the tree showed last, so a return to Settings in the same run opens where the user was.</summary>
    private static string s_lastPage = "general";

    private void CheckForUpdates_Click(object sender, RoutedEventArgs e) =>
        App.Services.GetRequiredService<Services.UpdateCoordinator>().ShowAndCheck(Window.GetWindow(this));

    public SettingsPage(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        SelectPage(s_lastPage);
        // The view model is built before the application list finishes loading, so the
        // One Click grid is filled when the page is shown and refilled when that list
        // changed size; an unchanged list keeps unsaved edits.
        Loaded += (_, _) =>
        {
            if (_viewModel.OneClickRows.Count != _viewModel.PackagerCount)
                _viewModel.RefreshOneClickRows();
            if (_viewModel.SupersedenceRows.Count == 0 || _viewModel.SupersedenceRows.Count > _viewModel.PackagerCount)
                _viewModel.RefreshSupersedenceRows();
        };
    }

    /// <summary>Shows one page of the Options tree and hides the others. An unknown id leaves the page as it is.</summary>
    public void SelectPage(string id)
    {
        if (FindName("page_" + id) is not FrameworkElement) return;
        s_lastPage = id;
        foreach (var node in Leaves(OptionsTree.Items))
            if ((string?)node.Tag == id) node.IsSelected = true;
        ShowOnly(id);
    }

    private void ShowOnly(string id)
    {
        foreach (var node in Leaves(OptionsTree.Items))
            if (node.Tag is string tag && FindName("page_" + tag) is FrameworkElement page)
                page.Visibility = tag == id ? Visibility.Visible : Visibility.Collapsed;
    }

    private static IEnumerable<TreeViewItem> Leaves(ItemCollection items)
    {
        foreach (var item in items.OfType<TreeViewItem>())
        {
            if (item.Tag is string) yield return item;
            foreach (var child in Leaves(item.Items)) yield return child;
        }
    }

    private void OptionsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem item) return;
        // A group has no page of its own: choosing it opens its first page.
        if (item.Tag is not string id)
        {
            if (item.Items.OfType<TreeViewItem>().FirstOrDefault() is { } first) first.IsSelected = true;
            return;
        }
        s_lastPage = id;
        ShowOnly(id);
    }
}
