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
using VM = ApplicationPackagerUI.ViewModels.Pages.DpContentManagerViewModel;

namespace ApplicationPackagerEngine.Tests.DpContent;

/// <summary>The DP Content Manager page and its dialogs in real windows, over the fake service, in both themes.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PageRenderTests
{
    private const double NavigationPaneWidth = 230;
    private const double CollapsedPaneWidth = 48;
    private const string LongText = "distribution-point-of-the-branch-office-in-the-north-west-region-building-seven.contoso.example";

    private static readonly (int Tab, string Grid)[] AllGrids =
    [
        (VM.DpsTab, "dgDps"), (VM.DpsTab, "dgDpPairs"), (VM.ContentTab, "dgContent"), (VM.ContentTab, "dgContentPairs"),
        (VM.IssuesTab, "dgIssues"), (VM.HistoryTab, "dgDpHistory"), (VM.IssuesTab, "dgDpContentResults")
    ];

    private static DpContentPageHarness LongHarness(int dps = 6, int contents = 5)
    {
        var h = DpContentPageHarness.Large(dps, contents, LongText);
        for (var i = 0; i < 6; i++)
            h.Audit.Records.Add(new DpContentAuditRecord(new DateTime(2026, 10, 23, 8, i, 0), @"LAB\administrator-with-a-long-name", i % 2 == 0 ? "Remove" : "DistributeAgain", "Finished",
                i % 3 == 0 ? "Partial" : "Success", "run-" + i, 1,
                Enumerable.Range(1, 12).Select(n => new DpContentAuditPair($"LAB{n:00000}", "App " + n, "DP001." + LongText, "[\"Display=\\\\DP001\\\\\"]", 0, n == 1 ? "Failed" : n == 2 ? "Skipped" : "Confirmed")).ToList(),
                "Removed the content of " + LongText + ".") { Verified = i != 2, HasChain = true });
        return h;
    }

    private static void WithPage(ApplicationTheme theme, Action<Window, DpContentManagerPage, VM, DpContentPageHarness> test,
        double width = 1440 - NavigationPaneWidth, double height = 900 - 48, Func<DpContentPageHarness>? harness = null)
    {
        WithApplication(theme, () =>
        {
            var h = (harness ?? (() => LongHarness()))();
            var viewModel = h.Create();
            var page = new DpContentManagerPage(viewModel);
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

    /// <summary>Selects a DP and a content and runs one redistribution of two issues, so every grid has rows.</summary>
    private static void Fill(Window window, VM viewModel)
    {
        viewModel.SelectedDp = viewModel.DpRows[1];
        viewModel.SelectedContent = viewModel.ContentRows[1];
        viewModel.SetSelectedIssues(viewModel.IssueRows.Take(2));
        Run(viewModel.RedistributeIssuesCommand.ExecuteAsync(null));
        viewModel.SelectedDp = viewModel.DpRows[1];
        viewModel.SelectedContent = viewModel.ContentRows[1];
        Settle(window);
    }

    private static void ShowTab(Window window, VM viewModel, int tab)
    {
        viewModel.SelectedTabIndex = tab;
        Run(viewModel.EnsureTabLoadedAsync());
        Settle(window);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheFourTabsRenderAndEveryGridVirtualizesThirtyThousandRows(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Assert.Equal(["DPs", "Content", "Status Issues", "History"], ById<TabControl>(page, "tabDpContent").Items.OfType<TabItem>().Select(t => (string)t.Header));
            viewModel.SelectedDp = viewModel.DpRows[0];
            viewModel.SelectedContent = viewModel.ContentRows[0];
            Settle(window);
            void Virtualized(string id, int rows)
            {
                var grid = ById<DataGrid>(page, id);
                Assert.Equal(rows, grid.Items.Count);
                var realized = Descendants(grid).OfType<DataGridRow>().Count();
                Assert.InRange(realized, 1, 100);
            }
            Assert.Equal(30_000, harness.Service.Pairs.Count);
            Virtualized("dgDps", 300);
            Virtualized("dgDpPairs", 100);
            ShowTab(window, viewModel, VM.ContentTab);
            Virtualized("dgContent", 100);
            Virtualized("dgContentPairs", 300);
            ShowTab(window, viewModel, VM.IssuesTab);
            Virtualized("dgIssues", 6_000);
            viewModel.SetSelectedIssues(viewModel.IssueRows);
            Settle(window);
            Assert.Equal(6_000, ById<DataGrid>(page, "dgIssues").SelectedItems.Count);
            Assert.True(ById<FrameworkElement>(page, "txtDpContentSummary").IsVisible);
            Assert.Contains(DpContentLabels.Count(30_000, "pair"), viewModel.LoadSummary, StringComparison.Ordinal);
        }, harness: () => LongHarness(300, 100));
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
            foreach (var (tab, id) in AllGrids)
            {
                ShowTab(window, viewModel, tab);
                var grid = ById<DataGrid>(page, id);
                grid.BringIntoView();
                Settle(window);
                AssertReadableRowsAndVisibleSelection(window, grid, $"{id}-{theme}");
            }
        });
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

            Check(VM.DpsTab, "scrollDpsTab", "txtDpFilter", "cboDpStatus", "txtDpCount", "dgDps", "txtDpDetail", "dgDpPairs");
            Check(VM.ContentTab, "scrollContentTab", "txtContentFilter", "cboContentType", "btnRedistributeFailed", "btnRedistributeSelectedDps", "btnRemoveFromDp", "btnValidate",
                "dgContent", "txtContentDetail", "dgContentPairs");
            Check(VM.IssuesTab, "scrollIssuesTab", "txtIssueFilter", "cboIssueState", "btnRedistributeIssues", "btnRemoveFailures", "dgIssues");
            Check(VM.HistoryTab, "scrollDpHistoryTab", "txtDpHistoryFilter", "cboDpHistoryAction", "btnDpHistoryRefresh", "txtDpHistoryPath", "dgDpHistory");
            foreach (var id in new[] { "btnDpContentRefresh", "btnDpContentCancel", "btnDpContentCsv", "btnDpContentHtml", "btnDpContentFolder", "txtDpContentIntro", "btnDpContentUndo",
                         "chkDpContentResultRows", "dgDpContentResults" })
            {
                var element = ById<FrameworkElement>(page, id);
                if (!element.IsVisible) continue;
                var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
                Assert.True(bounds.Right <= window.ActualWidth + 0.5 && bounds.Bottom <= window.ActualHeight + 0.5,
                    $"{id} ends at {bounds.Right:0},{bounds.Bottom:0} px in a {window.ActualWidth:0}x{window.ActualHeight:0} px window.");
            }
            var tabs = ById<TabControl>(page, "tabDpContent");
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
    public void TabReachesEveryGridAndTheFocusedRowIsSelected(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel);
            viewModel.SelectedDp = null;
            Settle(window);
            var dps = TabInto(window, ById<UIElement>(page, "txtDpFilter"), ById<DataGrid>(page, "dgDps"));
            Assert.Same(dps.Item, viewModel.SelectedDp);
            var next = viewModel.DpRows[viewModel.DpRows.ToList().IndexOf((DpGridRow)dps.Item) + 1];
            Press(window, Key.Down);
            Assert.Same(next, viewModel.SelectedDp);
            Assert.True(TabInto(window, ById<UIElement>(page, "cboDpStatus"), ById<DataGrid>(page, "dgDpPairs")).IsSelected);

            ShowTab(window, viewModel, VM.ContentTab);
            viewModel.SelectedContent = null;
            Settle(window);
            var content = TabInto(window, ById<UIElement>(page, "cboContentType"), ById<DataGrid>(page, "dgContent"));
            Assert.Same(content.Item, viewModel.SelectedContent);
            Settle(window);
            var pair = TabInto(window, ById<UIElement>(page, "cboContentType"), ById<DataGrid>(page, "dgContentPairs"));
            Assert.True(pair.IsSelected);
            Assert.Equal([((PairGridRow)pair.Item).Key], viewModel.SelectedContentPairs.Select(r => r.Key));

            ShowTab(window, viewModel, VM.IssuesTab);
            viewModel.SetSelectedIssues([]);
            Settle(window);
            var issue = TabInto(window, ById<UIElement>(page, "cboIssueState"), ById<DataGrid>(page, "dgIssues"));
            Assert.True(issue.IsSelected);
            Assert.Single(viewModel.SelectedIssues);
            Assert.True(viewModel.RedistributeIssuesCommand.CanExecute(null));

            Assert.True(TabInto(window, ById<UIElement>(page, "chkDpContentResultRows"), ById<DataGrid>(page, "dgDpContentResults")).IsSelected);

            ShowTab(window, viewModel, VM.HistoryTab);
            var record = TabInto(window, ById<UIElement>(page, "btnDpHistoryRefresh"), ById<DataGrid>(page, "dgDpHistory"));
            Assert.Same(record.Item, viewModel.SelectedHistory);
            Assert.All(AllGrids.Select(g => (DataGrid)page.FindName(g.Grid)), g => Assert.True(ApplicationPackagerUI.Helpers.DataGridRightClick.GetSelectsRow(g)));
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void FromThePerDpGridsOneOrTwoTabsReachTheActions(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel);
            ShowTab(window, viewModel, VM.ContentTab);
            Assert.All(AllGrids.Select(g => (DataGrid)page.FindName(g.Grid)), g => Assert.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(g)));
            TabInto(window, ById<UIElement>(page, "cboContentType"), ById<DataGrid>(page, "dgContentPairs"));
            string Next()
            {
                ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Settle(window);
                return Keyboard.FocusedElement is FrameworkElement f ? AutomationProperties.GetAutomationId(f) is { Length: > 0 } id ? id : f.GetType().Name + " in " + (Ancestor<DataGrid>(f)?.Name ?? "-") : "";
            }
            Assert.Equal("btnRedistributeFailed", Next());
            Assert.Equal("btnRedistributeSelectedDps", Next());

            ShowTab(window, viewModel, VM.IssuesTab);
            TabInto(window, ById<UIElement>(page, "cboIssueState"), ById<DataGrid>(page, "dgIssues"));
            Assert.Equal("btnRedistributeIssues", Next());
        });
    }

    /// <summary>The longest values the columns hold on a real site, by grid and column header.</summary>
    private static readonly (string Grid, string Column, string Text)[] LongValues =
    [
        ("dgIssues", "State", "Failed (content updating)"), ("dgIssues", "Type", "Software update package"), ("dgIssues", "Last status", "12/30/2026 11:59 PM"),
        ("dgContentPairs", "State", "Failed (content updating)"), ("dgContentPairs", "Last status", "12/30/2026 11:59 PM"),
        ("dgDpPairs", "State", "Failed (content updating)"), ("dgDpPairs", "Type", "Software update package"), ("dgDpPairs", "Last status", "12/30/2026 11:59 PM"),
        ("dgContent", "Type", "Software update package"),
        ("dgDpHistory", "Time", "12/30/2026 11:59 PM"), ("dgDpHistory", "Result", "Succeeded"), ("dgDpHistory", "Result", "Requested"), ("dgDpHistory", "User", @"CONTOSO\labadmin"), ("dgDpHistory", "DPs", "CM01.CONTOSO.COM")
    ];

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheLongestRealValuesFitTheirColumnsAt1440PixelsWithTheSidebarCollapsed(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel);
            foreach (var (tab, id) in AllGrids)
            {
                ShowTab(window, viewModel, tab);
                var grid = ById<DataGrid>(page, id);
                if (id == "dgDpHistory")
                {
                    var message = grid.Columns.Single(c => Equals(c.Header, "Message"));
                    var block = Descendants(Descendants(grid).OfType<DataGridCell>().First(c => ReferenceEquals(c.Column, message))).OfType<TextBlock>().First();
                    Assert.Equal(TextWrapping.Wrap, block.TextWrapping);
                    Assert.True(block.ActualHeight >= block.DesiredSize.Height - 0.5, "The message is cut in height.");
                }
                foreach (var (_, header, text) in LongValues.Where(v => v.Grid == id))
                {
                    var column = grid.Columns.Single(c => Equals(c.Header, header));
                    var cell = Descendants(grid).OfType<DataGridCell>().First(c => ReferenceEquals(c.Column, column));
                    var block = Descendants(cell).OfType<TextBlock>().First();
                    var padding = cell.ActualWidth - block.ActualWidth;
                    var needed = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
                        new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, System.Windows.Media.Brushes.Black, VisualTreeHelper.GetDpi(block).PixelsPerDip).WidthIncludingTrailingWhitespace;
                    Assert.True(needed + padding <= column.ActualWidth + 0.5, $"{id} {header}: \"{text}\" needs {needed + padding:0} px and the column has {column.ActualWidth:0} px.");
                }
            }
        }, 1440 - CollapsedPaneWidth);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void AClickOnAColumnHeaderSortsThroughThePageAndShowsTheDirection(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            var grid = ById<DataGrid>(page, "dgDps");
            var failed = grid.Columns.Single(c => Equals(c.Header, "Failed"));
            var header = Descendants(grid).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>().Single(h => ReferenceEquals(h.Column, failed));
            typeof(System.Windows.Controls.Primitives.DataGridColumnHeader).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(header, null);
            Settle(window);
            Assert.Equal(("Failed", true), viewModel.SortOf(VM.DpsGrid));
            Assert.Equal(System.ComponentModel.ListSortDirection.Ascending, failed.SortDirection);
            Run(viewModel.RefreshCommand.ExecuteAsync(null));
            Settle(window);
            Assert.Equal(System.ComponentModel.ListSortDirection.Ascending, failed.SortDirection);
            Assert.True(viewModel.DpRows.Zip(viewModel.DpRows.Skip(1)).All(p => p.First.Dp.Failed <= p.Second.Dp.Failed));
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
                Assert.True(boxBounds.Right <= window.ActualWidth + 0.5, $"{where}: {AutomationProperties.GetAutomationId(box)} ends at {boxBounds.Right:0} px in a {window.ActualWidth:0} px window.");
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
    public void EveryCheckBoxOfThePageShowsItsWholeText(ApplicationTheme theme, double width, double height)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel);
            Assert.True(AssertCheckBoxTextFits(window, page, $"page at {width}x{height}") >= 1);
        }, width, height);
    }

    /// <summary>A removal of <paramref name="pairs"/> pairs that leaves contents with no DP: the plan has all three boxes.</summary>
    private static DpContentPlan RemovalPlan(int pairs)
    {
        var h = DpContentPageHarness.Large(pairs / 5, 5, LongText);
        var all = h.Service.Pairs.ToList();
        var prepared = h.Service.PrepareAsync(new DpContentChange(DpContentChangeKind.Remove, all, false, false, [], [], null), CancellationToken.None).Result;
        var (plan, problem) = DpContentText.BuildPlan(prepared, new DpContentIndex(h.Service.Snapshot()));
        Assert.Null(problem);
        Assert.Equal(pairs, plan!.Plan.Items.Count);
        Assert.NotNull(plan.Plan.ExtraConfirmation);
        Assert.NotNull(plan.Plan.SecondConfirmation);
        return plan;
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ThePlanWindowOfFiveHundredPairsKeepsItsBoxesAndButtonsInsideA1366By768WorkArea(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var plan = RemovalPlan(500);
            var dialog = new ChangePlanViewModel(plan.Plan, (_, _, _) => Task.FromResult(new ChangePlanResult([])));
            var window = new ChangePlanWindow(dialog);
            Show(window);
            try
            {
                Assert.True(window.ActualHeight <= 728 && window.ActualWidth <= 1366, $"The plan window is {window.ActualWidth:0}x{window.ActualHeight:0} px.");
                foreach (var id in new[] { "chkDestructive", "chkExtra", "chkSecond", "btnConfirm", "btnCancel", "txtNotes" })
                    AssertInside(window, (FrameworkElement)window.FindName(id), window.ActualHeight, id);
                var grid = (DataGrid)window.FindName("gridPlan");
                Assert.Equal(500, grid.Items.Count);
                var scroller = Descendants(grid).OfType<ScrollViewer>().First();
                Assert.True(scroller.ExtentHeight > scroller.ViewportHeight);
                Assert.InRange(Descendants(grid).OfType<DataGridRow>().Count(), 1, 60);
                Assert.False(dialog.ConfirmCommand.CanExecute(null));
                dialog.DestructiveAcknowledged = true;
                dialog.ExtraAcknowledged = true;
                Assert.False(dialog.ConfirmCommand.CanExecute(null));
                dialog.SecondAcknowledged = true;
                Assert.True(dialog.ConfirmCommand.CanExecute(null));

                window.Width = window.MinWidth;
                Settle(window);
                Assert.True(AssertCheckBoxTextFits(window, window, "plan window at its minimum width") >= 3);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ThePairWindowOfFiveHundredPairsScrollsItsListAndKeepsItsButtonsInsideA1366By768WorkArea(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var h = DpContentPageHarness.Large(100, 5, LongText);
            var picker = new DpContentPairsViewModel("Remove from DP", VM.PickIntro(DpContentChangeKind.Remove), h.Service.Pairs.ToList());
            var window = new DpContentPairsWindow(picker);
            Show(window);
            try
            {
                window.Height = Math.Min(728, window.MaxHeight);
                Settle(window);
                Assert.True(window.ActualHeight <= 728 && window.ActualWidth <= 1366, $"The pair window is {window.ActualWidth:0}x{window.ActualHeight:0} px.");
                foreach (var id in new[] { "btnPairsContinue", "btnPairsCancel", "btnPairsCheckShown", "btnPairsClear", "txtPairsFilter" })
                    AssertInside(window, ById<FrameworkElement>(window, id), window.ActualHeight, id);
                var grid = ById<DataGrid>(window, "gridPairs");
                Assert.Equal(500, grid.Items.Count);
                Assert.InRange(Descendants(grid).OfType<DataGridRow>().Count(), 1, 80);
                AssertResizableReadableColumns(grid);
                AssertNoSidewaysScroll(grid, "gridPairs");
                AssertReadableRowsAndVisibleSelection(window, grid, $"gridPairs-{theme}");

                var box = Descendants(Row(grid, 0)).OfType<CheckBox>().Single();
                var cell = Ancestor<DataGridCell>(box)!;
                var cellBounds = cell.TransformToAncestor(window).TransformBounds(new Rect(cell.RenderSize));
                var boxBounds = box.TransformToAncestor(window).TransformBounds(new Rect(box.RenderSize));
                Assert.True(boxBounds.Left >= cellBounds.Left - 0.5 && boxBounds.Right <= cellBounds.Right + 0.5, $"The check box spans {boxBounds.Left:0} to {boxBounds.Right:0} px in a cell from {cellBounds.Left:0} to {cellBounds.Right:0} px.");
                Assert.True(box.DesiredSize.Width <= box.ActualWidth + 0.5, $"The check box needs {box.DesiredSize.Width:0} px and has {box.ActualWidth:0} px.");

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
                AssertInside(window, ById<FrameworkElement>(window, "btnPairsContinue"), window.ActualHeight, "btnPairsContinue at the minimum size");
            }
            finally { window.Close(); }
        });
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

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ThePairWindowOpensOnTheFirstPairAndReachesContinueInThreeTabs(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var h = new DpContentPageHarness();
            var picker = new DpContentPairsViewModel("Redistribute selected DPs", VM.PickIntro(DpContentChangeKind.Redistribute), [h.SevenZipOnDp02]);
            var window = new DpContentPairsWindow(picker);
            Show(window);
            try
            {
                Settle(window);
                Assert.Equal("DataGridCell in gridPairs", FocusedId());
                Assert.Equal("btnPairsCheckShown", NextFocus(window));
                Assert.Equal("btnPairsClear", NextFocus(window));
                Assert.Equal("btnPairsContinue", NextFocus(window));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ThePlanWindowOpensOnTheFirstLineAndReachesConfirmAfterItsBoxes(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var h = new DpContentPageHarness();
            var prepared = h.Service.PrepareAsync(new DpContentChange(DpContentChangeKind.Redistribute, [h.SevenZipOnDp02], false, false, [], [], null), CancellationToken.None).Result;
            var (plain, _) = DpContentText.BuildPlan(prepared, new DpContentIndex(h.Service.Snapshot()));
            var window = new ChangePlanWindow(new ChangePlanViewModel(plain!.Plan, (_, _, _) => Task.FromResult(new ChangePlanResult([]))));
            Show(window);
            try
            {
                Settle(window);
                Assert.Equal("DataGridCell in gridPlan", FocusedId());
                Assert.Equal("btnChangePlanConfirm", NextFocus(window));
            }
            finally { window.Close(); }

            var removal = RemovalPlan(10);
            var dialog = new ChangePlanViewModel(removal.Plan, (_, _, _) => Task.FromResult(new ChangePlanResult([])));
            window = new ChangePlanWindow(dialog);
            Show(window);
            try
            {
                Settle(window);
                Assert.Equal("DataGridCell in gridPlan", FocusedId());
                Assert.Equal("chkChangePlanDestructive", NextFocus(window));
                Assert.Equal("chkChangePlanExtra", NextFocus(window));
                Assert.Equal("chkChangePlanSecond", NextFocus(window));
                dialog.DestructiveAcknowledged = dialog.ExtraAcknowledged = dialog.SecondAcknowledged = true;
                Settle(window);
                Assert.Equal("btnChangePlanConfirm", NextFocus(window));
            }
            finally { window.Close(); }
        });
    }
}