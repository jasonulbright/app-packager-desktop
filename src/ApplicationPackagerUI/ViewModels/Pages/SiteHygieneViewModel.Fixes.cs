using System.Globalization;
using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.ViewModels.Pages;

public sealed partial class SiteHygieneViewModel
{
    public const string NotConfirmed = "Nothing was changed.";
    public const string WriteRunning = "A fix is still running. Wait until it ends, then start the next one.";
    public const string SelectFindings = "Select one or more findings first.";
    public const string ReportOnly = "The selected findings have no typed fix. They are for report only.";
    public const string SuppressedSelection = "The selected findings are suppressed. Select Unsuppress before a fix.";
    public const string PrepareFailed = "The findings could not be checked again on the site. Nothing was changed. The log has the details.";
    public const string SelectionChanged = "The selection changed while the findings were checked again. Nothing was changed. Start the fix again.";
    public const string ResultChanged = "A new scan result replaced the list while the findings were checked again. Nothing was changed. Start the fix again.";
    public const string BackgroundRunText =
        "The plan window was closed while the fix was still running. The fix continues in the background and can still change the site. Wait until it ends before you start another fix.";
    public const string NothingToSuppress = "Select one or more findings that are not suppressed.";
    public const string NothingToUnsuppress = "Select one or more suppressed findings. Show suppressed lists them.";
    public const string SuppressFailed = "The suppressions could not be saved. The log has the details.";
    public const string ExportNotScanned = "Nothing was scanned yet, so nothing can be exported. Select Scan first.";
    public const string ExportNoRelations = "The relationships were not read. Select Read relationships, then export.";
    /// <summary>Each tab that Export CSV and Export HTML write, by index and header, in tab order.</summary>
    public static readonly IReadOnlyList<(int Index, string Name)> ExportTabs =
        [(FindingsTab, "Findings"), (RelationsTab, "Relationships"), (LiveTab, "Live"), (SummaryTab, "Summary"), (HistoryTab, "History")];

    public static readonly string ExportOptions = $"The Options tab has no rows. Select the {OrList(ExportTabs.Select(t => t.Name).ToList())} tab to export.";

    /// <summary>"A", "A or B", "A, B or C".</summary>
    public static string OrList(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " or " + names[^1];
    public const string NothingToExport = "The list has no rows to export.";

    public static string MixedKinds(IEnumerable<HygieneFixKind> kinds) =>
        "The selected findings need different fixes: " + string.Join(", ", kinds.Select(SiteHygieneLabels.FixLabel)) + ". Select the findings of one fix, for example with the filter, then select Run fix.";

    public static string ReportFailed(string format) => $"The {format} report could not be written. The log has the details.";

    private bool _background;

    /// <summary>A finding list or a plan window closed: the view puts the keyboard focus back on the findings grid, on the selected row.</summary>
    public event EventHandler? FindingsFocusRequested;

    public ObservableCollection<ChangePlanItemResult> LastResults { get; } = [];

    [ObservableProperty] private string _resultSummary = "";

    /// <summary>The result card shows its rows; a finished run collapses it to the one-line summary, so the tab keeps its height.</summary>
    [ObservableProperty] private bool _resultsExpanded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunFixCommand), nameof(SuppressCommand), nameof(UnsuppressCommand))]
    private bool _isWriting;

    public bool HasResults => LastResults.Count > 0;

    /// <summary>The result rows show on the Findings tab only: on the other tabs they would take the height of the tab.</summary>
    public bool ShowsResults => HasResults && SelectedTabIndex == FindingsTab;

    /// <summary>The result card shows with the result rows, and on every tab while the audit log of the last fix is not complete.</summary>
    public bool ShowsResultPanel => ShowsResults || (HasResults && HasAuditWarning);

    /// <summary>The warning of a fix whose audit log has no Finished record; empty when the audit log is complete.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAuditWarning), nameof(ShowsResultPanel))]
    private string _auditWarning = "";

    public bool HasAuditWarning => AuditWarning.Length > 0;

    public const string AuditWarningStart = "The audit log is not complete: the Finished record of the last fix is missing.";

    public static string AuditWarningText(string path) => AuditWarningStart + (path.Length > 0 ? " Audit log: " + path : "");

    private void ShowAuditState(SiteHygienePlan plan) =>
        AuditWarning = plan.Outcomes.FirstOrDefault(o => o.AuditIncomplete) is { } missing ? AuditWarningText(missing.AuditPath) : "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReportsFolderCommand))]
    private string? _lastReportFolder;

    private bool CanAct() => !IsWriting;

    /// <summary>Run fix is available while no fix runs and the selection holds a finding this page can fix.</summary>
    private bool CanRunFix() => !IsWriting && RunFixHint.Length == 0;

    /// <summary>Why Run fix is not available: no result, no selection, or a selection the page cannot fix; empty when Run fix is available.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunFixHint))]
    [NotifyCanExecuteChangedFor(nameof(RunFixCommand))]
    private string _runFixHint = NotScanned;

    public bool HasRunFixHint => RunFixHint.Length > 0;

    partial void OnSelectedFindingsChanged(IReadOnlyList<FindingGridRow> value) => ShowRunFixHint();

    private void ShowRunFixHint() =>
        RunFixHint = _result is null ? NotScanned : SelectedFindings.Count == 0 ? SelectFindings : FixHint(SelectedFindings) ?? "";

    /// <summary>The reason a selection cannot go to a plan; null when it can.</summary>
    public static string? FixHint(IReadOnlyList<FindingGridRow> selected)
    {
        if (selected.Count == 0) return null;
        var open = selected.Where(r => !r.IsSuppressed).ToList();
        if (open.Count == 0) return SuppressedSelection;
        var runnable = open.Where(r => SiteHygieneLabels.IsFixable(r.Finding)).Select(r => r.Finding.FixKind).Distinct().ToList();
        if (runnable.Count > 1) return MixedKinds(runnable);
        if (runnable.Count == 1) return null;
        var reasons = open.Select(r => SiteHygieneLabels.ReportOnlyReason(r.Finding)).Distinct(StringComparer.Ordinal).ToList();
        return reasons.Count == 1 ? reasons[0] : ReportOnly;
    }

    private string SelectionKey() => string.Join(",", SelectedFindings.Select(r => r.Key).Order(StringComparer.Ordinal));

    [RelayCommand(CanExecute = nameof(CanRunFix))]
    private async Task RunFix()
    {
        if (IsWriting)
        {
            StatusMessage = _background ? BackgroundRunText : WriteRunning;
            return;
        }
        if (_result is null)
        {
            StatusMessage = NotScanned;
            return;
        }
        if (ConnectionProblem() is { } problem)
        {
            StatusMessage = problem;
            return;
        }
        if (!_result.IsFor(CurrentSite(), CurrentProvider()))
        {
            StatusMessage = SameSite(_result.SiteCode, CurrentSite()) ? OtherConnection(_result, CurrentSite(), CurrentProvider()) : OtherSite(_result.SiteCode, CurrentSite());
            return;
        }
        var generation = _resultGeneration;
        var selected = SelectedFindings.ToList();
        if (selected.Count == 0)
        {
            StatusMessage = SelectFindings;
            return;
        }
        if (FixHint(selected) is { } hint)
        {
            StatusMessage = hint;
            return;
        }
        var candidates = selected.Where(r => !r.IsSuppressed && SiteHygieneLabels.IsFixable(r.Finding)).ToList();
        var kind = candidates[0].Finding.FixKind;
        IReadOnlyList<HygieneFinding> picked = candidates.Select(r => r.Finding).ToList();
        var before = SelectionKey();
        bool StillSelected() => string.Equals(SelectionKey(), before, StringComparison.Ordinal);
        if (picked.Count > 1)
        {
            var picker = new HygieneFindingsPickerViewModel(SiteHygieneLabels.FixLabel(kind), PickIntro(kind), picked);
            Services.Dialogs.ShowFindings(picker);
            FindingsFocusRequested?.Invoke(this, EventArgs.Empty);
            if (picker.Result is not { Count: > 0 } chosen)
            {
                StatusMessage = NotConfirmed;
                return;
            }
            if (generation != _resultGeneration)
            {
                StatusMessage = ResultChangedAtPick;
                return;
            }
            if (!StillSelected())
            {
                StatusMessage = SelectionChangedAtPick;
                return;
            }
            picked = chosen;
        }
        var left = selected.Count - candidates.Count;
        var request = new HygieneChange(Kind: kind, Findings: picked, DeleteConfirmed: false, Impact: [], Checks: [], Fingerprint: null);
        await PlanAndRunAsync(request, StillSelected, left, generation);
    }

    public const string ResultChangedAtPick = "A new scan result replaced the list while the finding list was open. Nothing was changed. Start the fix again.";
    public const string SelectionChangedAtPick = "The selection changed while the finding list was open. Nothing was changed. Start the fix again.";

    public static string PickIntro(HygieneFixKind kind) => SiteHygieneLabels.IsDelete(kind)
        ? "Clear each object that must stay. Only the checked objects are checked again on the site and planned. A delete cannot be undone."
        : "Clear each object that must not change. Only the checked objects are checked again on the site and planned.";

    /// <summary>
    /// Checks the findings again on the site, shows the plan made from the prepared change, and runs it
    /// when the operator confirms. The selection is read once, before the check; a plan whose selection
    /// or result changed during the check is discarded.
    /// </summary>
    private async Task PlanAndRunAsync(HygieneChange request, Func<bool> stillSelected, int leftOut, int generation)
    {
        var detached = false;
        IsWriting = true;
        try
        {
            StatusMessage = "Checking the findings again on the site.";
            HygieneChange prepared;
            try { prepared = await Task.Run(() => Services.Hygiene.PrepareAsync(request, CancellationToken.None)); }
            catch (Exception ex)
            {
                Services.Log.Warning(LogArea, $"The check before a fix failed ({FailureName(ex)}).");
                StatusMessage = PlainMessage(ex, PrepareFailed);
                return;
            }
            if (generation != _resultGeneration)
            {
                StatusMessage = ResultChanged;
                return;
            }
            if (!stillSelected())
            {
                StatusMessage = SelectionChanged;
                return;
            }
            var (plan, refusal) = SiteHygieneText.BuildPlan(prepared);
            if (plan is null)
            {
                StatusMessage = refusal ?? NotConfirmed;
                return;
            }
            StatusMessage = "";
            var outcome = Services.Plans.Show(plan.Plan, Services.Writer.RunFor(plan));
            FindingsFocusRequested?.Invoke(this, EventArgs.Empty);
            if (outcome.Result?.Stop == ChangePlanStop.Detached)
            {
                detached = true;
                _ = WaitForBackgroundRunAsync(plan);
                StatusMessage = BackgroundRunText;
                return;
            }
            ShowOutcome(plan, outcome, leftOut);
        }
        finally
        {
            if (!detached) IsWriting = false;
        }
    }

    /// <summary>Keeps every fix blocked until the run that a closed plan window left working ends; then the fixed findings leave the list.</summary>
    private async Task WaitForBackgroundRunAsync(SiteHygienePlan plan)
    {
        _background = true;
        try { await Services.Writer.Running; }
        catch (Exception ex)
        {
            Services.Log.Warning(LogArea, $"A fix that continued in the background failed ({FailureName(ex)}).");
        }
        finally
        {
            var done = MarkFixed(plan);
            ShowAuditState(plan);
            StatusMessage = $"The fix that continued in the background has ended: {SiteHygieneLabels.Count(done, "finding")} fixed." + (HasAuditWarning ? " " + AuditWarning : "");
            _background = false;
            IsWriting = false;
            HistoryChanged();
        }
    }

    private int MarkFixed(SiteHygienePlan plan)
    {
        var done = plan.DoneKeys();
        foreach (var key in done) _fixedKeys.Add(key);
        if (done.Count > 0)
        {
            ApplyFindingFilter();
            RefreshSummary();
        }
        return done.Count;
    }

    private void ShowOutcome(SiteHygienePlan plan, ChangePlanOutcome outcome, int leftOut)
    {
        if (!outcome.Confirmed || outcome.Result is not { } result)
        {
            StatusMessage = NotConfirmed;
            return;
        }
        LastResults.Clear();
        foreach (var row in result.Items) LastResults.Add(row);
        ResultsExpanded = false;
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowsResults));
        OnPropertyChanged(nameof(ShowsResultPanel));
        MarkFixed(plan);
        ShowAuditState(plan);
        HistoryChanged();
        ResultSummary = $"{plan.Plan.Title}: {result.Summary}";
        StatusMessage = ResultSummary + (leftOut > 0 ? $" {SiteHygieneLabels.Count(leftOut, "selected finding")} had no fix or {(leftOut == 1 ? "was" : "were")} suppressed and {(leftOut == 1 ? "was" : "were")} not in the plan." : "");
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Suppress()
    {
        var rows = SelectedFindings.Where(r => !r.IsSuppressed).ToList();
        if (rows.Count == 0)
        {
            StatusMessage = NothingToSuppress;
            return;
        }
        var comment = Services.Dialogs.AskComment("Suppress findings",
            $"Why {(rows.Count == 1 ? "is this finding" : $"are these {SiteHygieneLabels.Number(rows.Count)} findings")} suppressed? The comment is saved with each suppression. A suppressed finding is hidden until Show suppressed is on.");
        if (comment is not { Length: > 0 })
        {
            StatusMessage = NotConfirmed;
            return;
        }
        var now = DateTime.UtcNow;
        var site = ResultSite();
        var provider = ResultProvider();
        await ChangeSuppressionsAsync(rows,
            list => Services.Suppressions.AddRange(list.Select(r => new Suppression(CheckId: r.Finding.CheckId, ObjectKey: r.Finding.ObjectKey, Comment: comment, CreatedUtc: now) { SiteCode = site, Provider = provider }).ToList()),
            n => $"Suppressed {SiteHygieneLabels.Count(n, "finding")}." + (ShowSuppressed ? "" : " Show suppressed lists them."), "Suppress", site, provider);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Unsuppress()
    {
        var rows = SelectedFindings.Where(r => r.IsSuppressed).ToList();
        if (rows.Count == 0)
        {
            StatusMessage = NothingToUnsuppress;
            return;
        }
        var site = ResultSite();
        var provider = ResultProvider();
        await ChangeSuppressionsAsync(rows, list => Services.Suppressions.RemoveRange(site, list.Select(r => (r.Finding.CheckId, r.Finding.ObjectKey)).ToList(), provider),
            n => $"Unsuppressed {SiteHygieneLabels.Count(n, "finding")}.", "Unsuppress", site, provider);
    }

    /// <summary>
    /// Writes the suppression change of every row off the UI thread in one read and one write of the store,
    /// then reads the store again so the rows show what it holds. A failed write changes no row.
    /// </summary>
    private async Task ChangeSuppressionsAsync(IReadOnlyList<FindingGridRow> rows, Action<IReadOnlyList<FindingGridRow>> change, Func<int, string> done, string action, string site, string provider)
    {
        Exception? failure = null;
        await Task.Run(() =>
        {
            try { change(rows); }
            catch (Exception ex) { failure = ex; }
        });
        _suppressions = await Task.Run(() => ReadSuppressions(site, provider));
        BuildRows();
        ApplyFindingFilter();
        RefreshSummary();
        var count = failure is null ? rows.Count : 0;
        Services.Log.Info(LogArea, $"{action}: {count} of {rows.Count} findings.");
        if (failure is not null)
        {
            Services.Log.Warning(LogArea, $"{action} stopped ({failure.GetType().Name}).");
            StatusMessage = PlainMessage(failure, SuppressFailed) + " No suppression was changed.";
            return;
        }
        StatusMessage = done(count);
    }

    private bool CanCopyFixText() => DetailFinding?.Finding.FixText is { Length: > 0 };

    [RelayCommand(CanExecute = nameof(CanCopyFixText))]
    private void CopyFixText()
    {
        if (DetailFinding?.Finding.FixText is not { Length: > 0 } text) return;
        Services.Dialogs.SetClipboard(text);
        StatusMessage = "Copied the PowerShell text of the finding. The page does not run this text.";
    }

    [RelayCommand]
    private Task ExportCsv() => ExportAsync("CSV", Services.Reports.WriteCsv);

    [RelayCommand]
    private Task ExportHtml() => ExportAsync("HTML", Services.Reports.WriteHtml);

    private bool CanOpenReportsFolder() => LastReportFolder is not null || ReportFolder.Length > 0;

    [RelayCommand(CanExecute = nameof(CanOpenReportsFolder))]
    private void OpenReportsFolder()
    {
        var folder = LastReportFolder ?? ReportFolder;
        if (folder.Length > 0) Services.Dialogs.OpenFolder(folder);
    }

    public static string FilterText(params (string Value, string Name)[] filters) =>
        string.Join(" ", filters.Where(f => !string.IsNullOrWhiteSpace(f.Value)).Select(f => $"{f.Name}: {f.Value.Trim()}."));

    /// <summary>The truncation, the note of the scan and the source of the result, for the file header; null when there is nothing to say.</summary>
    private string? ScanNote()
    {
        if (_result is not { } result) return null;
        var parts = new List<string>();
        if (result.Truncated) parts.Add("The read was truncated: the findings are not complete.");
        if (result.Note is { Length: > 0 } note) parts.Add(SecretMask.Apply(note));
        var notSelected = result.Checks.Count(SiteHygieneLabels.WasNotSelected);
        if (notSelected > 0) parts.Add($"{SiteHygieneLabels.Count(notSelected, "check")} {(notSelected == 1 ? "was" : "were")} not in the scanned areas or options.");
        if (_isStored) parts.Add("Stored result of the last scan, not read from the site in this session.");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private int HiddenSuppressed() => LiveRows.Count(r => r.IsSuppressed);

    /// <summary>Each check that did not run as "ID: reason", for the file header.</summary>
    private IReadOnlyList<string> ChecksNotRunLines() =>
        (_result?.Checks ?? []).Where(SiteHygieneLabels.IsNotRun)
            .Select(c => $"{c.CheckId}: {SiteHygieneLabels.StateText(c.State)}.{(c.Reason is { Length: > 0 } r ? " " + SecretMask.Apply(r) : "")}").ToList();

    /// <summary>The rows of the active view as the filters show them; null with the reason when the view has nothing.</summary>
    public (HygieneReport? Report, string? Problem) ActiveReport()
    {
        FlushFilters();
        var culture = CultureInfo.InvariantCulture;
        switch (SelectedTabIndex)
        {
            case OptionsTab:
                return (null, ExportOptions);
            case LiveTab:
                return (null, "The Live tab exports through its own report writer.");
            case HistoryTab:
                if (!IsHistoryLoaded) return (null, HistoryNotRead);
                return (new HygieneReport(View: "History", Columns: ["Time", "Action", "Phase", "Result", "Objects", "Site", "Provider", "User", "Verified", "Run", "Message"],
                    Rows: HistoryRows.Select(r => (IReadOnlyList<string>)[r.Record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", culture), r.Action, r.Record.Phase, r.Result, r.Objects, r.Site, r.Provider,
                        r.User, r.VerifiedText, r.Record.RunId, SecretMask.Apply(r.Record.Message)]).ToList(), Truncated: false, Note: HistoryCapText.Length > 0 ? HistoryCapText : null)
                { Filter = FilterText((HistoryText, "Text")) }, null);
            case RelationsTab:
                if (_relations is not { } relations) return (null, ExportNoRelations);
                return (new HygieneReport("Relationships", ["Kind", "From key", "From", "To key", "To", "Health"],
                    EdgeRows.Select(r => (IReadOnlyList<string>)[r.Kind, r.Edge.FromKey, r.Edge.FromName, r.Edge.ToKey, r.Edge.ToName, r.Edge.Health]).ToList(),
                    relations.Truncated, relations.Truncated ? "The read was truncated: the relationships are not complete." : null)
                { Filter = FilterText((EdgeText, "Text"), (EdgeKind == AllText ? "" : EdgeKind, "Kind")) }, null);
            case SummaryTab:
                if (_result is not { } summary) return (null, ExportNotScanned);
                return (new HygieneReport("Check status", ["Check", "Name", "Category", "Severity", "State", "Findings", "Reason"],
                    CheckRows.Select(r => (IReadOnlyList<string>)[r.CheckId, r.Name, r.Category, SiteHygieneLabels.SeverityName(r.Check.Severity), r.State, r.Findings, r.Reason]).ToList(),
                    summary.Truncated, ScanNote()) { ChecksNotRun = ChecksNotRunLines(), ScannedUtc = summary.ScannedUtc }, null);
            default:
                if (_result is not { } scan) return (null, ExportNotScanned);
                return (new HygieneReport("Findings", ["Severity", "Check", "Category", "Object type", "Object key", "Object", "Evidence", "Recommended action", "New", "Fix", "Suppressed"],
                    FindingRows.Select(r => (IReadOnlyList<string>)[SiteHygieneLabels.SeverityName(r.Finding.Severity), r.CheckId, r.Category, r.Finding.ObjectType, r.Finding.ObjectKey,
                        r.Finding.ObjectName, r.Finding.Evidence, r.Finding.Recommendation, r.Finding.IsNew ? "Yes" : "No", SiteHygieneLabels.FixColumn(r.Finding), r.IsSuppressed ? "Yes" : "No"]).ToList(),
                    scan.Truncated, ScanNote())
                {
                    Filter = FilterText((FindingText, "Text"), (CategoryFilter == AllText ? "" : CategoryFilter, "Category"), (SeverityFilter == AllText ? "" : SeverityFilter, "Severity"),
                        (ShowSuppressed ? "shown" : HiddenSuppressed() is > 0 and var hidden ? $"{SiteHygieneLabels.Number(hidden)} hidden" : "", "Suppressed"),
                        (_allFindings.Count - LiveRows.Count() is > 0 and var omitted ? $"{SiteHygieneLabels.Number(omitted)} omitted" : "", "Fixed since the scan")),
                    ChecksNotRun = ChecksNotRunLines(),
                    ScannedUtc = scan.ScannedUtc
                }, null);
        }
    }

    /// <summary>Writes the rows the filters show; refuses when the view was not read, and names truncation and the checks that did not run in the file header.</summary>
    private async Task ExportAsync(string format, Func<HygieneReport, string> write)
    {
        if (SelectedTabIndex == LiveTab)
        {
            await ExportLiveAsync(format);
            return;
        }
        var (report, problem) = ActiveReport();
        if (report is null)
        {
            StatusMessage = problem ?? NothingToExport;
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
            StatusMessage = ex is HygieneServiceException ? PlainMessage(ex, ReportFailed(format)) : ReportFailed(format);
            Services.Log.Warning(LogArea, $"The {format} report could not be written ({ex.GetType().Name}).");
            return;
        }
        StatusMessage = $"Saved the {format} report to {path}: {SiteHygieneLabels.Count(report.Rows.Count, "row")}." + (report.Filter.Length > 0 ? " Filter: " + report.Filter : " No filter.")
            + (report.Note is { } note ? " " + note : "")
            + NotesNote(format, path);
        Services.Log.Info(LogArea, $"Saved a {format} report with {report.Rows.Count} rows.");
        LastReportFolder = Path.GetDirectoryName(path) is { Length: > 0 } folder ? folder : null;
    }

    /// <summary>The sentence of a saved report that names the notes file; a CSV report keeps its notes in a separate file.</summary>
    private static string NotesNote(string format, string path) =>
        format == "CSV" ? $" The notes are in {Path.GetFileName(HygieneReportWriter.AboutPath(path))}." : "";
}
