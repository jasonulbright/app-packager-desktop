using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using DataGrid = System.Windows.Controls.DataGrid;
using FontFamily = System.Windows.Media.FontFamily;
using Size = System.Windows.Size;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The Packages grid at the default window size. A data grid with a star column gives
/// the other columns less than their Width when the grid is narrower than the column
/// total, down to each column's MinWidth; a column without a MinWidth then cuts its
/// header. The columns are read from the page markup and laid out with the control
/// styles the application loads.
/// </summary>
public sealed class PackagesGridHeaderWidthTests
{
    private const double WindowWidth = 1200;
    private const double NavigationPaneWidth = 220;
    private const double PageMargin = 32;

    private sealed record Column(string Header, DataGridLength Width, double? MinWidth, bool Visible);

    private static string PagePath()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "src", "ApplicationPackagerUI", "Views", "Pages", "PackagesPage.xaml")))
            folder = folder.Parent;
        Assert.NotNull(folder);
        return Path.Combine(folder.FullName, "src", "ApplicationPackagerUI", "Views", "Pages", "PackagesPage.xaml");
    }

    private static List<Column> Columns(bool withHiddenColumns)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var grid = XDocument.Load(PagePath()).Descendants().Single(e => (string?)e.Attribute(x + "Name") == "PackagerGrid");
        var columns = grid.Elements().Single(e => e.Name.LocalName == "DataGrid.Columns").Elements()
            .Select(e => new Column(
                (string?)e.Attribute("Header") ?? "",
                (DataGridLength)new DataGridLengthConverter().ConvertFromInvariantString((string?)e.Attribute("Width") ?? "Auto")!,
                (string?)e.Attribute("MinWidth") is { } min ? double.Parse(min, CultureInfo.InvariantCulture) : null,
                (string?)e.Attribute("Visibility") != "Collapsed"))
            .Where(c => c.Visible || withHiddenColumns)
            .ToList();
        Assert.Contains(columns, c => c.Header == "Current (ConfigMgr)");
        Assert.Contains(columns, c => c.Header == "KEV");
        return columns;
    }

    private static void RunSta(Action action) => StaTestHelper.Run(action);

    private static DataGrid NewGrid()
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            RowHeaderWidth = 0,
            CanUserAddRows = false,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14
        };
        grid.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
        grid.Resources.MergedDictionaries.Add(new ControlsDictionary());
        return grid;
    }

    private static void Layout(DataGrid grid, double width)
    {
        grid.Measure(new Size(width, 600));
        grid.Arrange(new Rect(0, 0, width, 600));
        grid.UpdateLayout();
        // The grid computes its column widths in a queued operation; a width change before that is not redistributed.
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        grid.UpdateLayout();
    }

    /// <summary>The width each header takes with its sort arrow shown, from a grid whose columns size to their headers.</summary>
    private static Dictionary<string, double> HeaderWidths(IEnumerable<string> headers)
    {
        var grid = NewGrid();
        foreach (var header in headers)
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header, Width = DataGridLength.SizeToHeader, SortDirection = System.ComponentModel.ListSortDirection.Ascending
            });
        Layout(grid, 4000);
        return grid.Columns.ToDictionary(c => (string)c.Header, c => Math.Ceiling(c.ActualWidth));
    }

    [WindowTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EveryColumnShowsItsWholeHeaderAtTheDefaultWindowSize(bool restoredFromMaximized, bool withHiddenColumns)
    {
        RunSta(() =>
        {
            var columns = Columns(withHiddenColumns);
            var needed = HeaderWidths(columns.Where(c => c.Header.Length > 0).Select(c => c.Header));
            Assert.All(needed.Values, width => Assert.InRange(width, 30, 400));

            var grid = NewGrid();
            foreach (var column in columns)
            {
                var added = new DataGridTextColumn { Header = column.Header, Width = column.Width };
                if (column.MinWidth is { } min) added.MinWidth = min;
                grid.Columns.Add(added);
            }
            grid.ItemsSource = new[] { "a", "b", "c" };
            // A grid that becomes narrower takes the width away from its columns.
            if (restoredFromMaximized) Layout(grid, 2560 - NavigationPaneWidth - PageMargin);
            Layout(grid, WindowWidth - NavigationPaneWidth - PageMargin);

            var cut = grid.Columns.Where(c => c.Header is string { Length: > 0 } header && c.ActualWidth < needed[header])
                .Select(c => $"{c.Header}: {c.ActualWidth:0} of {needed[(string)c.Header]:0}")
                .ToList();
            Assert.True(cut.Count == 0, "Header cut: " + string.Join("; ", cut));
        });
    }

    [WindowTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoColumnCanShrinkBelowItsHeader(bool withHiddenColumns)
    {
        RunSta(() =>
        {
            var columns = Columns(withHiddenColumns).Where(c => c.Header.Length > 0).ToList();
            var needed = HeaderWidths(columns.Select(c => c.Header));

            var low = columns
                .Where(c => (c.MinWidth ?? 0) < needed[c.Header] || (c.Width.IsAbsolute && c.Width.Value < needed[c.Header]))
                .Select(c => $"{c.Header}: Width {c.Width}, MinWidth {c.MinWidth?.ToString(CultureInfo.InvariantCulture) ?? "not set"}, header {needed[c.Header]:0}")
                .ToList();
            Assert.True(low.Count == 0, "Below the header width: " + string.Join("; ", low));
        });
    }
}
