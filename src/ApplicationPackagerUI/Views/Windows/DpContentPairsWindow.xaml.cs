using System.Windows;
using System.Windows.Input;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class DpContentPairsWindow
{
    private readonly DpContentPairsViewModel _viewModel;

    public DpContentPairsWindow(DpContentPairsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        // The pair list scrolls inside the window, so the footer buttons stay on the screen with any number of pairs.
        MaxHeight = SystemParameters.WorkArea.Height;
        MaxWidth = SystemParameters.WorkArea.Width;
        SourceInitialized += (_, _) =>
        {
            if (Height > MaxHeight) Height = MaxHeight;
        };
    }

    private void OnGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        _viewModel.ToggleSelected();
        e.Handled = true;
    }
}
