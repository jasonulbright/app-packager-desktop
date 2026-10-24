using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.ViewModels.Pages;

public sealed partial class DpContentManagerViewModel
{
    public const string NotConfirmed = "Nothing was changed.";
    public const string WriteRunning = "A change is still running. Wait until it ends, then start the next one.";
    public const string NotLoadedAction = "The distribution point content was not read. Select Refresh first.";
    public const string PrepareFailed = "The current state of the pairs could not be read. Nothing was changed. The log has the details.";
    public const string SelectionChanged = "The selection changed while the current state was read. Nothing was changed. Start the action again.";
    public const string SelectContent = "Select a content first.";
    public const string NoFailedDp = "The selected content has no DP whose install failed.";
    public const string SelectContentDps = "Select one or more DPs in the status list of the content first.";
    public const string SelectIssues = "Select one or more status issues first.";
    public const string SelectFailedIssues = "Select one or more failed status issues first. Remove failures removes only failed pairs.";
    public const string NoContentPairs = "The selected content has no DP status row.";
    public const string NothingToUndo = "There is no change to undo.";
    public const string BackgroundRunText =
        "The plan window was closed while the run was still working. The run continues in the background and can still change the distribution points. Wait until it ends before you start another change.";
    public const string BackgroundRunEndedText = "The run that continued in the background has ended. The lists were read again.";
    public const string ExportNotLoaded = "The rows were not read. Select Refresh, then export.";
    public const string NothingToExport = "The list has no rows to export.";

    public static string ReportFailed(string format) => $"The {format} report could not be written. The log has the details.";

    private bool _background;

    public ObservableCollection<ChangePlanItemResult> LastResults { get; } = [];

    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private bool _showResultRows = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoLastCommand), nameof(RedistributeFailedCommand), nameof(RedistributeSelectedDpsCommand), nameof(RemoveFromDpCommand),
        nameof(ValidateCommand), nameof(RedistributeIssuesCommand), nameof(RemoveFailuresCommand))]
    private bool _isWriting;

    /// <summary>The Distribute again of the pairs the last removal took from DPs that were Installed; it outlives the visit until a new run replaces it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUndo), nameof(HasResultPanel))]
    [NotifyCanExecuteChangedFor(nameof(UndoLastCommand))]
    private DpContentChange? _lastUndo;

    public bool HasUndo => LastUndo is not null;
    public bool HasResults => LastResults.Count > 0;
    public bool HasResultPanel => HasResults || HasUndo;

    /// <summary>The folder of the last report written; null before the first export.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReportsFolderCommand))]
    private string? _lastReportFolder;

    private void ReleaseResults()
    {
        LastResults.Clear();
        NotifyResults();
        LastReportFolder = null;
        ResultSummary = LastUndo is { } undo ? UndoText(undo) : "";
    }

    private static string UndoText(DpContentChange undo) =>
        $"Undo last change can distribute again {DpContentLabels.Count(undo.Pairs.Count, "pair")} that the last removal took from DPs where {(undo.Pairs.Count == 1 ? "it was" : "they were")} Installed.";

    private void NotifyResults()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasResultPanel));
    }

    private bool CanAct() => !IsWriting;

    private string ContentSelectionKey() => (SelectedContent?.Key ?? "") + "#" + string.Join(",", SelectedContentPairs.Select(r => r.Key).Order(StringComparer.Ordinal));

    private string IssueSelectionKey() => string.Join(",", SelectedIssues.Select(r => r.Key).Order(StringComparer.Ordinal));

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task RedistributeFailed()
    {
        if (SelectedContent is not { } content) return Say(SelectContent);
        var failed = PairsOf(content).Where(p => p.IsFailed && !DpContentLabels.IsRemoval(p.State)).ToList();
        if (failed.Count == 0) return Say(NoFailedDp);
        return RunActionAsync(DpContentChangeKind.Redistribute, "Redistribute failed", failed, null, ContentSelectionKey);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task RedistributeSelectedDps()
    {
        if (SelectedContent is null) return Say(SelectContent);
        if (SelectedContentPairs.Count == 0) return Say(SelectContentDps);
        return RunActionAsync(DpContentChangeKind.Redistribute, "Redistribute selected DPs", SelectedContentPairs.Select(r => r.Pair).ToList(), null, ContentSelectionKey);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task RemoveFromDp() => ContentAction(DpContentChangeKind.Remove, "Remove from DP");

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task Validate() => ContentAction(DpContentChangeKind.Validate, "Validate");

    /// <summary>
    /// Every pair of the selected content; the pairs selected in its status list are checked. With no
    /// selection a validation checks every pair and a removal checks none.
    /// </summary>
    private Task ContentAction(DpContentChangeKind kind, string title)
    {
        if (SelectedContent is not { } content) return Say(SelectContent);
        var pairs = PairsOf(content);
        if (pairs.Count == 0) return Say(NoContentPairs);
        IReadOnlySet<string>? selected = SelectedContentPairs.Count > 0 ? new HashSet<string>(SelectedContentPairs.Select(r => r.Key), StringComparer.Ordinal)
            : kind == DpContentChangeKind.Remove ? new HashSet<string>(StringComparer.Ordinal) : null;
        return RunActionAsync(kind, title, pairs, selected, ContentSelectionKey);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task RedistributeIssues()
    {
        if (SelectedIssues.Count == 0) return Say(SelectIssues);
        return RunActionAsync(DpContentChangeKind.Redistribute, "Redistribute selected", SelectedIssues.Select(r => r.Pair).ToList(), null, IssueSelectionKey);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task RemoveFailures()
    {
        var failed = SelectedIssues.Where(r => r.Pair.IsFailed).Select(r => r.Pair).ToList();
        if (failed.Count == 0) return Say(SelectedIssues.Count == 0 ? SelectIssues : SelectFailedIssues);
        return RunActionAsync(DpContentChangeKind.Remove, "Remove failures", failed, null, IssueSelectionKey);
    }

    private IReadOnlyList<PairRow> PairsOf(ContentGridRow content) => _index?.PairsOf(content.Content) ?? [];

    private Task Say(string message)
    {
        StatusMessage = message;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Shows the pairs of the action, then reads the current state of the checked pairs and shows the
    /// plan made from the prepared change. The selection is read once, before the read; a plan whose
    /// selection changed during the read is discarded.
    /// </summary>
    private async Task RunActionAsync(DpContentChangeKind kind, string title, IReadOnlyList<PairRow> candidates, IReadOnlySet<string>? checkedKeys, Func<string> selection)
    {
        if (IsWriting)
        {
            StatusMessage = _background ? BackgroundRunText : WriteRunning;
            return;
        }
        if (_index is null)
        {
            StatusMessage = NotLoadedAction;
            return;
        }
        if (ConnectionProblem() is { } problem)
        {
            StatusMessage = problem;
            return;
        }
        var picker = new DpContentPairsViewModel(title, PickIntro(kind), candidates, checkedKeys);
        Services.Dialogs.ShowPairs(picker);
        if (picker.Result is not { Count: > 0 } pairs)
        {
            StatusMessage = NotConfirmed;
            return;
        }
        var before = selection();
        var request = new DpContentChange(Kind: kind, Pairs: pairs, RemovalConfirmed: false, LastCopyAcknowledged: false, Impact: [], Checks: [], Fingerprint: null);
        await PlanAndRunAsync(request, () => string.Equals(selection(), before, StringComparison.Ordinal), isUndo: false);
    }

    public static string PickIntro(DpContentChangeKind kind) => kind switch
    {
        DpContentChangeKind.Remove => "Check each pair to remove and clear each pair that must keep its content. Only the checked pairs are read again and planned.",
        _ => "Clear each pair that must not change. Only the checked pairs are read again and planned."
    };

    /// <summary>Reads the current state, shows the plan and runs it when the operator confirms; true when a run changed the site.</summary>
    private async Task PlanAndRunAsync(DpContentChange request, Func<bool> stillSelected, bool isUndo)
    {
        var index = _index;
        var generation = Generation;
        var detached = false;
        var ran = false;
        IsWriting = true;
        try
        {
            StatusMessage = "Reading the current state of the pairs.";
            var visit = VisitToken;
            DpContentChange prepared;
            try { prepared = await Task.Run(() => Services.Content.PrepareAsync(request, visit), visit); }
            catch (OperationCanceledException) when (visit.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Services.Log.Warning(LogArea, $"The state before a write could not be read ({FailureName(ex)}).");
                if (generation == Generation) StatusMessage = PlainMessage(ex, PrepareFailed);
                return;
            }
            if (generation != Generation || index is null) return;
            if (!stillSelected())
            {
                StatusMessage = SelectionChanged;
                return;
            }
            var (plan, refusal) = DpContentText.BuildPlan(prepared, index);
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
                _ = WaitForBackgroundRunAsync(plan, isUndo);
                if (generation == Generation) StatusMessage = BackgroundRunText;
                return;
            }
            ran = ShowOutcome(plan, outcome, isUndo);
        }
        finally
        {
            if (!detached) IsWriting = false;
        }
        if (ran) await RefreshAfterWriteAsync();
    }

    /// <summary>Keeps every write blocked until the run that a closed plan window left working ends; its Undo stays and the lists are read again.</summary>
    private async Task WaitForBackgroundRunAsync(DpContentPlan plan, bool isUndo)
    {
        _background = true;
        try { await Services.Writer.Running; }
        catch (Exception ex)
        {
            Services.Log.Warning(LogArea, $"A run that continued in the background failed ({FailureName(ex)}).");
        }
        finally
        {
            KeepUndo(plan, isUndo);
            if (_active) StatusMessage = BackgroundRunEndedText;
            else if (LastUndo is { } undo && !HasResults) ResultSummary = UndoText(undo);
            _background = false;
            IsWriting = false;
        }
        if (_active) await RefreshAfterWriteAsync();
    }

    /// <summary>
    /// The Undo after a run: a removal that carries an Undo replaces the last one, and a run without
    /// an Undo (another kind, or a removal that removed nothing installed) keeps it; an Undo run keeps
    /// every pair that did not end Requested or Confirmed.
    /// </summary>
    private void KeepUndo(DpContentPlan plan, bool isUndo)
    {
        if (!isUndo)
        {
            if (plan.UndoChange() is { } undo) LastUndo = undo;
            return;
        }
        // Unchanged means the site shows no sign of the distribution, so the pair stays in the Undo.
        var done = new HashSet<string>(plan.Outcomes.SelectMany(o => o.Pairs).Where(p => p.Result is DpContentWriter.Requested or DpContentWriter.Confirmed)
            .Select(p => DpContentLabels.Key(p.Pair)), StringComparer.Ordinal);
        var left = plan.Change.Pairs.Where(p => !done.Contains(DpContentLabels.Key(p))).ToList();
        LastUndo = left.Count == 0 ? null : plan.Change with { Pairs = left, RemovalConfirmed = false, LastCopyAcknowledged = false, Impact = [], Checks = [], Fingerprint = null };
    }

    private bool ShowOutcome(DpContentPlan plan, ChangePlanOutcome outcome, bool isUndo)
    {
        if (!outcome.Confirmed || outcome.Result is not { } result)
        {
            StatusMessage = NotConfirmed;
            return false;
        }
        LastResults.Clear();
        foreach (var row in result.Items) LastResults.Add(row);
        NotifyResults();
        KeepUndo(plan, isUndo);
        ResultSummary = $"{plan.Plan.Title}: {result.Summary}";
        StatusMessage = ResultSummary;
        return true;
    }

    private bool CanUndoLast() => !IsWriting && LastUndo is not null;

    /// <summary>Shows the plan that distributes the removed pairs again: a plan of its own, made from the prepared change.</summary>
    [RelayCommand(CanExecute = nameof(CanUndoLast))]
    private async Task UndoLast()
    {
        if (LastUndo is not { } undo)
        {
            StatusMessage = NothingToUndo;
            return;
        }
        if (_index is null)
        {
            StatusMessage = NotLoadedAction;
            return;
        }
        if (ConnectionProblem() is { } problem)
        {
            StatusMessage = problem;
            return;
        }
        await PlanAndRunAsync(undo with { RemovalConfirmed = false, LastCopyAcknowledged = false }, () => true, isUndo: true);
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

    /// <summary>The rows of the active view as the filters show them; null when the view has not been read.</summary>
    public DpContentReport? ActiveReport()
    {
        FlushFilters();
        if (SelectedTabIndex == HistoryTab)
        {
            if (!IsHistoryLoaded) return null;
            return new DpContentReport("DP content history", ["Time", "Action", "Result", "Pairs", "Succeeded", "Failed", "Skipped", "Content", "DPs", "User", "Verified", "Message"],
                HistoryRows.Select(r => (IReadOnlyList<string>)[r.Record.Timestamp.ToString("u", System.Globalization.CultureInfo.InvariantCulture), r.Action, r.Result,
                    r.Record.PairCount.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Record.Succeeded.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    r.Record.Failed.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Record.Skipped.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    r.Content, r.Dps, r.User, r.VerifiedText, r.Message]).ToList(),
                IsHistoryCapped, IsHistoryCapped ? HistoryCapText : null);
        }
        if (_index is null) return null;
        var note = TruncationNote;
        return SelectedTabIndex switch
        {
            ContentTab => new DpContentReport("DP content: content", ["Status", "Name", "Type", "PackageID", "Size", "DPs", "Installed", "In progress", "Failed", "Percent OK", "Deployments"],
                ContentRows.Select(r => (IReadOnlyList<string>)[r.Status, r.Name, r.Type, r.PackageId, r.Size, r.Dps, r.Installed, r.InProgress, r.Failed, r.PercentOk, r.Deployments]).ToList(), note is not null, note),
            IssuesTab => new DpContentReport("DP content: status issues", ["Status", "DP", "Content", "Type", "State", "State number", "PackageID", "Last status"],
                IssueRows.Select(r => (IReadOnlyList<string>)[r.Status, r.DpName, r.ContentName, r.Type, r.StateName, r.State.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    r.PackageId, r.LastStatus]).ToList(), note is not null, note),
            _ => new DpContentReport("DP content: distribution points", ["Status", "DP", "Site", "Total", "Installed", "In progress", "Failed", "Percent OK", "Pull DP", "Size (GB)"],
                DpRows.Select(r => (IReadOnlyList<string>)[r.Status, r.DpName, r.SiteCode, r.Total, r.Installed, r.InProgress, r.Failed, r.PercentOk, r.PullDp, r.SizeGb]).ToList(), note is not null, note)
        };
    }

    /// <summary>Writes the rows the filters show; refuses when the view was not read, and names a truncated read in the file header.</summary>
    private async Task ExportAsync(string format, Func<DpContentReport, string> write)
    {
        if (ActiveReport() is not { } report)
        {
            StatusMessage = ExportNotLoaded;
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
            StatusMessage = ReportFailed(format);
            Services.Log.Warning(LogArea, $"The {format} report could not be written ({ex.GetType().Name}).");
            return;
        }
        StatusMessage = $"Saved the {format} report to {path}: {DpContentLabels.Count(report.Rows.Count, "row")}, the rows the filters show." + (report.Note is { } note ? " " + note : "");
        Services.Log.Info(LogArea, $"Saved a {format} report with {report.Rows.Count} rows.");
        LastReportFolder = Path.GetDirectoryName(path) is { Length: > 0 } folder ? folder : null;
    }
}
