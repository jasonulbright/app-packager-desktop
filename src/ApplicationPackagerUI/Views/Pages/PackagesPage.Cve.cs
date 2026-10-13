using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.Views.Pages;

public partial class PackagesPage
{
    private readonly Services.OpenOncePerKey<Views.Windows.CveWindow> _cveWindows = new();

    private PackagesViewModel Packages => (PackagesViewModel)DataContext!;

    private async void CveCell_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: PackagerInfo row }) await OpenCveWindowAsync(row);
    }

    private async void ContextMenu_ShowCves(object sender, RoutedEventArgs e)
    {
        if (GetSelectedPackager() is { } row) await OpenCveWindowAsync(row);
    }

    private async Task OpenCveWindowAsync(PackagerInfo row)
    {
        try
        {
            // A double-click raises two clicks before the first window exists.
            var key = row.Uid.Length > 0 ? row.Uid : row.FullPath;
            var window = await _cveWindows.GetOrOpen(key,
                () => Views.Windows.CveWindow.ShowForAsync(Window.GetWindow(this), row, Packages), out var existing);
            if (existing)
            {
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            }
            else
            {
                window.Closed += (_, _) => _cveWindows.Closed(key);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Packages.AppendLog($"CVE window not opened: {ex.Message}");
        }
    }

    /// <summary>Rebuilds the CVE source submenu for the selected row; a row without a CPE has no sources.</summary>
    private void PackagerMenu_Opened(object sender, RoutedEventArgs e)
    {
        MenuOpenCveSources.Items.Clear();
        var row = GetSelectedPackager();
        var hasCpe = row is not null && row.Cpe.Length > 0;
        MenuShowCves.IsEnabled = row is not null;
        MenuOpenCveSources.IsEnabled = hasCpe;
        MenuCopyCveIds.IsEnabled = hasCpe;
        if (!hasCpe) return;
        foreach (var (name, url) in PackagesViewModel.CveSourceLinks(row!))
        {
            var item = new System.Windows.Controls.MenuItem { Header = name, Tag = url, ToolTip = url };
            item.Click += (sender, _) => Packages.OpenExternalLink((sender as System.Windows.Controls.MenuItem)?.Tag as string);
            MenuOpenCveSources.Items.Add(item);
        }
    }

    private async void ContextMenu_CopyCveIds(object sender, RoutedEventArgs e)
    {
        if (GetSelectedPackager() is not { } row) return;
        var result = await Task.Run(() => PackagesViewModel.CachedCveResult(row));
        if (result is null)
        {
            Packages.AppendLog("No CVE lookup for this row yet. Run Check CVEs first.");
            return;
        }
        var ids = result.Cves.Select(c => c.Id).ToList();
        if (ids.Count > 0)
        {
            try { Clipboard.SetText(string.Join(Environment.NewLine, ids)); }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                Packages.AppendLog($"CVE ids not copied: {ex.Message}");
                return;
            }
        }
        Packages.AppendLog($"{ids.Count} CVE id(s) copied.");
    }
}
