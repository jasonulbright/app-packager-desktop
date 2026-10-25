using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class PivotsCommentWindow
{
    public PivotsCommentWindow(PivotsCommentViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        ContentRendered += (_, _) => txtComment.Focus();
    }
}
