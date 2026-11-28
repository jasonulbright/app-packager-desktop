using System.Windows.Media.Imaging;

namespace MockProof;

internal sealed partial class Scene
{
    private void ShowPackages()
    {
        _main.GetNavigation().Navigate(typeof(ApplicationPackagerUI.Views.Pages.PackagesPage));
        _packages.MecmConnectionStatus = "Testing...";
        _packages.MecmConnectionStatus = "Connected";
    }

    [Screen("refine")]
    private async Task<BitmapSource> Refine()
    {
        ShowPackages();
        await SettleAsync();
        return Capture(_main);
    }
}
