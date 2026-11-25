namespace ApplicationPackagerEngine.Models;

/// <summary>
/// The one wording of the progress of an action that works through a counted set (Q-127): the status line while it runs
/// ("Check ConfigMgr: 3 of 328 — 7-Zip"), and the line of a cancelled run ("Check ConfigMgr cancelled: 3 of 328 products checked.").
/// The line of a finished run is <see cref="PackagingRunTally"/>; it counts the same set the same way.
/// </summary>
public static class RunProgress
{
    private const string Dash = " — ";

    /// <summary>The line while the action works on item <paramref name="current"/> of <paramref name="total"/>; the name of the item follows when there is one.</summary>
    public static string Running(string action, int current, int total, string? item = null) =>
        string.IsNullOrWhiteSpace(item) ? $"{action}: {current} of {total}" : $"{action}: {current} of {total}{Dash}{item}";

    /// <summary>The line of an action that has started and has not reached its first item: "Check ConfigMgr: 0 of 328".</summary>
    public static string Starting(string action, int total) => Running(action, 0, total);

    /// <summary>The line of a cancelled action: "Name cancelled: N of M nouns verb."</summary>
    public static string Cancelled(string action, int done, int total, string noun, string verb) =>
        $"{action} cancelled: {done} of {total} {(total == 1 ? noun : noun + "s")} {verb}.";
}
