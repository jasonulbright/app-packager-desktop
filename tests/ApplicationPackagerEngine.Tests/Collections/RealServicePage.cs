using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests.Collections;

/// <summary>The page over the real service and the real scripts on a stub site, with the harness's prompts and scripted plan dialogs.</summary>
internal sealed record RealServicePage(CollectionComplianceViewModel ViewModel, CollectionComplianceHarness Harness, StubSitePowerShell Site)
{
    public static async Task<RealServicePage> OpenAsync(StubSitePowerShell site, string? selectId = null)
    {
        var harness = new CollectionComplianceHarness();
        var service = new CollectionService(site, null, () => "t1");
        var viewModel = new CollectionComplianceViewModel(new CollectionCompliancePageServices(
            service, harness.Store, harness.Reports, harness.Prompts, harness.Plans, new CollectionWriter(service, harness.History, harness.Log, () => "LAB"),
            () => harness.Settings, () => null, _ => { }, harness.Log));
        await viewModel.OnNavigatedToAsync();
        if (selectId is not null)
        {
            viewModel.SelectedRow = viewModel.Rows.Single(r => r.CollectionId == selectId);
            viewModel.SetSelectedRows([viewModel.SelectedRow]);
            await viewModel.DetailLoad;
        }
        return new RealServicePage(viewModel, harness, site);
    }
}
