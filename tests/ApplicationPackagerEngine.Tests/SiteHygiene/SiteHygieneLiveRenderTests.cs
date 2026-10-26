using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Helpers;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using Brushes = System.Windows.Media.Brushes;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using FlowDirection = System.Windows.FlowDirection;
using DataGridCell = System.Windows.Controls.DataGridCell;
using DataGridRow = System.Windows.Controls.DataGridRow;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBlock = System.Windows.Controls.TextBlock;
using VM = ApplicationPackagerUI.ViewModels.Pages.SiteHygieneViewModel;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>The Live tab, the Trends chart, the alert list and the import window in real windows, over the fake Live services, in both themes.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SiteHygieneLiveRenderTests
{
    private const double NavigationPaneWidth = 230;
    private const double CollapsedPaneWidth = 48;

    /// <summary>A value must fit its column with this much room.</summary>
    private const double Room = 1.1;

    private static SiteHygienePageHarness Harness(int rows = 40)
    {
        var h = new SiteHygienePageHarness();
        h.Live!.Service.Next = _ => LiveData.Snapshot(rows: rows, name: LiveData.LongName);
        h.Live.History.SeriesOf = (column, days, max) => FakeMetricsHistory.Points(column, 60, gaps: [20, 41]);
        return h;
    }

    private static void WithPage(ApplicationTheme theme, Action<Window, SiteHygienePage, VM, SiteHygienePageHarness> test,
        double width = 1440 - NavigationPaneWidth, double height = 900 - 48, Func<SiteHygienePageHarness>? harness = null, TimeSpan? refreshWait = null)
    {
        WithApplication(theme, () =>
        {
            var h = (harness ?? (() => Harness()))();
            var viewModel = h.Create();
            var page = new SiteHygienePage(viewModel);
            var window = new Window { Width = width, Height = height, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Run(page.OnNavigatedToAsync());
                Run(viewModel.RefreshLiveAsync(), refreshWait);
                viewModel.SelectedTabIndex = VM.LiveTab;
                Settle(window);
                test(window, page, viewModel, h);
            }
            finally
            {
                viewModel.OnAppClosing();
                window.Close();
            }
        });
    }

    private static void ShowView(Window window, VM viewModel, LiveViewChoice choice)
    {
        viewModel.SelectedLiveView = choice;
        if (choice.Pane == LivePane.Trends) Run(viewModel.TrendTask);
        Settle(window);
    }

    private static IEnumerable<LiveViewChoice> GridViews => SiteHygieneLiveLabels.Views.Where(v => v.Pane == LivePane.Grid);

    private static LiveViewChoice Pane(LivePane pane) => SiteHygieneLiveLabels.Views.Single(v => v.Pane == pane);

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void EveryLiveViewVirtualizesAHundredThousandRowsAndShowsTheStatusAsAGlyphAndAWord(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            var grid = ById<DataGrid>(page, "dgLive");
            foreach (var choice in GridViews)
            {
                ShowView(window, viewModel, choice);
                Assert.Equal(100_000, grid.Items.Count);
                Assert.InRange(Descendants(grid).OfType<DataGridRow>().Count(), 1, 100);
                Assert.Equal(["Status", .. LiveData.Columns[choice.View!.Value]], grid.Columns.Select(c => (string)c.Header));
                var cells = Descendants(grid).OfType<DataGridCell>().Where(c => ReferenceEquals(c.Column, grid.Columns[0])).Select(c => Descendants(c).OfType<TextBlock>().First().Text).ToList();
                Assert.All(cells, text => Assert.Matches("^(✓|▲|✖|\\?) \\w", text));
                Assert.True(ById<FrameworkElement>(page, "txtHygieneLiveSummary").IsVisible);
                AssertReadableRowsAndVisibleSelection(window, grid, $"dgLive-{choice.Id}-{theme}");
            }
        }, harness: () => Harness(100_000), refreshWait: TimeSpan.FromMinutes(2));
    }

    /// <summary>The status cells of the rows shown and the longest real value of each other column of the view, by column header.</summary>
    private static IEnumerable<(string Header, string Text)> LongValues(LiveView view, IEnumerable<LiveGridRow> rows)
    {
        foreach (var text in rows.Select(r => r.StatusCell).Distinct())
            yield return (SiteHygieneLiveLabels.StatusHeader, text);
        foreach (var header in LiveData.Columns[view])
        {
            if (!LiveData.LongValues.TryGetValue(header, out var values)) continue;
            foreach (var text in values) yield return (header, text);
        }
    }

    private static void AssertValuesFit(DataGrid grid, IEnumerable<(string Header, string Text)> values, List<string> failures, string where)
    {
        foreach (var (header, text) in values)
        {
            var column = grid.Columns.Single(c => Equals(c.Header, header));
            var cell = Descendants(grid).OfType<DataGridCell>().First(c => ReferenceEquals(c.Column, column));
            var block = Descendants(cell).OfType<TextBlock>().First();
            var padding = cell.ActualWidth - block.ActualWidth;
            var needed = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(block).PixelsPerDip).WidthIncludingTrailingWhitespace * Room;
            if (needed + padding > column.ActualWidth + 0.5) failures.Add($"{where} {header}: \"{text}\" needs {needed + padding:0} px and the column has {column.ActualWidth:0} px.");
        }
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 1440 - CollapsedPaneWidth)]
    [InlineData(ApplicationTheme.Dark, 1440 - CollapsedPaneWidth)]
    [InlineData(ApplicationTheme.Light, 1440 - NavigationPaneWidth)]
    [InlineData(ApplicationTheme.Dark, 1440 - NavigationPaneWidth)]
    public void EveryLiveColumnHoldsItsLongestRealValueWithRoomAndNoGridScrollsSideways(ApplicationTheme theme, double width)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            var failures = new List<string>();
            var grid = ById<DataGrid>(page, "dgLive");
            foreach (var choice in GridViews)
            {
                ShowView(window, viewModel, choice);
                AssertResizableReadableColumns(grid);
                AssertNoSidewaysScroll(grid, $"dgLive {choice.Id} at {width}");
                AssertValuesFit(grid, LongValues(choice.View!.Value, viewModel.LiveViewRows), failures, choice.Id);
            }

            ShowView(window, viewModel, Pane(LivePane.Alerts));
            var alerts = ById<DataGrid>(page, "dgAlerts");
            AssertResizableReadableColumns(alerts);
            AssertNoSidewaysScroll(alerts, $"dgAlerts at {width}");
            AssertValuesFit(alerts, LiveAlertMetrics.All.Select(m => ("Metric", SiteHygieneLiveLabels.AlertMetric(m)))
                .Concat([("State", "▲ New"), ("State", "Seen"), ("Time", LiveData.LongTime)]), failures, "alerts");

            ShowView(window, viewModel, Pane(LivePane.Trends));
            var table = ById<DataGrid>(page, "dgTrend");
            AssertResizableReadableColumns(table);
            AssertNoSidewaysScroll(table, $"dgTrend at {width}");
            AssertValuesFit(table, [("Time", LiveData.LongTime), ("Value", "1,234,567.5"), ("Value", SiteHygieneLiveLabels.NotKnown), ("Note", TrendTableRow.GapText)],
                failures, "trends");
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }, width);
    }

    /// <summary>The CM01 rows with one row of each status the service writes, and the client rows of the SQL script.</summary>
    private static System.Text.Json.Nodes.JsonObject RealState()
    {
        var state = LiveFixtures.State();
        var classes = state["Classes"]!;
        var deployments = (System.Text.Json.Nodes.JsonArray)classes["SMS_DeploymentSummary"]!;
        deployments[0]!["NumberErrors"] = 1;
        deployments[1]!["NumberTargeted"] = 0;
        deployments[1]!["NumberSuccess"] = 0;
        deployments[2]!["FeatureType"] = 11;
        deployments[3]!["FeatureType"] = 12;
        var pairs = (System.Text.Json.Nodes.JsonArray)classes["SMS_PackageStatusDistPointsSummarizer"]!;
        pairs[0]!["State"] = 3;
        pairs[1]!["State"] = 2;
        pairs[2]!["State"] = 2;
        pairs[3]!["State"] = 2;
        pairs[3]!["PackageType"] = 3;
        ((System.Text.Json.Nodes.JsonArray)classes["SMS_DistributionPointInfo"]!).Add(new System.Text.Json.Nodes.JsonObject
        {
            ["NALPath"] = LiveFixtures.SiteSystemOf("DP02.contoso.com"), ["ServerName"] = "DP02.CONTOSO.COM", ["SiteCode"] = "MCM", ["IsPullDP"] = true, ["IsPXE"] = true, ["ResourceType"] = "Windows NT Workstation"
        });
        var components = (System.Text.Json.Nodes.JsonArray)classes["SMS_ComponentSummarizer"]!;
        components[0]!["Status"] = 2;
        components[2]!["Status"] = 1;
        components[2]!["State"] = 5;
        return state;
    }

    private const string RealClients = """
        @(
            ,@([int]16777219, 'CM01', [int]1, [int]1, 'Active/Pass', [datetime]'2026-10-25T14:07:34', [datetime]'2026-10-22T10:18:44', [datetime]'2026-10-19T08:20:53', [datetime]'2026-10-25T14:07:34', [datetime]'2026-10-19T09:27:10', [datetime]'2026-10-23T17:27:05', '5.00.9146.1009', 'Microsoft Windows NT Advanced Server 10.0')
            ,@([int]16777222, 'CLIENT02', [System.DBNull]::Value, [int]0, 'Inactive/Pass', [datetime]'2026-10-13T12:55:25', [System.DBNull]::Value, [System.DBNull]::Value, [System.DBNull]::Value, [System.DBNull]::Value, [System.DBNull]::Value, '5.00.9141.1011', 'Microsoft Windows NT Workstation 10.0')
            ,@([int]16777223, 'CLIENT03', [int]2, [System.DBNull]::Value, 'Active/Fail', [System.DBNull]::Value, [System.DBNull]::Value, [System.DBNull]::Value, [System.DBNull]::Value, [System.DBNull]::Value, [System.DBNull]::Value, '5.00.9146.1009', 'Microsoft Windows NT Workstation 10.0')
        )
        """;

    private const string RealInactive = """
        @(
            ,@([int]16777230, 'OLD', [datetime]'2026-06-01T00:00:00', [datetime]'2026-06-01T00:00:00', [int]146, 'Microsoft Windows NT Workstation 10.0', '5.00.9141.1011')
            ,@([int]16777231, 'NEVER', [System.DBNull]::Value, [System.DBNull]::Value, [System.DBNull]::Value, 'Microsoft Windows NT Workstation 10.0', '5.00.9141.1011')
            ,@([int]16777222, 'CLIENT02', [datetime]'2026-10-13T12:55:25', [datetime]'2026-10-12T08:25:53', [int]15, 'Microsoft Windows NT Workstation 10.0', '5.00.9141.1011')
        )
        """;

    /// <summary>
    /// The real Live service over the stub site and the real SQL script: every cell the service writes, in every column
    /// that is not a name column, fits its column with room, and no grid scrolls sideways.
    /// </summary>
    [Theory]
    [InlineData(ApplicationTheme.Light, 1440 - CollapsedPaneWidth)]
    [InlineData(ApplicationTheme.Dark, 1440 - NavigationPaneWidth)]
    public void EveryCellTheRealServiceWritesFitsItsColumn(ApplicationTheme theme, double width)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            var failures = new List<string>();
            var grid = ById<DataGrid>(page, "dgLive");
            foreach (var choice in GridViews)
            {
                ShowView(window, viewModel, choice);
                var result = viewModel.LastSnapshot!.Views[choice.View!.Value];
                Assert.True(result.Complete, $"{choice.Id}: {result.Message}");
                Assert.NotEmpty(viewModel.LiveViewRows);
                Assert.All(result.Rows, r => Assert.Contains(r.StatusText, LiveText.StatusTexts(choice.View.Value)));
                Assert.Equal(["Status", .. LiveAggregator.Columns(choice.View.Value)], grid.Columns.Select(c => (string)c.Header));
                AssertNoSidewaysScroll(grid, $"dgLive {choice.Id} at {width}");
                var values = viewModel.LiveViewRows.SelectMany(r => r.Cells.Select((text, i) => (Header: result.Columns[i], Text: text)).Prepend((Header: SiteHygieneLiveLabels.StatusHeader, Text: r.StatusCell)))
                    .Where(v => v.Header == SiteHygieneLiveLabels.StatusHeader || SiteHygieneLiveLabels.KindOf(v.Header) != LiveColumnKind.Name).ToList();
                AssertValuesFit(grid, values, failures, choice.Id);
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }, width, harness: () =>
        {
            var h = new SiteHygienePageHarness { Settings = new ApplicationPackagerUI.Models.AppSettings { SiteCode = "MCM", ProviderMachine = "CM01", DiscoverySqlServer = "CM01.contoso.com", DiscoverySqlDatabase = "CM_MCM" } };
            var sql = new FakeSqlHost { ClientsSet = RealClients, InactiveSet = RealInactive };
            h.LiveService = new SiteHygieneLiveService(new StubHygieneSite(RealState()), sql.Runner, connection: () => new ApplicationPackagerEngine.Services.ConfigMgrSession.ConfigMgrConnection("MCM", "CM01"));
            return h;
        });
    }

    public static TheoryData<ApplicationTheme, double, double> Sizes() => new()
    {
        { ApplicationTheme.Light, 900 - NavigationPaneWidth, 600 - 48 },
        { ApplicationTheme.Dark, 900 - NavigationPaneWidth, 600 - 48 },
        { ApplicationTheme.Light, 900 - CollapsedPaneWidth, 600 - 48 },
        { ApplicationTheme.Light, 1440 - NavigationPaneWidth, 900 - 48 },
        { ApplicationTheme.Dark, 1440 - CollapsedPaneWidth, 900 - 48 }
    };

    private static void AssertReachable(Window window, ScrollViewer scroller, FrameworkElement element, string id)
    {
        element.BringIntoView();
        Settle(window);
        Assert.True(element.IsVisible, $"{id} is not visible.");
        var bounds = element.TransformToAncestor(scroller).TransformBounds(new Rect(element.RenderSize));
        Assert.True(bounds.Left >= -0.5 && bounds.Right <= scroller.ViewportWidth + 0.5, $"{id} spans {bounds.Left:0} to {bounds.Right:0} px in a {scroller.ViewportWidth:0} px tab.");
        Assert.True(bounds.Bottom + scroller.VerticalOffset <= scroller.ExtentHeight + 0.5, $"{id} ends below the area the tab scrolls to.");
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void EveryLivePaneFitsItsWidthKeepsItsLeastHeightAndScrollsToEveryControl(ApplicationTheme theme, double width, double height)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            var scroller = ById<ScrollViewer>(page, "scrollLiveTab");
            void Check(LiveViewChoice choice, string view, params string[] ids)
            {
                ShowView(window, viewModel, choice);
                Assert.True(scroller.ScrollableWidth < 1, $"The Live tab scrolls sideways by {scroller.ScrollableWidth:0} px at {width}x{height}.");
                var shown = ById<FrameworkElement>(page, view);
                Assert.True(shown.ActualHeight >= SiteHygienePage.LiveViewMinHeight - 0.5, $"{view} gets {shown.ActualHeight:0} px at {width}x{height}.");
                foreach (var id in ids) AssertReachable(window, scroller, ById<FrameworkElement>(page, id), $"{id} at {width}x{height}");
            }
            Check(SiteHygieneLiveLabels.Views[0], "dgLive", "btnLiveRefresh", "btnLiveCancel", "btnLivePause", "btnLiveCopySummary", "btnLiveAlertsLog", "txtAutoRefresh", "cboLiveView",
                "cboLiveStatus", "txtLiveFilter", "txtLiveCount", "dgLive");
            Check(Pane(LivePane.Trends), "pnlTrends", "cboTrendMetric", "cboTrendDays", "chartTrend", "txtTrendReadout", "txtTrendNote", "dgTrend");
            var chart = ById<FrameworkElement>(page, "chartTrend");
            Assert.True(chart.ActualHeight >= SiteHygienePage.TrendChartMinHeight - 16.5, $"The chart gets {chart.ActualHeight:0} px at {width}x{height}.");
            Assert.True(ById<FrameworkElement>(page, "dgTrend").ActualHeight >= SiteHygienePage.TrendTableMinHeight - 0.5);
            Check(Pane(LivePane.Alerts), "dgAlerts", "btnAlertsSeen", "txtLiveCount", "dgAlerts");
            foreach (var id in new[] { "pnlHygieneAlertBanner", "txtAlertBanner", "btnAlertBannerShow", "btnAlertBannerSeen", "txtHygieneLiveSummary", "txtHygieneStatus" })
            {
                var element = ById<FrameworkElement>(page, id);
                Assert.True(element.IsVisible, $"{id} is not visible at {width}x{height}.");
                var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
                Assert.True(bounds.Left >= -0.5 && bounds.Right <= window.ActualWidth + 0.5 && bounds.Bottom <= window.ActualHeight + 0.5,
                    $"{id} spans {bounds.Left:0}-{bounds.Right:0},{bounds.Bottom:0} px in a {window.ActualWidth:0}x{window.ActualHeight:0} px window.");
            }
        }, width, height);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheChartBreaksTheLineAtEachGapMovesThePointWithTheKeysAndDrawsTheLineWithContrast(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            ShowView(window, viewModel, Pane(LivePane.Trends));
            var chart = ById<TrendChart>(page, "chartTrend");
            Assert.Equal(60, chart.Points.Count);
            Assert.Equal(3, chart.Lines.Count);
            Assert.Equal([20, 21, 19], chart.Lines.Select(l => l.Points.Count));
            Assert.True(chart.Focus());
            Settle(window);
            Press(window, Key.Right);
            Assert.Equal(0, viewModel.TrendSelectedIndex);
            Press(window, Key.End);
            Assert.Equal(59, viewModel.TrendSelectedIndex);
            Press(window, Key.Left);
            Assert.Equal(58, viewModel.TrendSelectedIndex);
            Assert.Same(viewModel.TrendRows[58], ById<DataGrid>(page, "dgTrend").SelectedItem);
            Assert.StartsWith(viewModel.SelectedTrendMetric.Title + ": ", viewModel.TrendReadout);
            Assert.Equal(viewModel.TrendReadout, AutomationProperties.GetHelpText(chart));
            Assert.Equal(viewModel.TrendReadout, ById<TextBlock>(page, "txtTrendReadout").Text);

            var line = chart.Lines[0];
            var pixels = Pixels(window, chart, $"sh-live-chart-{theme}");
            var back = MostCommon(pixels);
            var stroke = ((SolidColorBrush)line.Stroke).Color;
            Assert.True(Contrast(stroke, back) >= 3, $"The line {stroke} has a contrast of {Contrast(stroke, back):0.0} on the chart background {back}.");
            var labels = Descendants(chart).OfType<TextBlock>().ToList();
            Assert.True(labels.Count >= 5, $"The chart has {labels.Count} tick labels.");
            Assert.All(labels, l => Assert.True(Contrast(((SolidColorBrush)l.Foreground).Color, back) >= 4.5, $"The tick label {l.Text} is not readable on {back}."));
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void AChartOfOnePointOrOfNoPointShowsItsAxesWithoutALine(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            harness.Live!.History.SeriesOf = (column, days, max) => FakeMetricsHistory.Points(column, 1);
            ShowView(window, viewModel, Pane(LivePane.Trends));
            var chart = ById<TrendChart>(page, "chartTrend");
            Assert.Empty(chart.Lines);
            Assert.Single(chart.Children.OfType<System.Windows.Shapes.Ellipse>());
            harness.Live.History.SeriesOf = (column, days, max) => FakeMetricsHistory.Points(column, 0);
            viewModel.TrendDays = 7;
            Run(viewModel.TrendTask);
            Settle(window);
            Assert.Empty(chart.Lines);
            Assert.True(chart.Children.OfType<System.Windows.Shapes.Line>().Count() >= 2);
            Assert.True(ById<FrameworkElement>(page, "txtTrendNote").IsVisible);
        });
    }

    private static void Press(Window window, Key key)
    {
        var target = (UIElement)Keyboard.FocusedElement;
        var source = PresentationSource.FromVisual(target)!;
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled) target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
        Settle(window);
    }

    private static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T) element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as T;
    }

    /// <summary>Tabs from <paramref name="start"/> until the focus is in <paramref name="grid"/>, then once more: the next Tab leaves the grid.</summary>
    private static void TabIntoAndOut(Window window, UIElement start, DataGrid grid)
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
            if (focused is null || Ancestor<DataGrid>(focused) != grid) continue;
            ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Settle(window);
            Assert.NotSame(grid, Ancestor<DataGrid>(Keyboard.FocusedElement as DependencyObject));
            return;
        }
        Assert.Fail($"Tab never reaches {AutomationProperties.GetAutomationId(grid)}: {string.Join(" > ", path)}");
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TabReachesEachLiveGridOnceAndTheKeyboardRefreshesAndSwitchesTheView(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            foreach (var id in new[] { "dgLive", "dgTrend", "dgAlerts" })
                Assert.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(ById<DataGrid>(page, id)));
            TabIntoAndOut(window, ById<UIElement>(page, "cboLiveView"), ById<DataGrid>(page, "dgLive"));
            ShowView(window, viewModel, Pane(LivePane.Trends));
            TabIntoAndOut(window, ById<UIElement>(page, "chartTrend"), ById<DataGrid>(page, "dgTrend"));
            Run(viewModel.RefreshLiveAsync());
            ShowView(window, viewModel, Pane(LivePane.Alerts));
            TabIntoAndOut(window, ById<UIElement>(page, "btnAlertsSeen"), ById<DataGrid>(page, "dgAlerts"));

            var calls = harness.Live!.Service.RefreshCalls;
            var refresh = ById<Wpf.Ui.Controls.Button>(page, "btnLiveRefresh");
            Assert.True(refresh.Focus());
            Assert.True(refresh.Command.CanExecute(null));
            refresh.Command.Execute(null);
            StaTestHelper.PumpUntil(() => harness.Live.Service.RefreshCalls == calls + 1 && !viewModel.IsRefreshing);
            var views = ById<ComboBox>(page, "cboLiveView");
            Assert.True(views.Focus());
            Settle(window);
            views.SelectedIndex = 1;
            Settle(window);
            Assert.Equal(LiveView.Content, viewModel.SelectedLiveView.View);
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheAlertBannerShowsOnEveryTabAsOneLineAndLeavesWhenTheAlertsAreSeen(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Assert.True(viewModel.HasAlertBanner);
            var banner = ById<TextBlock>(page, "txtAlertBanner");
            Assert.Equal(TextWrapping.NoWrap, banner.TextWrapping);
            Assert.Equal(viewModel.AlertBannerText, AutomationProperties.GetName(banner));
            foreach (var tab in new[] { VM.FindingsTab, VM.RelationsTab, VM.LiveTab, VM.SummaryTab, VM.HistoryTab, VM.OptionsTab })
            {
                viewModel.SelectedTabIndex = tab;
                Settle(window);
                Assert.True(ById<FrameworkElement>(page, "pnlHygieneAlertBanner").IsVisible, $"The banner is hidden on tab {tab}.");
            }
            viewModel.MarkAlertsSeenCommand.Execute(null);
            Settle(window);
            Assert.False(ById<FrameworkElement>(page, "pnlHygieneAlertBanner").IsVisible);
        });
    }

    public static TheoryData<ApplicationTheme, double, double> TrendSizes() => new()
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

    /// <summary>
    /// A wrapped readout and a long note (three gaps, a downsampled series, skipped lines) sit between the chart and the
    /// table: the chart keeps its least height, the table shows its header and three whole measured rows, and the tab
    /// scrolls to the bottom of the table instead of cutting it.
    /// </summary>
    [Theory]
    [MemberData(nameof(TrendSizes))]
    public void TheTrendsTableKeepsThreeWholeRowsBelowAWrappedReadoutAndNote(ApplicationTheme theme, double width, double height)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            ShowView(window, viewModel, Pane(LivePane.Trends));
            viewModel.TrendSelectedIndex = 31;
            Settle(window);
            var where = $"at {width}x{height} {theme}";
            Assert.Contains("The value before this point is not known.", viewModel.TrendReadout);
            Assert.Contains("3 gaps", viewModel.TrendNote);
            var scroller = ById<ScrollViewer>(page, "scrollLiveTab");
            var table = ById<DataGrid>(page, "dgTrend");
            var (rows, view) = WholeLineKit.RowBounds(table);
            var whole = WholeLineKit.WholeLines(rows, view, table);
            Assert.True(whole >= SiteHygienePage.TrendTableRows, $"The Trends table shows {whole} whole rows {where}.");
            var chart = ById<FrameworkElement>(page, "chartTrend");
            var card = (FrameworkElement)VisualTreeHelper.GetParent(chart);
            while (card is not Border) card = (FrameworkElement)VisualTreeHelper.GetParent(card);
            Assert.True(card.ActualHeight >= SiteHygienePage.TrendChartMinHeight - 0.5, $"The chart card gets {card.ActualHeight:0} px {where}.");
            foreach (var id in new[] { "txtTrendReadout", "txtTrendNote", "dgTrend" }) AssertReachable(window, scroller, ById<FrameworkElement>(page, id), $"{id} {where}");
            var note = ById<TextBlock>(page, "txtTrendNote");
            var bottom = table.TransformToAncestor(scroller).TransformBounds(new Rect(table.RenderSize)).Bottom + scroller.VerticalOffset;
            Assert.True(bottom <= scroller.ExtentHeight + 0.5, $"The table ends at {bottom:0} px below the {scroller.ExtentHeight:0} px the tab scrolls to {where}.");
            Assert.True(note.ActualHeight > 0);
        }, width, height, harness: () =>
        {
            var h = Harness();
            h.Live!.History.SeriesOf = (column, days, max) => FakeMetricsHistory.Points(column, 60, gaps: [10, 20, 31], downsampled: true, skipped: 4);
            return h;
        });
    }

    /// <summary>The Alerts intro on the Options tab says that the first read of a session raises no alert, and shows every wrapped line in full.</summary>
    [Theory]
    [MemberData(nameof(TrendSizes))]
    public void TheAlertsIntroShowsTheStartingStateRuleInFullAtEverySize(ApplicationTheme theme, double width, double height)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            viewModel.SelectedTabIndex = VM.OptionsTab;
            Settle(window);
            var intro = ById<TextBlock>(page, "txtAlertsIntro");
            Assert.Contains("sets its starting state and raises no alert", intro.Text);
            Assert.Equal(TextWrapping.Wrap, intro.TextWrapping);
            intro.BringIntoView();
            Settle(window);
            var where = $"at {width}x{height} {theme}";
            Assert.True(intro.IsVisible, $"The Alerts intro is hidden {where}.");
            var scroller = Ancestor<ScrollViewer>(intro)!;
            var bounds = intro.TransformToAncestor(scroller).TransformBounds(new Rect(intro.RenderSize));
            Assert.True(bounds.Left >= -0.5 && bounds.Right <= scroller.ViewportWidth + 0.5, $"The Alerts intro spans {bounds.Left:0} to {bounds.Right:0} px {where}.");
            Assert.True(bounds.Bottom + scroller.VerticalOffset <= scroller.ExtentHeight + 0.5, $"The Alerts intro ends below the area the tab scrolls to {where}.");
            var lines = new FormattedText(intro.Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(intro.FontFamily, intro.FontStyle, intro.FontWeight, intro.FontStretch), intro.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(intro).PixelsPerDip)
            { MaxTextWidth = intro.ActualWidth };
            Assert.True(intro.ActualHeight + 0.5 >= lines.Height, $"The Alerts intro gets {intro.ActualHeight:0} px for {lines.Height:0} px of lines {where}.");
        }, width, height);
    }

    /// <summary>An auto-refresh that brings other rows keeps the scroll position, the selected row and the keyboard focus of the grid.</summary>
    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ARefreshWithOtherRowsKeepsTheGridScrollPositionSelectionAndFocus(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            ShowView(window, viewModel, SiteHygieneLiveLabels.Views[0]);
            var grid = ById<DataGrid>(page, "dgLive");
            var row = viewModel.LiveViewRows[150];
            viewModel.SelectedLiveRow = row;
            Settle(window);
            Assert.True(DataGridFocus.FocusItemCell(grid, row));
            Settle(window);
            var offset = page.LiveGridOffset;
            Assert.True(offset > 10, $"The grid did not scroll ({offset}).");
            var before = viewModel.LiveViewRows;

            harness.Live!.Service.Next = _ => LiveData.Snapshot(rows: 200, name: "Changed");
            Run(viewModel.RefreshLiveAsync());
            Settle(window);
            Assert.NotSame(before, viewModel.LiveViewRows);
            Assert.Equal(offset, page.LiveGridOffset, 0.5);
            Assert.Equal(row.Key, Assert.IsType<LiveGridRow>(grid.SelectedItem).Key);
            Assert.True(grid.IsKeyboardFocusWithin, $"The grid lost the keyboard focus to {Keyboard.FocusedElement?.GetType().Name} {(Keyboard.FocusedElement as FrameworkElement)?.Name}.");
        }, harness: () => Harness(200));
    }

    /// <summary>The cells of a row that record the thread that reads them.</summary>
    private sealed class ThreadCells(IReadOnlyList<string> cells, System.Collections.Concurrent.ConcurrentBag<int> threads) : IReadOnlyList<string>
    {
        public string this[int index] { get { threads.Add(Environment.CurrentManagedThreadId); return cells[index]; } }
        public int Count { get { threads.Add(Environment.CurrentManagedThreadId); return cells.Count; } }
        public IEnumerator<string> GetEnumerator() { threads.Add(Environment.CurrentManagedThreadId); return cells.GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void TheGridRowsOfARefreshAreBuiltOffTheUiThread()
    {
        var threads = new System.Collections.Concurrent.ConcurrentBag<int>();
        WithPage(ApplicationTheme.Light, (window, page, viewModel, harness) =>
        {
            Assert.NotEmpty(threads);
            Assert.DoesNotContain(Environment.CurrentManagedThreadId, threads);
            Assert.Equal(500, viewModel.LiveViewRows.Count);
        }, harness: () =>
        {
            var h = Harness();
            h.Live!.Service.Next = _ =>
            {
                var s = LiveData.Snapshot(rows: 500);
                return s with { Views = s.Views.ToDictionary(v => v.Key, v => v.Value with { Rows = v.Value.Rows.Select(r => r with { Cells = new ThreadCells(r.Cells, threads) }).ToList() }) };
            };
            return h;
        });
    }

    public static TheoryData<ApplicationTheme, int> ImportCases() => new()
    {
        { ApplicationTheme.Light, 0 },
        { ApplicationTheme.Dark, 0 },
        { ApplicationTheme.Light, 30 },
        { ApplicationTheme.Dark, 30 }
    };

    /// <summary>The import window opens with the focus on the plan; the plan box shows whole lines up to its limit and a scroll bar only when lines are left.</summary>
    [Theory]
    [MemberData(nameof(ImportCases))]
    public void TheImportWindowOpensOnThePlanAndShowsWholeLines(ApplicationTheme theme, int extraLines)
    {
        WithApplication(theme, () =>
        {
            var plan = string.Join(Environment.NewLine, ["Folder: C:\\Users\\labadmin\\AppData\\Local\\AppPackagerSuite\\site-hygiene\\legacy\\mecm-health-dashboard", "Metrics history: 42 rows.",
                .. Enumerable.Range(0, 3 + extraLines).Select(i => $"Setting {i}: value {i}.")]);
            var dialog = new HygieneImportViewModel(VM.ImportIntro, plan);
            var window = new HygieneImportWindow(dialog);
            Show(window);
            try
            {
                Settle(window);
                var box = ById<System.Windows.Controls.TextBox>(window, "txtImportPlan");
                StaTestHelper.PumpUntil(() => box.IsKeyboardFocusWithin);
                Settle(window);
                var where = $"import plan of {5 + extraLines} lines, {theme}";
                var shown = WholeLineKit.AssertWholeLines(box, where);
                Assert.Equal(Math.Min(HygieneImportWindow.PlanLines, box.LineCount), shown);
                var confirm = ById<FrameworkElement>(window, "btnImportConfirm");
                var bounds = confirm.TransformToAncestor(window).TransformBounds(new Rect(confirm.RenderSize));
                Assert.True(bounds.Bottom <= window.ActualHeight + 0.5, $"{where}: Import ends at {bounds.Bottom:0} px in a {window.ActualHeight:0} px window.");
            }
            finally { window.Close(); }
            Assert.False(dialog.Confirmed);
        });
    }
}
