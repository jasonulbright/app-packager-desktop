using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerUI.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerUI.Views.Pages;

public partial class DpContentManagerPage : INavigationAware
{
    private readonly DpContentManagerViewModel _viewModel;
    private bool _syncing;

    public DpContentManagerPage(DpContentManagerViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        foreach (var grid in SortedGrids()) grid.Sorting += OnGridSorting;
        viewModel.PropertyChanged += OnViewModelChanged;
        Loaded += (_, _) => ShowSorts();
    }

    public Task OnNavigatedToAsync() => _viewModel.OnNavigatedToAsync();

    public Task OnNavigatedFromAsync() => _viewModel.OnNavigatedFromAsync();

    private IEnumerable<DataGrid> SortedGrids() => [dgDps, dgDpPairs, dgContent, dgContentPairs, dgIssues, dgDpHistory];

    // The page sorts the rows itself, so a sort stays after a filter change and a refresh replace the rows.
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
            case nameof(DpContentManagerViewModel.SelectedIssues):
                Select(dgIssues, _viewModel.SelectedIssues);
                break;
            case nameof(DpContentManagerViewModel.SelectedContentPairs):
                Select(dgContentPairs, _viewModel.SelectedContentPairs);
                break;
            case nameof(DpContentManagerViewModel.DpRows) or nameof(DpContentManagerViewModel.ContentRows) or nameof(DpContentManagerViewModel.IssueRows)
                or nameof(DpContentManagerViewModel.DpPairRows) or nameof(DpContentManagerViewModel.ContentPairRows) or nameof(DpContentManagerViewModel.HistoryRows):
                ShowSorts();
                break;
        }
    }

    /// <summary>Selects the rows the view model keeps in a multi-select grid; the selection events this causes are not reported back.</summary>
    private void Select(DataGrid grid, IReadOnlyList<PairGridRow> rows)
    {
        if (_syncing) return;
        var current = grid.SelectedItems.Cast<object>().ToList();
        if (current.Count == rows.Count && current.All(r => rows.Contains(r))) return;
        _syncing = true;
        try
        {
            grid.SelectedItems.Clear();
            foreach (var row in rows) grid.SelectedItems.Add(row);
        }
        finally { _syncing = false; }
    }

    private void OnContentPairsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _viewModel.IsRestoring) return;
        _syncing = true;
        try { _viewModel.SetSelectedContentPairs(dgContentPairs.SelectedItems.OfType<PairGridRow>()); }
        finally { _syncing = false; }
    }

    private void OnIssuesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _viewModel.IsRestoring) return;
        _syncing = true;
        try { _viewModel.SetSelectedIssues(dgIssues.SelectedItems.OfType<PairGridRow>()); }
        finally { _syncing = false; }
    }
}
