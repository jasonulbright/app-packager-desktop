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
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
