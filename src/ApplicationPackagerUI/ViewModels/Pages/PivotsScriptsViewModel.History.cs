using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerUI.ViewModels.Pages;

public sealed partial class PivotsScriptsViewModel
{
    public const int MaxHistoryRecords = 2000;
    public const int MaxSessions = 500;
    public const string HistoryKey = "history";
    public const string HistoryReadFailed = "The audit log could not be read. The log has the details.";
    public const string SessionsReadFailed = "The saved sessions could not be read. The log has the details.";
    public const string SessionGone = "The saved session could not be read. The file is gone or damaged.";
    public const string SessionNoResult = "The saved session holds no result. The run was saved before it was sent and holds no response.";
    public static readonly string HistoryCappedNote = $"The list shows the newest {MaxHistoryRecords} records of the audit log. Older records are in the file.";
    public static readonly string SessionsCappedNote = $"The list shows the newest {MaxSessions} saved sessions. Older sessions are in the sessions folder.";
    public const string HistoryWindowNote ="The audit log is large, so the list shows only its last part. Older records are in the file.";
    public const string ConnectionFailed = "The connection test could not run. The log has the details.";

    private IReadOnlyList<PivotsAuditRecord> _records = [];
    private int _historyGeneration;

    public bool IsHistoryLoaded { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshHistoryCommand))]
    private bool _isHistoryLoading;

    [ObservableProperty] private string _historyText = "";
    [ObservableProperty] private IReadOnlyList<PivotsAuditRow> _historyRows = [];
    [ObservableProperty] private string _historyCount = "";
    [ObservableProperty] private string _historyPath = "";
    [ObservableProperty] private bool _isHistoryCapped;
    [ObservableProperty] private string _historyCapText = "";
    [ObservableProperty] private IReadOnlyList<SessionGridRow> _sessionRows = [];
    [ObservableProperty] private string _sessionCount = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSessionCommand))]
    private SessionGridRow? _selectedSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryDetails), nameof(HasHistoryDetails))]
    private PivotsAuditRow? _selectedHistory;

    public string HistoryDetails => SelectedHistory is { } row
        ? string.Join(Environment.NewLine, new[]
        {
            row.VerifiedText,
            $"{row.Action}, {row.Record.Phase}: {row.Result}. Devices: {row.Devices}." + (row.Target.Length > 0 ? $" Target: {row.Target}." : ""),
            (row.Record.ScriptGuid is { Length: > 0 } guid ? $"Script {guid}. " : "") + (row.Record.TextHash is { Length: > 0 } hash ? $"SHA-256 of the text: {hash}." : "")
                + (row.Record.OperationId is { Length: > 0 } op ? $" Operation {op}." : ""),
            row.Message,
            row.Record.Event == nameof(PivotsChangeKind.RunScript) ? ParameterValuesNotStored : ""
        }.Where(l => l.Length > 0))
        : "";

    public const string ParameterValuesNotStored = "Parameter values are not stored. The saved session holds the parameter names and a SHA-256 hash of each value.";

    /// <summary>The parameter names of a saved script run; its values are not stored.</summary>
    private static string SessionParameters(PivotsSessionRecord session) =>
        session.Kind != PivotsChangeKind.RunScript || session.Parameters.Count == 0 ? ""
            : $" Parameters: {string.Join(", ", session.Parameters.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))}. The values are not stored.";

    public bool HasHistoryDetails => HistoryDetails.Length > 0;

    partial void OnHistoryTextChanged(string value) => Debounce(HistoryKey, ApplyHistoryFilter);

    private Task? _historyTask;

    /// <summary>The read that runs now, or a new one when the history is not loaded.</summary>
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

    /// <summary>Reads the saved sessions and the audit log off the UI thread; it needs no site.</summary>
    public async Task LoadHistoryAsync()
    {
        var generation = Generation;
        var mine = ++_historyGeneration;
        var visit = VisitToken;
        IsHistoryLoading = true;
        try
        {
            IReadOnlyList<PivotsAuditRecord>? records = null;
            IReadOnlyList<PivotsSessionSummary>? sessions = null;
            string path = "";
            var dropped = false;
            string? auditProblem = null, sessionProblem = null;
            await Task.Run(() =>
            {
                try
                {
                    records = Services.AuditLog.Read(MaxHistoryRecords + 1);
                    path = Services.AuditLog.Path;
                    dropped = Services.AuditLog.OlderRecordsDropped;
                }
                catch (Exception ex)
                {
                    auditProblem = PlainMessage(ex, HistoryReadFailed);
                    Services.Log.Warning(LogArea, $"The audit log read failed ({ex.GetType().Name}).");
                }
                try { sessions = Services.Sessions.List(MaxSessions + 1); }
                catch (Exception ex)
                {
                    sessionProblem = PlainMessage(ex, SessionsReadFailed);
                    Services.Log.Warning(LogArea, $"The saved sessions could not be listed ({ex.GetType().Name}).");
                }
            }, visit);
            if (generation != Generation || mine != _historyGeneration) return;
            if (records is not null)
            {
                var capped = records.Count > MaxHistoryRecords;
                _records = capped ? records.Skip(records.Count - MaxHistoryRecords).ToList() : records;
                HistoryPath = path;
                HistoryCapText = capped ? HistoryCappedNote : dropped ? HistoryWindowNote : "";
                IsHistoryCapped = capped || dropped;
            }
            if (sessions is not null)
            {
                var selected = SelectedSession?.Key;
                var older = sessions.Count > MaxSessions;
                SessionRows = sessions.Take(MaxSessions).Select(s => new SessionGridRow(s)).ToList();
                SelectedSession = selected is null ? null : SessionRows.FirstOrDefault(r => r.Key == selected);
                SessionCount = PivotsLabels.Count(SessionRows.Count, "saved session") + (older ? ". " + SessionsCappedNote : "");
            }
            IsHistoryLoaded = true;
            ApplyHistoryFilter();
            if ((auditProblem ?? sessionProblem) is { } problem) StatusMessage = problem;
            Services.Log.Info(LogArea, $"Read {records?.Count ?? 0} audit records and {sessions?.Count ?? 0} sessions.");
        }
        catch (OperationCanceledException)
        {
            if (generation == Generation && mine == _historyGeneration) StatusMessage = ReadCancelled;
        }
        finally
        {
            if (mine == _historyGeneration) IsHistoryLoading = false;
        }
    }

    private void ApplyHistoryFilter()
    {
        var selected = SelectedHistory?.Record;
        var text = HistoryText.Trim();
        // The audit log holds whole seconds, so records of one second keep their file order: the index breaks the tie.
        var list = _records.Select((r, i) => new PivotsAuditRow(r) { Index = i })
            .Where(r => text.Length == 0 || Has(r.Action, text) || Has(r.Result, text) || Has(r.Target, text) || Has(r.User, text) || Has(r.Message, text) || Has(r.Record.ScriptGuid, text))
            .OrderByDescending(r => r.Record.Timestamp).ThenByDescending(r => r.Index).ToList();
        HistoryRows = list;
        SelectedHistory = selected is null ? null : list.FirstOrDefault(r => SameRecord(r.Record, selected));
        HistoryCount = list.Count == _records.Count ? PivotsLabels.Count(_records.Count, "record") : $"{PivotsLabels.Number(list.Count)} of {PivotsLabels.Count(_records.Count, "record")}";
    }

    /// <summary>A read of the audit log returns new record objects, so a record is matched by its run, time, event and phase.</summary>
    private static bool SameRecord(PivotsAuditRecord a, PivotsAuditRecord b) =>
        a.Timestamp == b.Timestamp && string.Equals(a.RunId, b.RunId, StringComparison.Ordinal)
        && string.Equals(a.Event, b.Event, StringComparison.Ordinal) && string.Equals(a.Phase, b.Phase, StringComparison.Ordinal);

    private void ReleaseHistory()
    {
        _historyGeneration++;
        _historyTask = null;
        IsHistoryLoading = false;
        IsHistoryLoaded = false;
        _records = [];
        HistoryRows = [];
        SelectedHistory = null;
        SessionRows = [];
        SelectedSession = null;
        HistoryCount = SessionCount = HistoryPath = HistoryCapText = "";
        IsHistoryCapped = false;
    }

    private bool CanOpenSession() => SelectedSession is not null;

    /// <summary>Opens a saved session read only in the result tabs of its kind.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenSession))]
    private async Task OpenSession()
    {
        if (SelectedSession is not { } row) return;
        if (IsWaiting)
        {
            StatusMessage = WaitRunning;
            return;
        }
        var generation = Generation;
        PivotsSessionRecord? session;
        try { session = await Task.Run(() => Services.Sessions.Read(row.Key)); }
        catch (Exception ex)
        {
            if (generation == Generation) StatusMessage = PlainMessage(ex, SessionGone);
            Services.Log.Warning(LogArea, $"A saved session could not be read ({ex.GetType().Name}).");
            return;
        }
        if (generation != Generation) return;
        if (session is null)
        {
            StatusMessage = SessionGone;
            return;
        }
        if (session.Results.Count == 0 && session.Raw.Count == 0)
        {
            StatusMessage = SessionNoResult;
            return;
        }
        var created = PivotsLabels.Time(DateTime.SpecifyKind(session.CreatedUtc, DateTimeKind.Utc));
        Show(new PivotsResultSet(session.Results, session.Partial,
            $"Saved session of {created}, read only: {PivotsLabels.Kind(session.Kind)} on {session.TargetDescription}. {StateCounts(session.Results)}" + (session.Partial ? " Partial." : "") + SessionParameters(session),
            OnlineOf(session.Devices), RawOf(session)), final: false);
        SelectedTabIndex = session.Kind == PivotsChangeKind.RunScript ? ScriptTab : PivotTab;
        StatusMessage = "The saved session is open in the result tabs. It is read only; a new run needs Resolve.";
    }

    private bool CanWaitAgain() => !IsWaiting && !IsWriting && SelectedSession is { CanWaitAgain: true };

    /// <summary>Reads the results of the selected saved run again for the wait time; it reads only and sends nothing.</summary>
    [RelayCommand(CanExecute = nameof(CanWaitAgain))]
    private void WaitAgain()
    {
        if (SelectedSession is not { CanWaitAgain: true } row || row.Session.OperationId is not { } operation) return;
        if (Busy() is { } busy)
        {
            StatusMessage = busy;
            return;
        }
        var number = ++_runNumber;
        AddPipeline(new PipelineRow(number, row.Session.Kind, DateTime.Now, row.Session.DeviceCount, "Waiting for results", ""));
        Show(new PivotsResultSet([], true, $"Run {number}: reading the results of the saved run of {row.Started} again. Nothing is sent.", new Dictionary<int, bool?>(), ""), final: false);
        BeginWait(operation, number, row.Session.Kind, row.Session.DeviceCount, null, new Dictionary<int, bool?>(), row.Key);
        SelectedTabIndex = row.Session.Kind == PivotsChangeKind.RunScript ? ScriptTab : PivotTab;
    }

    partial void OnSelectedSessionChanged(SessionGridRow? value) => WaitAgainCommand.NotifyCanExecuteChanged();

    public string SiteCodeText => Services.LoadSettings().SiteCode is { Length: > 0 } code ? code.Trim() : "Not set";
    public string ProviderText => Services.LoadSettings().ProviderMachine is { Length: > 0 } provider ? provider.Trim() : "Not set";

    [ObservableProperty] private string _libraryFolder = "";
    [ObservableProperty] private string _connectionResult = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private bool _isTesting;

    partial void OnLibraryFolderChanged(string value)
    {
        if (_applying) return;
        SaveLater();
        _libraryGeneration++;
        _libraryTask = null;
        IsLibraryLoaded = false;
        QueryLibrary = [];
        ScriptLibrary = [];
        if (_active) _ = EnsureTabLoadedAsync();
    }

    [RelayCommand]
    private void BrowseLibraryFolder()
    {
        if (Services.Dialogs.PickFolder(LibraryFolder) is { Length: > 0 } folder) LibraryFolder = folder;
    }

    [RelayCommand]
    private void ClearLibraryFolder() => LibraryFolder = "";

    private bool CanTestConnection() => !IsTesting;

    /// <summary>Asks the SMS Provider for its AdminService; the result is a plain sentence.</summary>
    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnection()
    {
        if (ConnectionProblem() is { } problem)
        {
            ConnectionResult = problem;
            return;
        }
        var generation = Generation;
        var visit = VisitToken;
        IsTesting = true;
        ConnectionResult = "Testing the connection to the SMS Provider.";
        try
        {
            var text = await Task.Run(() => Services.Service.TestConnectionAsync(visit), visit);
            if (generation == Generation) ConnectionResult = SecretMask.Apply(text);
        }
        catch (OperationCanceledException)
        {
            if (generation == Generation) ConnectionResult = ReadCancelled;
        }
        catch (Exception ex)
        {
            if (generation == Generation) ConnectionResult = PlainMessage(ex, ConnectionFailed);
            Services.Log.Warning(LogArea, $"The connection test failed ({FailureName(ex)}).");
        }
        finally
        {
            IsTesting = false;
        }
    }
}
