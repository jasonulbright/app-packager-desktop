using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using CheckBox = System.Windows.Controls.CheckBox;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using DataGridRow = System.Windows.Controls.DataGridRow;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TabControl = System.Windows.Controls.TabControl;
using TextBlock = System.Windows.Controls.TextBlock;
using TreeViewItem = System.Windows.Controls.TreeViewItem;
using VM = ApplicationPackagerUI.ViewModels.Pages.SiteHygieneViewModel;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>The Site Hygiene page and its dialogs in real windows, over the fake service, in both themes.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SiteHygieneRenderTests
{
    private const double NavigationPaneWidth = 230;
    private const double CollapsedPaneWidth = 48;
    private const string LongText = "Contoso Line of Business Application for the Finance Department in the North West Region, Building Seven";

    /// <summary>A value must fit its column with this much room: a lab window cut a value the test measured as just fitting.</summary>
    private const double Room = 1.1;

    private static readonly (int Tab, string Grid)[] AllGrids =
    [
        (VM.FindingsTab, "dgFindings"), (VM.RelationsTab, "dgEdges"), (VM.SummaryTab, "dgCategories"), (VM.SummaryTab, "dgChecks"), (VM.FindingsTab, "dgHygieneResults")
    ];

    private static SiteHygienePageHarness LongHarness(int findings = 60)
    {
        var h = SiteHygienePageHarness.Large(findings, LongText);
        for (var i = 0; i < 30; i++)
            h.Service.Edges.Add(new RelationEdge($"1678{i:0000}", $"{LongText} {i}", $"1679{i:0000}", $"{LongText} {i} older", i % 2 == 0 ? "Supersedes" : "DependsOn", i % 3 == 0 ? "Retired" : "Healthy"));
        return h;
    }

    private static void WithPage(ApplicationTheme theme, Action<Window, SiteHygienePage, VM, SiteHygienePageHarness> test,
        double width = 1440 - NavigationPaneWidth, double height = 900 - 48, Func<SiteHygienePageHarness>? harness = null)
    {
        WithApplication(theme, () =>
        {
            var h = (harness ?? (() => LongHarness()))();
            var viewModel = h.Create();
            var page = new SiteHygienePage(viewModel);
            var window = new Window { Width = width, Height = height, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Run(page.OnNavigatedToAsync());
                Settle(window);
                test(window, page, viewModel, h);
            }
            finally { window.Close(); }
        });
    }

    /// <summary>Scans, reads the relationships and runs one change fix, so every grid and the result panel have rows.</summary>
    private static void Fill(Window window, VM viewModel)
    {
        Run(viewModel.ScanAsync());
        Run(viewModel.LoadRelationsAsync());
        viewModel.SetSelectedFindings([viewModel.FindingRows.First(r => r.Finding.FixKind == HygieneFixKind.SetCollectionSchedule)]);
        Run(viewModel.RunFixCommand.ExecuteAsync(null));
        viewModel.SetSelectedFindings([viewModel.FindingRows[1]]);
        Settle(window);
    }

    private static void ShowTab(Window window, VM viewModel, int tab)
    {
        viewModel.SelectedTabIndex = tab;
        Settle(window);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheFourTabsRenderAndTheFindingsGridVirtualizesFiftyThousandRows(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Assert.Equal(["Findings", "Relationships", "Summary", "Options"], ById<TabControl>(page, "tabHygiene").Items.OfType<TabItem>().Select(t => (string)t.Header));
            Run(viewModel.ScanAsync());
            Settle(window);
            var grid = ById<DataGrid>(page, "dgFindings");
            Assert.Equal(50_000, grid.Items.Count);
            Assert.InRange(Descendants(grid).OfType<DataGridRow>().Count(), 1, 100);
            var events = page.SelectionEvents;
            viewModel.SetSelectedFindings(viewModel.FindingRows);
            viewModel.CategoryFilter = "Devices";
            viewModel.CategoryFilter = VM.AllText;
            Settle(window);
            Assert.Equal(50_000, grid.SelectedItems.Count);
            Assert.InRange(page.SelectionEvents - events, 1, 3);
            Assert.True(ById<FrameworkElement>(page, "txtHygieneSummary").IsVisible);
            foreach (var tab in new[] { VM.RelationsTab, VM.SummaryTab, VM.OptionsTab })
            {
                ShowTab(window, viewModel, tab);
                Assert.True(ById<FrameworkElement>(page, "txtHygieneSummary").IsVisible);
            }
        }, harness: () => LongHarness(50_000));
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 1440 - NavigationPaneWidth)]
    [InlineData(ApplicationTheme.Dark, 1440 - NavigationPaneWidth)]
    [InlineData(ApplicationTheme.Light, 1440 - CollapsedPaneWidth)]
    [InlineData(ApplicationTheme.Dark, 1440 - CollapsedPaneWidth)]
    public void EveryGridShowsAllItsColumnsAt1440Pixels(ApplicationTheme theme, double width)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel);
            foreach (var (tab, id) in AllGrids)
            {
                ShowTab(window, viewModel, tab);
                var grid = ById<DataGrid>(page, id);
                AssertResizableReadableColumns(grid);
                AssertNoSidewaysScroll(grid, id);
            }
        }, width);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void EveryGridShowsReadableRowsAndAVisibleSelection(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel);
            Run(viewModel.LoadRelationsAsync());
            foreach (var (tab, id) in AllGrids.Where(g => g.Grid != "dgHygieneResults"))
            {
                ShowTab(window, viewModel, tab);
                var grid = ById<DataGrid>(page, id);
                grid.BringIntoView();
                Settle(window);
                AssertReadableRowsAndVisibleSelection(window, grid, $"{id}-{theme}");
            }
        });
    }

    /// <summary>The longest values the fixed columns hold, by grid and column header.</summary>
    private static IEnumerable<(string Grid, string Column, string Text)> LongValues()
    {
        foreach (var severity in SiteHygieneLabels.Severities) yield return ("dgFindings", "Severity", SiteHygieneLabels.SeverityText(severity));
        yield return ("dgFindings", "Check", "COL-10");
        yield return ("dgFindings", "Check", "MNT-02");
        foreach (var category in VM.KnownCategories)
        {
            yield return ("dgFindings", "Category", category);
            yield return ("dgChecks", "Category", category);
        }
        yield return ("dgFindings", "New", "New");
        yield return ("dgFindings", "Suppressed", "Yes");
        foreach (var kind in Enum.GetValues<HygieneFixKind>())
        {
            yield return ("dgFindings", "Fix", SiteHygieneLabels.FixLabel(kind));
            yield return ("dgHygieneResults", "Fix", SiteHygieneLabels.FixLabel(kind));
        }
        foreach (var result in new[] { "Done", "Unchanged", "Failed", "Skipped" }) yield return ("dgHygieneResults", "Result", result);
        yield return ("dgEdges", "Kind", "Depends on");
        yield return ("dgEdges", "Kind", "Supersedes");
        foreach (var header in new[] { "High", "Medium", "Low", "Info", "Total" })
        {
            yield return ("dgCategories", header, "Not known");
            yield return ("dgCategories", header, "123,456");
        }
        yield return ("dgCategories", "Checks not run", "57");
        yield return ("dgChecks", "Check", "COL-10");
        foreach (var state in new[] { "Ran", "NotRun", "NeedsSiteDatabase", "Failed" }) yield return ("dgChecks", "State", SiteHygieneLabels.StateText(state));
        yield return ("dgChecks", "Findings", "Not known");
        yield return ("dgChecks", "Findings", "123,456");
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 1440 - CollapsedPaneWidth)]
    [InlineData(ApplicationTheme.Dark, 1440 - CollapsedPaneWidth)]
    [InlineData(ApplicationTheme.Light, 1440 - NavigationPaneWidth)]
    [InlineData(ApplicationTheme.Dark, 1440 - NavigationPaneWidth)]
    public void TheLongestRealValuesFitTheirColumnsWithRoomAt1440Pixels(ApplicationTheme theme, double width)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel);
            var failures = new List<string>();
            foreach (var (tab, id) in AllGrids)
            {
                ShowTab(window, viewModel, tab);
                var grid = ById<DataGrid>(page, id);
                foreach (var (_, header, text) in LongValues().Where(v => v.Grid == id))
                {
                    var column = grid.Columns.Single(c => Equals(c.Header, header));
                    var cell = Descendants(grid).OfType<DataGridCell>().First(c => ReferenceEquals(c.Column, column));
                    var block = Descendants(cell).OfType<TextBlock>().First();
                    var padding = cell.ActualWidth - block.ActualWidth;
                    var needed = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
                        new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, System.Windows.Media.Brushes.Black,
                        VisualTreeHelper.GetDpi(block).PixelsPerDip).WidthIncludingTrailingWhitespace * Room;
                    if (needed + padding > column.ActualWidth + 0.5) failures.Add($"{id} {header}: \"{text}\" needs {needed + padding:0} px and the column has {column.ActualWidth:0} px.");
                }
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }, width);
    }

    private static void AssertReachable(ScrollViewer scroller, FrameworkElement element, string id)
    {
        Assert.True(element.IsVisible, $"{id} is not visible.");
        var bounds = element.TransformToAncestor(scroller).TransformBounds(new Rect(element.RenderSize));
        Assert.True(bounds.Left >= -0.5 && bounds.Right <= scroller.ViewportWidth + 0.5, $"{id} spans {bounds.Left:0} to {bounds.Right:0} px in a {scroller.ViewportWidth:0} px tab.");
        Assert.True(bounds.Bottom + scroller.VerticalOffset <= scroller.ExtentHeight + 0.5, $"{id} ends below the area the tab scrolls to.");
    }

    public static TheoryData<ApplicationTheme, double, double> Sizes() => new()
    {
        { ApplicationTheme.Light, 900 - NavigationPaneWidth, 600 - 48 },
        { ApplicationTheme.Dark, 900 - NavigationPaneWidth, 600 - 48 },
        { ApplicationTheme.Light, 900 - CollapsedPaneWidth, 600 - 48 },
        { ApplicationTheme.Light, 1440 - NavigationPaneWidth, 900 - 48 },
        { ApplicationTheme.Dark, 1440 - CollapsedPaneWidth, 900 - 48 }
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void EveryTabFitsItsWidthAndScrollsToEveryControl(ApplicationTheme theme, double width, double height)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel);
            void Check(int tab, string scrollId, params string[] ids)
            {
                ShowTab(window, viewModel, tab);
                var scroller = ById<ScrollViewer>(page, scrollId);
                Assert.True(scroller.ScrollableWidth < 1, $"{scrollId} scrolls sideways by {scroller.ScrollableWidth:0} px at {width}x{height}.");
                foreach (var id in ids)
                {
                    var element = ById<FrameworkElement>(page, id);
                    element.BringIntoView();
                    Settle(window);
                    AssertReachable(scroller, element, id);
                }
            }

            Check(VM.FindingsTab, "scrollFindingsTab", "txtFindingFilter", "cboFindingCategory", "cboFindingSeverity", "chkShowSuppressed", "txtFindingCount", "btnHygieneRunFix",
                "btnHygieneSuppress", "btnHygieneUnsuppress", "dgFindings", "txtFindingDetailTitle", "txtFindingDetailEvidence", "txtFixText", "btnCopyFixText", "txtNotRun");
            Check(VM.RelationsTab, "scrollRelationsTab", "btnReadRelations", "btnCancelRelations", "txtRelationsSummary", "txtEdgeFilter", "cboEdgeKind", "dgEdges");
            Check(VM.SummaryTab, "scrollSummaryTab", "dgCategories", "dgChecks");
            Check(VM.OptionsTab, "scrollOptionsTab", "btnAreasAll", "btnAreasClear", "lstAreas", "cboPause", "chkProbeSources", "txtReportFolder", "btnOptionsFolder");
            foreach (var id in new[] { "btnHygieneScan", "btnHygieneCancel", "btnHygieneLiveViews", "btnHygieneCsv", "btnHygieneHtml", "btnHygieneFolder", "txtHygieneSummary", "txtHygieneAreas",
                         "dgHygieneResults", "txtHygieneStatus" })
            {
                var element = ById<FrameworkElement>(page, id);
                Assert.True(element.IsVisible, $"{id} is not visible.");
                var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
                Assert.True(bounds.Right <= window.ActualWidth + 0.5 && bounds.Bottom <= window.ActualHeight + 0.5,
                    $"{id} ends at {bounds.Right:0},{bounds.Bottom:0} px in a {window.ActualWidth:0}x{window.ActualHeight:0} px window.");
            }
            var tabs = ById<TabControl>(page, "tabHygiene");
            Assert.True(tabs.ActualHeight >= 180, $"The tabs get {tabs.ActualHeight:0} px at {width}x{height}.");
        }, width, height);
    }

    private static UIElement Focused() => Assert.IsAssignableFrom<UIElement>(Keyboard.FocusedElement);

    private static void Press(Window window, Key key)
    {
        var target = Focused();
        var source = PresentationSource.FromVisual(target)!;
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled) target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
        Settle(window);
    }

    private static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T) element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as T;
    }

    private static string FocusedId()
    {
        var focused = Keyboard.FocusedElement as FrameworkElement;
        return focused is null ? "none" : AutomationProperties.GetAutomationId(focused) is { Length: > 0 } id ? id : focused.GetType().Name + " in " + (Ancestor<DataGrid>(focused)?.Name ?? "-");
    }

    private static string NextFocus(Window window)
    {
        ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        Settle(window);
        return FocusedId();
    }

    private static DataGridRow TabInto(Window window, UIElement start, DataGrid grid)
    {
        Assert.True(Keyboard.Focus(start) == start || start.IsKeyboardFocusWithin, $"{AutomationProperties.GetAutomationId(start)} takes no focus.");
        Settle(window);
        var path = new List<string>();
        for (var i = 0; i < 40; i++)
        {
            ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Settle(window);
            var focused = Keyboard.FocusedElement as DependencyObject;
            path.Add(focused is FrameworkElement f ? $"{f.GetType().Name}({AutomationProperties.GetAutomationId(f)})" : focused?.GetType().Name ?? "none");
            if (focused is not null && Ancestor<DataGrid>(focused) == grid) return Assert.IsType<DataGridRow>(Ancestor<DataGridRow>(focused), exactMatch: false);
        }
        Assert.Fail($"Tab never reaches {AutomationProperties.GetAutomationId(grid)}: {string.Join(" > ", path)}");
        return null!;
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TabReachesEveryGridOnceAndTheFindingsActionsComeRightAfterTheGrid(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel);
            Assert.All(AllGrids.Select(g => (DataGrid)page.FindName(g.Grid)), g => Assert.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(g)));
            Assert.All(AllGrids.Select(g => (DataGrid)page.FindName(g.Grid)), g => Assert.True(ApplicationPackagerUI.Helpers.DataGridRightClick.GetSelectsRow(g)));

            viewModel.SetSelectedFindings([]);
            Settle(window);
            var finding = TabInto(window, ById<UIElement>(page, "chkShowSuppressed"), ById<DataGrid>(page, "dgFindings"));
            Assert.True(finding.IsSelected);
            Assert.Equal([((FindingGridRow)finding.Item).Key], viewModel.SelectedFindings.Select(r => r.Key));
            Assert.Same(finding.Item, viewModel.DetailFinding);
            Assert.True(viewModel.RunFixCommand.CanExecute(null));
            Assert.Equal("btnHygieneRunFix", NextFocus(window));
            Assert.Equal("btnHygieneSuppress", NextFocus(window));

            Assert.True(TabInto(window, ById<UIElement>(page, "txtFixText"), ById<DataGrid>(page, "dgHygieneResults")).IsSelected);

            ShowTab(window, viewModel, VM.RelationsTab);
            viewModel.RelationsTabIndex = VM.InventoryTab;
            Settle(window);
            var edge = TabInto(window, ById<UIElement>(page, "cboEdgeKind"), ById<DataGrid>(page, "dgEdges"));
            Assert.Same(edge.Item, viewModel.SelectedEdge);

            ShowTab(window, viewModel, VM.SummaryTab);
            Assert.True(TabInto(window, ById<UIElement>(page, "tabHygieneSummary"), ById<DataGrid>(page, "dgCategories")).IsSelected);
            var check = TabInto(window, Descendants(ById<DataGrid>(page, "dgCategories")).OfType<DataGridCell>().First(), ById<DataGrid>(page, "dgChecks"));
            Assert.True(check.IsSelected);
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheTreeOpensAChainWithTheKeyboardAndShowsTheHealthAsText(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel);
            ShowTab(window, viewModel, VM.RelationsTab);
            viewModel.RelationsTabIndex = VM.TreeTab;
            Settle(window);
            var tree = ById<System.Windows.Controls.TreeView>(page, "treeRelations");
            var group = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.True(group.Focus());
            Settle(window);
            Press(window, Key.Right);
            Assert.True(viewModel.TreeRoots[0].IsExpanded);
            Press(window, Key.Down);
            Press(window, Key.Right);
            var chain = viewModel.TreeRoots[0].Children.Single(c => c.IsSelected);
            Assert.True(chain.IsExpanded);
            Press(window, Key.Down);
            var child = chain.Children.Single(c => c.IsSelected);
            Assert.Matches("^Supersedes .*, (✓ Healthy|! Retired)$", child.Text);
            Assert.Equal(child.Text, AutomationProperties.GetName((TreeViewItem)Keyboard.FocusedElement));
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheSummaryLineShowsOnEveryTabAfterAReturnToThePage(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel);
            foreach (var tab in new[] { VM.FindingsTab, VM.RelationsTab, VM.SummaryTab, VM.OptionsTab })
            {
                ShowTab(window, viewModel, tab);
                Run(page.OnNavigatedFromAsync());
                Run(page.OnNavigatedToAsync());
                Settle(window);
                var summary = ById<TextBlock>(page, "txtHygieneSummary");
                Assert.True(summary.IsVisible && summary.ActualHeight > 0, $"The summary line is hidden on tab {tab}.");
                Assert.Contains(" suppressed. Last scan ", summary.Text);
            }
        });
    }

    /// <summary>Every visible check box shows its whole text: no text line runs past the right or bottom edge of its box.</summary>
    private static int AssertCheckBoxTextFits(Window window, DependencyObject root, string where)
    {
        var count = 0;
        foreach (var box in Descendants(root).OfType<CheckBox>().Where(b => b.IsVisible))
        {
            var boxBounds = box.TransformToAncestor(window).TransformBounds(new Rect(box.RenderSize));
            foreach (var text in Descendants(box).OfType<TextBlock>().Where(t => t.IsVisible && t.Text.Length > 0))
            {
                count++;
                var bounds = text.TransformToAncestor(window).TransformBounds(new Rect(text.RenderSize));
                Assert.True(bounds.Right <= boxBounds.Right + 0.5 && bounds.Bottom <= boxBounds.Bottom + 0.5,
                    $"{where}: the text of {AutomationProperties.GetAutomationId(box)} ends at {bounds.Right:0},{bounds.Bottom:0} px, its box at {boxBounds.Right:0},{boxBounds.Bottom:0} px.");
                var need = new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentCulture, text.FlowDirection,
                    new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, System.Windows.Media.Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip)
                { MaxTextWidth = Math.Max(1, text.ActualWidth + 1) };
                Assert.True(need.Height <= text.ActualHeight + 0.5, $"{where}: the text of {AutomationProperties.GetAutomationId(box)} needs {need.Height:0} px and shows {text.ActualHeight:0} px.");
            }
        }
        return count;
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void EveryCheckBoxOfTheOptionsTabShowsItsWholeText(ApplicationTheme theme, double width, double height)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            ShowTab(window, viewModel, VM.OptionsTab);
            var scroller = ById<ScrollViewer>(page, "scrollOptionsTab");
            Assert.True(AssertCheckBoxTextFits(window, scroller, $"options at {width}x{height}") >= 1);
            Assert.Equal(SiteHygieneLabels.Areas.Count, Descendants(scroller).OfType<CheckBox>().Count(b => AutomationProperties.GetAutomationId(b).StartsWith("chkArea", StringComparison.Ordinal)));
        }, width, height);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ThePickerOfFiveHundredFindingsScrollsItsListOpensOnTheFirstFindingAndReachesContinueInThreeTabs(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var h = SiteHygienePageHarness.Large(500, LongText);
            var picker = new HygieneFindingsPickerViewModel("Delete application", VM.PickIntro(HygieneFixKind.RemoveApplication), h.Service.Findings);
            var window = new HygieneFindingsPickerWindow(picker);
            Show(window);
            try
            {
                Settle(window);
                Assert.Equal("DataGridCell in gridHygienePick", FocusedId());
                Assert.Equal("btnHygienePickCheckShown", NextFocus(window));
                Assert.Equal("btnHygienePickClear", NextFocus(window));
                Assert.Equal("btnHygienePickContinue", NextFocus(window));

                window.Height = Math.Min(728, window.MaxHeight);
                Settle(window);
                Assert.True(window.ActualHeight <= 728 && window.ActualWidth <= 1366, $"The picker is {window.ActualWidth:0}x{window.ActualHeight:0} px.");
                foreach (var id in new[] { "btnHygienePickContinue", "btnHygienePickCancel", "btnHygienePickCheckShown", "btnHygienePickClear", "txtHygienePickFilter" })
                    AssertInside(window, ById<FrameworkElement>(window, id), window.ActualHeight, id);
                var grid = ById<DataGrid>(window, "gridHygienePick");
                Assert.Equal(500, grid.Items.Count);
                Assert.InRange(Descendants(grid).OfType<DataGridRow>().Count(), 1, 80);
                AssertResizableReadableColumns(grid);
                AssertNoSidewaysScroll(grid, "gridHygienePick");
                AssertReadableRowsAndVisibleSelection(window, grid, $"gridHygienePick-{theme}");

                grid.SelectedIndex = 0;
                Assert.True(Keyboard.Focus(Descendants(Row(grid, 0)).OfType<DataGridCell>().Last()) is not null);
                Settle(window);
                Press(window, Key.Space);
                Assert.Equal(499, picker.CheckedCount);
                Press(window, Key.Space);
                Assert.Equal(500, picker.CheckedCount);

                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                Settle(window);
                AssertInside(window, ById<FrameworkElement>(window, "btnHygienePickContinue"), window.ActualHeight, "btnHygienePickContinue at the minimum size");
            }
            finally { window.Close(); }
        });
    }

    private static SiteHygienePlan DeletePlan(int count)
    {
        var h = SiteHygienePageHarness.Large(0, LongText);
        var findings = Enumerable.Range(0, count).Select(i => SiteHygienePageHarness.Finding("APP-01", "Applications", HygieneSeverity.Medium, "Application", $"{16_777_300 + i}", $"{LongText} {i}",
            HygieneFixKind.RemoveApplication)).ToList();
        var prepared = h.Service.PrepareAsync(new HygieneChange(HygieneFixKind.RemoveApplication, findings, false, [], [], null), CancellationToken.None).Result;
        prepared = prepared with { Checks = prepared.Checks.Where(c => c.Name != "Cap").ToList() };
        var (plan, problem) = SiteHygieneText.BuildPlan(prepared);
        Assert.Null(problem);
        return plan!;
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheDeletePlanOpensOnTheFirstLineReachesConfirmAfterItsBoxesAndFitsA1366By768WorkArea(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var plan = DeletePlan(500);
            var dialog = new ChangePlanViewModel(plan.Plan, (_, _, _) => Task.FromResult(new ChangePlanResult([])));
            var window = new ChangePlanWindow(dialog);
            Show(window);
            try
            {
                Settle(window);
                Assert.Equal("DataGridCell in gridPlan", FocusedId());
                Assert.Equal("chkChangePlanDestructive", NextFocus(window));
                Assert.Equal("chkChangePlanExtra", NextFocus(window));
                dialog.DestructiveAcknowledged = true;
                Assert.False(dialog.ConfirmCommand.CanExecute(null));
                dialog.ExtraAcknowledged = true;
                Assert.True(dialog.ConfirmCommand.CanExecute(null));
                Settle(window);
                Assert.Equal("btnChangePlanConfirm", NextFocus(window));

                Assert.True(window.ActualHeight <= 728 && window.ActualWidth <= 1366, $"The plan window is {window.ActualWidth:0}x{window.ActualHeight:0} px.");
                foreach (var id in new[] { "chkDestructive", "chkExtra", "btnConfirm", "btnCancel", "txtNotes", "txtBody" })
                    AssertInside(window, (FrameworkElement)window.FindName(id), window.ActualHeight, id);
                Assert.Equal(SiteHygieneText.DeleteConfirmation(500), ((System.Windows.Controls.TextBlock)((CheckBox)window.FindName("chkExtra")).Content).Text);
                var grid = (DataGrid)window.FindName("gridPlan");
                Assert.Equal(500, grid.Items.Count);
                Assert.InRange(Descendants(grid).OfType<DataGridRow>().Count(), 1, 60);
                window.Width = window.MinWidth;
                Settle(window);
                Assert.True(AssertCheckBoxTextFits(window, window, "plan window at its minimum width") >= 2);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheSuppressCommentWindowOpensOnTheCommentBox(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var dialog = new PivotsCommentViewModel("Suppress findings", "Why are these 2 findings suppressed?");
            var window = new PivotsCommentWindow(dialog);
            Show(window);
            try
            {
                Settle(window);
                Assert.Equal("txtComment", FocusedId());
                Assert.False(dialog.OkCommand.CanExecute(null));
            }
            finally { window.Close(); }
        });
    }
}
