using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ApplicationPackagerUI.ViewModels.Pages;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerUI.Views.Pages;

/// <summary>The result tabs of a run: Results, Devices, Raw and Pipeline. The Pivot and Script tabs share one view model.</summary>
public partial class PivotsResultsPanel
{
    public const double DeviceColumnWidth = 200;
    public const double ValueColumnMinWidth = 120;

    private PivotsScriptsViewModel? _viewModel;
    private bool _syncing;

    public PivotsResultsPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as PivotsScriptsViewModel);
        IsVisibleChanged += OnPanelVisibleChanged;
        dgDevices.SelectionChanged += (_, _) => SelectionEvents++;
        dgResults.SelectionChanged += (_, _) => SelectionEvents++;
    }

    private void Attach(PivotsScriptsViewModel? viewModel)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = viewModel;
        if (viewModel is null) return;
        viewModel.PropertyChanged += OnViewModelChanged;
        BuildColumns(viewModel.ResultColumns);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is not { } vm) return;
        switch (e.PropertyName)
        {
            case nameof(PivotsScriptsViewModel.ResultColumns):
                BuildColumns(vm.ResultColumns);
                break;
            case nameof(PivotsScriptsViewModel.SelectedDevices):
                Select(dgDevices, vm.SelectedDevices);
                break;
            case nameof(PivotsScriptsViewModel.SelectedResults):
                Select(dgResults, vm.SelectedResults);
                break;
        }
    }

    /// <summary>The Device column, then one column per result column of the query, each wide enough for its header.</summary>
    private void BuildColumns(IReadOnlyList<string> columns)
    {
        var trimmed = (Style)FindResource("TrimmedCell");
        dgResults.Columns.Clear();
        dgResults.Columns.Add(new DataGridTextColumn
        {
            Header = "Device", Binding = new Binding(nameof(ResultGridRow.Device)), Width = DeviceColumnWidth, MinWidth = 160, ElementStyle = trimmed
        });
        for (var i = 0; i < columns.Count; i++)
        {
            var header = columns[i].Length > 0 ? columns[i] : $"Column {i + 1}";
            var cell = new Style(typeof(System.Windows.Controls.TextBlock), trimmed);
            cell.Setters.Add(new Setter(System.Windows.Controls.TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            cell.Setters.Add(new Setter(ToolTipProperty, new Binding($"{nameof(ResultGridRow.FullCells)}[{i}]") { Mode = BindingMode.OneWay }));
            dgResults.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding($"{nameof(ResultGridRow.Cells)}[{i}]") { Mode = BindingMode.OneWay },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = Math.Max(ValueColumnMinWidth, HeaderWidth(header)),
                ElementStyle = cell
            });
        }
    }

    private double HeaderWidth(string header)
    {
        var text = new FormattedText(header, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(dgResults.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), Math.Max(dgResults.FontSize, 14), Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 40;
    }

    /// <summary>The selection changes a mirror made in the grids; a test counts the work of a mirror with it.</summary>
    public int SelectionEvents { get; private set; }

    private bool _selectionPending;

    /// <summary>
    /// Mirrors the selection of the view model into a grid in linear work. A selection of every row
    /// is one SelectAll; a hidden panel (the result tabs of the other run tab) mirrors when it shows.
    /// </summary>
    private void Select<T>(DataGrid grid, IReadOnlyList<T> rows) where T : class
    {
        if (_syncing) return;
        if (!IsVisible)
        {
            _selectionPending = true;
            return;
        }
        var wanted = new HashSet<object>(rows, ReferenceEqualityComparer.Instance);
        var current = grid.SelectedItems;
        if (current.Count == wanted.Count && current.Cast<object>().All(wanted.Contains)) return;
        _syncing = true;
        try
        {
            if (wanted.Count > 0 && wanted.Count == grid.Items.Count && grid.Items.Cast<object>().All(wanted.Contains)) grid.SelectAll();
            else
            {
                grid.UnselectAll();
                foreach (var row in rows) grid.SelectedItems.Add(row);
            }
        }
        finally { _syncing = false; }
    }

    private void OnPanelVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible || !_selectionPending || _viewModel is not { } vm) return;
        _selectionPending = false;
        Select(dgDevices, vm.SelectedDevices);
        Select(dgResults, vm.SelectedResults);
    }

    private void OnDevicesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _viewModel is null) return;
        _syncing = true;
        try { _viewModel.SetSelectedDevices(dgDevices.SelectedItems.OfType<DeviceGridRow>()); }
        finally { _syncing = false; }
    }

    private void OnResultsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _viewModel is null) return;
        _syncing = true;
        try { _viewModel.SetSelectedResults(dgResults.SelectedItems.OfType<ResultGridRow>()); }
        finally { _syncing = false; }
    }
}
