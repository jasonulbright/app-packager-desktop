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
    }

    private bool _compact;

    private void ApplyLayout(bool compact)
    {
        _compact = compact;
        dgHygieneResults.MaxHeight = compact ? ShortResultsHeight : TallResultsHeight;
        txtHygieneIntro.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        pageRoot.Margin = compact ? CompactMargin : RegularMargin;
        txtHygieneAuditWarning.TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap;
        pnlHygieneAuditWarning.Padding = compact ? CompactWarningPadding : RegularWarningPadding;
        pnlHygieneAuditWarning.Margin = compact ? CompactWarningMargin : RegularWarningMargin;
        txtHygieneResultSummary.Visibility = compact && _viewModel.HasAuditWarning ? Visibility.Collapsed : Visibility.Visible;
    }

    private static readonly Thickness RegularMargin = new(24, 16, 24, 12);
    private static readonly Thickness CompactMargin = new(24, 6, 24, 4);
    private static readonly Thickness RegularWarningPadding = new(12, 8, 12, 8);
    private static readonly Thickness CompactWarningPadding = new(8, 0, 8, 0);
    private static readonly Thickness RegularWarningMargin = new(0, 0, 0, 6);
    private static readonly Thickness CompactWarningMargin = new(0, 0, 0, 2);

    /// <summary>
    /// Below this page height the result rows take less room and the intro line is hidden, so the tabs keep a
    /// usable height at 900x600. The audit warning then keeps one line in place of the result summary, which
    /// the status line also holds; its tooltip and automation name hold the full text.
    /// </summary>
    public const double ShortHeight = 700;
    public const double ShortResultsHeight = 64;
    public const double TallResultsHeight = 140;

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
            case nameof(SiteHygieneViewModel.HasAuditWarning):
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
    /// the selection is left; a closed modal window otherwise leaves it on the tab scroll area.
    /// </summary>
    private void FocusFindings()
    {
        if (!dgFindings.IsVisible) return;
        var row = _viewModel.SelectedFindings.LastOrDefault(r => dgFindings.Items.Contains(r));
        if (row is null || !Helpers.DataGridFocus.FocusItemCell(dgFindings, row)) Helpers.DataGridFocus.FocusFirstCell(dgFindings);
    }

    private void OnFindingsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _viewModel.IsRestoring) return;
        _syncing = true;
        try { _viewModel.SetSelectedFindings(dgFindings.SelectedItems.OfType<FindingGridRow>()); }
        finally { _syncing = false; }
    }
}
