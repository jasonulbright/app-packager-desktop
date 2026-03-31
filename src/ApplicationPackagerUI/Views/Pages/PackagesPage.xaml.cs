using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerUI.Views.Pages;

public partial class PackagesPage
{
    public PackagesPage(PackagesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackagesViewModel.ShowDebugColumns))
        {
            var show = ((PackagesViewModel)DataContext!).ShowDebugColumns;
            var vis = show ? Visibility.Visible : Visibility.Collapsed;
            ColCMName.Visibility = vis;
            ColScript.Visibility = vis;
        }
    }

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb)
        {
            tb.CaretIndex = tb.Text.Length;
            tb.ScrollToEnd();
        }
    }

    // ── Package dropdown ───────────────────────────────────────────

    private void PackageButton_Click(object sender, RoutedEventArgs e)
    {
        var vm = (PackagesViewModel)DataContext!;
        var menu = new System.Windows.Controls.ContextMenu();

        var mecm = new System.Windows.Controls.MenuItem { Header = "MECM", Icon = new SymbolIcon(SymbolRegular.Server24) };
        mecm.Click += (_, _) => _ = vm.PackageCommand.ExecuteAsync(null);
        menu.Items.Add(mecm);

        var psadt = new System.Windows.Controls.MenuItem { Header = "MECM + PSADT", IsEnabled = false, Icon = new SymbolIcon(SymbolRegular.WindowConsole20) };
        menu.Items.Add(psadt);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var intune = new System.Windows.Controls.MenuItem { Header = "Intune", IsEnabled = false, Icon = new SymbolIcon(SymbolRegular.Cloud24) };
        menu.Items.Add(intune);

        menu.PlacementTarget = sender as UIElement;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ── Context menu handlers ────────────────────────────────────

    private PackagerInfo? GetSelectedPackager() =>
        PackagerGrid.SelectedItem as PackagerInfo;

    private void ContextMenu_OpenLogFolder(object sender, RoutedEventArgs e)
    {
        var settings = App.Services.GetRequiredService<SettingsService>().Load();
        var logDir = Path.Combine(settings.DownloadRoot, "Logs");
        if (Directory.Exists(logDir))
            Process.Start(new ProcessStartInfo("explorer.exe", logDir));
    }

    private void ContextMenu_OpenStagedFolder(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedPackager();
        if (p is null) return;

        var settings = App.Services.GetRequiredService<SettingsService>().Load();
        var dlRoot = settings.DownloadRoot;
        if (!Directory.Exists(dlRoot)) return;

        var baseName = Path.GetFileNameWithoutExtension(p.Script)
            .Replace("package-", "", StringComparison.OrdinalIgnoreCase);

        var candidates = Directory.GetDirectories(dlRoot)
            .Where(d => Path.GetFileName(d)!.Contains(baseName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var target = candidates.Length > 0 ? candidates[0] : dlRoot;
        Process.Start(new ProcessStartInfo("explorer.exe", target));
    }

    private void ContextMenu_OpenNetworkShare(object sender, RoutedEventArgs e)
    {
        var settings = App.Services.GetRequiredService<SettingsService>().Load();
        if (!string.IsNullOrEmpty(settings.FileShareRoot))
            Process.Start(new ProcessStartInfo("explorer.exe", settings.FileShareRoot));
    }

    private void ContextMenu_CopyLatestVersion(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedPackager();
        if (p is not null && !string.IsNullOrEmpty(p.LatestVersion))
            Clipboard.SetText(p.LatestVersion);
    }

    private void ContextMenu_OpenVendorUrl(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedPackager();
        if (p is not null && !string.IsNullOrEmpty(p.VendorUrl))
            Process.Start(new ProcessStartInfo(p.VendorUrl) { UseShellExecute = true });
    }
}
