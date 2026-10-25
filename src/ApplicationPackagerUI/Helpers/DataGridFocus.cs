using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerUI.Helpers;

/// <summary>Puts the keyboard focus on the first cell of a grid, so a window opens with the focus on a control.</summary>
public static class DataGridFocus
{
    /// <summary>True when the first cell of the first row took the focus; false for a hidden or empty grid.</summary>
    public static bool FocusFirstCell(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        return grid.Items.Count > 0 && FocusItemCell(grid, grid.Items[0]);
    }

    /// <summary>True when the first cell of the row of <paramref name="item"/> took the focus; false for a hidden grid or an item it does not hold.</summary>
    public static bool FocusItemCell(DataGrid grid, object item)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (!grid.IsVisible || grid.Columns.Count == 0 || !grid.Items.Contains(item)) return false;
        grid.UpdateLayout();
        grid.ScrollIntoView(item);
        grid.UpdateLayout();
        if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row) return false;
        var presenter = Find<DataGridCellsPresenter>(row);
        if (presenter?.ItemContainerGenerator.ContainerFromIndex(0) is not DataGridCell cell) return false;
        return cell.Focus();
    }

    /// <summary>The first visual descendant of the type, depth first; null when the template holds none.</summary>
    public static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (Find<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}
