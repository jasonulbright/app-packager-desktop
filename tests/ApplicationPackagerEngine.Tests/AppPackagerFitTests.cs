using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using DataGrid = System.Windows.Controls.DataGrid;
using Point = System.Windows.Point;
using DataGridRow = System.Windows.Controls.DataGridRow;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The App Packager page in a real window at both sizes: the toolbar fits, the grid shows whole rows and the Catalog button works.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class AppPackagerFitTests
{
    private const double NavigationPaneWidth = 230;
    private const double FrameHeight = 48;

    /// <summary>The least number of complete data rows that the grid shows at every size in <see cref="Sizes"/>.</summary>
    internal const int MinimumWholeRows = 10;

    /// <summary>A row of a grid is 28 to 32 px high at 14 pt (decision D-20).</summary>
    internal const double MinimumRowHeight = 28, MaximumRowHeight = 32;

    public static TheoryData<ApplicationTheme, int, int> Sizes => new()
    {
        { ApplicationTheme.Light, 1440, 900 },
        { ApplicationTheme.Dark, 1440, 900 },
        { ApplicationTheme.Light, 1024, 720 },
        { ApplicationTheme.Dark, 1024, 720 },
        // The two widths of the owner screenshots (the toolbar was clipped at 1276 and icon-only at 1269) and the work area of a maximized
        // window on a 1080p display with the Windows 11 taskbar (1920 x 1032 at 100 percent).
        { ApplicationTheme.Light, 1276, 800 },
        { ApplicationTheme.Dark, 1276, 800 },
        { ApplicationTheme.Light, 1269, 800 },
        { ApplicationTheme.Dark, 1269, 800 },
        { ApplicationTheme.Light, 1920, 1032 },
        { ApplicationTheme.Dark, 1920, 1032 },
        // The smallest window of the main window (MinWidth 900): the labels do not fit in five lines, so the toolbar is icon-only.
        { ApplicationTheme.Light, 900, 700 },
        { ApplicationTheme.Dark, 900, 700 }
    };

    /// <summary>The height of a button of the compact size: 32 px at 14 pt (4 px above and below the text).</summary>
    internal const double CompactButtonHeight = 32;

    /// <summary>The rows of the grid that lie completely inside the area below the column headers.</summary>
    internal static int WholeRows(DataGrid grid)
    {
        var presenter = Descendants(grid).OfType<ScrollContentPresenter>().First();
        return Descendants(grid).OfType<DataGridRow>().Count(row =>
        {
            var top = row.TransformToAncestor(presenter).Transform(new Point(0, 0)).Y;
            return top >= -0.5 && top + row.ActualHeight <= presenter.ActualHeight + 0.5;
        });
    }

    /// <summary>The number of lines the buttons of the toolbar use, from their measured positions.</summary>
    internal static int ToolbarLines(FrameworkElement toolbar) =>
        Descendants(toolbar).OfType<ButtonBase>().Where(b => b.IsVisible)
            .Select(b => Math.Round(b.TransformToAncestor(toolbar).Transform(new Point(0, 0)).Y / 8)).Distinct().Count();

    internal static PackagesPage Create(int rows, out PackagesViewModel viewModel)
    {
        System.Windows.Application.Current.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
        viewModel = (PackagesViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
        viewModel.FilteredPackagers = new(Enumerable.Range(0, rows).Select(i => new PackagerInfo { Uid = $"app{i}", Vendor = "Contoso", Application = $"Contoso Application {i}" }));
        viewModel.LogText = string.Join(Environment.NewLine, Enumerable.Range(0, 30).Select(i => $"[10:00:{i:00}] Checked application {i}."));
        // The view model is built without its constructor here, so the texts that the buttons bind are set as the program sets them.
        viewModel.CheckMecmToolTip = "Check ConfigMgr";
        viewModel.DiscoverToolTip = "Discover unmanaged software";
        viewModel.PublishConfigMgrToolTip = "Publish to ConfigMgr";
        viewModel.PublishIntuneToolTip = "Publish to Intune";
        viewModel.PublishWsusToolTip = "Publish to WSUS";
        viewModel.WsusUpdatesToolTip = "WSUS Updates";
        return new PackagesPage(viewModel);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void TheToolbarFitsTheGridShowsWholeRowsAndTheCatalogButtonWorks(ApplicationTheme theme, int width, int height)
    {
        WithApplication(theme, () =>
        {
            var page = Create(12, out _);
            var window = new Window { Width = width - NavigationPaneWidth, Height = height - FrameHeight, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Settle(window);
                var where = $"App Packager {theme} {width}x{height}";
                var toolbar = ById<FrameworkElement>(page, "pnlAppPackagerToolbar");
                foreach (var button in Descendants(toolbar).OfType<ButtonBase>().Where(b => b.IsVisible))
                {
                    var right = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize)).Right;
                    Assert.True(right <= window.ActualWidth + 0.5, $"{where}: {AutomationProperties.GetAutomationId(button)} ends at {right:0} px of {window.ActualWidth:0} px.");
                }
                PageChromeKit.AssertNothingClipped(toolbar, where);

                // The buttons have the compact size, and the icon-only mode is a row of uniform squares.
                var compact = (string?)toolbar.Tag == "Compact";
                var buttons = Descendants(toolbar).OfType<ButtonBase>().Where(b => b.IsVisible).ToList();
                Assert.NotEmpty(buttons);
                if (compact)
                {
                    Assert.All(buttons, b => Assert.True(Math.Abs(b.ActualWidth - b.ActualHeight) < 0.5 && Math.Abs(b.ActualWidth - CompactButtonHeight) < 0.5,
                        $"{where}: {AutomationProperties.GetAutomationId(b)} is {b.ActualWidth:0.#} x {b.ActualHeight:0.#}; an icon button is a {CompactButtonHeight} px square."));
                    Assert.All(buttons, b => Assert.False(string.IsNullOrWhiteSpace(b.ToolTip?.ToString()), $"{where}: an icon button has no tooltip."));
                }
                else
                {
                    Assert.All(buttons, b => Assert.True(b.ActualHeight <= CompactButtonHeight + 0.5,
                        $"{where}: {AutomationProperties.GetAutomationId(b)} is {b.ActualHeight:0.#} px high; the compact size is {CompactButtonHeight} px."));
                }
                // A normal window shows the labels: the icon-only mode is for a width that cannot hold them in five lines.
                if (width <= 900) Assert.True(compact, $"{where}: the toolbar keeps its labels at {width} px.");
                if (width >= 1200) Assert.False(compact, $"{where}: the toolbar is icon-only at {width} px.");

                Pixels(window, toolbar, $"app-packager-{theme}-{width}");
                var grid = (DataGrid)page.FindName("PackagerGrid");
                var whole = WholeRows(grid);
                var lines = ToolbarLines(toolbar);
                // The design basis is the maximized 1080p window (D-20): ten whole rows there (rows of 28 to 32 px); the smaller windows keep six.
                var needed = height >= 1000 ? MinimumWholeRows : 6;
                var rowHeight = Descendants(grid).OfType<DataGridRow>().Where(r => r.ActualHeight > 0).Max(r => r.ActualHeight);
                Assert.True(rowHeight >= MinimumRowHeight - 0.5 && rowHeight <= MaximumRowHeight + 0.5, $"{where}: a row is {rowHeight:0.#} px high; a row is {MinimumRowHeight} to {MaximumRowHeight} px. Cells of the first row: " + string.Join(", ", Descendants(grid).OfType<System.Windows.Controls.DataGridCell>().Take(14).Select(c => $"{c.Column?.Header}={c.ActualHeight:0}")));
                Assert.True(whole >= needed, $"{where}: the grid shows {whole} whole rows (needs {needed}); the toolbar uses {lines} lines and the grid is {grid.ActualHeight:0} px tall.");

                var catalog = ById<ButtonBase>(page, "btnCatalog");
                Assert.True(catalog.IsVisible && catalog.IsEnabled && catalog.Focusable && catalog.IsTabStop, $"{where}: the Catalog button cannot take focus.");
                var bounds = catalog.TransformToAncestor(window).TransformBounds(new Rect(catalog.RenderSize));
                Assert.True(bounds.Left >= 0 && bounds.Right <= window.ActualWidth + 0.5 && bounds.Bottom <= window.ActualHeight, $"{where}: the Catalog button lies at {bounds}.");
                window.Activate();
                Assert.True(catalog.Focus(), $"{where}: the Catalog button takes no keyboard focus.");
                Assert.NotNull(UIElementAutomationPeer.CreatePeerForElement(catalog).GetPattern(PatternInterface.Invoke));
                Assert.False(string.IsNullOrWhiteSpace(UIElementAutomationPeer.CreatePeerForElement(catalog).GetName()), $"{where}: the Catalog button has no automation name.");
            }
            finally { window.Close(); }
        });
    }
}
