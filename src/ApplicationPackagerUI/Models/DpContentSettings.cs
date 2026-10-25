namespace ApplicationPackagerUI.Models;

/// <summary>The last tab, the filters and the sort of each grid of the DP Content Manager page. No value is a secret.</summary>
public sealed class DpContentSettings
{
    /// <summary>0 DPs, 1 Content, 2 Status Issues, 3 History.</summary>
    public int SelectedTab { get; set; }

    public string? DpText { get; set; } = "";
    public string? DpStatus { get; set; } = "";
    public string? ContentText { get; set; } = "";
    public string? ContentType { get; set; } = "";
    public string? IssueText { get; set; } = "";
    public string? IssueState { get; set; } = "";
    public string? HistoryText { get; set; } = "";
    public string? HistoryAction { get; set; } = "";

    /// <summary>The sorted column of each grid, as "column|Ascending" or "column|Descending", by grid name.</summary>
    public Dictionary<string, string>? Sorts { get; set; }
}
