using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Helpers;
using ApplicationPackagerUI.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerUI.Views.Pages;

public partial class SiteHygienePage : INavigationAware
{
    private readonly SiteHygieneViewModel _viewModel;
    private bool _syncing;
    private bool _selectionPending;

    public SiteHygienePage(SiteHygieneViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        foreach (var grid in SortedGrids()) grid.Sorting += OnGridSorting;
        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.FindingsFocusRequested += (_, _) => Dispatcher.BeginInvoke(FocusFindings, System.Windows.Threading.DispatcherPriority.Input);
        viewModel.LiveRowsReplacing += (_, _) => KeepLiveGridPlace();
        Loaded += (_, _) =>
        {
            ShowSorts();
            SelectFindings();
        };
        dgFindings.IsVisibleChanged += (_, _) =>
        {
            if (dgFindings.IsVisible && _selectionPending) SelectFindings();
        };
        FitTab(tab: gridFindingsTab, gridMinHeight: FindingsGridMinHeight, parts: [pnlFindingFilter, pnlFindingActions, pnlFindingDetail, txtNotRun]);
        FitTab(tab: gridRelationsTab, gridMinHeight: RelationsGridMinHeight, parts: [pnlRelationsActions]);
        FitTab(tab: gridHistoryTab, gridMinHeight: HistoryGridMinHeight, parts: [txtHygieneHistoryIntro, pnlHygieneHistoryFilter, pnlHygieneHistoryDetail]);
        _fitLiveTab = FitTab(tab: gridLiveTab, gridMinHeight: () => rowLiveView.MinHeight, parts: [pnlLiveActions, pnlLiveFilter, pnlLiveMessages]);
        pnlTrendText.SizeChanged += (_, _) => FitLiveView();
        pnlTrends.IsVisibleChanged += (_, _) => FitLiveView();
        dgTrend.LoadingRow += (_, _) => Dispatcher.BeginInvoke(FitLiveView, System.Windows.Threading.DispatcherPriority.Loaded);
        BuildLiveColumns();
        dgLive.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged) FitNameLeast();
        };
        dgLive.LayoutUpdated += (_, _) => FitEmptyLiveExtent();
        // A measure that scrolls the rows area by pixels realizes every row, so new rows restore the row scroll before the next layout.
        ((System.Collections.Specialized.INotifyCollectionChanged)dgLive.Items).CollectionChanged += (_, _) => FitEmptyLiveExtent();
        btnLiveRefresh.Click += (_, _) => _refreshKeepsFocus = btnLiveRefresh.IsKeyboardFocused;
        btnLiveRefresh.IsEnabledChanged += (_, e) =>
        {
            if (e.NewValue is true && _refreshKeepsFocus) Dispatcher.BeginInvoke(RestoreRefreshFocus, System.Windows.Threading.DispatcherPriority.Input);
        };
        dgHygieneResults.SizeChanged += (_, _) => FitResults();
        dgHygieneResults.LoadingRow += (_, _) => Dispatcher.BeginInvoke(FitResults, System.Windows.Threading.DispatcherPriority.Loaded);
        txtFixText.MaxHeight = WholeLines.BoxHeight(box: txtFixText, lines: FixLines);
        // The routed event also comes from the fix text box, whose extent changes with its text.
        scrollFindingsTab.ScrollChanged += (_, e) =>
        {
            if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) FitFixText();
        };
        dgFindings.SizeChanged += (_, _) => FitFixText();
        ShowResultSummary();
    }

    /// <summary>
    /// Sets the fix text box to the whole lines that fit while the findings grid keeps <see cref="FindingsGridMinHeight"/>
    /// and the tab does not scroll, from <see cref="MinFixLines"/> to <see cref="FixLines"/> and up to the lines of the
    /// text, so no line shows cut and the scroll bar shows only when lines are left.
    /// </summary>
    private void FitFixText()
    {
        if (!WholeLines.IsMeasured(txtFixText))
        {
            WholeLines.WhenMeasured(box: txtFixText, fit: FitFixText);
            return;
        }
        var line = WholeLines.LineHeight(txtFixText);
        var lines = Math.Clamp(WholeLines.LineCount(txtFixText), MinFixLines, FixLines);
        var shown = WholeLines.VisibleLines(txtFixText);
        var spare = (int)Math.Floor((dgFindings.ActualHeight - FindingsGridMinHeight - scrollFindingsTab.ScrollableHeight) / line);
        var height = WholeLines.BoxHeight(box: txtFixText, lines: Math.Clamp(shown + spare, MinFixLines, lines));
        if (Math.Abs(height - txtFixText.MaxHeight) > 0.5) txtFixText.MaxHeight = height;
    }

    /// <summary>
    /// The tab fills the visible height of its scroll area. Its least height is the height of the parts above and below
    /// the grid plus the least grid height, so every part shows in full and the tab scrolls only when the visible height
    /// is smaller.
    /// </summary>
    private static Action FitTab(Grid tab, double gridMinHeight, FrameworkElement[] parts) => FitTab(tab, () => gridMinHeight, parts);

    /// <inheritdoc cref="FitTab(Grid, double, FrameworkElement[])"/>
    /// <returns>The fit, for a caller whose least view height changes with other events.</returns>
    private static Action FitTab(Grid tab, Func<double> gridMinHeight, FrameworkElement[] parts)
    {
        void Fit()
        {
            var least = parts.Sum(OuterHeight) + gridMinHeight();
            if (Math.Abs(tab.MinHeight - least) > 0.5) tab.MinHeight = least;
        }

        foreach (var part in parts)
        {
            part.SizeChanged += (_, _) => Fit();
            part.IsVisibleChanged += (_, _) => Fit();
        }
        return Fit;
    }

    private static double OuterHeight(FrameworkElement part) => part.Visibility == Visibility.Visible ? part.ActualHeight + part.Margin.Top + part.Margin.Bottom : 0;

    /// <summary>
    /// The least height of the Live view. The grid and the alert list keep <see cref="LiveViewMinHeight"/>. Trends need the
    /// chart at <see cref="TrendChartMinHeight"/>, the measured readout and note lines between them (they wrap to more lines
    /// in a narrow window), and the table at its measured column header and <see cref="TrendTableRows"/> rows.
    /// </summary>
    private double LiveViewLeast()
    {
        if (!_viewModel.IsTrendsPane) return LiveViewMinHeight;
        var table = TrendTableLeast();
        if (Math.Abs(rowTrendTable.MinHeight - table) > 0.5) rowTrendTable.MinHeight = table;
        return Math.Max(LiveViewMinHeight, TrendChartMinHeight + OuterHeight(pnlTrendText) + table);
    }

    /// <summary>The table height for its header and <see cref="TrendTableRows"/> whole rows; <see cref="TrendTableMinHeight"/> before a row is laid out.</summary>
    private double TrendTableLeast() => WholeLines.GridHeight(grid: dgTrend, rows: TrendTableRows) is { } measured ? Math.Max(measured, TrendTableMinHeight) : TrendTableMinHeight;

    /// <summary>The rows the Trends table shows at its least height.</summary>
    public const int TrendTableRows = 3;

    private Action _fitLiveTab = () => { };

    private void FitLiveView()
    {
        var least = LiveViewLeast();
        if (Math.Abs(rowLiveView.MinHeight - least) > 0.5) rowLiveView.MinHeight = least;
        _fitLiveTab();
    }

    /// <summary>The result rows show at most <see cref="ResultLines"/> whole rows below the column header; more rows scroll.</summary>
    private void FitResults()
    {
        if (WholeLines.GridHeight(grid: dgHygieneResults, rows: ResultLines) is not { } height) return;
        if (Math.Abs(dgHygieneResults.MaxHeight - height) > 0.5) dgHygieneResults.MaxHeight = height;
    }

    /// <summary>The least height of the Live view: the grid, the chart with its table, or the alert list; the tab scrolls before the view gets less.</summary>
    public const double LiveViewMinHeight = TrendChartMinHeight + TrendTableMinHeight + 64;

    /// <summary>The least height of the Trends chart.</summary>
    public const double TrendChartMinHeight = 140;

    /// <summary>The least height of the Trends table: the column header and three rows.</summary>
    public const double TrendTableMinHeight = 128;

    /// <summary>The least height of the History grid; the tab scrolls before the grid gets less.</summary>
    public const double HistoryGridMinHeight = 160;

    /// <summary>The least height of the findings grid; the tab scrolls before the grid gets less.</summary>
    public const double FindingsGridMinHeight = 160;

    /// <summary>The least height of the relationship list; the tab scrolls before the list gets less.</summary>
    public const double RelationsGridMinHeight = 300;

    /// <summary>The most result rows the open result card shows, so the Findings tab keeps its least height at 1440x900; more rows scroll.</summary>
    public const int ResultLines = 2;

    /// <summary>The most lines the fix text box shows; more lines scroll.</summary>
    public const int FixLines = 8;

    /// <summary>The fewest lines the fix text box shows when the tab is short.</summary>
    public const int MinFixLines = 3;

    private void ShowResultSummary()
    {
        txtHygieneResultSummary.Visibility = _viewModel.HasAuditWarning ? Visibility.Collapsed : Visibility.Visible;
        FitResults();
    }

    /// <summary>The selection changes a mirror made in the findings grid; a test counts the work of a mirror with it.</summary>
    public int SelectionEvents { get; private set; }

    public Task OnNavigatedToAsync() => _viewModel.OnNavigatedToAsync();

    public Task OnNavigatedFromAsync() => _viewModel.OnNavigatedFromAsync();

    private IEnumerable<DataGrid> SortedGrids() => [dgFindings, dgEdges, dgChecks, dgHygieneHistory, dgLive];

    // The page sorts the rows itself, so a sort stays after a filter change and a new result replace the rows.
    private void OnGridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (sender is not DataGrid { Tag: string grid } || string.IsNullOrEmpty(e.Column.SortMemberPath)) return;
        _viewModel.Sort(grid, e.Column.SortMemberPath);
        ShowSorts();
    }

    private void ShowSorts()
    {
        foreach (var grid in SortedGrids())
        {
            var sort = grid.Tag is string name ? _viewModel.SortOf(name) : null;
            foreach (var column in grid.Columns)
                column.SortDirection = sort is { } s && s.Column == column.SortMemberPath ? s.Ascending ? ListSortDirection.Ascending : ListSortDirection.Descending : null;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SiteHygieneViewModel.SelectedFindings):
                SelectFindings();
                break;
            case nameof(SiteHygieneViewModel.FindingRows) or nameof(SiteHygieneViewModel.EdgeRows) or nameof(SiteHygieneViewModel.CheckRows) or nameof(SiteHygieneViewModel.HistoryRows):
                ShowSorts();
                break;
            case nameof(SiteHygieneViewModel.HasAuditWarning) or nameof(SiteHygieneViewModel.ShowsResults):
                ShowResultSummary();
                break;
            case nameof(SiteHygieneViewModel.LiveColumns) or nameof(SiteHygieneViewModel.SelectedLiveView):
                BuildLiveColumns();
                FitLiveView();
                break;
            case nameof(SiteHygieneViewModel.LiveViewRows):
                FitLiveColumns();
                ShowSorts();
                if (_liveGridPlace is not null) Dispatcher.BeginInvoke(RestoreLiveGridPlace, System.Windows.Threading.DispatcherPriority.ContextIdle);
                break;
        }
    }

    /// <summary>Refresh all had the keyboard focus when it was pressed; the button is disabled while the refresh runs, which moves the focus away.</summary>
    private bool _refreshKeepsFocus;

    /// <summary>
    /// The focus goes back to Refresh all after the refresh it started, unless the operator moved the focus to another
    /// control in the meantime. A focus that the disabled button lost sits on the tab scroll area, on a disabled control,
    /// on the window, or nowhere.
    /// </summary>
    private void RestoreRefreshFocus()
    {
        if (!_refreshKeepsFocus || !btnLiveRefresh.IsEnabled) return;
        _refreshKeepsFocus = false;
        if (!btnLiveRefresh.IsVisible) return;
        var moved = System.Windows.Input.Keyboard.FocusedElement is UIElement { IsEnabled: true } element && !ReferenceEquals(element, scrollLiveTab) && element is not Window;
        if (!moved) btnLiveRefresh.Focus();
    }

    /// <summary>The scroll position of the Live grid and whether it held the keyboard focus, taken just before its rows are replaced.</summary>
    private (double Offset, bool HadFocus)? _liveGridPlace;

    private void KeepLiveGridPlace()
    {
        if (!dgLive.IsVisible) return;
        _liveGridPlace = (Helpers.DataGridFocus.Find<ScrollViewer>(dgLive)?.VerticalOffset ?? 0, dgLive.IsKeyboardFocusWithin);
    }

    /// <summary>
    /// After an auto-refresh or a filter replaced the rows: the grid scrolls back to where it was, the selected row
    /// scrolls into view when it is off screen, and the focus goes back to the selected row (or the grid) when the grid had it.
    /// </summary>
    private void RestoreLiveGridPlace()
    {
        if (_liveGridPlace is not { } place) return;
        _liveGridPlace = null;
        if (!dgLive.IsVisible) return;
        dgLive.UpdateLayout();
        if (Helpers.DataGridFocus.Find<ScrollViewer>(dgLive) is { } scroll)
        {
            scroll.ScrollToVerticalOffset(Math.Min(place.Offset, scroll.ScrollableHeight));
            dgLive.UpdateLayout();
        }
        var selected = _viewModel.SelectedLiveRow;
        if (selected is not null && dgLive.Items.Contains(selected)) dgLive.ScrollIntoView(selected);
        // The focused cell can belong to a row container the new rows recycle, so the focus goes to the selected row again.
        if (!place.HadFocus) return;
        if (selected is null || !Helpers.DataGridFocus.FocusItemCell(dgLive, selected)) dgLive.Focus();
    }

    /// <summary>The Live grid scroll position, for a test of the place a refresh keeps.</summary>
    public double LiveGridOffset => Helpers.DataGridFocus.Find<ScrollViewer>(dgLive)?.VerticalOffset ?? 0;

    /// <summary>
    /// The Live grid has a status column and one column per header of the view shown. A value column is as wide as its
    /// widest value in the rows shown, and never narrower than its header with the sort icon. A name column shares the free
    /// width with the other name columns in proportion to its widest name, wraps a name to <see cref="LiveColumnFit.NameLines"/>
    /// lines, and keeps a least width that holds a name of <see cref="LiveColumnFit.TypicalNameChars"/> characters; a column
    /// of identifiers also holds its widest word on one line. When the least widths exceed the grid, the grid scrolls
    /// sideways and each name column gets the least width at which its names show whole (<see cref="FitNameLeast"/>).
    /// </summary>
    private void BuildLiveColumns()
    {
        var key = _viewModel.SelectedLiveView.View is { } view ? SiteHygieneViewModel.LiveSortKey(view) : null;
        var headers = _viewModel.LiveColumns;
        if (Equals(dgLive.Tag, key) && dgLive.Columns.Count == headers.Count + 1 && dgLive.Columns.Skip(1).Select(c => (string)c.Header).SequenceEqual(headers, StringComparer.Ordinal))
        {
            FitLiveColumns();
            return;
        }
        dgLive.Tag = key;
        dgLive.Columns.Clear();
        _liveFit ??= new LiveColumnFit(grid: dgLive, valueStyle: (Style)FindResource("TrimmedCell"));
        _fittedWidths.Clear();
        dgLive.Columns.Add(new DataGridTextColumn
        {
            Header = SiteHygieneLiveLabels.StatusHeader, HeaderTemplate = _liveFit.HeaderTemplate, Binding = new System.Windows.Data.Binding(nameof(LiveGridRow.StatusCell)), SortMemberPath = SiteHygieneLiveLabels.StatusColumn,
            ElementStyle = (Style)FindResource("StatusCell"), MinWidth = _liveFit.HeaderNeed(SiteHygieneLiveLabels.StatusHeader),
            Width = new DataGridLength(_liveFit.HeaderNeed(SiteHygieneLiveLabels.StatusHeader))
        });
        for (var i = 0; i < headers.Count; i++)
        {
            var name = SiteHygieneLiveLabels.KindOf(headers[i]) == LiveColumnKind.Name;
            dgLive.Columns.Add(new DataGridTextColumn
            {
                Header = headers[i], HeaderTemplate = _liveFit.HeaderTemplate, Binding = new System.Windows.Data.Binding($"{nameof(LiveGridRow.Cells)}[{i}]") { Mode = System.Windows.Data.BindingMode.OneWay },
                SortMemberPath = LiveGridRow.CellMember(i), ElementStyle = name ? _liveFit.NameStyle : _liveFit.ValueStyle,
                MinWidth = name ? Math.Max(_liveFit.NameLeast, _liveFit.HeaderNeed(headers[i])) : _liveFit.HeaderNeed(headers[i]),
                Width = name ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(_liveFit.HeaderNeed(headers[i]))
            });
        }
        FitLiveColumns();
        ShowSorts();
    }

    private LiveColumnFit? _liveFit;

    /// <summary>The width the fit last gave each column; a column whose width differs was resized by the user and keeps it.</summary>
    private readonly Dictionary<DataGridColumn, DataGridLength> _fittedWidths = [];

    /// <summary>Sizes the Live columns to the rows shown: the work is one pass over the rows and one measure per distinct long value.</summary>
    private void FitLiveColumns()
    {
        if (_liveFit is null || dgLive.Columns.Count == 0) return;
        var rows = _viewModel.LiveViewRows;
        var view = _viewModel.SelectedLiveView.View;
        _nameLeast.Clear();
        for (var c = 0; c < dgLive.Columns.Count; c++)
        {
            var column = dgLive.Columns[c];
            if (_fittedWidths.TryGetValue(column, out var fitted) && !SameLength(fitted, column.Width)) continue;
            var index = c - 1;
            IEnumerable<string> values = index < 0 ? rows.Select(r => r.StatusCell) : rows.Select(r => index < r.Cells.Count ? r.Cells[index] : "");
            if (column.Width.IsStar)
            {
                var header = (string)column.Header;
                var least = Math.Max(_liveFit.NameLeast, _liveFit.HeaderNeed(header));
                if (SiteHygieneLiveLabels.HoldsIdentifiers(view, header)) least = Math.Max(least, _liveFit.WordNeed(values));
                _nameLeast[column] = (least, Math.Max(least, _liveFit.WholeNameNeed(values)));
                var weight = new DataGridLength(_liveFit.NameWeight(values), DataGridLengthUnitType.Star);
                if (!SameLength(weight, column.Width)) column.Width = weight;
                _fittedWidths[column] = weight;
                continue;
            }
            // The grid takes width from a fixed column down to its least width before it scrolls, so the least width is the value width.
            var need = Math.Max(_liveFit.HeaderNeed((string)column.Header), _liveFit.ValueNeed(values));
            if (Math.Abs(column.MinWidth - need) > 0.5)
            {
                column.MinWidth = Math.Min(column.MinWidth, need);
                column.Width = new DataGridLength(need);
                column.MinWidth = need;
            }
            else if (!SameLength(new DataGridLength(need), column.Width)) column.Width = new DataGridLength(need);
            _fittedWidths[column] = new DataGridLength(need);
        }
        FitNameLeast();
    }

    /// <summary>
    /// The least widths of the name columns the fit sizes: the least width of the typical name (and of the widest word
    /// in a column of identifiers), and the least width at which every name shows whole, up to <see cref="LiveColumnFit.NameCap"/>.
    /// </summary>
    private readonly Dictionary<DataGridColumn, (double Least, double Whole)> _nameLeast = [];

    /// <summary>
    /// A grid whose least column widths exceed its width scrolls sideways; then each name column gets the width at
    /// which its names show whole, because the scroll shows the extra width. The work is one sum over the columns.
    /// </summary>
    private void FitNameLeast()
    {
        if (_nameLeast.Count == 0) return;
        var least = dgLive.Columns.Sum(c => _nameLeast.TryGetValue(c, out var n) ? n.Least : c.MinWidth);
        var scrolls = LiveGridWidth() is > 0 and var width && least > width;
        foreach (var (column, need) in _nameLeast)
        {
            var wanted = scrolls ? need.Whole : need.Least;
            if (Math.Abs(column.MinWidth - wanted) > 0.5) column.MinWidth = wanted;
        }
    }

    /// <summary>
    /// The width the Live columns share: the grid scroll area less the row headers and a vertical scroll bar. The bar
    /// width counts whether the bar shows or not, so a bar that a change of row heights shows or hides does not change the result.
    /// </summary>
    private double LiveGridWidth()
    {
        var area = Helpers.DataGridFocus.Find<ScrollViewer>(dgLive)?.ActualWidth ?? 0;
        return area > 0 ? area - dgLive.CellsPanelHorizontalOffset - SystemParameters.VerticalScrollBarWidth : 0;
    }

    /// <summary>
    /// A grid with no rows scrolls sideways as far as its columns reach. The rows panel sets the scroll width from its
    /// rows, so a grid with no rows has no scroll width and clips the headers at its right edge. With no rows the grid
    /// scrolls the rows area by pixels, and the rows area is at least as wide as the columns. The work is one sum over the columns.
    /// </summary>
    private void FitEmptyLiveExtent()
    {
        var empty = dgLive.Items.Count == 0;
        if (!empty && !_emptyExtent) return;
        if (Helpers.DataGridFocus.Find<ScrollViewer>(dgLive) is not { Content: ItemsPresenter rows } scroll) return;
        if (!empty)
        {
            _emptyExtent = false;
            scroll.ClearValue(ScrollViewer.CanContentScrollProperty);
            rows.ClearValue(MinWidthProperty);
            // The scroll viewer gives the rows panel its scroll directions only in its own measure; a panel without them realizes every row.
            scroll.InvalidateMeasure();
            return;
        }
        if (!_emptyExtent)
        {
            _emptyExtent = true;
            scroll.CanContentScroll = false;
            scroll.InvalidateMeasure();
        }
        // Layout rounding can put the sum of columns that fill the grid a fraction of a pixel past it.
        var width = Math.Floor(dgLive.CellsPanelHorizontalOffset + dgLive.Columns.Where(c => c.Visibility == Visibility.Visible).Sum(c => c.ActualWidth));
        if (Math.Abs(rows.MinWidth - width) > 0.5) rows.MinWidth = width;
    }

    private bool _emptyExtent;

    // The grid writes its desired and display widths into the column width during layout, so only the unit and the value compare.
    private static bool SameLength(DataGridLength a, DataGridLength b) => a.UnitType == b.UnitType && Math.Abs(a.Value - b.Value) < 0.5;

    /// <summary>The fit of the Live columns, for a test of the least widths.</summary>
    public LiveColumnFit? LiveFit => _liveFit;

    /// <summary>
    /// Mirrors the selection the view model keeps by key into the grid in linear work: a selection of
    /// every row is one SelectAll, and a hidden grid mirrors when it shows.
    /// </summary>
    private void SelectFindings()
    {
        if (_syncing) return;
        if (!dgFindings.IsVisible)
        {
            _selectionPending = true;
            return;
        }
        _selectionPending = false;
        var rows = _viewModel.SelectedFindings;
        var wanted = new HashSet<object>(rows, ReferenceEqualityComparer.Instance);
        var current = dgFindings.SelectedItems;
        if (current.Count == wanted.Count && current.Cast<object>().All(wanted.Contains)) return;
        _syncing = true;
        try
        {
            SelectionEvents++;
            if (wanted.Count > 0 && wanted.Count == dgFindings.Items.Count) dgFindings.SelectAll();
            else
            {
                dgFindings.UnselectAll();
                foreach (var row in rows) dgFindings.SelectedItems.Add(row);
            }
        }
        finally { _syncing = false; }
    }

    /// <summary>
    /// The keyboard focus goes to the selected row of the findings grid, or to its first row when no row of
    /// the selection is left. With no row left it goes to Run fix, or to the findings filter when Run fix is
    /// disabled; a closed modal window otherwise leaves it on the tab scroll area.
    /// </summary>
    private void FocusFindings()
    {
        if (!dgFindings.IsVisible) return;
        var row = _viewModel.SelectedFindings.LastOrDefault(r => dgFindings.Items.Contains(r));
        if (row is not null && Helpers.DataGridFocus.FocusItemCell(dgFindings, row)) return;
        if (dgFindings.Items.Count > 0 && Helpers.DataGridFocus.FocusFirstCell(dgFindings)) return;
        if (btnHygieneRunFix.IsEnabled && btnHygieneRunFix.Focus()) return;
        txtFindingFilter.Focus();
    }

    private void OnFindingsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _viewModel.IsRestoring) return;
        _syncing = true;
        try { _viewModel.SetSelectedFindings(dgFindings.SelectedItems.OfType<FindingGridRow>()); }
        finally { _syncing = false; }
    }
}
