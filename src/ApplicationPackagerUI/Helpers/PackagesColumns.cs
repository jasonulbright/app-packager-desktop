using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Helpers;

/// <summary>
/// The columns of the Packages grid that the Columns menu offers (overhaul V1, section 2.1). The select box, the icon and the Application
/// column are always shown. A new install shows the compact set. An install from before the compact set keeps the columns it showed.
/// Source and Intune show only while they have data and the operator chose them. The five debug columns also show while Show debug
/// columns is on.
/// </summary>
public static class PackagesColumns
{
    public const string ConfigMgrVersion = "ConfigMgr version";

    /// <summary>
    /// The header that "ConfigMgr version" had before. The hidden list and the saved layout keep this name, so an earlier build that
    /// reads the same file keeps the choices of the operator.
    /// </summary>
    private const string FormerConfigMgrVersion = "Current (ConfigMgr)";

    public const string LatestVersion = "Latest version";
    public const string CvesChecked = "CVEs checked (UTC)";

    // The headers that two more columns had before the labels of the mock. The saved lists and layouts keep the former names.
    private const string FormerLatestVersion = "Latest";
    private const string FormerCvesChecked = "CVEs Checked (UTC)";

    /// <summary>The columns that an earlier build can hide, by the names it knows.</summary>
    private static readonly IReadOnlyList<string> FormerToggleable =
        ["Vendor", FormerConfigMgrVersion, "Latest", "CVEs", "Max CVSS", "KEV", "In latest", "Newest CVE"];

    public static readonly IReadOnlyList<string> Debug = ["CMName", "Script", "Max EPSS", "NVD updated", CvesChecked];

    /// <summary>The entries of the Columns menu, in menu order.</summary>
    public static readonly IReadOnlyList<string> Toggleable =
    [
        "Vendor", "Source", ConfigMgrVersion, LatestVersion, "Status", "CVE check",
        "CVEs", "Max CVSS", "KEV", "In latest", "Newest CVE", "Max EPSS", "NVD updated", CvesChecked,
        "Intune", "CMName", "Script"
    ];

    /// <summary>The columns of the mock: of a new install, and of the Compact view entry.</summary>
    public static readonly IReadOnlyList<string> Compact = ["Vendor", ConfigMgrVersion, LatestVersion, "Status", "CVE check"];

    /// <summary>The columns that the grid showed before the compact set existed.</summary>
    public static readonly IReadOnlyList<string> Legacy =
        ["Vendor", "Source", ConfigMgrVersion, LatestVersion, "Status", "CVEs", "Max CVSS", "KEV", "In latest", "Newest CVE", "Intune"];

    public static string Rename(string header) => header switch
    {
        FormerConfigMgrVersion => ConfigMgrVersion,
        FormerLatestVersion => LatestVersion,
        FormerCvesChecked => CvesChecked,
        _ => header
    };

    private static string FormerName(string header) => header switch
    {
        ConfigMgrVersion => FormerConfigMgrVersion,
        LatestVersion => FormerLatestVersion,
        CvesChecked => FormerCvesChecked,
        _ => header
    };

    /// <summary>The chosen columns of a saved list, in menu order; an unknown column is dropped. No saved list is the set from before.</summary>
    public static IReadOnlyList<string> Shown(IEnumerable<string>? saved)
    {
        if (saved is null) return Legacy;
        var chosen = new HashSet<string>(saved.Select(Rename), StringComparer.Ordinal);
        return Toggleable.Where(chosen.Contains).ToList();
    }

    /// <summary>The saved list after one column was shown or hidden.</summary>
    public static string[] With(IEnumerable<string>? saved, string header, bool visible)
    {
        var chosen = new HashSet<string>(Shown(saved), StringComparer.Ordinal);
        if (Toggleable.Contains(header, StringComparer.Ordinal))
        {
            if (visible) chosen.Add(header);
            else chosen.Remove(header);
        }
        return Toggleable.Where(chosen.Contains).ToArray();
    }

    /// <summary>Saves the chosen columns, and the hidden list that an earlier build reads.</summary>
    public static void Store(AppSettings settings, IReadOnlyList<string> shown)
    {
        settings.PackagesShownColumns = shown.ToArray();
        settings.PackagesHiddenColumns = FormerToggleable.Where(h => !shown.Contains(Rename(h))).ToArray();
    }

    /// <summary>The saved layout with the names of this build.</summary>
    public static PackagesColumnLayout? ReadLayout(PackagesColumnLayout? stored) => WithIds(stored, Rename);

    /// <summary>The layout as it is saved, with the names that an earlier build reads.</summary>
    public static PackagesColumnLayout? StoredLayout(PackagesColumnLayout? layout) =>
        WithIds(layout, FormerName);

    private static PackagesColumnLayout? WithIds(PackagesColumnLayout? layout, Func<string, string> name) => layout is null ? null : new()
    {
        Columns = (layout.Columns ?? []).Where(c => c?.Id is not null)
            .Select(c => new PackagesColumnState { Id = name(c.Id), Width = c.Width }).ToList(),
        SortId = name(layout.SortId ?? ""),
        SortDescending = layout.SortDescending
    };

    /// <summary>
    /// Stores the choice of columns once. The first start of the interface of the mock shows the columns of the mock; the other columns stay
    /// in the Columns menu. True when the settings changed.
    /// </summary>
    public static bool Migrate(AppSettings settings, bool newInstall)
    {
        if (settings.PackagesShownColumns is not null) return false;
        _ = newInstall;
        Store(settings, Compact);
        return true;
    }
}
