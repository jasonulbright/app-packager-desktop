using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerUI.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace ApplicationPackagerUI.Views.Pages;

public partial class MaintenanceWindowsPage : INavigationAware
{
    private readonly MaintenanceWindowsViewModel _viewModel;

    public MaintenanceWindowsPage(MaintenanceWindowsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public Task OnNavigatedToAsync() => _viewModel.OnNavigatedToAsync();

    public Task OnNavigatedFromAsync() => _viewModel.OnNavigatedFromAsync();

    /// <summary>
    /// Below this width of the Windows area, the detail panel moves under the grid. Beside the
    /// grid, the panel and its gap take 376 px; the eight columns need 1050 px at their minimum
    /// widths, so the grid scrolls sideways until the area is 1426 px wide.
    /// </summary>
    public const double DetailsBesideMinWidth = 1100;

    public static bool DetailsBelow(double layoutWidth) => layoutWidth < DetailsBesideMinWidth;

    private void OnWindowsLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        var below = DetailsBelow(e.NewSize.Width);
        if (below == (Grid.GetRow(pnlWindowDetails) == 1)) return;
        var layout = gridWindowsLayout;
        layout.ColumnDefinitions[1].Width = new GridLength(below ? 0 : 16);
        layout.ColumnDefinitions[2].Width = new GridLength(below ? 0 : 360);
        layout.RowDefinitions[1].Height = below ? new GridLength(240) : GridLength.Auto;
        Grid.SetRow(pnlWindowDetails, below ? 1 : 0);
        Grid.SetColumn(pnlWindowDetails, below ? 0 : 2);
        Grid.SetColumnSpan(pnlWindowDetails, below ? 3 : 1);
        pnlWindowDetails.Margin = new Thickness(0, below ? 12 : 0, 0, 0);
    }

    private void OnWindowsSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel.SetSelectedWindows(dgWindows.SelectedItems.OfType<MaintenanceWindowRow>());

    private void OnCoverageSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel.SetSelectedCoverage(dgCoverage.SelectedItems.OfType<CoverageRow>());
}
