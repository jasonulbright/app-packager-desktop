using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using ListBox = System.Windows.Controls.ListBox;
using Point = System.Windows.Point;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ApplicationPackagerEngine.Tests.Compliance;

/// <summary>Real-window checks for the Compliance section: themes, binding errors, row contrast, selection and column widths.</summary>
internal static class RenderKit
{
    public const double ReadableColumnWidth = 60;

    /// <summary>The tooltip style App.xaml merges after the control library.</summary>
    public const string ToolTipStyles = "pack://application:,,,/ApplicationPackagerUI;component/Views/Styles/ToolTips.xaml";

    private sealed class BindingErrors : TraceListener
    {
        public List<string> Lines { get; } = [];
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message is not null) Lines.Add(message);
        }
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    public static T ById<T>(DependencyObject root, string id) where T : DependencyObject =>
        Descendants(root).OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);

    /// <summary>Runs the test with the theme and control dictionaries merged as App.xaml merges them; a binding error fails the test.</summary>
    public static void WithApplication(ApplicationTheme theme, Action test)
    {
        StaTestHelper.Run(() =>
        {
            Assert.Null(Application.Current);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var errors = new BindingErrors();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            try
            {
                application.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = theme });
                application.Resources.MergedDictionaries.Add(new ControlsDictionary());
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(ToolTipStyles, UriKind.Absolute) });
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                test();
                Assert.True(errors.Lines.Count == 0, "Binding errors: " + string.Join(Environment.NewLine, errors.Lines));
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
                application.Resources = new ResourceDictionary();
                var statics = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                typeof(Application).GetField("_appInstance", statics)!.SetValue(null, null);
                typeof(Application).GetField("_appCreatedInThisAppDomain", statics)!.SetValue(null, false);
            }
        });
    }

    public static void Show(Window window)
    {
        window.Left = -32000;
        window.Top = -32000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();
        window.UpdateLayout();
        StaTestHelper.Flush();
    }

    public static void Settle(Window window)
    {
        window.UpdateLayout();
        StaTestHelper.Flush();
    }

    public static void Run(Task task) => StaTestHelper.PumpUntil(() => task.IsCompleted);

    private static double TextWidth(TextBlock style, string text) =>
        new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(style.FontFamily, style.FontStyle, style.FontWeight, style.FontStretch), style.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(style).PixelsPerDip).WidthIncludingTrailingWhitespace;

    private static System.Windows.Controls.Primitives.DataGridColumnHeader Header(DataGrid grid, string header) =>
        Descendants(grid).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>().Single(h => h.Column is not null && Equals(h.Column.Header, header));

    /// <summary>Every column can be resized, is at least its minimum wide, the minimum holds the header text, and text cells trim with a tooltip.</summary>
    public static void AssertResizableReadableColumns(DataGrid grid)
    {
        Assert.True(grid.CanUserResizeColumns);
        foreach (var column in grid.Columns.Where(c => c.Visibility == Visibility.Visible))
        {
            var name = column.Header as string;
            Assert.True(column.CanUserResize, $"{name} cannot be resized.");
            Assert.True(column.ActualWidth >= column.MinWidth - 0.5, $"{name} is {column.ActualWidth} px wide below its minimum {column.MinWidth} px.");
            if (string.IsNullOrEmpty(name)) continue;
            Assert.True(column.MinWidth >= ReadableColumnWidth, $"{name} has a minimum of {column.MinWidth} px.");
            var header = Header(grid, name);
            var label = Descendants(header).OfType<TextBlock>().First(t => t.Text == name);
            // A header that wraps needs its widest word on one line; its other words go to the next line.
            var labelWidth = label.TextWrapping == TextWrapping.NoWrap ? TextWidth(label, name) : name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Max(w => TextWidth(label, w));
            var needed = labelWidth + header.Padding.Left + header.Padding.Right;
            Assert.True(column.MinWidth >= needed, $"{name} has a minimum of {column.MinWidth} px; its header needs {needed:0} px.");
            if (column is DataGridTextColumn text)
            {
                var setters = text.ElementStyle.Setters.OfType<Setter>().ToList();
                Assert.Contains(setters, s => s.Property == TextBlock.TextTrimmingProperty && Equals(s.Value, TextTrimming.CharacterEllipsis));
                Assert.Contains(setters, s => s.Property == FrameworkElement.ToolTipProperty);
            }
        }
    }

    public static void AssertNoSidewaysScroll(DataGrid grid, string name)
    {
        var scroller = Descendants(grid).OfType<ScrollViewer>().First();
        Assert.True(scroller.ScrollableWidth < 1, $"{name}: the columns need {scroller.ScrollableWidth:0} px more than the {scroller.ViewportWidth:0} px grid.");
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    public static double Contrast(Color a, Color b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>The pixels of the element's bounds, each composed over the theme's application background.</summary>
    public static List<Color> Pixels(Window window, FrameworkElement element, string? saveAs = null)
    {
        Settle(window);
        var root = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen())
            context.DrawRectangle((Brush)Application.Current.FindResource("ApplicationBackgroundBrush"), null, new Rect(0, 0, width, height));
        bitmap.Render(backdrop);
        bitmap.Render(root);
        var directory = Environment.GetEnvironmentVariable("APD_RENDER_DIR");
        if (saveAs is not null && !string.IsNullOrEmpty(directory))
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, saveAs + ".png"));
            encoder.Save(file);
        }
        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        var x = Math.Max(0, (int)Math.Ceiling(bounds.X));
        var y = Math.Max(0, (int)Math.Ceiling(bounds.Y));
        var w = Math.Min(width - x, (int)Math.Floor(bounds.Width));
        var h = Math.Min(height - y, (int)Math.Floor(bounds.Height));
        Assert.True(w > 0 && h > 0, $"The element at {bounds} is outside the rendered area.");
        var data = new byte[w * h * 4];
        bitmap.CopyPixels(new Int32Rect(x, y, w, h), data, w * 4, 0);
        var pixels = new List<Color>(w * h);
        for (var i = 0; i < data.Length; i += 4) pixels.Add(Color.FromRgb(data[i + 2], data[i + 1], data[i]));
        return pixels;
    }

    public static Color MostCommon(List<Color> pixels) => pixels.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;

    private static double TextContrast(List<Color> pixels)
    {
        var background = MostCommon(pixels);
        return pixels.Max(p => Contrast(p, background));
    }

    public static DataGridRow Row(DataGrid grid, int index)
    {
        grid.ScrollIntoView(grid.Items[index]);
        grid.UpdateLayout();
        return (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(index);
    }

    private static TextBlock RowText(DataGridRow row) =>
        Descendants(row).OfType<DataGridCell>().Where(c => c.Column is DataGridTextColumn).SelectMany(c => Descendants(c).OfType<TextBlock>())
            .First(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text) && t.ActualWidth > 20);

    /// <summary>A row's text is readable on its background; a selected row differs visibly and a focused cell has the theme's focus rectangle.</summary>
    public static void AssertReadableRowsAndVisibleSelection(Window window, DataGrid grid, string name)
    {
        Assert.True(grid.Items.Count >= 2, $"{name} needs two rows.");
        grid.SelectedIndex = 0;
        Settle(window);
        var plain = Pixels(window, RowText(Row(grid, 1)), name);
        Assert.True(TextContrast(plain) >= 4.5, $"{name}: the text of an unselected row has a contrast of {TextContrast(plain):0.00} on {MostCommon(plain)}.");
        var selected = Pixels(window, RowText(Row(grid, 0)));
        Assert.True(TextContrast(selected) >= 4.5, $"{name}: the text of a selected row has a contrast of {TextContrast(selected):0.00} on {MostCommon(selected)}.");
        Assert.Same(Application.Current.FindResource("DefaultControlFocusVisualStyle"), Descendants(Row(grid, 0)).OfType<DataGridCell>().First().FocusVisualStyle);
        var mark = Contrast(MostCommon(selected), MostCommon(plain));
        Assert.True(mark >= 1.15, $"{name}: a selected row ({MostCommon(selected)}) is not visibly different from an unselected row ({MostCommon(plain)}): {mark:0.00}.");
    }

    public static void AssertReadableListAndVisibleSelection(Window window, ListBox list, string name)
    {
        list.SelectedIndex = 0;
        Settle(window);
        TextBlock ItemText(int i) => Descendants((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(i)).OfType<TextBlock>()
            .First(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text));
        var plain = Pixels(window, ItemText(1), name);
        Assert.True(TextContrast(plain) >= 4.5, $"{name}: an unselected item has a contrast of {TextContrast(plain):0.00}.");
        var selected = Pixels(window, ItemText(0));
        Assert.True(TextContrast(selected) >= 4.5, $"{name}: the selected item has a contrast of {TextContrast(selected):0.00} on {MostCommon(selected)}.");
        var mark = Contrast(MostCommon(selected), MostCommon(plain));
        Assert.True(mark >= 1.15, $"{name}: the selected item ({MostCommon(selected)}) is not visibly different from another ({MostCommon(plain)}): {mark:0.00}.");
    }

    /// <summary>The element lies inside the window and above the footer.</summary>
    public static void AssertInside(Window window, FrameworkElement element, double bottomLimit, string id)
    {
        Assert.True(element.IsVisible, $"{id} is not visible.");
        var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        Assert.True(bounds.Right <= window.ActualWidth + 0.5, $"{id} ends at {bounds.Right:0} px in a {window.ActualWidth:0} px window.");
        Assert.True(bounds.Bottom <= bottomLimit + 0.5, $"{id} ends at {bounds.Bottom:0} px under {bottomLimit:0} px.");
    }

    public static Point Origin(FrameworkElement element, Visual root) => element.TranslatePoint(new Point(0, 0), (UIElement)root);
}
