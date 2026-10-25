namespace ApplicationPackagerUI.Models;

/// <summary>The last tabs, the filters, the scan scope, the scan options and the sorts of the Site Hygiene page. No value is a secret or object text.</summary>
public sealed class SiteHygieneSettings
{
    /// <summary>The tab order of <see cref="LiveTabLayout"/>: 0 Findings, 1 Relationships, 2 Live, 3 Summary, 4 History, 5 Options.</summary>
    public const int LiveTabLayout = 2;

    /// <summary>
    /// The tab order <see cref="SelectedTab"/> counts in. Null in a file written before the Live tab existed, where the
    /// order is 0 Findings, 1 Relationships, 2 Summary, 3 Options, 4 History.
    /// </summary>
    public int? TabLayout { get; set; }

    public int SelectedTab { get; set; }

    public string? HistoryText { get; set; } = "";

    /// <summary>0 Inventory, 1 Tree.</summary>
    public int RelationsTab { get; set; }

    public string? FindingText { get; set; } = "";
    public string? Category { get; set; } = "";
    public string? Severity { get; set; } = "";
    public bool ShowSuppressed { get; set; }
    public string? EdgeText { get; set; } = "";
    public string? EdgeKind { get; set; } = "";

    /// <summary>The scan area ids; null or empty scans every area.</summary>
    public List<string>? Areas { get; set; }

    /// <summary>The pause between provider calls in milliseconds.</summary>
    public int PauseMilliseconds { get; set; }

    public bool ProbeContentSources { get; set; }

    /// <summary>The sorted column of each grid, as "column|Ascending" or "column|Descending", by grid name.</summary>
    public Dictionary<string, string>? Sorts { get; set; }

    /// <summary>The id of the Live view shown; null shows the first view.</summary>
    public string? LiveView { get; set; }

    public string? LiveStatus { get; set; }

    public string? LiveText { get; set; }

    /// <summary>The days the Trends chart shows; null shows 30.</summary>
    public int? TrendDays { get; set; }

    public string? TrendMetric { get; set; }

    /// <summary>True once the operator answered the dashboard import offer; the offer then shows no more.</summary>
    public bool ImportOfferDismissed { get; set; }
}
