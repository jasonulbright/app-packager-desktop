using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The supersedence setting of an application is one default and one optional override for each application. They are edited in Settings,
/// Supersedence (the grid and its bulk actions); the App Packager page has no button for them.
/// </summary>
public partial class PackagesViewModel
{
    /// <summary>The identifier that keys the supersedence setting of a row; null for a row without a definition (a PS1 packager).</summary>
    public string? SupersedenceIdOf(PackagerInfo p) => _executionService.SupersedenceApplicationId(p);
}
