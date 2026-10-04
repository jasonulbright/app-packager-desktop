using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        ColIntune.Visibility = viewModel.ShowIntuneColumn ? Visibility.Visible : Visibility.Collapsed;

        KeyDown += OnPageKeyDown;
        Loaded += (_, _) => Focus();
    }

    private void OnPageKeyDown(object sender, KeyEventArgs e)
    {
        var vm = (PackagesViewModel)DataContext!;

        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            vm.ToggleSelectAllCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = vm.LoadPackagersCommand.ExecuteAsync(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.IsOperationRunning)
        {
            vm.CancelOperationCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.Control)
        {
            vm.ExportCsvCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;

        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        var vm = (PackagesViewModel)DataContext!;

        // The OLE drop source blocks until the handler returns; Explorer hangs through
        // the analysis and the review dialog unless the intake is queued behind it.
        Dispatcher.BeginInvoke(() => vm.HandleDroppedFilesCommand.ExecuteAsync(paths));
    }

    private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox cb)
        {
            var vm = (PackagesViewModel)DataContext!;
            var newState = cb.IsChecked == true;
            foreach (var p in vm.Packagers)
                p.IsSelected = newState;
            vm.IsAllSelected = newState;
        }
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
        else if (e.PropertyName == nameof(PackagesViewModel.ShowIntuneColumn))
        {
            ColIntune.Visibility = ((PackagesViewModel)DataContext!).ShowIntuneColumn ? Visibility.Visible : Visibility.Collapsed;
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

    private PackagerInfo? GetSelectedPackager() =>
        PackagerGrid.SelectedItem as PackagerInfo;

    /// <summary>The grid row the operator last selected, for the Application Workbench entry point.</summary>
    public PackagerInfo? SelectedPackager => GetSelectedPackager();

    private void OpenWorkbenchForSelection()
    {
        var p = GetSelectedPackager();
        if (Window.GetWindow(this) is Views.Windows.MainWindow main)
            main.OpenWorkbench(p is null ? null : WorkbenchApplicationCatalog.ApplicationIdFor(p));
    }

    private void ContextMenu_EditApplication(object sender, RoutedEventArgs e) => OpenWorkbenchForSelection();

    /// <summary>
    /// The theme gives every cell a Copy/Cut/Paste menu, which hides the grid's own menu
    /// on any row. A right-click on a row selects it and opens the grid's menu instead.
    /// </summary>
    private void PackagerGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || FindAncestor<DataGridRow>(source) is not { } row) return;
        if (PackagerGrid.ContextMenu is not { } menu) return;
        e.Handled = true;
        PackagerGrid.SelectedItem = row.Item;
        menu.PlacementTarget = PackagerGrid;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void PackagerGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // A double-click inside an editable cell (the selection checkbox) belongs to that cell, not to the row.
        if (e.OriginalSource is DependencyObject source
            && (FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(source) is not null
                || FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(source) is not null
                || FindAncestor<DataGridRow>(source) is null))
            return;
        if (GetSelectedPackager() is null) return;
        OpenWorkbenchForSelection();
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        return node as T;
    }

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
