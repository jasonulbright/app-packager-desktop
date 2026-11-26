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

        // The checklist panel has its own data context: a view model without a checklist (a test) shows neither the panel nor the strip.
        SetupPanel.DataContext = viewModel.Checklist;
        if (viewModel.Checklist is { } checklist) checklist.StateChanged += ApplyPane;

        SizeChanged += (_, _) =>
        {
            ApplyLogRow();
            PlaceChecklist();
        };
        WorkArea.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged) FitDetailPane();
        };

        PreviewKeyDown += OnPageKeyDown;
        Loaded += (_, _) =>
        {
            Focus();
            // A narrow window starts with the pane closed; the saved choice is not changed.
            _narrow = ActualWidth > 0 && ActualWidth + NavigationWidth < DetailPaneMinimumWindowWidth;
            _startedNarrow = _narrow;
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

    private void OpenWorkbenchForSelection() => OpenWorkbench(GetSelectedPackager());

    private void OpenWorkbench(PackagerInfo? p)
    {
        if (Window.GetWindow(this) is Views.Windows.MainWindow main)
            main.OpenWorkbench(p is null ? null : WorkbenchApplicationCatalog.ApplicationIdFor(p));
    }

    private void PackagerGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is PackagesViewModel viewModel) viewModel.DetailPackager = GetSelectedPackager();
    }

    /// <summary>Below this window width the pane starts closed; the saved choice is kept.</summary>
    public const double DetailPaneMinimumWindowWidth = 1100;

    private const double NavigationWidth = 230;
    private bool _narrow;
    private bool _startedNarrow;

    /// <summary>Below this page height the log gives its room to the setup strip while the strip shows.</summary>
    private const double ShortPageHeight = 800;

    private const double ShortLogHeight = 40;

    /// <summary>
    /// The log row shares the height with the grid. In a short page with the setup strip it has a small fixed height, so the grid keeps its
    /// whole rows.
    /// </summary>
    private void ApplyLogRow()
    {
        var small = SetupStrip.Visibility == Visibility.Visible && ActualHeight is > 0 and < ShortPageHeight;
        LogRow.MinHeight = small ? ShortLogHeight : 72;
        LogRow.Height = small ? new GridLength(ShortLogHeight) : new GridLength(1, GridUnitType.Star);
    }

    /// <summary>
    /// Fills the right column: the setup checklist when it is open, else the detail pane. It sets the column width and the visibility of the
    /// content, so closed content leaves the tab order. A window that started narrow shows the strip until the user opens the checklist.
    /// </summary>
    private void ApplyPane()
    {
        var viewModel = (PackagesViewModel)DataContext!;
        var checklist = viewModel.Checklist;
        var showChecklist = checklist is { IsOpen: true } && (!_startedNarrow || checklist.OpenedByUser);
        var open = !showChecklist && viewModel.IsDetailPaneOpen && !_narrow;
        var checklistHadFocus = !showChecklist && SetupPanel.IsKeyboardFocusWithin;
        DetailPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        DetailSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        FitDetailPane();
        btnDetailPane.Visibility = open || showChecklist ? Visibility.Collapsed : Visibility.Visible;
        SetupPanel.Visibility = showChecklist ? Visibility.Visible : Visibility.Collapsed;
        SetupStrip.Visibility = !showChecklist && checklist is { IsDue: true } ? Visibility.Visible : Visibility.Collapsed;
        PlaceChecklist();
        ApplyLogRow();
        // A closed checklist gives the keyboard focus to the button that opens it, or to the grid (R-21).
        if (checklistHadFocus)
        {
            UpdateLayout();
            if (SetupStrip.IsVisible) btnOpenSetupChecklist.Focus();
            else PackagerGrid.Focus();
        }
    }

    /// <summary>The least and the most width of the open detail pane.</summary>
    public const double DetailPaneMinimumWidth = 260, DetailPaneMaximumWidth = 640;

    /// <summary>The width that the grid keeps beside the pane, for the select box, Application, version and Status columns.</summary>
    private const double GridKeepsWidth = 480;

    /// <summary>
    /// Gives the pane column the saved width, limited to the room beside the grid. A closed pane has no width. The saved width stays when the
    /// window is too narrow for it.
    /// </summary>
    private void FitDetailPane()
    {
        if (DetailPane.Visibility != Visibility.Visible)
        {
            DetailPaneColumn.MinWidth = 0;
            DetailPaneColumn.MaxWidth = double.PositiveInfinity;
            DetailPaneColumn.Width = new GridLength(0);
            return;
        }
        var room = WorkArea.ActualWidth > 0 ? WorkArea.ActualWidth - GridKeepsWidth - DetailSplitter.Width : DetailPaneMaximumWidth;
        var most = Math.Clamp(room, DetailPaneMinimumWidth, DetailPaneMaximumWidth);
        // The limits of the column also hold during a drag of the splitter.
        DetailPaneColumn.MinWidth = DetailPaneMinimumWidth;
        DetailPaneColumn.MaxWidth = most;
        DetailPaneColumn.Width = new GridLength(Math.Clamp(((PackagesViewModel)DataContext!).DetailPaneWidth, DetailPaneMinimumWidth, most));
    }

    private void DetailSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (!e.Canceled) SaveDetailPaneWidth();
    }

    /// <summary>The arrow keys move the splitter without a drag, so the release of one saves the width.</summary>
    private void DetailSplitter_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right) SaveDetailPaneWidth();
    }

    private void SaveDetailPaneWidth()
    {
        UpdateLayout();
        if (DetailPaneColumn.ActualWidth > 0) ((PackagesViewModel)DataContext!).DetailPaneWidth = DetailPaneColumn.ActualWidth;
    }

    /// <summary>A page at least this wide gives the open checklist more room, so its four steps show without scrolling.</summary>
    public const double WideChecklistPageWidth = 1400;

    private const double ChecklistWidth = 320, WideChecklistWidth = 360;

    /// <summary>
    /// The checklist stands beside the grid. In a wide page it also stands beside the batch bar and the log, and it is wider. In a narrower
    /// page the bar and the log keep the whole width, and the steps scroll.
    /// </summary>
    private void PlaceChecklist()
    {
        var shown = SetupPanel.Visibility == Visibility.Visible;
        var wide = shown && ActualWidth >= WideChecklistPageWidth;
        SetupPanel.Width = wide ? WideChecklistWidth : ChecklistWidth;
        Grid.SetRowSpan(SetupPanel, wide ? 3 : 1);
        Grid.SetColumnSpan(WorkArea, shown ? 1 : 2);
        Grid.SetColumnSpan(BatchBar, wide ? 1 : 2);
        Grid.SetColumnSpan(LogBox, wide ? 1 : 2);
    }

    private void OpenChecklist_Click(object sender, RoutedEventArgs e) => ShowChecklist();

    /// <summary>Opens the setup checklist and moves the keyboard focus into it.</summary>
    private void ShowChecklist()
    {
        if (((PackagesViewModel)DataContext!).Checklist is not { } checklist) return;
        checklist.Show();
        ApplyPane();
        UpdateLayout();
        SetupPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
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

    private void ViewHistory_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedPackager() is { } packager) OpenHistory(packager);
    }

    private void OpenHistory(PackagerInfo packager)
    {
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

    /// <summary>Fills the context menu of the grid for the selected row each time it opens.</summary>
    private void PackagerMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.ContextMenu menu) FillRowMenu(menu, GetSelectedPackager());
    }

    private void FillRowMenu(System.Windows.Controls.ContextMenu menu, PackagerInfo? row) =>
        Helpers.RowActions.Fill(menu, row, RunRowAction, PackagesViewModel.CveSourceLinks, url => Packages.OpenExternalLink(url));

    /// <summary>The More actions menu of the detail pane, for the row that the pane shows.</summary>
    public System.Windows.Controls.ContextMenu BuildRowActionsMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        FillRowMenu(menu, ((PackagesViewModel)DataContext!).DetailPackager);
        return menu;
    }

    private void DetailMoreActions_Click(object sender, RoutedEventArgs e) => Open(sender, BuildRowActionsMenu());

    private async void RunRowAction(Helpers.RowActionKind kind, PackagerInfo row)
    {
        switch (kind)
        {
            case Helpers.RowActionKind.History: OpenHistory(row); break;
            case Helpers.RowActionKind.EditApplication: OpenWorkbench(row); break;
            case Helpers.RowActionKind.OpenLogFolder: OpenLogFolder(); break;
            case Helpers.RowActionKind.OpenStagedFolder: OpenStagedFolder(row); break;
            case Helpers.RowActionKind.OpenNetworkShare: OpenNetworkShare(); break;
            case Helpers.RowActionKind.CopyLatestVersion:
                if (!string.IsNullOrEmpty(row.LatestVersion)) Clipboard.SetText(row.LatestVersion);
                break;
            case Helpers.RowActionKind.OpenVendorUrl:
                if (!string.IsNullOrEmpty(row.VendorUrl)) Process.Start(new ProcessStartInfo(row.VendorUrl) { UseShellExecute = true });
                break;
            case Helpers.RowActionKind.ShowCves: await OpenCveWindowAsync(row); break;
            case Helpers.RowActionKind.CopyCveIds: await CopyCveIdsAsync(row); break;
        }
    }

    private static void OpenLogFolder()
    {
        var settings = App.Services.GetRequiredService<SettingsService>().Load();
        var logDir = Path.Combine(settings.DownloadRoot, "Logs");
        if (Directory.Exists(logDir))
            Process.Start(new ProcessStartInfo("explorer.exe", logDir));
    }

    private static void OpenStagedFolder(PackagerInfo row)
    {
        var settings = App.Services.GetRequiredService<SettingsService>().Load();
        var dlRoot = settings.DownloadRoot;
        if (!Directory.Exists(dlRoot)) return;

        var baseName = Path.GetFileNameWithoutExtension(row.Script)
            .Replace("package-", "", StringComparison.OrdinalIgnoreCase);

        var candidates = Directory.GetDirectories(dlRoot)
            .Where(d => Path.GetFileName(d)!.Contains(baseName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var target = candidates.Length > 0 ? candidates[0] : dlRoot;
        Process.Start(new ProcessStartInfo("explorer.exe", target));
    }

    private static void OpenNetworkShare()
    {
        var settings = App.Services.GetRequiredService<SettingsService>().Load();
        if (!string.IsNullOrEmpty(settings.FileShareRoot))
            Process.Start(new ProcessStartInfo("explorer.exe", settings.FileShareRoot));
    }
}
