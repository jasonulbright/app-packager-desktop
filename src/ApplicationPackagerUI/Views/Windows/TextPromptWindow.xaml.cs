using System.ComponentModel;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class TextPromptWindow
{
    private readonly TextPromptViewModel _viewModel;

    public TextPromptWindow(TextPromptViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        if (!viewModel.Multiline) Height = 260;
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += (_, _) => txtValue.Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsBusy) e.Cancel = true;
        base.OnClosing(e);
    }
}
