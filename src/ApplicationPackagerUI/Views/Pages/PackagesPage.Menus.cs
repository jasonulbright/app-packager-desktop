using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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

    /// <summary>The Check menu: the three checks under their own names.</summary>
    public System.Windows.Controls.ContextMenu BuildCheckMenu()
    {
        var vm = (PackagesViewModel)DataContext!;
        var latest = Item("Check Latest", "miCheckLatest", SymbolRegular.ArrowDownload24, "Look up the latest version of each checked application.");
        latest.Command = vm.CheckLatestCommand;
        var site = Item("Check ConfigMgr", "miCheckConfigMgr", SymbolRegular.Search24, vm.CheckMecmToolTip);
        site.Command = vm.CheckMecmCommand;
        var cves = Item("Check CVEs", "miCheckCves", SymbolRegular.ShieldError24,
            "Look up the CVEs fixed by updating each checked application from its ConfigMgr version to the latest version. Needs both versions and a CPE.");
        cves.Command = vm.CheckCvesCommand;
        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var item in new[] { latest, site, cves }) menu.Items.Add(item);
        return menu;
    }

    /// <summary>The More menu: the commands that are not part of a run.</summary>
    public System.Windows.Controls.ContextMenu BuildMoreMenu()
    {
        var vm = (PackagesViewModel)DataContext!;
        var report = Item("CVE Report", "miCveReport", SymbolRegular.DocumentTable24,
            "Write the CVE report for the checked applications (all applications when none is checked) as HTML, CSV, XLSX, Markdown and JSON, then open the HTML page.");
        report.Command = vm.CveReportCommand;
        var history = Item("Operation history...", "miOperationHistory", SymbolRegular.TextBulletListSquare24, "View recorded operations for the selected application.");
        history.IsEnabled = GetSelectedPackager() is not null;
        history.Click += ViewHistory_Click;
        var export = Item("Export Check Results...", "miExportCheckResults", SymbolRegular.DocumentTable24,
            "Saves the result of the last Check ConfigMgr run as a CSV or JSON file. The file type in the dialog chooses all checked applications or only those with an update available. Each run also writes CheckConfigMgrResult.csv in the SRLPackr data folder.");
        export.Command = vm.ExportCheckResultsCommand;
        var debug = new MenuItem { Header = "Show debug columns", IsCheckable = true, IsChecked = vm.ShowDebugColumns, ToolTip = "Show the CMName, Script, Max EPSS, NVD updated and CVEs Checked (UTC) columns." };
        AutomationProperties.SetAutomationId(debug, "miShowDebugColumns");
        debug.Click += (_, _) => vm.ShowDebugColumns = debug.IsChecked;
        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(report);
        menu.Items.Add(history);
        menu.Items.Add(export);
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

    private void CheckMenu_Click(object sender, RoutedEventArgs e) => Open(sender, BuildCheckMenu());

    private void More_Click(object sender, RoutedEventArgs e) => Open(sender, BuildMoreMenu());

    private void AddOrImport_Click(object sender, RoutedEventArgs e) => Open(sender, BuildAddOrImportMenu());

    // ---- the saved column layout ----

    private List<DataGridColumn> _movable = [];
    private readonly Dictionary<DataGridColumn, DataGridLength> _defaultWidths = [];
    private bool _applyingLayout;

    private static string ColumnId(DataGridColumn column) => column.Header as string ?? "";

    /// <summary>
    /// Applies the saved layout and follows the changes of the operator. The columns before the first column with a text header (the check box and
    /// the icon) never move.
    /// </summary>
    private void InitializeColumnLayout()
    {
        _movable = PackagerGrid.Columns.Where(c => ColumnId(c).Length > 0).ToList();
        foreach (var column in _movable) _defaultWidths[column] = column.Width;
        ApplyColumnLayout(((PackagesViewModel)DataContext!).ColumnLayout);

        PackagerGrid.ColumnReordered += (_, _) => SaveColumnLayout();
        // A resize ends with the release of the thumb in the column header.
        PackagerGrid.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => SaveColumnLayout()), true);
        // The grid sorts after this event, so the save waits for it.
        PackagerGrid.Sorting += (_, _) => Dispatcher.BeginInvoke(SaveColumnLayout);
        // A new list (a load) drops the sort of the grid, so the saved sort is applied again.
        DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(System.Windows.Controls.DataGrid))
            .AddValueChanged(PackagerGrid, (_, _) => Dispatcher.BeginInvoke(() => ApplySort(((PackagesViewModel)DataContext!).ColumnLayout)));
    }

    private void ApplyColumnLayout(PackagesColumnLayout? layout)
    {
        _applyingLayout = true;
        try
        {
            var byId = _movable.ToDictionary(ColumnId, StringComparer.Ordinal);
            var order = PackagesColumnLayout.Order(_movable.Select(ColumnId).ToList(), layout);
            var first = PackagerGrid.Columns.Count - _movable.Count;
            for (var i = 0; i < order.Count; i++) byId[order[i]].DisplayIndex = first + i;
            foreach (var column in _movable)
                column.Width = PackagesColumnLayout.WidthOf(layout, ColumnId(column)) is { } width
                    ? new DataGridLength(Math.Max(width, column.MinWidth))
                    : _defaultWidths[column];
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
                Width = !c.Width.Equals(_defaultWidths[c]) && c.Width.IsAbsolute ? c.Width.Value : 0
            }).ToList(),
            SortId = sorted is null ? "" : ColumnId(sorted),
            SortDescending = sorted?.SortDirection == ListSortDirection.Descending
        };
    }

    private void ResetColumnLayout()
    {
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
