using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The Export Check Results button of the Packages page, in a real window in both themes.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PackagesExportCheckResultsButtonTests
{
    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheButtonIsShownBelowTheGridWithATooltip(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            System.Windows.Application.Current.Resources["BooleanToVisibilityConverter"] = new System.Windows.Controls.BooleanToVisibilityConverter();
            var viewModel = (PackagesViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
            var page = new PackagesPage(viewModel);
            var window = new Window { Width = 1440 - 48, Height = 900 - 48, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Settle(window);
                var button = Descendants(page).OfType<FrameworkElement>()
                    .Single(e => AutomationProperties.GetAutomationId(e) == "btnExportCheckResults");
                Assert.True(button.IsVisible);
                Assert.True(button.ActualWidth > 40 && button.ActualHeight > 16);
                var tip = Convert.ToString(button.ToolTip);
                Assert.Contains("Check ConfigMgr", tip);
                Assert.Contains("only those with an update available", tip);
                var grid = (FrameworkElement)page.FindName("PackagerGrid");
                Assert.True(button.TranslatePoint(new System.Windows.Point(0, 0), page).Y > grid.TranslatePoint(new System.Windows.Point(0, 0), page).Y + grid.ActualHeight - 1,
                    "The button is not below the grid.");
            }
            finally { window.Close(); }
        });
    }
}
