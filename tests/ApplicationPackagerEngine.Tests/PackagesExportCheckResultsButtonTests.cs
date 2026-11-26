using System.Runtime.CompilerServices;
using System.Windows.Automation;
using System.Windows.Controls;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The menus of the Packages page (overhaul V1, phase 3c), built on a real page in both themes.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PackagesExportCheckResultsButtonTests
{
    private static List<string> Ids(ContextMenu menu) => menu.Items.OfType<MenuItem>().Select(AutomationProperties.GetAutomationId).ToList();

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheMenusHoldTheCommandsThatLeftTheToolbarWithTheirTooltips(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            System.Windows.Application.Current.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
            var viewModel = (PackagesViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
            viewModel.CheckMecmToolTip = "Set the site code in Settings to check ConfigMgr.";
            var page = new PackagesPage(viewModel);

            Assert.Equal(["miCheckLatest", "miCheckConfigMgr", "miCheckCves"], Ids(page.BuildCheckMenu()));
            var site = page.BuildCheckMenu().Items.OfType<MenuItem>().ElementAt(1);
            Assert.Equal(viewModel.CheckMecmToolTip, site.ToolTip);
            Assert.True(ToolTipService.GetShowOnDisabled(site), "A disabled check keeps its reason.");

            var more = page.BuildMoreMenu();
            Assert.Equal(["miCveReport", "miOperationHistory", "miExportCheckResults", "miSetupChecklist", "miSetup", "miShowDebugColumns"], Ids(more));
            var items = more.Items.OfType<MenuItem>().ToList();
            Assert.False(items[1].IsEnabled, "Operation history needs a selected row.");
            var export = Convert.ToString(items[2].ToolTip);
            Assert.Contains("Check ConfigMgr", export);
            Assert.Contains("only those with an update available", export);

            Assert.Equal(["miAddFromWeb", "miAddFromFile", "miConvertFromCm", "miImportFromUrl"], Ids(page.BuildAddOrImportMenu()));
        });
    }
}
