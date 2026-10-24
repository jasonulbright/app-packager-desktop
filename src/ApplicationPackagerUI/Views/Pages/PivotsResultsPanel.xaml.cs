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
            dgResults.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding($"{nameof(ResultGridRow.Cells)}[{i}]") { Mode = BindingMode.OneWay },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = Math.Max(ValueColumnMinWidth, HeaderWidth(header)),
                ElementStyle = trimmed
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

    private void Select<T>(DataGrid grid, IReadOnlyList<T> rows)
    {
        if (_syncing) return;
        var current = grid.SelectedItems.Cast<object>().ToList();
        if (current.Count == rows.Count && current.All(r => rows.Contains((T)r))) return;
        _syncing = true;
        try
        {
            grid.SelectedItems.Clear();
            foreach (var row in rows) grid.SelectedItems.Add(row);
        }
        finally { _syncing = false; }
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
