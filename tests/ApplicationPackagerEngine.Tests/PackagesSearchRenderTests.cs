using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using DataGrid = System.Windows.Controls.DataGrid;
using Image = System.Windows.Controls.Image;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The Packages grid in a real window, in both themes: a search for "7-zip" keeps only the matching rows, and the
/// icon column shows the icon of each remaining row (Q-23b: the dark theme once showed no icons and an unfiltered list).
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PackagesSearchRenderTests
{
    private static string WriteIcon(string folder)
    {
        var path = Path.Combine(folder, "icon.png");
        var pixels = new byte[16 * 16 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 1] = 60; pixels[i + 2] = 30; pixels[i + 3] = 255; }
        var source = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static PackagerInfo NewRow(string uid, string vendor, string application, string iconPath)
    {
        var row = new PackagerInfo { Uid = uid, Vendor = vendor, Application = application, CMName = application };
        // The row looks its icon up in the icon pack; the test gives it the file that lookup would find.
        typeof(PackagerInfo).GetField("_iconPath", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(row, iconPath);
        return row;
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ASearchForSevenZipKeepsOnlyTheMatchingRowsAndTheirIcons(ApplicationTheme theme)
    {
        var folder = Path.Combine(Path.GetTempPath(), "apd-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var icon = WriteIcon(folder);
            WithApplication(theme, () =>
            {
                System.Windows.Application.Current.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
                var viewModel = (PackagesViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
                var page = new PackagesPage(viewModel);
                viewModel.Packagers = new(
                [
                    NewRow("7zip", "Igor Pavlov", "7-Zip", icon),
                    NewRow("notepadpp", "Notepad++ Team", "Notepad++", icon),
                    NewRow("7zip-zs", "Tc4shell", "7-Zip Zstandard", icon),
                    NewRow("git", "The Git Development Community", "Git", icon),
                    NewRow("vlc", "VideoLAN", "VLC media player", icon),
                ]);
                viewModel.FilteredPackagers = new(viewModel.Packagers);
                var window = new Window { Width = 1440 - 48, Height = 900 - 48, WindowStyle = WindowStyle.None, Content = page };
                Show(window);
                try
                {
                    Settle(window);
                    var grid = (DataGrid)page.FindName("PackagerGrid");
                    Assert.Equal(5, grid.Items.Count);

                    viewModel.SearchText = "7-zip";
                    Settle(window);

                    Assert.Equal(2, grid.Items.Count);
                    Assert.Equal(["7-Zip", "7-Zip Zstandard"], grid.Items.Cast<PackagerInfo>().Select(p => p.Application).OrderBy(n => n.Length));
                    Assert.Equal("Showing 2 of 5 packager(s).", viewModel.StatusText);
                    for (var i = 0; i < grid.Items.Count; i++)
                    {
                        var images = Descendants(Row(grid, i)).OfType<Image>()
                            .Where(image => image.Parent is Grid g && AutomationProperties.GetAutomationId(g) == "PackagerIcon").ToList();
                        var image = Assert.Single(images);
                        Assert.NotNull(image.Source);
                        Assert.True(image.ActualWidth > 0, $"{theme}: the icon of row {i} has no width.");
                    }

                    viewModel.SearchText = "";
                    Settle(window);
                    Assert.Equal(5, grid.Items.Count);
                }
                finally { window.Close(); }
            });
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
