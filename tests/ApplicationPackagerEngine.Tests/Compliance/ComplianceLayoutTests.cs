using System.Windows;
using ApplicationPackagerUI.ViewModels.Pages;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.ComplianceRenderTests;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerEngine.Tests.Compliance;

/// <summary>The results panel leaves the grids their space at small window sizes.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ComplianceLayoutTests
{
    public static TheoryData<ApplicationTheme, double, double> Sizes() => new()
    {
        { ApplicationTheme.Light, 900 - NavigationPaneWidth, 600 - 48 },
        { ApplicationTheme.Dark, 900 - NavigationPaneWidth, 600 - 48 },
        { ApplicationTheme.Light, 1440 - NavigationPaneWidth, 900 - 48 },
        { ApplicationTheme.Dark, 1440 - NavigationPaneWidth, 900 - 48 }
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void TheResultsTakeAtMostAThirdOfTheSectionAndCollapse(ApplicationTheme theme, double width, double height)
    {
        WithSection(theme, (window, page, viewModel, harness) =>
        {
            ShowTab(window, viewModel, ComplianceViewModel.LiveTab);
            viewModel.Live.SetSelectedDeployments(viewModel.Live.Deployments);
            Run(viewModel.Live.DeleteDeploymentsCommand.ExecuteAsync(null));
            Settle(window);

            var section = ById<FrameworkElement>(page, "viewCompliance");
            var results = ById<FrameworkElement>(page, "pnlComplianceResults");
            var rows = ById<DataGrid>(page, "dgComplianceResults");
            Assert.True(results.IsVisible);
            Assert.True(results.ActualHeight <= section.ActualHeight / 3 + 0.5,
                $"The results take {results.ActualHeight:0} px of the {section.ActualHeight:0} px section at {width}x{height}.");
            Assert.True(rows.ActualHeight >= 60, $"The result rows get {rows.ActualHeight:0} px.");
            if (width > 1000) Assert.True(rows.ActualHeight >= 120, $"The result rows get {rows.ActualHeight:0} px at {width}x{height}.");

            var summary = ById<FrameworkElement>(page, "txtComplianceResultSummary");
            viewModel.Shared.ShowResultRows = false;
            Settle(window);
            Assert.False(rows.IsVisible);
            Assert.True(summary.IsVisible);
            Assert.True(ById<FrameworkElement>(page, "btnComplianceUndo").IsVisible);
            viewModel.Shared.ShowResultRows = true;
            Settle(window);
            Assert.True(rows.IsVisible);
        }, width, height);
    }

    /// <summary>At its minimum width the column shows the cell text of the first row uncut, and the cell tooltip holds <paramref name="tooltip"/>.</summary>
    private static void AssertCellFitsAtMinimum(Window window, DataGrid grid, string header, string text, string? tooltip = null)
    {
        var column = grid.Columns.Single(c => Equals(c.Header, header));
        column.Width = new System.Windows.Controls.DataGridLength(column.MinWidth);
        Settle(window);
        var cell = Descendants(Row(grid, 0)).OfType<System.Windows.Controls.DataGridCell>().Single(c => c.Column == column);
        var block = Descendants(cell).OfType<System.Windows.Controls.TextBlock>().Single();
        Assert.Equal(text, block.Text);
        var needed = new System.Windows.Media.FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
            new System.Windows.Media.Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize,
            System.Windows.Media.Brushes.Black, System.Windows.Media.VisualTreeHelper.GetDpi(block).PixelsPerDip).WidthIncludingTrailingWhitespace;
        Assert.True(needed <= block.ActualWidth + 0.5, $"{header}: '{text}' needs {needed:0} px; the cell gives {block.ActualWidth:0} px at the {column.MinWidth} px minimum.");
        Assert.Equal(tooltip ?? text, block.ToolTip);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void UnknownCountsAndLiveComplianceFitTheirMinimumWidths(ApplicationTheme theme)
    {
        WithSection(theme, (window, page, viewModel, harness) =>
        {
            var service = harness.Service;
            service.Deployments[0] = service.Deployments[0] with { CountsKnown = false };
            service.Compliance[0] = service.Compliance[0] with { CountsKnown = false };
            service.Baselines[0] = service.Baselines[0] with { Compliant = 12, NonCompliant = 3, Failed = 0 };

            ShowTab(window, viewModel, ComplianceViewModel.LiveTab);
            viewModel.Live.SelectedBaseline = null;
            Settle(window);
            var deployments = ById<DataGrid>(page, "dgDeployments");
            deployments.Items.SortDescriptions.Clear();
            Assert.Equal("unknown", ((DeploymentGridRow)deployments.Items[0]).Error);
            AssertCellFitsAtMinimum(window, deployments, "Error", "unknown");
            var baselines = ById<DataGrid>(page, "dgLiveBaselines");
            var first = (BaselineGridRow)baselines.Items[0];
            Assert.Equal("CB-SMB", first.BaselineId);
            AssertCellFitsAtMinimum(window, baselines, "Compliance", "12 / 3 / 0", "12 compliant, 3 non-compliant, 0 error");

            ShowTab(window, viewModel, ComplianceViewModel.AuditingTab);
            viewModel.Auditing.SelectedTabIndex = ComplianceAuditingViewModel.ComplianceTab;
            Settle(window);
            var counts = ById<DataGrid>(page, "dgComplianceCounts");
            Assert.Equal("unknown", ((ComplianceGridRow)counts.Items[0]).Error);
            AssertCellFitsAtMinimum(window, counts, "Error", "unknown");
        });
    }
}
