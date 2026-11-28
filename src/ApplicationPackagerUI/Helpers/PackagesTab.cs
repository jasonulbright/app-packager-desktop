using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.Helpers;

/// <summary>The status tabs of the Packages grid. Other is a row that counts under All only.</summary>
public enum PackagesTabKind
{
    Other,
    Update,
    NotFound,
    NotChecked,
    UpToDate
}

/// <summary>The tab the grid shows: every row, one status tab, or the checked rows (the "Show selected" link of the selection line).</summary>
public enum PackagesTabChoice
{
    All,
    Update,
    NotFound,
    NotChecked,
    Selected,
    UpToDate
}

/// <summary>The row counts of the status tabs.</summary>
public sealed record PackagesTabCounts(int All, int Update, int NotFound, int NotChecked)
{
    public static readonly PackagesTabCounts Empty = new(0, 0, 0, 0);
}

/// <summary>
/// Which status tab a row belongs to. Update is "Update available" with or without a CVE count;
/// NotFound is the status Check ConfigMgr writes; NotChecked is a row with no ConfigMgr version
/// whose status says nothing was compared yet. Every other status (running, staged, errors) is
/// on the All tab only.
/// </summary>
public static class PackagesTab
{
    public const string NotFoundStatus = "Absent from ConfigMgr";

    public const string UpToDateStatus = "Up to date";

    /// <summary>True for a status that says nothing was compared with the site yet.</summary>
    public static bool IsUncheckedStatus(string? status) => UncheckedStatuses.Contains(status ?? "");

    /// <summary>The statuses of a row that no check has compared yet.</summary>
    private static readonly HashSet<string> UncheckedStatuses = new(StringComparer.Ordinal)
    {
        "", "Ready", "Cached", "Latest retrieved"
    };

    public static PackagesTabKind Of(string? status, string? currentVersion)
    {
        if (CveRowState.IsUpdateAvailable(status)) return PackagesTabKind.Update;
        if (status == NotFoundStatus) return PackagesTabKind.NotFound;
        if (status == UpToDateStatus) return PackagesTabKind.UpToDate;
        if (string.IsNullOrEmpty(currentVersion) && UncheckedStatuses.Contains(status ?? "")) return PackagesTabKind.NotChecked;
        return PackagesTabKind.Other;
    }

    public static PackagesTabKind Of(PackagerInfo row) => Of(row.Status, row.CurrentVersion);

    /// <summary>True when the row is on the tab; every row is on All.</summary>
    public static bool Contains(PackagesTabKind? tab, PackagerInfo row) => tab is null || Of(row) == tab;

    /// <summary>True when the row is on the chosen tab. Every row is on All; Selected holds the checked rows.</summary>
    public static bool Matches(PackagesTabChoice choice, PackagerInfo row) => choice switch
    {
        PackagesTabChoice.All => true,
        PackagesTabChoice.Update => Of(row) == PackagesTabKind.Update,
        PackagesTabChoice.NotFound => Of(row) == PackagesTabKind.NotFound,
        PackagesTabChoice.NotChecked => Of(row) == PackagesTabKind.NotChecked,
        PackagesTabChoice.UpToDate => Of(row) == PackagesTabKind.UpToDate,
        _ => row.IsSelected
    };

    public static PackagesTabCounts Count(IEnumerable<PackagerInfo> rows)
    {
        int all = 0, update = 0, notFound = 0, notChecked = 0;
        foreach (var row in rows)
        {
            all++;
            switch (Of(row))
            {
                case PackagesTabKind.Update: update++; break;
                case PackagesTabKind.NotFound: notFound++; break;
                case PackagesTabKind.NotChecked: notChecked++; break;
            }
        }
        return new PackagesTabCounts(all, update, notFound, notChecked);
    }
}

/// <summary>How many rows are checked, and how many of those the current view hides.</summary>
public static class SelectionCounts
{
    public static (int Selected, int Hidden) Of(IEnumerable<PackagerInfo> all, IEnumerable<PackagerInfo> visible)
    {
        var shown = new HashSet<PackagerInfo>(visible);
        int selected = 0, hidden = 0;
        foreach (var row in all)
        {
            if (!row.IsSelected) continue;
            selected++;
            if (!shown.Contains(row)) hidden++;
        }
        return (selected, hidden);
    }
}

/// <summary>A two-way converter for a group of RadioButtons over one enum property: the button is checked when the property equals its parameter.</summary>
public sealed class EnumEqualsConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is true && parameter is not null ? Enum.Parse(targetType, parameter.ToString()!) : System.Windows.Data.Binding.DoNothing;
}
