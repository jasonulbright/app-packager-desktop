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

    /// <summary>The header that "ConfigMgr version" had before; a saved file can still name it.</summary>
    private const string FormerConfigMgrVersion = "Current (ConfigMgr)";

    public static readonly IReadOnlyList<string> Debug = ["CMName", "Script", "Max EPSS", "NVD updated", "CVEs Checked (UTC)"];

    /// <summary>The entries of the Columns menu, in menu order.</summary>
    public static readonly IReadOnlyList<string> Toggleable =
    [
        ConfigMgrVersion, "Latest", "Status", "CVE check",
        "Vendor", "Source", "Intune", "CMName", "Script", "Max EPSS", "NVD updated", "CVEs Checked (UTC)",
        "CVEs", "Max CVSS", "KEV", "In latest", "Newest CVE"
    ];

    /// <summary>The columns of a new install, and of the Compact view entry.</summary>
    public static readonly IReadOnlyList<string> Compact = [ConfigMgrVersion, "Latest", "Status", "CVE check"];

    /// <summary>The columns that the grid showed before the compact set existed.</summary>
    public static readonly IReadOnlyList<string> Legacy =
        [ConfigMgrVersion, "Latest", "Status", "Vendor", "Source", "Intune", "CVEs", "Max CVSS", "KEV", "In latest", "Newest CVE"];

    public static string Rename(string header) => string.Equals(header, FormerConfigMgrVersion, StringComparison.Ordinal) ? ConfigMgrVersion : header;

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

    /// <summary>
    /// Stores the choice of columns once. A new install with no saved choice gets the compact set. Every other install keeps what it
    /// showed: the set from before, less the columns that the operator had hidden. True when the settings changed.
    /// </summary>
    public static bool Migrate(AppSettings settings, bool newInstall)
    {
        var renamed = RenameLayout(settings.PackagesGridLayout);
        if (settings.PackagesShownColumns is not null) return renamed;
        var hidden = (settings.PackagesHiddenColumns ?? []).Select(Rename).ToHashSet(StringComparer.Ordinal);
        settings.PackagesShownColumns = newInstall && hidden.Count == 0 && settings.PackagesGridLayout is null
            ? Compact.ToArray()
            : Legacy.Where(h => !hidden.Contains(h)).ToArray();
        settings.PackagesHiddenColumns = [];
        return true;
    }

    private static bool RenameLayout(PackagesColumnLayout? layout)
    {
        if (layout is null) return false;
        var changed = false;
        foreach (var column in (layout.Columns ?? []).Where(c => c?.Id is not null && Rename(c.Id) != c.Id))
        {
            column.Id = Rename(column.Id);
            changed = true;
        }
        if (Rename(layout.SortId ?? "") != (layout.SortId ?? ""))
        {
            layout.SortId = Rename(layout.SortId!);
            changed = true;
        }
        return changed;
    }
}
