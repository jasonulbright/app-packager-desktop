using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Helpers;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using Brushes = System.Windows.Media.Brushes;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using FlowDirection = System.Windows.FlowDirection;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using TextBlock = System.Windows.Controls.TextBlock;
using VM = ApplicationPackagerUI.ViewModels.Pages.SiteHygieneViewModel;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>
/// The Live grids with the values the CM01 site returns, at 1440x900 and 900x600 with the navigation pane open and
/// collapsed, in both themes: what shows cut, what wraps, what scrolls, and the keyboard and UI Automation paths.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SiteHygieneLiveFitTests
{
    private const double NavigationPaneWidth = 230;
    private const double CollapsedPaneWidth = 48;
    private const double TitleBar = 48;

    /// <summary>A name of a program deployment longer than the typical name, as the site writes "package (program)".</summary>
    private const string LongProgramName = "CMCO-CANARY-Package (CMCO-CANARY-RunAvailable) - Install Program for All Users";

    private static LiveRow Row(string key, LiveStatus status, string text, params string[] cells) => new(Key: key, Status: status, StatusText: text, Cells: cells);

    /// <summary>The nine deployments of the CM01 site (CSV export of the Deployments view) and one longer program name.</summary>
    private static readonly IReadOnlyList<LiveRow> LabDeployments =
    [
        Row("d1", LiveStatus.Warning, "Below 95 %", "CMCO-CANARY-App", "Application", "CMCO-CANARY-Devices", "Available", "1", "0", "0", "0", "0.0"),
        Row("d2", LiveStatus.Ok, "OK", "CMCO-CANARY-Baseline", "Baseline", "CMCO-CANARY-Devices", "Required", "1", "1", "0", "0", "100.0"),
        Row("d3", LiveStatus.Ok, "OK", "CMCO-CANARY-Remediate-Baseline", "Baseline", "CMCO-CANARY-Devices", "Required", "1", "1", "0", "0", "100.0"),
        Row("d4", LiveStatus.Ok, "OK", "ConfigMgr Client Health", "Baseline", "CH Lab 1.0.4", "Required", "1", "1", "0", "0", "100.0"),
        Row("d5", LiveStatus.Ok, "OK", "CMCO-CANARY-Package (CMCO-CANARY-Run)", "Program", "CMCO-CANARY-Devices", "Required", "1", "1", "0", "0", "100.0"),
        Row("d6", LiveStatus.Warning, "Below 95 %", "CMCO-CANARY-Package (CMCO-CANARY-RunAvailable)", "Program", "CMCO-CANARY-Devices", "Available", "1", "0", "0", "0", "0.0"),
        Row("d7", LiveStatus.Ok, "OK", "ConfigMgr Client Health (Deploy)", "Program", "CH Lab 1.0.4", "Required", "1", "1", "0", "0", "100.0"),
        Row("d8", LiveStatus.Warning, "In progress", "CMCO-CANARY-SWU-Group", "Software update", "CMCO-CANARY-SWU-Devices", "Required", "1", "0", "0", "1", "0.0"),
        Row("d9", LiveStatus.Warning, "In progress", "202609-W11-25H2", "Software update", "All Desktop and Server Clients", "Available", "4", "2", "0", "2", "50.0"),
        Row("d10", LiveStatus.Ok, "OK", LongProgramName, "Program", "CMCO-CANARY-Devices", "Required", "1", "1", "0", "0", "100.0")
    ];

    /// <summary>The longest component names and the site systems of the CM01 site (CSV export of the Site Health view).</summary>
    private static readonly IReadOnlyList<LiveRow> LabSiteHealth =
    [
        Row("s1", LiveStatus.Ok, "OK", "SMS_REPLICATION_CONFIGURATION_MONITOR", "Component", "CM01.CONTOSO.COM", "Started", "2026-10-24 04:55:59"),
        Row("s2", LiveStatus.Ok, "OK", "SMS_DATABASE_NOTIFICATION_MONITOR", "Component", "CM01.CONTOSO.COM", "Started", "2026-10-24 04:55:59"),
        Row("s3", LiveStatus.Ok, "OK", "SMS_SITE_SYSTEM_STATUS_SUMMARIZER", "Component", "CM01.CONTOSO.COM", "Started", "2026-10-24 04:55:59"),
        Row("s4", LiveStatus.Ok, "OK", "CONFIGURATION_MANAGER_UPDATE", "Component", "CM01.CONTOSO.COM", "Started", "2026-10-24 04:55:59"),
        Row("s5", LiveStatus.Failed, "Critical", "SMS_DISTRIBUTION_MANAGER", "Component", "CM01.CONTOSO.COM", "Started", "2026-10-24 04:55:58"),
        Row("s6", LiveStatus.Ok, "OK", "SMS Fallback Status Point", "Site system", "CM01.contoso.com", "", ""),
        Row("s7", LiveStatus.Ok, "OK", "SMS Software Update Point", "Site system", "CM01.contoso.com", "", ""),
        Row("s8", LiveStatus.Ok, "OK", "SMS SRS Reporting Point", "Site system", "CM01.contoso.com", "", "")
    ];

    /// <summary>The alert messages the evaluator writes, with the longest one.</summary>
    private static readonly IReadOnlyList<AlertEvent> LabAlerts =
    [
        new(new DateTime(2026, 10, 25, 19, 49, 29, DateTimeKind.Utc), LiveAlertMetrics.ContentFailed, "Content: 1 DP-content pair failed."),
        new(new DateTime(2026, 10, 25, 19, 55, 18, DateTimeKind.Utc), LiveAlertMetrics.SiteCritical, "Site health: 1 component or site system is critical."),
        new(new DateTime(2026, 10, 25, 20, 2, 54, DateTimeKind.Utc), LiveAlertMetrics.Compliance, "Deployments: overall compliance 58.3 % is below the 70 % floor.")
    ];

    private sealed class LabAlertEvaluator : IAlertEvaluator
    {
        private bool _fired;

        public IReadOnlyList<AlertEvent> Evaluate(LiveSnapshot snapshot, AlertSettings settings)
        {
            if (_fired) return [];
            _fired = true;
            return LabAlerts;
        }

        public IReadOnlyCollection<string> Breached => [];
    }

    private static LiveSnapshot LabSnapshot()
    {
        var s = LiveData.Snapshot(rows: 3);
        var views = s.Views.ToDictionary(p => p.Key, p => p.Value);
        views[LiveView.Deployments] = views[LiveView.Deployments] with { Rows = LabDeployments };
        views[LiveView.SiteHealth] = views[LiveView.SiteHealth] with { Rows = LabSiteHealth };
        return s with { Views = views };
    }

    internal static SiteHygienePageHarness LabHarness()
    {
        var h = new SiteHygienePageHarness();
        h.Live!.Service.Next = _ => LabSnapshot();
        h.RealLive = h.Live.Services() with { Alerts = new LabAlertEvaluator() };
        return h;
    }

    private static void WithPage(ApplicationTheme theme, double width, double height, Action<Window, SiteHygienePage, VM, SiteHygienePageHarness> test,
        Func<SiteHygienePageHarness>? harness = null)
    {
        WithApplication(theme, () =>
        {
            var h = (harness ?? LabHarness)();
            var viewModel = h.Create();
            var page = new SiteHygienePage(viewModel);
            var window = new Window { Width = width, Height = height, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Run(page.OnNavigatedToAsync());
                Run(viewModel.RefreshLiveAsync());
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

    private static void ShowView(Window window, VM viewModel, Func<LiveViewChoice, bool> which)
    {
        viewModel.SelectedLiveView = SiteHygieneLiveLabels.Views.Single(which);
        Settle(window);
    }

    private static FormattedText Format(TextBlock block, string text) =>
        new(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
            block.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(block).PixelsPerDip);

    /// <summary>Why the text block of the cell shows less than its text; null when it shows all of it.</summary>
    private static string? CutOf(DataGridCell cell)
    {
        var block = Descendants(cell).OfType<TextBlock>().First();
        if (block.Text.Length == 0) return null;
        var text = Format(block, block.Text);
        if (block.TextWrapping == TextWrapping.NoWrap)
            return text.WidthIncludingTrailingWhitespace > block.ActualWidth + 0.5 ? $"needs {text.WidthIncludingTrailingWhitespace:0} px on one line and has {block.ActualWidth:0} px" : null;
        text.MaxTextWidth = Math.Max(1, block.ActualWidth);
        return text.Height > block.ActualHeight + 0.5 || text.Width > block.ActualWidth + 0.5
            ? $"needs {text.Height:0} px of lines at {block.ActualWidth:0} px and has {block.ActualHeight:0} px" : null;
    }

    private static IEnumerable<DataGridCell> Cells(DataGrid grid) => Descendants(grid).OfType<DataGridCell>().Where(c => c.IsVisible);

    /// <summary>
    /// Every value cell shows its whole text. A name cell of at most the typical length shows its whole text when
    /// <paramref name="wholeNames"/>; any other name cell that shows less has the whole name as its tooltip.
    /// </summary>
    private static void AssertNoCutCell(DataGrid grid, List<string> failures, string where, bool wholeNames = true)
    {
        foreach (var cell in Cells(grid))
        {
            var block = Descendants(cell).OfType<TextBlock>().First();
            var header = (string)cell.Column.Header;
            var name = grid.Name == "dgLive" && header != SiteHygieneLiveLabels.StatusHeader && SiteHygieneLiveLabels.KindOf(header) == LiveColumnKind.Name;
            if (name && (!wholeNames || block.Text.Length > LiveColumnFit.TypicalNameChars))
            {
                Assert.Equal(TextWrapping.Wrap, block.TextWrapping);
                Assert.Equal(block.Text, block.ToolTip as string);
                continue;
            }
            if (CutOf(cell) is { } cut) failures.Add($"{where} {header} \"{block.Text}\" {cut}.");
        }
    }

    /// <summary>No column header shows its text in part, with the sort icon shown on that column.</summary>
    private static void AssertNoCutHeader(Window window, DataGrid grid, List<string> failures, string where)
    {
        foreach (var column in grid.Columns)
        {
            var before = column.SortDirection;
            column.SortDirection = System.ComponentModel.ListSortDirection.Ascending;
            Settle(window);
            var header = Descendants(grid).OfType<DataGridColumnHeader>().Single(h => ReferenceEquals(h.Column, column));
            var label = Descendants(header).OfType<TextBlock>().First(t => t.Text == (string)column.Header);
            var presenter = (FrameworkElement)VisualTreeHelper.GetParent(label);
            var text = Format(label, label.Text);
            var words = label.Text.Split(' ').Max(w => Format(label, w).WidthIncludingTrailingWhitespace);
            if (label.TextWrapping == TextWrapping.NoWrap && text.WidthIncludingTrailingWhitespace > presenter.ActualWidth + 0.5)
                failures.Add($"{where} header {column.Header} needs {text.WidthIncludingTrailingWhitespace:0} px and shows {presenter.ActualWidth:0} px when sorted.");
            // Layout rounds the presenter width to whole pixels; the text measure gets the same half pixel.
            text.MaxTextWidth = Math.Max(1, presenter.ActualWidth + 0.5);
            if (words > presenter.ActualWidth + 0.5 || text.Height > label.ActualHeight + 0.5 || text.Height > LiveColumnFit.HeaderLines * label.FontFamily.LineSpacing * label.FontSize + 0.5)
                failures.Add($"{where} header {column.Header} needs {words:0} px for a word and {text.Height:0} px of lines; it shows {presenter.ActualWidth:0}x{label.ActualHeight:0} px when sorted.");
            column.SortDirection = before;
        }
        Settle(window);
    }

    private static ScrollViewer Scroller(DataGrid grid) => Descendants(grid).OfType<ScrollViewer>().First();

    private static bool SidewaysBarShows(DataGrid grid) =>
        Descendants(grid).OfType<ScrollBar>().Any(b => b.Orientation == System.Windows.Controls.Orientation.Horizontal && b.IsVisible && b.ActualWidth > 0);

    [Theory]
    [InlineData(ApplicationTheme.Light, 1440 - CollapsedPaneWidth)]
    [InlineData(ApplicationTheme.Dark, 1440 - CollapsedPaneWidth)]
    [InlineData(ApplicationTheme.Light, 1440 - NavigationPaneWidth)]
    [InlineData(ApplicationTheme.Dark, 1440 - NavigationPaneWidth)]
    public void TheRealDeploymentNamesShowInFullAt1440AndNoHeaderIsCut(ApplicationTheme theme, double width)
    {
        WithPage(theme, width, 900 - TitleBar, (window, page, viewModel, harness) =>
        {
            var failures = new List<string>();
            var grid = ById<DataGrid>(page, "dgLive");
            foreach (var choice in SiteHygieneLiveLabels.Views.Where(v => v.Pane == LivePane.Grid))
            {
                ShowView(window, viewModel, v => v == choice);
                var where = $"{choice.Id} at {width} {theme}:";
                Assert.True(Scroller(grid).ScrollableWidth < 1, $"{where} the columns need {Scroller(grid).ScrollableWidth:0} px more than the {Scroller(grid).ViewportWidth:0} px grid.");
                AssertNoCutCell(grid, failures, where);
                AssertNoCutHeader(window, grid, failures, where);
            }
            Assert.Equal(LabDeployments.Count, viewModel.LastSnapshot!.Views[LiveView.Deployments].Rows.Count);
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheStatusAndCountColumnsTakeTheirMeasuredNeedAndTheNamesTakeTheRest(ApplicationTheme theme)
    {
        WithPage(theme, 1440 - NavigationPaneWidth, 900 - TitleBar, (window, page, viewModel, harness) =>
        {
            ShowView(window, viewModel, v => v.View == LiveView.Deployments);
            var grid = ById<DataGrid>(page, "dgLive");
            var fit = page.LiveFit!;
            var status = grid.Columns[0];
            var widest = LabDeployments.Select(r => SiteHygieneLiveLabels.StatusCell(r.Status, r.StatusText)).Max(fit.Width);
            Assert.Equal(Math.Max(fit.HeaderNeed("Status"), Math.Ceiling(widest * LiveColumnFit.ValueRoom + fit.CellPadding)), status.ActualWidth, 1);
            foreach (var header in new[] { "Targeted", "Success", "Errors", "In progress", "Compliance (%)" })
                Assert.Equal(fit.HeaderNeed(header), grid.Columns.Single(c => (string)c.Header == header).ActualWidth, 1);
            var name = grid.Columns.Single(c => (string)c.Header == "Name");
            var collection = grid.Columns.Single(c => (string)c.Header == "Collection");
            Assert.True(name.Width.IsStar && collection.Width.IsStar);
            Assert.True(name.ActualWidth > collection.ActualWidth, $"Name gets {name.ActualWidth:0} px and Collection {collection.ActualWidth:0} px; the names of Name are longer.");
            Assert.True(name.MinWidth >= fit.NameLeast && collection.MinWidth >= fit.NameLeast);
            var typical = Format(Descendants(grid).OfType<DataGridCell>().Select(c => Descendants(c).OfType<TextBlock>().First()).First(), LiveColumnFit.TypicalName);
            typical.MaxTextWidth = fit.NameLeast - fit.CellPadding;
            Assert.True(typical.Height <= LiveColumnFit.NameLines * fit.LineHeight + 0.5, $"The typical name needs {typical.Height:0} px of lines at the least width.");
        });
    }

    public static TheoryData<ApplicationTheme, double> SmallSizes() => new()
    {
        { ApplicationTheme.Light, 900 - NavigationPaneWidth },
        { ApplicationTheme.Dark, 900 - NavigationPaneWidth },
        { ApplicationTheme.Light, 900 - CollapsedPaneWidth },
        { ApplicationTheme.Dark, 900 - CollapsedPaneWidth }
    };

    /// <summary>At 900x600 a grid whose least column widths exceed it scrolls sideways; a value is never cut, a name of typical length wraps whole, and an alert message wraps whole.</summary>
    [Theory]
    [MemberData(nameof(SmallSizes))]
    public void At900By600EveryGridWrapsOrScrollsAndCutsNoValue(ApplicationTheme theme, double width)
    {
        WithPage(theme, width, 600 - TitleBar, (window, page, viewModel, harness) =>
        {
            var failures = new List<string>();
            var grid = ById<DataGrid>(page, "dgLive");
            foreach (var choice in SiteHygieneLiveLabels.Views.Where(v => v.Pane == LivePane.Grid))
            {
                ShowView(window, viewModel, v => v == choice);
                var where = $"{choice.Id} at {width}x600 {theme}:";
                var least = grid.Columns.Sum(c => c.MinWidth);
                var scroller = Scroller(grid);
                if (least > scroller.ViewportWidth + 1) Assert.True(scroller.ScrollableWidth > 0 && SidewaysBarShows(grid), $"{where} the columns need {least:0} px in a {scroller.ViewportWidth:0} px grid and no scroll bar shows.");
                AssertNoCutCell(grid, failures, where, wholeNames: false);
            }
            ShowView(window, viewModel, v => v.Pane == LivePane.Alerts);
            var alerts = ById<DataGrid>(page, "dgAlerts");
            Assert.Equal(LabAlerts.Count, alerts.Items.Count);
            var message = alerts.Columns.Single(c => (string)c.Header == "Message");
            Assert.All(Cells(alerts).Where(c => ReferenceEquals(c.Column, message)), c => Assert.Equal(TextWrapping.Wrap, Descendants(c).OfType<TextBlock>().First().TextWrapping));
            AssertNoCutCell(alerts, failures, $"alerts at {width}x600 {theme}:");
            if (alerts.Columns.Sum(c => c.MinWidth) > Scroller(alerts).ViewportWidth + 1) Assert.True(SidewaysBarShows(alerts), $"The alert columns exceed the grid at {width} and no scroll bar shows.");
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void AColumnTheUserResizedKeepsItsWidthAfterARefresh(ApplicationTheme theme)
    {
        WithPage(theme, 1440 - CollapsedPaneWidth, 900 - TitleBar, (window, page, viewModel, harness) =>
        {
            ShowView(window, viewModel, v => v.View == LiveView.Deployments);
            var grid = ById<DataGrid>(page, "dgLive");
            var type = grid.Columns.Single(c => (string)c.Header == "Type");
            type.Width = new DataGridLength(300);
            Settle(window);
            Run(viewModel.RefreshLiveAsync());
            Settle(window);
            Assert.Equal(300, type.ActualWidth, 1);
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

    private static IEnumerable<AutomationPeer> PeerTree(AutomationPeer peer)
    {
        foreach (var child in peer.GetChildren() ?? [])
        {
            yield return child;
            foreach (var d in PeerTree(child)) yield return d;
        }
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheChartIsAFocusableAutomationElementWithItsMetricAsNameAndTheReadoutAsValue(ApplicationTheme theme)
    {
        WithPage(theme, 1440 - NavigationPaneWidth, 900 - TitleBar, (window, page, viewModel, harness) =>
        {
            ShowView(window, viewModel, v => v.Pane == LivePane.Trends);
            Run(viewModel.TrendTask);
            Settle(window);
            var chart = ById<TrendChart>(page, "chartTrend");
            var peer = UIElementAutomationPeer.CreatePeerForElement(chart);
            Assert.NotNull(peer);
            Assert.Contains(PeerTree(UIElementAutomationPeer.CreatePeerForElement(window)!), p => ReferenceEquals(p, peer));
            Assert.Equal(viewModel.SelectedTrendMetric.Title, peer.GetName());
            Assert.Equal("chartTrend", peer.GetAutomationId());
            Assert.True(peer.IsKeyboardFocusable());
            Assert.True(peer.IsControlElement());
            var value = Assert.IsAssignableFrom<IValueProvider>(peer.GetPattern(PatternInterface.Value));
            Assert.True(value.IsReadOnly);
            Assert.True(chart.Focus());
            Settle(window);
            Assert.True(peer.HasKeyboardFocus());
            Press(window, Key.End);
            Assert.False(string.IsNullOrEmpty(viewModel.TrendReadout));
            Assert.Equal(viewModel.TrendReadout, value.Value);
            Press(window, Key.Left);
            Assert.Equal(viewModel.TrendReadout, value.Value);
            Assert.StartsWith(viewModel.SelectedTrendMetric.Title + ": ", value.Value);
        }, harness: () =>
        {
            var h = LabHarness();
            h.Live!.History.SeriesOf = (column, days, max) => FakeMetricsHistory.Points(column, 30, gaps: [10]);
            return h;
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void AKeyboardRefreshAllKeepsTheFocusOnTheButton(ApplicationTheme theme)
    {
        WithPage(theme, 1440 - NavigationPaneWidth, 900 - TitleBar, (window, page, viewModel, harness) =>
        {
            var refresh = ById<Wpf.Ui.Controls.Button>(page, "btnLiveRefresh");
            Assert.True(refresh.Focus());
            Settle(window);
            harness.Live!.Service.Gate = new TaskCompletionSource();
            var calls = harness.Live.Service.RefreshCalls;
            ((IInvokeProvider)new ButtonAutomationPeer(refresh).GetPattern(PatternInterface.Invoke)!).Invoke();
            StaTestHelper.PumpUntil(() => viewModel.IsRefreshing);
            Settle(window);
            Assert.False(refresh.IsEnabled);
            harness.Live.Service.Gate.SetResult();
            StaTestHelper.PumpUntil(() => harness.Live.Service.RefreshCalls == calls + 1 && !viewModel.IsRefreshing);
            Settle(window);
            Assert.True(refresh.IsKeyboardFocused, $"The focus is on {(Keyboard.FocusedElement as FrameworkElement)?.Name} {Keyboard.FocusedElement?.GetType().Name}.");

            // A refresh that the timer starts takes no focus.
            Assert.True(ById<UIElement>(page, "txtLiveFilter").Focus());
            Run(viewModel.RefreshLiveAsync());
            Settle(window);
            Assert.False(refresh.IsKeyboardFocused);
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheImportNoteShowsOnTheOptionsImportRowAsWholeLines(ApplicationTheme theme)
    {
        const string refusal = @"The settings file C:\Users\labadmin\AppData\Local\AppPackagerSuite\site-hygiene\legacy\mecm-health-dashboard\SiteHygiene.prefs.json is not valid JSON. Nothing was imported.";
        WithPage(theme, 900 - NavigationPaneWidth, 600 - TitleBar, (window, page, viewModel, harness) =>
        {
            viewModel.SelectedTabIndex = VM.OptionsTab;
            Settle(window);
            var note = ById<TextBlock>(page, "txtImportNote");
            note.BringIntoView();
            Settle(window);
            Assert.True(note.IsVisible);
            Assert.Equal(refusal, note.Text);
            Assert.Equal(TextWrapping.Wrap, note.TextWrapping);
            var lines = Format(note, note.Text);
            lines.MaxTextWidth = note.ActualWidth;
            Assert.True(note.ActualHeight + 0.5 >= lines.Height, $"The note gets {note.ActualHeight:0} px for {lines.Height:0} px of lines.");
            Assert.False(viewModel.HasImportOffer);
        }, harness: () =>
        {
            var h = LabHarness();
            h.Live!.Import.Next = new LegacyImportPlan(@"C:\Users\labadmin\AppData\Local\AppPackagerSuite\site-hygiene\legacy\mecm-health-dashboard", [], 0, false, refusal);
            return h;
        });
    }
}
