namespace ApplicationPackagerUI.Helpers;

/// <summary>
/// The columns of the Packages grid that the Columns menu lets the operator hide (overhaul V1, phase 3). The select box, the icon, the Application and the Status
/// are always shown. Source and Intune show by their own data, and the debug columns by their own check box; none of those is in the menu.
/// </summary>
public static class PackagesColumns
{
    public static readonly IReadOnlyList<string> Toggleable =
        ["Vendor", "Current (ConfigMgr)", "Latest", "CVEs", "Max CVSS", "KEV", "In latest", "Newest CVE"];

    /// <summary>The hidden headers of a saved list: only a header the menu offers counts, so an unknown or removed column in a saved file is dropped.</summary>
    public static IReadOnlySet<string> Clean(IEnumerable<string>? saved) =>
        new HashSet<string>((saved ?? []).Where(h => Toggleable.Contains(h, StringComparer.Ordinal)), StringComparer.Ordinal);

    /// <summary>The saved list after one column was shown or hidden, in menu order.</summary>
    public static string[] With(IEnumerable<string>? saved, string header, bool visible)
    {
        var hidden = new HashSet<string>(Clean(saved), StringComparer.Ordinal);
        if (!Toggleable.Contains(header, StringComparer.Ordinal)) return Toggleable.Where(hidden.Contains).ToArray();
        if (visible) hidden.Remove(header); else hidden.Add(header);
        return Toggleable.Where(hidden.Contains).ToArray();
    }
}
