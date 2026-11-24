using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// The search of the Packages grid. The PowerShell app (App Packager) searches the Application, Vendor, Status and
/// CMName columns, case-insensitive, for the text anywhere in the value; the grid of this app searches the same four
/// columns the same way, so a search gives the same rows in both apps.
/// </summary>
public static class PackagerSearch
{
    public static bool Matches(PackagerInfo packager, string term) =>
        Contains(packager.Application, term) || Contains(packager.Vendor, term) || Contains(packager.Status, term) || Contains(packager.CMName, term);

    private static bool Contains(string? value, string term) => (value ?? "").Contains(term, StringComparison.OrdinalIgnoreCase);
}
