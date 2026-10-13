using System.Globalization;
using ApplicationPackagerEngine.Services.Vulnerability;

namespace ApplicationPackagerUI.Models;

public partial class PackagerInfo
{
    /// <summary>The compare status of a row whose vendor version is newer; a CVE count can follow it in parentheses.</summary>
    public const string UpdateAvailableStatus = "Update available";

    /// <summary>CPE 2.3 name from the definition or the packager header; empty when it has none.</summary>
    public string Cpe { get; init; } = "";
    /// <summary>The vendor's security advisory page from the definition or the packager header.</summary>
    public string AdvisoryUrl { get; init; } = "";

    /// <summary>The CVE count, or the reason there is none: "CPE missing", "Error", "No update".</summary>
    [ObservableProperty] private string _cveCount = "";
    [ObservableProperty] private string _cveCvss = "";
    [ObservableProperty] private string _cveKev = "";
    [ObservableProperty] private string _cveEpss = "";
    /// <summary>UTC time of the lookup the cells show.</summary>
    [ObservableProperty] private string _cveChecked = "";
    [ObservableProperty] private string _cveFixed = "";
    [ObservableProperty] private string _cveNewest = "";
    [ObservableProperty] private string _cveUpdated = "";

    // The cells are text; a text sort puts 10 before 9. Each key is the number the cell
    // starts with, and -1 for a cell without one.
    public int CveCountSort => (int)LeadingNumber(CveCount);
    public double CveCvssSort => LeadingNumber(CveCvss);
    public int CveFixedSort => (int)LeadingNumber(CveFixed);
    public double CveEpssSort => LeadingNumber(CveEpss);

    /// <summary>"3 CVEs, 1 in KEV" for the version pair the cells describe; empty when the cells carry no count.</summary>
    public string CveStatusText { get; private set; } = "";

    private (string Current, string Latest)? _cveCellVersions;

    /// <summary>Shows the cells and takes the row's versions as the pair they describe.</summary>
    public void SetCveCells(VulnerabilityGridCells cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        CveCount = cells.Count;
        CveCvss = cells.Cvss;
        CveKev = cells.Kev;
        CveEpss = cells.Epss;
        CveChecked = cells.Checked;
        CveFixed = cells.Fixed;
        CveNewest = cells.Newest;
        CveUpdated = cells.Updated;
        CveStatusText = cells.Text;
        _cveCellVersions = (CurrentVersion, LatestVersion);
    }

    partial void OnCurrentVersionChanged(string value) => DropCveCellsOfAnotherVersionPair();

    partial void OnLatestVersionChanged(string value) => DropCveCellsOfAnotherVersionPair();

    /// <summary>
    /// A count describes one ConfigMgr version and one vendor version. When either changes,
    /// the cells and the count on the status go; the cells of a row without a CPE say so
    /// for every pair and stay.
    /// </summary>
    private void DropCveCellsOfAnotherVersionPair()
    {
        if (_cveCellVersions is not { } shown || (shown.Current == CurrentVersion && shown.Latest == LatestVersion)) return;
        SetCveCells(VulnerabilityGridCells.From(null, Cpe));
        if (Status.StartsWith(UpdateAvailableStatus, StringComparison.Ordinal)) Status = UpdateAvailableStatus;
    }

    private static double LeadingNumber(string? text)
    {
        var value = text ?? "";
        var length = 0;
        while (length < value.Length && (char.IsAsciiDigit(value[length]) || value[length] == '.')) length++;
        return length > 0 && double.TryParse(value.AsSpan(0, length), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
            ? number
            : -1;
    }
}
