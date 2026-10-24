using System.Globalization;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>One scan area of the source tool: its id, as the service takes it, and its title.</summary>
public sealed record HygieneArea(string Id, string Title);

/// <summary>The words, keys and counts of the Site Hygiene page.</summary>
public static class SiteHygieneLabels
{
    public const string Ran = "Ran";
    public const string NotRun = "NotRun";
    public const string NeedsSiteDatabase = "NeedsSiteDatabase";
    public const string Failed = "Failed";
    public const string Unknown = "Unknown";
    public const string Supersedes = "Supersedes";
    public const string DependsOn = "DependsOn";

    /// <summary>The scan areas in the order of the source tool.</summary>
    public static readonly IReadOnlyList<HygieneArea> Areas =
    [
        new("Applications", "Applications"),
        new("AppRelationships", "Application relationships and content paths"),
        new("Content", "Content distribution"),
        new("Packages", "Packages"),
        new("Collections", "Collections"),
        new("CollectionSchedules", "Collection evaluation schedules"),
        new("Deployments", "Deployments"),
        new("Devices", "Devices"),
        new("Boundaries", "Boundaries"),
        new("TaskSequences", "Task sequences"),
        new("Updates", "Software updates"),
        new("UpdateContent", "Software update package content"),
        new("DistributionPoints", "Distribution points"),
        new("Compliance", "Compliance and client settings"),
        new("Drivers", "Drivers"),
        new("Security", "Administrative users"),
        new("MaintenanceWindows", "Maintenance windows"),
        new("Site", "Site maintenance")
    ];

    /// <summary>The severities from the most severe down, as the filter and the summary list them.</summary>
    public static readonly IReadOnlyList<HygieneSeverity> Severities = [HygieneSeverity.High, HygieneSeverity.Medium, HygieneSeverity.Low, HygieneSeverity.Info];

    /// <summary>The key of a finding: the check and the stable object key, never the display name.</summary>
    public static string Key(HygieneFinding finding) => Key(finding.CheckId, finding.ObjectKey);

    public static string Key(string checkId, string objectKey) => checkId.ToUpperInvariant() + "|" + objectKey.ToUpperInvariant();

    public static string Number(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Count(int count, string singular, string? plural = null) =>
        $"{Number(count)} {(count == 1 ? singular : plural ?? singular + "s")}";

    public static string SeverityName(HygieneSeverity severity) => severity switch
    {
        HygieneSeverity.High => "High",
        HygieneSeverity.Medium => "Medium",
        HygieneSeverity.Low => "Low",
        _ => "Info"
    };

    /// <summary>The glyph and the word: the word carries the meaning, the glyph helps the eye.</summary>
    public static string SeverityText(HygieneSeverity severity) => severity switch
    {
        HygieneSeverity.High => "✖ High",
        HygieneSeverity.Medium => "▲ Medium",
        HygieneSeverity.Low => "● Low",
        _ => "○ Info"
    };

    public static bool IsDelete(HygieneFixKind kind) => kind.ToString().StartsWith("Remove", StringComparison.Ordinal);

    public static bool IsFixable(HygieneFinding finding) => finding.FixKind != HygieneFixKind.None;

    public static string FixLabel(HygieneFixKind kind) => kind switch
    {
        HygieneFixKind.None => "Report only",
        HygieneFixKind.RemoveApplication => "Delete application",
        HygieneFixKind.RemoveApplicationDeployment => "Delete deployment",
        HygieneFixKind.RemoveApplicationRevisionHistory => "Delete old revisions",
        HygieneFixKind.RemovePackage => "Delete package",
        HygieneFixKind.RemoveCollection => "Delete collection",
        HygieneFixKind.RemoveDevice => "Delete device",
        HygieneFixKind.RemoveBootImage => "Delete boot image",
        HygieneFixKind.RemoveDriverPackage => "Delete driver package",
        HygieneFixKind.RemoveAutoDeploymentRule => "Delete deployment rule",
        HygieneFixKind.RemoveMaintenanceWindow => "Delete maintenance window",
        HygieneFixKind.SetCollectionSchedule => "Change schedule",
        HygieneFixKind.SetCollectionRefreshType => "Change refresh type",
        HygieneFixKind.SetApplication => "Change application",
        HygieneFixKind.ResumeApplication => "Reinstate application",
        HygieneFixKind.SetBoundaryGroup => "Change boundary group",
        HygieneFixKind.SetSiteMaintenanceTask => "Change maintenance task",
        HygieneFixKind.RunAutoDeploymentRule => "Run deployment rule",
        _ => kind.ToString()
    };

    public static string StateText(string state) => state switch
    {
        Ran => "Ran",
        NotRun => "Not run",
        NeedsSiteDatabase => "Needs the site database",
        Failed => "Failed",
        _ => state
    };

    public static bool DidRun(HygieneCheckStatus check) => string.Equals(check.State, Ran, StringComparison.OrdinalIgnoreCase);

    public static string KindText(string kind) => string.Equals(kind, DependsOn, StringComparison.OrdinalIgnoreCase) ? "Depends on" : string.Equals(kind, Supersedes, StringComparison.OrdinalIgnoreCase) ? "Supersedes" : kind;

    /// <summary>The health of an edge as a glyph and its word; an empty health is not known.</summary>
    public static string HealthText(string? health)
    {
        var word = string.IsNullOrWhiteSpace(health) ? Unknown : health.Trim();
        var glyph = word.ToUpperInvariant() switch
        {
            "HEALTHY" or "OK" => "✓",
            "DELETED" or "MISSING" or "CYCLE" or "CIRCULAR" or "ERROR" => "✖",
            UnknownUpper => "?",
            _ => "!"
        };
        return $"{glyph} {word}";
    }

    private const string UnknownUpper = "UNKNOWN";

    public static string Time(DateTime utc) => utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}

public sealed class FindingGridRow(HygieneFinding finding, bool isSuppressed)
{
    public HygieneFinding Finding { get; } = finding;
    public string Key { get; } = SiteHygieneLabels.Key(finding);
    public bool IsSuppressed { get; } = isSuppressed;
    public string Severity => SiteHygieneLabels.SeverityText(Finding.Severity);
    public string CheckId => Finding.CheckId;
    public string Category => Finding.Category;
    public string ObjectName => Finding.ObjectName;
    public string ObjectText => $"{Finding.ObjectType} {Finding.ObjectName} ({Finding.ObjectKey})";
    public string Evidence => FirstLine(Finding.Evidence);
    public string Recommendation => FirstLine(Finding.Recommendation);
    public string New => Finding.IsNew ? "New" : "";
    public string Fix => SiteHygieneLabels.FixLabel(Finding.FixKind);
    public string Suppressed => IsSuppressed ? "Yes" : "";

    public static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end] + " …";
    }

    public override string ToString() => $"{CheckId} {ObjectText}";
}

public sealed class CheckStatusRow(HygieneCheckStatus check)
{
    public HygieneCheckStatus Check { get; } = check;
    public string CheckId => Check.CheckId;
    public string Name => Check.Name;
    public string Category => Check.Category;
    public string State => SiteHygieneLabels.StateText(Check.State);

    /// <summary>A check that did not run has no finding count: the cell says so instead of showing zero.</summary>
    public string Findings => SiteHygieneLabels.DidRun(Check) ? SiteHygieneLabels.Number(Check.Findings) : "Not known";

    public string Reason => Check.Reason ?? "";
}

/// <summary>One category of the Summary tab: the findings by severity, and the checks that did not run.</summary>
public sealed record SummaryCategoryRow(string Category, string High, string Medium, string Low, string Info, string Total, string NotRun);

public sealed class RelationEdgeRow(RelationEdge edge)
{
    public RelationEdge Edge { get; } = edge;
    public string Key { get; } = $"{edge.Kind}|{edge.FromKey}|{edge.ToKey}".ToUpperInvariant();
    public string Kind => SiteHygieneLabels.KindText(Edge.Kind);
    public string From => $"{Edge.FromName} ({Edge.FromKey})";
    public string To => $"{Edge.ToName} ({Edge.ToKey})";
    public string Health => SiteHygieneLabels.HealthText(Edge.Health);
}
