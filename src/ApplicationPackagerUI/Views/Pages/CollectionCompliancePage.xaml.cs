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
