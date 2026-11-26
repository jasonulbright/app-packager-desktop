using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class PublishResultsWindow
{
    public PublishResultsWindow(PublishResultsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        for (var i = 0; i < viewModel.Systems.Count; i++)
        {
            Matrix.Columns.Add(new DataGridTextColumn
            {
                Header = viewModel.Systems[i],
                Binding = new Binding($"Cells[{i}]"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 180
            });
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PublishRetryOffer offer }) return;
        ((PublishResultsViewModel)DataContext).RequestRetry(offer);
        Close();
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
