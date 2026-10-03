using System.Windows;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class WsusCatalogImportWindow : ApplicationPackagerUI.Services.IDialogHostWindow
{
    public ContentDialogHost DialogHost => PromptHost;

    private readonly WsusCatalogImportViewModel _viewModel;

    public WsusCatalogImportWindow(WsusCatalogImportViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Closing += (_, _) => _viewModel.CancelCommand.Execute(null);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
