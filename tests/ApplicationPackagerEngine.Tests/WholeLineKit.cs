using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridRow = System.Windows.Controls.DataGridRow;
using Orientation = System.Windows.Controls.Orientation;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using TextBox = System.Windows.Controls.TextBox;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Finds text lines and grid rows that show in part: each line or row is compared with the visible area of its box,
/// both taken from the layout, so the check holds for every font, row style and display scale.
/// </summary>
internal static class WholeLineKit
{
    /// <summary>The bounds of every text line of the box, in the coordinates of the box.</summary>
    public static IReadOnlyList<Rect> LineBounds(TextBox box)
    {
        var lines = new List<Rect>();
        for (var i = 0; i < box.LineCount; i++)
        {
            var start = box.GetCharacterIndexFromLineIndex(i);
            var bounds = box.GetRectFromCharacterIndex(start);
            Assert.False(bounds.IsEmpty, $"Line {i} of {AutomationId(box)} has no bounds.");
            lines.Add(bounds);
        }
        return lines;
    }

    /// <summary>The visible text area of the box, in the coordinates of the box.</summary>
    public static Rect TextView(TextBox box)
    {
        var view = First<ScrollContentPresenter>(box);
        return view.TransformToAncestor(box).TransformBounds(new Rect(view.RenderSize));
    }

    /// <summary>The bounds of every realized row of the grid and the visible row area, in the coordinates of the grid.</summary>
    public static (IReadOnlyList<Rect> Rows, Rect View) RowBounds(DataGrid grid)
    {
        var view = First<ScrollContentPresenter>(grid);
        var rows = Descendants(grid).OfType<DataGridRow>().Where(r => r.IsVisible)
            .Select(r => r.TransformToAncestor(grid).TransformBounds(new Rect(r.RenderSize))).ToList();
        return (rows, view.TransformToAncestor(grid).TransformBounds(new Rect(view.RenderSize)));
    }

    /// <summary>The indexes of the lines that show in part: more than one device pixel of the line is inside the view and more than one is outside.</summary>
    public static IReadOnlyList<int> CutLines(IReadOnlyList<Rect> lines, Rect view, Visual owner)
    {
        var pixel = 1 / VisualTreeHelper.GetDpi(owner).DpiScaleY;
        var cut = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var inside = Math.Min(line.Bottom, view.Bottom) - Math.Max(line.Top, view.Top);
            if (inside > pixel && inside < line.Height - pixel) cut.Add(i);
        }
        return cut;
    }

    /// <summary>The count of lines that show in full.</summary>
    public static int WholeLines(IReadOnlyList<Rect> lines, Rect view, Visual owner)
    {
        var pixel = 1 / VisualTreeHelper.GetDpi(owner).DpiScaleY;
        return lines.Count(l => l.Top >= view.Top - pixel && l.Bottom <= view.Bottom + pixel);
    }

    /// <summary>The vertical scroll bar of the view, when its template holds one.</summary>
    public static ScrollBar? VerticalBar(DependencyObject view) =>
        Descendants(view).OfType<ScrollBar>().FirstOrDefault(b => b.Orientation == Orientation.Vertical);

    /// <summary>The vertical scroll bar shows and is drawn.</summary>
    public static bool BarShows(DependencyObject view) =>
        VerticalBar(view) is { IsVisible: true, ActualHeight: > 0, Opacity: > 0.99 } bar
        && Descendants(bar).OfType<Thumb>().Any(t => t.IsVisible && t.ActualHeight > 0 && t.Opacity > 0.99);

    /// <summary>
    /// No line of the box shows in part. When every line shows in full the box has no scroll bar; when a line is
    /// outside the view the scroll bar shows. Returns the count of whole lines in view.
    /// </summary>
    public static int AssertWholeLines(TextBox box, string where)
    {
        Assert.True(box.IsVisible, $"{where}: {AutomationId(box)} is not visible.");
        var lines = LineBounds(box);
        var view = TextView(box);
        var cut = CutLines(lines, view, box);
        Assert.True(cut.Count == 0, $"{where}: {AutomationId(box)} cuts line {string.Join(", ", cut)} of {lines.Count}: view {view.Top:0.0} to {view.Bottom:0.0}, line {Describe(lines, cut)}.");
        var whole = WholeLines(lines, view, box);
        var bar = BarShows(box);
        if (whole == lines.Count) Assert.False(bar, $"{where}: {AutomationId(box)} shows all {lines.Count} lines and a scroll bar.");
        else Assert.True(bar, $"{where}: {AutomationId(box)} shows {whole} of {lines.Count} lines and no scroll bar.");
        return whole;
    }

    /// <summary>
    /// No realized row of the grid shows in part. When every row shows in full the grid has no vertical scroll bar;
    /// when a row is outside the view the scroll bar shows. Returns the count of whole rows in view.
    /// </summary>
    public static int AssertWholeRows(DataGrid grid, string where)
    {
        Assert.True(grid.IsVisible, $"{where}: {AutomationId(grid)} is not visible.");
        var (rows, view) = RowBounds(grid);
        var cut = CutLines(rows, view, grid);
        Assert.True(cut.Count == 0, $"{where}: {AutomationId(grid)} cuts row {string.Join(", ", cut)} of {rows.Count}: view {view.Top:0.0} to {view.Bottom:0.0}, row {Describe(rows, cut)}.");
        var whole = WholeLines(rows, view, grid);
        var bar = BarShows(grid);
        if (whole == grid.Items.Count) Assert.False(bar, $"{where}: {AutomationId(grid)} shows all {grid.Items.Count} rows and a scroll bar.");
        else Assert.True(bar, $"{where}: {AutomationId(grid)} shows {whole} of {grid.Items.Count} rows and no scroll bar.");
        return whole;
    }

    private static string Describe(IReadOnlyList<Rect> lines, IReadOnlyList<int> cut) =>
        string.Join("; ", cut.Select(i => $"{i} {lines[i].Top:0.0} to {lines[i].Bottom:0.0}"));

    private static string AutomationId(FrameworkElement element) =>
        System.Windows.Automation.AutomationProperties.GetAutomationId(element) is { Length: > 0 } id ? id : element.Name;

    private static T First<T>(DependencyObject root) where T : DependencyObject =>
        Descendants(root).OfType<T>().FirstOrDefault() ?? throw new InvalidOperationException($"The template holds no {typeof(T).Name}.");

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}
