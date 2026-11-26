using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridColumnHeader = System.Windows.Controls.Primitives.DataGridColumnHeader;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The events that the right grip of a column header raises for a drag and for a double click of the mouse.</summary>
internal static class ColumnGripKit
{
    private static Thumb Grip(Window window, DataGrid grid, DataGridColumn column)
    {
        grid.ScrollIntoView(grid.Items[0], column);
        Settle(window);
        var header = Descendants(grid).OfType<DataGridColumnHeader>().Single(h => ReferenceEquals(h.Column, column));
        return Descendants(header).OfType<Thumb>().Single(t => t.Name == "PART_RightHeaderGripper");
    }

    private static void PressAndRelease(Thumb grip, double change)
    {
        grip.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        if (change != 0) grip.RaiseEvent(new DragDeltaEventArgs(change, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        grip.RaiseEvent(new DragCompletedEventArgs(change, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
    }

    public static void Drag(Window window, DataGrid grid, DataGridColumn column, double change)
    {
        PressAndRelease(Grip(window, grid, column), change);
        Settle(window);
    }

    /// <summary>A click, then a second press with a click count of two, which starts no drag.</summary>
    public static void DoubleClick(Window window, DataGrid grid, DataGridColumn column)
    {
        var grip = Grip(window, grid, column);
        PressAndRelease(grip, 0);
        Settle(window);
        // The mouse hits an element inside the template of the grip.
        var target = Descendants(grip).OfType<UIElement>().LastOrDefault() ?? grip;
        foreach (var routed in new[] { Mouse.PreviewMouseDownEvent, Mouse.MouseDownEvent })
        {
            var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = routed };
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.GetSetMethod(true)!.Invoke(press, [2]);
            target.RaiseEvent(press);
        }
        Settle(window);
        Settle(window);
    }
}
