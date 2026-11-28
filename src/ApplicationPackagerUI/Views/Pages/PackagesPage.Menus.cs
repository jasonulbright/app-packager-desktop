using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using ApplicationPackagerUI.Helpers;
using ApplicationPackagerUI.ViewModels.Pages;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace ApplicationPackagerUI.Views.Pages;

/// <summary>
/// The menus of the page (overhaul V1, phase 3c) and the saved column layout. Each menu is built when it opens, so each item reads the state of that
/// moment. A disabled item keeps its tooltip, so the reason it is off can be read.
/// </summary>
public partial class PackagesPage
{
    private static MenuItem Item(string header, string id, SymbolRegular glyph, string? toolTip = null)
    {
        var item = new MenuItem { Header = header, Icon = new SymbolIcon(glyph) };
        AutomationProperties.SetAutomationId(item, id);
        if (!string.IsNullOrEmpty(toolTip))
        {
            item.ToolTip = toolTip;
            ToolTipService.SetShowOnDisabled(item, true);
        }
        return item;
    }

    private static void Open(object sender, System.Windows.Controls.ContextMenu menu)
    {
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>The More menu: the commands that are not part of a run.</summary>
    public System.Windows.Controls.ContextMenu BuildMoreMenu()
    {
        var vm = (PackagesViewModel)DataContext!;
        var report = Item("CVE Report", "miCveReport", SymbolRegular.DocumentTable24,
            "Write the CVE report for the checked applications (all applications when none is checked) as HTML, CSV, XLSX, Markdown and JSON, then open the HTML page.");
        report.Command = vm.CveReportCommand;
        var history = Item("Operation history...", "miOperationHistory", SymbolRegular.TextBulletListSquare24, "View recorded operations for the selected application.");
        history.IsEnabled = HistoryRow() is not null;
        history.Click += ViewHistory_Click;
        var export = Item("Export Check Results...", "miExportCheckResults", SymbolRegular.DocumentTable24,
            "Saves the result of the last Check ConfigMgr run as a CSV or JSON file. The file type in the dialog chooses all checked applications or only those with an update available. Each run also writes CheckConfigMgrResult.csv in the SRLPackr data folder.");
        export.Command = vm.ExportCheckResultsCommand;
        var debug = new MenuItem { Header = "Show debug columns", IsCheckable = true, IsChecked = vm.ShowDebugColumns, ToolTip = "Show the CMName, Script, Max EPSS, NVD updated and CVEs Checked (UTC) columns." };
        AutomationProperties.SetAutomationId(debug, "miShowDebugColumns");
        debug.Click += (_, _) => vm.ShowDebugColumns = debug.IsChecked;
        var convert = Item("Convert from ConfigMgr", "miMoreConvertFromCm", SymbolRegular.ArrowSwap24, "Convert a ConfigMgr application to Intune apps.");
        convert.Click += ConvertFromCm_Click;
        var import = Item("Import from URL", "miMoreImportFromUrl", SymbolRegular.ArrowDownload24, "Open the window that imports vendor definitions from a feed address.");
        import.Command = vm.ImportFromUrlCommand;
        var details = Item("Show details", "miShowDetails", SymbolRegular.PanelRight24, "Open the details of the selected application beside the grid, with the comment of the next run.");
        details.IsEnabled = GetSelectedPackager() is not null;
        details.Click += (_, _) => ShowDetails();
        var showChecked = Item("Show checked applications", "miShowSelected", SymbolRegular.CheckboxChecked24,
            "Show only the checked applications, so that you see the ones that the search or the status list hides.");
        showChecked.IsEnabled = vm.HasSelection;
        showChecked.Command = vm.ShowSelectedCommand;
        var clear = Item("Clear selection", "miClearSelection", SymbolRegular.CheckboxUnchecked24, "Uncheck every application, also those that the search or the status list hides.");
        clear.IsEnabled = vm.HasSelection;
        clear.Command = vm.ClearSelectionCommand;
        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(convert);
        menu.Items.Add(import);
        menu.Items.Add(new Separator());
        menu.Items.Add(details);
        menu.Items.Add(showChecked);
        menu.Items.Add(clear);
        menu.Items.Add(new Separator());
        menu.Items.Add(report);
        menu.Items.Add(history);
        menu.Items.Add(export);
        var checklist = Item("Setup checklist", "miSetupChecklist", SymbolRegular.TaskListSquareLtr24, "Open the setup checklist beside the grid.");
        checklist.IsEnabled = vm.Checklist is not null;
        checklist.Click += (_, _) => ShowChecklist();
        // Setup needs no stored setting, so it is always a way in. Only a run turns it off: a save during a run would change what the run reads.
        var setup = Item("Setup...", "miSetup", SymbolRegular.Settings24, "Open the Setup window: the systems, the site and the content folders. It is off while a run is active.");
        setup.IsEnabled = !vm.IsOperationRunning;
        setup.Click += (_, _) => (Window.GetWindow(this) as Views.Windows.MainWindow)?.OpenSetup();
        menu.Items.Add(new Separator());
        menu.Items.Add(checklist);
        menu.Items.Add(setup);
        menu.Items.Add(new Separator());
        menu.Items.Add(debug);
        return menu;
    }

    /// <summary>The Add or import menu: every way to put an application into the grid.</summary>
    public System.Windows.Controls.ContextMenu BuildAddOrImportMenu()
    {
        var vm = (PackagesViewModel)DataContext!;
        var web = Item("Add from web", "miAddFromWeb", SymbolRegular.Globe24, "Add an application from a vendor web page.");
        web.Click += (_, _) => NavigateTo(typeof(AddAppPage));
        var file = Item("Add from file", "miAddFromFile", SymbolRegular.DocumentAdd24, "Add an application from an installer file.");
        file.Click += (_, _) => NavigateTo(typeof(AddFilePage));
        var convert = Item("Convert from ConfigMgr", "miConvertFromCm", SymbolRegular.ArrowSwap24, "Convert a ConfigMgr application to Intune apps.");
        convert.Click += ConvertFromCm_Click;
        var import = Item("Import From URL", "miImportFromUrl", SymbolRegular.ArrowDownload24, "Open the window that imports vendor definitions from a feed address.");
        import.Command = vm.ImportFromUrlCommand;
        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var item in new[] { web, file, convert, import }) menu.Items.Add(item);
        return menu;
    }

    private void NavigateTo(Type page)
    {
        if (Window.GetWindow(this) is Views.Windows.MainWindow main) main.GetNavigation().Navigate(page);
    }

    private void More_Click(object sender, RoutedEventArgs e) => Open(sender, BuildMoreMenu());

    private void AddOrImport_Click(object sender, RoutedEventArgs e) => Open(sender, BuildAddOrImportMenu());

    // ---- the saved column layout ----

    private List<DataGridColumn> _movable = [];
    private readonly Dictionary<DataGridColumn, DataGridLength> _defaultWidths = [];
    private readonly Dictionary<DataGridColumn, double> _defaultMinWidths = [];
    private bool _applyingLayout;
    // The columns whose width the operator set. A resized star column stays a star in WPF, so its width is read from what it shows.
    private readonly HashSet<DataGridColumn> _userSized = [];

    private static string ColumnId(DataGridColumn column) => column.Header as string ?? "";

    /// <summary>
    /// Applies the saved layout and follows the changes of the operator. The columns before the first column with a text header (the check box and
    /// the icon) never move.
    /// </summary>
    private void InitializeColumnLayout()
    {
        _movable = PackagerGrid.Columns.Where(c => ColumnId(c).Length > 0).ToList();
        foreach (var column in _movable)
        {
            _defaultWidths[column] = column.Width;
            _defaultMinWidths[column] = column.MinWidth;
        }
        ApplyColumnLayout(((PackagesViewModel)DataContext!).ColumnLayout);

        PackagerGrid.ColumnReordered += (_, _) => SaveColumnLayout();
        // A resize ends with the release of the thumb in the column header.
        PackagerGrid.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnGridThumbPressed), true);
        PackagerGrid.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnGridThumbReleased), true);
        PackagerGrid.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnGridMouseDown), true);
        // The grid sorts after this event, so the save waits for it.
        PackagerGrid.Sorting += (_, _) => Dispatcher.BeginInvoke(SaveColumnLayout);
        // A new list (a load) drops the sort of the grid, so the saved sort is applied again.
        DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(System.Windows.Controls.DataGrid))
            .AddValueChanged(PackagerGrid, (_, _) => Dispatcher.BeginInvoke(() => ApplySort(((PackagesViewModel)DataContext!).ColumnLayout)));
    }

    private (DataGridColumn Column, double Width)? _pressed;

    private void OnGridThumbPressed(object sender, DragStartedEventArgs e)
    {
        _pressed = ColumnGrips.SizedColumn(PackagerGrid, e.OriginalSource) is { } column ? (column, column.ActualWidth) : null;
        if (_pressed is { } pressed) Release(pressed.Column);
    }

    /// <summary>A released header grip ends a resize. A click on a grip that moved nothing is not one: the column keeps its star width.</summary>
    private void OnGridThumbReleased(object sender, DragCompletedEventArgs e)
    {
        var pressed = _pressed;
        _pressed = null;
        if (ColumnGrips.SizedColumn(PackagerGrid, e.OriginalSource) is not { } column || pressed is not { } start || !ReferenceEquals(start.Column, column)) return;
        if (Math.Abs(column.ActualWidth - start.Width) < 0.5)
        {
            Hold(column);
            return;
        }
        MarkResized(column);
        SaveColumnLayout();
    }

    /// <summary>
    /// A double click on a grip fits the column to its content, which can be narrower than the width that the operator set. The second press of a
    /// double click starts no drag, so the held MinWidth ends here. The fitted width is then a width that the operator set.
    /// </summary>
    private void OnGridMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || ColumnGrips.SizedColumn(PackagerGrid, e.OriginalSource) is not { } column || !_movable.Contains(column)) return;
        Release(column);
        _userSized.Remove(column);
        ColumnGrips.AfterFit(PackagerGrid, column, () =>
        {
            MarkResized(column);
            SaveColumnLayout();
        });
    }

    /// <summary>Keeps the width that the column shows as the width that the operator set.</summary>
    public void MarkResized(DataGridColumn column)
    {
        if (!_movable.Contains(column)) return;
        _userSized.Add(column);
        Hold(column);
    }

    // A grid narrower than its columns takes width from every column that is wider than its MinWidth. So a pixel width that the operator set is
    // also the MinWidth of its column: the other columns give the width, and the saved width shows again after a restart. A column that is
    // still a star keeps the MinWidth of the page.
    private void Hold(DataGridColumn column)
    {
        if (_userSized.Contains(column) && column.Width.IsAbsolute) column.MinWidth = Math.Max(column.Width.Value, _defaultMinWidths[column]);
    }

    /// <summary>Gives a column the MinWidth of the page again, so a grip can make it narrower.</summary>
    private void Release(DataGridColumn column)
    {
        if (_defaultMinWidths.TryGetValue(column, out var width)) column.MinWidth = width;
    }

    // A saved width equal to the pixel width of the page is not a choice of the operator.
    private double? SavedWidth(PackagesColumnLayout? layout, DataGridColumn column) =>
        PackagesColumnLayout.WidthOf(layout, ColumnId(column)) is { } width
        && !(_defaultWidths[column].IsAbsolute && Math.Abs(width - _defaultWidths[column].Value) < 0.01)
            ? width : null;

    /// <summary>A sort cannot change while a row is edited, so an open edit ends first.</summary>
    private void EndGridEdit()
    {
        PackagerGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        PackagerGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void ApplyColumnLayout(PackagesColumnLayout? layout)
    {
        EndGridEdit();
        _applyingLayout = true;
        _userSized.Clear();
        foreach (var column in _movable)
            if (SavedWidth(layout, column) is not null) _userSized.Add(column);
        try
        {
            var byId = _movable.ToDictionary(ColumnId, StringComparer.Ordinal);
            var order = PackagesColumnLayout.Order(_movable.Select(ColumnId).ToList(), layout);
            var first = PackagerGrid.Columns.Count - _movable.Count;
            for (var i = 0; i < order.Count; i++) byId[order[i]].DisplayIndex = first + i;
            foreach (var column in _movable)
            {
                Release(column);
                column.Width = SavedWidth(layout, column) is { } width
                    ? new DataGridLength(Math.Max(width, column.MinWidth))
                    : _defaultWidths[column];
                Hold(column);
            }
            ApplySort(layout);
        }
        finally { _applyingLayout = false; }
    }

    private void ApplySort(PackagesColumnLayout? layout)
    {
        var sorted = _movable.FirstOrDefault(c => string.Equals(ColumnId(c), layout?.SortId, StringComparison.Ordinal));
        if (sorted is null || string.IsNullOrEmpty(sorted.SortMemberPath) || PackagerGrid.ItemsSource is null) return;
        var direction = layout!.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        PackagerGrid.Items.SortDescriptions.Clear();
        PackagerGrid.Items.SortDescriptions.Add(new SortDescription(sorted.SortMemberPath, direction));
        foreach (var column in PackagerGrid.Columns) column.SortDirection = ReferenceEquals(column, sorted) ? direction : null;
    }

    /// <summary>The layout the grid shows now. A column keeps a width only when it differs from the width of the page definition.</summary>
    public PackagesColumnLayout ReadColumnLayout()
    {
        var sorted = _movable.FirstOrDefault(c => c.SortDirection is not null);
        return new PackagesColumnLayout
        {
            Columns = _movable.OrderBy(c => c.DisplayIndex).Select(c => new PackagesColumnState
            {
                Id = ColumnId(c),
                Width = c.Width.IsAbsolute
                    // DataGridLength equality includes the width on screen, so only the set value is compared.
                    ? (_userSized.Contains(c) || !_defaultWidths[c].IsAbsolute || c.Width.Value != _defaultWidths[c].Value ? c.Width.Value : 0)
                    : (_userSized.Contains(c) && c.ActualWidth > 0 ? c.ActualWidth : 0)
            }).ToList(),
            SortId = sorted is null ? "" : ColumnId(sorted),
            SortDescending = sorted?.SortDirection == ListSortDirection.Descending
        };
    }

    private void ResetColumnLayout()
    {
        EndGridEdit();
        _applyingLayout = true;
        try
        {
            PackagerGrid.Items.SortDescriptions.Clear();
            foreach (var column in PackagerGrid.Columns) column.SortDirection = null;
        }
        finally { _applyingLayout = false; }
        ApplyColumnLayout(null);
    }

    private void SaveColumnLayout()
    {
        if (!_applyingLayout) ((PackagesViewModel)DataContext!).SaveColumnLayout(ReadColumnLayout());
    }
}
