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

    /// <summary>A row of a grid is 28 to 33 px high at 14 pt (decision D-20); the rows of this page are 33 and 32 px in turn, as in the mock.</summary>
    internal const double MinimumRowHeight = 28, MaximumRowHeight = 33;

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
        // The smallest window of the main window (MinWidth 900): the labels do not fit in four lines, so the toolbar is icon-only.
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
        viewModel.PublishAllToolTip = "Publish All";
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
                // The run buttons, the count of checked applications, and the search row with Select updates, Columns and More.
                var rowsOfActions = new[] { ById<FrameworkElement>(page, "pnlBatchBar"), (FrameworkElement)page.FindName("FilterBar") };
                foreach (var bar in rowsOfActions)
                {
                    foreach (var control in Descendants(bar).OfType<System.Windows.Controls.Control>().Where(c => c.IsVisible && c is ButtonBase or System.Windows.Controls.ComboBox or System.Windows.Controls.TextBox && c.TemplatedParent is not System.Windows.Controls.ComboBox))
                    {
                        var at = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
                        Assert.True(at.Left >= -0.5 && at.Right <= window.ActualWidth + 0.5, $"{where}: {AutomationProperties.GetAutomationId(control)} lies at {at.Left:0} to {at.Right:0} px of {window.ActualWidth:0} px.");
                    }
                    PageChromeKit.AssertNothingClipped(bar, where);
                }

                var buttons = Descendants(toolbar).OfType<ButtonBase>().Where(b => b.IsVisible).ToList();
                Assert.Equal(["btnCheckLatest", "btnCheckConfigMgr", "btnCheckCves", "btnStage", "btnPublishAll", "btnOneClick"], buttons.Select(AutomationProperties.GetAutomationId));
                Assert.All(buttons, b => Assert.True(Math.Abs(b.ActualHeight - CompactButtonHeight) <= 0.5,
                    $"{where}: {AutomationProperties.GetAutomationId(b)} is {b.ActualHeight:0.#} px high; the compact size is {CompactButtonHeight} px."));
                // No button lies over another.
                var places = buttons.Select(b => b.TransformToAncestor(window).TransformBounds(new Rect(b.RenderSize))).ToList();
                for (var i = 0; i < places.Count; i++)
                    for (var j = i + 1; j < places.Count; j++)
                    {
                        var shared = Rect.Intersect(places[i], places[j]);
                        Assert.True(shared.IsEmpty || shared.Width < 0.5 || shared.Height < 0.5, $"{where}: {AutomationProperties.GetAutomationId(buttons[i])} lies over {AutomationProperties.GetAutomationId(buttons[j])}.");
                    }
                // The run buttons are one line where they fit; only a narrower window wraps them.
                if (width >= 1269) Assert.True(ToolbarLines(toolbar) == 1, $"{where}: the toolbar uses {ToolbarLines(toolbar)} lines.");
                else Assert.InRange(ToolbarLines(toolbar), 1, 2);

                Pixels(window, toolbar, $"app-packager-{theme}-{width}");
                var grid = (DataGrid)page.FindName("PackagerGrid");
                var whole = WholeRows(grid);
                var lines = ToolbarLines(toolbar);
                // The design basis is the maximized 1080p window (D-20): ten whole rows there (rows of 28 to 32 px); the smaller windows keep five.
                var needed = height >= 1000 ? MinimumWholeRows : 5;
                var rowHeight = Descendants(grid).OfType<DataGridRow>().Where(r => r.ActualHeight > 0).Max(r => r.ActualHeight);
                var firstRow = Descendants(grid).OfType<DataGridRow>().First(r => r.ActualHeight > 0);
                Assert.True(rowHeight >= MinimumRowHeight - 0.5 && rowHeight <= MaximumRowHeight + 0.5, $"{where}: a row is {rowHeight:0.#} px high; a row is {MinimumRowHeight} to {MaximumRowHeight} px. Row margin {firstRow.Margin}, min {firstRow.MinHeight}, padding {firstRow.Padding}. Cells: " + string.Join("; ", Descendants(firstRow).OfType<System.Windows.Controls.DataGridCell>().Take(14).Select(c => $"{c.Column?.Header} h={c.ActualHeight:0} min={c.MinHeight} pad={c.Padding} content={string.Join("/", Descendants(c).OfType<FrameworkElement>().Skip(1).Take(4).Select(e => $"{e.GetType().Name}:{e.DesiredSize.Height:0}"))}")));
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

    // While a run is active the line under the grid shows Cancel; it has the height and the line of Operation history and Logs beside it.
    [Theory]
    [MemberData(nameof(Sizes))]
    public void TheCancelButtonHasTheHeightAndTheLineOfItsNeighboursWhileARunIsActive(ApplicationTheme theme, int width, int height)
    {
        WithApplication(theme, () =>
        {
            var page = Create(12, out var viewModel);
            // The view model is built without its constructor, so the setter (which notifies commands that do not exist) is not used: the field is set and the change is announced.
            typeof(PackagesViewModel).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Single(f => f.Name.TrimStart('_').Equals("isOperationRunning", StringComparison.OrdinalIgnoreCase)).SetValue(viewModel, true);
            typeof(PackagesViewModel).GetMethod("OnPropertyChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { typeof(string) }, null)!.Invoke(viewModel, new object[] { "IsOperationRunning" });
            var window = new Window { Width = width - NavigationPaneWidth, Height = height - FrameHeight, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Settle(window);
                var where = $"App Packager {theme} {width}x{height}, run active";
                var line = ById<FrameworkElement>(page, "pnlLogBar");
                Pixels(window, line, $"app-packager-running-{theme}-{width}");
                var buttons = Descendants(line).OfType<ButtonBase>().Where(b => b.IsVisible).ToList();
                Assert.Equal(["btnCancelOperation", "btnLogs", "btnOperationHistory"], buttons.Select(AutomationProperties.GetAutomationId).Order(StringComparer.Ordinal));
                var cancel = buttons.Single(b => AutomationProperties.GetAutomationId(b) == "btnCancelOperation");
                Point Top(FrameworkElement e) => e.TransformToAncestor(line).Transform(new Point(0, 0));
                Assert.All(buttons, b => Assert.True(Math.Abs(Top(b).Y - Top(cancel).Y) < 1, $"{where}: {AutomationProperties.GetAutomationId(b)} is not on the line of Cancel."));
                Assert.All(buttons, b => Assert.True(Math.Abs(b.ActualHeight - cancel.ActualHeight) < 0.5, $"{where}: {AutomationProperties.GetAutomationId(b)} is {b.ActualHeight:0.#} px high."));
                foreach (var button in buttons)
                {
                    var at = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                    Assert.True(at.Left >= -0.5 && at.Right <= window.ActualWidth + 0.5 && at.Bottom <= window.ActualHeight + 0.5, $"{where}: {AutomationProperties.GetAutomationId(button)} lies at {at}.");
                }
                PageChromeKit.AssertNothingClipped(line, where);
            }
            finally { window.Close(); }
        });
    }
}
