using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using DataGrid = System.Windows.Controls.DataGrid;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ApplicationPackagerUI.Helpers;

/// <summary>
/// Measures the widths of the Live grid columns with the font, cell padding and header template of the grid: a header
/// with its sort icon, a value with its cell padding, and the least width at which a typical name shows on the name lines.
/// </summary>
public sealed class LiveColumnFit
{
    /// <summary>A value keeps a tenth of its width as room, because a glyph from a fallback font can be wider than the measure.</summary>
    public const double ValueRoom = 1.1;

    /// <summary>The characters of the typical name that a name column shows in full at its least width.</summary>
    public const int TypicalNameChars = 40;

    /// <summary>The most lines a name cell shows; a longer name is trimmed and its tooltip shows it in full.</summary>
    public const int NameLines = 3;

    /// <summary>The typical name the least width of a name column holds: <see cref="TypicalNameChars"/> characters of mixed case and words.</summary>
    public const string TypicalName = "Contoso Line of Business Application for";

    /// <summary>
    /// The most distinct values of a column that are measured, longest first. Counts, times and codes have a near-constant
    /// width per character, so a shorter value of these is not wider than the values measured.
    /// </summary>
    private const int MeasuredValues = 64;

    private readonly DataGrid _grid;
    private readonly Typeface _cellFace;
    private readonly double _dip;
    private readonly Style? _headerStyle;
    private readonly Dictionary<string, double> _headerNeeds = new(StringComparer.Ordinal);

    public LiveColumnFit(DataGrid grid, Style valueStyle)
    {
        _grid = grid;
        _dip = VisualTreeHelper.GetDpi(grid).PixelsPerDip;
        _cellFace = new Typeface(grid.FontFamily, grid.FontStyle, FontWeights.Normal, grid.FontStretch);
        ValueStyle = valueStyle;
        var cell = new DataGridCell { Style = grid.CellStyle };
        var block = new TextBlock { Style = valueStyle };
        CellPadding = cell.Padding.Left + cell.Padding.Right + cell.BorderThickness.Left + cell.BorderThickness.Right + block.Margin.Left + block.Margin.Right;
        LineHeight = Math.Ceiling(grid.FontFamily.LineSpacing * grid.FontSize * _dip) / _dip;
        NameStyle = new Style(typeof(TextBlock), valueStyle)
        {
            Setters =
            {
                new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap),
                new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis),
                new Setter(FrameworkElement.MaxHeightProperty, NameLines * LineHeight),
                new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(TextBlock.Text)) { RelativeSource = RelativeSource.Self })
            }
        };
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding());
        label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        HeaderTemplate = new DataTemplate { VisualTree = label };
        HeaderTemplate.Seal();
        _headerStyle = grid.ColumnHeaderStyle ?? grid.TryFindResource(typeof(DataGridColumnHeader)) as Style;
        NameLeast = Math.Ceiling(WrappedWidth(TypicalName, NameLines) + CellPadding);
    }

    /// <summary>The cell text style of a value column: one line, trimmed, with the value as the tooltip.</summary>
    public Style ValueStyle { get; }

    /// <summary>The cell text style of a name column: up to <see cref="NameLines"/> lines, then trimmed, with the name as the tooltip.</summary>
    public Style NameStyle { get; }

    /// <summary>The header text of a column: it wraps at the spaces to <see cref="HeaderLines"/> lines when that makes the column narrower.</summary>
    public DataTemplate HeaderTemplate { get; }

    /// <summary>The least width of a column: the resize grippers at both edges of the header leave room for a few characters.</summary>
    public const double ReadableWidth = 60;

    /// <summary>The most lines of a header text.</summary>
    public const int HeaderLines = 2;

    /// <summary>The cell padding, cell border and text margin around a cell value.</summary>
    public double CellPadding { get; }

    /// <summary>The height of one text line of a cell.</summary>
    public double LineHeight { get; }

    /// <summary>The least width of a name column: <see cref="TypicalName"/> shows in full on <see cref="NameLines"/> lines.</summary>
    public double NameLeast { get; }

    /// <summary>
    /// The width a header needs: the sort icon, the header padding, and its text in the header font on at most
    /// <see cref="HeaderLines"/> lines, broken only between words.
    /// </summary>
    public double HeaderNeed(string header)
    {
        if (_headerNeeds.TryGetValue(header, out var need)) return need;
        // A header measured once keeps the size of its first content, so each header text gets its own probe.
        var probe = HeaderProbe(header);
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        need = Math.Ceiling(probe.DesiredSize.Width);
        if (Descendants(probe).OfType<TextBlock>().FirstOrDefault(t => t.Text == header) is { } text)
        {
            var face = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
            var line = Format(header, face, text.FontSize).WidthIncludingTrailingWhitespace;
            var word = header.Split(' ', StringSplitOptions.RemoveEmptyEntries).Max(w => Format(w, face, text.FontSize).WidthIncludingTrailingWhitespace);
            var lineHeight = text.FontFamily.LineSpacing * text.FontSize;
            var wrapped = Math.Max(word, WrappedWidth(header, HeaderLines, face, text.FontSize, lineHeight));
            // The binary search ends within half a pixel and layout rounds the header width, so the wrapped text keeps one pixel of room.
            need = Math.Ceiling(need - line + Math.Min(line, wrapped + 1));
        }
        need = Math.Max(need, ReadableWidth);
        _headerNeeds[header] = need;
        return need;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private DataGridColumnHeader HeaderProbe(string header)
    {
        var probe = new DataGridColumnHeader { Style = _headerStyle, FontFamily = _grid.FontFamily, FontSize = _grid.FontSize, Content = header, ContentTemplate = HeaderTemplate };
        probe.ApplyTemplate();
        // The template shows the sort icon only on a sorted column; the probe shows it, so a header holds its text when sorted.
        if (probe.Template?.FindName("SortIcon", probe) is UIElement icon) icon.Visibility = Visibility.Visible;
        return probe;
    }

    /// <summary>The width a value column needs for the widest of <paramref name="values"/>, with room and cell padding; 0 for no value.</summary>
    public double ValueNeed(IEnumerable<string> values)
    {
        var widest = Longest(values).Select(Width).DefaultIfEmpty(0).Max();
        return widest > 0 ? Math.Ceiling(widest * ValueRoom + CellPadding) : 0;
    }

    /// <summary>
    /// The share of the free width of a name column: the one-line width of its widest name, cut to
    /// <see cref="TypicalNameChars"/> characters, so one very long name does not take the width of the other name columns.
    /// </summary>
    public double NameWeight(IEnumerable<string> values)
    {
        var widest = Longest(values).Select(v => Width(v.Length > TypicalNameChars ? v[..TypicalNameChars] : v)).DefaultIfEmpty(0).Max();
        return Math.Max(NameLeast, Math.Ceiling(widest * ValueRoom + CellPadding));
    }

    /// <summary>
    /// The most width a name column takes for its whole names when the grid scrolls sideways; a name that needs more
    /// shows on <see cref="NameLines"/> lines, trimmed, with its tooltip.
    /// </summary>
    public const double NameCap = 360;

    /// <summary>
    /// The width at which the widest word of <paramref name="values"/> shows on one line, with room and cell padding;
    /// 0 for no value. A word is the text between two line break points, so a server name or an ID is one word.
    /// </summary>
    public double WordNeed(IEnumerable<string> values)
    {
        var widest = Longest(values).Select(v => Format(v).MinWidth).DefaultIfEmpty(0).Max();
        return widest > 0 ? Math.Ceiling(widest * ValueRoom + CellPadding) : 0;
    }

    /// <summary>
    /// The width at which every one of <paramref name="values"/> shows in full on at most <see cref="NameLines"/> lines,
    /// with no word broken inside, with room and cell padding, and at most <see cref="NameCap"/>; 0 for no value.
    /// The work is a binary search of line widths per measured value.
    /// </summary>
    public double WholeNameNeed(IEnumerable<string> values)
    {
        var widest = Longest(values).Select(v => Math.Max(Format(v).MinWidth, WrappedWidth(v, NameLines))).DefaultIfEmpty(0).Max();
        return widest > 0 ? Math.Min(NameCap, Math.Ceiling(widest * ValueRoom + CellPadding)) : 0;
    }

    /// <summary>The one-line width of <paramref name="text"/> in the cell font.</summary>
    public double Width(string text) => Format(text).WidthIncludingTrailingWhitespace;

    /// <summary>The least text width at which <paramref name="text"/> wraps to at most <paramref name="lines"/> lines in the cell font.</summary>
    public double WrappedWidth(string text, int lines) => WrappedWidth(text, lines, _cellFace, _grid.FontSize, LineHeight);

    private double WrappedWidth(string text, int lines, Typeface face, double size, double lineHeight)
    {
        var low = 1.0;
        var high = Math.Ceiling(Format(text, face, size).WidthIncludingTrailingWhitespace) + 1;
        while (high - low > 0.5)
        {
            var mid = (low + high) / 2;
            var formatted = Format(text, face, size);
            formatted.MaxTextWidth = mid;
            if (formatted.Height <= lines * lineHeight + 0.5 && formatted.Width <= mid + 0.5) high = mid;
            else low = mid;
        }
        return high;
    }

    private FormattedText Format(string text) => Format(text, _cellFace, _grid.FontSize);

    private FormattedText Format(string text, Typeface face, double size) =>
        new(text, CultureInfo.CurrentCulture, _grid.FlowDirection, face, size, Brushes.Black, _dip);

    /// <summary>The distinct values of the greatest lengths, at most <see cref="MeasuredValues"/>; the work is one pass over the values.</summary>
    private static IEnumerable<string> Longest(IEnumerable<string> values)
    {
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
            if (!string.IsNullOrEmpty(value)) distinct.Add(value);
        return distinct.OrderByDescending(v => v.Length).Take(MeasuredValues);
    }
}
