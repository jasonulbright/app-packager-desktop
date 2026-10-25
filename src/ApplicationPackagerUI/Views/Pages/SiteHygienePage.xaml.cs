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
        Loaded += (_, _) =>
        {
            ShowSorts();
            SelectFindings();
        };
        dgFindings.IsVisibleChanged += (_, _) =>
        {
            if (dgFindings.IsVisible && _selectionPending) SelectFindings();
        };
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Height < ShortHeight);
        FitTab(tab: gridFindingsTab, gridMinHeight: FindingsGridMinHeight, parts: [pnlFindingFilter, pnlFindingActions, pnlFindingDetail, txtNotRun]);
        FitTab(tab: gridRelationsTab, gridMinHeight: RelationsGridMinHeight, parts: [pnlRelationsActions]);
        FitTab(tab: gridHistoryTab, gridMinHeight: HistoryGridMinHeight, parts: [txtHygieneHistoryIntro, pnlHygieneHistoryFilter, pnlHygieneHistoryDetail]);
        FitTab(tab: gridLiveTab, gridMinHeight: LiveViewMinHeight, parts: [pnlLiveActions, pnlLiveFilter, pnlLiveMessages]);
        BuildLiveColumns();
        dgHygieneResults.SizeChanged += (_, _) => FitResults();
        dgHygieneResults.LoadingRow += (_, _) => Dispatcher.BeginInvoke(FitResults, System.Windows.Threading.DispatcherPriority.Loaded);
        txtFixText.MaxHeight = WholeLines.BoxHeight(box: txtFixText, lines: FixLines);
        // The routed event also comes from the fix text box, whose extent changes with its text.
        scrollFindingsTab.ScrollChanged += (_, e) =>
        {
            if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) FitFixText();
        };
        dgFindings.SizeChanged += (_, _) => FitFixText();
        ApplyLayout(compact: false);
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
    private static void FitTab(Grid tab, double gridMinHeight, FrameworkElement[] parts)
    {
        static double Height(FrameworkElement part) => part.Visibility == Visibility.Visible ? part.ActualHeight + part.Margin.Top + part.Margin.Bottom : 0;

        void Fit()
        {
            var least = parts.Sum(Height) + gridMinHeight;
            if (Math.Abs(tab.MinHeight - least) > 0.5) tab.MinHeight = least;
        }

        foreach (var part in parts)
        {
            part.SizeChanged += (_, _) => Fit();
            part.IsVisibleChanged += (_, _) => Fit();
        }
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

    private bool _compact;

    private void ApplyLayout(bool compact)
    {
        _compact = compact;
        txtHygieneIntro.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        pageRoot.Margin = compact ? CompactMargin : RegularMargin;
        txtHygieneResultSummary.Visibility = _viewModel.HasAuditWarning ? Visibility.Collapsed : Visibility.Visible;
        FitResults();
    }

    private static readonly Thickness RegularMargin = new(24, 16, 24, 12);
    private static readonly Thickness CompactMargin = new(24, 6, 24, 4);

    /// <summary>Below this page height the intro line is hidden, so the tabs keep a usable height at 900x600.</summary>
    public const double ShortHeight = 700;

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
                ApplyLayout(_compact);
                break;
            case nameof(SiteHygieneViewModel.LiveColumns) or nameof(SiteHygieneViewModel.SelectedLiveView):
                BuildLiveColumns();
                break;
            case nameof(SiteHygieneViewModel.LiveViewRows):
                ShowSorts();
                break;
        }
    }

    /// <summary>
    /// The Live grid has a status column and one column per header of the view shown. A name column takes the free width;
    /// every other column has the width of its longest value, so no value is cut at 1440 pixels.
    /// </summary>
    private void BuildLiveColumns()
    {
        var key = _viewModel.SelectedLiveView.View is { } view ? SiteHygieneViewModel.LiveSortKey(view) : null;
        var headers = _viewModel.LiveColumns;
        if (Equals(dgLive.Tag, key) && dgLive.Columns.Count == headers.Count + 1 && dgLive.Columns.Skip(1).Select(c => (string)c.Header).SequenceEqual(headers, StringComparer.Ordinal)) return;
        dgLive.Tag = key;
        dgLive.Columns.Clear();
        var cell = (Style)FindResource("TrimmedCell");
        var status = Math.Max(SiteHygieneLiveLabels.StatusWidth, StatusColumnWidth(_viewModel.SelectedLiveView.View));
        dgLive.Columns.Add(new DataGridTextColumn
        {
            Header = SiteHygieneLiveLabels.StatusHeader, Binding = new System.Windows.Data.Binding(nameof(LiveGridRow.StatusCell)), SortMemberPath = SiteHygieneLiveLabels.StatusColumn,
            Width = status, MinWidth = status, ElementStyle = cell
        });
        for (var i = 0; i < headers.Count; i++)
        {
            var kind = SiteHygieneLiveLabels.KindOf(headers[i]);
            var (width, min) = SiteHygieneLiveLabels.Width(kind);
            var header = HeaderWidth(headers[i]);
            min = Math.Max(min, header);
            width = Math.Max(width, header);
            dgLive.Columns.Add(new DataGridTextColumn
            {
                Header = headers[i], Binding = new System.Windows.Data.Binding($"{nameof(LiveGridRow.Cells)}[{i}]") { Mode = System.Windows.Data.BindingMode.OneWay },
                SortMemberPath = LiveGridRow.CellMember(i), ElementStyle = cell, MinWidth = min,
                Width = kind == LiveColumnKind.Name ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width)
            });
        }
        ShowSorts();
    }

    /// <summary>The room of the sort glyph and the header padding around the header text.</summary>
    private const double HeaderRoom = 32;

    /// <summary>The width of the widest status cell the view can show (each glyph with each status text of the view), with a 10 % margin.</summary>
    private double StatusColumnWidth(LiveView? view)
    {
        if (view is not { } v) return 0;
        var typeface = new System.Windows.Media.Typeface(dgLive.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dip = System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var widest = LiveText.StatusTexts(v).SelectMany(t => Enum.GetValues<LiveStatus>().Select(s => SiteHygieneLiveLabels.StatusCell(s, t)))
            .Max(text => new System.Windows.Media.FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, dgLive.FontSize,
                System.Windows.Media.Brushes.Black, dip).WidthIncludingTrailingWhitespace);
        return Math.Ceiling(widest * 1.1 + HeaderRoom);
    }

    /// <summary>The width the column header needs: its text in the semibold header font, the sort glyph and the padding.</summary>
    private double HeaderWidth(string header)
    {
        var text = new System.Windows.Media.FormattedText(header, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new System.Windows.Media.Typeface(dgLive.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), dgLive.FontSize, System.Windows.Media.Brushes.Black,
            System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return Math.Ceiling(text.WidthIncludingTrailingWhitespace + HeaderRoom);
    }

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
