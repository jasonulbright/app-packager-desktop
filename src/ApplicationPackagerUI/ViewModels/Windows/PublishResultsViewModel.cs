using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>One row of the results grid: the application and one cell text for each system, in the order of <see cref="PublishResultsViewModel.Systems"/>.</summary>
public sealed record PublishResultsRow(string Application, IReadOnlyList<string> Cells);

/// <summary>
/// What the results say about one failed destination. <see cref="CanRetry"/> is true where the app can verify what the destination holds.
/// </summary>
public sealed record PublishRetryOffer(string Uid, string Application, string Destination, bool CanRetry, string Advice)
{
    public string Text => $"{Application}, {Destination}: {Advice}";

    /// <summary>The name of the retry button, which a screen reader reads: "Retry ConfigMgr for Notepad++".</summary>
    public string ButtonName => $"Retry {Destination} for {Application}";
}

/// <summary>
/// The publish results window (overhaul V1, phase 5a): one cell for each application and system, built from the per-destination results of the run, with
/// counts and notices that say only what the run did. What reached a system stays published, and the window says so when a later system failed.
/// </summary>
public sealed class PublishResultsViewModel
{
    /// <param name="planned">The systems of the confirmed plan, so a system that no application reached still has its column.</param>
    /// <param name="offerRetry">True for a run that can retry a failed destination from this window. A One Click run cannot.</param>
    public PublishResultsViewModel(IReadOnlyList<(string Application, PublishRowResult Result)> rows, bool cancelled, IReadOnlyList<string>? planned = null,
        bool offerRetry = false)
    {
        Retries = offerRetry
            ? rows.SelectMany(r => r.Result.Destinations.Select(d => (Cell: d, Advice: PublishRetry.For(d)))
                .Where(x => x.Advice is not null)
                .Select(x => new PublishRetryOffer(r.Result.Uid, r.Application, x.Cell.Destination, x.Advice!.CanRetry, x.Advice.Text))).ToList()
            : [];
        Cancelled = cancelled;
        Systems = rows.SelectMany(r => r.Result.Destinations.Select(d => d.Destination)).Concat(planned ?? []).Distinct()
            .OrderBy(d => Array.IndexOf(new[] { OneClickDestinations.ConfigMgr, OneClickDestinations.Intune, OneClickDestinations.Wsus }, d)).ToList();
        Rows = rows.Select(r => new PublishResultsRow(r.Application,
            Systems.Select(s => r.Result.For(s)?.Text ?? "Not run").ToList())).ToList();
        var cells = rows.SelectMany(r => r.Result.Destinations).ToList();
        Published = cells.Count(c => c.Kind == PublishDestinationKind.Published);
        Failed = cells.Count(c => c.Kind is PublishDestinationKind.Failed or PublishDestinationKind.Blocked or PublishDestinationKind.NotSupported);
        Skipped = cells.Count(c => c.Kind == PublishDestinationKind.Skipped);
        Notices = rows.Where(r => r.Result.Partial).Select(r => PartialNotice(r.Application, r.Result)).ToList();
        Warnings = cells.Count(c => c.Warning);
        Details = rows.SelectMany(r => r.Result.Destinations.Where(d => (d.Kind != PublishDestinationKind.Published || d.Warning) && d.Message.Length > 0)
            .Select(d => $"{r.Application}, {d.Destination}: {d.Message}")).ToList();
    }

    public bool Cancelled { get; }

    /// <summary>One entry for each failed destination: a retry, or the reason why there is none.</summary>
    public IReadOnlyList<PublishRetryOffer> Retries { get; }

    /// <summary>Why the retry that the operator asked for did not run; empty when none was refused.</summary>
    public string RetryNote { get; init; } = "";

    /// <summary>The retry that the operator asked for; the caller runs it after the window closed.</summary>
    public PublishRetryOffer? RequestedRetry { get; private set; }

    public void RequestRetry(PublishRetryOffer offer)
    {
        if (offer.CanRetry && Retries.Contains(offer)) RequestedRetry = offer;
    }

    public IReadOnlyList<string> Systems { get; }

    public IReadOnlyList<PublishResultsRow> Rows { get; }

    public int Published { get; }

    public int Failed { get; }

    public int Skipped { get; }

    /// <summary>Published cells whose supersedence was not applied.</summary>
    public int Warnings { get; }

    /// <summary>One sentence for each application that reached one system and not another.</summary>
    public IReadOnlyList<string> Notices { get; }

    /// <summary>The message of each cell that is not Published, with its application and system.</summary>
    public IReadOnlyList<string> Details { get; }

    /// <summary>"3 publishes completed", "Publish completed with issues", "Publish was cancelled".</summary>
    public string Headline =>
        Cancelled ? "Publish was cancelled"
        : Failed > 0 || Warnings > 0 ? "Publish completed with issues"
        : Published == 0 ? "Nothing was published"
        : $"{Published} {(Published == 1 ? "publish" : "publishes")} completed";

    public string PublishedText => $"Published {Published}";
    public string FailedText => $"Failed {Failed}";
    public string SkippedText => $"Skipped {Skipped}";

    /// <summary>"Notepad++ reached ConfigMgr. It did not reach Intune (Failed at content upload)." Generated from the facts of the run, never fixed text.</summary>
    public static string PartialNotice(string application, PublishRowResult result)
    {
        var reached = result.Destinations.Where(d => d.Kind == PublishDestinationKind.Published).Select(d => d.Destination).ToList();
        var other = result.Destinations.Where(d => d.Kind != PublishDestinationKind.Published).ToList();
        // A stopped or broken publish may have written; a failed content step leaves the application there. Neither is "did not reach".
        static bool Unknown(PublishDestinationResult d) => d.Kind == PublishDestinationKind.Failed && d.Step is "cancel" or "error";
        static bool Created(PublishDestinationResult d) => d.Kind == PublishDestinationKind.Failed && d.Step is "content" or "content upload";
        var parts = new List<string> { $"{application} reached {Join(reached)}." };
        var missed = other.Where(d => !Unknown(d) && !Created(d)).Select(d => $"{d.Destination} ({d.Text})").ToList();
        if (missed.Count > 0) parts.Add($"It did not reach {Join(missed)}.");
        parts.AddRange(other.Where(Created).Select(d => $"{d.Destination} has the application, and a later step failed ({d.Text})."));
        parts.AddRange(other.Where(Unknown).Select(d => $"The state in {d.Destination} is not known. Check the system."));
        parts.Add("What reached a system stays published.");
        return string.Join(" ", parts);
    }

    private static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Join("", items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
}
