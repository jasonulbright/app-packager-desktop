using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

public sealed partial class PivotsScriptsViewModel
{
    public const int ResultsView = 0;
    public const int DevicesView = 1;
    public const int RawView = 2;
    public const int PipelineView = 3;
    public const string ResultFilterKey = "results";
    public const int DefaultWaitMinutes = 5;
    public const int MaxRawCharacters = 2_000_000;

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

    /// <summary>The outcome the result tabs show, kept across visits so a run that ended while the page was left shows on return.</summary>
    private (PivotsOutcome Outcome, string Header, ResolvedTargets? Targets)? _shown;

    private IReadOnlyList<DeviceGridRow> _allDevices = [];
    private IReadOnlyList<ResultGridRow> _allResults = [];

    [ObservableProperty] private int _resultTabIndex;
    [ObservableProperty] private int _waitMinutes = DefaultWaitMinutes;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopWaitingCommand))]
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

    /// <summary>Shows the kept outcome again after a return to the page.</summary>
    private void ShowKeptResult()
    {
        if (_shown is { } shown) Render(shown.Outcome, shown.Header, shown.Targets);
    }

    /// <summary>The view reports the rows the operator selected in the Devices grid.</summary>
    public void SetSelectedDevices(IEnumerable<DeviceGridRow> rows) => SelectedDevices = rows.ToList();

    /// <summary>The view reports the rows the operator selected in the Results grid.</summary>
    public void SetSelectedResults(IEnumerable<ResultGridRow> rows) => SelectedResults = rows.ToList();

    private List<string> SelectedDeviceNames()
    {
        var names = (ResultTabIndex == ResultsView ? SelectedResults.Select(r => r.Device) : SelectedDevices.Select(r => r.Device))
            .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return names;
    }

    private void StartWait(PivotsPlan plan, PivotsOutcome sent, string operationId)
    {
        var number = ++_runNumber;
        var targets = plan.Change.Targets;
        var total = targets?.Devices.Count ?? sent.Devices.Count;
        AddPipeline(new PipelineRow(number, plan.Kind, DateTime.Now, total, "Waiting for results", ""));
        Show(sent, $"Run {number}: {PivotsLabels.Kind(plan.Kind)} sent to {PivotsLabels.Count(total, "device")}. Waiting for results.", targets, final: false);
        var cts = new CancellationTokenSource();
        _waitCts = cts;
        var mine = ++_waitGeneration;
        WaitProgress = $"Waiting for results: 0 of {PivotsLabels.Count(total, "device")} answered.";
        IsWaiting = true;
        _waitTask = WaitAsync(plan, sent, operationId, number, total, cts, mine);
    }

    private async Task WaitAsync(PivotsPlan plan, PivotsOutcome sent, string operationId, int number, int total, CancellationTokenSource cts, int mine)
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
        PivotsOutcome final;
        string note;
        var window = WaitWindow;
        try
        {
            final = await Task.Run(() => Services.Service.WaitForResultsAsync(operationId, window, counter, cts.Token), CancellationToken.None);
            note = cts.IsCancellationRequested ? StoppedText : final.Partial ? PartialText : "";
            if (cts.IsCancellationRequested) final = final with { Partial = true };
        }
        catch (OperationCanceledException)
        {
            final = (counter.Last ?? sent) with { Partial = true };
            note = StoppedText;
        }
        catch (Exception ex)
        {
            final = (counter.Last ?? sent) with { Partial = true, Succeeded = false };
            note = PlainMessage(ex, WaitFailed);
            Services.Log.Warning(LogArea, $"The results of a run could not be read ({FailureName(ex)}).");
        }
        finally
        {
            if (ReferenceEquals(_waitCts, cts)) _waitCts = null;
            cts.Dispose();
        }
        if (mine != _waitGeneration) return;
        var counts = StateCounts(final.Devices);
        var header = $"Run {number}: {PivotsLabels.Kind(plan.Kind)} on {PivotsLabels.Count(total, "device")}. {counts}" + (note.Length > 0 ? " " + note : "");
        ReplacePipeline(number, final.Partial ? note == StoppedText ? "Stopped waiting" : "Partial" : "Completed", PivotsLabels.Count(final.Rows.Count > 0 ? final.Rows.Count : final.Devices.Count(d => d.State == PivotsDeviceStates.Completed), "result"));
        Services.Log.Info(LogArea, $"Run {number} ended: devices={final.Devices.Count} rows={final.Rows.Count} partial={final.Partial}.");
        IsWaiting = false;
        WaitProgress = "";
        Show(final, header, plan.Change.Targets, final: true);
        if (_active && IsHistoryLoaded) await LoadHistoryAsync();
    }

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
        try { _waitCts?.Cancel(); } catch (ObjectDisposedException) { }
        StatusMessage = "The page stops waiting. Clients that received the request still run it.";
    }

    /// <summary>Shows an outcome in the result tabs. Only a final outcome sets the headline of the last run.</summary>
    private void Show(PivotsOutcome outcome, string header, ResolvedTargets? targets, bool final)
    {
        _shown = (outcome, header, targets);
        if (final) LastRunHeadline = $"Last run: {StateCounts(outcome.Devices)}" + (outcome.Partial ? " Partial." : "");
        if (_active) Render(outcome, header, targets);
        UpdateSummary();
    }

    private void Render(PivotsOutcome outcome, string header, ResolvedTargets? targets)
    {
        var online = new Dictionary<int, bool?>();
        foreach (var device in targets?.Devices ?? []) online.TryAdd(device.ResourceId, device.IsOnline);
        _allDevices = outcome.Devices.Select(d => new DeviceGridRow(d, online.TryGetValue(d.ResourceId, out var o) ? o : null)).ToList();
        if (outcome.Columns.Count > 0)
        {
            ResultColumns = outcome.Columns;
            _allResults = outcome.Rows.Select((r, i) => new ResultGridRow(r, i)).ToList();
        }
        else
        {
            ResultColumns = ["State", "Output"];
            _allResults = outcome.Devices.Select((d, i) => new ResultGridRow(new PivotsResultRow(d.ResourceId, d.Name, [PivotsLabels.DeviceState(d.State), d.Output]), i)).ToList();
        }
        var raw = string.Join(Environment.NewLine, outcome.Raw);
        RawText = raw.Length > MaxRawCharacters ? raw[..MaxRawCharacters] + Environment.NewLine + $"The raw text is cut at {PivotsLabels.Number(MaxRawCharacters)} characters. The saved session holds all of it." : raw;
        IsPartial = outcome.Partial;
        ResultHeader = header;
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
            DevicesView => new PivotsReport("Devices", ["Device", "ResourceID", "State", "Online", "Output", "Message"],
                DeviceRows.Select(r => (IReadOnlyList<string>)[r.Device, r.ResourceId, r.State, r.Online, r.Result.Output, r.Message]).ToList(), IsPartial, note) { Filter = filter },
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
            var answered = value.Devices.Count(d => PivotsLabels.IsFinal(d.State));
            show($"Waiting for results: {PivotsLabels.Number(answered)} of {PivotsLabels.Count(total, "device")} answered.");
        }
    }
}
