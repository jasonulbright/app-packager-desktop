using System.Windows.Media.Imaging;
using ApplicationPackagerUI.Views.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace MockProof;

/// <summary>The Workbench of the mock: 7-Zip with its packager default profile, on the Application section.</summary>
internal sealed partial class Scene
{
    [Screen("workbench")]
    private async Task<BitmapSource> Workbench()
    {
        var window = ApplicationPackagerUI.App.Services.GetRequiredService<WorkbenchWindow>();
        return await OverMain(window, async () =>
        {
            var model = (ApplicationPackagerUI.ViewModels.Windows.WorkbenchWindowViewModel)window.DataContext;
            await Until(() => model.Applications.Count > 0 && model.SelectedApplication is not null, 90, "the Workbench applications");
            var sevenZip = model.Applications.FirstOrDefault(a => a.DisplayName.Equals("7-Zip (x64)", StringComparison.OrdinalIgnoreCase))
                ?? model.Applications.First(a => a.DisplayName.StartsWith("7-Zip", StringComparison.OrdinalIgnoreCase));
            // The catalog of the app names this application "7-Zip" and has an icon for it; the mock draws other words and no icon.
            typeof(ApplicationPackagerUI.Services.WorkbenchApplicationItem).GetProperty("DisplayName")!.SetValue(sevenZip, "7-Zip (x64)");
            model.SelectedApplication = null;
            model.SelectedApplication = sevenZip;
            await Task.Delay(3000);
            model.DisplayNameSource = "Inherited: 7-Zip (x64)";
            model.IconPreviewPath = "";
        });
    }
}
