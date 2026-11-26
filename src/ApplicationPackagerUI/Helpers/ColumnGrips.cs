using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridColumnHeader = System.Windows.Controls.Primitives.DataGridColumnHeader;

namespace ApplicationPackagerUI.Helpers;

/// <summary>The grips of the column headers of a data grid: a drag sizes a column, a double click fits it to its content.</summary>
public static class ColumnGrips
{
    /// <summary>The column that a grip of a column header sizes; null for any other element, such as the thumb of a scroll bar.</summary>
    public static DataGridColumn? SizedColumn(DataGrid grid, object? source)
    {
        // A drag event comes from the grip itself, a mouse button event from an element inside its template.
        if (Ancestor<Thumb>(source as DependencyObject) is not { } grip || Ancestor<DataGridColumnHeader>(grip) is not { Column: { } column }) return null;
        // The left grip of a header sizes the column that stands before it.
        return grip.Name != "PART_LeftHeaderGripper" ? column
            : grid.Columns.Where(c => c.Visibility == Visibility.Visible && c.DisplayIndex < column.DisplayIndex)
                .OrderByDescending(c => c.DisplayIndex).FirstOrDefault() ?? column;
    }

    /// <summary>
    /// Call this on the second press of a double click, before WPF sets the column to Auto. After the layout it sets the width of the content as a
    /// pixel width, then calls <paramref name="fitted"/>. The caller lowers the MinWidth of the column first, so the column can become narrower.
    /// </summary>
    public static void AfterFit(DataGrid grid, DataGridColumn column, Action fitted) =>
        grid.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!column.Width.IsAuto) return;
            grid.UpdateLayout();
            // The desired width is the width of the content; a grid short of space shows an Auto column narrower than that.
            var width = double.IsFinite(column.Width.DesiredValue) ? column.Width.DesiredValue : column.ActualWidth;
            column.Width = new DataGridLength(Math.Clamp(width, column.MinWidth, column.MaxWidth));
            fitted();
        });

    private static T? Ancestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as T;
    }
}
