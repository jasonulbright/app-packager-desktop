using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// The Discover unmanaged software lists in the settings. Every list holds definition ids,
/// the key of <see cref="AppSettings.HiddenApplications"/>, compared without regard to case.
/// </summary>
public static class DiscoveryPreferences
{
    /// <summary>
    /// Trims the overrides, drops blank and repeated ids, and keeps the two lists disjoint:
    /// an id in both stays dismissed.
    /// </summary>
    public static void Normalize(AppSettings settings)
    {
        settings.HiddenApplications ??= [];
        settings.DiscoveryDismissed = Distinct(settings.DiscoveryDismissed);
        var dismissed = Set(settings.DiscoveryDismissed);
        settings.DiscoveryEnabled = Distinct(settings.DiscoveryEnabled).Where(id => !dismissed.Contains(id)).ToArray();
        settings.DiscoverySqlServer = (settings.DiscoverySqlServer ?? "").Trim();
        settings.DiscoverySqlDatabase = (settings.DiscoverySqlDatabase ?? "").Trim();
    }

    /// <summary>
    /// Same effect as checking the applications on the Applications page: the ids leave the
    /// hidden list and the dismissed list, and join the enabled list. Returns how many ids left
    /// the hidden list. The caller saves and reloads the grid.
    /// </summary>
    public static int Enable(AppSettings settings, IEnumerable<string> ids)
    {
        var enable = Set(ids);
        var hidden = settings.HiddenApplications ?? [];
        var removed = hidden.Where(enable.Contains).ToList();
        settings.HiddenApplications = hidden.Where(id => !enable.Contains(id)).ToArray();
        settings.DiscoveryDismissed = Distinct(settings.DiscoveryDismissed).Where(id => !enable.Contains(id)).ToArray();
        settings.DiscoveryEnabled = Distinct((settings.DiscoveryEnabled ?? []).Concat(removed));
        return removed.Count;
    }

    /// <summary>Adds the ids to the dismissed list once each and removes them from the enabled list. Returns how many were added.</summary>
    public static int Dismiss(AppSettings settings, IEnumerable<string> ids)
    {
        var list = Distinct(settings.DiscoveryDismissed).ToList();
        var seen = Set(list);
        var added = 0;
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id.Trim())) continue;
            list.Add(id.Trim());
            added++;
        }
        settings.DiscoveryDismissed = list.ToArray();
        settings.DiscoveryEnabled = Distinct(settings.DiscoveryEnabled).Where(id => !seen.Contains(id)).ToArray();
        return added;
    }

    /// <summary>
    /// A hidden list imported on first run, without the ids Discover enabled: an import must not
    /// hide an application again after the operator made it visible from a discovery.
    /// </summary>
    public static string[] WithoutEnabled(IEnumerable<string> hidden, AppSettings settings)
    {
        var enabled = Set(settings.DiscoveryEnabled ?? []);
        return hidden.Where(id => !enabled.Contains(id)).ToArray();
    }

    private static string[] Distinct(IEnumerable<string>? ids)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return (ids ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Where(seen.Add)
            .ToArray();
    }

    private static HashSet<string> Set(IEnumerable<string> ids) =>
        new(ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()), StringComparer.OrdinalIgnoreCase);
}
