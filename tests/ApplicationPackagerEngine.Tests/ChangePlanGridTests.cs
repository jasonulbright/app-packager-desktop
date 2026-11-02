using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerUI.Helpers;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The plan grid and the result grid of the change-plan window at its default size, over a plan of eight items with mixed outcomes.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ChangePlanGridTests
{
    private static ChangePlanViewModel Eight() => new(
        new ChangePlan("Remove maintenance windows", "Remove 8 windows", Enumerable.Range(1, 8).Select(i => new ChangePlanItem("Remove", "Window " + i, "Collection SMS0000" + i))),
        (confirmed, _, _) => Task.FromResult(new ChangePlanResult(confirmed.Plan.Items.Select((item, i) => (i % 4) switch
        {
            1 => new ChangePlanItemResult(item, false, "Access was denied."),
            3 => ChangePlanItemResult.Skip(item, "Nothing to remove."),
            _ => new ChangePlanItemResult(item, true, "Removed.")
        }).ToList())));

    private static void WithWindow(ApplicationTheme theme, Action<Window, DataGrid, DataGrid> test)
    {
        WithApplication(theme, () =>
        {
            var viewModel = Eight();
            var window = new ChangePlanWindow(viewModel);
            Show(window);
            try
            {
                test(window, (DataGrid)window.FindName("gridPlan"), (DataGrid)window.FindName("gridResult"));
            }
            finally { window.Close(); }
        });
    }

    private static ChangePlanViewModel ModelOf(Window window) => (ChangePlanViewModel)window.DataContext;

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void BothGridsShowAtLeastThreeWholeRowsAtTheDefaultSize(ApplicationTheme theme)
    {
        WithWindow(theme, (window, plan, result) =>
        {
            var rows = new MinimumSizeRows();
            rows.Add("gridPlan", plan);
            Run(ModelOf(window).ConfirmCommand.ExecuteAsync(null));
            Settle(window);
            rows.Add("gridResult", result);
            rows.AssertEnough($"Change plan {theme}");
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheResultColumnFollowsTheOutcomeAndReadsOnTheRowFillSelectedOrNot(ApplicationTheme theme)
    {
        WithWindow(theme, (window, plan, result) =>
        {
            Run(ModelOf(window).ConfirmCommand.ExecuteAsync(null));
            Settle(window);
            var seen = StatusColumnKit.Assert(window, result, $"gridResult-{theme}", "Result");
            Assert.Contains(OutcomeKind.Success, seen);
            Assert.Contains(OutcomeKind.Critical, seen);
            Assert.Contains(OutcomeKind.Caution, seen);
        });
    }
}
