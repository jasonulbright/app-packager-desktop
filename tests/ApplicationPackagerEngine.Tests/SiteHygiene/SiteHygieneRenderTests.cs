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

    private const string ToolTipStylesSource = "/ApplicationPackagerUI;component/Views/Styles/ToolTips.xaml";

    private static readonly (int Tab, string Grid)[] AllGrids =
    [
        (VM.FindingsTab, "dgFindings"), (VM.RelationsTab, "dgEdges"), (VM.SummaryTab, "dgCategories"), (VM.SummaryTab, "dgChecks"), (VM.FindingsTab, "dgHygieneResults"),
        (VM.HistoryTab, "dgHygieneHistory")
    ];

    private static SiteHygienePageHarness LongHarness(int findings = 60)
    {
        var h = SiteHygienePageHarness.Large(findings, LongText);
        h.AuditLog = FakeHygieneAudit.Long(40, LongText);
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

    /// <summary>Scans, reads the relationships, runs one change fix and opens the result card, so every grid and the result card have rows.</summary>
    private static void Fill(Window window, VM viewModel)
    {
        Run(viewModel.ScanAsync());
        Run(viewModel.LoadRelationsAsync());
        Run(viewModel.LoadHistoryAsync());
        viewModel.SelectedHistory = viewModel.HistoryRows[0];
        viewModel.SetSelectedFindings([viewModel.FindingRows.First(r => r.Finding.FixKind == HygieneFixKind.SetCollectionRefreshType)]);
        Run(viewModel.RunFixCommand.ExecuteAsync(null));
        Assert.False(viewModel.ResultsExpanded);
        viewModel.ResultsExpanded = true;
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
            var headers = ById<TabControl>(page, "tabHygiene").Items.OfType<TabItem>().Select(t => (string)t.Header).ToList();
            Assert.Equal(["Findings", "Relationships", "Summary", "Options", "History"], headers);
            Assert.All(VM.ExportTabs, t => Assert.Equal(headers[t.Index], t.Name));
            Assert.Equal(headers.Where(h => h != headers[VM.OptionsTab]), VM.ExportTabs.Select(t => t.Name));
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
            foreach (var tab in new[] { VM.RelationsTab, VM.SummaryTab, VM.OptionsTab, VM.HistoryTab })
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
            yield return ("dgFindings", "Fix", SiteHygieneLabels.FixColumn(kind));
            yield return ("dgHygieneResults", "Fix", SiteHygieneLabels.FixLabel(kind));
        }
        foreach (var result in new[] { "Done", "Partial", "Unchanged", "Failed", "Skipped" }) yield return ("dgHygieneResults", "Result", result);
        yield return ("dgEdges", "Kind", "Depends on");
        yield return ("dgEdges", "Kind", "Supersedes");
        foreach (var header in new[] { "Error", "Warning", "Info", "Total" })
        {
            yield return ("dgCategories", header, "Not known");
            yield return ("dgCategories", header, "123,456");
            yield return ("dgCategories", header, "At least 123,456");
        }
        yield return ("dgCategories", "Checks not run", "57");
        yield return ("dgChecks", "Check", "COL-10");
        foreach (var state in new[] { "Ran", "NotRun", "NotSelected", "NeedsSiteDatabase", "Failed" }) yield return ("dgChecks", "State", SiteHygieneLabels.StateText(state));
        yield return ("dgChecks", "Findings", "Not known");
        yield return ("dgHygieneHistory", "Time", new DateTime(2026, 12, 28, 23, 59, 59).ToString("g", System.Globalization.CultureInfo.CurrentCulture));
        foreach (var kind in Enum.GetValues<HygieneFixKind>()) yield return ("dgHygieneHistory", "Action", SiteHygieneLabels.FixLabel(kind));
        foreach (var result in new[] { "Succeeded", "Partial", "Failed", "Skipped", "Started", "Objects", "Finished" }) yield return ("dgHygieneHistory", "Result", result);
        yield return ("dgHygieneHistory", "Site", "MCM");
        yield return ("dgHygieneHistory", "Verified", "✓ Verified");
        yield return ("dgHygieneHistory", "Verified", "! Not verified");
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
            Check(VM.HistoryTab, "scrollHygieneHistoryTab", "txtHygieneHistoryFilter", "btnHygieneHistoryRefresh", "txtHygieneHistoryCount", "dgHygieneHistory", "txtHygieneHistoryDetail");
            Check(VM.OptionsTab, "scrollOptionsTab", "btnAreasAll", "btnAreasClear", "lstAreas", "cboPause", "chkProbeSources", "txtReportFolder", "btnOptionsFolder");
            Assert.False(ById<FrameworkElement>(page, "pnlHygieneResults").IsVisible, "The result card takes the height of the Options tab.");
            ShowTab(window, viewModel, VM.FindingsTab);
            foreach (var id in new[] { "btnHygieneScan", "btnHygieneCancel", "btnHygieneLiveViews", "btnHygieneCsv", "btnHygieneHtml", "btnHygieneFolder", "txtHygieneSummary", "txtHygieneAreas",
                         "dgHygieneResults", "txtHygieneStatus" })
            {
                var element = ById<FrameworkElement>(page, id);
                Assert.True(element.IsVisible, $"{id} is not visible.");
                var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
                Assert.True(bounds.Right <= window.ActualWidth + 0.5 && bounds.Bottom <= window.ActualHeight + 0.5,
                    $"{id} ends at {bounds.Right:0},{bounds.Bottom:0} px in a {window.ActualWidth:0}x{window.ActualHeight:0} px window.");
            }
            // The run leaves the card collapsed to one line: the tabs keep a usable height at every size.
            viewModel.ResultsExpanded = false;
            Settle(window);
            var tabs = ById<TabControl>(page, "tabHygiene");
            Assert.True(tabs.ActualHeight >= 180, $"The tabs get {tabs.ActualHeight:0} px at {width}x{height}; the result card takes {ById<FrameworkElement>(page, "pnlHygieneResults").ActualHeight:0} px.");
        }, width, height);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void TheMissingAuditRecordWarningShowsWithTheResultsAtEverySize(ApplicationTheme theme, double width, double height)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel);
            Assert.True(viewModel.HasAuditWarning);
            foreach (var id in new[] { "pnlHygieneAuditWarning", "txtHygieneAuditWarning", "dgHygieneResults", "txtHygieneStatus", "btnHygieneScan" })
            {
                var element = ById<FrameworkElement>(page, id);
                Assert.True(element.IsVisible, $"{id} is not visible at {width}x{height}.");
                var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
                Assert.True(bounds.Left >= -0.5 && bounds.Right <= window.ActualWidth + 0.5 && bounds.Bottom <= window.ActualHeight + 0.5,
                    $"{id} spans {bounds.Left:0}-{bounds.Right:0},{bounds.Bottom:0} px in a {window.ActualWidth:0}x{window.ActualHeight:0} px window.");
            }
            var text = ById<TextBlock>(page, "txtHygieneAuditWarning");
            Assert.Equal(viewModel.AuditWarning, text.ToolTip);
            Assert.Equal(viewModel.AuditWarning, AutomationProperties.GetName(text));
            Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
            Assert.False(ById<FrameworkElement>(page, "txtHygieneResultSummary").IsVisible, $"The result summary and the audit warning both take a line at {width}x{height}.");

            // On the other tabs the warning stays and the result rows leave the height to the tab.
            ShowTab(window, viewModel, VM.HistoryTab);
            Assert.True(ById<FrameworkElement>(page, "txtHygieneAuditWarning").IsVisible, $"The audit warning is hidden on the History tab at {width}x{height}.");
            Assert.False(((FrameworkElement)page.FindName("dgHygieneResults")).IsVisible, $"The result rows take the height of the History tab at {width}x{height}.");

            // Collapsed, the card keeps the warning line and the tabs keep a usable height.
            ShowTab(window, viewModel, VM.FindingsTab);
            viewModel.ResultsExpanded = false;
            Settle(window);
            Assert.True(ById<FrameworkElement>(page, "txtHygieneAuditWarning").IsVisible, $"The audit warning is hidden in the collapsed card at {width}x{height}.");
            Assert.True(ById<TabControl>(page, "tabHygiene").ActualHeight >= 180, $"The tabs get {ById<TabControl>(page, "tabHygiene").ActualHeight:0} px at {width}x{height}.");
        }, width, height, () =>
        {
            var h = LongHarness();
            h.Service.AuditIncomplete = true;
            return h;
        });
    }

    /// <summary>A Finished record as a fix run of the page writes it: a run ID of 32 hex digits and one line per collection.</summary>
    private static HygieneAuditRecord RunRecord(int objects, int index) =>
        new(new DateTime(2026, 10, 25, 1, 15, 59).AddMinutes(-index), @"CONTOSO\labadmin", nameof(HygieneFixKind.RemoveCollection), HygieneAuditPhases.Finished, "Succeeded",
            $"{index:x2}5f0c3a9be14d2c8f7a6e1d0b3c4a59", Enumerable.Range(0, objects).Select(n => new HygieneAuditObject("COL-01", "Collection", $"MCM002{50 + n:X2}",
                $"Contoso Finance Workstations Pilot Wave {n + 1:00}", "Done")).ToList(), $"{objects} objects: {objects} done, 0 failed, 0 skipped.")
        {
            Site = "MCM",
            Provider = "cm01.contoso.com",
            Verified = true,
            HasChain = true
        };

    public static TheoryData<ApplicationTheme, double, double, int> HistoryDetailCases()
    {
        var data = new TheoryData<ApplicationTheme, double, double, int>();
        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
        foreach (var (width, height) in new[] { (1440 - CollapsedPaneWidth, 900 - 48), (1440 - NavigationPaneWidth, 900 - 48), (900 - CollapsedPaneWidth, 600 - 48), (900 - NavigationPaneWidth, 600 - 48) })
        foreach (var objects in new[] { 2, HygieneHistoryRow.DetailObjects, HygieneAuditLog.MaxObjectsPerRecord })
            data.Add(theme, width, height, objects);
        return data;
    }

    [Theory]
    [MemberData(nameof(HistoryDetailCases))]
    public void TheHistoryDetailShowsEveryObjectLineUpToItsLimitAndAVisibleScrollBarBeyondIt(ApplicationTheme theme, double width, double height, int objects)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Run(viewModel.LoadHistoryAsync());
            ShowTab(window, viewModel, VM.HistoryTab);
            viewModel.SelectedHistory = viewModel.HistoryRows.Single(r => r.Record.Objects.Count == objects);
            Settle(window);

            var box = ById<System.Windows.Controls.TextBox>(page, "txtHygieneHistoryDetail");
            var panel = ById<FrameworkElement>(page, "pnlHygieneHistoryDetail");
            var tab = ById<ScrollViewer>(page, "scrollHygieneHistoryTab");
            panel.BringIntoView();
            Settle(window);
            AssertReachable(tab, panel, "pnlHygieneHistoryDetail");
            Assert.True(tab.ScrollableWidth < 1, $"The History tab scrolls sideways by {tab.ScrollableWidth:0} px at {width}x{height}.");
            var grid = ById<DataGrid>(page, "dgHygieneHistory");
            Assert.True(grid.ActualHeight >= 159.5, $"The History grid gets {grid.ActualHeight:0} px at {width}x{height}.");

            var text = Descendants(box).OfType<ScrollViewer>().First();
            var cut = text.ScrollableHeight >= 1;
            if (objects <= HygieneHistoryRow.DetailObjects)
                Assert.False(cut, $"The detail of {objects} objects is cut by {text.ScrollableHeight:0} px at {width}x{height}: the box is {text.ViewportHeight:0} px high.");
            if (objects > HygieneHistoryRow.DetailObjects) Assert.True(cut, $"The detail of {objects} objects shows without a scroll bar at {width}x{height}.");
            if (!cut) return;
            var bar = Descendants(text).OfType<System.Windows.Controls.Primitives.ScrollBar>().Single(b => b.Orientation == System.Windows.Controls.Orientation.Vertical);
            var thumb = Descendants(bar).OfType<System.Windows.Controls.Primitives.Thumb>().Single();
            Assert.False(box.IsMouseOver);
            Assert.True(bar.IsVisible && bar.Opacity > 0.99 && thumb.IsVisible && thumb.Opacity > 0.99 && thumb.ActualWidth >= 6,
                $"The scroll bar of the detail is not readable without the pointer at {width}x{height}: bar {bar.GetType().Name} {bar.ActualWidth:0} px, opacity {bar.Opacity:0.00}; thumb {thumb.ActualWidth:0} px, opacity {thumb.Opacity:0.00}.");
            var bounds = bar.TransformToAncestor(box).TransformBounds(new Rect(bar.RenderSize));
            Assert.True(bounds.Left >= -0.5 && bounds.Right <= box.ActualWidth + 0.5, $"The scroll bar spans {bounds.Left:0} to {bounds.Right:0} px in a {box.ActualWidth:0} px box.");
            var back = MostCommon(Pixels(window, box, $"sh-history-detail-{theme}-{width:0}x{height:0}-{objects}"));
            var thumbPixels = Pixels(window, thumb);
            var marked = thumbPixels.Count(p => Contrast(p, back) >= 3) / (double)thumbPixels.Count;
            Assert.True(marked >= 0.2, $"The scroll bar thumb is not readable on the box background {back} at {width}x{height}: {marked:P0} of its pixels have a contrast of 3 or more.");
        }, width, height, () => new SiteHygienePageHarness
        {
            AuditLog = new FakeHygieneAudit([RunRecord(HygieneAuditLog.MaxObjectsPerRecord, 2), RunRecord(HygieneHistoryRow.DetailObjects, 1), RunRecord(2, 0)])
        });
    }

    /// <summary>
    /// After a fix run in the session the result card is on the page. At 1440x900 the History tab must not scroll and the
    /// detail box shows every line up to its limit inside the tab. At 900x600 the tab scrolls; the detail box keeps its full
    /// height of lines and the tab scrolls to it.
    /// </summary>
    [Theory]
    [MemberData(nameof(HistoryDetailCases))]
    public void AfterAFixRunTheHistoryDetailShowsEveryLineUpToItsLimitWithoutAScrollOfTheTab(ApplicationTheme theme, double width, double height, int objects)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel);
            Assert.True(viewModel.HasResults);
            ShowTab(window, viewModel, VM.HistoryTab);
            viewModel.SelectedHistory = viewModel.HistoryRows.Single(r => r.Record.Objects.Count == objects);
            Settle(window);

            var tab = ById<ScrollViewer>(page, "scrollHygieneHistoryTab");
            var box = ById<System.Windows.Controls.TextBox>(page, "txtHygieneHistoryDetail");
            var text = Descendants(box).OfType<ScrollViewer>().First();
            var lineHeight = box.FontFamily.LineSpacing * box.FontSize;
            var wanted = Math.Min(text.ExtentHeight, HygieneHistoryRow.DetailLines * lineHeight);
            Assert.True(text.ViewportHeight >= wanted - 1,
                $"The detail box of {objects} objects is {text.ViewportHeight:0} px high and needs {wanted:0} px ({wanted / lineHeight:0.0} lines) at {width}x{height}.");
            var grid = ById<DataGrid>(page, "dgHygieneHistory");
            Assert.True(grid.ActualHeight >= 159.5, $"The History grid gets {grid.ActualHeight:0} px at {width}x{height}.");

            if (height >= 900 - 48)
            {
                Assert.True(tab.ScrollableHeight < 1, $"The History tab scrolls by {tab.ScrollableHeight:0} px at {width}x{height} after a fix run.");
                var bounds = box.TransformToAncestor(tab).TransformBounds(new Rect(box.RenderSize));
                Assert.True(bounds.Top >= -0.5 && bounds.Bottom <= tab.ViewportHeight + 0.5,
                    $"The detail box spans {bounds.Top:0} to {bounds.Bottom:0} px in a {tab.ViewportHeight:0} px tab at {width}x{height}.");
                return;
            }

            // 900x600: the tab scrolls, and the scroll shows the whole detail box.
            Assert.True(tab.ScrollableHeight >= 1, $"The History tab does not scroll at {width}x{height}; the test does not show the scroll.");
            box.BringIntoView();
            Settle(window);
            var shown = box.TransformToAncestor(tab).TransformBounds(new Rect(box.RenderSize));
            Assert.True(shown.Top >= -0.5 && shown.Bottom <= tab.ViewportHeight + 0.5,
                $"After the scroll the detail box spans {shown.Top:0} to {shown.Bottom:0} px in a {tab.ViewportHeight:0} px tab at {width}x{height}.");
        }, width, height, () =>
        {
            var h = SiteHygienePageHarness.Large(60, LongText);
            h.AuditLog = new FakeHygieneAudit([RunRecord(HygieneAuditLog.MaxObjectsPerRecord, 2), RunRecord(HygieneHistoryRow.DetailObjects, 1), RunRecord(2, 0)]);
            return h;
        });
    }

    /// <summary>The most height the collapsed result card takes below the tabs, its margin included.</summary>
    private const double CollapsedCardHeight = 64;

    public static TheoryData<ApplicationTheme, double, double, int, bool> FixRunCases()
    {
        var data = new TheoryData<ApplicationTheme, double, double, int, bool>();
        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
        foreach (var (width, height) in new[] { (1440 - CollapsedPaneWidth, 900 - 48), (1440 - NavigationPaneWidth, 900 - 48), (900 - NavigationPaneWidth, 600 - 48) })
        foreach (var objects in new[] { 2, 10, 100 })
            data.Add(theme, width, height, objects, objects == 10);
        return data;
    }

    /// <summary>
    /// A site of 60 long findings, the audit records of three runs, the relationships, and <paramref name="objects"/> collection
    /// refresh findings that one fix run changes.
    /// </summary>
    private static SiteHygienePageHarness FixRunHarness(int objects, bool auditIncomplete)
    {
        var h = LongHarness();
        h.AuditLog = new FakeHygieneAudit([RunRecord(HygieneAuditLog.MaxObjectsPerRecord, 2), RunRecord(HygieneHistoryRow.DetailObjects, 1), RunRecord(2, 0)]);
        h.Service.AuditIncomplete = auditIncomplete;
        for (var i = 0; i < objects; i++)
            h.Service.Findings.Add(SiteHygienePageHarness.Finding("COL-05", "Collections", HygieneSeverity.Warning, "Collection", $"MCM003{i:X2}", $"Contoso Finance Workstations Pilot Wave {i + 1:000}",
                HygieneFixKind.SetCollectionRefreshType, $"Set-CMCollection -CollectionId MCM003{i:X2} -RefreshType Continuous", evidence: "Incremental and a full evaluation every 2 hours."));
        return h;
    }

    /// <summary>Scans, reads the relationships and the history, then runs one fix of every collection refresh finding.</summary>
    private static void RunFix(Window window, VM viewModel, int objects)
    {
        Run(viewModel.ScanAsync());
        Run(viewModel.LoadRelationsAsync());
        Run(viewModel.LoadHistoryAsync());
        viewModel.SetSelectedFindings(viewModel.FindingRows.Where(r => r.Finding.FixKind == HygieneFixKind.SetCollectionRefreshType && r.Key.Contains("MCM003", StringComparison.Ordinal)));
        Assert.Equal(objects, viewModel.SelectedFindings.Count);
        Run(viewModel.RunFixCommand.ExecuteAsync(null));
        Settle(window);
        Assert.Equal(objects, viewModel.LastResults.Count);
    }

    /// <summary>The element shows in full inside the visible area of the tab.</summary>
    private static void AssertShown(ScrollViewer tab, FrameworkElement element, string where)
    {
        Assert.True(element.IsVisible, $"{where}: {AutomationProperties.GetAutomationId(element)} is not visible.");
        var bounds = element.TransformToAncestor(tab).TransformBounds(new Rect(element.RenderSize));
        Assert.True(bounds.Top >= -0.5 && bounds.Bottom <= tab.ViewportHeight + 0.5,
            $"{where}: {AutomationProperties.GetAutomationId(element)} spans {bounds.Top:0} to {bounds.Bottom:0} px in a {tab.ViewportHeight:0} px tab.");
    }

    /// <summary>The text block shows its whole text: the wrapped text is not taller than the block and no ellipsis cuts it.</summary>
    private static void AssertWholeText(TextBlock block, string where)
    {
        var need = new FormattedText(block.Text, System.Globalization.CultureInfo.CurrentCulture, block.FlowDirection,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, System.Windows.Media.Brushes.Black, VisualTreeHelper.GetDpi(block).PixelsPerDip)
        { MaxTextWidth = Math.Max(1, block.ActualWidth) };
        if (block.LineStackingStrategy == LineStackingStrategy.BlockLineHeight && block.LineHeight > 0) need.LineHeight = block.LineHeight;
        Assert.True(need.Height <= block.ActualHeight + 0.5, $"{where}: {AutomationProperties.GetAutomationId(block)} needs {need.Height:0} px and shows {block.ActualHeight:0} px.");
    }

    /// <summary>An unselected row without the pointer shows the row or the alternating row background of its grid.</summary>
    private static void AssertRowBackgrounds(DataGrid grid, string where)
    {
        var rows = Descendants(grid).OfType<DataGridRow>().Where(r => r.IsVisible && !r.IsSelected && !r.IsMouseOver).ToList();
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            var border = Assert.IsAssignableFrom<System.Windows.Controls.Border>(row.Template.FindName("DGR_Border", row));
            var wanted = ItemsControl.GetAlternationIndex(row) == 1 && grid.AlternatingRowBackground is not null ? grid.AlternatingRowBackground : grid.RowBackground;
            Assert.True(Equals(wanted, border.Background), $"{where}: row {row.GetIndex()} shows {border.Background} in place of {wanted}.");
        }
    }

    private static readonly string[] DetailParts = ["txtFindingDetailTitle", "txtFindingDetailObject", "txtFindingDetailEvidence", "txtFindingDetailRecommendation", "txtFixText", "btnCopyFixText"];

    /// <summary>
    /// After a fix run of 2, 10 and 100 objects the result card is collapsed to one line. At 1440x900, with the sidebar
    /// collapsed and open, the Findings, History and Relationships tabs do not scroll and the detail of the selected finding
    /// shows in full. At 900x600 the tab scrolls, and the scroll shows every part of the detail in full.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixRunCases))]
    public void AfterAFixRunTheTabsKeepTheirHeightAndTheSelectedFindingShowsInFull(ApplicationTheme theme, double width, double height, int objects, bool auditIncomplete)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            RunFix(window, viewModel, objects);
            var where = $"{objects} objects at {width}x{height}";
            AssertRowBackgrounds(ById<DataGrid>(page, "dgFindings"), where);
            var tall = height >= 900 - 48;

            viewModel.SetSelectedFindings([viewModel.FindingRows.First(r => r.Finding.FixKind != HygieneFixKind.None && r.ObjectName.StartsWith(LongText, StringComparison.Ordinal))]);
            Settle(window);
            Assert.False(viewModel.ResultsExpanded, $"{where}: the result card is expanded after the run.");
            var card = ById<FrameworkElement>(page, "pnlHygieneResults");
            Assert.True(card.IsVisible, $"{where}: the result card is not on the Findings tab.");
            Assert.True(card.ActualHeight + card.Margin.Top + card.Margin.Bottom <= CollapsedCardHeight + 0.5,
                $"{where}: the collapsed result card takes {card.ActualHeight + card.Margin.Top + card.Margin.Bottom:0} px.");
            Assert.False(((FrameworkElement)page.FindName("dgHygieneResults")).IsVisible, $"{where}: the result rows show in the collapsed card.");
            var line = ById<TextBlock>(page, auditIncomplete ? "txtHygieneAuditWarning" : "txtHygieneResultSummary");
            Assert.True(line.IsVisible, $"{where}: the one-line summary is hidden.");
            Assert.Equal(TextWrapping.NoWrap, line.TextWrapping);
            Assert.Equal(line.Text, line.ToolTip);
            var lineBounds = line.TransformToAncestor(window).TransformBounds(new Rect(line.RenderSize));
            Assert.True(lineBounds.Right <= window.ActualWidth + 0.5, $"{where}: the one-line summary ends at {lineBounds.Right:0} px in a {window.ActualWidth:0} px window.");

            var findings = ById<ScrollViewer>(page, "scrollFindingsTab");
            Assert.True(findings.ScrollableWidth < 1, $"{where}: the Findings tab scrolls sideways by {findings.ScrollableWidth:0} px.");
            Assert.True(ById<DataGrid>(page, "dgFindings").ActualHeight >= SiteHygienePage.FindingsGridMinHeight - 0.5, $"{where}: the findings grid gets {ById<DataGrid>(page, "dgFindings").ActualHeight:0} px.");
            if (tall) Assert.True(findings.ScrollableHeight < 1, $"{where}: the Findings tab scrolls by {findings.ScrollableHeight:0} px in a {findings.ViewportHeight:0} px view.");
            foreach (var id in DetailParts.Concat(viewModel.HasDetailFixNote ? ["txtFindingDetailFixNote"] : []))
            {
                var part = ById<FrameworkElement>(page, id);
                if (!tall)
                {
                    part.BringIntoView();
                    Settle(window);
                }
                AssertShown(findings, part, where);
                if (part is TextBlock block) AssertWholeText(block, where);
            }

            // The card opens to at most ResultLines result rows; more rows scroll with a scroll bar that shows without the pointer.
            viewModel.ResultsExpanded = true;
            Settle(window);
            var results = ById<DataGrid>(page, "dgHygieneResults");
            Assert.True(results.IsVisible, $"{where}: the result rows do not show in the open card.");
            Assert.False(results.IsMouseOver);
            Assert.Equal(Math.Min(objects, SiteHygienePage.ResultLines), WholeLineKit.AssertWholeRows(results, where));
            if (objects > SiteHygienePage.ResultLines)
            {
                var thumb = Descendants(WholeLineKit.VerticalBar(results)!).OfType<System.Windows.Controls.Primitives.Thumb>().Single();
                Assert.True(thumb.ActualWidth >= 6, $"{where}: the thumb of the result rows is {thumb.ActualWidth:0} px wide.");
            }
            foreach (var id in DetailParts)
            {
                var part = ById<FrameworkElement>(page, id);
                part.BringIntoView();
                Settle(window);
                AssertShown(findings, part, where + " with the open card");
            }
            viewModel.ResultsExpanded = false;
            Settle(window);

            ShowTab(window, viewModel, VM.HistoryTab);
            Assert.Equal(auditIncomplete, ById<FrameworkElement>(page, "pnlHygieneResults").IsVisible);
            viewModel.SelectedHistory = viewModel.HistoryRows.Single(r => r.Record.Objects.Count == HygieneHistoryRow.DetailObjects);
            Settle(window);
            var history = ById<ScrollViewer>(page, "scrollHygieneHistoryTab");
            var box = ById<System.Windows.Controls.TextBox>(page, "txtHygieneHistoryDetail");
            var text = Descendants(box).OfType<ScrollViewer>().First();
            Assert.True(text.ScrollableHeight < 1, $"{where}: the History detail box is cut by {text.ScrollableHeight:0} px.");
            if (tall)
            {
                Assert.True(history.ScrollableHeight < 1, $"{where}: the History tab scrolls by {history.ScrollableHeight:0} px.");
                AssertShown(history, box, where + " on History");
            }
            else
            {
                // 900x600: the tab scrolls to the whole detail box, which keeps its lines.
                box.BringIntoView();
                Settle(window);
                AssertReachable(history, box, "txtHygieneHistoryDetail");
            }
            WholeLineKit.AssertWholeLines(box, where + " on History");

            ShowTab(window, viewModel, VM.RelationsTab);
            viewModel.RelationsTabIndex = VM.InventoryTab;
            Settle(window);
            var relations = ById<ScrollViewer>(page, "scrollRelationsTab");
            Assert.True(relations.ScrollableWidth < 1, $"{where}: the Relationships tab scrolls sideways by {relations.ScrollableWidth:0} px.");
            if (tall) Assert.True(relations.ScrollableHeight < 1, $"{where}: the Relationships tab scrolls by {relations.ScrollableHeight:0} px.");
            var inner = ById<TabControl>(page, "tabRelations");
            Assert.True(inner.ActualHeight >= SiteHygienePage.RelationsGridMinHeight - 0.5, $"{where}: the relationship tabs get {inner.ActualHeight:0} px.");
            var edges = ById<DataGrid>(page, "dgEdges");
            if (tall) AssertShown(relations, edges, where + " on Relationships");
            else
            {
                edges.BringIntoView();
                Settle(window);
                AssertReachable(relations, edges, "dgEdges");
            }
        }, width, height, () => FixRunHarness(objects, auditIncomplete));
    }

    private const string DeploymentFindingKey = "16777499";

    /// <summary>The fix text of an APP-02 finding: one Remove-CMApplicationDeployment line per deployment.</summary>
    private static string DeploymentLines(int deployments) =>
        string.Join("\r\n", Enumerable.Range(0, deployments).Select(n => $"Remove-CMApplicationDeployment -Id {16_777_500 + n} -Force"));

    /// <summary>The fix run site with an APP-02 finding of <paramref name="deployments"/> deployments, which the tests select.</summary>
    private static SiteHygienePageHarness DeploymentHarness(int objects, int deployments)
    {
        var h = FixRunHarness(objects, auditIncomplete: false);
        h.Service.Findings.Add(SiteHygienePageHarness.Finding("APP-02", "Applications", HygieneSeverity.Error, "Deployment", DeploymentFindingKey, "Contoso Printer Drivers to All Desktops",
            HygieneFixKind.RemoveApplicationDeployment, DeploymentLines(deployments), evidence: $"The application is retired and still has {deployments} deployments."));
        return h;
    }

    private static void SelectDeploymentFinding(Window window, VM viewModel)
    {
        viewModel.SetSelectedFindings([viewModel.FindingRows.Single(r => r.Finding.ObjectKey == DeploymentFindingKey)]);
        Settle(window);
    }

    /// <summary>The Findings tab does not scroll, the grid keeps its least height and every part of the detail shows in full.</summary>
    private static void AssertFindingsTabFits(Page page, VM viewModel, string where)
    {
        var findings = ById<ScrollViewer>(page, "scrollFindingsTab");
        string Part(string id) => page.FindName(id) is FrameworkElement { IsVisible: true } e ? $"{id} {e.ActualHeight + e.Margin.Top + e.Margin.Bottom:0}" : $"{id} hidden";
        var parts = string.Join(", ", new[] { "pnlFindingFilter", "pnlFindingActions", "pnlFindingDetail", "txtFixText", "txtNotRun", "dgFindings", "pnlHygieneResults" }.Select(Part));
        Assert.True(findings.ScrollableHeight < 1, $"{where}: the Findings tab scrolls by {findings.ScrollableHeight:0} px in a {findings.ViewportHeight:0} px view: {parts} px.");
        Assert.True(findings.ScrollableWidth < 1, $"{where}: the Findings tab scrolls sideways by {findings.ScrollableWidth:0} px.");
        var grid = ById<DataGrid>(page, "dgFindings");
        Assert.True(grid.ActualHeight >= SiteHygienePage.FindingsGridMinHeight - 0.5, $"{where}: the findings grid gets {grid.ActualHeight:0} px.");
        foreach (var id in DetailParts.Concat(viewModel.HasDetailFixNote ? ["txtFindingDetailFixNote"] : []))
        {
            var part = ById<FrameworkElement>(page, id);
            AssertShown(findings, part, where);
            if (part is TextBlock block) AssertWholeText(block, where);
        }
    }

    public static TheoryData<ApplicationTheme, double, int> OpenCardCases()
    {
        var data = new TheoryData<ApplicationTheme, double, int>();
        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
        foreach (var width in new[] { 1440 - CollapsedPaneWidth, 1440 - NavigationPaneWidth })
        foreach (var rows in new[] { 2, 3, 10 })
            data.Add(theme, width, rows);
        return data;
    }

    /// <summary>
    /// At 1440x900, with the sidebar collapsed and open, the open result card of 2, 3 and 10 result rows leaves the Findings
    /// tab without a scroll: the grid keeps its least height and the detail of the selected APP-02 finding shows in full.
    /// </summary>
    [Theory]
    [MemberData(nameof(OpenCardCases))]
    public void WithTheResultCardOpenTheFindingsTabDoesNotScrollAt1440By900(ApplicationTheme theme, double width, int rows)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            RunFix(window, viewModel, rows);
            SelectDeploymentFinding(window, viewModel);
            viewModel.ResultsExpanded = true;
            Settle(window);
            var where = $"{rows} result rows, open card, at {width}x{900 - 48}";

            var results = ById<DataGrid>(page, "dgHygieneResults");
            Assert.True(results.IsVisible, $"{where}: the result rows do not show in the open card.");
            var whole = WholeLineKit.AssertWholeRows(results, where);
            Assert.Equal(Math.Min(rows, SiteHygienePage.ResultLines), whole);
            AssertFindingsTabFits(page, viewModel, where);
        }, width, 900 - 48, () => DeploymentHarness(rows, deployments: 4));
    }

    public static TheoryData<ApplicationTheme, double, int, bool> FixLineCases()
    {
        var data = new TheoryData<ApplicationTheme, double, int, bool>();
        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
        foreach (var width in new[] { 1440 - CollapsedPaneWidth, 1440 - NavigationPaneWidth })
        foreach (var lines in new[] { 4, 12 })
        foreach (var open in new[] { false, true })
            data.Add(theme, width, lines, open);
        return data;
    }

    /// <summary>
    /// At 1440x900 the fix text box shows whole lines: every line up to <see cref="SiteHygienePage.FixLines"/> with the
    /// result card collapsed, at least <see cref="SiteHygienePage.MinFixLines"/> with the card open, and a scroll bar that
    /// shows without the pointer when lines are left. The box and the detail never make the Findings tab scroll.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixLineCases))]
    public void TheFixTextBoxShowsWholeLinesAndAVisibleScrollBarWithoutAScrollOfTheTab(ApplicationTheme theme, double width, int lines, bool open) =>
        AssertFixTextBox(theme: theme, width: width, lines: lines, open: open, font: null);

    /// <summary>
    /// A font list of which no font is installed falls back to a font whose laid-out line height differs from the line
    /// spacing of the list: the box still shows whole lines, and the scroll bar only when lines are left.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(12)]
    public void TheFixTextBoxShowsWholeLinesWhenTheListedFontsAreMissing(int lines) =>
        AssertFixTextBox(theme: ApplicationTheme.Light, width: 1440 - CollapsedPaneWidth, lines: lines, open: false, font: "Absent Mono, Absent Code");

    private static void AssertFixTextBox(ApplicationTheme theme, double width, int lines, bool open, string? font)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            if (font is not null) ById<System.Windows.Controls.TextBox>(page, "txtFixText").FontFamily = new System.Windows.Media.FontFamily(font);
            RunFix(window, viewModel, 3);
            SelectDeploymentFinding(window, viewModel);
            viewModel.ResultsExpanded = open;
            Settle(window);
            var where = $"{lines} fix lines, card {(open ? "open" : "collapsed")}, at {width}x{900 - 48}";

            var box = ById<System.Windows.Controls.TextBox>(page, "txtFixText");
            Assert.Same(box.TryFindResource("DetailBox"), box.Style);
            var text = Descendants(box).OfType<ScrollViewer>().First();
            Assert.Equal(lines, box.LineCount);
            var shown = WholeLineKit.AssertWholeLines(box, where);
            Assert.True(shown <= SiteHygienePage.FixLines, $"{where}: the box shows {shown} lines, more than {SiteHygienePage.FixLines}.");
            var wanted = open ? Math.Min(lines, SiteHygienePage.MinFixLines) : Math.Min(lines, SiteHygienePage.FixLines);
            Assert.True(shown >= wanted, $"{where}: the box shows {shown} of {lines} lines and must show {wanted}.");
            // The thumb of a short box is short, so its rounded ends take a larger share of its bounds.
            if (shown < lines) AssertReadableScrollBar(window, box, text, $"sh-fix-text-{theme}-{width:0}-{lines}-{open}", share: 0.15);
            AssertFindingsTabFits(page, viewModel, where);
        }, width, 900 - 48, () => DeploymentHarness(3, lines));
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

            // The Fix column sorts a finding with a fix first: a report-only finding disables Run fix, and Tab then skips it.
            viewModel.Sort(VM.FindingsGrid, nameof(FindingGridRow.Fix));
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
            foreach (var tab in new[] { VM.FindingsTab, VM.RelationsTab, VM.SummaryTab, VM.OptionsTab, VM.HistoryTab })
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
        var findings = Enumerable.Range(0, count).Select(i => SiteHygienePageHarness.Finding("APP-01", "Applications", HygieneSeverity.Warning, "Application", $"{16_777_300 + i}", $"{LongText} {i}",
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
    public void ThePlanNotesAndThePowerShellTextAreLeftAlignedNotJustified(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var plan = DeletePlan(3);
            var window = new ChangePlanWindow(new ChangePlanViewModel(plan.Plan, (_, _, _) => Task.FromResult(new ChangePlanResult([]))));
            Show(window);
            try
            {
                Settle(window);
                foreach (var id in new[] { "txtNotes", "txtBody" })
                    Assert.Equal(TextAlignment.Left, ((System.Windows.Controls.TextBox)window.FindName(id)).TextAlignment);
            }
            finally { window.Close(); }
        });
    }

    private static readonly string[] ElevenNotes =
    [
        "Before each delete the page reads the collection again.", "It skips a collection with a deployment.", "It skips a collection with a collection rule.",
        "It skips a collection with a client setting.", "It skips a collection an automatic deployment rule uses.", "It skips a collection an administrative user uses.",
        "The site keeps no copy of a deleted collection.", "A delete cannot be undone.", "The audit log records each object.", "The page reads each object back after the delete.",
        "The run stops at the first failure of the provider."
    ];

    /// <summary>The scroll bar of a text box shows without the pointer over it, and its thumb stands out from the box background.</summary>
    private static void AssertReadableScrollBar(Window window, System.Windows.Controls.TextBox box, ScrollViewer text, string where, double share = 0.2)
    {
        var bar = Descendants(text).OfType<System.Windows.Controls.Primitives.ScrollBar>().Single(b => b.Orientation == System.Windows.Controls.Orientation.Vertical);
        var thumb = Descendants(bar).OfType<System.Windows.Controls.Primitives.Thumb>().Single();
        Assert.False(box.IsMouseOver);
        Assert.True(bar.IsVisible && bar.Opacity > 0.99 && thumb.IsVisible && thumb.Opacity > 0.99 && thumb.ActualWidth >= 6,
            $"{where}: the scroll bar is not readable without the pointer: bar {bar.ActualWidth:0} px, opacity {bar.Opacity:0.00}; thumb {thumb.ActualWidth:0} px, opacity {thumb.Opacity:0.00}.");
        var back = MostCommon(Pixels(window, box, where));
        var thumbPixels = Pixels(window, thumb);
        var marked = thumbPixels.Count(p => Contrast(p, back) >= 3) / (double)thumbPixels.Count;
        Assert.True(marked >= share, $"{where}: the thumb is not readable on the box background {back}: {marked:P0} of its pixels have a contrast of 3 or more.");
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, false)]
    [InlineData(ApplicationTheme.Dark, false)]
    [InlineData(ApplicationTheme.Light, true)]
    [InlineData(ApplicationTheme.Dark, true)]
    public void ThePlanWindowShowsElevenNoteLinesInWholeLinesAndAReadableScrollBarWhenItIsShort(ApplicationTheme theme, bool body)
    {
        WithApplication(theme, () =>
        {
            var items = Enumerable.Range(0, 2).Select(i => new ChangePlanItem("Delete collection", $"Contoso Finance Workstations Pilot Wave {i + 1:00} (MCM002{50 + i:X2})", "Done")).ToList();
            var plan = new ChangePlan("Delete 2 collections", "Delete 2 collections", items, isDestructive: true)
            {
                Notes = ElevenNotes,
                Body = body ? "Remove-CMCollection -Id MCM00250 -Force" + Environment.NewLine + "Remove-CMCollection -Id MCM00251 -Force" : null
            };
            var window = new ChangePlanWindow(new ChangePlanViewModel(plan, (_, _, _) => Task.FromResult(new ChangePlanResult([]))));
            Show(window);
            try
            {
                Settle(window);
                var box = (System.Windows.Controls.TextBox)window.FindName("txtNotes");
                var text = Descendants(box).OfType<ScrollViewer>().First();
                var line = box.FontFamily.LineSpacing * box.FontSize;
                Assert.True(window.ActualHeight <= Math.Min(728, SystemParameters.WorkArea.Height) + 0.5, $"The plan window is {window.ActualHeight:0} px high.");
                Assert.True(text.ScrollableHeight < 1, $"The notes box shows {text.ViewportHeight / line:0.0} of {text.ExtentHeight / line:0.0} lines in a {window.ActualHeight:0} px window.");
                foreach (var id in new[] { "chkDestructive", "btnConfirm", "btnCancel", "txtNotes", "gridPlan" })
                    AssertInside(window, (FrameworkElement)window.FindName(id), window.ActualHeight, id);

                // Four lines shorter than the change list at its least height: the notes box gives up whole lines and the scroll bar shows.
                var grid = (FrameworkElement)window.FindName("gridPlan");
                window.Height = window.ActualHeight - (grid.ActualHeight - ChangePlanWindow.GridMinHeight) - 4 * line;
                Settle(window);
                var lines = text.ViewportHeight / line;
                Assert.True(Math.Abs(lines - Math.Round(lines)) < 0.05, $"The notes box of a {window.ActualHeight:0} px window shows {lines:0.00} lines: a line is cut.");
                Assert.True(text.ScrollableHeight >= 1, $"The notes box of a {window.ActualHeight:0} px window shows every line; the test does not show the scroll bar.");
                AssertReadableScrollBar(window, box, text, $"plan-notes-{theme}-{body}");
                Assert.True(grid.ActualHeight >= ChangePlanWindow.GridMinHeight - 0.5, $"The change list gets {grid.ActualHeight:0} px in a {window.ActualHeight:0} px window.");
                foreach (var id in new[] { "chkDestructive", "btnConfirm", "btnCancel", "txtNotes" })
                    AssertInside(window, (FrameworkElement)window.FindName(id), window.ActualHeight, id + " in the shorter window");

                // The minimum height: the notes box keeps whole lines and the footer stays inside the window.
                window.Height = window.MinHeight;
                Settle(window);
                lines = text.ViewportHeight / line;
                Assert.True(lines >= ChangePlanWindow.MinNotesLines - 0.05 && Math.Abs(lines - Math.Round(lines)) < 0.05, $"The notes box at the minimum height shows {lines:0.00} lines.");
                foreach (var id in new[] { "chkDestructive", "btnConfirm", "btnCancel", "txtNotes" })
                    AssertInside(window, (FrameworkElement)window.FindName(id), window.ActualHeight, id + " at the minimum height");
            }
            finally { window.Close(); }
        });
    }

    private static readonly string[] FourteenNotes =
    [
        .. ElevenNotes, "The page writes one Started and one Finished record for the run.", "A skipped collection stays on the site as it is.",
        "The findings list shows the fixed findings as gone after the run."
    ];

    /// <summary>
    /// A plan of 14 note lines and 2 changes: the change list has room above its least height, so the notes box takes that
    /// room and shows every line in the opened window, in whole lines.
    /// </summary>
    [Theory]
    [InlineData(ApplicationTheme.Light, false)]
    [InlineData(ApplicationTheme.Dark, false)]
    [InlineData(ApplicationTheme.Light, true)]
    [InlineData(ApplicationTheme.Dark, true)]
    public void ThePlanNotesBoxUsesTheFreeHeightOfAShortChangeListForFourteenLines(ApplicationTheme theme, bool body)
    {
        WithApplication(theme, () =>
        {
            var items = Enumerable.Range(0, 2).Select(i => new ChangePlanItem("Delete collection", $"Contoso Finance Workstations Pilot Wave {i + 1:00} (MCM002{50 + i:X2})", "Done")).ToList();
            var plan = new ChangePlan("Delete 2 collections", "Delete 2 collections", items, isDestructive: true)
            {
                Notes = FourteenNotes,
                Body = body ? "Remove-CMCollection -Id MCM00250 -Force" + Environment.NewLine + "Remove-CMCollection -Id MCM00251 -Force" : null
            };
            var window = new ChangePlanWindow(new ChangePlanViewModel(plan, (_, _, _) => Task.FromResult(new ChangePlanResult([]))));
            Show(window);
            try
            {
                Settle(window);
                var box = (System.Windows.Controls.TextBox)window.FindName("txtNotes");
                var text = Descendants(box).OfType<ScrollViewer>().First();
                var line = box.FontFamily.LineSpacing * box.FontSize;
                var grid = (FrameworkElement)window.FindName("gridPlan");
                Assert.True(window.ActualHeight <= Math.Min(ChangePlanWindow.MaxOpenHeight, SystemParameters.WorkArea.Height) + 0.5, $"The plan window is {window.ActualHeight:0} px high.");
                Assert.True(text.ExtentHeight / line >= FourteenNotes.Length - 0.05, $"The notes have {text.ExtentHeight / line:0.0} lines; the test needs {FourteenNotes.Length}.");
                Assert.True(text.ScrollableHeight < 1,
                    $"The notes box shows {text.ViewportHeight / line:0.0} of {text.ExtentHeight / line:0.0} lines in a {window.ActualHeight:0} px window; the change list has {grid.ActualHeight:0} px.");
                var lines = text.ViewportHeight / line;
                Assert.True(Math.Abs(lines - Math.Round(lines)) < 0.05, $"The notes box shows {lines:0.00} lines: a line is cut.");
                Assert.True(grid.ActualHeight >= ChangePlanWindow.GridMinHeight - 0.5, $"The change list gets {grid.ActualHeight:0} px in a {window.ActualHeight:0} px window.");
                foreach (var id in new[] { "chkDestructive", "btnConfirm", "btnCancel", "txtNotes", "gridPlan" })
                    AssertInside(window, (FrameworkElement)window.FindName(id), window.ActualHeight, id);
            }
            finally { window.Close(); }
        });
    }

    public static TheoryData<ApplicationTheme, int> PlanBodyCases()
    {
        var data = new TheoryData<ApplicationTheme, int>();
        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
        foreach (var objects in new[] { 2, ChangePlanWindow.BodyLines, ChangePlanWindow.BodyLines + 1, 8, 9, 10 })
            data.Add(theme, objects);
        return data;
    }

    /// <summary>
    /// The plan of a fix shows its PowerShell text in a box of whole lines: every line when the text has at most
    /// <see cref="ChangePlanWindow.BodyLines"/> lines, without a scroll bar; else that many whole lines and a scroll bar
    /// that shows without the pointer. The notes box above it shows whole lines too.
    /// </summary>
    [Theory]
    [MemberData(nameof(PlanBodyCases))]
    public void ThePlanWindowShowsThePowerShellTextInWholeLinesAndAScrollBarOnlyWhenLinesAreLeft(ApplicationTheme theme, int objects)
    {
        WithApplication(theme, () =>
        {
            var h = SiteHygienePageHarness.Large(0, LongText);
            var findings = Enumerable.Range(0, objects).Select(i => SiteHygienePageHarness.Finding("COL-01", "Collections", HygieneSeverity.Warning, "Collection", $"MCM002{i:X2}",
                $"Contoso Finance Workstations Pilot Wave {i + 1:00}", HygieneFixKind.RemoveCollection, $"Remove-CMCollection -Id MCM002{i:X2} -Force")).ToList();
            var prepared = h.Service.PrepareAsync(new HygieneChange(HygieneFixKind.RemoveCollection, findings, false, [], [], null), CancellationToken.None).Result;
            var (plan, problem) = SiteHygieneText.BuildPlan(prepared);
            Assert.Null(problem);
            var window = new ChangePlanWindow(new ChangePlanViewModel(plan!.Plan, (_, _, _) => Task.FromResult(new ChangePlanResult([]))));
            Show(window);
            try
            {
                Settle(window);
                var where = $"plan of {objects} objects";
                var body = (System.Windows.Controls.TextBox)window.FindName("txtBody");
                Assert.Same(body.TryFindResource("DetailBox"), body.Style);
                Assert.Equal(objects, body.LineCount);
                var shown = WholeLineKit.AssertWholeLines(body, where);
                Assert.Equal(Math.Min(objects, ChangePlanWindow.BodyLines), shown);
                if (shown < objects) AssertReadableScrollBar(window, body, Descendants(body).OfType<ScrollViewer>().First(), $"plan-body-{theme}-{objects}", share: 0.15);
                WholeLineKit.AssertWholeLines((System.Windows.Controls.TextBox)window.FindName("txtNotes"), where + ", notes");
                foreach (var id in new[] { "chkDestructive", "chkExtra", "btnConfirm", "btnCancel", "txtNotes", "txtBody", "gridPlan" })
                    AssertInside(window, (FrameworkElement)window.FindName(id), window.ActualHeight, id);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void AToolTipThatShowsWhenThePlanWindowOpensIsClosedFirst()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var target = new TextBlock { Text = "Cell" };
            var tip = new System.Windows.Controls.ToolTip { Content = LongText };
            target.ToolTip = tip;
            var window = new Window { Width = 400, Height = 300, Content = target };
            Show(window);
            try
            {
                Settle(window);
                tip.PlacementTarget = target;
                tip.IsOpen = true;
                Settle(window);
                Assert.True(tip.IsOpen);
                bool? openWhilePlanShows = null;
                window.Dispatcher.BeginInvoke(() =>
                {
                    openWhilePlanShows = tip.IsOpen;
                    foreach (var plan in System.Windows.Application.Current.Windows.OfType<ChangePlanWindow>().ToList()) plan.Close();
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                new ChangePlanDialogs(new ApplicationPackagerEngine.Tests.MaintenanceWindows.RecordingMaintenanceLog()).Show(DeletePlan(1).Plan, (_, _, _) => Task.FromResult(new ChangePlanResult([])));
                Assert.False(openWhilePlanShows);

                tip.IsOpen = true;
                Settle(window);
                Assert.Equal(1, ApplicationPackagerUI.Helpers.OpenToolTips.Close());
                Assert.False(tip.IsOpen);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheFocusGoesBackToTheSelectedFindingWhenThePlanWindowCloses(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Run(viewModel.ScanAsync());
            Settle(window);
            var row = viewModel.FindingRows.First(r => r.Finding.FixKind == HygieneFixKind.SetCollectionRefreshType);
            viewModel.SetSelectedFindings([row]);
            harness.Plans.Confirm = false;
            Settle(window);
            Assert.True(Keyboard.Focus(ById<UIElement>(page, "btnHygieneRunFix")) is not null);
            Run(viewModel.RunFixCommand.ExecuteAsync(null));
            Settle(window);
            Assert.Single(harness.Plans.Shown);
            var focused = Assert.IsAssignableFrom<DependencyObject>(Keyboard.FocusedElement);
            Assert.Same(ById<DataGrid>(page, "dgFindings"), Ancestor<DataGrid>(focused));
            Assert.Same(row, Ancestor<DataGridRow>(focused)!.Item);
            Assert.Equal([row.Key], viewModel.SelectedFindings.Select(r => r.Key));
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheFocusGoesToTheFindingFilterWhenAFixRemovesTheLastRowOfTheView(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Run(viewModel.ScanAsync());
            viewModel.FindingText = harness.EmptyCollection.ObjectName;
            viewModel.FlushFilters();
            Settle(window);
            var row = Assert.Single(viewModel.FindingRows);
            viewModel.SetSelectedFindings([row]);
            Settle(window);
            Assert.True(Keyboard.Focus(ById<UIElement>(page, "btnHygieneRunFix")) is not null);
            Run(viewModel.RunFixCommand.ExecuteAsync(null));
            Settle(window);
            Assert.Single(harness.Plans.Shown);
            Assert.Empty(viewModel.FindingRows);
            Assert.False(viewModel.RunFixCommand.CanExecute(null));
            Assert.Equal("txtFindingFilter", FocusedId());
        }, harness: () => new SiteHygienePageHarness());
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheToolTipOfATrimmedCellIsLeftAlignedNotJustified(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Run(viewModel.ScanAsync());
            Settle(window);
            var cell = Descendants(ById<DataGrid>(page, "dgFindings")).OfType<TextBlock>()
                .First(t => t.TextTrimming == TextTrimming.CharacterEllipsis && t.ToolTip is string { Length: > 0 } && Ancestor<DataGridCell>(t) is not null);
            var tip = new System.Windows.Controls.ToolTip { Content = cell.ToolTip, PlacementTarget = cell };
            tip.IsOpen = true;
            Settle(window);
            try
            {
                var texts = Descendants(tip).OfType<TextBlock>().Where(t => t.Text.Length > 0).ToList();
                Assert.NotEmpty(texts);
                Assert.All(texts, t => Assert.Equal(TextAlignment.Left, t.TextAlignment));
            }
            finally { tip.IsOpen = false; }
        });
    }

    [Fact]
    public void TheApplicationMergesTheToolTipStyleAfterTheControlLibrary()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "src", "ApplicationPackagerUI"))) folder = folder.Parent;
        var app = File.ReadAllText(Path.Combine(folder!.FullName, "src", "ApplicationPackagerUI", "App.xaml"));
        var controls = app.IndexOf("<ui:ControlsDictionary", StringComparison.Ordinal);
        var tips = app.IndexOf(ToolTipStylesSource, StringComparison.Ordinal);
        Assert.True(controls >= 0 && tips > controls, "App.xaml does not merge the tooltip style after the control dictionary.");
    }

    public static TheoryData<ApplicationTheme, double, double> TreeSizes() => new()
    {
        { ApplicationTheme.Light, 900 - NavigationPaneWidth, 600 - 48 },
        { ApplicationTheme.Dark, 900 - NavigationPaneWidth, 600 - 48 },
        { ApplicationTheme.Light, 900 - CollapsedPaneWidth, 600 - 48 },
        { ApplicationTheme.Dark, 900 - CollapsedPaneWidth, 600 - 48 },
        { ApplicationTheme.Light, 1440 - NavigationPaneWidth, 900 - 48 },
        { ApplicationTheme.Dark, 1440 - NavigationPaneWidth, 900 - 48 },
        { ApplicationTheme.Light, 1440 - CollapsedPaneWidth, 900 - 48 },
        { ApplicationTheme.Dark, 1440 - CollapsedPaneWidth, 900 - 48 }
    };

    [Theory]
    [MemberData(nameof(TreeSizes))]
    public void TheTreeKeepsLongTextInsideTheWindowAndTrimsItWithATooltip(ApplicationTheme theme, double width, double height)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Run(viewModel.LoadRelationsAsync());
            ShowTab(window, viewModel, VM.RelationsTab);
            viewModel.RelationsTabIndex = VM.TreeTab;
            Settle(window);
            foreach (var root in viewModel.TreeRoots.Take(3))
            {
                root.IsExpanded = true;
                foreach (var chain in root.Children.Take(3)) chain.IsExpanded = true;
            }
            Settle(window);
            var tree = ById<System.Windows.Controls.TreeView>(page, "treeRelations");
            var treeBounds = tree.TransformToAncestor(window).TransformBounds(new Rect(tree.RenderSize));
            Assert.True(treeBounds.Right <= window.ActualWidth + 0.5, $"The tree ends at {treeBounds.Right:0} px in a {window.ActualWidth:0} px window.");
            var texts = Descendants(tree).OfType<TextBlock>().Where(t => t.IsVisible && t.DataContext is RelationTreeNode node && t.Text == node.Text).ToList();
            Assert.True(texts.Count >= 3, $"The tree shows {texts.Count} items.");
            var outside = texts.Select(t => (t.Text, Right: t.TransformToAncestor(window).TransformBounds(new Rect(t.RenderSize)).Right)).Where(t => t.Right > treeBounds.Right + 0.5)
                .Select(t => $"\"{t.Text}\" ends at {t.Right:0} px").ToList();
            Assert.True(outside.Count == 0, $"The tree ends at {treeBounds.Right:0} px at {width}x{height}: " + string.Join("; ", outside));
            var trimmed = 0;
            foreach (var text in texts)
            {
                Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
                Assert.Equal(text.Text, text.ToolTip);
                var needed = new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
                    new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, System.Windows.Media.Brushes.Black,
                    VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace;
                if (needed > text.ActualWidth + 0.5) trimmed++;
            }
            if (width < 1000) Assert.True(trimmed > 0, "No item text is trimmed at 900 px, so the test does not show the trim.");
        }, width, height);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void RunFixIsDisabledWithItsReasonOnTheFirstVisit(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Assert.False(ById<UIElement>(page, "btnHygieneRunFix").IsEnabled);
            var hint = ById<TextBlock>(page, "txtRunFixHint");
            Assert.True(hint.IsVisible);
            Assert.Equal(VM.NotScanned, hint.Text);
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
