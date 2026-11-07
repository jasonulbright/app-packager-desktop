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
    internal const int MinimumWholeRows = 6;

    public static TheoryData<ApplicationTheme, int, int> Sizes => new()
    {
        { ApplicationTheme.Light, 1440, 900 },
        { ApplicationTheme.Dark, 1440, 900 },
        { ApplicationTheme.Light, 1024, 720 },
        { ApplicationTheme.Dark, 1024, 720 }
    };

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
        return new PackagesPage(viewModel);
    }

    [WindowTheory]
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

                Pixels(window, toolbar, $"app-packager-{theme}-{width}");
                var grid = (DataGrid)page.FindName("PackagerGrid");
                var whole = WholeRows(grid);
                var lines = ToolbarLines(toolbar);
                Assert.True(whole >= MinimumWholeRows, $"{where}: the grid shows {whole} whole rows; the toolbar uses {lines} lines and the grid is {grid.ActualHeight:0} px tall.");

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
