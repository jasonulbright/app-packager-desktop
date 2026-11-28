using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>One row of the results grid: the application and one cell text for each system, in the order of <see cref="PublishResultsViewModel.Systems"/>.</summary>
public sealed record PublishResultsRow(string Application, IReadOnlyList<string> Cells)
{
    public string Uid { get; init; } = "";

    public IReadOnlyList<Wpf.Ui.Controls.SymbolRegular> Glyphs { get; init; } = [];

    public IReadOnlyList<string> Names { get; init; } = [];

    public bool Failed { get; init; }
}

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
public sealed partial class PublishResultsViewModel
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
            Systems.Select(s => r.Result.For(s)?.Text ?? "Not run").ToList())
        {
            Uid = r.Result.Uid,
            Glyphs = Systems.Select(s => Glyph(r.Result.For(s))).ToList(),
            Names = Systems.Select(s => $"{r.Application}, {s}, {r.Result.For(s)?.Text ?? "Not run"}").ToList(),
            Failed = r.Result.Destinations.Any(d => d.Kind == PublishDestinationKind.Failed)
        }).ToList();
        var cells = rows.SelectMany(r => r.Result.Destinations).ToList();
        Published = cells.Count(c => c.Kind == PublishDestinationKind.Published);
        Failed = cells.Count(c => c.Kind is PublishDestinationKind.Failed or PublishDestinationKind.Blocked or PublishDestinationKind.NotSupported);
        Skipped = cells.Count(c => c.Kind == PublishDestinationKind.Skipped);
        Notices = rows.Where(r => r.Result.Partial).Select(r => PartialNotice(r.Application, r.Result)).ToList();
        _rows = rows;
        Warnings = cells.Count(c => c.Warning);
        Details = rows.SelectMany(r => r.Result.Destinations.Where(d => (d.Kind != PublishDestinationKind.Published || d.Warning) && d.Message.Length > 0)
            .Select(d => $"{r.Application}, {d.Destination}: {d.Message}")).ToList();
    }

    private static Wpf.Ui.Controls.SymbolRegular Glyph(PublishDestinationResult? cell) => cell switch
    {
        null => Wpf.Ui.Controls.SymbolRegular.Circle24,
        { Kind: PublishDestinationKind.Published, Warning: false } => Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24,
        { Kind: PublishDestinationKind.Published } => Wpf.Ui.Controls.SymbolRegular.Warning24,
        { Kind: PublishDestinationKind.Skipped } => Wpf.Ui.Controls.SymbolRegular.SubtractCircle24,
        _ => Wpf.Ui.Controls.SymbolRegular.DismissCircle24
    };

    private readonly IReadOnlyList<(string Application, PublishRowResult Result)> _rows;

    /// <summary>The applications for which the run asked for no deployment (no test collection step).</summary>
    public IReadOnlySet<string> WithoutDeployment { get; init; } = new HashSet<string>();

    /// <summary>"Publishing created 2 ConfigMgr applications. No deployment was created."; empty when the facts do not show that.</summary>
    public string DeploymentNotice
    {
        get
        {
            var count = _rows.Count(r => WithoutDeployment.Contains(r.Result.Uid)
                && r.Result.For(OneClickDestinations.ConfigMgr) is { Created: true });
            return count == 0 ? ""
                : $"Publishing created {(count == 1 ? "a ConfigMgr application" : count + " ConfigMgr applications")}. No deployment was created.";
        }
    }

    /// <summary>The Markdown report of the run; empty when none was written.</summary>
    public string ReportPath { get; init; } = "";

    public bool HasReport => ReportPath.Length > 0;

    /// <summary>Opens the log of the session; null hides the button.</summary>
    public Action<System.Windows.Window>? ShowLogs { get; init; }

    /// <summary>Opens the operation history of one application, by its Uid; null hides the button.</summary>
    public Action<string, System.Windows.Window>? ShowHistory { get; init; }

    public bool HasLogs => ShowLogs is not null;

    public bool HasHistory => ShowHistory is not null;

    /// <summary>The first application with a failed system; null when none failed.</summary>
    public PublishResultsRow? FirstFailed => Rows.FirstOrDefault(r => r.Failed);

    public bool HasFailure => FirstFailed is not null;

    /// <summary>The application that the operator asked to review; the caller selects it in the grid after the window closed.</summary>
    public string ReviewUid { get; private set; } = "";

    public void RequestReview(PublishResultsRow row) => ReviewUid = row.Uid;

    public bool Cancelled { get; }

    /// <summary>One entry for each failed destination: a retry, or the reason why there is none.</summary>
    public IReadOnlyList<PublishRetryOffer> Retries { get; }

    /// <summary>The site line of the window; empty when the caller gave none.</summary>
    public string SiteLine { get; init; } = "";

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
        : HasIssues ? (IsOneClick ? "One Click completed with issues" : "Publish completed with issues")
        : Published == 0 ? "Nothing was published"
        : IsOneClick ? "One Click completed"
        : $"{PublishedApplications} {(PublishedApplications == 1 ? "application" : "applications")} published";

    private int PublishedApplications => _rows.Count(r => r.Result.Destinations.Any(d => d.Kind == PublishDestinationKind.Published));

    public string PublishedText => $"Published {Published}";
    public string FailedText => $"Failed {Failed}";
    public string SkippedText => $"Skipped {Skipped}";

    /// <summary>
    /// "Notepad++ reached ConfigMgr. Its Intune content upload failed. The WSUS version was already published." Generated from the facts of the
    /// run, never fixed text. A stopped or broken publish may have written, so its state is named as not known.
    /// </summary>
    public static string PartialNotice(string application, PublishRowResult result)
    {
        var reached = result.Destinations.Where(d => d.Kind == PublishDestinationKind.Published).Select(d => d.Destination).ToList();
        var parts = new List<string> { $"{WithoutArchitecture(application)} reached {Join(reached)}." };
        foreach (var d in result.Destinations.Where(d => d.Kind != PublishDestinationKind.Published))
            parts.Add(d.Kind switch
            {
                PublishDestinationKind.Failed when d.Step is "cancel" or "error" => $"The state in {d.Destination} is not known. Check the system.",
                PublishDestinationKind.Failed when d.Step is "content" => $"{d.Destination} has the application, and its content step failed.",
                PublishDestinationKind.Failed when d.Step.Length > 0 => $"Its {d.Destination} {d.Step} failed.",
                PublishDestinationKind.Skipped when d.Message.Contains("already published", StringComparison.OrdinalIgnoreCase)
                    => $"The {d.Destination} version was already published.",
                _ => $"It did not reach {d.Destination} ({d.Text})."
            });
        if (parts.Count > 1 && result.Destinations.Any(d => d.Kind == PublishDestinationKind.Failed && d.Step is "cancel" or "error" or "content"))
            parts.Add("What reached a system stays published.");
        return string.Join(" ", parts);
    }

    private static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Join("", items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
}
