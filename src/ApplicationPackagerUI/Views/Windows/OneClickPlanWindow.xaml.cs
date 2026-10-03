using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class OneClickPlanWindow : ApplicationPackagerUI.Services.IDialogHostWindow
{
    public ContentDialogHost DialogHost => PromptHost;

    private readonly OneClickPlanViewModel _viewModel;
    private readonly string _reportFolder;

    public OneClickPlanWindow(OneClickPlanViewModel viewModel, string reportFolder)
    {
        _viewModel = viewModel;
        _reportFolder = reportFolder;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.PropertyChanged += OnViewModelChanged;
        Closing += OnClosing;
        ApplyColumns();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OneClickPlanViewModel.IsRunView) or nameof(OneClickPlanViewModel.ShowResultConfigMgr)
            or nameof(OneClickPlanViewModel.ShowResultWsus) or nameof(OneClickPlanViewModel.ShowResultIntune))
            ApplyColumns();
    }

    /// <summary>
    /// During a run the result columns of the destinations in use take the place of the
    /// destination boxes and the plan columns; with both shown, a result column is
    /// narrower than one package id.
    /// </summary>
    private void ApplyColumns()
    {
        var run = _viewModel.IsRunView;
        static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
        colMecm.Visibility = colWsus.Visibility = colIntune.Visibility = Show(!run);
        colLast.Visibility = colPlanned.Visibility = Show(!run);
        colStep.Visibility = Show(run);
        colResultMecm.Visibility = Show(run && _viewModel.ShowResultConfigMgr);
        colResultWsus.Visibility = Show(run && _viewModel.ShowResultWsus);
        colResultIntune.Visibility = Show(run && _viewModel.ShowResultIntune);
        colReason.Width = run ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(230);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel.IsRunning) e.Cancel = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsRunning) Close();
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        var path = _viewModel.ReportPath;
        if (path.Length == 0 || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_reportFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_reportFolder}\""));
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) { }
    }
}
