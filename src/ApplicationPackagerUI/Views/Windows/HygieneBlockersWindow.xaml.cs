using System.Windows;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class HygieneBlockersWindow
{
    public HygieneBlockersWindow(HygieneBlockersViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        // The list scrolls inside the window, so the Close button stays on the screen with any number of objects.
        MaxHeight = SystemParameters.WorkArea.Height;
        MaxWidth = SystemParameters.WorkArea.Width;
        SourceInitialized += (_, _) =>
        {
            if (Height > MaxHeight) Height = MaxHeight;
        };
        ContentRendered += (_, _) =>
        {
            if (!Helpers.DataGridFocus.FocusFirstCell(gridBlockers)) btnBlockersClose.Focus();
        };
    }
}
