using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>Where the header check box of the Packages grid stands in its cycle.</summary>
public enum SelectionCycleState
{
    /// <summary>Nothing was bulk selected (header unchecked).</summary>
    None,
    /// <summary>Every visible row was selected (header checked).</summary>
    All,
    /// <summary>Only the visible rows with an update were selected (header indeterminate).</summary>
    UpdatesOnly,
}

/// <summary>
/// The bulk selection of the Packages grid: the header cycles none, every visible row, the visible rows with an update, none. A bulk step
/// changes the checks of the visible (filtered) rows only, so a checked row that the filter hides stays checked; the selection line counts
/// it, and Clear selection unchecks it. Row clicks, sorting and filtering do not change the state or write a log line.
/// </summary>
public sealed class SelectionCycle
{
    public const string SelectedAllLine = "Selected all visible rows.";
    public const string SelectedUpdatesLine = "Selected rows with 'Update available' status.";
    public const string ClearedLine = "Deselected the visible rows.";

    public SelectionCycleState State { get; private set; }

    /// <summary>The header check box value: checked, indeterminate (updates only) or unchecked.</summary>
    public bool? HeaderCheck => State switch
    {
        SelectionCycleState.All => true,
        SelectionCycleState.UpdatesOnly => null,
        _ => false,
    };

    /// <summary>One click on the header check box.</summary>
    public void Advance(IReadOnlyCollection<PackagerInfo> visible, Action<string> log)
    {
        switch (State)
        {
            case SelectionCycleState.None: SelectAllVisible(visible, log); break;
            case SelectionCycleState.All: SelectUpdates(visible, log); break;
            default: Clear(visible, log); break;
        }
    }

    public void SelectAllVisible(IEnumerable<PackagerInfo> visible, Action<string> log)
    {
        foreach (var p in visible) p.IsSelected = p.IsRunnable;
        State = SelectionCycleState.All;
        log(SelectedAllLine);
    }

    public void SelectUpdates(IEnumerable<PackagerInfo> visible, Action<string> log)
    {
        foreach (var p in visible) p.IsSelected = p.IsRunnable && CveRowState.IsUpdateAvailable(p.Status);
        State = SelectionCycleState.UpdatesOnly;
        log(SelectedUpdatesLine);
    }

    public void Clear(IEnumerable<PackagerInfo> visible, Action<string> log)
    {
        foreach (var p in visible) p.IsSelected = false;
        State = SelectionCycleState.None;
        log(ClearedLine);
    }
}

/// <summary>The checks over a reload of the list: each row keeps its check by its Uid, and a checked row that is gone is named.</summary>
public static class SelectionKeeper
{
    /// <summary>Checks the rows of <paramref name="after"/> that were checked before. Returns the names of the checked rows that are gone.</summary>
    public static IReadOnlyList<string> Restore(IEnumerable<PackagerInfo> before, IReadOnlyList<PackagerInfo> after)
    {
        var checkedRows = before.Where(p => p.IsSelected && p.Uid.Length > 0).ToList();
        var byUid = after.Where(p => p.Uid.Length > 0).GroupBy(p => p.Uid, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var gone = new List<string>();
        foreach (var old in checkedRows)
        {
            if (byUid.TryGetValue(old.Uid, out var now)) now.IsSelected = now.IsRunnable;
            else gone.Add(old.Application);
        }
        return gone;
    }

    public static string GoneLine(IReadOnlyList<string> gone) =>
        $"{gone.Count} checked {(gone.Count == 1 ? "application is" : "applications are")} no longer in the grid, so "
        + $"{(gone.Count == 1 ? "its check was" : "their checks were")} dropped: {string.Join(", ", gone)}.";
}
