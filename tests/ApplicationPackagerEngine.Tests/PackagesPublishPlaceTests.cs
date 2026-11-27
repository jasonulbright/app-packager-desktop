using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Back from the publish review keeps the focused row, the scroll position and the checks, and the focus goes to Publish All.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PackagesPublishPlaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apd-place-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public PackagesPublishPlaceTests()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
        Directory.CreateDirectory(AppDataPaths.Root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        TestCleanup.DeleteDirectory(_root);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void BackFromTheReviewKeepsTheRowAndTheScrollAndFocusesPublishAll(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            System.Windows.Application.Current.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
            using var scope = SiteContext.Scope(null, _root);
            File.WriteAllText(SettingsService.SettingsPath,
                """{ "FirstRunCompleted": true, "Systems": { "ConfigMgr": false, "Intune": false, "Wsus": true }, "Wsus": { "ServerName": "wsus01.contoso.com" } }""");
            var rows = Enumerable.Range(0, 100)
                .Select(i => new PackagerInfo { Uid = "app" + i, Application = "Application " + i, Vendor = "Contoso", LatestVersion = "1.0", IsSelected = i % 10 == 0 })
                .ToArray();
            var dialogs = new PublishRunKit.Dialogs();
            var viewModel = PublishRunKit.Create(new SettingsService(), dialogs, rows);
            viewModel.IsPublishAllAvailable = true;
            var page = new PackagesPage(viewModel);
            var window = new Window { Width = 1600, Height = 900, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Settle(window);
                var grid = (DataGrid)page.FindName("PackagerGrid");
                grid.ScrollIntoView(rows[60]);
                Settle(window);
                grid.SelectedItem = rows[60];
                var scroller = Descendants(grid).OfType<ScrollViewer>().First();
                var offset = scroller.VerticalOffset;
                Assert.True(offset > 0, "The grid did not scroll.");
                // The review window of the program takes the focus and can move the grid; the dialog of the test does the same.
                dialogs.Answers.Enqueue(_ =>
                {
                    scroller.ScrollToVerticalOffset(0);
                    grid.SelectedItem = null;
                    window.UpdateLayout();
                    return false;
                });

                var task = PublishRunKit.PublishAllAsync(viewModel);
                Settle(window);

                Assert.True(task.IsCompletedSuccessfully);
                Assert.Same(rows[60], grid.SelectedItem);
                Assert.Equal(offset, scroller.VerticalOffset, 1.0);
                Assert.Equal(10, rows.Count(r => r.IsSelected));
                var publish = ById<System.Windows.Controls.Button>(page, "btnPublishAll");
                Assert.Same(publish, FocusManager.GetFocusedElement(window));
            }
            finally { window.Close(); }
        });
    }
}
