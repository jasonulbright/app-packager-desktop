using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerUI.Models;
using Wpf.Ui.Appearance;
using Xunit.Abstractions;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridRow = System.Windows.Controls.DataGridRow;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The Packages grid with hundreds of rows (overhaul V1, phase 3): the grid makes row containers for the rows near the view only. The test also
/// records the measured times; it asserts on the number of row containers and never on a time. The lines go to the test output and, when
/// APD_MEASUREMENTS_FILE names a file, to that file, which the CI job prints.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PackagesGridRowCountTests(ITestOutputHelper output)
{
    public const string MeasurementsVariable = "APD_MEASUREMENTS_FILE";

    // The view, and no more than one view of cache before and after it, with a margin for partly shown rows.
    private static int MostRows(int visible) => 3 * visible + 4;

    private static List<DataGridRow> Realized(DataGrid grid) => Descendants(grid).OfType<DataGridRow>().ToList();

    private static int Visible(DataGrid grid)
    {
        var presenter = Descendants(grid).OfType<ScrollContentPresenter>().First();
        return Realized(grid).Count(row =>
        {
            if (!row.IsVisible || row.ActualHeight <= 0) return false;
            var bounds = row.TransformToAncestor(presenter).TransformBounds(new Rect(row.RenderSize));
            return bounds.Bottom > 0.5 && bounds.Top < presenter.ActualHeight - 0.5;
        });
    }

    private void Record(string line)
    {
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable(MeasurementsVariable) is { Length: > 0 } file)
            File.AppendAllText(file, line + Environment.NewLine);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(2000)]
    public void TheGridMakesRowContainersForTheRowsNearTheViewOnly(int count)
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var page = AppPackagerFitTests.Create(count, out var viewModel);
            var window = new Window { Width = 1920 - 230, Height = 1032 - 48, WindowStyle = WindowStyle.None, Content = page };
            var watch = Stopwatch.StartNew();
            Show(window);
            try
            {
                Settle(window);
                var layout = watch.Elapsed;
                var grid = (DataGrid)page.FindName("PackagerGrid");
                Assert.Equal(count, grid.Items.Count);
                var visible = Visible(grid);
                var realized = Realized(grid).Count;
                Assert.True(visible >= 5, $"{count} rows: the grid shows {visible} rows.");

                watch.Restart();
                foreach (var row in viewModel.FilteredPackagers) row.Status = "Update available";
                Settle(window);
                var refresh = watch.Elapsed;

                watch.Restart();
                grid.ScrollIntoView(grid.Items[count - 1]);
                Settle(window);
                var scroll = watch.Elapsed;
                var realizedAtEnd = Realized(grid).Count;

                Record($"Packages grid, {count} rows at 1920x1032: {visible} rows in view, {realized} row containers after the first layout "
                    + $"({layout.TotalMilliseconds:0} ms), status change of every row {refresh.TotalMilliseconds:0} ms, "
                    + $"{realizedAtEnd} row containers after a scroll to the last row ({scroll.TotalMilliseconds:0} ms).");

                Assert.True(realized <= MostRows(visible), $"{count} rows: {realized} row containers for {visible} rows in view. Row virtualization is off.");
                Assert.True(realizedAtEnd <= MostRows(visible), $"{count} rows: {realizedAtEnd} row containers after the scroll, for {visible} rows in view.");
                Assert.Contains(Realized(grid), row => ReferenceEquals(row.Item, viewModel.FilteredPackagers[count - 1]));
                Assert.True(VirtualizingPanel.GetIsVirtualizing(grid));
                Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(grid));
                Assert.True(ScrollViewer.GetCanContentScroll(grid));
            }
            finally { window.Close(); }
        });
    }
}
