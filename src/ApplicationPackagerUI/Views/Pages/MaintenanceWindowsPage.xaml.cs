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
    /// grid, the panel and its gap take 276 px by default; the five fixed columns take 560 px
    /// and the three star columns at least 270 px, so the grid scrolls sideways only below
    /// about 1120 px or after the user widens a column or the panel.
    /// </summary>
    public const double DetailsBesideMinWidth = 1100;

    public static bool DetailsBelow(double layoutWidth) => layoutWidth < DetailsBesideMinWidth;

    private GridLength _detailsWidth = new(260);

    private void OnWindowsLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        var below = DetailsBelow(e.NewSize.Width);
        if (below == (Grid.GetRow(pnlWindowDetails) == 1)) return;
        var layout = gridWindowsLayout;
        if (below) _detailsWidth = layout.ColumnDefinitions[2].Width;
        layout.ColumnDefinitions[1].Width = new GridLength(below ? 0 : 16);
        layout.ColumnDefinitions[2].MinWidth = below ? 0 : 220;
        layout.ColumnDefinitions[2].Width = below ? new GridLength(0) : _detailsWidth;
        splitWindowDetails.Visibility = below ? Visibility.Collapsed : Visibility.Visible;
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
