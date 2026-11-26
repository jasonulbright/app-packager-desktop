using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>The end of a publish: the application to select, when the operator asked to review one.</summary>
public sealed class PublishEndedEventArgs(PackagerInfo? review) : EventArgs
{
    public PackagerInfo? Review { get; } = review;
}

/// <summary>The results of a publish run, and the retry of a destination that failed.</summary>
public partial class PackagesViewModel
{
    /// <summary>What a retry takes over from the run: the checked rows and the build that the run published.</summary>
    private sealed record PublishRunBuild(IReadOnlyList<PackagerInfo> Rows, RunSnapshot? Snapshot, string? SealedVersion,
        Func<(string Version, string? Failure)>? ReadBuild);

    /// <summary>
    /// Shows the results. A retry that the operator asks for there runs after the window closed, and the results open again with its cell in
    /// place of the failed one, or with the reason why it did not run.
    /// </summary>
    private async Task ShowResultsAsync(List<(string Application, PublishRowResult Result)> rows, bool cancelled,
        IReadOnlyList<string> systems, PublishRunBuild build, IReadOnlySet<string>? withoutDeployment = null)
    {
        var note = "";
        while (true)
        {
            var settings = _settingsService.Load();
            var siteLine = SiteFooter.Format(settings.SiteCode, settings.ProviderMachine);
            var report = WritePublishReport(rows, cancelled, systems, siteLine);
            var results = new PublishResultsViewModel(rows, cancelled, systems, offerRetry: true)
            {
                RetryNote = note,
                SiteLine = siteLine,
                ReportPath = report,
                WithoutDeployment = withoutDeployment ?? new HashSet<string>(),
                ShowLogs = owner => new Views.Windows.LogWindow(this) { Owner = owner }.ShowDialog(),
                ShowHistory = _inventory is null ? null : (uid, owner) =>
                {
                    if (build.Rows.FirstOrDefault(p => p.Uid == uid) is { } packager)
                        new Views.Windows.OperationHistoryWindow(packager, _inventory) { Owner = owner }.ShowDialog();
                }
            };
            _publishDialogs.ShowPublishResults(results);
            if (results.ReviewUid.Length > 0) _reviewAfterPublish = build.Rows.FirstOrDefault(p => p.Uid == results.ReviewUid);
            if (results.RequestedRetry is not { } asked) return;

            var index = rows.FindIndex(r => r.Result.Uid == asked.Uid);
            var row = build.Rows.First(p => p.Uid == asked.Uid);
            var (cell, why) = await RetryDestinationAsync(row, asked.Destination, rows[index].Result.Version, build);
            note = why;
            if (cell is null) continue;
            var cells = rows[index].Result.Destinations.Select(d => d.Destination == cell.Destination ? cell : d).ToList();
            rows[index] = (rows[index].Application, rows[index].Result with { Destinations = cells });
            // The row stays checked while any of its systems failed.
            row.IsSelected = cells.Any(c => c.Kind == PublishDestinationKind.Failed);
        }
    }

    private string WritePublishReport(IReadOnlyList<(string Application, PublishRowResult Result)> rows, bool cancelled,
        IReadOnlyList<string> systems, string siteLine)
    {
        try
        {
            var path = ApplicationPackagerEngine.Services.PublishReportWriter.Write(PublishReportFolder, DateTime.Now, siteLine,
                new PublishResultsViewModel(rows, cancelled, systems).Headline, cancelled, rows);
            AddLogLine("Publish report: " + path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLogLine("The publish report was not written: " + ex.Message);
            return "";
        }
    }

    /// <summary>The folder of the Publish All run reports.</summary>
    public static string PublishReportFolder => ApplicationPackagerEngine.Services.PublishReportWriter.Folder(LogDir);

    /// <summary>The application that Review failed application asked for; the page selects it when the publish ends.</summary>
    private PackagerInfo? _reviewAfterPublish;

    /// <summary>One failed destination of one application again: the same version, through the publish review and its read before the write. A
    /// ConfigMgr retry writes nothing when ConfigMgr has the version or cannot be read, and it keeps an existing application without asking,
    /// so it never replaces one. Returns the new cell, or null and the reason why nothing ran.
    /// </summary>
    private async Task<(PublishDestinationResult? Cell, string Note)> RetryDestinationAsync(
        PackagerInfo p, string destination, string version, PublishRunBuild build)
    {
        var what = $"Retry of {destination} for {p.Application}";
        (PublishDestinationResult?, string) Refuse(string reason)
        {
            var text = $"{what} did not run. {reason} Nothing was published.";
            AddLogLine(text);
            StatusText = text;
            return (null, text);
        }
        string? VersionRefusal()
        {
            if (build.ReadBuild?.Invoke().Failure is { } gone) return gone;
            var now = build.ReadBuild?.Invoke().Version ?? build.SealedVersion ?? PackagerExecutionService.PublishVersion(p);
            return string.Equals(now, version, StringComparison.Ordinal) ? null
                : $"The failed publish was of version {version}, and the version is now {(now.Length > 0 ? now : "unknown")}. Use Publish All to review it.";
        }

        IsOperationRunning = true;
        try
        {
            var settings = _settingsService.Load();
            if (VersionRefusal() is { } changed) return Refuse(changed);

            var site = ConfigMgrFact.None;
            if (destination == OneClickDestinations.ConfigMgr)
            {
                site = await ReadConfigMgrBeforeRetryAsync(p);
                if (PublishRetry.ConfigMgrRefusal(site, version) is { } held) return Refuse(held);
            }

            var plan = BuildPlan([PlanApp(p, settings, hidden: false, site, build.SealedVersion)], settings, destination);
            if (plan.Systems.Count == 0) return Refuse($"{destination} is no longer in use (Settings, Systems in Use).");
            var confirmed = await PublishReviewGate.ConfirmAsync(plan, Comment, _publishDialogs.ShowPublishReview,
                shown => RereadPlanAsync(shown, [p], build.ReadBuild), AddLogLine);
            if (confirmed is null) return Refuse("The review closed without a confirmation.");
            Comment = confirmed.Comment;
            if (VersionRefusal() is { } changedAfter) return Refuse(changedAfter);
            if (destination == OneClickDestinations.ConfigMgr
                && PublishRetry.ConfigMgrRefusal(confirmed.Plan.Rows[0].App.Site, version) is { } appeared) return Refuse(appeared);

            settings = _settingsService.Load();
            if (destination == OneClickDestinations.Wsus && !ConfirmWsusPrerequisites(settings, "Retry")) return Refuse("WSUS is not ready.");
            if (ConfirmLocalSourceFolders([p]).Count == 0) return Refuse("The application has no local source folder.");

            return (await RunRetryAsync(p, destination, version, build), "");
        }
        finally { IsOperationRunning = false; }
    }

    private async Task<PublishDestinationResult?> RunRetryAsync(PackagerInfo p, string destination, string version, PublishRunBuild build)
    {
        var target = new DestinationSet(destination == OneClickDestinations.ConfigMgr, destination == OneClickDestinations.Wsus, false).ToTargetName();
        var wsus = destination == OneClickDestinations.Wsus;
        foreach (var sentence in ApplicationPackagerEngine.Services.PublishPreflight.Lines(target))
            AddLogLine("Before publish: " + sentence);

        ShowProgress = true;
        ProgressCurrent = 1;
        ProgressTotal = 1;
        _operationCts = new CancellationTokenSource();
        var token = _operationCts.Token;
        _executionService.SupersedenceConfirmation = new ApplicationPackagerEngine.Services.Supersedence.SupersedenceConfirmer(
            Views.Windows.SupersedenceConfirmDialog.AskAsync, () => _operationCts?.Cancel());
        StatusText = ApplicationPackagerEngine.Models.RunProgress.Running(wsus ? "Publishing to WSUS" : "Packaging", 1, 1, p.Application);
        p.Status = wsus ? "Publishing to WSUS..." : "Packaging...";
        AddLogLine($"Retry: {destination} for {p.Application} ({p.Script})");

        var cells = new List<PublishDestinationResult>();
        void ThrowIfBuildIsGone()
        {
            if (build.ReadBuild?.Invoke().Failure is { } failure) throw new InvalidOperationException(failure);
        }
        try
        {
            await PublishRowRunner.RunAsync(p.Uid, version, target,
                async packageTarget =>
                {
                    ThrowIfBuildIsGone();
                    // Skip keeps an existing application. A retry has no Overwrite answer.
                    var result = await _executionService.PackageAsync(p, Comment, line => AddLogLine($"  {line}"), token,
                        ApplicationPackagerEngine.Services.OnExistingBehavior.Skip, build.Snapshot, packageTarget, reviewedVersion: version);
                    if (result.ExitCode != 0) LogStdErr(result.StdErr);
                    return result;
                },
                () =>
                {
                    ThrowIfBuildIsGone();
                    return _publishRuns.PublishWsusRowAsync(p, build.Snapshot, version, AddLogLine, token);
                },
                AddLogLine, token, null, cells);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            AddLogLine("The retry was cancelled.");
        }
        finally
        {
            ShowProgress = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }

        var cell = cells.FirstOrDefault(c => c.Destination == destination);
        if (cell is null) return null;
        if (cell.Kind == PublishDestinationKind.Published)
        {
            if (!wsus) p.Status = RowStatus(target, 0);
        }
        else if (cell.Step == "cancel") p.Status = "Cancelled";
        else if (cell.Kind == PublishDestinationKind.Skipped) p.Status = "Skipped";
        else if (cell.Kind == PublishDestinationKind.Failed) p.Status = RowStatus(target, 1);
        StatusText = $"Retry of {destination} for {p.Application}: {cell.Text}.";
        AddLogLine(StatusText + (cell.Message.Length > 0 && cell.Kind != PublishDestinationKind.Published ? " " + cell.Message : ""));
        return cell;
    }

    private async Task<ConfigMgrFact> ReadConfigMgrBeforeRetryAsync(PackagerInfo p)
    {
        _operationCts = new CancellationTokenSource();
        var token = _operationCts.Token;
        ShowProgress = true;
        ProgressCurrent = 1;
        ProgressTotal = 1;
        StatusText = ApplicationPackagerEngine.Models.RunProgress.Running("Reading ConfigMgr before publish", 1, 1, p.Application);
        try { return await ReadConfigMgrFactAsync(p, token) ?? ConfigMgrFact.Unreadable("the read was cancelled"); }
        finally
        {
            ShowProgress = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    /// <summary>One site query for one application; null when cancelled.</summary>
    private async Task<ConfigMgrFact?> ReadConfigMgrFactAsync(PackagerInfo p, CancellationToken token)
    {
        try
        {
            var result = await _mecmQueryService.QueryVersionAsync(p.CMName, token, p.ArpName);
            if (token.IsCancellationRequested) return null;
            return result.Failed ? ConfigMgrFact.Unreadable(result.FailureReason ?? "the query failed")
                : result.Found ? ConfigMgrFact.Found(result.SoftwareVersion ?? "")
                : ConfigMgrFact.None;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return ConfigMgrFact.Unreadable(ex.Message);
        }
    }
}
