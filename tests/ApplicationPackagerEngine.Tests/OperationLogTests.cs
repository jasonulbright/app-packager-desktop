using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using ListBox = System.Windows.Controls.ListBox;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The capped log behind the Logs button and the last line under the grid (overhaul V1, section 2.7).</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class OperationLogTests
{
    [Fact]
    public void TheLogKeepsTheNewestLinesUpToItsLimit()
    {
        var log = new OperationLog(3);
        for (var i = 1; i <= 10; i++) log.Append("line " + i);
        Assert.Equal(["line 8", "line 9", "line 10"], log.Select(e => e.Text));
        Assert.Equal("line 10", log.Last);
        Assert.Equal(2000, new OperationLog().Limit);
    }

    [Fact]
    public void ALineFromAPoolThreadReachesABoundLog()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            AppPackagerFitTests.Create(0, out var viewModel);
            viewModel.LogText = "first";
            var window = new LogWindow(viewModel);
            Show(window);
            try
            {
                Settle(window);
                var download = Task.Run(() => viewModel.AppendLog("from a download"));
                Run(download);
                download.GetAwaiter().GetResult();
                Settle(window);
                var list = ById<ListBox>(window, "lstLog");
                Assert.Equal(2, list.Items.Count);
                Assert.EndsWith("from a download", viewModel.LastLogLine);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void EqualLinesAreSeparateAndTheLogScrollsOnlyFromItsEnd()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            AppPackagerFitTests.Create(0, out var viewModel);
            viewModel.LogText = string.Join(Environment.NewLine, Enumerable.Repeat("same line", 300));
            var window = new LogWindow(viewModel);
            Show(window);
            try
            {
                Settle(window);
                Settle(window);
                var list = ById<ListBox>(window, "lstLog");
                var scroller = Descendants(list).OfType<ScrollViewer>().First();
                Assert.True(scroller.VerticalOffset > 0 && scroller.VerticalOffset >= scroller.ScrollableHeight - 0.5, "The log does not open at its end.");
                list.SelectedItem = viewModel.LogLines[299];
                Assert.Equal(299, list.SelectedIndex);
                viewModel.LogLines.Append("same line");
                Settle(window);
                Settle(window);
                Assert.True(scroller.VerticalOffset >= scroller.ScrollableHeight - 0.5, "The log does not follow the new line from its end.");

                scroller.ScrollToTop();
                Settle(window);
                viewModel.LogLines.Append("same line");
                Settle(window);
                Settle(window);
                Assert.Equal(0, scroller.VerticalOffset, 0.5);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ThePageShowsTheLastLineAndTheTwoButtonsUnderTheGrid(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var page = AppPackagerFitTests.Create(4, out var viewModel);
            var window = new Window { Width = 1920 - 230, Height = 1032 - 48, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Settle(window);
                viewModel.AppendLog("The last line.");
                Settle(window);
                var summary = ById<TextBlock>(page, "txtLogSummary");
                Assert.EndsWith("The last line.", summary.Text);
                var logs = ById<ButtonBase>(page, "btnLogs");
                var history = ById<ButtonBase>(page, "btnOperationHistory");
                Assert.True(logs.IsVisible && logs.IsEnabled && logs.Focusable && logs.IsTabStop);
                Assert.False(history.IsEnabled, "Operation history is on with no selected row.");
                var grid = (System.Windows.Controls.DataGrid)page.FindName("PackagerGrid");
                grid.SelectedItem = viewModel.FilteredPackagers[0];
                Settle(window);
                Assert.True(history.IsEnabled);
                PageChromeKit.AssertNothingClipped(ById<Border>(page, "pnlLogBar"), $"log bar {theme}");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheLogWindowShowsTheKeptLinesAndFollowsNewOnes(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            AppPackagerFitTests.Create(0, out var viewModel);
            viewModel.LogText = string.Join(Environment.NewLine, Enumerable.Range(1, 500).Select(i => "line " + i));
            var window = new LogWindow(viewModel);
            Show(window);
            try
            {
                Settle(window);
                var list = ById<ListBox>(window, "lstLog");
                Assert.Equal(500, list.Items.Count);
                // The list is virtualized: far fewer containers than lines.
                Assert.True(Descendants(list).OfType<ListBoxItem>().Count() < 200);
                viewModel.AppendLog("a new line");
                Settle(window);
                Assert.Equal(501, list.Items.Count);
                foreach (var id in new[] { "btnLogCopy", "btnLogFolder", "btnLogClose" })
                    Assert.True(ById<ButtonBase>(window, id).IsVisible);
                PageChromeKit.AssertAutomationNames(window, $"log window {theme}");
            }
            finally { window.Close(); }
        });
    }
}
