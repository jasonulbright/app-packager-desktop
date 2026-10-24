using System.Globalization;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>One scan area of the source tool: its id, as the service takes it, and its title.</summary>
public sealed record HygieneArea(string Id, string Title);

/// <summary>The words, keys and counts of the Site Hygiene page.</summary>
public static class SiteHygieneLabels
{
    public const string Ran = HygieneCheckStates.Ran;
    public const string NotRun = HygieneCheckStates.NotRun;
    public const string NotSelected = HygieneCheckStates.NotSelected;
    public const string NeedsSiteDatabase = HygieneCheckStates.NeedsSiteDatabase;
    public const string Failed = HygieneCheckStates.Failed;
    public const string Unknown = "Unknown";
    public const string Supersedes = RelationKinds.Supersedes;
    public const string DependsOn = RelationKinds.DependsOn;

    /// <summary>The scan areas of the service, in its order.</summary>
    public static readonly IReadOnlyList<HygieneArea> Areas = HygieneAreas.All.Select(a => new HygieneArea(a.Id, a.Title)).ToList();

    /// <summary>The severities from the most severe down, as the filter and the summary list them.</summary>
    public static readonly IReadOnlyList<HygieneSeverity> Severities = [HygieneSeverity.Error, HygieneSeverity.Warning, HygieneSeverity.Info];

    /// <summary>The key of a finding: the check and the stable object key, never the display name.</summary>
    public static string Key(HygieneFinding finding) => Key(finding.CheckId, finding.ObjectKey);

    /// <summary>The key of <see cref="HygieneFinding.KeyOf"/> in upper case, so an ordinal compare matches the case-insensitive compare of the stores.</summary>
    public static string Key(string checkId, string objectKey) => HygieneFinding.KeyOf(checkId, objectKey).ToUpperInvariant();

    public static string Number(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Count(int count, string singular, string? plural = null) =>
        $"{Number(count)} {(count == 1 ? singular : plural ?? singular + "s")}";

    public static string SeverityName(HygieneSeverity severity) => severity switch
    {
        HygieneSeverity.Error => "Error",
        HygieneSeverity.Warning => "Warning",
        _ => "Info"
    };

    /// <summary>A count of findings of one severity for the summary line, for example "2 errors" or "1 info".</summary>
    public static string SeverityCount(HygieneSeverity severity, int count) => severity switch
    {
        HygieneSeverity.Error => Count(count, "error"),
        HygieneSeverity.Warning => Count(count, "warning"),
        _ => $"{Number(count)} info"
    };

    /// <summary>The glyph and the word: the word carries the meaning, the glyph helps the eye.</summary>
    public static string SeverityText(HygieneSeverity severity) => severity switch
    {
        HygieneSeverity.Error => "✖ Error",
        HygieneSeverity.Warning => "▲ Warning",
        _ => "○ Info"
    };

    public static bool IsDelete(HygieneFixKind kind) => HygieneFixKinds.IsDelete(kind);

    /// <summary>True when the service runs the fix kind; every other kind, None included, is report-only.</summary>
    public static bool CanRun(HygieneFixKind kind) => SiteHygieneService.SupportedKinds.Contains(kind);

    public static bool IsFixable(HygieneFinding finding) => CanRun(finding.FixKind);

    /// <summary>The Fix column: the fix of a kind the service runs, otherwise "Report only"; the detail pane names the kind and the reason.</summary>
    public static string FixColumn(HygieneFixKind kind) => CanRun(kind) ? FixLabel(kind) : FixLabel(HygieneFixKind.None);

    /// <summary>Why a finding has no fix on this page; empty for a finding with a fix.</summary>
    public static string ReportOnlyReason(HygieneFixKind kind) =>
        CanRun(kind) ? ""
        : kind == HygieneFixKind.None ? "This finding has no typed fix. Use the recommended action in the ConfigMgr console."
        : $"This page does not run the fix \"{FixLabel(kind).ToLowerInvariant()}\": no check of this page proves it safe. Use the recommended action in the ConfigMgr console.";

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
        NotSelected => "Not selected",
        NeedsSiteDatabase => "Needs the site database",
        Failed => "Failed",
        _ => state
    };

    public static bool DidRun(HygieneCheckStatus check) => string.Equals(check.State, Ran, StringComparison.OrdinalIgnoreCase);

    public static bool WasNotSelected(HygieneCheckStatus check) => string.Equals(check.State, NotSelected, StringComparison.OrdinalIgnoreCase);

    /// <summary>A check of a selected area that did not run: its read failed or was cut, it needs the site database, or it threw.</summary>
    public static bool IsNotRun(HygieneCheckStatus check) => !DidRun(check) && !WasNotSelected(check);

    public static string KindText(string kind) => string.Equals(kind, DependsOn, StringComparison.OrdinalIgnoreCase) ? "Depends on" : string.Equals(kind, Supersedes, StringComparison.OrdinalIgnoreCase) ? "Supersedes" : kind;

    /// <summary>The health of an edge as a glyph and its word; an empty health is not known.</summary>
    public static string HealthText(string? health)
    {
        var word = string.IsNullOrWhiteSpace(health) ? Unknown : health.Trim();
        var glyph = word.ToUpperInvariant() switch
        {
            "HEALTHY" or "OK" => "✓",
            "DELETED" or "MISSING" or "ORPHANED" or "CYCLE" or "CIRCULAR" or "ERROR" => "✖",
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
    public string Fix => SiteHygieneLabels.FixColumn(Finding.FixKind);
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
public sealed record SummaryCategoryRow(string Category, string Error, string Warning, string Info, string Total, string NotRun);

public sealed class RelationEdgeRow(RelationEdge edge)
{
    public RelationEdge Edge { get; } = edge;
    public string Key { get; } = $"{edge.Kind}|{edge.FromKey}|{edge.ToKey}".ToUpperInvariant();
    public string Kind => SiteHygieneLabels.KindText(Edge.Kind);
    public string From => $"{Edge.FromName} ({Edge.FromKey})";
    public string To => $"{Edge.ToName} ({Edge.ToKey})";
    public string Health => SiteHygieneLabels.HealthText(Edge.Health);
}
