using ApplicationPackagerEngine.Models;
using ScopeSource = ApplicationPackagerEngine.Models.ScopeSource;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>One line of the table of one system.</summary>
public sealed record PublishReviewLine(string Application, string Mark, string Versions, string System, string Change, string Deployment, string Detail)
{
    public string Name => $"{Application}, {System}, {Detail}";

    /// <summary>"Changed" and "Outside the view", the marks of the application; empty when it has none.</summary>
    public string Checked { get; init; } = "";
}

/// <summary>The card of one system when the plan has several. <see cref="Detail"/> is every line of the plan about that system.</summary>
public sealed record PublishSystemCard(string Destination, string Text, string Detail);

/// <summary>One row of the review grid: the application and one text for each system, in the order of <see cref="PublishPlan.Systems"/>.</summary>
public sealed record PublishReviewRow(string Application, string Version, string Checked, IReadOnlyList<string> Cells)
{
    public string Mark => Helpers.PackagesCells.MarkOf(Application);

    public IReadOnlyList<Wpf.Ui.Controls.SymbolRegular> Glyphs { get; init; } = [];

    /// <summary>The name of each cell: the application, the system and the cell text.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];
}

/// <summary>
/// The publish review window (overhaul V1, phase 5a): what Publish All will write for the checked applications, the comment of the run, and the
/// button that starts it. Back closes the window and nothing runs. The comment moves here from a dialog that asked after the click.
/// </summary>
public partial class PublishReviewViewModel : ObservableObject
{
    public PublishReviewViewModel(PublishPlan plan, string comment, IReadOnlyDictionary<string, string>? changes = null)
    {
        Plan = plan;
        Changes = changes ?? new Dictionary<string, string>();
        _comment = comment ?? "";
        ShowsComment = HasComment && _comment.Trim().Length > 0;
        Rows = plan.Rows.Select(r => new PublishReviewRow(r.App.Application, r.App.Version.Length > 0 ? r.App.Version : "Not checked",
            string.Join(", ", new[] { Changes.ContainsKey(r.App.Uid) ? "Changed" : "", r.App.Hidden ? "Outside the view" : "" }.Where(t => t.Length > 0)),
            r.Cells.Select(c => plan.Systems.Count > 1 ? MatrixText(c.Text) : c.Text).ToList())
        {
            Glyphs = r.Cells.Select(c => c.Kind switch
            {
                PublishCellKind.Create => Wpf.Ui.Controls.SymbolRegular.ArrowCircleUp24,
                PublishCellKind.Skip => Wpf.Ui.Controls.SymbolRegular.SubtractCircle24,
                PublishCellKind.Blocked => Wpf.Ui.Controls.SymbolRegular.Warning24,
                _ => Wpf.Ui.Controls.SymbolRegular.DismissCircle24
            }).ToList(),
            Names = r.Cells.Select(c => $"{r.App.Application}, {c.Destination}, {c.Text}").ToList()
        }).ToList();
    }

    /// <summary>What differs from the review the operator confirmed before; empty on the first review.</summary>
    public IReadOnlyDictionary<string, string> Changes { get; }

    /// <summary>Why this review opened again, with each difference; empty on the first review.</summary>
    public string ChangedText => Changes.Count == 0 ? ""
        : "Nothing was published. The plan changed after you confirmed it. Check it and confirm again. " + string.Join(" ", Changes.Values);

    public PublishPlan Plan { get; }

    public IReadOnlyList<PublishReviewRow> Rows { get; }

    public string CountsText => Plan.CountsText;

    public string ConfirmText => Plan.ConfirmText;

    public bool CanRun => Plan.CanRun;

    /// <summary>One system: the table has one line for each application. Several: one column for each system.</summary>
    public bool SingleSystem => Plan.Systems.Count <= 1;

    public bool SeveralSystems => !SingleSystem;

    /// <summary>The line under the heading: "ConfigMgr · Site MCM" for one system, "Site MCM · ConfigMgr, Intune and WSUS" for several.</summary>
    public string SubLine
    {
        get
        {
            var site = Plan.SiteCode.Length == 0 ? "" : "Site " + Plan.SiteCode;
            var systems = Plan.RetryDestination.Length > 0 ? Plan.RetryDestination : AndList(Plan.Systems);
            if (site.Length == 0) return systems;
            return SingleSystem ? $"{systems} \u00b7 {site}" : $"{site} \u00b7 {systems}";
        }
    }

    private static string AndList(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    /// <summary>The banner over the table.</summary>
    public string BannerText
    {
        get
        {
            var hidden = HiddenText.Length > 0 ? " " + HiddenText : "";
            if (Plan.RetryDestination.Length > 0) return ScopeText + hidden;
            if (SingleSystem)
            {
                var off = new[] { OneClickDestinations.ConfigMgr, OneClickDestinations.Intune, OneClickDestinations.Wsus }.Where(s => !Plan.Systems.Contains(s)).ToList();
                var tail = off.Count == 0 ? "" : $" {AndList(off)} {(off.Count == 1 ? "is" : "are")} off.";
                return "Review the exact changes before you publish." + tail + hidden;
            }
            var skipped = Plan.Rows.SelectMany(r => r.Cells).Where(c => c.Kind == PublishCellKind.Skip).ToList();
            var held = skipped.Count == 0 ? ""
                : skipped.All(c => c.Destination == OneClickDestinations.Wsus && c.Text.EndsWith("version already published", StringComparison.Ordinal))
                    ? $" {skipped.Count} WSUS {(skipped.Count == 1 ? "update is" : "updates are")} already published."
                    : $" {skipped.Count} {(skipped.Count == 1 ? "publish is" : "publishes are")} skipped.";
            return $"{Plan.Writes} destination {(Plan.Writes == 1 ? "publish is" : "publishes are")} planned.{held} Review each destination below.{hidden}";
        }
    }

    /// <summary>The lines of the table of one system.</summary>
    public IReadOnlyList<PublishReviewLine> Lines => !SingleSystem ? [] : Plan.Rows.Select((r, i) =>
    {
        var cell = r.Cells.Count > 0 ? r.Cells[0] : null;
        var current = r.App.Site.Kind == ConfigMgrFactKind.Found && r.App.Site.Version.Length > 0 ? r.App.Site.Version : "None";
        // What follows the new application is in the Other changes card, and the warnings of the cell are in the notice above the table.
        var change = ShortChange(cell?.Text ?? "");
        var deployment = r.App.ScriptDecides ? "Script decides"
            : r.App.ConfigMgrSteps.Contains("deploy to ", StringComparison.Ordinal) ? "Test deployment" : "None";
        var version = r.App.Version.Length > 0 ? r.App.Version : "Not checked";
        return new PublishReviewLine(r.App.Application, Mark(r.App.Application), $"{current} \u2192 {version}", cell?.Destination ?? "", change, deployment,
            cell?.Text ?? "") { Checked = Rows[i].Checked };
    }).ToList();

    private static string ShortChange(string text)
    {
        string[] marks = [". Then: ", " (ConfigMgr has ", Unfinished, PublishPlan.WarningMark];
        var ends = marks.Select(m => text.IndexOf(m, StringComparison.Ordinal)).Where(i => i > 0).ToList();
        return ends.Count == 0 ? text : text[..ends.Min()];
    }

    private const string Unfinished = " (the last publish of this version did not finish";

    private bool Retry => Plan.RetryDestination.Length > 0;

    private static PublishPlanCell? ConfigMgrCell(PublishPlanRow row) =>
        row.Cells.FirstOrDefault(c => c.Destination == OneClickDestinations.ConfigMgr && c.Kind == PublishCellKind.Create);

    /// <summary>True when the run can replace the ConfigMgr application of the row: it asks first. A retry never replaces one.</summary>
    private bool MayReplace(PublishPlanRow row) => !Retry && ConfigMgrCell(row) is { } cell
        && (cell.Text.Contains(PublishPlan.ReplaceAsk, StringComparison.Ordinal) || cell.Text.Contains("(ConfigMgr was not read)", StringComparison.Ordinal));

    /// <summary>The warnings that the short text of a cell of the one-system table leaves out.</summary>
    private IEnumerable<string> CellWarnings(PublishPlanCell cell)
    {
        var parts = cell.Text.Split(PublishPlan.WarningMark);
        var action = parts[0];
        var replace = !Retry && action.Contains(PublishPlan.ReplaceAsk, StringComparison.Ordinal) ? " If the title matches, the run asks before it replaces it." : "";
        if (action.Contains(Unfinished, StringComparison.Ordinal))
            yield return "The last publish of this version did not finish. Verify it in ConfigMgr." + replace;
        else if (replace.Length > 0)
        {
            const string has = " (ConfigMgr has ";
            var start = action.IndexOf(has, StringComparison.Ordinal);
            var end = action.IndexOf("; " + PublishPlan.ReplaceAsk, StringComparison.Ordinal);
            yield return (start >= 0 && end > start ? "ConfigMgr has " + action[(start + has.Length)..end] + "." : "ConfigMgr has an application.") + replace;
        }
        foreach (var warning in parts.Skip(1)) yield return "Warning: " + warning.TrimEnd('.') + ".";
    }

    /// <summary>The line of the Intune review that can open during the run.</summary>
    public const string IntuneReviewText = "The run may ask Intune review, when a stage has a finding that needs a decision. Cancel stops the whole run.";

    /// <summary>
    /// What the run risks, as text: the warnings that the table of one system cuts, the prompt that can overwrite an application, what a retry
    /// does not do and the Intune review. Empty for a plan with none of these.
    /// </summary>
    public string RiskText
    {
        get
        {
            var lines = new List<string>();
            // The table of several systems wraps the whole text of each cell, so only the table of one system cuts it.
            if (SingleSystem)
                foreach (var row in Plan.Rows)
                    foreach (var cell in row.Cells.Where(c => c.Kind == PublishCellKind.Create))
                        lines.AddRange(CellWarnings(cell).Select(w => $"{row.App.Application}: {w}"));
            if (Plan.Rows.Any(MayReplace))
                lines.Add("The run may ask Application Already Exists (Overwrite, Skip or Cancel remaining). Overwrite replaces the application in ConfigMgr.");
            if (Plan.RetryDestination == OneClickDestinations.ConfigMgr)
                lines.Add("The retry does not replace an application. It publishes nothing when ConfigMgr has this version.");
            // The mock draws the review of all three systems with no warning and no such line, so there only the tooltip of the button has it.
            if ((Plan.Systems.Count < 3 || lines.Count > 0)
                && Plan.Rows.Any(r => r.Cells.Any(c => c.Destination == OneClickDestinations.Intune && c.Kind == PublishCellKind.Create)))
                lines.Add(IntuneReviewText);
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>True when the comment of the run was not empty as the review opened: the review then shows it, so no old comment is published unseen.</summary>
    public bool ShowsComment { get; }

    /// <summary>True when an application of the review has a mark; only then does the table have the Checked column.</summary>
    public bool HasCheckedMarks => Rows.Any(r => r.Checked.Length > 0);

    /// <summary>The two characters before an application name; a host sets it to the marks that the grid shows.</summary>
    public Func<string, string> Mark { get; init; } = Helpers.PackagesCells.MarkOf;

    private string PerApplication(Func<PublishPlanApp, string> text) =>
        Plan.Rows.Count == 1 ? text(Plan.Rows[0].App) : string.Join(Environment.NewLine, Plan.Rows.Select(r => r.App.Application + ": " + text(r.App)));

    /// <summary>One text when every application has the same; else one line for each application.</summary>
    private string SameOrPerApplication(Func<PublishPlanApp, string> text) =>
        Plan.Rows.Select(r => text(r.App)).Distinct().ToList() is [var only] ? only : PerApplication(text);

    private string ExistingText(PublishPlanApp a)
    {
        if (a.Site.Kind == ConfigMgrFactKind.Unreadable) return "ConfigMgr was not read";
        if (a.Site.Kind != ConfigMgrFactKind.Found) return "None found by the last check";
        var old = a.Site.Version.Length > 0 ? $"{WithoutArchitecture(a.Application)} {a.Site.Version}" : "the application with no version";
        var sentence = char.ToUpperInvariant(old[0]) + old[1..];
        if (MayReplace(Plan.Rows.First(r => r.App.Uid == a.Uid))) return $"{sentence} is in ConfigMgr. The run can replace it. It asks first.";
        if (a.Supersedes) return $"{sentence} is in ConfigMgr. Supersedence is on.";
        return "Keep " + old;
    }

    private string CardLine(string label) =>
        Plan.Cards.SelectMany(c => c.Lines).FirstOrDefault(l => l.Label == label)?.Text ?? "";

    /// <summary>The "Content and detection" card of one system.</summary>
    public IReadOnlyList<PublishCardLine> ContentLines
    {
        get
        {
            var configMgr = Plan.Systems.Contains(OneClickDestinations.ConfigMgr);
            var lines = new List<PublishCardLine> { new("Stage and package", "Prepare installer content and build the package") };
            if (configMgr)
            {
                lines.Add(new("Detection method", PerApplication(a => a.Detection.Length > 0 ? a.Detection : "Set by the packager")));
                var root = CardLine("Content source");
                lines.Add(new("Content source", PerApplication(a => a.ContentSource.Length > 0 ? a.ContentSource : root)));
            }
            else foreach (var line in Plan.Cards.SelectMany(c => c.Lines).Where(l => l.Label is "Detection method" or "Applies to")) lines.Add(line);
            return lines;
        }
    }

    /// <summary>The "Other changes" card of one system.</summary>
    public IReadOnlyList<PublishCardLine> OtherLines
    {
        get
        {
            if (!Plan.Systems.Contains(OneClickDestinations.ConfigMgr))
                return Plan.Cards.SelectMany(c => c.Lines).Where(l => l.Label is "Scope" or "Assignments").ToList();
            return
            [
                new("Existing application", PerApplication(ExistingText)),
                new("Supersedence", SameOrPerApplication(a => a.ScriptDecides ? "The PS1 packager script decides"
                    : a.Supersedes ? "On. The run asks before it applies it." : "Off")),
                new("Distribution", PerApplication(a => a.ScriptDecides ? "The PS1 packager script decides"
                    : a.ConfigMgrSteps.Length > 0 ? a.ConfigMgrSteps : "No distribution point groups selected"))
            ];
        }
    }

    private static string WithoutArchitecture(string name)
    {
        var bracket = name.LastIndexOf(" (", StringComparison.Ordinal);
        return bracket > 0 ? name[..bracket] : name;
    }

    /// <summary>The cards of several systems: one sentence or two for each.</summary>
    public IReadOnlyList<PublishSystemCard> SystemCards => SingleSystem ? [] : Plan.Systems.Select(system => new PublishSystemCard(system, Summary(system), CardDetail(system))).ToList();

    private string CardDetail(string system) => string.Join(Environment.NewLine,
        Plan.Cards.Where(c => c.Destination == system).SelectMany(c => c.Lines).Select(l => l.Label + ": " + l.Text));

    /// <summary>Every line of the plan about the one system of a single-system review: the tooltip of its two cards; null when the plan has no card.</summary>
    public string? SystemDetail => SingleSystem && Plan.Systems.Count == 1 && CardDetail(Plan.Systems[0]) is { Length: > 0 } text ? text : null;

    private string Summary(string system)
    {
        var apps = Plan.Rows.Where(r => r.Cells.Any(c => c.Destination == system && c.Kind == PublishCellKind.Create)).Select(r => r.App).ToList();
        if (apps.Count == 0) return "No application is published to this system.";
        switch (system)
        {
            case OneClickDestinations.ConfigMgr:
                return ConfigMgrSummary(apps);
            case OneClickDestinations.Intune:
                return "Upload app content and add detection rules. No assignments.";
            default:
                var what = apps.Count == 1 ? $"the {WithoutArchitecture(apps[0].Application)} update" : $"{apps.Count} updates";
                var scope = Plan.Cards.Where(c => c.Destination == system).SelectMany(c => c.Lines).FirstOrDefault(l => l.Label == "Scope")?.Text ?? "";
                const string approved = "approved for group ";
                var approval = scope.StartsWith(approved, StringComparison.Ordinal) ? $"The run approves it for group {scope[approved.Length..]}." : "No approvals.";
                return $"Publish {what} to the update server. {approval}";
        }
    }

    /// <summary>The card starts with "Keep existing applications" only when no application of it can be replaced or superseded and no script decides.</summary>
    private string ConfigMgrSummary(IReadOnlyList<PublishPlanApp> apps)
    {
        var uids = apps.Select(a => a.Uid).ToHashSet(StringComparer.Ordinal);
        var first = Plan.Rows.Where(r => uids.Contains(r.App.Uid)).Any(MayReplace) ? "The run can replace an application with the same title. It asks first."
            : apps.Any(a => a.Supersedes) ? "The run asks before it applies supersedence to an older application."
            : apps.Any(a => a.ScriptDecides) ? ""
            : "Keep existing applications.";
        var parts = new List<string> { first, "Add detection methods for the new versions." };
        parts.AddRange(apps.Where(a => a.ScriptDecides).Select(a => $"{a.Application}: the PS1 packager script decides distribution and deployment."));
        parts.AddRange(apps.Where(a => a.ConfigMgrSteps.Length > 0).Select(a => $"{a.Application}: {a.ConfigMgrSteps}."));
        if (apps.All(a => !a.ScriptDecides && a.ConfigMgrSteps.Length == 0)) parts.Add("No supersedence, content distribution or deployment.");
        return string.Join(" ", parts.Where(p => p.Length > 0));
    }

    /// <summary>The cells of the table of several systems, with the shorter words of the mock for a new ConfigMgr application.</summary>
    public static string MatrixText(string cell) =>
        cell == PublishPlan.ConfigMgrAction ? "Create app and deployment type" : cell;

    /// <summary>The header of a system column: the system and what it writes to.</summary>
    public string SystemHeader(string system) =>
        system == OneClickDestinations.ConfigMgr && Plan.SiteCode.Length > 0 ? $"{system} \u00b7 Site {Plan.SiteCode}"
        : system == OneClickDestinations.Wsus && WsusName.Length > 0 ? $"{system} \u00b7 {WsusName}"
        : system;

    /// <summary>The WSUS server that the run writes to; empty when not known.</summary>
    public string WsusName { get; init; } = "";

    /// <summary>What the run may ask and what is read again before it writes: the tooltip of the confirm button.</summary>
    public string ConfirmToolTip => string.Join(Environment.NewLine + Environment.NewLine,
        new[] { "Start the publish run for the applications and systems listed here.", PlanNoteText, MayAskText }.Where(t => t.Length > 0));

    public string ScopeText => Plan.RetryDestination.Length > 0
        ? $"Retry of a failed destination: {Plan.RetryDestination} only."
        : "Publish All: " + ApplicationPackagerEngine.Models.ScopeText.For(
            new DestinationSet(Plan.Systems.Contains(OneClickDestinations.ConfigMgr), Plan.Systems.Contains(OneClickDestinations.Wsus),
                Plan.Systems.Contains(OneClickDestinations.Intune)), ScopeSource.SystemsInUse, Plan.SiteCode);

    public string HeadingText => $"Review {Plan.Rows.Count} {(Plan.Rows.Count == 1 ? "application" : "applications")}";

    /// <summary>The site line of the window, as the footer of the main window writes it; empty when the plan has no site.</summary>
    public string SiteLine => Plan.Site.Length == 0 ? "" : SiteFooter.Format(Plan.SiteCode, Plan.Provider);

    /// <summary>"Planned from the last check at 2026-11-26 14:05." The oldest check of the listed applications is the one named.</summary>
    public string LastCheckText
    {
        get
        {
            var times = Plan.Rows.Select(r => r.App.CheckedAt).ToList();
            var unknown = times.Count(t => t is null);
            if (unknown == times.Count) return "Planned from the last check. No check time is stored.";
            var oldest = times.Where(t => t is not null).Min()!.Value.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            var text = times.Distinct().Count() == 1 ? $"Planned from the last check at {oldest}." : $"Planned from the last check at {oldest} (the oldest of the listed checks).";
            return unknown == 0 ? text : $"{text} No check time is stored for {unknown} {(unknown == 1 ? "application" : "applications")}.";
        }
    }

    /// <summary>What is read again before the first write, and what the run does when a destination already has the application.</summary>
    public string PlanNoteText => Plan.RetryDestination == OneClickDestinations.ConfigMgr
        ? LastCheckText + " ConfigMgr is read again when you confirm. The retry does not replace an application. It publishes nothing when ConfigMgr has this version."
        : Plan.Systems.Contains(OneClickDestinations.ConfigMgr)
        ? LastCheckText + " ConfigMgr is read again when you confirm. If an application exists, the run asks."
        : LastCheckText + " The settings and the versions are read again when you confirm."
          + (Plan.Systems.Contains(OneClickDestinations.Wsus)
              ? " The publish does not change an update that WSUS already has for a version. It refuses a stage that differs from it." : "");

    /// <summary>ConfigMgr and Intune write the comment to the application. WSUS does not.</summary>
    public bool HasComment => Plan.Systems.Contains(OneClickDestinations.ConfigMgr) || Plan.Systems.Contains(OneClickDestinations.Intune);

    /// <summary>The prompts that can still open during the run. Each one still asks; the review does not answer it.</summary>
    public string MayAskText
    {
        get
        {
            var asks = new List<string>();
            bool Writes(string system) => Plan.Rows.Any(r => r.Cells.Any(c => c.Destination == system && c.Kind == PublishCellKind.Create));
            if (Writes(OneClickDestinations.ConfigMgr))
            {
                if (Plan.RetryDestination.Length == 0) asks.Add("Application Already Exists, when ConfigMgr has an application with the same title (Overwrite, Skip or Cancel remaining)");
                asks.Add("the supersedence confirmation, when supersedence is on and ConfigMgr has an older application");
            }
            if (Writes(OneClickDestinations.Intune)) asks.Add("Intune review, when a stage has a finding that needs a decision");
            return asks.Count == 0 ? "" : "The run may ask: " + string.Join("; ", asks) + ".";
        }
    }

    public IReadOnlyList<PublishCard> Cards => Plan.Cards;

    public bool HasCards => Plan.Cards.Count > 0;

    /// <summary>Says why the confirm button is off when every system of every application is skipped or cannot take it.</summary>
    public string NothingText => Plan.Writes == 0 && Plan.Rows.Count > 0 ? "Nothing to publish: no listed system takes a publish of these applications." : "";

    /// <summary>Why the run cannot start: each system that is in use and not ready, with its reason. Empty when it can start.</summary>
    public string BlockedText => string.Join(" ", Plan.Blocked.Select(b => b.Destination + ": " + b.Reason));

    public bool HasBlocked => Plan.Blocked.Count > 0;

    /// <summary>"N checked applications are outside the current view. They are published too."; empty when none is.</summary>
    public string HiddenText => Plan.HiddenCount == 0 ? ""
        : $"{Plan.HiddenCount} checked {(Plan.HiddenCount == 1 ? "application is" : "applications are")} outside the current view. {(Plan.HiddenCount == 1 ? "It is" : "They are")} published too.";

    [ObservableProperty] private string _comment;

    /// <summary>True after the confirm button was pressed.</summary>
    public bool Accepted { get; private set; }

    public event EventHandler? CloseRequested;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Accept()
    {
        if (!CanRun) return;
        Comment = Comment.Trim();
        Accepted = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Back()
    {
        Accepted = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
