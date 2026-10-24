namespace ApplicationPackagerUI.Models;

/// <summary>The last tabs, the filters, the scan scope, the scan options and the sorts of the Site Hygiene page. No value is a secret or object text.</summary>
public sealed class SiteHygieneSettings
{
    /// <summary>0 Findings, 1 Relationships, 2 Summary, 3 Options.</summary>
    public int SelectedTab { get; set; }

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
}
