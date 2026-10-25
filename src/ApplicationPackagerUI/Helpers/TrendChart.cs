using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ApplicationPackagerUI.ViewModels.Pages;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ApplicationPackagerUI.Helpers;

/// <summary>
/// A line chart of one metric drawn with shapes on a canvas: a value axis and a time axis with tick labels, a polyline per
/// run of points, and a marker on the selected point. A point after a gap starts a new polyline, so a gap shows as a break.
/// The arrow keys, Home, End and the pointer select a point; the table below the chart holds the same points.
/// </summary>
public sealed class TrendChart : Canvas
{
    public const double LeftAxis = 64;
    public const double BottomAxis = 24;
    public const double TopRoom = 10;
    public const double RightRoom = 16;
    public const double MarkerSize = 9;
    public const double PointSize = 5;

    /// <summary>The primary text brush: each theme keeps it readable on the card background, which the accent brushes do not promise for every system accent.</summary>
    public const string LineBrush = "TextFillColorPrimaryBrush";

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<TrendTableRow>), typeof(TrendChart),
        new FrameworkPropertyMetadata(Array.Empty<TrendTableRow>(), (d, _) => ((TrendChart)d).Rebuild()));

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(TrendChart),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((TrendChart)d).Rebuild()));

    public TrendChart()
    {
        Focusable = true;
        ClipToBounds = true;
        Background = Brushes.Transparent;
        SizeChanged += (_, _) => Rebuild();
        IsVisibleChanged += (_, _) => Rebuild();
    }

    public IReadOnlyList<TrendTableRow> Points
    {
        get => (IReadOnlyList<TrendTableRow>)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>The polylines of the last drawing; a test counts the breaks with them.</summary>
    public IReadOnlyList<Polyline> Lines => Children.OfType<Polyline>().ToList();

    /// <summary>The value range the axis shows: from zero or the least value, whichever is lower, to the largest value.</summary>
    public static (double Low, double High) Range(IReadOnlyList<TrendTableRow> points)
    {
        var values = points.Select(p => p.Value).Where(v => !double.IsNaN(v)).ToList();
        if (values.Count == 0) return (0, 1);
        var low = Math.Min(0, values.Min());
        var high = values.Max();
        return high - low < 1e-9 ? (low, low + 1) : (low, high);
    }

    /// <summary>The x position of each point, from the first to the last time; a single point is in the middle.</summary>
    public static double X(IReadOnlyList<TrendTableRow> points, int index, double left, double width)
    {
        if (points.Count < 2) return left + width / 2;
        var first = points[0].TimeUtc.Ticks;
        var span = points[^1].TimeUtc.Ticks - first;
        return span <= 0 ? left + width * index / (points.Count - 1) : left + width * (points[index].TimeUtc.Ticks - first) / span;
    }

    private void Rebuild()
    {
        Children.Clear();
        if (!IsVisible || ActualWidth < LeftAxis + RightRoom + 20 || ActualHeight < TopRoom + BottomAxis + 20) return;
        var points = Points ?? [];
        var left = LeftAxis;
        var top = TopRoom;
        var width = ActualWidth - LeftAxis - RightRoom;
        var height = ActualHeight - TopRoom - BottomAxis;
        var (low, high) = Range(points);
        double Y(double v) => top + height - (v - low) / (high - low) * height;

        Add(Axis(left, top, left, top + height));
        Add(Axis(left, top + height, left + width, top + height));
        foreach (var value in new[] { low, (low + high) / 2, high })
        {
            var y = Y(value);
            Add(Axis(left - 4, y, left, y));
            var label = Label(SiteHygieneLiveLabels.Value(Math.Round(value, 1)));
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            SetLeft(label, Math.Max(0, left - 8 - label.DesiredSize.Width));
            SetTop(label, Math.Clamp(y - label.DesiredSize.Height / 2, 0, ActualHeight - label.DesiredSize.Height));
            Add(label);
        }
        if (points.Count == 0) return;

        var span = points[^1].TimeUtc - points[0].TimeUtc;
        var format = span < TimeSpan.FromDays(2) ? "g" : "d";
        var ticks = points.Count == 1 ? new[] { 0 } : new[] { 0, points.Count - 1 };
        foreach (var index in ticks)
        {
            var x = X(points, index, left, width);
            Add(Axis(x, top + height, x, top + height + 4));
            var label = Label(points[index].TimeUtc.ToLocalTime().ToString(format, CultureInfo.CurrentCulture));
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var labelLeft = index == 0 && points.Count > 1 ? x : index == points.Count - 1 && points.Count > 1 ? x - label.DesiredSize.Width : x - label.DesiredSize.Width / 2;
            SetLeft(label, Math.Clamp(labelLeft, 0, Math.Max(0, ActualWidth - label.DesiredSize.Width)));
            SetTop(label, top + height + 5);
            Add(label);
        }

        var run = new PointCollection();
        void Flush()
        {
            if (run.Count == 1)
            {
                var dot = new Ellipse { Width = PointSize, Height = PointSize };
                dot.SetResourceReference(Shape.FillProperty, LineBrush);
                SetLeft(dot, run[0].X - PointSize / 2);
                SetTop(dot, run[0].Y - PointSize / 2);
                Add(dot);
            }
            else if (run.Count > 1)
            {
                var line = new Polyline { Points = run, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
                line.SetResourceReference(Shape.StrokeProperty, LineBrush);
                Add(line);
            }
            run = [];
        }
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].AfterGap) Flush();
            if (double.IsNaN(points[i].Value))
            {
                Flush();
                continue;
            }
            run.Add(new Point(X(points, i, left, width), Y(points[i].Value)));
        }
        Flush();

        if (SelectedIndex >= 0 && SelectedIndex < points.Count && !double.IsNaN(points[SelectedIndex].Value))
        {
            var x = X(points, SelectedIndex, left, width);
            var guide = Axis(x, top, x, top + height);
            guide.StrokeDashArray = [2, 2];
            Add(guide);
            var marker = new Ellipse { Width = MarkerSize, Height = MarkerSize, StrokeThickness = 2 };
            marker.SetResourceReference(Shape.FillProperty, "AccentFillColorDefaultBrush");
            marker.SetResourceReference(Shape.StrokeProperty, "TextFillColorPrimaryBrush");
            SetLeft(marker, x - MarkerSize / 2);
            SetTop(marker, Y(points[SelectedIndex].Value) - MarkerSize / 2);
            Add(marker);
        }
    }

    private void Add(UIElement element)
    {
        if (element is FrameworkElement f) f.IsHitTestVisible = false;
        Children.Add(element);
    }

    private static Line Axis(double x1, double y1, double x2, double y2)
    {
        var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = 1, SnapsToDevicePixels = true };
        line.SetResourceReference(Shape.StrokeProperty, "TextFillColorSecondaryBrush");
        return line;
    }

    private static TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 11 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return label;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SelectNearest(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        SelectNearest(e.GetPosition(this).X);
    }

    private void SelectNearest(double x)
    {
        var points = Points ?? [];
        if (points.Count == 0) return;
        var width = ActualWidth - LeftAxis - RightRoom;
        var best = 0;
        for (var i = 1; i < points.Count; i++)
            if (Math.Abs(X(points, i, LeftAxis, width) - x) < Math.Abs(X(points, best, LeftAxis, width) - x)) best = i;
        SelectedIndex = best;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var count = Points?.Count ?? 0;
        if (count == 0 || e.Handled) return;
        var index = e.Key switch
        {
            Key.Right or Key.Down => Math.Min(count - 1, Math.Max(0, SelectedIndex + 1)),
            Key.Left or Key.Up => Math.Max(0, SelectedIndex < 0 ? 0 : SelectedIndex - 1),
            Key.Home => 0,
            Key.End => count - 1,
            _ => (int?)null
        };
        if (index is not { } next) return;
        SelectedIndex = next;
        e.Handled = true;
    }
}
