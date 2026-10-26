using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerUI.Helpers;

/// <summary>
/// Makes a read-only grid one Tab stop whose focused row is the selected row. The grid needs
/// cells that are tab stops; the theme's cell style is not one, so Tab skips such a grid.
/// </summary>
public static class DataGridKeyboard
{
    public static readonly DependencyProperty SelectsFocusedRowProperty = DependencyProperty.RegisterAttached(
        "SelectsFocusedRow", typeof(bool), typeof(DataGridKeyboard), new PropertyMetadata(false, OnSelectsFocusedRowChanged));

    public static bool GetSelectsFocusedRow(DependencyObject element) => (bool)element.GetValue(SelectsFocusedRowProperty);

    public static void SetSelectsFocusedRow(DependencyObject element, bool value) => element.SetValue(SelectsFocusedRowProperty, value);

    private static readonly KeyboardFocusChangedEventHandler FocusHandler = OnGotKeyboardFocus;

    private static void OnSelectsFocusedRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;
        grid.RemoveHandler(UIElement.GotKeyboardFocusEvent, FocusHandler);
        if (e.NewValue is not true) return;
        KeyboardNavigation.SetTabNavigation(grid, KeyboardNavigationMode.Once);
        grid.AddHandler(UIElement.GotKeyboardFocusEvent, FocusHandler, true);
    }

    private static T? Ancestor<T>(DependencyObject? element, DependencyObject? stop = null) where T : DependencyObject
    {
        while (element is not null && element != stop)
        {
            if (element is T found) return found;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private static bool Inside(DataGrid grid, object? element) =>
        element is DependencyObject d && (d == grid || Ancestor<DataGrid>(d) == grid);

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (e.NewFocus is not DependencyObject focused || Ancestor<DataGridRow>(focused, grid) is not { } row) return;
        if (ItemsControl.ItemsControlFromItemContainer(row) != grid) return;

        // Tab from outside the grid focuses the first row; focus moves to the selected row so the selection stays.
        if (!Inside(grid, e.OldFocus) && !row.IsSelected && grid.SelectedItem is { } selected && FocusRow(grid, selected)) return;

        if (!row.IsSelected && (grid.SelectionMode == DataGridSelectionMode.Single || grid.SelectedItems.Count == 0)) row.IsSelected = true;
    }

    private static bool FocusRow(DataGrid grid, object item)
    {
        grid.ScrollIntoView(item);
        grid.UpdateLayout();
        if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row) return false;
        var cell = FirstCell(row);
        if (cell is null) return false;
        grid.CurrentCell = new DataGridCellInfo(cell);
        return cell.Focus();
    }

    private static DataGridCell? FirstCell(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is DataGridCell cell) return cell;
            if (FirstCell(child) is { } found) return found;
        }
        return null;
    }
}
