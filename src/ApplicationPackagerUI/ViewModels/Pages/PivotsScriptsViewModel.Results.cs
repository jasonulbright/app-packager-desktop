using System.Globalization;
using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>What the result tabs show: the devices, whether the set is partial, the header and the raw responses.</summary>
public sealed record PivotsResultSet(IReadOnlyList<DeviceResult> Devices, bool Partial, string Header, IReadOnlyDictionary<int, bool?> Online, string Raw);

public sealed partial class PivotsScriptsViewModel
{
    public const int ResultsView = 0;
    public const int DevicesView = 1;
    public const int RawView = 2;
    public const int PipelineView = 3;
    public const string ResultFilterKey = "results";
    public const int DefaultWaitMinutes = 5;
    public const int MaxRawCharacters = 2_000_000;
    public const int MaxResultColumns = 64;

    public const string WaitFailed = "The results could not be read. The result set below is partial. The log has the details.";
    public const string StoppedText =
        "Partial: the page stopped waiting. Clients that received the request still run it; their results are not read. The devices still marked Sent or Running did not answer before the stop.";
    public const string PartialText = "Partial: the wait window ended before every device answered.";
    public const string NothingToExport = "The list has no rows to export.";
    public const string ExportNotThisView = "Select the Results, Devices or Pipeline tab to export its rows.";
    public const string NoResultsYet = "No result yet. Run a query or a script, or open a saved session in History.";

    public static readonly IReadOnlyList<int> WaitChoices = [1, 2, 5, 10, 15, 30, 60];

    public IReadOnlyList<int> WaitChoiceList => WaitChoices;

    private CancellationTokenSource? _waitCts;
    private Task _waitTask = Task.CompletedTask;
    private int _runNumber;
    private int _waitGeneration;
    private bool _stopRequested;

    /// <summary>The set the result tabs show, kept across visits so a run that ended while the page was left shows on return.</summary>
    private PivotsResultSet? _shown;

    private IReadOnlyList<DeviceGridRow> _allDevices = [];
    private IReadOnlyList<ResultGridRow> _allResults = [];

    [ObservableProperty] private int _resultTabIndex;
    [ObservableProperty] private int _waitMinutes = DefaultWaitMinutes;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopWaitingCommand), nameof(WaitAgainCommand))]
    private bool _isWaiting;

    /// <summary>The count line of a running wait: how many devices answered. The result grids change only when the wait ends.</summary>
    [ObservableProperty] private string _waitProgress = "";

    [ObservableProperty] private string _resultHeader = NoResultsYet;
    [ObservableProperty] private bool _isPartial;
    [ObservableProperty] private string _lastRunHeadline = "";
    [ObservableProperty] private IReadOnlyList<string> _resultColumns = [];
    [ObservableProperty] private IReadOnlyList<ResultGridRow> _resultRows = [];
    [ObservableProperty] private IReadOnlyList<DeviceGridRow> _deviceRows = [];
    [ObservableProperty] private string _rawText = "";
    [ObservableProperty] private IReadOnlyList<PipelineRow> _pipelineRows = [];
    [ObservableProperty] private string _resultFilter = "";
    [ObservableProperty] private string _resultCount = "";
    [ObservableProperty] private IReadOnlyList<DeviceGridRow> _selectedDevices = [];
    [ObservableProperty] private IReadOnlyList<ResultGridRow> _selectedResults = [];

    /// <summary>The folder of the last report written; null before the first export.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReportsFolderCommand))]
    private string? _lastReportFolder;

    /// <summary>The wait that runs now, or a completed task.</summary>
    public Task WaitTask => _waitTask;

    public TimeSpan WaitWindow => TimeSpan.FromMinutes(WaitChoices.Contains(WaitMinutes) ? WaitMinutes : DefaultWaitMinutes);

    partial void OnResultTabIndexChanged(int value) => SaveLater();
    partial void OnWaitMinutesChanged(int value) => SaveLater();
    partial void OnResultFilterChanged(string value) => Debounce(ResultFilterKey, ApplyResultFilter);
    partial void OnIsWaitingChanged(bool value) => UpdateSummary();

    private void ReleaseResultRows()
    {
        _allDevices = [];
        _allResults = [];
        ResultRows = [];
        DeviceRows = [];
        ResultColumns = [];
        RawText = "";
        SelectedDevices = [];
        SelectedResults = [];
        ResultCount = "";
        LastReportFolder = null;
    }

    /// <summary>Shows the kept set again after a return to the page.</summary>
    private void ShowKeptResult()
    {
        if (_shown is { } shown) Render(shown);
    }

    /// <summary>The view reports the rows the operator selected in the Devices grid.</summary>
    public void SetSelectedDevices(IEnumerable<DeviceGridRow> rows) => SelectedDevices = rows.ToList();

    /// <summary>The view reports the rows the operator selected in the Results grid.</summary>
    public void SetSelectedResults(IEnumerable<ResultGridRow> rows) => SelectedResults = rows.ToList();

    private List<string> SelectedDeviceNames() =>
        (ResultTabIndex == ResultsView ? SelectedResults.Select(r => r.Device) : SelectedDevices.Select(r => r.Device))
            .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static IReadOnlyDictionary<int, bool?> OnlineOf(IEnumerable<ResolvedDevice>? devices)
    {
        var online = new Dictionary<int, bool?>();
        foreach (var device in devices ?? []) online.TryAdd(device.ResourceId, device.IsOnline);
        return online;
    }

    private void StartWait(PivotsPlan plan, PivotsOutcome sent, string operationId)
    {
        var number = ++_runNumber;
        var total = plan.Change.DeviceCount > 0 ? plan.Change.DeviceCount : plan.Change.Targets?.Devices.Count ?? sent.Devices.Count;
        AddPipeline(new PipelineRow(number, plan.Kind, DateTime.Now, total, "Waiting for results", ""));
        var online = OnlineOf(plan.Change.Targets?.Devices);
        Show(new PivotsResultSet(sent.Devices, true, $"Run {number}: {PivotsLabels.Kind(plan.Kind)} sent to {PivotsLabels.Count(total, "device")}. Waiting for results.", online, ""), final: false);
        BeginWait(operationId, number, plan.Kind, total, sent, online, sent.SessionId);
    }

    private void BeginWait(string operationId, int number, PivotsChangeKind kind, int total, PivotsOutcome? sent, IReadOnlyDictionary<int, bool?> online, string? sessionId)
    {
        var cts = new CancellationTokenSource();
        _waitCts = cts;
        _stopRequested = false;
        var mine = ++_waitGeneration;
        WaitProgress = $"Waiting for results: 0 of {PivotsLabels.Count(total, "device")} answered.";
        IsWaiting = true;
        _waitTask = WaitAsync(operationId, number, kind, total, sent, online, sessionId, cts, mine);
    }

    private async Task WaitAsync(string operationId, int number, PivotsChangeKind kind, int total, PivotsOutcome? sent, IReadOnlyDictionary<int, bool?> online, string? sessionId,
        CancellationTokenSource cts, int mine)
    {
        var context = SynchronizationContext.Current;
        var counter = new WaitCounter(text =>
        {
            void Set()
            {
                if (mine == _waitGeneration && IsWaiting) WaitProgress = text;
            }
            if (context is null) Set();
            else context.Post(_ => Set(), null);
        }, total);
        IReadOnlyList<DeviceResult> devices;
        bool partial;
        string note;
        string? session = sessionId;
        cts.CancelAfter(WaitWindow);
        try
        {
            var final = await Task.Run(() => Services.Service.WaitForResultsAsync(operationId, counter, cts.Token), CancellationToken.None);
            devices = final.Devices;
            session = final.SessionId ?? sessionId;
            partial = final.Partial || cts.IsCancellationRequested || final.Devices.Any(d => !d.IsFinal);
            note = _stopRequested ? StoppedText : partial ? PartialText : "";
        }
        catch (OperationCanceledException)
        {
            devices = (counter.Last ?? sent)?.Devices ?? [];
            partial = true;
            note = _stopRequested ? StoppedText : PartialText;
        }
        catch (Exception ex)
        {
            devices = (counter.Last ?? sent)?.Devices ?? [];
            partial = true;
            note = PlainMessage(ex, WaitFailed);
            Services.Log.Warning(LogArea, $"The results of a run could not be read ({FailureName(ex)}).");
        }
        finally
        {
            if (ReferenceEquals(_waitCts, cts)) _waitCts = null;
            cts.Dispose();
        }
        if (mine != _waitGeneration) return;
        var header = $"Run {number}: {PivotsLabels.Kind(kind)} on {PivotsLabels.Count(total, "device")}. {StateCounts(devices)}" + (note.Length > 0 ? " " + note : "")
            + MoreResultsNote(devices);
        ReplacePipeline(number, partial ? _stopRequested ? "Stopped waiting" : "Partial" : "Completed", PivotsLabels.Count(RowCount(devices), "result"));
        Services.Log.Info(LogArea, $"Run {number} ended: devices={devices.Count} answered={devices.Count(d => d.IsFinal)} partial={partial}.");
        IsWaiting = false;
        WaitProgress = "";
        await ShowAsync(devices, partial, header, null, session, final: true, online);
        if (_active && IsHistoryLoaded) await LoadHistoryAsync();
    }

    private static int RowCount(IReadOnlyList<DeviceResult> devices) =>
        devices.Any(d => d.Rows.Count > 0) ? devices.Sum(d => d.Rows.Count) : devices.Count(d => d.State == PivotsDeviceStates.Completed);

    private static string MoreResultsNote(IReadOnlyList<DeviceResult> devices) =>
        devices.Count(d => d.MoreResults) is var more and > 0 ? $" {PivotsLabels.Count(more, "device")} hold more result rows than the site returned." : "";

    /// <summary>"Completed 10, Failed 1, No response 2." from the device states; states with no device are left out.</summary>
    public static string StateCounts(IReadOnlyList<DeviceResult> devices)
    {
        var parts = devices.GroupBy(d => PivotsLabels.DeviceState(d.State), StringComparer.Ordinal)
            .OrderBy(g => Array.IndexOf(StateOrder, g.Key) is var i && i < 0 ? int.MaxValue : i)
            .Select(g => $"{g.Key} {PivotsLabels.Number(g.Count())}").ToList();
        return parts.Count == 0 ? "No device reported." : string.Join(", ", parts) + ".";
    }

    private static readonly string[] StateOrder = ["Completed", "Failed", "Timed out", "No response", "Running", "Sent", "Skipped"];

    private bool CanStopWaiting() => IsWaiting;

    /// <summary>Ends the wait on the page only. Clients that received the request still run it.</summary>
    [RelayCommand(CanExecute = nameof(CanStopWaiting))]
    private void StopWaiting()
    {
        _stopRequested = true;
        try { _waitCts?.Cancel(); } catch (ObjectDisposedException) { }
        StatusMessage = "The page stops waiting. Clients that received the request still run it.";
    }

    /// <summary>Shows a result set and reads the raw responses of its saved session. Only a final set sets the headline of the last run.</summary>
    private async Task ShowAsync(IReadOnlyList<DeviceResult> devices, bool partial, string header, ResolvedTargets? targets, string? sessionId, bool final,
        IReadOnlyDictionary<int, bool?>? online = null)
    {
        var raw = "";
        if (sessionId is { Length: > 0 } id)
        {
            try { raw = await Task.Run(() => RawOf(Services.Sessions.Read(id))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PivotsServiceException or InvalidOperationException)
            {
                Services.Log.Warning(LogArea, $"The raw responses of a session could not be read ({ex.GetType().Name}).");
            }
        }
        Show(new PivotsResultSet(devices, partial, header, online ?? OnlineOf(targets?.Devices), raw), final);
    }

    /// <summary>The raw records of a session as text, one block per request or response.</summary>
    public static string RawOf(PivotsSessionRecord? record) =>
        record is null ? "" : string.Join(Environment.NewLine + Environment.NewLine, record.Raw.Select(r =>
            $"{r.Utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC {r.Phase}{(r.HttpStatus is { } s ? " HTTP " + s.ToString(CultureInfo.InvariantCulture) : "")}{Environment.NewLine}{SecretMask.Apply(r.Body)}"));

    private void Show(PivotsResultSet set, bool final)
    {
        _shown = set;
        if (final) LastRunHeadline = $"Last run: {StateCounts(set.Devices)}" + (set.Partial ? " Partial." : "");
        if (_active) Render(set);
        UpdateSummary();
    }

    /// <summary>The result columns in the order they first appear in the rows of the devices, at most <see cref="MaxResultColumns"/>.</summary>
    public static IReadOnlyList<string> ColumnsOf(IReadOnlyList<DeviceResult> devices)
    {
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
            foreach (var row in device.Rows)
                foreach (var key in row.Keys)
                    if (columns.Count < MaxResultColumns && seen.Add(key)) columns.Add(key);
        return columns;
    }

    private void Render(PivotsResultSet set)
    {
        _allDevices = set.Devices.Select(d => new DeviceGridRow(d, set.Online.TryGetValue(d.ResourceId, out var o) ? o : null)).ToList();
        var columns = ColumnsOf(set.Devices);
        var results = new List<ResultGridRow>();
        if (columns.Count > 0)
        {
            ResultColumns = columns;
            foreach (var device in set.Devices)
                foreach (var row in device.Rows)
                    results.Add(new ResultGridRow(new ResultRowData(device.ResourceId, device.Name, columns.Select(c => row.TryGetValue(c, out var v) ? v : "").ToList()), results.Count));
        }
        else
        {
            ResultColumns = ["State", "Output"];
            foreach (var device in set.Devices)
                results.Add(new ResultGridRow(new ResultRowData(device.ResourceId, device.Name, [PivotsLabels.DeviceState(device.State), device.Output]), results.Count));
        }
        _allResults = results;
        RawText = set.Raw.Length > MaxRawCharacters
            ? set.Raw[..MaxRawCharacters] + Environment.NewLine + $"The raw text is cut at {PivotsLabels.Number(MaxRawCharacters)} characters. The saved session holds all of it."
            : set.Raw;
        IsPartial = set.Partial;
        ResultHeader = set.Header;
        ApplyResultFilter();
    }

    private void ApplyResultFilter()
    {
        var text = ResultFilter.Trim();
        var devices = _allDevices.Where(r => text.Length == 0 || Has(r.Device, text) || Has(r.State, text) || Has(r.Message, text) || Has(r.Result.Output, text)).ToList();
        var results = _allResults.Where(r => text.Length == 0 || Has(r.Device, text) || r.Cells.Any(c => Has(c, text))).ToList();
        DeviceRows = devices;
        ResultRows = results;
        var selectedDevices = SelectedDevices.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var selectedResults = SelectedResults.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        SelectedDevices = devices.Where(r => selectedDevices.Contains(r.Key)).ToList();
        SelectedResults = results.Where(r => selectedResults.Contains(r.Key)).ToList();
        ResultCount = $"{(results.Count == _allResults.Count ? PivotsLabels.Count(results.Count, "result row") : $"{PivotsLabels.Number(results.Count)} of {PivotsLabels.Count(_allResults.Count, "result row")}")}, "
            + (devices.Count == _allDevices.Count ? PivotsLabels.Count(devices.Count, "device") : $"{PivotsLabels.Number(devices.Count)} of {PivotsLabels.Count(_allDevices.Count, "device")}") + ".";
    }

    private void AddPipeline(PipelineRow row) => PipelineRows = [.. PipelineRows, row];

    private void ReplacePipeline(int number, string state, string results) =>
        PipelineRows = PipelineRows.Select(r => r.Number == number ? r with { State = state, Results = results } : r).ToList();

    /// <summary>The rows of the active result tab as the filter shows them; null for the Raw tab.</summary>
    public PivotsReport? ActiveReport()
    {
        FlushFilters();
        var filter = ResultFilter.Trim().Length > 0 ? $"Text: {ResultFilter.Trim()}." : "";
        var note = IsPartial ? "Partial: the result set does not hold a final state for every device." : null;
        return ResultTabIndex switch
        {
            ResultsView => new PivotsReport("Results", ["Device", .. ResultColumns], ResultRows.Select(r => (IReadOnlyList<string>)[r.Device, .. r.Cells]).ToList(), IsPartial, note) { Filter = filter },
            DevicesView => new PivotsReport("Devices", ["Device", "ResourceID", "State", "Online", "Exit code", "Output", "Message"],
                DeviceRows.Select(r => (IReadOnlyList<string>)[r.Device, r.ResourceId, r.State, r.Online, r.ExitCode, r.Result.Output, r.Message]).ToList(), IsPartial, note) { Filter = filter },
            PipelineView => new PivotsReport("Pipeline", ["Run", "Action", "Started", "Targets", "State", "Results"],
                PipelineRows.Select(r => (IReadOnlyList<string>)[r.Run, r.Action, r.Started, r.Targets, r.State, r.Results]).ToList(), false, null),
            _ => null
        };
    }

    [RelayCommand]
    private Task ExportCsv() => ExportAsync("CSV", Services.Reports.WriteCsv);

    [RelayCommand]
    private Task ExportJson() => ExportAsync("JSON", Services.Reports.WriteJson);

    private async Task ExportAsync(string format, Func<PivotsReport, string> write)
    {
        if (ActiveReport() is not { } report)
        {
            StatusMessage = ExportNotThisView;
            return;
        }
        if (report.Rows.Count == 0)
        {
            StatusMessage = NothingToExport;
            return;
        }
        string path;
        try { path = await Task.Run(() => write(report)); }
        catch (Exception ex)
        {
            StatusMessage = $"The {format} report could not be written. The log has the details.";
            Services.Log.Warning(LogArea, $"The {format} report could not be written ({ex.GetType().Name}).");
            return;
        }
        StatusMessage = $"Saved the {format} report to {path}: {PivotsLabels.Count(report.Rows.Count, "row")}." + (report.Filter.Length > 0 ? " Filter: " + report.Filter : " No filter.") + (report.Note is { } n ? " " + n : "");
        Services.Log.Info(LogArea, $"Saved a {format} report with {report.Rows.Count} rows.");
        LastReportFolder = Path.GetDirectoryName(path) is { Length: > 0 } folder ? folder : null;
    }

    private bool CanOpenReportsFolder() => LastReportFolder is not null;

    [RelayCommand(CanExecute = nameof(CanOpenReportsFolder))]
    private void OpenReportsFolder()
    {
        if (LastReportFolder is { } folder) Services.Dialogs.OpenFolder(folder);
    }

    /// <summary>Copies the rows of the active result tab as a PowerShell expression that gives them back as objects.</summary>
    [RelayCommand]
    private void CopyAsPowerShell()
    {
        if (ActiveReport() is not { } report)
        {
            StatusMessage = ExportNotThisView;
            return;
        }
        if (report.Rows.Count == 0)
        {
            StatusMessage = "There are no rows to copy.";
            return;
        }
        Services.Dialogs.SetClipboard(PivotsScriptsText.CopyAsPowerShell(report.Columns, report.Rows));
        StatusMessage = $"Copied {PivotsLabels.Count(report.Rows.Count, "row")} as PowerShell.";
    }

    /// <summary>Counts the devices that answered in each progress report of a wait; the report itself is never shown as a result.</summary>
    private sealed class WaitCounter(Action<string> show, int total) : IProgress<PivotsOutcome>
    {
        private readonly object _gate = new();
        private PivotsOutcome? _last;

        public PivotsOutcome? Last
        {
            get { lock (_gate) return _last; }
        }

        public void Report(PivotsOutcome value)
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate) _last = value;
            var answered = value.Devices.Count(d => d.IsFinal);
            show($"Waiting for results: {PivotsLabels.Number(answered)} of {PivotsLabels.Count(total, "device")} answered.");
        }
    }
}
