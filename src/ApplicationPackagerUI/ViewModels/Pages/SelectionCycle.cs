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
/// The bulk selection of the Packages grid, as the PowerShell app does it (its column header click): the header
/// cycles none, every visible row, the visible rows with an update, none. A bulk step clears every row first, then
/// selects within the visible (filtered) set only, so a row the filter hides never keeps or gains a selection.
/// Row clicks, sorting and filtering do not change the state or write a log line.
/// </summary>
public sealed class SelectionCycle
{
    public const string SelectedAllLine = "Selected all visible rows.";
    public const string SelectedUpdatesLine = "Selected rows with 'Update available' status.";
    public const string ClearedLine = "Deselected all rows.";

    public SelectionCycleState State { get; private set; }

    /// <summary>The header check box value: checked, indeterminate (updates only) or unchecked.</summary>
    public bool? HeaderCheck => State switch
    {
        SelectionCycleState.All => true,
        SelectionCycleState.UpdatesOnly => null,
        _ => false,
    };

    /// <summary>One click on the header check box.</summary>
    public void Advance(IEnumerable<PackagerInfo> all, IEnumerable<PackagerInfo> visible, Action<string> log)
    {
        switch (State)
        {
            case SelectionCycleState.None: SelectAllVisible(all, visible, log); break;
            case SelectionCycleState.All: SelectUpdates(all, visible, log); break;
            default: Clear(all, log); break;
        }
    }

    public void SelectAllVisible(IEnumerable<PackagerInfo> all, IEnumerable<PackagerInfo> visible, Action<string> log)
    {
        foreach (var p in all) p.IsSelected = false;
        foreach (var p in visible) p.IsSelected = p.IsRunnable;
        State = SelectionCycleState.All;
        log(SelectedAllLine);
    }

    public void SelectUpdates(IEnumerable<PackagerInfo> all, IEnumerable<PackagerInfo> visible, Action<string> log)
    {
        foreach (var p in all) p.IsSelected = false;
        foreach (var p in visible) p.IsSelected = p.IsRunnable && CveRowState.IsUpdateAvailable(p.Status);
        State = SelectionCycleState.UpdatesOnly;
        log(SelectedUpdatesLine);
    }

    public void Clear(IEnumerable<PackagerInfo> all, Action<string> log)
    {
        foreach (var p in all) p.IsSelected = false;
        State = SelectionCycleState.None;
        log(ClearedLine);
    }
}
