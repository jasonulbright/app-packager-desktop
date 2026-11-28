using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The Application Settings grid with many columns turned on: the width of each column and the cells of the selected row.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ApplicationSettingsGridRenderTests
{
    private static readonly string[] Chosen =
        ["source", "scope", "installertype", "supersedence", "title", "rule", "reboot", "dpgroup", "testcollection", "detection", "tracked"];

    /// <summary>A row that offers every choice, so the cells of the chosen columns are enabled except Detection.</summary>
    internal static SupersedenceRow Row(string uid)
    {
        var row = new SupersedenceRow
        {
            Uid = uid, ApplicationId = "catalog:" + uid, DefinitionId = uid, Application = "Contoso " + uid, Vendor = "Contoso", Architecture = "x64",
            OffersArchitecture = true, OffersInstallScope = true, OffersNetworkSplit = true, InstallerTypes = ["MSI", "EXE"]
        };
        var oneClick = new OneClickRow { Uid = uid, Application = row.Application, Vendor = row.Vendor };
        var work = new ApplicationWork { OneClickRowOf = _ => oneClick };
        work.Distribution[row.ApplicationId] = new() { DpGroupName = "Contoso DPs", TestCollectionName = "Contoso Test" };
        foreach (var column in ApplicationColumns.All)
        {
            var reason = column.NotApplicable(work, row);
            var value = column.Get(work, row);
            if (column.Choices is { } choices) row.Lists[column.Id] = choices.Contains(value) ? choices : [value];
            row.Cells[column.Id] = value;
            row.Editable[column.Id] = reason is null;
            row.Tips[column.Id] = reason ?? column.Tooltip;
        }
        return row;
    }

    /// <summary>The compiled page with the columns that the page builds for the chosen ids, over stand-ins for the view model.</summary>
    private static SettingsPage Page()
    {
        var page = (SettingsPage)RuntimeHelpers.GetUninitializedObject(typeof(SettingsPage));
        typeof(Page).GetConstructor(Type.EmptyTypes)!.Invoke(page, null);
        page.DataContext = new
        {
            Sidebar = new SidebarSettingsEditor(new SidebarService(SidebarRegistry.CreateDefault(), new InMemorySidebarStore(null))),
            SupersedenceRows = new List<SupersedenceRow> { Row("one"), Row("two") }
        };
        page.InitializeComponent();
        var viewModel = (SettingsViewModel)RuntimeHelpers.GetUninitializedObject(typeof(SettingsViewModel));
        const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(SettingsViewModel).GetField("<VisibleColumnIds>k__BackingField", instance)!.SetValue(viewModel, Chosen.ToHashSet(StringComparer.Ordinal));
        typeof(SettingsPage).GetField("_viewModel", instance)!.SetValue(page, viewModel);
        typeof(SettingsPage).GetMethod("BuildApplicationColumns", instance)!.Invoke(page, null);
        typeof(SettingsPage).GetMethod("TrackApplicationColumnGrips", instance)!.Invoke(page, null);
        page.SelectPage("supersedence");
        return page;
    }

    private static void WithPage(ApplicationTheme theme, int width, Action<Window, DataGrid> test) =>
        PageHeaderRenderTests.With("Settings", theme, () =>
        {
            var page = Page();
            var window = new Window { Width = width, Height = 984, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Settle(window);
                Settle(window);
                test(window, (DataGrid)page.FindName("dgApplications"));
            }
            finally { window.Close(); }
        });

    [Fact]
    public void ManyColumnsKeepTheirWidthAndTheGridScrollsSideways() => WithPage(ApplicationTheme.Light, 1690, (window, grid) =>
    {
        var declared = ApplicationColumns.All.Where(c => Chosen.Contains(c.Id)).ToDictionary(c => c.Header, c => (double)c.Width);
        declared["Check"] = 56;
        declared["Application"] = 220;
        declared["Vendor"] = 120;
        var narrow = grid.Columns.Where(c => c.ActualWidth < declared[(string)c.Header] - 0.5)
            .Select(c => $"{c.Header} {c.ActualWidth:0} of {declared[(string)c.Header]:0}").ToList();
        Assert.Equal(declared.Count, grid.Columns.Count);
        Assert.True(narrow.Count == 0, "Too narrow: " + string.Join("; ", narrow));
        var scroller = Descendants(grid).OfType<ScrollViewer>().First();
        Assert.True(scroller.ScrollableWidth > 1, $"The grid does not scroll sideways: viewport {scroller.ViewportWidth:0}, extent {scroller.ExtentWidth:0}.");
    });

    [Fact]
    public void AGripWidensNarrowsAndFitsAColumnOfTheScrollingGrid() => WithPage(ApplicationTheme.Light, 1690, (window, grid) =>
    {
        DataGridColumn Column(string header) => grid.Columns.Single(c => c.Header as string == header);
        string Widths() => string.Join("; ", grid.Columns.Select(c => $"{c.Header} {c.ActualWidth:0.##} (min {c.MinWidth:0.##}, {c.Width.UnitType})"));
        void Shows(string header, double width)
        {
            Settle(window);
            Assert.True(Math.Abs(Column(header).ActualWidth - width) < 1, $"{header} should be {width:0} px: {Widths()}");
        }
        void Drag(string header, double change) => ColumnGripKit.Drag(window, grid, Column(header), change);
        var scope = Column("Install scope").ActualWidth;

        // The grid scrolls, so no other column has width to give.
        Drag("Vendor", 80);
        Shows("Vendor", 316);
        Drag("Vendor", -226);
        Shows("Vendor", 90);
        Assert.True(Column("Application").Width.IsStar, Widths());
        Shows("Install scope", scope);

        Drag("Install scope", -500);
        Shows("Install scope", 40);
        Drag("Check", -30);
        Shows("Check", 56);

        // The Application column can be narrower than its first minimum.
        Drag("Application", -60);
        Shows("Application", 160);

        Drag("Vendor", 160);
        Shows("Vendor", 250);
        ColumnGripKit.DoubleClick(window, grid, Column("Vendor"));
        var vendor = Column("Vendor");
        Assert.True(vendor.ActualWidth is > 40 and < 160, Widths());
        Assert.True(vendor.Width.IsAbsolute && Math.Abs(vendor.MinWidth - vendor.ActualWidth) < 0.5, Widths());
        Shows("Install scope", 40);
    });

    [Fact]
    public void TheOrderTheWidthsAndTheSortAreSavedAndShowAgainOnANewPage()
    {
        var root = Path.Combine(Path.GetTempPath(), "apd-appgrid-" + Guid.NewGuid().ToString("N"));
        var before = Environment.GetEnvironmentVariable(ApplicationPackagerEngine.Services.AppDataPaths.DataRootVariable);
        Environment.SetEnvironmentVariable(ApplicationPackagerEngine.Services.AppDataPaths.DataRootVariable, root);
        try
        {
            PageHeaderRenderTests.With("Settings", ApplicationTheme.Light, () =>
            {
                using var scope = ApplicationPackagerEngine.Services.SiteContext.Scope(null, root);
                Directory.CreateDirectory(ApplicationPackagerEngine.Services.AppDataPaths.Root);
                const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Instance;
                var settings = new SettingsService();
                SettingsPage Open()
                {
                    var page = Page();
                    var viewModel = typeof(SettingsPage).GetField("_viewModel", instance)!.GetValue(page)!;
                    typeof(SettingsViewModel).GetField("_settingsService", instance)!.SetValue(viewModel, settings);
                    typeof(SettingsPage).GetField("_sizedApplicationColumns", instance)!.SetValue(page, new HashSet<string>(StringComparer.Ordinal));
                    typeof(SettingsPage).GetMethod("TrackApplicationLayout", instance)!.Invoke(page, null);
                    typeof(SettingsPage).GetMethod("BuildApplicationColumns", instance)!.Invoke(page, null);
                    return page;
                }
                void Show1(SettingsPage page, Action<Window, DataGrid> test)
                {
                    var window = new Window { Width = 1690, Height = 984, WindowStyle = WindowStyle.None, Content = page };
                    Show(window);
                    try
                    {
                        Settle(window);
                        Settle(window);
                        test(window, (DataGrid)page.FindName("dgApplications"));
                    }
                    finally { window.Close(); }
                }
                static DataGridColumn Column(DataGrid grid, string header) => grid.Columns.Single(c => c.Header as string == header);

                var first = Open();
                Show1(first, (window, grid) =>
                {
                    // A click on a grip saves nothing.
                    var shownBefore = string.Join("; ", grid.Columns.Select(c => $"{c.Header} {c.ActualWidth:0.##}"));
                    ColumnGripKit.Drag(window, grid, Column(grid, "Vendor"), 0);
                    Settle(window);
                    Assert.True(settings.Load().ApplicationSettingsGridLayout is null,
                        "A click on a grip saved a layout. Before: " + shownBefore + ". After: " + string.Join("; ", grid.Columns.Select(c => $"{c.Header} {c.ActualWidth:0.##}")));
                    ColumnGripKit.Drag(window, grid, Column(grid, "Vendor"), 60);
                    Settle(window);
                    Column(grid, "Install scope").DisplayIndex = 1;
                    Column(grid, "Application").SortDirection = System.ComponentModel.ListSortDirection.Descending;
                    typeof(SettingsPage).GetMethod("SaveApplicationLayout", instance)!.Invoke(first, null);
                });
                var saved = settings.Load().ApplicationSettingsGridLayout!;
                Assert.Equal("Install scope", saved.Columns[0].Id);
                Assert.Equal(296, saved.Columns.Single(c => c.Id == "Vendor").Width, 1.0);
                Assert.Equal(("Application", true), (saved.SortId, saved.SortDescending));

                Show1(Open(), (_, grid) =>
                {
                    Assert.Equal(1, Column(grid, "Install scope").DisplayIndex);
                    Assert.Equal(296, Column(grid, "Vendor").ActualWidth, 1.0);
                    Assert.Equal(System.ComponentModel.ListSortDirection.Descending, Column(grid, "Application").SortDirection);
                });
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(ApplicationPackagerEngine.Services.AppDataPaths.DataRootVariable, before);
            TestCleanup.DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(ApplicationTheme.Dark)]
    [InlineData(ApplicationTheme.Light)]
    public void EveryCellOfTheSelectedRowHasTheFillOfTheRowAndReadableText(ApplicationTheme theme) => WithPage(theme, 1690, (window, grid) =>
    {
        grid.SelectedIndex = 0;
        Settle(window);
        var row = Compliance.RenderKit.Row(grid, 0);
        DataGridCell Cell(DataGridColumn column) => Descendants(row).OfType<DataGridCell>().Single(c => ReferenceEquals(c.Column, column));
        // The left edge of a cell, half way down: no text or control of the cell covers it.
        Color Fill(DataGridCell cell)
        {
            var pixels = Pixels(window, cell);
            var width = (int)Math.Floor(cell.ActualWidth);
            return pixels[pixels.Count / width / 2 * width + 1];
        }
        var expected = Fill(Cell(grid.Columns.Single(c => c.Header as string == "Vendor")));
        var report = new List<string> { $"Vendor fill {expected}" };
        var wrong = false;
        foreach (var column in grid.Columns.Where(c => c is DataGridTemplateColumn && c.Header as string != "Check"))
        {
            grid.ScrollIntoView(grid.Items[0], column);
            Settle(window);
            var cell = Cell(column);
            var fill = Fill(cell);
            var control = Descendants(cell).OfType<System.Windows.Controls.Control>().First(c => c is ComboBox or TextBox);
            // A disabled control shows dimmed text on purpose, so only the enabled ones have a contrast floor.
            var contrast = 0.0;
            if (control.IsEnabled)
            {
                FrameworkElement text = control is ComboBox combo
                    ? Descendants(combo).OfType<TextBlock>().First(t => t.IsVisible && t.Text == combo.SelectedItem as string)
                    : control;
                contrast = TextContrast(Pixels(window, text));
            }
            var bad = fill != expected || (control.IsEnabled && contrast < 4.5);
            wrong |= bad;
            report.Add($"{column.Header}: fill {fill}, {(control.IsEnabled ? $"text contrast {contrast:0.00}" : "disabled")}, cell background {cell.Background}, cell foreground {cell.Foreground}{(bad ? " WRONG" : "")}");
        }
        Assert.False(wrong, string.Join(Environment.NewLine, report));
    });
}
