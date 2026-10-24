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
    public const string ExportOptions = "The Options tab has no rows. Select the Findings, Relationships or Summary tab to export.";
    public const string NothingToExport = "The list has no rows to export.";

    public static string MixedKinds(IEnumerable<HygieneFixKind> kinds) =>
        "The selected findings need different fixes: " + string.Join(", ", kinds.Select(SiteHygieneLabels.FixLabel)) + ". Select the findings of one fix, for example with the filter, then select Run fix.";

    public static string ReportFailed(string format) => $"The {format} report could not be written. The log has the details.";

    private bool _background;

    public ObservableCollection<ChangePlanItemResult> LastResults { get; } = [];

    [ObservableProperty] private string _resultSummary = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunFixCommand), nameof(SuppressCommand), nameof(UnsuppressCommand))]
    private bool _isWriting;

    public bool HasResults => LastResults.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReportsFolderCommand))]
    private string? _lastReportFolder;

    private bool CanAct() => !IsWriting;

    /// <summary>Run fix is available while no fix runs and the selection holds a finding this page can fix.</summary>
    private bool CanRunFix() => !IsWriting && RunFixHint.Length == 0;

    /// <summary>Why Run fix is not available for the selection; empty when it is, or when nothing is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunFixHint))]
    [NotifyCanExecuteChangedFor(nameof(RunFixCommand))]
    private string _runFixHint = "";

    public bool HasRunFixHint => RunFixHint.Length > 0;

    partial void OnSelectedFindingsChanged(IReadOnlyList<FindingGridRow> value) => RunFixHint = FixHint(value) ?? "";

    /// <summary>The reason a selection cannot go to a plan; null when it can.</summary>
    public static string? FixHint(IReadOnlyList<FindingGridRow> selected)
    {
        if (selected.Count == 0) return null;
        var open = selected.Where(r => !r.IsSuppressed).ToList();
        if (open.Count == 0) return SuppressedSelection;
        var runnable = open.Where(r => SiteHygieneLabels.IsFixable(r.Finding)).Select(r => r.Finding.FixKind).Distinct().ToList();
        if (runnable.Count > 1) return MixedKinds(runnable);
        if (runnable.Count == 1) return null;
        var kinds = open.Select(r => r.Finding.FixKind).Distinct().ToList();
        return kinds.Count == 1 ? SiteHygieneLabels.ReportOnlyReason(kinds[0]) : ReportOnly;
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
        if (!SameSite(_result.SiteCode, CurrentSite()))
        {
            StatusMessage = OtherSite(_result.SiteCode, CurrentSite());
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
            StatusMessage = $"The fix that continued in the background has ended: {SiteHygieneLabels.Count(done, "finding")} fixed.";
            _background = false;
            IsWriting = false;
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
        OnPropertyChanged(nameof(HasResults));
        MarkFixed(plan);
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
        await ChangeSuppressionsAsync(rows,
            list => Services.Suppressions.AddRange(list.Select(r => new Suppression(CheckId: r.Finding.CheckId, ObjectKey: r.Finding.ObjectKey, Comment: comment, CreatedUtc: now) { SiteCode = site }).ToList()),
            n => $"Suppressed {SiteHygieneLabels.Count(n, "finding")}." + (ShowSuppressed ? "" : " Show suppressed lists them."), "Suppress", site);
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
        await ChangeSuppressionsAsync(rows, list => Services.Suppressions.RemoveRange(site, list.Select(r => (r.Finding.CheckId, r.Finding.ObjectKey)).ToList()),
            n => $"Unsuppressed {SiteHygieneLabels.Count(n, "finding")}.", "Unsuppress", site);
    }

    /// <summary>
    /// Writes the suppression change of every row off the UI thread in one read and one write of the store,
    /// then reads the store again so the rows show what it holds. A failed write changes no row.
    /// </summary>
    private async Task ChangeSuppressionsAsync(IReadOnlyList<FindingGridRow> rows, Action<IReadOnlyList<FindingGridRow>> change, Func<int, string> done, string action, string site)
    {
        Exception? failure = null;
        await Task.Run(() =>
        {
            try { change(rows); }
            catch (Exception ex) { failure = ex; }
        });
        _suppressions = await Task.Run(() => ReadSuppressions(site));
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
    private async Task OpenLiveViews()
    {
        SuiteToolLaunchResult result;
        try { result = await Services.OpenLiveViews(); }
        catch (Exception ex)
        {
            Services.Log.Warning(LogArea, $"The Live views could not start ({ex.GetType().Name}).");
            StatusMessage = "The Live views could not start. The log has the details.";
            return;
        }
        StatusMessage = result.Outcome switch
        {
            SuiteToolLaunchOutcome.Started => "The Live views open in the Site Hygiene suite tool.",
            SuiteToolLaunchOutcome.BroughtToFront => "The Site Hygiene suite tool is open.",
            _ => result.Message ?? "The Live views could not start."
        };
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
                        r.Finding.ObjectName, r.Finding.Evidence, r.Finding.Recommendation, r.Finding.IsNew ? "Yes" : "No", SiteHygieneLabels.FixColumn(r.Finding.FixKind), r.IsSuppressed ? "Yes" : "No"]).ToList(),
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
            + (report.Note is { } note ? " " + note : "");
        Services.Log.Info(LogArea, $"Saved a {format} report with {report.Rows.Count} rows.");
        LastReportFolder = Path.GetDirectoryName(path) is { Length: > 0 } folder ? folder : null;
    }
}
