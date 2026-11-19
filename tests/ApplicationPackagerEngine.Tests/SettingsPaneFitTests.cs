using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using TreeView = System.Windows.Controls.TreeView;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The tree pane of Settings fits its names without a horizontal scroll bar at every size of the main window, in both themes,
/// and a splitter lets the user change its width (Q-88).
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SettingsPaneFitTests
{
    /// <summary>The smallest main window (MinWidth 900, MinHeight 600), a 1440x900 display and the 1920x1032 work area of a 1080p display.</summary>
    private static readonly (int Width, int Height)[] Sizes = [(900, 600), (1440, 900), (1920, 1032)];

    private const double NavigationPaneWidth = 230;
    private const double FrameHeight = 48;

    public static TheoryData<ApplicationTheme, int, int> Cases()
    {
        var data = new TheoryData<ApplicationTheme, int, int>();
        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
            foreach (var (width, height) in Sizes)
                data.Add(theme, width, height);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheTreePaneShowsNoHorizontalScrollBar(ApplicationTheme theme, int width, int height)
    {
        PageHeaderRenderTests.With("Settings", theme, () =>
        {
            using var host = new PageHeaderRenderTests();
            var (page, _) = host.Create("Settings");
            var settings = (SettingsPage)page;
            var window = new Window { Width = width - NavigationPaneWidth, Height = height - FrameHeight, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Settle(window);
                var where = $"{theme} {width}x{height}";
                var tree = (TreeView)settings.FindName("OptionsTree");
                var bars = Descendants(tree).OfType<ScrollViewer>()
                    .Where(s => s.ComputedHorizontalScrollBarVisibility == Visibility.Visible)
                    .Select(s => $"{s.ExtentWidth:0.0} px of content in {s.ViewportWidth:0.0} px").ToList();
                Assert.True(bars.Count == 0, $"{where}: the tree pane scrolls sideways ({string.Join("; ", bars)})");

                var widest = Descendants(tree).OfType<TreeViewItem>().Max(i => i.DesiredSize.Width);
                Assert.True(widest <= tree.ActualWidth + 0.5, $"{where}: the widest name needs {widest:0.0} px in a {tree.ActualWidth:0.0} px pane");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ASplitterLetsTheUserChangeTheWidthOfTheTreePane()
    {
        PageHeaderRenderTests.With("Settings", ApplicationTheme.Light, () =>
        {
            using var host = new PageHeaderRenderTests();
            var (page, _) = host.Create("Settings");
            var window = new Window { Width = 1920 - NavigationPaneWidth, Height = 1032 - FrameHeight, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Settle(window);
                var splitter = Descendants(page).OfType<GridSplitter>().SingleOrDefault();
                Assert.NotNull(splitter);
                Assert.True(splitter!.ActualWidth >= 4, $"The splitter is {splitter.ActualWidth:0.0} px wide; it is hard to grab");
            }
            finally { window.Close(); }
        });
    }
}
