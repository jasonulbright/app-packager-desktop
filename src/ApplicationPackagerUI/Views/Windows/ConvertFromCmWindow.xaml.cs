using System.Windows;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class ConvertFromCmWindow
{
    public ConvertFromCmWindow(ConvertFromCmViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        txtIntro.Text = ConvertFromCmViewModel.IntroText;
        // A closed window has no Cancel button, so a running operation stops with it.
        Closing += (_, _) => { if (viewModel.CancelCommand.CanExecute(null)) viewModel.CancelCommand.Execute(null); };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
