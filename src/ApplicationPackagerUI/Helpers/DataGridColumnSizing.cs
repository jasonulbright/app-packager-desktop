using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerUI.Helpers;

/// <summary>
/// Turns the star-sized columns of a data grid into fixed widths when the user starts to resize a
/// column. A star column gives width to a widened column only down to its minimum, so with star
/// columns at their minimums a drag changes nothing; with fixed widths the drag widens the column
/// and the grid scrolls sideways.
/// </summary>
public static class DataGridColumnSizing
{
    public static readonly DependencyProperty FixStarWidthsOnResizeProperty = DependencyProperty.RegisterAttached(
        "FixStarWidthsOnResize", typeof(bool), typeof(DataGridColumnSizing), new PropertyMetadata(false, OnFixStarWidthsOnResizeChanged));

    public static bool GetFixStarWidthsOnResize(DependencyObject element) => (bool)element.GetValue(FixStarWidthsOnResizeProperty);

    public static void SetFixStarWidthsOnResize(DependencyObject element, bool value) => element.SetValue(FixStarWidthsOnResizeProperty, value);

    private static void OnFixStarWidthsOnResizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;
        grid.RemoveHandler(Thumb.DragStartedEvent, (DragStartedEventHandler)OnGripperDragStarted);
        grid.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        if (e.NewValue is not true) return;
        grid.AddHandler(Thumb.DragStartedEvent, (DragStartedEventHandler)OnGripperDragStarted, handledEventsToo: true);
        grid.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
    }

    private static void OnGripperDragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is DataGrid grid && IsHeaderGripper(e.OriginalSource as DependencyObject)) FixStarWidths(grid);
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid && IsHeaderGripper(e.OriginalSource as DependencyObject)) FixStarWidths(grid);
    }

    private static bool IsHeaderGripper(DependencyObject? source)
    {
        var thumb = false;
        for (var current = source; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is Thumb) thumb = true;
            if (current is DataGridColumnHeader) return thumb;
            if (current is DataGrid) return false;
        }
        return false;
    }

    public static void FixStarWidths(DataGrid grid)
    {
        foreach (var column in grid.Columns)
            if (column.Width.IsStar && column.ActualWidth > 0)
                column.Width = new DataGridLength(column.ActualWidth);
    }
}
