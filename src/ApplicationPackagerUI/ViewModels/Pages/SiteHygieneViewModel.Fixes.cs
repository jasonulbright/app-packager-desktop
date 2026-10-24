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

    private string SelectionKey() => string.Join(",", SelectedFindings.Select(r => r.Key).Order(StringComparer.Ordinal));

    [RelayCommand(CanExecute = nameof(CanAct))]
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
        var selected = SelectedFindings.ToList();
        if (selected.Count == 0)
        {
            StatusMessage = SelectFindings;
            return;
        }
        var candidates = selected.Where(r => !r.IsSuppressed && SiteHygieneLabels.IsFixable(r.Finding)).ToList();
        if (candidates.Count == 0)
        {
            StatusMessage = selected.All(r => r.IsSuppressed) ? SuppressedSelection : ReportOnly;
            return;
        }
        var kinds = candidates.Select(r => r.Finding.FixKind).Distinct().ToList();
        if (kinds.Count > 1)
        {
            StatusMessage = MixedKinds(kinds);
            return;
        }
        var kind = kinds[0];
        IReadOnlyList<HygieneFinding> picked = candidates.Select(r => r.Finding).ToList();
        if (picked.Count > 1)
        {
            var picker = new HygieneFindingsPickerViewModel(SiteHygieneLabels.FixLabel(kind), PickIntro(kind), picked);
            Services.Dialogs.ShowFindings(picker);
            if (picker.Result is not { Count: > 0 } chosen)
            {
                StatusMessage = NotConfirmed;
                return;
            }
            picked = chosen;
        }
        var left = selected.Count - candidates.Count;
        var before = SelectionKey();
        var request = new HygieneChange(Kind: kind, Findings: picked, DeleteConfirmed: false, Impact: [], Checks: [], Fingerprint: null);
        await PlanAndRunAsync(request, () => string.Equals(SelectionKey(), before, StringComparison.Ordinal), left);
    }

    public static string PickIntro(HygieneFixKind kind) => SiteHygieneLabels.IsDelete(kind)
        ? "Clear each object that must stay. Only the checked objects are checked again on the site and planned. A delete cannot be undone."
        : "Clear each object that must not change. Only the checked objects are checked again on the site and planned.";

    /// <summary>
    /// Checks the findings again on the site, shows the plan made from the prepared change, and runs it
    /// when the operator confirms. The selection is read once, before the check; a plan whose selection
    /// or result changed during the check is discarded.
    /// </summary>
    private async Task PlanAndRunAsync(HygieneChange request, Func<bool> stillSelected, int leftOut)
    {
        var generation = _resultGeneration;
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
        await ChangeSuppressionsAsync(rows, r => Services.Suppressions.Add(new Suppression(CheckId: r.Finding.CheckId, ObjectKey: r.Finding.ObjectKey, Comment: comment, CreatedUtc: now)),
            n => $"Suppressed {SiteHygieneLabels.Count(n, "finding")}." + (ShowSuppressed ? "" : " Show suppressed lists them."), "Suppress");
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
        await ChangeSuppressionsAsync(rows, r => Services.Suppressions.Remove(r.Finding.CheckId, r.Finding.ObjectKey),
            n => $"Unsuppressed {SiteHygieneLabels.Count(n, "finding")}.", "Unsuppress");
    }

    /// <summary>Writes the suppression change of each row off the UI thread, then reads the store again so the rows show what it holds.</summary>
    private async Task ChangeSuppressionsAsync(IReadOnlyList<FindingGridRow> rows, Action<FindingGridRow> change, Func<int, string> done, string action)
    {
        var count = 0;
        Exception? failure = null;
        await Task.Run(() =>
        {
            foreach (var row in rows)
            {
                try
                {
                    change(row);
                    count++;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    break;
                }
            }
        });
        _suppressions = await Task.Run(ReadSuppressions);
        BuildRows();
        ApplyFindingFilter();
        RefreshSummary();
        Services.Log.Info(LogArea, $"{action}: {count} of {rows.Count} findings.");
        if (failure is not null)
        {
            Services.Log.Warning(LogArea, $"{action} stopped ({failure.GetType().Name}).");
            StatusMessage = (count > 0 ? done(count) + " " : "") + PlainMessage(failure, SuppressFailed);
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

    /// <summary>The truncation and the checks that did not run, for the file header; null when every check ran on a full read.</summary>
    private string? ScanNote()
    {
        if (_result is not { } result) return null;
        var parts = new List<string>();
        if (result.Truncated) parts.Add("The read was truncated: the findings are not complete.");
        var notRun = result.Checks.Where(c => !SiteHygieneLabels.DidRun(c)).ToList();
        if (notRun.Count > 0) parts.Add($"{SiteHygieneLabels.Count(notRun.Count, "check")} did not run: " + string.Join(", ", notRun.Select(c => c.CheckId)) + ".");
        parts.Add($"Scan of {result.ScannedUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}{(_isStored ? ", stored result" : "")}.");
        return string.Join(" ", parts);
    }

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
                return (new HygieneReport("Check status", ["Check", "Name", "Category", "State", "Findings", "Reason"],
                    CheckRows.Select(r => (IReadOnlyList<string>)[r.CheckId, r.Name, r.Category, r.State, r.Findings, r.Reason]).ToList(), summary.Truncated, ScanNote()), null);
            default:
                if (_result is not { } scan) return (null, ExportNotScanned);
                return (new HygieneReport("Findings", ["Severity", "Check", "Category", "Object type", "Object key", "Object", "Evidence", "Recommended action", "New", "Fix", "Suppressed"],
                    FindingRows.Select(r => (IReadOnlyList<string>)[SiteHygieneLabels.SeverityName(r.Finding.Severity), r.CheckId, r.Category, r.Finding.ObjectType, r.Finding.ObjectKey,
                        r.Finding.ObjectName, r.Finding.Evidence, r.Finding.Recommendation, r.Finding.IsNew ? "Yes" : "No", SiteHygieneLabels.FixLabel(r.Finding.FixKind), r.IsSuppressed ? "Yes" : "No"]).ToList(),
                    scan.Truncated, ScanNote())
                {
                    Filter = FilterText((FindingText, "Text"), (CategoryFilter == AllText ? "" : CategoryFilter, "Category"), (SeverityFilter == AllText ? "" : SeverityFilter, "Severity"),
                        (ShowSuppressed ? "shown" : "", "Suppressed"))
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
            StatusMessage = ex is SiteHygieneServiceException ? PlainMessage(ex, ReportFailed(format)) : ReportFailed(format);
            Services.Log.Warning(LogArea, $"The {format} report could not be written ({ex.GetType().Name}).");
            return;
        }
        StatusMessage = $"Saved the {format} report to {path}: {SiteHygieneLabels.Count(report.Rows.Count, "row")}." + (report.Filter.Length > 0 ? " Filter: " + report.Filter : " No filter.")
            + (report.Note is { } note ? " " + note : "");
        Services.Log.Info(LogArea, $"Saved a {format} report with {report.Rows.Count} rows.");
        LastReportFolder = Path.GetDirectoryName(path) is { Length: > 0 } folder ? folder : null;
    }
}
