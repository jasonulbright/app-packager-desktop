using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>One cell of a results table as the window draws it: the glyph, the color class and the text. No glyph is the dash of a system that is off.</summary>
public sealed record ResultSignal(string Icon, string Kind, string Text);

/// <summary>One column of a results table that holds a system: its header.</summary>
public sealed record ResultSystemColumn(string System, string Header);

/// <summary>One row of the results table as the window draws it.</summary>
public sealed record ResultLine(string Uid, string Application, string Mark, string Version, IReadOnlyList<ResultSignal> Cells, string Result, string Tip)
{
    public string Name => $"{Application}, {string.Join(", ", Cells.Where(c => c.Icon.Length > 0).Select(c => c.Text))}";

    /// <summary>The name of each system cell: the application, the system and what happened there.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];
}

/// <summary>
/// What the results window shows. It has three layouts, those of the mock: a publish to one system (a column for each of the three systems, with a
/// dash for a system that is off, and the result in words), a publish to several (a column for each system) and a One Click run (three counts,
/// then the table of the first layout).
/// </summary>
public sealed partial class PublishResultsViewModel
{
    private static readonly string[] AllSystems = [OneClickDestinations.ConfigMgr, OneClickDestinations.Intune, OneClickDestinations.Wsus];

    /// <summary>True for the results of a One Click run.</summary>
    public bool IsOneClick { get; init; }

    /// <summary>The version of each application, by its Uid.</summary>
    public IReadOnlyDictionary<string, string> Versions { get; init; } = new Dictionary<string, string>();

    /// <summary>The two characters before an application name.</summary>
    public Func<string, string> Mark { get; init; } = Helpers.PackagesCells.MarkOf;

    /// <summary>The site code of the run; empty when the caller gave none.</summary>
    public string SiteCode { get; init; } = "";

    /// <summary>What each system writes to, for the header of its column in the layout of several systems.</summary>
    public IReadOnlyDictionary<string, string> SystemNames { get; init; } = new Dictionary<string, string>();

    /// <summary>A word on the run for the line under the heading, such as its time; empty adds nothing.</summary>
    public string RunNote { get; init; } = "";

    /// <summary>"Audacity 3.7.9 was kept. Supersedence is off."; empty when the run does not know it.</summary>
    public string KeptNotice { get; init; } = "";

    /// <summary>The runs of One Click that have reports; null hides the button.</summary>
    public Action<System.Windows.Window>? ShowRunHistory { get; init; }

    public bool HasRunHistory => ShowRunHistory is not null;

    public bool IsMatrix => !IsOneClick && Systems.Count > 1;

    public bool IsList => !IsMatrix;

    public bool IsPlainSuccess => !IsOneClick && !IsMatrix && Failed == 0 && Warnings == 0 && Published > 0 && !Cancelled;

    /// <summary>The report has its own button except on the plain success of one system, where the row menu opens it.</summary>
    public bool ShowReportButton => HasReport && !IsPlainSuccess;

    private bool HasIssues => Failed > 0 || Warnings > 0;

    private static string Plural(int count, string one, string many) => count == 1 ? one : many;

    public string SubLine
    {
        get
        {
            var site = SiteCode.Length == 0 ? "" : "Site " + SiteCode;
            string[] parts = IsOneClick ? [site, RunNote]
                : IsMatrix ? [$"{Rows.Count} {Plural(Rows.Count, "application", "applications")}",
                    $"{Published} destination {Plural(Published, "publish", "publishes")} succeeded", $"{Failed} failed", $"{Skipped} skipped"]
                : [string.Join(", ", Systems), site, RunNote];
            return string.Join(" · ", parts.Where(p => p.Length > 0));
        }
    }

    /// <summary>The blue banner of a plain success: where the application is now.</summary>
    public string SuccessBanner
    {
        get
        {
            if (!IsPlainSuccess || _rows.Count == 0) return "";
            var console = Systems.Contains(OneClickDestinations.ConfigMgr) ? "the ConfigMgr console" : Systems.FirstOrDefault() ?? "the system";
            var what = _rows.Count == 1
                ? $"{WithoutArchitecture(_rows[0].Application)} {VersionOf(_rows[0].Result.Uid)}".TrimEnd() + " is available"
                : $"{_rows.Count} applications are available";
            var created = DeploymentNotice.Length > 0 ? " No deployment was created." : "";
            return $"{what} in {console}.{created}";
        }
    }

    /// <summary>The amber notice: what reached one system and not another.</summary>
    public string IssueNotice => string.Join(" ", Notices);

    /// <summary>
    /// The gray notice under the table. The banner of a plain success says that no deployment was created, so its notice names what was kept.
    /// </summary>
    public string TableNotice => !IsOneClick && IsPlainSuccess ? KeptNotice : DeploymentNotice;

    /// <summary>
    /// The message of each failed system, and of a published one with a warning, that the table does not show: the table of several systems has no
    /// column for a message, and the Details column of the others shows one message of each application.
    /// </summary>
    public IReadOnlyList<string> FailureLines => _rows.SelectMany(r => r.Result.Destinations
        .Where(d => (d.Kind is not (PublishDestinationKind.Published or PublishDestinationKind.Skipped) || d.Warning) && d.Message.Length > 0)
        .Where(d => IsMatrix || d.Message != ResultWords(r.Result))
        .Select(d => $"{r.Application}, {d.Destination}: {d.Message}")).ToList();

    /// <summary>What to verify before a retry, or what a retry does, for each failed system of a window that can retry.</summary>
    public IReadOnlyList<string> RetryLines => Retries.Select(o => o.Text).ToList();

    /// <summary>
    /// The commands of the row menu that act on a failure, with how to open the menu; empty when the menu has none of them. It is the tooltip
    /// and the help text of the table.
    /// </summary>
    public string RowMenuTip
    {
        get
        {
            var names = new List<string>();
            if (Retries.Any(o => o.CanRetry)) names.Add("Retry");
            if (HasFailure) names.Add("Review failed application");
            if (IsMatrix && FailureLines.Count > 0) names.Add("Show failure messages");
            return names.Count == 0 ? ""
                : $"{Join(names)} {(names.Count == 1 ? "is" : "are")} in the menu of the result row. Select the row, then right-click it or press the Apps key or Shift+F10.";
        }
    }

    /// <summary>
    /// <see cref="RowMenuTip"/> as a line of the window. The mock draws two windows with a failure and no such line, so they have none: a One
    /// Click run, which has a button for the review, and several systems with a failure and no word on a retry.
    /// </summary>
    public string RowMenuHelp => IsOneClick || (IsMatrix && HasFailure && Retries.Count == 0) ? "" : RowMenuTip;

    /// <summary>What to do before a retry, under the table of several systems.</summary>
    public string FailureHelp
    {
        get
        {
            if (!IsMatrix || Failed == 0) return "";
            var failed = _rows.SelectMany(r => r.Result.Destinations).Where(d => d.Kind == PublishDestinationKind.Failed).Select(d => d.Destination).Distinct().ToList();
            var which = failed.Count == 1 ? failed[0] : "destination";
            return $"Review the failed destination and verify the existing {which} state before retrying. Successful destinations stay published.";
        }
    }

    private string VersionOf(string uid) => Versions.TryGetValue(uid, out var version) ? version : "";

    private static string WithoutArchitecture(string name)
    {
        var bracket = name.LastIndexOf(" (", StringComparison.Ordinal);
        return bracket > 0 ? name[..bracket] : name;
    }

    /// <summary>The system columns of the table.</summary>
    public IReadOnlyList<ResultSystemColumn> Columns => IsMatrix
        ? Systems.Select(s => new ResultSystemColumn(s, MatrixHeader(s))).ToList()
        : AllSystems.Select(s => new ResultSystemColumn(s, $"{s} · {(Systems.Contains(s) ? "enabled" : "off")}")).ToList();

    private string MatrixHeader(string system) =>
        system == OneClickDestinations.ConfigMgr && SiteCode.Length > 0 ? $"{system} · Site {SiteCode}"
        : SystemNames.TryGetValue(system, out var name) && name.Length > 0 ? $"{system} · {name}" : system;

    private static ResultSignal Signal(PublishDestinationResult? cell, bool says) => cell switch
    {
        { Kind: PublishDestinationKind.Skipped } when says && cell.Message.Contains("already published", StringComparison.OrdinalIgnoreCase)
            => new ResultSignal("clock", "", "Skipped \u00b7 already published"),
        null => new ResultSignal("", "", Helpers.PackagesCells.NoValue),
        { Kind: PublishDestinationKind.Published, Warning: false } => new ResultSignal("checkmark_circle", "good", cell.Text),
        { Kind: PublishDestinationKind.Published } => new ResultSignal("warning", "warn", cell.Text),
        { Kind: PublishDestinationKind.Skipped } => new ResultSignal("clock", "", cell.Text),
        _ => new ResultSignal("error_circle", "bad", cell.Text)
    };

    private static string ResultWords(PublishRowResult result)
    {
        var cell = result.Destinations.FirstOrDefault(d => d.Kind == PublishDestinationKind.Failed)
            ?? result.Destinations.FirstOrDefault(d => d.Warning)
            ?? result.Destinations.FirstOrDefault();
        if (cell is null) return "";
        if (cell is { Kind: PublishDestinationKind.Published, Warning: false, Created: true, Destination: OneClickDestinations.ConfigMgr })
            return "Application and deployment type created";
        return cell.Message.Length > 0 ? cell.Message : cell.Text;
    }

    public IReadOnlyList<ResultLine> Lines => _rows.Select(r => new ResultLine(r.Result.Uid, r.Application, Mark(r.Application), VersionOf(r.Result.Uid),
        Columns.Select(c => Signal(r.Result.For(c.System), IsMatrix)).ToList(), ResultWords(r.Result),
        RowTip(r.Result))
    {
        Names = Columns.Select(c => $"{r.Application}, {c.System}, {r.Result.For(c.System)?.Text ?? (Systems.Contains(c.System) ? "Not run" : "Off")}").ToList()
    }).ToList();

    /// <summary>The message of each system. Where no line of the window says that the run made no deployment, the row says it.</summary>
    private string RowTip(PublishRowResult result)
    {
        var lines = result.Destinations.Where(d => d.Message.Length > 0).Select(d => $"{d.Destination}: {d.Message}").ToList();
        if (!IsOneClick && !IsPlainSuccess && WithoutDeployment.Contains(result.Uid) && result.For(OneClickDestinations.ConfigMgr) is { Created: true })
            lines.Add("No deployment was created.");
        return string.Join(Environment.NewLine, lines);
    }
}
