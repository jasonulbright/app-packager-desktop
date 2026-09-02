using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class SetupWindow
{
    public SetupWindow(SetupWindowViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        viewModel.CloseRequested += (_, saved) =>
        {
            DialogResult = saved;
            Close();
        };
    }
}
