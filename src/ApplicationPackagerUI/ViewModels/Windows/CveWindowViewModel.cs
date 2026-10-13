using ApplicationPackagerEngine.Services.Vulnerability;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.ViewModels.Windows;

public sealed record CveLink(string Name, string Url);

/// <summary>One CVE as the window lists it.</summary>
public sealed record CveWindowRow(string Id, string Cvss, string Epss, string Kev, string FixedIn, string Published,
    string Modified, string Summary, IReadOnlyList<CveLink> Links);

/// <summary>The CVE list of one application row: what the cached lookup holds, and why when it holds nothing.</summary>
public partial class CveWindowViewModel : ObservableObject
{
    public const string NotChecked = "Not checked. Run Check ConfigMgr, Check Latest and Check CVEs for this application.";
    public const string RefreshNeedsVersions = "Refresh needs a CPE, a ConfigMgr version and a latest version.";
    public const string AnotherRunInProgress = "Another run is in progress. Wait for it to finish.";
    public const string LookingUp = "Looking up...";
    public const string LookupFailed = "The lookup failed; the last good result is shown. See the log.";

    public PackagerInfo Row { get; }
    public string Title { get; }
    public ObservableCollection<CveLink> ProductLinks { get; } = [];
    public ObservableCollection<CveWindowRow> Rows { get; } = [];

    [ObservableProperty] private string _rangeText = "";
    [ObservableProperty] private string _noteText = "";
    [ObservableProperty] private bool _hasNote;
    [ObservableProperty] private bool _canRefresh = true;

    /// <summary>The result the window shows; null when no lookup exists for the row's version pair.</summary>
    public VulnerabilityResult? Result { get; private set; }

    public CveWindowViewModel(PackagerInfo row, VulnerabilityResult? result)
    {
        Row = row ?? throw new ArgumentNullException(nameof(row));
        Title = string.IsNullOrWhiteSpace(row.Vendor) ? row.Application : $"{row.Application} ({row.Vendor})";
        Render(result);
    }

    public bool CanLookUp =>
        !string.IsNullOrWhiteSpace(Row.Cpe) && !string.IsNullOrWhiteSpace(Row.CurrentVersion) && !string.IsNullOrWhiteSpace(Row.LatestVersion);

    public IReadOnlyList<string> CveIds => Result?.Cves.Select(c => c.Id).ToList() ?? [];

    public void ShowNote(string? text)
    {
        NoteText = text ?? "";
        HasNote = NoteText.Length > 0;
    }

    public void Render(VulnerabilityResult? result)
    {
        Result = result;
        ProductLinks.Clear();
        Rows.Clear();
        if (result is null)
        {
            RangeText = $"ConfigMgr {Dash(Row.CurrentVersion)}, latest {Dash(Row.LatestVersion)}";
            var reason = Row.CveCount;
            ShowNote(reason.Length == 0 || reason.All(char.IsAsciiDigit) ? NotChecked : reason);
            foreach (var link in Links(VulnerabilityLinks.Product(Row.Cpe, Row.CurrentVersion, Row.AdvisoryUrl))) ProductLinks.Add(link);
            return;
        }

        var unfixed = result.UnfixedCount > 0 ? $" {result.UnfixedCount} more still affect {result.To}." : "";
        var fixText = VulnerabilityText.FixText(result);
        if (fixText.Length > 0) fixText = $" {fixText}.";
        var dates = result.NewestPublished.Length > 0 ? $" Newest CVE {result.NewestPublished}, NVD updated {result.LastNvdUpdate}." : "";
        RangeText = $"{VulnerabilityText.CountText(result.Count, result.KevCount)} fixed by updating from {result.From} to {result.To}. "
                    + $"Source {Dash(result.Source)}, checked {result.QueriedAt}.{unfixed}{fixText}{dates}";
        ShowNote(result.Error);
        foreach (var link in Links(result.Links)) ProductLinks.Add(link);
        foreach (var row in BuildRows(result)) Rows.Add(row);
    }

    public static List<CveWindowRow> BuildRows(VulnerabilityResult result)
    {
        var rows = new List<CveWindowRow>();
        foreach (var c in result.Cves)
        {
            var links = Links(c.Links);
            if (c.References.Count > 0 && VulnerabilityText.IsHttps(c.References[0])) links.Add(new CveLink("Reference", c.References[0]));
            rows.Add(new CveWindowRow(
                c.Id,
                VulnerabilityText.CvssText(c.Score, c.Severity),
                VulnerabilityText.PercentText(c.Epss),
                c.Kev ? "Yes" : "",
                c.FixedIn + (c.FixedInLatest ? " (latest)" : ""),
                VulnerabilityText.DayText(c.Published),
                VulnerabilityText.DayText(c.LastModified),
                c.Summary,
                links));
        }
        return rows;
    }

    private static List<CveLink> Links(OrderedDictionary<string, string> links) =>
        links.Where(l => VulnerabilityText.IsHttps(l.Value)).Select(l => new CveLink(l.Key, l.Value)).ToList();

    private static string Dash(string? value) => string.IsNullOrEmpty(value) ? "-" : value;
}
