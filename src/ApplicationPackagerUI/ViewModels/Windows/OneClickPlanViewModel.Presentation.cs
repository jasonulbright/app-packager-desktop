using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>One word of the line of steps of the run; every word but the first has an arrow before it.</summary>
public sealed record OneClickFlowStep(string Text, bool HasArrow);

/// <summary>One system beside the action: the glyph, the color class and the text.</summary>
public sealed record OneClickSystemSignal(string Icon, string Kind, string Text);

/// <summary>
/// What the plan window shows of the plan, with the words of the mock. It has two layouts: a run that can publish has a box for each system and one
/// column for the action and its reason; a run that only checks has an action and a reason column.
/// </summary>
public partial class OneClickPlanViewModel
{
    /// <summary>The names of the actions in the list, in the order publish, check, stage.</summary>
    public IReadOnlyList<string> ActionChoices { get; } = ["Check, stage, package and publish", "Check only", "Check and stage"];

    /// <summary>The name of the saved action in the list.</summary>
    public string ActionChoice => OneClickActionNames.Normalize(_context.Action) switch
    {
        OneClickActionNames.Report => ActionChoices[1],
        OneClickActionNames.Stage => ActionChoices[2],
        _ => ActionChoices[0]
    };

    public bool IsPublishRun => OneClickActionNames.Normalize(_context.Action) == OneClickActionNames.StageAndPackage;

    public bool IsCheckRun => !IsPublishRun;

    public bool IsReportOnly => OneClickActionNames.Normalize(_context.Action) == OneClickActionNames.Report;

    public IReadOnlyList<OneClickFlowStep> FlowSteps
    {
        get
        {
            List<string> steps = ["Check latest version", "Check ConfigMgr", "Check CVEs"];
            if (!IsReportOnly) steps.Add("Stage");
            if (IsPublishRun) steps.AddRange(["Package", "Publish"]);
            return steps.Select((text, i) => new OneClickFlowStep(text, i > 0)).ToList();
        }
    }

    private string SiteCode => _context.PlannedSite.Split('|')[0];

    private string SiteWords => SiteCode.Length > 0 ? "Site " + SiteCode : "";

    /// <summary>"2 applications included · 1 skipped · ConfigMgr Site MCM".</summary>
    public string PlanLine
    {
        get
        {
            var included = Rows.Count(r => r.Include);
            var parts = new List<string> { $"{included} {(included == 1 ? "application" : "applications")} included", $"{Rows.Count - included} skipped" };
            if (SiteCode.Length > 0) parts.Add("ConfigMgr Site " + SiteCode);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The systems beside the action of a publish run: "ConfigMgr enabled", "Intune off".</summary>
    public IReadOnlyList<OneClickSystemSignal> SystemSignals
    {
        get
        {
            var inUse = _context.SystemsText;
            var on = inUse.StartsWith("In use: ", StringComparison.Ordinal) ? inUse[8..].Split('.')[0] : "";
            return new[] { OneClickDestinations.ConfigMgr, OneClickDestinations.Intune, OneClickDestinations.Wsus }
                .Select(s => on.Contains(s, StringComparison.Ordinal)
                    ? new OneClickSystemSignal("checkmark_circle", "good", s + " enabled")
                    : new OneClickSystemSignal("clock", "", s + " off")).ToList();
        }
    }

    private bool InUse(string system) => SystemSignals.Any(s => s.Kind == "good" && s.Text.StartsWith(system + " ", StringComparison.Ordinal));

    /// <summary>A destination box is off while its system is not in use.</summary>
    public bool ConfigMgrInUse => _context.SystemsText.Length == 0 || InUse(OneClickDestinations.ConfigMgr);

    public bool WsusInUse => _context.SystemsText.Length == 0 || InUse(OneClickDestinations.Wsus);

    public bool IntuneInUse => _context.SystemsText.Length == 0 || InUse(OneClickDestinations.Intune);

    /// <summary>"ConfigMgr checks use Site MCM", beside the action of a run that does not publish.</summary>
    public string CheckSiteText => SiteCode.Length > 0 ? $"ConfigMgr checks use {SiteWords}" : "";

    /// <summary>The banner of a run that does not publish.</summary>
    public string CheckBanner => IsReportOnly
        ? "Check only. This run checks versions and CVEs. It makes no changes to ConfigMgr, Intune or WSUS."
        : "Check and stage. This run checks versions and CVEs and stages the content. It makes no changes to ConfigMgr, Intune or WSUS.";

    public string FootNote => IsPublishRun
        ? "Intune and WSUS must be enabled in Connections before a run can use them."
        : "Inclusion applies to this run. Use One Click settings to edit tracking.";

    public string ForceText => IsPublishRun ? "Force recheck and republish eligible applications" : "Force recheck and ignore cadence";

    public string RunText => IsPublishRun ? "Run One Click" : IsReportOnly ? "Run checks" : "Run checks and stage";

    /// <summary>The whole of what the older window said above the grid: the saved action, its scope and the note on this run.</summary>
    public string PlanDetail => string.Join(Environment.NewLine + Environment.NewLine,
        new[] { SummaryText, ActionText, ScopeText, SystemsText, HintText }.Where(t => t.Length > 0));

    private void RaisePresentation()
    {
        OnPropertyChanged(nameof(PlanLine));
        OnPropertyChanged(nameof(PlanDetail));
    }
}
