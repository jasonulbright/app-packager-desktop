using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class WsusUpdatesWindow : ApplicationPackagerUI.Services.IDialogHostWindow
{
    public ContentDialogHost DialogHost => PromptHost;

    private readonly WsusUpdatesViewModel _viewModel;

    public WsusUpdatesWindow(WsusUpdatesViewModel viewModel, Action<WsusUpdatesWindow> openCatalogImport)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CatalogImportRequested += (_, _) => openCatalogImport(this);
        ContentRendered += async (_, _) => await viewModel.LoadAsync();
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel.SetSelection(gridUpdates.SelectedItems.OfType<WsusPublishedUpdate>());

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
