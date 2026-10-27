using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using NavigationViewItem = Wpf.Ui.Controls.NavigationViewItem;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The grouped sidebar of the real main window in both themes, at two window sizes, with the
/// pane open and closed. Every limit comes from the layout or the text measure.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SidebarGroupRenderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-sidebar-render-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public SidebarGroupRenderTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _savedRoot);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>WCAG 2 minimum contrast for normal text.</summary>
    private const double TextContrastMinimum = 4.5;

    /// <summary>WCAG 2 minimum contrast for a non-text mark.</summary>
    private const double MarkContrastMinimum = 3.0;

    private static ServiceProvider MainWindowServices()
    {
        var services = new ServiceCollection().AddPackagerServices().AddSidebar();
        Wpf.Ui.DependencyInjection.ServiceCollectionExtensions.AddNavigationViewPageProvider(services);
        services.AddSingleton<ApplicationPackagerUI.ViewModels.Windows.MainWindowViewModel>();
        services.AddSingleton<Wpf.Ui.INavigationService, Wpf.Ui.NavigationService>();
        services.AddSingleton<Wpf.Ui.ISnackbarService, Wpf.Ui.SnackbarService>();
        services.AddSingleton<Wpf.Ui.IContentDialogService, Wpf.Ui.ContentDialogService>();
        services.AddSingleton<MainWindow>();
        foreach (var type in new[] { typeof(PackagesPage), typeof(AddAppPage), typeof(AddFilePage), typeof(SettingsPage),
                     typeof(DetectionTesterPage), typeof(MaintenanceWindowsPage), typeof(CollectionCompliancePage), typeof(InstallerAnalysisPage),
                     typeof(DeploymentHelperPage), typeof(DpContentManagerPage), typeof(PivotsScriptsPage), typeof(SiteHygienePage) })
            services.AddSingleton(type, _ => new System.Windows.Controls.Page { Tag = type });
        return services.BuildServiceProvider();
    }

    private static double TextWidth(TextBlock text) =>
        Measure(text).WidthIncludingTrailingWhitespace;

    private static FormattedText Measure(TextBlock text) =>
        new(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(text).PixelsPerDip);

    private static Rect Bounds(FrameworkElement element, Visual root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));

    /// <summary>
    /// The pixels of the element's bounds, each composed over the theme's application background.
    /// The main window carries the theme dictionaries itself: it stays open after the test, and
    /// an application object would restyle it during teardown.
    /// </summary>
    private static List<Color> Pixels(Window window, FrameworkElement element, string? saveAs = null)
    {
        RenderKit.Settle(window);
        var root = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen())
            context.DrawRectangle((System.Windows.Media.Brush)window.FindResource("ApplicationBackgroundBrush"), null, new Rect(0, 0, width, height));
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
        var bounds = Bounds(element, root);
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

    /// <summary>
    /// The navigation view animates the width of its pane grid from the compact length to the
    /// open length when the window first shows. The animation runs on the clock, not on the
    /// dispatcher queue, so a settled layout can still hold a part-open pane; the wait ends when
    /// the animated width holds its final value and the layout follows it.
    /// </summary>
    private static void WaitForPaneAnimation(Window window, Wpf.Ui.Controls.NavigationView navigation)
    {
        var pane = (FrameworkElement)navigation.Template.FindName("PaneGrid", navigation);
        var target = navigation.IsPaneOpen ? navigation.OpenPaneLength : navigation.CompactPaneLength;
        StaTestHelper.PumpUntil(() =>
        {
            window.UpdateLayout();
            return pane.Width.Equals(target);
        });
        RenderKit.Settle(window);
    }

    private static double MaxContrast(List<Color> pixels)
    {
        var background = RenderKit.MostCommon(pixels);
        return pixels.Max(p => RenderKit.Contrast(p, background));
    }

    public static TheoryData<ApplicationTheme, int, int, bool> Cases()
    {
        var data = new TheoryData<ApplicationTheme, int, int, bool>();
        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
            foreach (var (width, height) in new[] { (1440, 900), (1024, 720) })
                foreach (var open in new[] { true, false })
                    data.Add(theme, width, height, open);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheCaptionsShowWholeTheDividersReplaceThemWhenClosedAndTheSelectedEntryStandsOut(ApplicationTheme theme, int width, int height, bool open)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsService.SettingsPath)!);
        File.WriteAllText(SettingsService.SettingsPath,
            $$"""{ "FirstRunCompleted": true, "WindowWidth": {{width}}, "WindowHeight": {{height}}, "PaneOpen": {{(open ? "true" : "false")}} }""");
        StaTestHelper.Run(() =>
        {
            using var provider = MainWindowServices();
            var window = provider.GetRequiredService<MainWindow>();
            window.Left = -32000;
            window.Top = -32000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = theme });
            window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            ApplicationHostService.ShowMainWindow(window);
            RenderKit.Settle(window);
            var where = $"{theme} {width}x{height} {(open ? "open" : "closed")}";
            Assert.Equal(width, window.ActualWidth, 0.5);
            Assert.Equal(height, window.ActualHeight, 0.5);

            var navigation = window.GetNavigation();
            Assert.Equal(open, navigation.IsPaneOpen);
            WaitForPaneAnimation(window, navigation);
            var root = (FrameworkElement)window.Content;
            var captions = navigation.MenuItems.OfType<SidebarGroupCaption>().ToList();
            Assert.Equal([SidebarGroupIds.Tools], captions.Select(c => c.Group.Id));
            var entries = navigation.MenuItems.OfType<NavigationViewItem>().ToList();
            var packages = entries[0];
            Assert.Same(packages, navigation.MenuItems[0]);
            Assert.Equal("App Packager", packages.Content);
            var entryBounds = Bounds(packages, root);
            var workbench = entries.Single(i => (string)i.Tag == SidebarIds.Workbench);
            Assert.Equal("Workbench", workbench.Content);
            var tester = entries.Single(i => (string)i.Tag == SidebarIds.DetectionTester);
            var maintenance = entries.Single(i => (string)i.Tag == SidebarIds.MaintenanceWindows);
            var firstTool = entries.Single(i => (string)i.Tag == SidebarIds.InstallerAnalysis);
            Assert.Equal(["App Packager", "Add from web", "Add from file", "Workbench"], entries.Take(4).Select(e => (string)e.Content));

            foreach (var caption in captions)
            {
                var name = $"{where} {caption.Group.Id}";
                Assert.True(caption.IsVisible, $"{name}: the caption is not visible.");
                Assert.False(caption.Focusable);
                var box = Bounds(caption, root);
                Assert.True(box.Bottom <= root.ActualHeight + 0.5, $"{name}: the caption ends at {box.Bottom:0.0} in a {root.ActualHeight:0.0} px window.");
                Assert.DoesNotContain(entries, e => Bounds(e, root) is var b && b.IntersectsWith(box) && b.Height > 0
                    && Math.Min(b.Bottom, box.Bottom) - Math.Max(b.Top, box.Top) > 0.5);

                if (open)
                {
                    var text = caption.Text;
                    Assert.True(text.IsVisible, $"{name}: the caption text is hidden.");
                    Assert.False(caption.Divider.IsVisible, $"{name}: the divider shows with the pane open.");
                    var measure = Measure(text);
                    var pixel = 1 / VisualTreeHelper.GetDpi(text).DpiScaleX;
                    Assert.True(text.ActualWidth + pixel >= TextWidth(text), $"{name}: the caption needs {TextWidth(text):0.0} px and has {text.ActualWidth:0.0} px.");
                    Assert.True(text.ActualHeight + pixel >= measure.Height, $"{name}: the caption needs {measure.Height:0.0} px high and has {text.ActualHeight:0.0} px.");
                    Assert.True(LayoutInformation.GetLayoutClip(text) is null,
                        $"{name}: the caption is clipped to {LayoutInformation.GetLayoutClip(text)?.Bounds}; it needs {text.DesiredSize.Width:0.0} px with its margin.");
                    var textBox = Bounds(text, root);
                    Assert.True(textBox.Left >= entryBounds.Left - pixel && textBox.Right <= entryBounds.Right + pixel,
                        $"{name}: the caption runs from {textBox.Left:0.0} to {textBox.Right:0.0}; the entries run from {entryBounds.Left:0.0} to {entryBounds.Right:0.0}.");
                    var contrast = MaxContrast(Pixels(window, text, $"sidebar-{theme}-{width}x{height}-open-{caption.Group.Id}"));
                    Assert.True(contrast >= TextContrastMinimum, $"{name}: the caption text has a contrast of {contrast:0.00}.");
                }
                else
                {
                    Assert.False(caption.Text.IsVisible, $"{name}: the caption text shows with the pane closed.");
                    var divider = caption.Divider;
                    Assert.True(divider.IsVisible && divider.ActualHeight > 0 && divider.ActualWidth > 0, $"{name}: the divider is not drawn.");
                    var line = Bounds(divider, root);
                    Assert.True(line.Left >= entryBounds.Left - 0.5 && line.Right <= entryBounds.Right + 0.5,
                        $"{name}: the divider runs from {line.Left:0.0} to {line.Right:0.0}; the entries run from {entryBounds.Left:0.0} to {entryBounds.Right:0.0}.");
                    var contrast = MaxContrast(Pixels(window, caption, $"sidebar-{theme}-{width}x{height}-closed-{caption.Group.Id}"));
                    Assert.True(contrast >= MarkContrastMinimum, $"{name}: the divider has a contrast of {contrast:0.00}.");
                }
            }

            var tools = Bounds(captions[0], root);
            Assert.True(tools.Top >= Bounds(workbench, root).Bottom - 0.5, $"{where}: the Tools caption is above the workbench entry.");
            Assert.True(tools.Bottom <= Bounds(firstTool, root).Top + 0.5, $"{where}: the Tools caption is below the first tool.");

            Assert.True(navigation.Navigate(typeof(DetectionTesterPage)));
            RenderKit.Settle(window);
            Assert.Same(tester, navigation.SelectedItem);
            Assert.True(tester.IsActive);
            Assert.False(maintenance.IsActive);
            var selected = Pixels(window, tester, $"sidebar-{theme}-{width}x{height}-{(open ? "open" : "closed")}-selected");
            var plain = Pixels(window, maintenance);
            Assert.NotEqual(RenderKit.MostCommon(plain), RenderKit.MostCommon(selected));
            Assert.True(MaxContrast(selected) >= MarkContrastMinimum, $"{where}: the selected entry has a contrast of {MaxContrast(selected):0.00}.");
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheWorkbenchWindowTitleIsWorkbench(ApplicationTheme theme)
    {
        RenderKit.WithApplication(theme, () =>
        {
            var window = (WorkbenchWindow)RuntimeHelpers.GetUninitializedObject(typeof(WorkbenchWindow));
            typeof(WorkbenchWindow).BaseType!.GetConstructor(Type.EmptyTypes)!.Invoke(window, null);
            window.InitializeComponent();
            Assert.Equal("Workbench", window.Title);
            var titleBar = Assert.Single(LogicalTree(window).OfType<Wpf.Ui.Controls.TitleBar>());
            Assert.Equal("Workbench", titleBar.Title);
        });
    }

    private static IEnumerable<object> LogicalTree(object root)
    {
        yield return root;
        if (root is not DependencyObject node) yield break;
        foreach (var child in LogicalTreeHelper.GetChildren(node))
            foreach (var d in LogicalTree(child)) yield return d;
    }
}
