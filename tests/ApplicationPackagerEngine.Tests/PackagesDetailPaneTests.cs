using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Helpers;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using Application = System.Windows.Application;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Overhaul V1 phase 4 (Q-126): the detail pane of the focused application on the Packages page.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PackagesDetailPaneTests
{
    [Fact]
    public void TheLinesNameEveryFactAndNeverShowABlank()
    {
        var row = new PackagerInfo { Uid = "a", Application = "Alpha", Vendor = "Contoso", Status = "Ready" };
        var lines = PackagesDetail.Lines(row, "").ToDictionary(l => l.Label, l => l.Text);
        Assert.Equal("Contoso", lines["Vendor"]);
        Assert.Equal("Ready", lines["Status"]);
        Assert.Equal("Not known", lines["ConfigMgr version"]);
        Assert.Equal("Not known", lines["Latest version"]);
        Assert.Equal("Not checked", lines["CVEs"]);
        Assert.Equal("Not staged", lines["Stage"]);

        row.CurrentVersion = "1.0";
        row.LatestVersion = "2.0";
        var full = PackagesDetail.Lines(row, "Staged 2.0").ToDictionary(l => l.Label, l => l.Text);
        Assert.Equal("1.0", full["ConfigMgr version"]);
        Assert.Equal("2.0", full["Latest version"]);
        Assert.Equal("Staged 2.0", full["Stage"]);
    }

    [Theory]
    [InlineData(false, null, "Not staged")]
    [InlineData(true, null, "Staged")]
    [InlineData(true, "26.06", "Staged 26.06")]
    public void TheStageTextFollowsTheManifest(bool has, string? version, string expected) =>
        Assert.Equal(expected, PackagesDetail.StageText(has, version));

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ThePaneFollowsTheFocusedRowOpensAndClosesAndGivesTheFocusBack(ApplicationTheme theme)
    {
        RenderKit.WithApplication(theme, () =>
        {
            Application.Current.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
            var rows = new List<PackagerInfo> { new() { Uid = "a", Application = "Alpha", Vendor = "Contoso", Status = "Ready" }, new() { Uid = "b", Application = "Bravo", Status = "Ready" } };
            var viewModel = (PackagesViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
            viewModel.Packagers = new ObservableCollection<PackagerInfo>(rows);
            viewModel.FilteredPackagers = new ObservableCollection<PackagerInfo>(rows);
            viewModel.IsDetailPaneOpen = true;
            var page = new PackagesPage(viewModel);
            var window = new Window { Width = 1500, Height = 800, WindowStyle = WindowStyle.None, Content = page };
            RenderKit.Show(window);
            try
            {
                window.UpdateLayout();
                var pane = RenderKit.ById<Border>(page, "pnlDetailPane");
                Assert.True(pane.IsVisible, "The pane is closed in a wide window with the saved choice open.");

                viewModel.DetailPackager = rows[0];
                window.UpdateLayout();
                Assert.Equal("Alpha", RenderKit.ById<TextBlock>(page, "txtDetailTitle").Text);
                var texts = RenderKit.Descendants(pane).OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert.Contains("Contoso", texts);
                Assert.Contains("Not known", texts);

                var close = RenderKit.ById<ButtonBase>(page, "btnDetailPaneClose");
                close.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.UpdateLayout();
                Assert.False(pane.IsVisible);
                // A closed pane leaves no control that takes the keyboard: its buttons are collapsed.
                Assert.False(RenderKit.ById<ButtonBase>(page, "btnDetailWorkbench").IsVisible);
                var open = RenderKit.ById<ButtonBase>(page, "btnDetailPaneOpen");
                Assert.True(open.IsVisible);
                Assert.False(viewModel.IsDetailPaneOpen);

                open.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.UpdateLayout();
                Assert.True(pane.IsVisible);
                Assert.True(viewModel.IsDetailPaneOpen);
            }
            finally { window.Close(); }
        });
    }

    private static PackagesViewModel PaneViewModel(double savedWidth)
    {
        Application.Current.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
        var viewModel = (PackagesViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
        viewModel.Packagers = [];
        viewModel.FilteredPackagers = [];
        viewModel.IsDetailPaneOpen = true;
        viewModel.DetailPaneWidth = savedWidth;
        return viewModel;
    }

    private static void Press(GridSplitter splitter, Key key)
    {
        var source = PresentationSource.FromVisual(splitter)!;
        splitter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
        splitter.UpdateLayout();
        splitter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyUpEvent });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheSplitterChangesTheWidthWithTheKeyboardAndTheWidthIsSaved(ApplicationTheme theme)
    {
        var root = Path.Combine(Path.GetTempPath(), "apd-pane-" + Guid.NewGuid().ToString("N"));
        var before = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, root);
        try
        {
            RenderKit.WithApplication(theme, () =>
            {
                using var scope = SiteContext.Scope(null, root);
                Directory.CreateDirectory(AppDataPaths.Root);
                var viewModel = PaneViewModel(400);
                var settings = new SettingsService();
                typeof(PackagesViewModel).GetField("_settingsService", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, settings);
                var page = new PackagesPage(viewModel);
                var window = new Window { Width = 1690, Height = 900, WindowStyle = WindowStyle.None, Content = page };
                RenderKit.Show(window);
                try
                {
                    RenderKit.Settle(window);
                    var pane = RenderKit.ById<Border>(page, "pnlDetailPane");
                    var splitter = RenderKit.ById<GridSplitter>(page, "splDetailPane");
                    Assert.Equal(400, pane.ActualWidth, 1.0);
                    Assert.True(splitter.IsVisible && splitter.Focusable && splitter.IsTabStop, "The splitter cannot take the keyboard focus.");
                    Assert.True(splitter.ActualWidth >= 4, $"The splitter is {splitter.ActualWidth:0.0} px wide; it is hard to grab.");
                    Assert.Equal("Detail pane width", AutomationProperties.GetName(splitter));

                    Press(splitter, Key.Left);
                    RenderKit.Settle(window);
                    Assert.Equal(416, pane.ActualWidth, 1.0);
                    Assert.Equal(416, viewModel.DetailPaneWidth, 1.0);
                    Assert.Equal(416, settings.Load().PackagesDetailPaneWidth, 1.0);

                    RenderKit.ById<ButtonBase>(page, "btnDetailPaneClose").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    RenderKit.Settle(window);
                    Assert.False(pane.IsVisible);
                    Assert.False(splitter.IsVisible, "The splitter of a closed pane stays in the tab order.");

                    RenderKit.ById<ButtonBase>(page, "btnDetailPaneOpen").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    RenderKit.Settle(window);
                    Assert.Equal(416, pane.ActualWidth, 1.0);
                }
                finally { window.Close(); }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, before);
            TestCleanup.DeleteDirectory(root);
        }
    }

    [Fact]
    public void ANarrowWindowLimitsThePaneAndKeepsTheSavedWidth()
    {
        RenderKit.WithApplication(ApplicationTheme.Light, () =>
        {
            var viewModel = PaneViewModel(640);
            var page = new PackagesPage(viewModel);
            var window = new Window { Width = 1024 - 230, Height = 720 - 48, WindowStyle = WindowStyle.None, Content = page };
            RenderKit.Show(window);
            try
            {
                RenderKit.Settle(window);
                var pane = RenderKit.ById<Border>(page, "pnlDetailPane");
                Assert.False(pane.IsVisible, "A narrow window starts with the pane open.");
                RenderKit.ById<ButtonBase>(page, "btnDetailPaneOpen").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                RenderKit.Settle(window);
                var grid = (System.Windows.Controls.DataGrid)page.FindName("PackagerGrid");
                Assert.InRange(pane.ActualWidth, PackagesPage.DetailPaneMinimumWidth - 0.5, 300);
                Assert.True(grid.ActualWidth >= 440, $"The grid keeps {grid.ActualWidth:0} px beside the pane.");
                Assert.Equal(640, viewModel.DetailPaneWidth);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ANewSettingsFileHasTheDefaultPaneWidth() => Assert.Equal(320, new AppSettings().PackagesDetailPaneWidth);
}
