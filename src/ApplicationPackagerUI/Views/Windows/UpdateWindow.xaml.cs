using System.Diagnostics;
using System.Windows;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class UpdateWindow
{
    private readonly UpdateViewModel _viewModel;

    public UpdateWindow(UpdateViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Notes_Click(object sender, RoutedEventArgs e)
    {
        // Only an https address of the repository's releases opens.
        var url = _viewModel.ReleaseUrl;
        if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _viewModel.StatusText = $"The release page did not open: {ex.Message}";
        }
    }
}
