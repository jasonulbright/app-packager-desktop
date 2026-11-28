using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Helpers;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using Application = System.Windows.Application;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The one list of row actions behind the grid context menu and the More actions menu of the detail pane (overhaul V1, phase 4).</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PackagesRowActionsTests
{
    private static List<(string Id, string Header, bool Enabled)> Items(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Select(i => (AutomationProperties.GetAutomationId(i), (string)i.Header, i.IsEnabled)).ToList();

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheGridMenuAndTheMoreActionsMenuOfThePaneOfferTheSameActions(ApplicationTheme theme)
    {
        RenderKit.WithApplication(theme, () =>
        {
            Application.Current.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
            var row = new PackagerInfo { Uid = "a", Application = "Alpha", Vendor = "Contoso", Status = "Ready" };
            var viewModel = (PackagesViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
            viewModel.Packagers = [row];
            viewModel.FilteredPackagers = [row];
            viewModel.IsDetailPaneOpen = true;
            var page = new PackagesPage(viewModel);
            var window = new Window { Width = 1500, Height = 800, WindowStyle = WindowStyle.None, Content = page };
            RenderKit.Show(window);
            try
            {
                RenderKit.Settle(window);
                var more = RenderKit.ById<ButtonBase>(page, "btnDetailMoreActions");
                Assert.False(more.IsEnabled, "More actions is on with no application in the pane.");
                Assert.Equal("More actions", (string)more.Content);

                var grid = (System.Windows.Controls.DataGrid)page.FindName("PackagerGrid");
                grid.SelectedItem = row;
                RenderKit.Settle(window);
                Assert.True(more.IsEnabled && more.Focusable && more.IsTabStop, "More actions cannot take the keyboard focus.");

                var gridMenu = grid.ContextMenu;
                gridMenu.PlacementTarget = grid;
                gridMenu.IsOpen = true;
                RenderKit.Settle(window);
                var fromGrid = Items(gridMenu);
                gridMenu.IsOpen = false;
                var fromPane = Items(page.BuildRowActionsMenu());

                // The row menu of the grid also opens the pane; its other entries are the entries of the pane.
                Assert.Equal(("miRowShowDetails", "Show details", true), fromGrid[0]);
                Assert.Equal(fromPane, fromGrid.Skip(1));
                Assert.Equal(RowActions.All.Select(a => a.AutomationId), fromPane.Select(i => i.Id));
                Assert.Equal(RowActions.All.Select(a => a.Header), fromPane.Select(i => i.Header));
                // An application without a CPE has no CVE sources and no CVE ids.
                Assert.Equal(["miRowOpenCveSources", "miRowCopyCveIds"], fromPane.Where(i => !i.Enabled).Select(i => i.Id));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void WithoutARowEveryActionIsOffAndWithACpeEveryActionIsOn()
    {
        Assert.All(RowActions.All, action => Assert.False(action.IsEnabledFor(null)));
        var row = new PackagerInfo { Uid = "a", Cpe = "cpe:2.3:a:contoso:alpha" };
        Assert.All(RowActions.All, action => Assert.True(action.IsEnabledFor(row)));
        Assert.Equal(RowActions.All.Count, RowActions.All.Select(a => a.AutomationId).Distinct().Count());
    }
}
