using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class SetupWindow
{
    public SetupWindowViewModel ViewModel { get; }

    public SetupWindow(SetupWindowViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.CloseRequested += (_, saved) =>
        {
            DialogResult = saved;
            Close();
        };
    }
}
