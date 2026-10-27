using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ApplicationPackagerEngine.Tests.Compliance;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using DataGrid = System.Windows.Controls.DataGrid;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Checks of the page header, the controls and the keyboard order that every restyled page and window shares.</summary>
internal static class PageChromeKit
{
    /// <summary>WCAG 2 minimum contrast for normal text.</summary>
    public const double TextContrastMinimum = 4.5;

    public static Color ColorOf(object brush) => Assert.IsType<SolidColorBrush>(brush, exactMatch: false).Color;

    public static double TextWidth(TextBlock text) =>
        new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, System.Windows.Media.Brushes.Black,
            VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace;

    /// <summary>The title uses the theme's primary text brush and its glyphs reach the minimum contrast on the page background.</summary>
    public static void AssertTitle(Window window, TextBlock title, string where, Func<Window, FrameworkElement, string?, List<Color>>? pixels = null)
    {
        var primary = ColorOf(title.FindResource("TextFillColorPrimaryBrush"));
        Assert.True(primary == ColorOf(title.Foreground), $"{where}: the title is {ColorOf(title.Foreground)}, not the primary text color {primary}.");
        var background = ColorOf(title.FindResource("ApplicationBackgroundBrush"));
        var drawn = (pixels ?? RenderKit.Pixels)(window, title, null);
        var contrast = drawn.Max(p => RenderKit.Contrast(p, background));
        Assert.True(contrast >= TextContrastMinimum, $"{where}: the title has a contrast of {contrast:0.00} on {background}.");
        Assert.True(title.ActualWidth + 0.5 >= TextWidth(title), $"{where}: the title needs {TextWidth(title):0.0} px and has {title.ActualWidth:0.0} px.");
    }

    /// <summary>No visible button or check box is arranged narrower than its measured content, and no untrimmed one-line text is cut.</summary>
    public static void AssertNothingClipped(DependencyObject root, string where)
    {
        foreach (var element in RenderKit.Descendants(root).OfType<FrameworkElement>().Where(e => e.IsVisible && e is ButtonBase or System.Windows.Controls.CheckBox))
        {
            if (element.TemplatedParent is Expander or DataGridColumnHeader) continue;
            var name = AutomationProperties.GetAutomationId(element) is { Length: > 0 } id ? id : element.GetType().Name;
            var width = element.ActualWidth + element.Margin.Left + element.Margin.Right;
            Assert.True(width >= element.DesiredSize.Width - 0.5,
                $"{where}: {name} is {element.ActualWidth:0.0} px wide; it needs {element.DesiredSize.Width - element.Margin.Left - element.Margin.Right:0.0} px.");
        }
        foreach (var text in RenderKit.Descendants(root).OfType<TextBlock>().Where(t => t.IsVisible && t.TextTrimming == TextTrimming.None && t.TextWrapping == TextWrapping.NoWrap && t.Text.Length > 0))
        {
            var needed = TextWidth(text);
            var visible = LayoutInformation.GetLayoutClip(text) is { } clip ? clip.Bounds.Width : text.ActualWidth;
            Assert.True(visible >= needed - 0.5, $"{where}: the text '{text.Text}' shows {visible:0.0} px of {needed:0.0} px.");
        }
    }

    /// <summary>Every visible control the user can operate has an automation name.</summary>
    public static void AssertAutomationNames(DependencyObject root, string where)
    {
        foreach (var control in RenderKit.Descendants(root).OfType<Control>().Where(c => c.IsVisible && c.Focusable && c is ButtonBase or TextBox or ComboBox or DataGrid))
        {
            var peer = UIElementAutomationPeer.CreatePeerForElement(control);
            Assert.False(string.IsNullOrWhiteSpace(peer?.GetName()), $"{where}: a {control.GetType().Name} with id '{AutomationProperties.GetAutomationId(control)}' has no automation name.");
        }
    }

    /// <summary>The automation id, or the automation name, of the element with keyboard focus.</summary>
    public static string Focused() =>
        Keyboard.FocusedElement is DependencyObject d
            ? AutomationProperties.GetAutomationId(d) is { Length: > 0 } id ? id : AutomationProperties.GetName(d) is { Length: > 0 } name ? name : d.GetType().Name
            : "";

    /// <summary>Presses Tab up to <paramref name="limit"/> times from the focused element and returns each focused id.</summary>
    public static List<string> Tabs(int limit)
    {
        var order = new List<string>();
        for (var i = 0; i < limit && Keyboard.FocusedElement is UIElement current; i++)
        {
            current.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            StaTestHelper.Flush();
            order.Add(Focused());
        }
        return order;
    }
}
