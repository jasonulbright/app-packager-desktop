using System.Windows;
using Size = System.Windows.Size;

namespace ApplicationPackagerUI.Controls;

/// <summary>
/// A stack whose last child takes the height that is left, and at least <see cref="LeastFill"/>. A grid in the last child gets a height that
/// has an end, so it scrolls its own rows. Where the room is lower than <see cref="NeededHeight"/>, the owner puts the stack into a scroll
/// viewer and gives it the height of the viewport.
/// </summary>
public sealed class LeastFillPanel : System.Windows.Controls.Panel
{
    public static readonly DependencyProperty ViewportHeightProperty = DependencyProperty.Register(nameof(ViewportHeight), typeof(double),
        typeof(LeastFillPanel), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty LeastFillProperty = DependencyProperty.Register(nameof(LeastFill), typeof(double),
        typeof(LeastFillPanel), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The height that the scroll viewer around the panel shows; not read outside a scroll viewer.</summary>
    public double ViewportHeight { get => (double)GetValue(ViewportHeightProperty); set => SetValue(ViewportHeightProperty, value); }

    public double LeastFill { get => (double)GetValue(LeastFillProperty); set => SetValue(LeastFillProperty, value); }

    /// <summary>The height of the children with the last one at its least height.</summary>
    public double NeededHeight { get; private set; }

    /// <summary>Raised from a measure pass when <see cref="NeededHeight"/> changed; a handler must not change the layout at once.</summary>
    public event EventHandler? NeededHeightChanged;

    protected override Size MeasureOverride(Size availableSize)
    {
        double used = 0, width = 0, needed = 0;
        var room = double.IsInfinity(availableSize.Height) ? ViewportHeight : availableSize.Height;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var last = i == InternalChildren.Count - 1;
            var fill = Math.Max(LeastFill, room - used);
            if (last) needed = used + LeastFill;
            child.Measure(new Size(availableSize.Width, last ? fill : double.PositiveInfinity));
            used += last ? fill : child.DesiredSize.Height;
            width = Math.Max(width, child.DesiredSize.Width);
        }
        if (Math.Abs(needed - NeededHeight) > 0.5)
        {
            NeededHeight = needed;
            NeededHeightChanged?.Invoke(this, EventArgs.Empty);
        }
        return new Size(width, used);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double top = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var height = i == InternalChildren.Count - 1 ? Math.Max(0, finalSize.Height - top) : child.DesiredSize.Height;
            child.Arrange(new Rect(0, top, finalSize.Width, height));
            top += height;
        }
        return finalSize;
    }
}
