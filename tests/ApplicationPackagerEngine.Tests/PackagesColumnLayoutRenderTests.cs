using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The layout that the grid reads back after the operator moves, sizes and sorts columns.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PackagesColumnLayoutRenderTests
{
    [Fact]
    public void TheGridReadsBackAMovedASizedAndASortedColumn()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            System.Windows.Application.Current.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
            var viewModel = (PackagesViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
            viewModel.FilteredPackagers = new(Enumerable.Range(0, 3).Select(i => new PackagerInfo { Uid = "a" + i, Application = "App " + i, Vendor = "Contoso" }));
            var page = new PackagesPage(viewModel);
            var window = new Window { Width = 1700, Height = 900, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Settle(window);
                var before = page.ReadColumnLayout();
                Assert.All(before.Columns, c => Assert.Equal(0, c.Width));
                Assert.Equal("", before.SortId);

                var grid = (DataGrid)page.FindName("PackagerGrid");
                var vendor = grid.Columns.Single(c => c.Header as string == "Vendor");
                var application = grid.Columns.Single(c => c.Header as string == "Application");
                vendor.DisplayIndex = application.DisplayIndex;
                vendor.Width = new DataGridLength(222);
                vendor.SortDirection = System.ComponentModel.ListSortDirection.Descending;

                var after = page.ReadColumnLayout();
                Assert.Equal(["Vendor", "Application"], after.Columns.Take(2).Select(c => c.Id));
                Assert.Equal(222, after.Columns[0].Width);
                Assert.Equal("Vendor", after.SortId);
                Assert.True(after.SortDescending);
            }
            finally { window.Close(); }
        });
    }
}
