using System.ComponentModel;
using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>What a One Click run applies to every row; nothing here asks the operator once the run starts.</summary>
public sealed record OneClickRunOptions(string Action, bool Force, string OnExisting, string Comment);

/// <summary>Checks, stages and publishes the included rows, writing progress into each row.</summary>
public interface IOneClickRunner
{
    /// <returns>True when the run was canceled before every row finished.</returns>
    Task<bool> RunAsync(IReadOnlyList<OneClickPlanItem> rows, OneClickRunOptions options, CancellationToken ct);
}

/// <summary>Everything the plan window reads when it opens.</summary>
public sealed record OneClickPlanContext
{
    public required IReadOnlyList<(OneClickApp App, PackagerInfo? Row)> Apps { get; init; }
    /// <summary>The saved settings; read again on every replan.</summary>
    public required Func<OneClickPlanSettings> Settings { get; init; }
    public IReadOnlyDictionary<string, OneClickHistoryEntry> History { get; init; } = new Dictionary<string, OneClickHistoryEntry>();
    public required string Action { get; init; }
    public bool Force { get; init; }
    public string OnExisting { get; init; } = OneClickOnExisting.Skip;
    public string Comment { get; init; } = "";
    /// <summary>"ConfigMgr site MCM"; empty when no site is set.</summary>
    public string SiteText { get; init; } = "";
    /// <summary>The site and provider that the plan was made for, as <see cref="CurrentSite"/> writes them.</summary>
    public string PlannedSite { get; init; } = "";
    /// <summary>The site and provider that the settings name now; null when the caller does not follow them.</summary>
    public Func<string>? CurrentSite { get; init; }
    /// <summary>"In use: ConfigMgr. Not in use: Intune, WSUS."</summary>
    public string SystemsText { get; init; } = "";
    public required string ReportFolder { get; init; }
    public Func<DateTime> Clock { get; init; } = () => DateTime.Now;
    public string Operator { get; init; } = $"{Environment.UserDomainName}\\{Environment.UserName}";
    public string Computer { get; init; } = Environment.MachineName;
    /// <summary>Asks for missing local installer folders before Run; returns the rows that keep one.</summary>
    public Func<IReadOnlyList<PackagerInfo>, IReadOnlyList<PackagerInfo>>? ConfirmLocalSources { get; init; }
    public Action<string>? Log { get; init; }
    /// <summary>True when the effective supersedence mode of the application is on; null when the caller does not say.</summary>
    public Func<PackagerInfo, bool>? Supersedes { get; init; }
}

/// <summary>
/// The One Click plan before the run, the progress grid during it and the report
/// after it. A replan runs only on an operator change; a box the replan itself
/// sets starts no further replan, and Include keeps the operator's value.
/// </summary>
public partial class OneClickPlanViewModel : ObservableObject
{
    public const string HintText =
        "Include and the destination boxes apply to this run only; Settings, One Click holds the saved values. Run asks nothing after it starts.";

    private readonly OneClickPlanContext _context;
    private readonly IOneClickRunner _runner;
    private readonly Dictionary<string, (OneClickApp App, PackagerInfo? Row)> _apps;
    private bool _refreshing;
    private CancellationTokenSource? _runCts;
    private DateTime _started;

    public ObservableCollection<OneClickPlanItem> Rows { get; } = [];

    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _actionText = "";
    [ObservableProperty] private string _scopeText = "";
    [ObservableProperty] private string _blockingText = "";
    [ObservableProperty] private bool _force;
    [ObservableProperty] private bool _isRunning;
    /// <summary>True from Run on: the result columns replace the destination boxes, the plan columns collapse.</summary>
    [ObservableProperty] private bool _isRunView;
    [ObservableProperty] private bool _showResultConfigMgr;
    [ObservableProperty] private bool _showResultWsus;
    [ObservableProperty] private bool _showResultIntune;
    [ObservableProperty] private string _reportPath = "";

    public const string SiteChangedText =
        "The ConfigMgr site or its provider changed after this plan was made. Nothing was run. Close this window and select One Click again.";

    public string Action => _context.Action;

    /// <summary>The comment that the run writes to each published application.</summary>
    [ObservableProperty] private string _comment = "";

    /// <summary>
    /// True when a run that publishes opened with a comment: the window then shows it, so no old comment is published unseen.
    /// </summary>
    public bool ShowsComment { get; private set; }

    /// <summary>
    /// What the run risks, as text: the saved setting that overwrites without a question, the supersedence question that can open during the run,
    /// and each distribution, deployment or approval. Empty for a run with none of these.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsHint))]
    private string _riskText = "";

    /// <summary>The general note of a run that publishes; a warning takes its place.</summary>
    public bool ShowsHint => IsPublishRun && RiskText.Length == 0;

    /// <summary>The state of a run or of the written plan: "Nothing to run.", "Complete: …". Empty before Plan only or Run.</summary>
    [ObservableProperty] private string _statusLine = "";

    private string Risks(IReadOnlyList<OneClickPlanRow> included)
    {
        if (!IsPublishRun) return "";
        var lines = new List<string>();
        if (OneClickOnExisting.Normalize(_context.OnExisting) == OneClickOnExisting.Overwrite
            && included.Any(r => r.PublishTo.Contains(OneClickDestinations.ConfigMgr)))
            lines.Add("The saved One Click setting overwrites an existing ConfigMgr application. Run asks nothing about it after it starts.");
        var superseded = included
            .Where(r => r.PublishTo.Contains(OneClickDestinations.ConfigMgr) || r.PublishTo.Contains(OneClickDestinations.Intune))
            .Where(r => _apps.TryGetValue(r.Key, out var app) && app.Row is { } row && _context.Supersedes?.Invoke(row) == true)
            .Select(r => r.Application).ToList();
        if (superseded.Count > 0)
            lines.Add($"Supersedence is on for {string.Join(", ", superseded)}. The run can ask before it supersedes an older application.");
        if (new[] { "distribute to ", "deploy to ", "approved for group " }.Any(w => ScopeText.Contains(w, StringComparison.Ordinal)))
            lines.Add(ScopeText);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>"2 applications included, 1 not included. ConfigMgr site MCM."</summary>
    [ObservableProperty] private string _headerText = "";

    private string HeaderLine(int included, int total) =>
        $"{included} {(included == 1 ? "application" : "applications")} included, {total - included} not included."
        + (_context.SiteText.Length > 0 ? " " + _context.SiteText + "." : "");

    /// <summary>The steps of the saved action, in order.</summary>
    public string FlowText => Flow(_context.Action);

    public string SystemsText => _context.SystemsText;

    public static string Flow(string action) => OneClickActionNames.Normalize(action) switch
    {
        OneClickActionNames.Report => "Check Latest, then Check ConfigMgr, then Check CVEs",
        OneClickActionNames.Stage => "Check Latest, then Check ConfigMgr, then Check CVEs, then Stage",
        _ => "Check Latest, then Check ConfigMgr, then Check CVEs, then Stage, then Publish"
    };

    /// <summary>The first row of the run that failed; null when none did.</summary>
    public OneClickPlanItem? FirstFailed => Rows.FirstOrDefault(r => r.Outcome == OneClickOutcomes.Failed || r.Cells.Any(c => c.Kind == PublishDestinationKind.Failed));

    [ObservableProperty] private bool _hasFailure;

    /// <summary>True when the finished run has a destination cell to show.</summary>
    [ObservableProperty] private bool _hasResults;

    private bool _runCanceled;
    private List<string> _runSystems = [];

    /// <summary>The results of the finished run for the shared results window: one row for each included application.</summary>
    public PublishResultsViewModel BuildResults(Action<System.Windows.Window>? runHistory = null) => new(
        Rows.Where(r => r.Include).Select(r => (r.Application, new PublishRowResult(r.Key, r.Version, r.Cells.ToList()))).ToList(),
        _runCanceled, _runSystems)
    {
        ReportPath = ReportPath,
        IsOneClick = true,
        SiteCode = _context.PlannedSite.Split('|')[0],
        RunNote = _context.Clock().ToString("d MMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        Versions = Rows.Where(r => r.Include).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First().Version),
        Mark = name => _context.Apps.FirstOrDefault(a => a.Row?.Application == name).Row?.Mark ?? Helpers.PackagesCells.MarkOf(name),
        ShowRunHistory = runHistory,
    };

    /// <summary>Raised after a run that has results, so the window can show them.</summary>
    public event EventHandler? ResultsReady;
    public bool HasScope => ScopeText.Length > 0;
    public bool HasBlocking => BlockingText.Length > 0;
    public bool CanRun => !IsRunning && !IsRunView && Rows.Any(r => r.Include);
    public bool CanPlanOnly => !IsRunning && !IsRunView;
    public bool CanClose => !IsRunning;
    public bool CanOpenReport => ReportPath.Length > 0;

    /// <summary>Counts the replans, so a test can prove that a replan starts no other.</summary>
    public int RefreshCount { get; private set; }

    public OneClickPlanViewModel(OneClickPlanContext context, IOneClickRunner runner)
    {
        _context = context;
        _runner = runner;
        _force = context.Force;
        _comment = context.Comment;
        ShowsComment = IsPublishRun && context.Comment.Trim().Length > 0;
        _apps = context.Apps.ToDictionary(a => a.App.Key, StringComparer.OrdinalIgnoreCase);
        Refresh();
    }

    partial void OnScopeTextChanged(string value) => OnPropertyChanged(nameof(HasScope));
    partial void OnBlockingTextChanged(string value) => OnPropertyChanged(nameof(HasBlocking));
    partial void OnReportPathChanged(string value) => OnPropertyChanged(nameof(CanOpenReport));
    partial void OnForceChanged(bool value) => Refresh();

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanPlanOnly));
        OnPropertyChanged(nameof(CanClose));
        RunCommand.NotifyCanExecuteChanged();
        PlanOnlyCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunViewChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanPlanOnly));
        RunCommand.NotifyCanExecuteChanged();
        PlanOnlyCommand.NotifyCanExecuteChanged();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_refreshing || IsRunning || IsRunView || sender is not OneClickPlanItem item) return;
        switch (e.PropertyName)
        {
            case nameof(OneClickPlanItem.Include):
                item.IncludeTouched = true;
                Refresh();
                break;
            case nameof(OneClickPlanItem.ConfigMgr):
            case nameof(OneClickPlanItem.Wsus):
            case nameof(OneClickPlanItem.Intune):
                Refresh();
                break;
        }
    }

    /// <summary>The saved settings with this window's destination boxes in place of the saved ones.</summary>
    private OneClickPlanSettings RunSettings()
    {
        var saved = _context.Settings();
        if (Rows.Count == 0) return saved;
        return saved with
        {
            Destinations = Rows.ToDictionary(r => r.Key, r => r.Destinations, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>Recomputes the plan from the boxes in the grid; a box the operator cleared stays cleared.</summary>
    public void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        RefreshCount++;
        try
        {
            var settings = RunSettings();
            var plan = OneClickPlanner.Plan(_context.Apps.Select(a => a.App), settings, _context.History, _context.Action, Force, _context.Clock());
            var existing = Rows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var p in plan)
            {
                if (existing.TryGetValue(p.Key, out var item))
                {
                    item.Version = ShownVersion(p);
                    item.Planned = p.Planned;
                    item.Reason = p.Reason;
                    item.LastPublished = p.LastPublishedText;
                    item.PublishTo = p.PublishTo;
                    item.NotReady = p.NotReady;
                    if (!item.IncludeTouched) item.Include = p.Include;
                    continue;
                }
                item = new OneClickPlanItem
                {
                    Key = p.Key,
                    Application = p.Application,
                    Vendor = p.Vendor,
                    Row = _apps.TryGetValue(p.Key, out var app) ? app.Row : null,
                    Version = ShownVersion(p),
                    PriorKnownVersion = _context.History.TryGetValue(p.Key, out var prior) ? prior.LastKnownVersion : null,
                    ConfigMgr = p.Destinations.ConfigMgr,
                    Wsus = p.Destinations.Wsus,
                    Intune = p.Destinations.Intune,
                    LastPublished = p.LastPublishedText,
                    Planned = p.Planned,
                    Reason = p.Reason,
                    PublishTo = p.PublishTo,
                    NotReady = p.NotReady,
                    Include = p.Include
                };
                item.PropertyChanged += OnItemChanged;
                Rows.Add(item);
            }

            var byKey = Rows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
            // The source is read from the saved settings: the run settings of this window give every row a selection of its own.
            var saved = _context.Settings();
            foreach (var item in Rows)
            {
                var (set, source) = OneClickPlanner.DestinationsFor(saved, item.Key);
                var changed = set.ConfigMgr != item.ConfigMgr || set.Wsus != item.Wsus || set.Intune != item.Intune;
                item.SourceText = ApplicationPackagerEngine.Models.ScopeText.Cell(changed ? ScopeSource.ChangedForRun : source == "Row" ? ScopeSource.SavedForApplication : ScopeSource.Default);
            }
            bool Included(OneClickPlanRow r) => byKey.TryGetValue(r.Key, out var i) && i.Include;
            foreach (var planned in plan)
                if (byKey.TryGetValue(planned.Key, out var row))
                    row.ChangeDetails = string.Join(Environment.NewLine, planned.PublishTo.Select(d => d + ": "
                        + (d == OneClickDestinations.ConfigMgr && row.Row is { IsJsonDefined: false, StagedManifestPath: null }
                            ? "the PS1 packager script decides distribution and deployment" : OneClickPlanner.Scope(d, settings.Scope))));
            var summary = OneClickPlanner.Summarize(plan, settings, _context.Action, Included);
            SummaryText = summary.Line;
            HeaderText = HeaderLine(plan.Count(Included), plan.Count);
            ActionText = OneClickPlanner.ActionLine(_context.Action, _context.OnExisting);
            ScopeText = OneClickPlanner.ScopeLine(plan.Where(Included), settings.Scope);
            BlockingText = summary.Blocking.Count > 0 ? "Not ready: " + string.Join("; ", summary.Blocking) : "";
            RiskText = Risks(plan.Where(Included).ToList());
            StatusLine = "";
            OnPropertyChanged(nameof(CanRun));
            RunCommand.NotifyCanExecuteChanged();
            RaisePresentation();
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>The version that the run would publish: of the local installer when the stage takes one.</summary>
    private string ShownVersion(OneClickPlanRow row) =>
        _apps.TryGetValue(row.Key, out var app) && app.App.StageVersion is { } local ? local : row.Version;

    /// <summary>Writes the report of the included rows: the plan before a run, the outcomes after it.</summary>
    public (string MarkdownPath, string JsonPath) WriteReport(DateTime ended, bool canceled, string label = "One Click report")
    {
        var used = Rows.Where(r => r.Include).ToList();
        var settings = _context.Settings();
        var scope = new Dictionary<string, string>();
        foreach (var d in OneClickDestinations.All)
            if (used.Any(r => r.Destinations.Has(d))) scope[d] = OneClickPlanner.Scope(d, settings.Scope);

        var run = new OneClickReportRun
        {
            Started = _started,
            Ended = ended,
            Operator = _context.Operator,
            Computer = _context.Computer,
            Action = OneClickActionNames.Label(_context.Action),
            Force = Force,
            Canceled = canceled,
            Scope = scope
        };
        var rows = used.Select(r => new OneClickReportRow
        {
            Key = r.Key,
            Application = r.Application,
            Version = r.Version,
            Outcome = r.Outcome.Length > 0 ? r.Outcome : canceled ? OneClickOutcomes.Skipped : OneClickOutcomes.Planned,
            Reason = r.Outcome.Length > 0 || !canceled ? r.Reason : "run canceled before this application",
            Results = r.Results,
            Ids = new Dictionary<string, string>(r.Ids),
            Details = new Dictionary<string, string>(r.Details)
        }).ToList();

        var paths = OneClickReportWriter.Write(run, rows, _context.ReportFolder);
        ReportPath = paths.MarkdownPath;
        _context.Log?.Invoke($"{label}: {paths.MarkdownPath}");
        return paths;
    }

    [RelayCommand(CanExecute = nameof(CanPlanOnly))]
    private void PlanOnly()
    {
        _started = _context.Clock();
        var paths = WriteReport(_context.Clock(), false, "One Click plan");
        SummaryText = StatusLine = "Plan written: " + paths.MarkdownPath;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        // The plan names one site; the run reads the stored settings. They must be the same.
        if (_context.CurrentSite is { } current && current() != _context.PlannedSite)
        {
            SummaryText = SiteChangedText;
            BlockingText = SiteChangedText;
            _context.Log?.Invoke("One Click: " + SiteChangedText);
            return;
        }
        var included = Rows.Where(r => r.Include).ToList();
        if (included.Count == 0) return;

        if (OneClickActionNames.Normalize(_context.Action) != OneClickActionNames.Report && _context.ConfirmLocalSources is { } confirm)
        {
            var kept = new HashSet<PackagerInfo>(confirm(included.Where(r => r.Row is not null).Select(r => r.Row!).ToList()));
            _refreshing = true;
            try
            {
                foreach (var r in included.Where(r => r.Row is not null && !kept.Contains(r.Row)))
                {
                    r.Include = false;
                    r.IncludeTouched = true;
                    r.Reason = "no local source folder";
                }
            }
            finally { _refreshing = false; }
            included = Rows.Where(r => r.Include).ToList();
            HeaderText = HeaderLine(included.Count, Rows.Count);
            if (included.Count == 0)
            {
                SummaryText = StatusLine = "Nothing to run.";
                OnPropertyChanged(nameof(CanRun));
                return;
            }
        }

        _started = _context.Clock();
        IsRunning = true;
        IsRunView = true;
        ShowResultConfigMgr = included.Any(r => r.ConfigMgr);
        ShowResultWsus = included.Any(r => r.Wsus);
        ShowResultIntune = included.Any(r => r.Intune);
        foreach (var r in included)
        {
            r.Step = "Pending";
            r.Cells.Clear();
        }
        _runSystems = OneClickDestinations.All.Where(d => included.Any(r => r.Destinations.Has(d))).ToList();
        SummaryText = StatusLine = $"Running: {included.Count} application(s)";
        _context.Log?.Invoke($"One Click: {included.Count} app(s), action={OneClickActionNames.Normalize(_context.Action)}{(Force ? ", force=on" : "")}");

        _runCts = new CancellationTokenSource();
        var canceled = false;
        string? stopped = null;
        try
        {
            canceled = await _runner.RunAsync(included, new OneClickRunOptions(_context.Action, Force, _context.OnExisting, Comment.Trim()), _runCts.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        catch (Exception ex)
        {
            // Any other exception ends the run as well: the window leaves the run state and writes the report.
            stopped = ex.Message;
            _context.Log?.Invoke("One Click run stopped: " + ex.Message);
        }
        finally
        {
            canceled |= _runCts.IsCancellationRequested;
            _runCts.Dispose();
            _runCts = null;
            IsRunning = false;
        }

        // A row the run did not finish no longer reads as pending or as the step it was in.
        foreach (var r in included.Where(r => r.Outcome.Length == 0 && (canceled || stopped is not null)))
        {
            var notStarted = r.Step == "Pending";
            r.Step = notStarted ? "Not started" : "Interrupted";
            if (stopped is null) continue;
            // The report states the difference: a row cut off in a step failed, a row never reached was skipped.
            r.Outcome = notStarted ? OneClickOutcomes.Skipped : OneClickOutcomes.Failed;
            r.Reason = notStarted ? "run stopped before this application: " + stopped : "run stopped during this application: " + stopped;
        }

        // An application that failed before a destination was tried (the check or the stage) counts as failed for that destination.
        if (OneClickActionNames.Normalize(_context.Action) == OneClickActionNames.StageAndPackage)
            foreach (var r in included.Where(r => r.Outcome == OneClickOutcomes.Failed))
                foreach (var d in OneClickDestinations.All.Where(d => r.Destinations.Has(d) && r.Cells.All(c => c.Destination != d)))
                    r.Cells.Add(new(d, PublishDestinationKind.Failed, "", "Not published: " + r.Reason));

        var paths = WriteReport(_context.Clock(), canceled);
        var published = Rows.Count(r => r.Outcome == OneClickOutcomes.Published);
        var failed = Rows.Count(r => r.Outcome == OneClickOutcomes.Failed);
        var superseded = Rows.Count(r => r.Outcome == OneClickOutcomes.Superseded);
        var label = stopped is not null ? $"Failed: {stopped}."
            : $"{(canceled ? "Canceled" : "Complete")}: {published} published, {failed} failed{(superseded > 0 ? $", {superseded} superseded" : "")}.";
        SummaryText = StatusLine = $"{label} Report: {Path.GetFileName(paths.MarkdownPath)}";
        _runCanceled = canceled;
        HasFailure = FirstFailed is not null;
        HasResults = included.Any(r => r.Cells.Count > 0);
        if (HasResults) ResultsReady?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _runCts?.Cancel();
}
