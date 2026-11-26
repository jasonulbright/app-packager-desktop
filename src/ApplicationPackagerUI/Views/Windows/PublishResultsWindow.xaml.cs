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
            Matrix.Columns.Add(PublishMatrixColumns.For(viewModel.Systems[i], i, 180));
        }
    }

    private PublishResultsViewModel Results => (PublishResultsViewModel)DataContext;

    private void History_Click(object sender, RoutedEventArgs e)
    {
        if ((Matrix.SelectedItem as PublishResultsRow ?? Results.FirstFailed ?? Results.Rows.FirstOrDefault()) is { } row)
            Results.ShowHistory?.Invoke(row.Uid, this);
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (!System.IO.File.Exists(Results.ReportPath)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Results.ReportPath) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private void Logs_Click(object sender, RoutedEventArgs e) => Results.ShowLogs?.Invoke(this);

    private void Review_Click(object sender, RoutedEventArgs e)
    {
        if (Results.FirstFailed is not { } row) return;
        Results.RequestReview(row);
        Close();
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PublishRetryOffer offer }) return;
        ((PublishResultsViewModel)DataContext).RequestRetry(offer);
        Close();
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
