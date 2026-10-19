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
}
