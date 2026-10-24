using System.Windows;
using System.Windows.Input;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class HygieneFindingsPickerWindow
{
    private readonly HygieneFindingsPickerViewModel _viewModel;

    public HygieneFindingsPickerWindow(HygieneFindingsPickerViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        // The finding list scrolls inside the window, so the footer buttons stay on the screen with any number of findings.
        MaxHeight = SystemParameters.WorkArea.Height;
        MaxWidth = SystemParameters.WorkArea.Width;
        SourceInitialized += (_, _) =>
        {
            if (Height > MaxHeight) Height = MaxHeight;
        };
        ContentRendered += (_, _) => FocusFirstControl();
    }

    /// <summary>Puts the focus on the first finding, or on the filter when the list is empty.</summary>
    public void FocusFirstControl()
    {
        if (!Helpers.DataGridFocus.FocusFirstCell(gridHygienePick)) txtHygienePickFilter.Focus();
    }

    private void OnGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        _viewModel.ToggleSelected();
        e.Handled = true;
    }
}
