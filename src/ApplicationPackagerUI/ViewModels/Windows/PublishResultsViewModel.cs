using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>One row of the results grid: the application and one cell text for each system, in the order of <see cref="PublishResultsViewModel.Systems"/>.</summary>
public sealed record PublishResultsRow(string Application, IReadOnlyList<string> Cells);

/// <summary>
/// The publish results window (overhaul V1, phase 5a): one cell for each application and system, built from the per-destination results of the run, with
/// counts and notices that say only what the run did. What reached a system stays published, and the window says so when a later system failed.
/// </summary>
public sealed class PublishResultsViewModel
{
    public PublishResultsViewModel(IReadOnlyList<(string Application, PublishRowResult Result)> rows, bool cancelled)
    {
        Cancelled = cancelled;
        Systems = rows.SelectMany(r => r.Result.Destinations.Select(d => d.Destination)).Distinct()
            .OrderBy(d => Array.IndexOf(new[] { OneClickDestinations.ConfigMgr, OneClickDestinations.Intune, OneClickDestinations.Wsus }, d)).ToList();
        Rows = rows.Select(r => new PublishResultsRow(r.Application,
            Systems.Select(s => r.Result.For(s)?.Text ?? "Not run").ToList())).ToList();
        var cells = rows.SelectMany(r => r.Result.Destinations).ToList();
        Published = cells.Count(c => c.Kind == PublishDestinationKind.Published);
        Failed = cells.Count(c => c.Kind is PublishDestinationKind.Failed or PublishDestinationKind.Blocked or PublishDestinationKind.NotSupported);
        Skipped = cells.Count(c => c.Kind == PublishDestinationKind.Skipped);
        Notices = rows.Where(r => r.Result.Partial).Select(r => PartialNotice(r.Application, r.Result)).ToList();
        Details = rows.SelectMany(r => r.Result.Destinations.Where(d => d.Kind != PublishDestinationKind.Published && d.Message.Length > 0)
            .Select(d => $"{r.Application}, {d.Destination}: {d.Message}")).ToList();
    }

    public bool Cancelled { get; }

    public IReadOnlyList<string> Systems { get; }

    public IReadOnlyList<PublishResultsRow> Rows { get; }

    public int Published { get; }

    public int Failed { get; }

    public int Skipped { get; }

    /// <summary>One sentence for each application that reached one system and not another.</summary>
    public IReadOnlyList<string> Notices { get; }

    /// <summary>The message of each cell that is not Published, with its application and system.</summary>
    public IReadOnlyList<string> Details { get; }

    /// <summary>"3 publishes completed", "Publish completed with issues", "Publish was cancelled".</summary>
    public string Headline =>
        Cancelled ? "Publish was cancelled"
        : Failed > 0 ? "Publish completed with issues"
        : Published == 0 ? "Nothing was published"
        : $"{Published} {(Published == 1 ? "publish" : "publishes")} completed";

    public string PublishedText => $"Published {Published}";
    public string FailedText => $"Failed {Failed}";
    public string SkippedText => $"Skipped {Skipped}";

    /// <summary>"Notepad++ reached ConfigMgr. It did not reach Intune (Failed at content upload)." Generated from the facts of the run, never fixed text.</summary>
    public static string PartialNotice(string application, PublishRowResult result)
    {
        var reached = result.Destinations.Where(d => d.Kind == PublishDestinationKind.Published).Select(d => d.Destination).ToList();
        var missed = result.Destinations.Where(d => d.Kind != PublishDestinationKind.Published)
            .Select(d => $"{d.Destination} ({d.Text})").ToList();
        return $"{application} reached {Join(reached)}. It did not reach {Join(missed)}. What reached a system stays published.";
    }

    private static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Join("", items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
}
