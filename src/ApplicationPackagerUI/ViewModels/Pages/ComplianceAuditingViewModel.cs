using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The Auditing tab: Drift (the catalog version in each baseline's tag against the shipped
/// catalog) and Compliance (counts per baseline and collection with the Stale flag), with filters
/// and CSV and HTML export of the rows shown. One read gets both lists.
/// </summary>
public sealed partial class ComplianceAuditingViewModel(ComplianceShared shared) : ComplianceTabViewModel(shared)
{
    public const string ReadFailedText = "The drift and compliance data could not be read. The log has the details.";
    public const string NothingToExport = "The list has no rows to export.";
    public const int DriftTab = 0;
    public const int ComplianceTab = 1;
    public const string AllStates = "";

    public static string ReportFailed(string format) => $"The {format} report could not be written. The log has the details.";

    private IReadOnlyList<DriftRow> _drift = [];
    private IReadOnlyList<ComplianceRow> _compliance = [];
    private bool _applying;

    protected override string ReadFailed => ReadFailedText;

    public ObservableCollection<DriftGridRow> DriftRows { get; } = [];
    public ObservableCollection<ComplianceGridRow> ComplianceRows { get; } = [];

    public IReadOnlyList<Choice<string>> DriftStates { get; } =
        [new(AllStates, "All states"), new("Current", "Current"), new("Behind", "Behind"), new("Untagged", "Untagged"), new("Unknown", "Unknown")];

    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private string _textFilter = "";
    [ObservableProperty] private string _driftState = AllStates;
    [ObservableProperty] private bool _onlyStale;
    [ObservableProperty] private string _driftCount = "";
    [ObservableProperty] private string _complianceCount = "";

    [ObservableProperty] private DriftGridRow? _selectedDrift;
    [ObservableProperty] private ComplianceGridRow? _selectedCompliance;

    public event EventHandler? ChoicesChanged;

    partial void OnSelectedTabIndexChanged(int value) => Changed();
    partial void OnTextFilterChanged(string value) => Changed();
    partial void OnDriftStateChanged(string value) => Changed();
    partial void OnOnlyStaleChanged(bool value) => Changed();

    private void Changed()
    {
        if (_applying) return;
        ApplyFilter();
        ChoicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyChoices(ComplianceSettings saved)
    {
        _applying = true;
        try
        {
            SelectedTabIndex = saved.AuditingTab == ComplianceTab ? ComplianceTab : DriftTab;
            TextFilter = saved.AuditingText ?? "";
            DriftState = DriftStates.Any(s => s.Value == saved.DriftState) ? saved.DriftState : AllStates;
            OnlyStale = saved.OnlyStale;
        }
        finally { _applying = false; }
    }

    public void StoreChoices(ComplianceSettings choices)
    {
        choices.AuditingTab = SelectedTabIndex;
        choices.AuditingText = TextFilter;
        choices.DriftState = DriftState;
        choices.OnlyStale = OnlyStale;
    }

    protected override async Task<Action> ReadAsync(CancellationToken ct)
    {
        var drift = await Services.Compliance.GetDriftAsync(ct);
        var compliance = await Services.Compliance.GetComplianceAsync(ct);
        return () =>
        {
            _drift = drift;
            _compliance = compliance;
            ApplyFilter();
            Services.Log.Info(ComplianceShared.LogArea, $"Read {drift.Count} drift rows and {compliance.Count} compliance rows.");
        };
    }

    protected override void ReleaseData()
    {
        _drift = [];
        _compliance = [];
        SelectedDrift = null;
        SelectedCompliance = null;
        DriftRows.Clear();
        ComplianceRows.Clear();
        DriftCount = "";
        ComplianceCount = "";
    }

    private void ApplyFilter()
    {
        var text = TextFilter.Trim();
        Replace(DriftRows, _drift.Where(d => (DriftState == AllStates || string.Equals(d.State, DriftState, StringComparison.OrdinalIgnoreCase))
                && (text.Length == 0 || Contains(d.BaselineName, text) || Contains(d.BaselineId, text) || Contains(d.CatalogId, text)))
            .OrderBy(d => d.BaselineName, StringComparer.OrdinalIgnoreCase).Select(d => new DriftGridRow(d)).ToList());
        Replace(ComplianceRows, _compliance.Where(c => (!OnlyStale || c.Stale) && (text.Length == 0 || Contains(c.BaselineName, text) || Contains(c.CollectionName, text)))
            .OrderBy(c => c.BaselineName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.CollectionName, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ComplianceGridRow(c)).ToList());
        DriftCount = DriftRows.Count == _drift.Count ? ComplianceText.Count(_drift.Count, "baseline") : $"{DriftRows.Count} of {ComplianceText.Count(_drift.Count, "baseline")}";
        var stale = _compliance.Count(c => c.Stale);
        ComplianceCount = (ComplianceRows.Count == _compliance.Count ? ComplianceText.Count(_compliance.Count, "row") : $"{ComplianceRows.Count} of {ComplianceText.Count(_compliance.Count, "row")}")
            + $", {stale} stale";
    }

    [RelayCommand]
    private Task ExportCsv() => ExportAsync("CSV", Services.Reports.WriteCsv);

    [RelayCommand]
    private Task ExportHtml() => ExportAsync("HTML", Services.Reports.WriteHtml);

    /// <summary>Writes the rows of the shown sub-tab with its filters applied.</summary>
    private async Task ExportAsync(string format, Func<IReadOnlyList<DriftRow>, IReadOnlyList<ComplianceRow>, string> write)
    {
        IReadOnlyList<DriftRow> drift = SelectedTabIndex == DriftTab ? DriftRows.Select(r => r.Row).ToList() : [];
        IReadOnlyList<ComplianceRow> compliance = SelectedTabIndex == ComplianceTab ? ComplianceRows.Select(r => r.Row).ToList() : [];
        if (drift.Count + compliance.Count == 0)
        {
            Shared.StatusMessage = NothingToExport;
            return;
        }
        string path;
        try { path = await Task.Run(() => write(drift, compliance)); }
        catch (Exception ex)
        {
            Shared.StatusMessage = ex is ComplianceServiceException c ? SecretMask.Apply(c.Message) : ReportFailed(format);
            Services.Log.Warning(ComplianceShared.LogArea, $"The {format} report could not be written ({ex.GetType().Name}).");
            return;
        }
        Shared.StatusMessage = $"Saved the {format} report to {path}.";
        Services.Log.Info(ComplianceShared.LogArea, $"Saved a {format} report with {drift.Count + compliance.Count} rows.");
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder) Services.Dialogs.OpenFolder(folder);
    }
}
