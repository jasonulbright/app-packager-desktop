using System.Windows.Media.Imaging;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using ApplicationPackagerEngine.Models;

namespace MockProof;

/// <summary>The first start of the mock: the checklist beside the catalog, with one of three steps done and the site not verified.</summary>
internal sealed partial class Scene
{
    [Screen("onboarding")]
    private async Task<BitmapSource> Onboarding()
    {
        ShowPackages();
        await SettleAsync();
        var page = ApplicationPackagerUI.App.Services.GetRequiredService<PackagesPage>();
        var rows = MockData.Rows(everyCpe: true).Take(16).ToList();
        foreach (var row in rows)
        {
            row.CurrentVersion = "";
            row.LatestVersion = "";
            row.Status = "Ready";
            row.CveCount = "";
        }
        _packages.Packagers = new System.Collections.ObjectModel.ObservableCollection<ApplicationPackagerUI.Models.PackagerInfo>(rows);
        Invoke(_packages, "WatchRows");
        Invoke(_packages, "ApplyFilter", true, true);
        _packages.MecmConnectionStatus = "Unknown";
        var checklist = _packages.Checklist!;
        checklist.Show();
        (string Title, bool Done, bool Next)[] steps = [("systems", true, false), ("site", false, true), ("folders", false, false)];
        string[] details =
        [
            "ConfigMgr is enabled. Intune and WSUS are off.",
            "Site MCM is configured. Verify the connection before checking versions.",
            "Choose where APD stages installers and writes package content.",
        ];
        checklist.ShownSteps.Clear();
        for (var i = 0; i < 3; i++)
        {
            checklist.ShownSteps.Add(checklist.Steps[i]);
            checklist.Steps[i].Detail = details[i];
            checklist.Steps[i].Place(steps[i].Done, false, steps[i].Next);
        }
        checklist.ProgressText = "1 of 3 complete";
        Invoke(page, "ApplyPane");
        await SettleAsync();
        return Capture(_main);
    }
}
