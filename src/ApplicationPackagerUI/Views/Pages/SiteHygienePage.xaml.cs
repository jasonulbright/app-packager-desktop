using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
        dgHygieneResults.SizeChanged += (_, _) => FitResults();
        dgHygieneResults.LoadingRow += (_, _) => Dispatcher.BeginInvoke(FitResults, System.Windows.Threading.DispatcherPriority.Loaded);
        ApplyLayout(compact: false);
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

    /// <summary>The result rows show at most <see cref="ResultLines"/> rows below the column header, <see cref="CompactResultLines"/> on a short page; more rows scroll.</summary>
    private void FitResults()
    {
        var lines = _compact ? CompactResultLines : ResultLines;
        if (!dgHygieneResults.IsVisible || dgHygieneResults.ItemContainerGenerator.ContainerFromIndex(0) is not DataGridRow { ActualHeight: > 0 } row) return;
        if (Helpers.DataGridFocus.Find<ScrollContentPresenter>(dgHygieneResults) is not { ActualHeight: > 0 } rows) return;
        // The column header, the borders and a sideways scroll bar take the height of the grid outside its rows.
        var chrome = dgHygieneResults.ActualHeight - rows.ActualHeight;
        var height = Math.Ceiling(chrome + lines * row.ActualHeight);
        if (Math.Abs(dgHygieneResults.MaxHeight - height) > 0.5) dgHygieneResults.MaxHeight = height;
    }

    /// <summary>The least height of the History grid; the tab scrolls before the grid gets less.</summary>
    public const double HistoryGridMinHeight = 160;

    /// <summary>The least height of the findings grid; the tab scrolls before the grid gets less.</summary>
    public const double FindingsGridMinHeight = 160;

    /// <summary>The least height of the relationship list; the tab scrolls before the list gets less.</summary>
    public const double RelationsGridMinHeight = 300;

    /// <summary>The most result rows the open result card shows; more rows scroll.</summary>
    public const int ResultLines = 3;

    /// <summary>The result rows the open result card shows below <see cref="ShortHeight"/>, so the tabs keep a usable height.</summary>
    public const int CompactResultLines = 2;

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

    private IEnumerable<DataGrid> SortedGrids() => [dgFindings, dgEdges, dgChecks, dgHygieneHistory];

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
        }
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
