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
    private readonly ComplianceViewModel _compliance;

    public CollectionCompliancePage(CollectionComplianceViewModel viewModel, ComplianceViewModel compliance)
    {
        _viewModel = viewModel;
        _compliance = compliance;
        DataContext = viewModel;
        InitializeComponent();
        viewCompliance.DataContext = compliance;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        BuildPreviewColumns();
        ApplyDetails();
        pnlCollectionFilters.SizeChanged += (_, _) => UpdateTabMinHeight();
        pnlCollectionTools.SizeChanged += (_, _) => UpdateTabMinHeight();
    }

    private const double GridMinHeight = 160;
    private const double DetailsMinHeight = 140;

    /// <summary>
    /// The Collections tab needs its filter and tool rows at their wrapped height, the grid's
    /// minimum and the details at their height; a lower viewport scrolls the tab instead of
    /// cutting the details.
    /// </summary>
    private void UpdateTabMinHeight()
    {
        var rows = gridCollectionsLayout.RowDefinitions;
        var detailsHeight = rows[2].Height.IsAbsolute ? rows[2].Height.Value : rows[2].ActualHeight;
        var details = _viewModel.ShowDetails ? Math.Max(detailsHeight, DetailsMinHeight) + rows[1].Height.Value : 0;
        gridCollectionsTab.MinHeight = pnlCollectionFilters.ActualHeight + pnlCollectionFilters.Margin.Bottom
            + pnlCollectionTools.ActualHeight + pnlCollectionTools.Margin.Bottom + GridMinHeight + details;
    }

    private void OnDetailsResized(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) => UpdateTabMinHeight();

    private GridLength _detailsHeight = new(240);

    /// <summary>
    /// A hidden details panel gives its row to the grid; showing it again restores the height the
    /// splitter last gave it. The splitter sets the row height directly, so no binding can hold it.
    /// </summary>
    private void ApplyDetails()
    {
        var row = gridCollectionsLayout.RowDefinitions[2];
        var show = _viewModel.ShowDetails;
        if (show)
        {
            row.MinHeight = DetailsMinHeight;
            row.Height = _detailsHeight;
        }
        else
        {
            if (row.Height.Value > 0) _detailsHeight = row.Height;
            row.MinHeight = 0;
            row.Height = new GridLength(0);
        }
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        splitCollectionDetails.Visibility = visibility;
        pnlCollectionDetails.Visibility = visibility;
        UpdateTabMinHeight();
    }

    public async Task OnNavigatedToAsync()
    {
        await _viewModel.OnNavigatedToAsync();
        await _compliance.OnNavigatedToAsync(_viewModel.IsComplianceSection);
    }

    public Task OnNavigatedFromAsync() => Task.WhenAll(_viewModel.OnNavigatedFromAsync(), _compliance.OnNavigatedFromAsync());

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
        if (e.PropertyName == nameof(CollectionComplianceViewModel.ShowDetails)) ApplyDetails();
        if (e.PropertyName == nameof(CollectionComplianceViewModel.SelectedSection))
        {
            if (_viewModel.IsComplianceSection) _ = _compliance.ShowAsync();
            else _compliance.Hide();
        }
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
                MinWidth = Math.Max(100, HeaderWidth(columns[i])),
                ElementStyle = style
            });
        }
    }

    /// <summary>The width a column header needs for its text, with the header padding and the sort glyph space.</summary>
    private double HeaderWidth(string text) =>
        Math.Ceiling(new System.Windows.Media.FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new System.Windows.Media.Typeface(dgPreview.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), Math.Max(dgPreview.FontSize, 14),
            System.Windows.Media.Brushes.Black, System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace) + 40;
}
