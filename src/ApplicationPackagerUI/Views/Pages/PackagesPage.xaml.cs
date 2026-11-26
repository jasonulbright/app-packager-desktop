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
        viewModel.ImportFromUrlRequested += (_, _) => ShowImportFromUrl(viewModel);
        ColIntune.Visibility = viewModel.ShowIntuneColumn ? Visibility.Visible : Visibility.Collapsed;
        ColSource.Visibility = viewModel.ShowSourceColumn ? Visibility.Visible : Visibility.Collapsed;
        ApplySavedColumns();
        InitializeColumnLayout();

        PreviewKeyDown += OnPageKeyDown;
        Loaded += (_, _) =>
        {
            Focus();
            // A narrow window starts with the pane closed; the saved choice is not changed.
            _narrow = ActualWidth > 0 && ActualWidth + NavigationWidth < DetailPaneMinimumWindowWidth;
            ApplyPane();
        };
    }

    private void ShowImportFromUrl(PackagesViewModel viewModel)
    {
        var owner = Window.GetWindow(this);
        Windows.VendorImportWindow? window = null;
        var import = new ViewModels.Windows.VendorImportViewModel(
            VendorImportService.CreateDefault(),
            (title, message) => Task.FromResult(System.Windows.MessageBox.Show(window ?? owner, message, title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes));
        window = new Windows.VendorImportWindow(import) { Owner = owner };
        window.ShowDialog();
        if (import.ImportedAny) _ = viewModel.LoadPackagersCommand.ExecuteAsync(null);
    }

    private void OnPageKeyDown(object sender, KeyEventArgs e)
    {
        // Text boxes keep Ctrl+A and Ctrl+E for themselves.
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase
            && Keyboard.Modifiers == ModifierKeys.Control && e.Key is (Key.A or Key.E)) return;

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
        // The header is tri-state: the view model owns the value, the click only advances its cycle.
        var vm = (PackagesViewModel)DataContext!;
        vm.CycleSelectionCommand.Execute(null);
        if (sender is System.Windows.Controls.CheckBox cb) cb.IsChecked = vm.HeaderSelection;
        e.Handled = true;
    }

    /// <summary>Hides the columns that the operator hid with the Columns menu; the others keep their own rule.</summary>
    private void ApplySavedColumns()
    {
        var hidden = ((PackagesViewModel)DataContext!).HiddenColumns;
        foreach (var column in PackagerGrid.Columns)
            if (column.Header is string header && Helpers.PackagesColumns.Toggleable.Contains(header))
                column.Visibility = hidden.Contains(header) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The Columns menu: one check item for each column that may be hidden, and Reset columns. Each choice is saved at once.</summary>
    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = (PackagesViewModel)DataContext!;
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        var hidden = viewModel.HiddenColumns;
        foreach (var header in Helpers.PackagesColumns.Toggleable)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header, IsCheckable = true, IsChecked = !hidden.Contains(header), StaysOpenOnClick = true };
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, "miColumn" + new string(header.Where(char.IsLetterOrDigit).ToArray()));
            item.Click += (_, _) => { viewModel.SetColumnVisible(header, item.IsChecked); ApplySavedColumns(); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new System.Windows.Controls.Separator());
        var reset = new System.Windows.Controls.MenuItem { Header = "Reset columns" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(reset, "miColumnsReset");
        reset.Click += (_, _) => { viewModel.ResetColumns(); ApplySavedColumns(); ResetColumnLayout(); };
        menu.Items.Add(reset);
        menu.IsOpen = true;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackagesViewModel.HeaderSelection))
            SelectAllCheckBox.IsChecked = ((PackagesViewModel)DataContext!).HeaderSelection;
        if (e.PropertyName == nameof(PackagesViewModel.ShowDebugColumns))
        {
            var show = ((PackagesViewModel)DataContext!).ShowDebugColumns;
            var vis = show ? Visibility.Visible : Visibility.Collapsed;
            ColCMName.Visibility = vis;
            ColScript.Visibility = vis;
            ColCveEpss.Visibility = vis;
            ColCveUpdated.Visibility = vis;
            ColCveChecked.Visibility = vis;
        }
        else if (e.PropertyName == nameof(PackagesViewModel.ShowSourceColumn))
        {
            ColSource.Visibility = ((PackagesViewModel)DataContext!).ShowSourceColumn ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (e.PropertyName == nameof(PackagesViewModel.ShowIntuneColumn))
        {
            ColIntune.Visibility = ((PackagesViewModel)DataContext!).ShowIntuneColumn ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (e.PropertyName == nameof(PackagesViewModel.FocusedPackager) && ((PackagesViewModel)DataContext!).FocusedPackager is { } row)
        {
            PackagerGrid.SelectedItem = row;
            PackagerGrid.ScrollIntoView(row);
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

    private void PackagerGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is PackagesViewModel viewModel) viewModel.DetailPackager = GetSelectedPackager();
    }

    /// <summary>Below this window width the pane starts closed; the saved choice is kept.</summary>
    public const double DetailPaneMinimumWindowWidth = 1100;

    private const double NavigationWidth = 230;
    private bool _narrow;

    /// <summary>Opens or closes the pane: the column width and the visibility of its content, so a closed pane leaves the tab order.</summary>
    private void ApplyPane()
    {
        var open = ((PackagesViewModel)DataContext!).IsDetailPaneOpen && !_narrow;
        DetailPaneColumn.Width = new GridLength(open ? 320 : 0);
        DetailPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        btnDetailPane.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DetailOpenWorkbench_Click(object sender, RoutedEventArgs e) => OpenWorkbenchForSelection();

    /// <summary>Closing the pane returns the keyboard focus to the button that opens it (R-21).</summary>
    private void DetailClose_Click(object sender, RoutedEventArgs e)
    {
        ((PackagesViewModel)DataContext!).IsDetailPaneOpen = false;
        ApplyPane();
        btnDetailPane.Focus();
    }

    private void DetailOpen_Click(object sender, RoutedEventArgs e)
    {
        _narrow = false;
        ((PackagesViewModel)DataContext!).IsDetailPaneOpen = true;
        ApplyPane();
    }

    private Views.Windows.CatalogWindow? _catalog;

    /// <summary>Opens the Catalog window, or brings the open one to the front.</summary>
    private void Catalog_Click(object sender, RoutedEventArgs e)
    {
        if (_catalog is { IsVisible: true })
        {
            _catalog.Activate();
            return;
        }
        _catalog = App.Services.GetRequiredService<Views.Windows.CatalogWindow>();
        _catalog.Owner = Window.GetWindow(this);
        _catalog.Closed += (_, _) => _catalog = null;
        _catalog.Show();
    }

    private Views.Windows.ConvertFromCmWindow? _convertFromCm;

    /// <summary>Opens the ConfigMgr conversion window, or brings the open one to the front.</summary>
    private void ConvertFromCm_Click(object sender, RoutedEventArgs e)
    {
        if (_convertFromCm is { IsVisible: true })
        {
            _convertFromCm.Activate();
            return;
        }
        _convertFromCm = App.Services.GetRequiredService<Views.Windows.ConvertFromCmWindow>();
        _convertFromCm.Owner = Window.GetWindow(this);
        _convertFromCm.Closed += (_, _) => _convertFromCm = null;
        _convertFromCm.Show();
    }

    private void ViewHistory_Click(object sender, RoutedEventArgs e) => OpenHistoryForSelection();

    private void ContextMenu_ViewHistory(object sender, RoutedEventArgs e) => OpenHistoryForSelection();

    private void OpenHistoryForSelection()
    {
        if (GetSelectedPackager() is not { } packager) return;

        var inventory = App.Services.GetRequiredService<ApplicationPackagerEngine.Services.ApplicationInventoryService>();
        var history = new Views.Windows.OperationHistoryWindow(packager, inventory)
        {
            Owner = Window.GetWindow(this)
        };
        history.ShowDialog();
    }

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
            && (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null
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
