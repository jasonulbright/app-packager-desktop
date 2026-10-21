using System.Windows.Input;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class RingCollectionPickerWindow
{
    private readonly RingCollectionPickerViewModel _viewModel;

    public RingCollectionPickerWindow(RingCollectionPickerViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += (_, _) => txtFilter.Focus();
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.PickCommand.CanExecute(null)) _viewModel.PickCommand.Execute(null);
    }
}
