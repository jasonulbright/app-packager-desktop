using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerUI.ViewModels.Pages;

public sealed partial class SiteHygieneViewModel
{
    public const int MaxHistoryRecords = 2000;
    public const string HistoryGrid = "history";
    public const string HistoryReadFailed = "The audit log could not be read. The log has the details.";
    public const string HistoryNoLog = "The audit log is not set up on this page.";
    public const string HistoryNotRead = "The audit log was not read yet. Open the History tab or select Refresh.";
    public static readonly string HistoryCappedNote = $"The list shows the newest {MaxHistoryRecords} records of the audit log. Older records are in the file.";
    public const string HistoryWindowNote = "The audit log is large, so the list shows only its last part. Older records are in the file.";

    private IReadOnlyList<HygieneAuditRecord> _records = [];
    private int _historyGeneration;
    private Task? _historyTask;

    /// <summary>True once the audit log was read and no fix ran since.</summary>
    public bool IsHistoryLoaded { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshHistoryCommand))]
    private bool _isHistoryLoading;

    [ObservableProperty] private string _historyText = "";
    [ObservableProperty] private IReadOnlyList<HygieneHistoryRow> _historyRows = [];
    [ObservableProperty] private string _historyCount = "";
    [ObservableProperty] private string _historyPath = "";
    [ObservableProperty] private string _historyCapText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryDetails), nameof(HasHistoryDetails))]
    private HygieneHistoryRow? _selectedHistory;

    /// <summary>The whole record of the selected row: whether the hash chain verifies it, the run, the message and the result of each object.</summary>
    public string HistoryDetails => SelectedHistory?.Details ?? "";

    public bool HasHistoryDetails => HistoryDetails.Length > 0;

    public Task HistoryTask => _historyTask ?? Task.CompletedTask;

    partial void OnHistoryTextChanged(string value) => Debounce(HistoryGrid, ApplyHistoryFilter);

    /// <summary>Reads the audit log when the History tab shows and the list is not read; it reads the file only, never the site.</summary>
    private Task EnsureHistoryAsync()
    {
        if (IsHistoryLoaded) return Task.CompletedTask;
        if (_historyTask is { IsCompleted: false } pending) return pending;
        var task = LoadHistoryAsync();
        _historyTask = task.IsCompleted ? null : task;
        return task;
    }

    private bool CanRefreshHistory() => !IsHistoryLoading;

    [RelayCommand(CanExecute = nameof(CanRefreshHistory))]
    private Task RefreshHistory() => LoadHistoryAsync();

    /// <summary>Reads the newest records of the audit log off the UI thread.</summary>
    public async Task LoadHistoryAsync()
    {
        var mine = ++_historyGeneration;
        if (Services.AuditLog is not { } audit)
        {
            HistoryCount = HistoryNoLog;
            return;
        }
        IsHistoryLoading = true;
        try
        {
            // One record more than the list holds tells a log of exactly the cap from a longer one.
            var (records, path, dropped) = await Task.Run(() =>
            {
                var read = audit.Read(MaxHistoryRecords + 1);
                return (read, audit.Path, audit.OlderRecordsDropped);
            });
            if (mine != _historyGeneration) return;
            var capped = records.Count > MaxHistoryRecords;
            _records = capped ? records.Skip(records.Count - MaxHistoryRecords).ToList() : records;
            HistoryPath = path;
            HistoryCapText = capped ? HistoryCappedNote : dropped ? HistoryWindowNote : "";
            IsHistoryLoaded = true;
            ApplyHistoryFilter();
            Services.Log.Info(LogArea, $"Read {records.Count} audit records.");
        }
        catch (Exception ex)
        {
            if (mine != _historyGeneration) return;
            StatusMessage = PlainMessage(ex, HistoryReadFailed);
            Services.Log.Warning(LogArea, $"The audit log read failed ({ex.GetType().Name}).");
        }
        finally
        {
            if (mine == _historyGeneration) IsHistoryLoading = false;
        }
    }

    /// <summary>A fix wrote records: the next visit of the History tab reads the log again, and a shown History tab reads it now.</summary>
    private void HistoryChanged()
    {
        IsHistoryLoaded = false;
        if (_active && SelectedTabIndex == HistoryTab) _ = LoadHistoryAsync();
    }

    private void ApplyHistoryFilter()
    {
        var selected = SelectedHistory?.Record;
        var text = HistoryText.Trim();
        // The audit log holds whole seconds, so records of one second keep their file order: the index breaks the tie.
        var rows = _records.Select((r, i) => new HygieneHistoryRow(r) { Index = i })
            .Where(r => text.Length == 0 || Has(r.Action, text) || Has(r.Result, text) || Has(r.User, text) || Has(r.Site, text) || Has(r.Provider, text) || Has(r.Record.Message, text)
                || Has(r.Record.RunId, text) || r.Record.Objects.Any(o => Has(o.ObjectName, text) || Has(o.ObjectKey, text) || Has(o.CheckId, text)));
        var list = (_sorts.ContainsKey(HistoryGrid) ? Sorted(HistoryGrid, rows) : rows.OrderByDescending(r => r.Record.Timestamp).ThenByDescending(r => r.Index)).ToList();
        _restoring = true;
        try
        {
            HistoryRows = list;
            SelectedHistory = selected is null ? null : list.FirstOrDefault(r => SameRecord(r.Record, selected));
        }
        finally { _restoring = false; }
        HistoryCount = list.Count == _records.Count ? SiteHygieneLabels.Count(_records.Count, "record") : $"{SiteHygieneLabels.Number(list.Count)} of {SiteHygieneLabels.Count(_records.Count, "record")}";
    }

    /// <summary>A read of the audit log returns new record objects, so a record is matched by its run, time, phase and first object.</summary>
    private static bool SameRecord(HygieneAuditRecord a, HygieneAuditRecord b) =>
        a.Timestamp == b.Timestamp && string.Equals(a.RunId, b.RunId, StringComparison.Ordinal) && string.Equals(a.Event, b.Event, StringComparison.Ordinal)
        && string.Equals(a.Phase, b.Phase, StringComparison.Ordinal) && string.Equals(a.Objects.FirstOrDefault()?.ObjectKey, b.Objects.FirstOrDefault()?.ObjectKey, StringComparison.Ordinal);
}
