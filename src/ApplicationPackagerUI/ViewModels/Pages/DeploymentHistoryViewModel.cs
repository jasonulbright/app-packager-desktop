using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The History tab: the records of the audit log with filters, CSV and HTML export of the rows the
/// filters show, and Remove deployment for a create record that carries its deployment ID. The
/// service decides whether the deployment can still be removed.
/// </summary>
public sealed partial class DeploymentHistoryViewModel(DeploymentShared shared) : DeploymentTabViewModel(shared)
{
    public const int MaxRecords = 2000;
    public const string AllText = "All";
    public const string ReadFailedText = "The audit log could not be read. The log has the details.";
    public const string NothingToExport = "The list has no rows to export.";
    public const string NotLoaded = "The audit log was not read. Select Refresh, then export.";
    public const string NoDeploymentId = "Select a create record that names its deployment ID.";
    public const string UnknownType = "The record does not name a known object type, so the deployment cannot be removed from here.";
    public static readonly string CappedNote = $"The list shows the newest {MaxRecords} records of the audit log. Older records are in the file.";
    public static string ReportFailed(string format) => $"The {format} report could not be written. The log has the details.";

    private IReadOnlyList<DeploymentAuditRecord> _records = [];

    protected override bool NeedsSite => false;
    protected override string ReadFailed => ReadFailedText;

    public ObservableCollection<DeploymentHistoryRow> Rows { get; } = [];
    public IReadOnlyList<string> Events { get; } = [AllText, "Create", "Distribute", "Undo"];
    public IReadOnlyList<string> Types { get; } = [AllText, .. Enum.GetValues<DeploymentObjectType>().Select(t => t.ToString())];

    [ObservableProperty] private string _textFilter = "";
    [ObservableProperty] private string _eventFilter = AllText;
    [ObservableProperty] private string _typeFilter = AllText;
    [ObservableProperty] private string _rowCount = "";
    [ObservableProperty] private string _logPath = "";

    /// <summary>True when the read returned the most records it reads, so older records may be left out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CapNote))]
    private bool _isCapped;

    public string CapNote => IsCapped ? CappedNote : "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveDeploymentCommand))]
    private DeploymentHistoryRow? _selectedRow;

    /// <summary>The folder of the last report written; null before the first export.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReportsFolderCommand))]
    private string? _lastReportFolder;

    partial void OnTextFilterChanged(string value) => ApplyFilter();
    partial void OnEventFilterChanged(string value) => ApplyFilter();
    partial void OnTypeFilterChanged(string value) => ApplyFilter();

    protected override Task<Action> ReadAsync(CancellationToken ct)
    {
        var records = Services.AuditLog.Read(MaxRecords);
        var path = Services.AuditLog.Path;
        return Task.FromResult<Action>(() =>
        {
            _records = records;
            LogPath = path;
            IsCapped = records.Count >= MaxRecords;
            ApplyFilter();
            Services.Log.Info(DeploymentShared.LogArea, $"Read {records.Count} audit records.");
        });
    }

    protected override void ReleaseData()
    {
        _records = [];
        SelectedRow = null;
        Rows.Clear();
        RowCount = "";
        LogPath = "";
        IsCapped = false;
        TextFilter = "";
        EventFilter = AllText;
        TypeFilter = AllText;
        LastReportFolder = null;
    }

    private static bool SameType(string recorded, string wanted) => TypeOf(recorded) is { } type && TypeOf(wanted) == type;

    private void ApplyFilter()
    {
        var selected = SelectedRow?.Record;
        var text = TextFilter.Trim();
        var wanted = _records
            .Where(r => EventFilter == AllText || string.Equals(r.Event, EventFilter, StringComparison.OrdinalIgnoreCase))
            .Where(r => TypeFilter == AllText || SameType(r.DeploymentType, TypeFilter))
            .Where(r => text.Length == 0 || Contains(r.ApplicationName, text) || Contains(r.CollectionName, text) || Contains(r.CollectionID, text)
                || Contains(r.DeploymentID, text) || Contains(r.DeploymentUniqueID, text) || Contains(r.User, text) || Contains(r.Result, text) || Contains(r.ObjectId, text))
            .OrderByDescending(r => r.Timestamp).ToList();
        var keep = Rows.Where(r => wanted.Contains(r.Record)).ToDictionary(r => r.Record);
        Replace(Rows, wanted.Select(r => keep.TryGetValue(r, out var row) ? row : new DeploymentHistoryRow(r)).ToList());
        SelectedRow = Rows.FirstOrDefault(r => ReferenceEquals(r.Record, selected));
        RowCount = Rows.Count == _records.Count ? DeploymentText.Count(_records.Count, "record") : $"{Rows.Count} of {DeploymentText.Count(_records.Count, "record")}";
    }

    [RelayCommand]
    private Task ExportCsv() => ExportAsync("CSV", Services.Reports.WriteCsv);

    [RelayCommand]
    private Task ExportHtml() => ExportAsync("HTML", Services.Reports.WriteHtml);

    private bool CanOpenReportsFolder() => LastReportFolder is not null;

    [RelayCommand(CanExecute = nameof(CanOpenReportsFolder))]
    private void OpenReportsFolder()
    {
        if (LastReportFolder is { } folder) Services.Dialogs.OpenFolder(folder);
    }

    /// <summary>Writes the rows the filters show; refuses when the last read did not succeed.</summary>
    private async Task ExportAsync(string format, Func<IReadOnlyList<DeploymentAuditRecord>, string> write)
    {
        if (!IsLoaded)
        {
            Shared.StatusMessage = NotLoaded;
            return;
        }
        IReadOnlyList<DeploymentAuditRecord> rows = Rows.Select(r => r.Record).ToList();
        if (rows.Count == 0)
        {
            Shared.StatusMessage = NothingToExport;
            return;
        }
        string path;
        try { path = await Task.Run(() => write(rows)); }
        catch (Exception ex)
        {
            Shared.StatusMessage = ex is DeploymentServiceException d ? SecretMask.Apply(d.Message) : ReportFailed(format);
            Services.Log.Warning(DeploymentShared.LogArea, $"The {format} report could not be written ({ex.GetType().Name}).");
            return;
        }
        Shared.StatusMessage = $"Saved the {format} report to {path}: {DeploymentText.Count(rows.Count, "record")}, the rows the filters show." + (IsCapped ? " " + CappedNote : "");
        Services.Log.Info(DeploymentShared.LogArea, $"Saved a {format} report with {rows.Count} rows.");
        LastReportFolder = Path.GetDirectoryName(path) is { Length: > 0 } folder ? folder : null;
    }

    public static DeploymentObjectType? TypeOf(string recorded) =>
        recorded.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "application" or "app" or "apps" => DeploymentObjectType.Application,
            "package" or "packages" => DeploymentObjectType.Package,
            "tasksequence" or "tasksequences" => DeploymentObjectType.TaskSequence,
            "softwareupdategroup" or "softwareupdategroups" or "sug" => DeploymentObjectType.SoftwareUpdateGroup,
            _ => null
        };

    /// <summary>
    /// The captured deployment of a create record, for the service to compare before it removes
    /// anything; the only place the page turns an audit record into a removal. The ID is the unique
    /// ID the record names; DeploymentID of an application or update group record is the numeric
    /// assignment ID, which no removal takes.
    /// </summary>
    public static DeploymentRecord? Captured(DeploymentAuditRecord record)
    {
        var id = record.DeploymentUniqueID.Length > 0 ? record.DeploymentUniqueID : record.DeploymentID;
        if (TypeOf(record.DeploymentType) is not { } type || id.Length == 0) return null;
        return new DeploymentRecord(DeploymentId: id, Type: type, ObjectId: record.ObjectId, ObjectName: record.ApplicationName,
            CollectionId: record.CollectionID, CollectionName: record.CollectionName, Purpose: record.DeployPurpose,
            AvailableTime: record.AvailableDateTime ?? record.Timestamp, Deadline: record.DeadlineDateTime, Created: record.Timestamp, CreatedBy: record.User,
            UtcBasis: string.Equals(record.TimeBasedOn, "Utc", StringComparison.OrdinalIgnoreCase), Program: record.ProgramName,
            AssignmentId: record.DeploymentUniqueID.Length > 0 && record.DeploymentID != record.DeploymentUniqueID ? record.DeploymentID : "");
    }

    private bool CanRemoveDeployment() => SelectedRow is not null;

    /// <summary>Shows the plan that removes the deployment of the selected create record.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveDeployment))]
    private async Task RemoveDeployment()
    {
        if (SelectedRow is not { } row) return;
        if (!row.CanUndo)
        {
            Shared.StatusMessage = NoDeploymentId;
            return;
        }
        if (Captured(row.Record) is not { } existing)
        {
            Shared.StatusMessage = UnknownType;
            return;
        }
        var request = new DeploymentChange(Kind: DeploymentChangeKind.RemoveDeployment, Object: null, Collection: null, Options: null, DpGroups: [],
            CreateNowContentDistributing: false, RequiredConfirmed: false, BackdatedAcknowledged: false, Existing: existing, Checks: [], Impact: [],
            CreatedId: existing.DeploymentId);
        var run = await Shared.RunPlanAsync([request], prepared => DeploymentText.BuildRemovePlan(prepared[0]));
        if (run is not { Outcome.Confirmed: true }) return;
        await Shared.AfterWriteAsync();
    }
}
