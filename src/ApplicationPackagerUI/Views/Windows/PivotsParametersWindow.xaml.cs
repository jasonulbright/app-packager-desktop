using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ApplicationPackagerUI.ViewModels.Windows;
using TextBox = System.Windows.Controls.TextBox;

namespace ApplicationPackagerUI.Views.Windows;

public partial class PivotsParametersWindow
{
    public PivotsParametersWindow(PivotsParametersViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        MaxHeight = SystemParameters.WorkArea.Height;
        MaxWidth = SystemParameters.WorkArea.Width;
        ContentRendered += (_, _) => FocusFirstControl();
    }

    /// <summary>Puts the focus on the first value field, or on OK when the script has no parameters.</summary>
    public void FocusFirstControl()
    {
        if (First<TextBox>(listParameters) is { } box && box.Focus()) return;
        if (!btnOk.Focus()) btnCancel.Focus();
    }

    private static T? First<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (First<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}
