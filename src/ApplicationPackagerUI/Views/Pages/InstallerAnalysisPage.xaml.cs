using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace ApplicationPackagerUI.Views.Pages;

public partial class InstallerAnalysisPage : INavigationAware
{
    private readonly InstallerAnalysisViewModel _viewModel;
    private readonly IAppLog? _log;

    public InstallerAnalysisPage(InstallerAnalysisViewModel viewModel, IAppLog? log = null)
    {
        _viewModel = viewModel;
        _log = log;
        DataContext = viewModel;
        InitializeComponent();
    }

    public Task OnNavigatedToAsync() => _viewModel.OnNavigatedToAsync();

    public Task OnNavigatedFromAsync() => _viewModel.OnNavigatedFromAsync();

    /// <summary>The dropped file paths, or null when the drop holds no files.</summary>
    public static IReadOnlyList<string>? DroppedPaths(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths ? paths : null;

    // A drop is handled before the path box sees it, so a dropped file is analyzed rather than pasted as text.
    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (DroppedPaths(e.Data) is not { } paths) return;
        e.Handled = true;
        try { await _viewModel.DropAsync(paths); }
        catch (Exception ex)
        {
            _log?.Warning(InstallerAnalysisViewModel.LogArea, $"A drop could not be handled ({ex.GetType().Name}).");
        }
    }

    // A right-click does not select a row by itself, so a context menu action would run on the row selected before.
    private void OnGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid grid) return;
        for (var element = e.OriginalSource as DependencyObject; element is not null && !ReferenceEquals(element, grid);
             element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is not System.Windows.Controls.DataGridRow row) continue;
            if (!row.IsSelected) grid.SelectedItem = row.Item;
            row.Focus();
            return;
        }
    }

    // An Overview grid sizes to its rows, so its own scroller cannot move; the wheel goes to the Overview scroller.
    private void OnSectionGridPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject grid) return;
        for (var parent = VisualTreeHelper.GetParent(grid); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not UIElement element) continue;
            e.Handled = true;
            element.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = grid });
            return;
        }
    }
}
