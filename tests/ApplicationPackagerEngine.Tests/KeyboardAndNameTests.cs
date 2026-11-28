using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ApplicationPackagerUI.Helpers;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The keyboard and screen reader rules of the App Packager grid, the template parts and the navigation.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class KeyboardAndNameTests
{
    private static (PackagesPage Page, Window Window, DataGrid Grid) Shown(double width = 1600, double height = 900)
    {
        var page = AppPackagerFitTests.Create(6, out var viewModel);
        viewModel.Packagers = new(viewModel.FilteredPackagers);
        viewModel.IsDetailPaneOpen = true;
        var window = new Window { Width = width, Height = height, WindowStyle = WindowStyle.None, Content = page };
        Show(window);
        Settle(window);
        return (page, window, (DataGrid)page.FindName("PackagerGrid"));
    }

    private static string FocusedId(Window window)
    {
        var focused = FocusManager.GetFocusedElement(window) as DependencyObject;
        return focused is null ? "none" : AutomationProperties.GetAutomationId(focused) is { Length: > 0 } id ? id : focused.GetType().Name;
    }

    private static DataGridCell FocusFirstCell(Window window, DataGrid grid)
    {
        grid.SelectedIndex = 0;
        Settle(window);
        var cell = Descendants(grid).OfType<DataGridCell>().First(c => c.Column.DisplayIndex == 2);
        cell.Focus();
        return cell;
    }

    private static bool PressTab(DataGridCell cell)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(cell)!, 0, Key.Tab) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        cell.RaiseEvent(args);
        return args.Handled;
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TabLeavesTheGridToThePaneThenTheBatchBarInTheOrderOfThePage(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var (page, window, grid) = Shown();
            try
            {
                var cell = FocusFirstCell(window, grid);
                Assert.True(PressTab(cell), "The grid kept the Tab key.");
                Assert.Equal("splDetailPane", FocusedId(window));

                ById<ButtonBase>(page, "btnDetailPaneClose").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Settle(window);
                Assert.True(PressTab(FocusFirstCell(window, grid)));
                // With the pane closed the next stop is the line under the grid.
                Assert.Equal("btnOperationHistory", FocusedId(window));

                Assert.True(page.LeaveGrid(back: true));
                Assert.Equal("btnMore", FocusedId(window));

                // The page order of the mock: the run buttons, the search row, the grid with its last line, then the pane.
                var order = Descendants(page).OfType<FrameworkElement>().Select(AutomationProperties.GetAutomationId).Where(id => id.Length > 0).ToList();
                int At(string id) { var at = order.IndexOf(id); Assert.True(at >= 0, id + " is not on the page."); return at; }
                Assert.True(At("pnlBatchBar") < At("txtAppPackagerSearch") && At("txtAppPackagerSearch") < At("cboStatusFilter"));
                Assert.True(At("cboStatusFilter") < At("btnSelectUpdates") && At("btnSelectUpdates") < At("btnColumns") && At("btnColumns") < At("btnMore"));
                Assert.True(At("btnStage") < At("btnPublishAll") && At("btnMore") < At("btnLogs") && At("btnLogs") < At("pnlDetailPane"));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TabFromTheSearchBoxReachesColumnsThenMore()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var (page, window, _) = Shown();
            try
            {
                window.Activate();
                var search = Descendants(page).OfType<TextBox>().Single(b => AutomationProperties.GetAutomationId(b) == "txtAppPackagerSearch");
                Keyboard.Focus(search);
                Settle(window);
                var walk = PageChromeKit.Tabs(5);
                var columns = walk.IndexOf("btnColumns");
                Assert.True(columns >= 0 && walk.IndexOf("btnMore") == columns + 1, "Tab from the search box: " + string.Join(" > ", walk));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void EachRowCheckBoxAndCveCellNamesItsApplication()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var (page, window, grid) = Shown();
            try
            {
                Assert.Equal("Applications", UIElementAutomationPeer.CreatePeerForElement(grid).GetName());
                var boxes = Descendants(grid).OfType<CheckBox>().Where(b => b.DataContext is PackagerInfo && b.IsVisible).ToList();
                Assert.NotEmpty(boxes);
                Assert.All(boxes, b => Assert.Equal("Include " + ((PackagerInfo)b.DataContext).Application, AutomationProperties.GetName(b)));
                var row = new PackagerInfo { Application = "Alpha" };
                Assert.Equal("CVEs of Alpha: CPE missing", row.CveCellName);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ThePartsOfADateBoxAndOfAnEditableListHaveTheNameOfTheirOwner(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var date = new DatePicker();
            var combo = new ComboBox { IsEditable = true };
            AutomationProperties.SetName(date, "Available date");
            AutomationProperties.SetName(combo, "Application");
            TemplatePartNames.SetFromOwner(date, true);
            TemplatePartNames.SetFromOwner(combo, true);
            var panel = new StackPanel();
            panel.Children.Add(date);
            panel.Children.Add(combo);
            var window = new Window { Width = 400, Height = 300, Content = panel };
            Show(window);
            try
            {
                Settle(window);
                var boxes = Descendants(panel).OfType<TextBox>().ToList();
                Assert.True(boxes.Count >= 2, "The date box and the editable list have no text box.");
                Assert.All(boxes, b => Assert.False(string.IsNullOrWhiteSpace(UIElementAutomationPeer.CreatePeerForElement(b).GetName())));
                var calendar = Descendants(date).OfType<System.Windows.Controls.Button>().First();
                Assert.Equal("Available date, open the calendar", AutomationProperties.GetName(calendar));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TheNavigationHasNoButtonWithoutAName()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var navigation = new Wpf.Ui.Controls.NavigationView { PaneDisplayMode = Wpf.Ui.Controls.NavigationViewPaneDisplayMode.Left, OpenPaneLength = 220 };
            navigation.MenuItems.Add(new Wpf.Ui.Controls.NavigationViewItem { Content = "App Packager" });
            var window = new Window { Width = 800, Height = 500, Content = navigation };
            Show(window);
            try
            {
                Settle(window);
                NavigationNames.Apply(navigation);
                Assert.Contains(Descendants(navigation).OfType<ButtonBase>(), b => AutomationProperties.GetName(b) == NavigationNames.ToggleName);
                var unnamed = Descendants(navigation).OfType<ButtonBase>().Where(b => b.IsVisible && b.Focusable)
                    .Where(b => string.IsNullOrWhiteSpace(UIElementAutomationPeer.CreatePeerForElement(b)?.GetName()))
                    .Select(b => b.GetType().Name + " '" + b.Name + "'").ToList();
                Assert.True(unnamed.Count == 0, "Buttons without a name: " + string.Join(", ", unnamed));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(Key.Tab, ModifierKeys.None, false, true, FocusNavigationDirection.Next)]
    [InlineData(Key.Tab, ModifierKeys.Shift, false, true, FocusNavigationDirection.Previous)]
    [InlineData(Key.Tab, ModifierKeys.None, false, false, null)]
    [InlineData(Key.Tab, ModifierKeys.Control, false, false, FocusNavigationDirection.Next)]
    [InlineData(Key.Tab, ModifierKeys.None, true, false, FocusNavigationDirection.Next)]
    [InlineData(Key.A, ModifierKeys.None, false, true, null)]
    public void AnEditorWhoseTextDidNotChangePassesTabOn(Key key, ModifierKeys modifiers, bool afterEscape, bool unchanged, FocusNavigationDirection? expected) =>
        Assert.Equal(expected, PivotsScriptsPage.EditorExit(key, modifiers, afterEscape, unchanged));

    [Theory]
    [InlineData(1920, 1032)]
    [InlineData(1024, 720)]
    public void TheCompactColumnsFitTheFullWindowAndScrollIntoViewInASmallOne(int width, int height)
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var (page, window, grid) = Shown(width - 230, height - 48);
            try
            {
                foreach (var column in grid.Columns.Where(c => c.Header is string h && PackagesColumns.Toggleable.Contains(h)))
                    column.Visibility = PackagesColumns.Compact.Contains((string)column.Header) ? Visibility.Visible : Visibility.Collapsed;
                Settle(window);
                var scroller = Descendants(grid).OfType<ScrollViewer>().First();
                if (width >= 1920)
                    Assert.True(scroller.ExtentWidth <= scroller.ViewportWidth + 1,
                        $"{width}x{height}: the columns need {scroller.ExtentWidth:0} px of {scroller.ViewportWidth:0} px.");
                // A smaller window may scroll sideways: no column goes below its least width, and every column can be brought into view.
                else if (scroller.ExtentWidth > scroller.ViewportWidth + 1)
                    Assert.Equal(Visibility.Visible, scroller.ComputedHorizontalScrollBarVisibility);
                foreach (var header in new[] { "Application", "Vendor", "ConfigMgr version", "Latest version", "Status", "CVE check" })
                {
                    var column = grid.Columns.Single(c => c.Header as string == header);
                    Assert.True(column.ActualWidth >= column.MinWidth - 0.5, $"{header} is {column.ActualWidth:0} px wide.");
                    grid.ScrollIntoView(grid.Items[0], column);
                    Settle(window);
                    var shown = Descendants(grid).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>().Single(h => ReferenceEquals(h.Column, column));
                    var bounds = shown.TransformToAncestor(grid).TransformBounds(new Rect(shown.RenderSize));
                    Assert.True(shown.IsVisible && bounds.Right > 0 && bounds.Left < grid.ActualWidth, $"{header} cannot be brought into view at {width}x{height}.");
                }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void AStatusChangeInAFilteredViewKeepsTheSortAndTheScroll()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var page = AppPackagerFitTests.Create(100, out var viewModel);
            viewModel.Packagers = new(viewModel.FilteredPackagers);
            foreach (var row in viewModel.Packagers) row.Status = "Update available";
            viewModel.StatusTab = PackagesTabChoice.Update;
            var window = new Window { Width = 1600, Height = 700, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Settle(window);
                var grid = (DataGrid)page.FindName("PackagerGrid");
                grid.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription("Application", System.ComponentModel.ListSortDirection.Descending));
                Settle(window);
                var scroller = Descendants(grid).OfType<ScrollViewer>().First();
                scroller.ScrollToVerticalOffset(40);
                Settle(window);
                var offset = scroller.VerticalOffset;

                ((PackagerInfo)grid.Items[90]).Status = "Up to date";
                viewModel.RefreshCounts();
                Settle(window);

                Assert.Equal(99, grid.Items.Count);
                Assert.Single(grid.Items.SortDescriptions);
                Assert.Equal(offset, scroller.VerticalOffset, 1.0);
            }
            finally { window.Close(); }
        });
    }

    /// <summary>The glyph of each action and the automation ids that the lab checks drive.</summary>
    [Fact]
    public void ThePageKeepsTheGlyphsAndTheAutomationIdsOfItsActions()
    {
        var xaml = File.ReadAllText(Path.Combine(ApplicationPackagerEngine.Tests.Compliance.CatalogServiceTests.RepositoryRoot(),
            "src", "ApplicationPackagerUI", "Views", "Pages", "PackagesPage.xaml"));
        foreach (var (label, property, glyph) in new[]
                 {
                     ("Check latest", "Icon", "arrow_sync"), ("Check ConfigMgr", "Icon", "server"), ("Check CVEs", "Icon", "shield"), ("Stage", "Icon", "box"),
                     ("Review publish", "Icon", "arrow_right"), ("One Click", "Icon", "play"), ("Catalog", "Icon", "list"), ("Add or import", "TrailingIcon", "chevron_down"),
                     ("Columns", "TrailingIcon", "chevron_down"), ("More", "TrailingIcon", "chevron_down"), ("Select updates", "Icon", "checkmark"),
                     ("Logs", "Icon", "document"), ("Operation history", "Icon", "history"), ("Cancel", "Icon", "dismiss")
                 })
        {
            Assert.Matches($"mk:Mk\\.{property}=\"{glyph}\"[^>]*Content=\"{label}\"", xaml);
            Assert.True(ApplicationPackagerUI.Controls.Mock.Ico.Knows(glyph), glyph);
        }
        foreach (var id in new[]
                 {
                     "btnCatalog", "btnAddOrImport", "btnOneClick", "btnCheckLatest", "btnCheckConfigMgr", "btnCheckCves", "btnColumns", "btnMore",
                     "txtAppPackagerSearch", "cboStatusFilter", "btnStage", "btnPublishAll", "btnSelectUpdates", "btnCancelOperation",
                     "splDetailPane", "pnlDetailPane", "btnDetailWorkbench", "btnDetailMoreActions", "btnDetailSystems", "btnLogs",
                     "btnOperationHistory", "txtLogSummary", "pnlBatchBar", "txtSelectionCount", "txtRunComment"
                 })
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", xaml);
        // Show selected, Clear selection and Show details are entries of the More menu.
        var menus = File.ReadAllText(Path.Combine(ApplicationPackagerEngine.Tests.Compliance.CatalogServiceTests.RepositoryRoot(),
            "src", "ApplicationPackagerUI", "Views", "Pages", "PackagesPage.Menus.cs"));
        foreach (var id in new[] { "miShowSelected", "miClearSelection", "miShowDetails" }) Assert.Contains($"\"{id}\"", menus);
        Assert.Contains("x:Name=\"PackagerGrid\"", xaml);
    }
}
