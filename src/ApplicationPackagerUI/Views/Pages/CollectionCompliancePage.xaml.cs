using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ApplicationPackagerUI.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace ApplicationPackagerUI.Views.Pages;

public partial class CollectionCompliancePage : INavigationAware
{
    private readonly CollectionComplianceViewModel _viewModel;

    public CollectionCompliancePage(CollectionComplianceViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        BuildPreviewColumns();
    }

    public Task OnNavigatedToAsync() => _viewModel.OnNavigatedToAsync();

    public Task OnNavigatedFromAsync() => _viewModel.OnNavigatedFromAsync();

    /// <summary>
    /// Below this width of the Collections area, the detail panel moves under the grid. Beside the
    /// grid, the panel and its gap take 376 px by default and the seven columns at least 726 px.
    /// </summary>
    public const double DetailsBesideMinWidth = 1100;

    public static bool DetailsBelow(double layoutWidth) => layoutWidth < DetailsBesideMinWidth;

    private GridLength _detailsWidth = new(360);

    private void OnCollectionsLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        var below = DetailsBelow(e.NewSize.Width);
        if (below == (Grid.GetRow(pnlCollectionDetails) == 1)) return;
        var layout = gridCollectionsLayout;
        if (below) _detailsWidth = layout.ColumnDefinitions[2].Width;
        layout.ColumnDefinitions[1].Width = new GridLength(below ? 0 : 16);
        layout.ColumnDefinitions[2].MinWidth = below ? 0 : 300;
        layout.ColumnDefinitions[2].Width = below ? new GridLength(0) : _detailsWidth;
        splitCollectionDetails.Visibility = below ? Visibility.Collapsed : Visibility.Visible;
        layout.RowDefinitions[1].Height = below ? new GridLength(300) : GridLength.Auto;
        Grid.SetRow(pnlCollectionDetails, below ? 1 : 0);
        Grid.SetColumn(pnlCollectionDetails, below ? 0 : 2);
        Grid.SetColumnSpan(pnlCollectionDetails, below ? 3 : 1);
        pnlCollectionDetails.Margin = new Thickness(0, below ? 12 : 0, 0, 0);
    }

    private void OnCollectionsSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel.SetSelectedRows(dgCollections.SelectedItems.OfType<CollectionGridRow>());

    private void OnMembersSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel.SetSelectedMembers(dgMembers.SelectedItems.OfType<MemberRow>());

    private void OnWqlTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is CollectionTreeNode { Collection: { } collection }) _viewModel.WqlCollection = collection;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CollectionComplianceViewModel.PreviewColumns)) BuildPreviewColumns();
    }

    /// <summary>The preview columns come from the query; each binds to the row's indexer, so a column name with a dot is no binding path.</summary>
    private void BuildPreviewColumns()
    {
        dgPreview.Columns.Clear();
        var style = (Style)FindResource("TrimmedCell");
        var columns = _viewModel.PreviewColumns;
        for (var i = 0; i < columns.Count; i++)
        {
            dgPreview.Columns.Add(new DataGridTextColumn
            {
                Header = columns[i],
                Binding = new Binding($"[{i}]") { Mode = BindingMode.OneWay },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 120,
                ElementStyle = style
            });
        }
    }
}
