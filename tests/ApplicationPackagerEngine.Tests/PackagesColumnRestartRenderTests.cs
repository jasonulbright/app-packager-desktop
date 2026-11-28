using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A grid that is narrower than its columns takes width from them. A width that the operator set with a header grip must show again after a
/// restart in a window of the same size.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PackagesColumnRestartRenderTests : IDisposable
{
    // Less than the column total of the page, so some columns show less than their width, and more than the minimum total.
    private const double NarrowGrid = 1500;
    private const double WideGrid = 1800;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ap-columns-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public PackagesColumnRestartRenderTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _savedRoot);
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
    }

    private sealed record Shown(PackagesPage Page, Window Window, DataGrid Grid)
    {
        public DataGridColumn Column(string header) => Grid.Columns.Single(c => c.Header as string == header);

        public string Widths() => string.Join("; ", Grid.Columns.Where(c => c.Visibility == Visibility.Visible && c.Header is string)
            .Select(c => $"{c.Header} {c.ActualWidth:0.##} ({c.Width.UnitType} {c.Width.Value:0.##}, min {c.MinWidth:0.##})"));

        public void Resize(double width)
        {
            Grid.Width = width;
            Settle(Window);
        }

        public void Drag(string header, double change) => ColumnGripKit.Drag(Window, Grid, Column(header), change);

        public void DoubleClick(string header) => ColumnGripKit.DoubleClick(Window, Grid, Column(header));
    }

    private static PackagesViewModel ViewModel(SettingsService settings)
    {
        var viewModel = (PackagesViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
        typeof(PackagesViewModel).GetField("_settingsService", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(viewModel, settings);
        viewModel.FilteredPackagers = new(Enumerable.Range(0, 3).Select(i => new PackagerInfo { Uid = "a" + i, Application = "App " + i, Vendor = "Contoso" }));
        return viewModel;
    }

    /// <summary>One run of the page, from its construction to the close of its window, over the settings of the test.</summary>
    private static void Run(SettingsService settings, Action<Shown> test)
    {
        var page = new PackagesPage(ViewModel(settings));
        var grid = (DataGrid)page.FindName("PackagerGrid");
        grid.Width = NarrowGrid;
        var window = new Window { Width = 2000, Height = 900, WindowStyle = WindowStyle.None, Content = page };
        Show(window);
        try
        {
            Settle(window);
            test(new Shown(page, window, grid));
        }
        finally { window.Close(); }
    }

    private static string Saved(SettingsService settings) =>
        string.Join("; ", settings.Load().PackagesGridLayout?.Columns.Where(c => c.Width != 0).Select(c => $"{c.Id} {c.Width:0.##}") ?? []);

    private static void InApplication(Action test) => WithApplication(ApplicationTheme.Light, () =>
    {
        System.Windows.Application.Current.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
        test();
    });

    [Fact]
    public void ADraggedWidthShowsAgainAfterARestartAndTheOtherColumnsFollowThePage() => InApplication(() =>
    {
        var settings = new SettingsService();
        double seen = 0;
        var report = "";
        Run(settings, shown =>
        {
            var vendor = shown.Column("Vendor");
            var before = vendor.ActualWidth;
            Assert.True(vendor.Width.IsStar, "Vendor does not follow the page before the drag: " + shown.Widths());
            report = "Before the drag: " + shown.Widths();
            shown.Drag("Vendor", 60);
            seen = vendor.ActualWidth;
            report += Environment.NewLine + "After the drag: " + shown.Widths() + Environment.NewLine + "Saved: " + Saved(settings);
            Assert.True(seen > before + 50, report);
        });

        Run(settings, shown =>
        {
            report += Environment.NewLine + "After the restart: " + shown.Widths();
            Assert.True(Math.Abs(shown.Column("Vendor").ActualWidth - seen) < 0.5, report);
            // Only the dragged column has a saved width.
            var widths = settings.Load().PackagesGridLayout!.Columns.Where(c => c.Width != 0).ToList();
            Assert.True(widths.Count == 1 && widths[0].Id == "Vendor", report);

            // In a wider grid each column that the operator did not size shows the width of the page definition.
            shown.Resize(WideGrid);
            Assert.True(shown.Column("Status").Width.IsStar, shown.Widths());
            Assert.Equal(120, shown.Column("CVEs").ActualWidth, 1);
            Assert.Equal(seen, shown.Column("Vendor").ActualWidth, 1);
            Assert.True(shown.Column("Application").Width.IsStar);
            Assert.True(shown.Column("Application").ActualWidth > 161, shown.Widths());

            // A restored width can be dragged to a smaller one, and that one is saved.
            shown.Resize(NarrowGrid);
            shown.Drag("Vendor", -50);
            Assert.Equal(seen - 50, shown.Column("Vendor").ActualWidth, 1);
            seen = shown.Column("Vendor").ActualWidth;
        });

        Run(settings, shown =>
        {
            Assert.Equal(seen, shown.Column("Vendor").ActualWidth, 1);
            typeof(PackagesPage).GetMethod("ResetColumnLayout", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(shown.Page, null);
            Settle(shown.Window);
            var vendor = shown.Column("Vendor");
            Assert.True(vendor.Width.IsStar && vendor.Width.Value == 328, shown.Widths());
            Assert.Equal(100, vendor.MinWidth);
        });
    });

    [Fact]
    public void ADoubleClickOnTheGripFitsASizedColumnAndTheFittedWidthIsKept() => InApplication(() =>
    {
        var settings = new SettingsService();
        double fitted = 0;
        Run(settings, shown => shown.Drag("Vendor", 60));

        // The width of the column is held now, as it is after a restart.
        Run(settings, shown =>
        {
            var vendor = shown.Column("Vendor");
            var sized = vendor.ActualWidth;
            shown.DoubleClick("Vendor");
            fitted = vendor.ActualWidth;
            var report = $"Sized {sized:0.##}, saved {Saved(settings)}: {shown.Widths()}";
            Assert.True(fitted < sized - 40, report);
            Assert.True(vendor.Width.IsAbsolute, report);
            Assert.True(Math.Abs(settings.Load().PackagesGridLayout!.Columns.Single(c => c.Id == "Vendor").Width - fitted) < 0.5, report);
        });

        Run(settings, shown => Assert.True(Math.Abs(shown.Column("Vendor").ActualWidth - fitted) < 0.5, shown.Widths()));
    });

    [Fact]
    public void ASavedLayoutWithOnlyAWidthLoadsAndAPageWidthInItIsNotAChoiceOfTheOperator() => InApplication(() =>
    {
        var settings = new SettingsService();
        var stored = settings.Load();
        stored.PackagesGridLayout = new() { Columns = [new() { Id = "Vendor", Width = 179.67 }, new() { Id = "CVEs", Width = 120 }] };
        settings.Save(stored);

        Run(settings, shown =>
        {
            Assert.True(Math.Abs(shown.Column("Vendor").ActualWidth - 179.67) < 0.5, shown.Widths());
            // A column that holds a choice has it as its least width; the width of the page holds nothing.
            Assert.Equal(179.67, shown.Column("Vendor").MinWidth, 0.01);
            Assert.Equal(90, shown.Column("CVEs").MinWidth);
        });
    });
}
