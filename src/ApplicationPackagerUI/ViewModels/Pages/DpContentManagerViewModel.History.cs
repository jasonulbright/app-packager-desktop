using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerUI.ViewModels.Pages;

public sealed partial class DpContentManagerViewModel
{
    public const int MaxHistoryRecords = 2000;
    public const string HistoryReadFailed = "The audit log could not be read. The log has the details.";
    public static readonly string HistoryCappedNote = $"The list shows the newest {MaxHistoryRecords} records of the audit log. Older records are in the file.";

    private IReadOnlyList<DpContentAuditRecord> _records = [];
    private CancellationTokenSource? _historyCts;
    private int _historyGeneration;

    public IReadOnlyList<string> HistoryActions { get; } =
        [AllText, .. Enum.GetValues<DpContentChangeKind>().Select(k => DpContentHistoryText.ActionLabel(k.ToString()))];

    public bool IsHistoryLoaded { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshHistoryCommand))]
    private bool _isHistoryLoading;

    [ObservableProperty] private string _historyText = "";
    [ObservableProperty] private string _historyAction = AllText;
    [ObservableProperty] private IReadOnlyList<DpContentHistoryRow> _historyRows = [];
    [ObservableProperty] private string _historyCount = "";
    [ObservableProperty] private string _historyPath = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryCapText))]
    private bool _isHistoryCapped;

    public string HistoryCapText => IsHistoryCapped ? HistoryCappedNote : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryDetails), nameof(HasHistoryDetails))]
    private DpContentHistoryRow? _selectedHistory;

    /// <summary>The whole record of the selected row: whether it is verified, its pairs and its message.</summary>
    public string HistoryDetails => SelectedHistory is { } row
        ? string.Join(Environment.NewLine, new[] { row.VerifiedText, $"Content: {row.Content}", $"DPs: {row.Dps}", row.Message }.Where(l => l.Length > 0))
        : "";

    public bool HasHistoryDetails => HistoryDetails.Length > 0;

    partial void OnHistoryTextChanged(string value)
    {
        ApplyHistoryFilter();
        SaveLater();
    }

    partial void OnHistoryActionChanged(string value)
    {
        ApplyHistoryFilter();
        SaveLater();
    }

    private bool CanRefreshHistory() => !IsHistoryLoading;

    [RelayCommand(CanExecute = nameof(CanRefreshHistory))]
    private Task RefreshHistory() => LoadHistoryAsync();

    /// <summary>Reads the audit log off the UI thread; it needs no site.</summary>
    public async Task LoadHistoryAsync()
    {
        var generation = Generation;
        var mine = ++_historyGeneration;
        try { _historyCts?.Cancel(); } catch (ObjectDisposedException) { }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(VisitToken);
        _historyCts = cts;
        IsHistoryLoading = true;
        try
        {
            var (records, path) = await Task.Run(() => (Services.AuditLog.Read(MaxHistoryRecords), Services.AuditLog.Path), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (generation != Generation || mine != _historyGeneration) return;
            _records = records;
            HistoryPath = path;
            IsHistoryCapped = records.Count >= MaxHistoryRecords;
            IsHistoryLoaded = true;
            ApplyHistoryFilter();
            Services.Log.Info(LogArea, $"Read {records.Count} audit records.");
        }
        catch (OperationCanceledException)
        {
            if (generation == Generation && mine == _historyGeneration) StatusMessage = ReadCancelled;
        }
        catch (Exception ex)
        {
            if (generation != Generation || mine != _historyGeneration) return;
            StatusMessage = HistoryReadFailed;
            Services.Log.Warning(LogArea, $"The audit log read failed ({ex.GetType().Name}).");
        }
        finally
        {
            if (ReferenceEquals(_historyCts, cts)) _historyCts = null;
            if (mine == _historyGeneration) IsHistoryLoading = false;
        }
    }

    private void ApplyHistoryFilter()
    {
        var selected = SelectedHistory?.Record;
        var text = HistoryText.Trim();
        var rows = _records
            .Where(r => HistoryAction == AllText || string.Equals(DpContentHistoryText.ActionLabel(r.Action), HistoryAction, StringComparison.OrdinalIgnoreCase))
            .Where(r => text.Length == 0 || Has(r.PackageIds, text) || Has(r.DpNames, text) || Has(r.User, text) || Has(r.Result, text) || Has(r.Message, text))
            .Select(r => new DpContentHistoryRow(r));
        var list = (_sorts.ContainsKey(HistoryGrid) ? Sorted(HistoryGrid, rows) : rows.OrderByDescending(r => r.Record.Timestamp)).ToList();
        HistoryRows = list;
        SelectedHistory = selected is null ? null : list.FirstOrDefault(r => ReferenceEquals(r.Record, selected));
        HistoryCount = list.Count == _records.Count ? DpContentStates.Count(_records.Count, "record") : $"{DpContentStates.Number(list.Count)} of {DpContentStates.Count(_records.Count, "record")}";
    }

    private void ReleaseHistory()
    {
        _historyGeneration++;
        try { _historyCts?.Cancel(); } catch (ObjectDisposedException) { }
        _historyCts = null;
        IsHistoryLoading = false;
        IsHistoryLoaded = false;
        _records = [];
        HistoryRows = [];
        SelectedHistory = null;
        HistoryCount = "";
        HistoryPath = "";
        IsHistoryCapped = false;
    }
}
