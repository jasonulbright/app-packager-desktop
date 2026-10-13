namespace ApplicationPackagerUI.Models;

public partial class PackagerInfo
{
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
}
