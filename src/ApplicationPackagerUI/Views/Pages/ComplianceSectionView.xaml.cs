using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.Views.Pages;

public partial class ComplianceSectionView : UserControl
{
    private ComplianceViewModel? _viewModel;
    private GridLength _detailsWidth = new(380);
    private GridLength _deploymentsHeight = new(1, GridUnitType.Star);

    public ComplianceSectionView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ComplianceViewModel);
    }

    private void Attach(ComplianceViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.Catalog.PropertyChanged -= OnCatalogChanged;
            _viewModel.Live.PropertyChanged -= OnLiveChanged;
        }
        _viewModel = viewModel;
        if (viewModel is null) return;
        viewModel.Catalog.PropertyChanged += OnCatalogChanged;
        viewModel.Live.PropertyChanged += OnLiveChanged;
        ApplyCatalogDetails();
        ApplyDeployments();
    }

    private void OnCatalogChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComplianceCatalogViewModel.ShowDetails)) ApplyCatalogDetails();
    }

    private void OnLiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComplianceLiveViewModel.ShowDeployments)) ApplyDeployments();
    }

    /// <summary>
    /// A hidden details panel gives its column to the grid; showing it again restores the width
    /// the splitter last gave it. The splitter sets the column width directly, so no binding can hold it.
    /// </summary>
    private void ApplyCatalogDetails()
    {
        if (_viewModel is null) return;
        var column = gridCatalogLayout.ColumnDefinitions[2];
        var show = _viewModel.Catalog.ShowDetails;
        if (show)
        {
            column.MinWidth = 240;
            column.Width = _detailsWidth;
        }
        else
        {
            if (column.Width.Value > 0) _detailsWidth = column.Width;
            column.MinWidth = 0;
            column.Width = new GridLength(0);
        }
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        splitCatalogDetails.Visibility = visibility;
        pnlCatalogDetails.Visibility = visibility;
    }

    private void ApplyDeployments()
    {
        if (_viewModel is null) return;
        var row = ((Grid)dgDeployments.Parent).RowDefinitions[4];
        if (_viewModel.Live.ShowDeployments)
        {
            row.MinHeight = 140;
            row.Height = _deploymentsHeight;
        }
        else
        {
            if (row.Height.Value > 0) _deploymentsHeight = row.Height;
            row.MinHeight = 0;
            row.Height = new GridLength(0);
        }
    }

    private void OnDeploymentsSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel?.Live.SetSelectedDeployments(dgDeployments.SelectedItems.OfType<DeploymentGridRow>());
}
