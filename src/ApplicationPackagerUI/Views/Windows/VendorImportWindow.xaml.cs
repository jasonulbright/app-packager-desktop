using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class VendorImportWindow
{
    public VendorImportWindow(VendorImportViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        ContentRendered += (_, _) => txtFeedAddress.Focus();
    }

    // A grid inside the scroll viewer would keep the mouse wheel; the wheel moves the window content instead.
    private void OnGridPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        scrollImport.ScrollToVerticalOffset(scrollImport.VerticalOffset - e.Delta);
        e.Handled = true;
    }
}
