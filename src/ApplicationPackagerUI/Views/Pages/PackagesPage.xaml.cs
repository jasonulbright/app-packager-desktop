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
        // A column header has no data context, so the header box is bound to the view model itself.
        SelectAllCheckBox.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new System.Windows.Data.Binding(nameof(PackagesViewModel.CheckedPart)) { Source = viewModel, Mode = System.Windows.Data.BindingMode.OneWay });

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.ImportFromUrlRequested += (_, _) => ShowImportFromUrl(viewModel);
        viewModel.PublishStarting += (_, _) => NoteGridPlace();
        // After the publish command ended, so Publish All is enabled again and can take the focus.
        viewModel.PublishEnded += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            RestoreGridPlace();
            if (e.Review is { } row) ShowRow(row);
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        ApplySavedColumns();
        InitializeColumnLayout();

        // The checklist panel has its own data context: a view model without a checklist (a test) shows neither the panel nor the strip.
        SetupPanel.DataContext = viewModel.Checklist;
        if (viewModel.Checklist is { } checklist) checklist.StateChanged += ApplyPane;

        SizeChanged += (_, _) => PlaceChecklist();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PackagesViewModel.IsOperationRunning) or nameof(PackagesViewModel.ShowProgress))
                Dispatcher.Invoke(PlaceLogBar);
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
        // The header is tri-state: the view model owns the cycle, the click only advances its cycle.
        var vm = (PackagesViewModel)DataContext!;
        vm.CycleSelectionCommand.Execute(null);
        // The box follows the checked applications again.
        if (sender is System.Windows.Controls.CheckBox cb) cb.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateTarget();
        e.Handled = true;
    }

    /// <summary>
    /// Shows the columns that the Columns menu chose. Source and Intune also need data, and the debug columns also show while Show debug
    /// columns is on.
    /// </summary>
    private void ApplySavedColumns()
    {
        var viewModel = (PackagesViewModel)DataContext!;
        var shown = viewModel.ShownColumns;
        foreach (var column in PackagerGrid.Columns)
        {
            if (column.Header is not string header || !Helpers.PackagesColumns.Toggleable.Contains(header)) continue;
            var visible = shown.Contains(header);
            if (header == "Source") visible &= viewModel.ShowSourceColumn;
            if (header == "Intune") visible &= viewModel.ShowIntuneColumn;
            if (viewModel.ShowDebugColumns && Helpers.PackagesColumns.Debug.Contains(header)) visible = true;
            column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>The Columns menu: one check item for each column that may be hidden, and Reset columns. Each choice is saved at once.</summary>
    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = (PackagesViewModel)DataContext!;
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        var shown = viewModel.ShownColumns;
        foreach (var header in Helpers.PackagesColumns.Toggleable)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header, IsCheckable = true, IsChecked = shown.Contains(header), StaysOpenOnClick = true };
            if (header is "Source" or "Intune")
                item.ToolTip = header == "Source" ? "Shows while an application has an imported source." : "Shows while Intune is in Settings, Systems in Use.";
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, "miColumn" + new string(header.Where(char.IsLetterOrDigit).ToArray()));
            item.Click += (_, _) => { viewModel.SetColumnVisible(header, item.IsChecked); ApplySavedColumns(); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new System.Windows.Controls.Separator());
        var compact = new System.Windows.Controls.MenuItem { Header = "Compact view", ToolTip = "Show ConfigMgr version, Latest, Status and CVE check only." };
        System.Windows.Automation.AutomationProperties.SetAutomationId(compact, "miColumnsCompact");
        compact.Click += (_, _) => { viewModel.ShowCompactColumns(); ApplySavedColumns(); };
        menu.Items.Add(compact);
        var reset = new System.Windows.Controls.MenuItem { Header = "Reset columns" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(reset, "miColumnsReset");
        reset.Click += (_, _) => { viewModel.ResetColumns(); ApplySavedColumns(); ResetColumnLayout(); };
        menu.Items.Add(reset);
        menu.IsOpen = true;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PackagesViewModel.ShowDebugColumns) or nameof(PackagesViewModel.ShowSourceColumn)
            or nameof(PackagesViewModel.ShowIntuneColumn))
            ApplySavedColumns();
        else if (e.PropertyName == nameof(PackagesViewModel.FocusedPackager) && ((PackagesViewModel)DataContext!).FocusedPackager is { } row)
        {
            PackagerGrid.SelectedItem = row;
            PackagerGrid.ScrollIntoView(row);
        }
    }

    private (PackagerInfo? Row, double Offset)? _place;

    private ScrollViewer? GridScroller() => Find<ScrollViewer>(PackagerGrid);

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (Find<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private void NoteGridPlace() => _place = (GetSelectedPackager(), GridScroller()?.VerticalOffset ?? 0);

    /// <summary>
    /// Puts the focused row and the scroll position back after the review or the results, and gives the keyboard focus to Publish All
    /// (R-21). A row that left the filtered view is not brought back; the scroll position stays.
    /// </summary>
    private void RestoreGridPlace()
    {
        if (_place is not { } place) return;
        _place = null;
        if (place.Row is not null && PackagerGrid.Items.Contains(place.Row)) PackagerGrid.SelectedItem = place.Row;
        GridScroller()?.ScrollToVerticalOffset(place.Offset);
        UpdateLayout();
        if (!IsVisible) return;
        if (btnPublishAll.IsEnabled) btnPublishAll.Focus();
        else PackagerGrid.Focus();
    }

    /// <summary>Selects a row and moves the focus to the grid, for Review failed application.</summary>
    private void ShowRow(PackagerInfo row)
    {
        var viewModel = Packages;
        if (!PackagerGrid.Items.Contains(row))
        {
            viewModel.SearchText = "";
            viewModel.StatusTab = Helpers.PackagesTabChoice.All;
        }
        PackagerGrid.SelectedItem = row;
        PackagerGrid.ScrollIntoView(row);
        PackagerGrid.Focus();
    }

    private Views.Windows.LogWindow? _log;

    /// <summary>Opens the Logs window, or brings the open one to the front.</summary>
    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        if (_log is { IsVisible: true })
        {
            _log.Activate();
            return;
        }
        _log = new Views.Windows.LogWindow(Packages) { Owner = Window.GetWindow(this) };
        _log.Closed += (_, _) =>
        {
            _log = null;
            btnLogs.Focus();
        };
        _log.Show();
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

    /// <summary>
    /// Tab and Ctrl+Tab leave the grid: forward to the detail pane, else to the batch bar; back to the search row. The arrow keys move
    /// between the cells. Without this, Tab walks every cell of every row, and Ctrl+Tab jumps past the pane and the batch bar.
    /// </summary>
    private void PackagerGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || (Keyboard.Modifiers & ModifierKeys.Alt) != 0) return;
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return;
        e.Handled = LeaveGrid((Keyboard.Modifiers & ModifierKeys.Shift) != 0);
    }

    /// <summary>Moves the focus out of the grid. True when a control took it.</summary>
    public bool LeaveGrid(bool back)
    {
        IEnumerable<UIElement> targets = back
            ? [btnMore, btnColumns]
            : [DetailSplitter, DetailPane, SetupPanel, btnOperationHistory, btnLogs];
        foreach (var target in targets)
        {
            if (!target.IsVisible || !target.IsEnabled) continue;
            if (ReferenceEquals(target, SetupPanel) || ReferenceEquals(target, DetailPane))
            {
                if (((FrameworkElement)target).MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) return true;
                continue;
            }
            if (target.Focus()) return true;
        }
        return false;
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
        SetupPanel.Visibility = showChecklist ? Visibility.Visible : Visibility.Collapsed;
        PlaceChecklist();
        // A closed checklist gives the keyboard focus to the grid (R-21).
        if (checklistHadFocus)
        {
            UpdateLayout();
            PackagerGrid.Focus();
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

    private const double ChecklistWidth = 390, ChecklistGap = 16;

    // The widths of the five text columns of the mock at the two widths of its table. A column that the operator sized keeps its width.
    private static readonly (string Header, double Wide, double Beside)[] ColumnWeights =
    [
        ("Application", 420, 344), ("Vendor", 328, 268), ("ConfigMgr version", 208, 171), ("Latest version", 169, 134), ("Status", 313, 166), ("CVE check", 186, 134)
    ];

    private void FitChecklistColumns(bool beside)
    {
        foreach (var (header, wide, narrow) in ColumnWeights)
        {
            var column = PackagerGrid.Columns.FirstOrDefault(c => c.Header as string == header);
            if (column is null || _userSized.Contains(column) || !column.Width.IsStar) continue;
            // Only beside the checklist may a column go below the least width of the page.
            var least = _defaultMinWidths.GetValueOrDefault(column, column.MinWidth);
            column.MinWidth = beside ? Math.Min(least, narrow - 30) : least;
            column.Width = new DataGridLength(beside ? narrow : wide, DataGridLengthUnitType.Star);
        }
    }

    /// <summary>
    /// First start, as the mock draws it: the checklist stands at the right of the search row and the grid; the checks and the run buttons, the
    /// status list and the line of the last run leave the page; the header has Skip for now.
    /// </summary>
    private void PlaceChecklist()
    {
        var shown = SetupPanel.Visibility == Visibility.Visible;
        var room = shown ? new Thickness(0, 0, ChecklistWidth + ChecklistGap, 0) : new Thickness(0);
        // The mock sets the search row of the first start one unit higher, and its placeholder in the middle of the box.
        FilterBar.Margin = new Thickness(room.Left, shown ? -1 : 0, room.Right, 14);
        Controls.Mock.Mk.SetTextDrop(SearchBox, shown ? 0 : -1);
        foreach (var button in new[] { SelectUpdatesButton, btnColumns, btnMore }) Controls.Mock.Mk.SetTextDrop(button, shown ? 1 : 0);
        WorkArea.Margin = room;
        BatchBar.Visibility = FilterGlyph.Visibility = StatusFilterList.Visibility = shown ? Visibility.Collapsed : Visibility.Visible;
        PlaceLogBar();
        CatalogHeading.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        FitChecklistColumns(shown);
        if (shown) SelectAllCheckBox.IsChecked = false;
        else SelectAllCheckBox.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new System.Windows.Data.Binding(nameof(PackagesViewModel.CheckedPart)) { Source = DataContext, Mode = System.Windows.Data.BindingMode.OneWay });
        if (Header.Actions is System.Windows.Controls.Panel actions)
            foreach (FrameworkElement child in actions.Children)
                child.Visibility = Equals(child.Tag, "skip") == shown ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The line under the grid holds Cancel and the progress bar, so it shows during a run also while the checklist is open.</summary>
    private void PlaceLogBar() => LogBar.Visibility = SetupPanel.Visibility != Visibility.Visible
        || DataContext is PackagesViewModel { IsOperationRunning: true } or PackagesViewModel { ShowProgress: true } ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Opens the setup checklist and moves the keyboard focus into it.</summary>
    internal void ShowChecklist()
    {
        if (((PackagesViewModel)DataContext!).Checklist is not { } checklist) return;
        checklist.Show();
        ApplyPane();
        UpdateLayout();
        SetupPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void DetailSystems_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as Views.Windows.MainWindow)?.OpenSettingsPage("systems");

    private void DetailOpenWorkbench_Click(object sender, RoutedEventArgs e) => OpenWorkbenchForSelection();

    /// <summary>Closing the pane returns the keyboard focus to the button that opens it (R-21).</summary>
    private void DetailClose_Click(object sender, RoutedEventArgs e)
    {
        ((PackagesViewModel)DataContext!).IsDetailPaneOpen = false;
        ApplyPane();
        PackagerGrid.Focus();
    }

    /// <summary>Opens the detail pane of the focused row: the row menu and More, Show details.</summary>
    public void ShowDetails()
    {
        _narrow = false;
        // The open checklist stands in the place of the pane, so it closes; nothing is stored.
        if (SetupPanel.Visibility == Visibility.Visible) ((PackagesViewModel)DataContext!).Checklist?.CloseCommand.Execute(null);
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

    private PackagerInfo? HistoryRow() => GetSelectedPackager() ?? Packages.Packagers?.FirstOrDefault(p => p.IsSelected);

    private void ViewHistory_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryRow() is { } packager) OpenHistory(packager);
        else
            System.Windows.MessageBox.Show(Window.GetWindow(this), "Select or check an application first. Its operation history then opens.", "Operation history",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
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
        if (sender is not System.Windows.Controls.ContextMenu menu) return;
        FillRowMenu(menu, GetSelectedPackager());
        AddDetailsEntry(menu, GetSelectedPackager());
    }

    private void FillRowMenu(System.Windows.Controls.ContextMenu menu, PackagerInfo? row) =>
        Helpers.RowActions.Fill(menu, row, RunRowAction, PackagesViewModel.CveSourceLinks, url => Packages.OpenExternalLink(url));

    // The row menu of the grid also opens the detail pane, which holds the run comment.
    private void AddDetailsEntry(System.Windows.Controls.ContextMenu menu, PackagerInfo? row)
    {
        var details = Item("Show details", "miRowShowDetails", Wpf.Ui.Controls.SymbolRegular.PanelRight24, "Open the details of the application beside the grid, with the comment of the next run.");
        details.IsEnabled = row is not null;
        details.Click += (_, _) => ShowDetails();
        menu.Items.Insert(0, details);
        menu.Items.Insert(1, new System.Windows.Controls.Separator());
    }

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
