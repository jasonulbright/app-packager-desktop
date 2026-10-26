using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerUI.Helpers;

/// <summary>
/// Selects the row under the pointer on a right-click. A right-click does not select a row by
/// itself, so a menu or a button would act on the row selected before.
/// </summary>
public static class DataGridRightClick
{
    public static readonly DependencyProperty SelectsRowProperty = DependencyProperty.RegisterAttached(
        "SelectsRow", typeof(bool), typeof(DataGridRightClick), new PropertyMetadata(false, OnSelectsRowChanged));

    public static bool GetSelectsRow(DependencyObject element) => (bool)element.GetValue(SelectsRowProperty);

    public static void SetSelectsRow(DependencyObject element, bool value) => element.SetValue(SelectsRowProperty, value);

    private static void OnSelectsRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;
        grid.PreviewMouseRightButtonDown -= OnPreviewMouseRightButtonDown;
        if (e.NewValue is true) grid.PreviewMouseRightButtonDown += OnPreviewMouseRightButtonDown;
    }

    private static void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        for (var element = e.OriginalSource as DependencyObject; element is not null && !ReferenceEquals(element, grid);
             element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is not DataGridRow row) continue;
            if (!row.IsSelected) grid.SelectedItem = row.Item;
            row.Focus();
            return;
        }
    }
}
