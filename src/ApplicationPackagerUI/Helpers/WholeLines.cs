using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridRow = System.Windows.Controls.DataGridRow;
using TextBox = System.Windows.Controls.TextBox;

namespace ApplicationPackagerUI.Helpers;

/// <summary>
/// Heights of read-only text boxes and grids that show whole lines and whole rows, from the line and row heights the
/// layout produced. The font metrics differ from the laid-out line height by a fraction of a pixel per line, and a row
/// takes its margin in addition to its own height, so a height made from the font metrics or the row height alone cuts
/// the last line or row.
/// </summary>
public static class WholeLines
{
    [ThreadStatic] private static HashSet<TextBox>? _pending;

    private static HashSet<TextBox> Pending => _pending ??= [];

    /// <summary>
    /// True when the box has a laid-out text. Before its text is laid out the box reports a line count of -1, and a
    /// height made then from the font metrics differs from the laid-out height.
    /// </summary>
    public static bool IsMeasured(TextBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        return box.IsVisible && box.ExtentHeight > 0 && box.LineCount > 0;
    }

    /// <summary>The height of one text line in the box: the laid-out text height over its line count, or the font metrics before the first layout.</summary>
    public static double LineHeight(TextBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        var lines = box.LineCount;
        return lines > 0 && box.ExtentHeight > 0 ? box.ExtentHeight / lines : box.FontFamily.LineSpacing * box.FontSize;
    }

    /// <summary>
    /// The height of the box outside its text area: the border, the padding and the template parts; the padding and
    /// border before the first layout. The text area comes from the arranged content presenter: the viewport height of
    /// the box can keep the value of an earlier layout, and a height made from it alternates between two values.
    /// </summary>
    public static double Chrome(TextBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        return DataGridFocus.Find<ScrollContentPresenter>(box) is { ActualHeight: > 0 } view && box.ActualHeight > view.ActualHeight
            ? box.ActualHeight - view.ActualHeight
            : box.Padding.Top + box.Padding.Bottom + box.BorderThickness.Top + box.BorderThickness.Bottom;
    }

    /// <summary>The count of whole lines the text area of the box has room for at its present height.</summary>
    public static int VisibleLines(TextBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        return (int)Math.Round((box.ActualHeight - Chrome(box)) / LineHeight(box));
    }

    /// <summary>The line count of the laid-out text, or 0 before the first layout.</summary>
    public static int LineCount(TextBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        return box.ExtentHeight > 0 ? Math.Max(0, box.LineCount) : 0;
    }

    /// <summary>
    /// Runs <paramref name="fit"/> now when the box is laid out, else once after the layout pass; a fit queued earlier
    /// and not run yet is not queued again.
    /// </summary>
    public static void WhenMeasured(TextBox box, Action fit)
    {
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(fit);
        if (IsMeasured(box))
        {
            fit();
            return;
        }
        if (!box.IsVisible || box.ExtentHeight <= 0 || Pending.Contains(box)) return;
        Pending.Add(box);
        box.Dispatcher.BeginInvoke(() =>
        {
            Pending.Remove(box);
            if (IsMeasured(box)) fit();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The height of the box that shows <paramref name="lines"/> whole lines. When the lines are all the lines of the text,
    /// the height holds the whole laid-out text, so no scroll bar shows.
    /// </summary>
    public static double BoxHeight(TextBox box, int lines)
    {
        ArgumentNullException.ThrowIfNull(box);
        var text = lines >= LineCount(box) && box.ExtentHeight > 0 ? box.ExtentHeight : lines * LineHeight(box);
        return UpToPixel(box, Chrome(box) + text);
    }

    /// <summary>The height of one row slot of the grid: the row and its margin.</summary>
    public static double RowHeight(DataGridRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.ActualHeight + row.Margin.Top + row.Margin.Bottom;
    }

    /// <summary>
    /// The height of the grid that shows <paramref name="rows"/> whole rows below its column header, from the tallest
    /// realized row; null before the first row is laid out.
    /// </summary>
    public static double? GridHeight(DataGrid grid, int rows)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (!grid.IsVisible || DataGridFocus.Find<ScrollContentPresenter>(grid) is not { ActualHeight: > 0 } view) return null;
        var slot = 0.0;
        for (var i = 0; i < grid.Items.Count; i++)
            if (grid.ItemContainerGenerator.ContainerFromIndex(i) is DataGridRow { ActualHeight: > 0 } row) slot = Math.Max(slot, RowHeight(row));
        if (slot <= 0) return null;
        // The column header, the borders and a sideways scroll bar take the height of the grid outside its rows.
        var chrome = grid.ActualHeight - view.ActualHeight;
        return UpToPixel(grid, chrome + rows * slot);
    }

    /// <summary>The height rounded up to whole device pixels, so layout rounding never takes a part of a pixel from the last line.</summary>
    public static double UpToPixel(Visual visual, double height)
    {
        ArgumentNullException.ThrowIfNull(visual);
        var scale = VisualTreeHelper.GetDpi(visual).DpiScaleY;
        return Math.Ceiling(height * scale - 0.01) / scale;
    }
}
