namespace ApplicationPackagerUI.Helpers;

/// <summary>One column of the saved layout of the Packages grid: its header, its width in pixels when the operator resized it, else 0.</summary>
public sealed class PackagesColumnState
{
    public string Id { get; set; } = "";
    public double Width { get; set; }
}

/// <summary>
/// The saved layout of the Packages grid (overhaul V1, phase 3c): the order of the columns that may move, the widths that the operator set, and the
/// sort. The hidden columns keep their own list (AppSettings.PackagesHiddenColumns).
/// </summary>
public sealed class PackagesColumnLayout
{
    /// <summary>The movable columns in display order.</summary>
    public List<PackagesColumnState> Columns { get; set; } = [];

    /// <summary>The header of the sorted column; empty when the grid is not sorted.</summary>
    public string SortId { get; set; } = "";

    public bool SortDescending { get; set; }

    // A settings file can hold null for the list or for an entry, so every reader goes through this.
    private static IEnumerable<PackagesColumnState> Entries(PackagesColumnLayout? layout) =>
        (layout?.Columns ?? []).Where(c => c?.Id is not null);

    /// <summary>
    /// The display order for the columns that the grid has: the saved ones in the saved order, then each column the saved layout does not name, at the
    /// place it has in the grid's own order. An id that the grid does not have, or that is there twice, is dropped.
    /// </summary>
    public static IReadOnlyList<string> Order(IReadOnlyList<string> defaults, PackagesColumnLayout? saved)
    {
        var known = new HashSet<string>(defaults, StringComparer.Ordinal);
        var order = Entries(saved).Select(c => c.Id).Where(known.Contains).Distinct(StringComparer.Ordinal).ToList();
        for (var i = 0; i < defaults.Count; i++)
        {
            if (order.Contains(defaults[i])) continue;
            // The new column goes after the nearest default neighbor on its left that the order already has.
            var at = 0;
            for (var left = i - 1; left >= 0; left--)
            {
                var index = order.IndexOf(defaults[left]);
                if (index >= 0) { at = index + 1; break; }
            }
            order.Insert(at, defaults[i]);
        }
        return order;
    }

    /// <summary>The saved width of a column, or null when it has none or the value is not a usable width.</summary>
    public static double? WidthOf(PackagesColumnLayout? saved, string id)
    {
        var width = Entries(saved).FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal))?.Width ?? 0;
        return double.IsFinite(width) && width is > 0 and <= 4000 ? width : null;
    }
}
