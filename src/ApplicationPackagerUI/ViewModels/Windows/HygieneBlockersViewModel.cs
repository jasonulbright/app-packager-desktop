using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>One object that refers to the application: its kind, its name and what it does.</summary>
public sealed record HygieneBlockerRow(string Kind, string Name, string Detail)
{
    public HygieneBlockerRow(HygieneBlocker blocker) : this(blocker.Kind, blocker.Name, blocker.Detail) { }
}

/// <summary>
/// What blocks the delete of one application. The list is made from the data the last scan read: no query is made when the window opens. A
/// task sequence or a collection that the account may not see is not in that data, and the window says so.
/// </summary>
public sealed partial class HygieneBlockersViewModel : ObservableObject
{
    public const string NotRead = "The last scan did not read this data, or its result was loaded from the store. Select Scan, then show the blockers again.";
    public const string NoBlocker = "Nothing in the data of the last scan refers to this application: no deployment, no task sequence, no stale reference row, no dependency and no supersedence rule.";
    public const string Limits = "The list shows what the account sees. A task sequence or a collection outside the security scope of the account is not in it, and the site still counts it when it deletes.";

    /// <param name="applicationName">The name shown in the title.</param>
    /// <param name="modelName">The ModelName of the application.</param>
    /// <param name="blockers">The blockers; null when the scan did not read the data.</param>
    /// <param name="note">What the scan could not read.</param>
    public HygieneBlockersViewModel(string applicationName, string modelName, IReadOnlyList<HygieneBlocker>? blockers, string note)
    {
        ApplicationName = applicationName;
        ModelName = modelName;
        Title = $"What blocks the delete of {applicationName}";
        Rows = (blockers ?? []).Select(b => new HygieneBlockerRow(b)).ToList();
        IsRead = blockers is not null;
        Summary = blockers is null ? NotRead
            : blockers.Count == 0 ? NoBlocker
            : $"{SiteHygieneLabels.Count(blockers.Count, "object")} {(blockers.Count == 1 ? "refers" : "refer")} to the application and {(blockers.Count == 1 ? "blocks" : "block")} its delete. {KindCounts(blockers)}";
        Note = blockers is null ? "" : (note.Length > 0 ? note + " " : "") + Limits;
    }

    public string ApplicationName { get; }

    public string ModelName { get; }

    public string Title { get; }

    public string Summary { get; }

    public string Note { get; }

    public bool HasNote => Note.Length > 0;

    public bool IsRead { get; }

    public IReadOnlyList<HygieneBlockerRow> Rows { get; }

    public bool HasRows => Rows.Count > 0;

    public event EventHandler? CloseRequested;

    private static string KindCounts(IReadOnlyList<HygieneBlocker> blockers) =>
        string.Join(" ", blockers.GroupBy(b => b.Kind, StringComparer.Ordinal).Select(g => $"{g.Key}: {SiteHygieneLabels.Number(g.Count())}."));

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
