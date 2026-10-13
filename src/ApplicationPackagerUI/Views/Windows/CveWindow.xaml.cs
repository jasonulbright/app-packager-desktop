using System.Windows;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class CveWindow
{
    private readonly CveWindowViewModel _viewModel;
    private readonly PackagesViewModel _packages;

    public CveWindow(CveWindowViewModel viewModel, PackagesViewModel packages)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        InitializeComponent();
        DataContext = viewModel;
        Title = $"CVEs — {viewModel.Row.Application}";
    }

    /// <summary>
    /// Opens the window for a row beside its owner. The window is modeless: the main
    /// window's progress and Cancel stay usable while a refresh runs.
    /// </summary>
    public static async Task<CveWindow> ShowForAsync(Window? owner, PackagerInfo row, PackagesViewModel packages)
    {
        var cached = await Task.Run(() => PackagesViewModel.CachedCveResult(row));
        var window = new CveWindow(new CveWindowViewModel(row, cached), packages);
        if (owner is not null)
        {
            window.Owner = owner;
            var area = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            // A maximized window reports its restore position in Left and Top.
            var ownerBounds = owner.WindowState == WindowState.Maximized
                ? SystemParameters.WorkArea
                : new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);
            var corner = CveWindowPlacement.Beside(ownerBounds, new Size(window.Width, window.Height), area);
            window.Left = corner.X;
            window.Top = corner.Y;
        }
        window.Show();
        return window;
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CveLink link }) _packages.OpenExternalLink(link.Url);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var row = _viewModel.Row;
        if (!_viewModel.CanLookUp)
        {
            _viewModel.ShowNote(CveWindowViewModel.RefreshNeedsVersions);
            return;
        }
        if (_packages.IsOperationRunning)
        {
            _viewModel.ShowNote(CveWindowViewModel.AnotherRunInProgress);
            return;
        }

        _viewModel.CanRefresh = false;
        _viewModel.ShowNote(CveWindowViewModel.LookingUp);
        try
        {
            var context = await _packages.RunCveCheckAsync([row], force: true);
            if (context is null)
            {
                _viewModel.ShowNote(CveWindowViewModel.AnotherRunInProgress);
                return;
            }
            // The run's own result first: a cache write can fail after a good lookup. The
            // cache holds the last good result when the lookup itself failed.
            var fresh = context.Results.GetValueOrDefault(row.Uid);
            var shown = fresh is not null && string.IsNullOrEmpty(fresh.Error)
                ? fresh
                : await Task.Run(() => PackagesViewModel.CachedCveResult(row));
            _viewModel.Render(shown);
            if (row.CveCount == ApplicationPackagerEngine.Services.Vulnerability.VulnerabilityGridCells.Error)
                _viewModel.ShowNote(CveWindowViewModel.LookupFailed);
        }
        catch (Exception ex)
        {
            _packages.AppendLog($"CVE window update failed: {ex.Message}");
            _viewModel.ShowNote(CveWindowViewModel.LookupFailed);
        }
        finally
        {
            _viewModel.CanRefresh = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var ids = _viewModel.CveIds;
        if (ids.Count > 0)
        {
            try { Clipboard.SetText(string.Join(Environment.NewLine, ids)); }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                _packages.AppendLog($"CVE ids not copied: {ex.Message}");
                return;
            }
        }
        _packages.AppendLog($"{ids.Count} CVE id(s) copied.");
    }

    private async void Export_Click(object sender, RoutedEventArgs e) => await _packages.WriteCveReportAsync([_viewModel.Row]);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
