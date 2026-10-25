using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridRow = System.Windows.Controls.DataGridRow;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The Packages grid in a real window, in both themes: a selected row differs clearly from a hovered and an unselected row.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PackagesGridRenderTests
{
    /// <summary>The least RGB distance between two row fills that reads as a different fill; the fills of the theme differ by about 15.</summary>
    private const double ClearlyDifferent = 30;

    private static double Distance(Color a, Color b) =>
        Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    /// <summary>The fill of the row: the most common pixel of its first text column, outside the text.</summary>
    private static Color Fill(Window window, DataGridRow row) =>
        MostCommon(Pixels(window, Descendants(row).OfType<System.Windows.Controls.DataGridCell>().First(c => c.Column is DataGridTextColumn)));

    /// <summary>
    /// The pointer state of a row without a real pointer: the input system writes the same read-only property when the
    /// pointer enters the row.
    /// </summary>
    private static void SetPointerOver(UIElement element, bool value)
    {
        var key = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        element.SetValue(key, value);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ASelectedRowDiffersClearlyFromAHoveredRowAndAnUnselectedRow(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            System.Windows.Application.Current.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
            // The page needs only the bound rows; a constructed view model reads the settings and the definitions folder.
            var viewModel = (PackagesViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
            var page = new PackagesPage(viewModel);
            viewModel.FilteredPackagers = new(Enumerable.Range(0, 4).Select(i => new PackagerInfo { Uid = $"app{i}", Vendor = "Contoso", Application = $"Contoso Application {i}" }));
            var window = new Window { Width = 1440 - 48, Height = 900 - 48, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Settle(window);
                var grid = (DataGrid)page.FindName("PackagerGrid");
                Assert.NotNull(grid.RowStyle);
                Assert.Same(page.TryFindResource("SelectableRow"), grid.RowStyle);
                grid.SelectedIndex = 0;
                Settle(window);
                var selected = Row(grid, 0);
                var hovered = Row(grid, 1);
                var normal = Row(grid, 2);
                SetPointerOver(hovered, true);
                Settle(window);
                Assert.True(selected.IsSelected && !hovered.IsSelected && !normal.IsSelected);
                Assert.True((bool)hovered.GetValue(UIElement.IsMouseOverProperty) && !(bool)normal.GetValue(UIElement.IsMouseOverProperty));

                var (s, h, n) = (Fill(window, selected), Fill(window, hovered), Fill(window, normal));
                var where = $"{theme}: selected {s}, hovered {h}, unselected {n}";
                Assert.True(Distance(s, h) >= ClearlyDifferent, $"{where}: the selected row is not clearly different from the hovered row ({Distance(s, h):0}).");
                Assert.True(Distance(s, n) >= ClearlyDifferent, $"{where}: the selected row is not clearly different from an unselected row ({Distance(s, n):0}).");
                Assert.True(Distance(h, n) >= 8, $"{where}: the hovered row shows no change ({Distance(h, n):0}).");
                Assert.True(Contrast(s, n) >= 1.15 || Distance(s, n) >= 40, $"{where}: the selected row is not visible on an unselected row.");
                var pill = Descendants(selected).OfType<System.Windows.Shapes.Rectangle>().Single(r => r.Name == "ActiveRectangle");
                Assert.True(pill.IsVisible, $"{where}: the selected row shows no pill.");
                var text = Pixels(window, Descendants(selected).OfType<System.Windows.Controls.DataGridCell>().First(c => c.Column is DataGridTextColumn), $"packages-rows-{theme}");
                Assert.True(text.Max(p => Contrast(p, s)) >= 4.5, $"{where}: the text of the selected row has a contrast of {text.Max(p => Contrast(p, s)):0.00}.");
                SetPointerOver(hovered, false);

                // The check box column keeps its two-way binding on a row of the shared style.
                viewModel.FilteredPackagers[2].IsSelected = true;
                Settle(window);
                Assert.True(Descendants(Row(grid, 2)).OfType<CheckBox>().Single().IsChecked);
                Assert.Equal(1, grid.FrozenColumnCount);
            }
            finally { window.Close(); }
        });
    }
}
