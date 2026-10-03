using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>The WSUS publish of one row: the stage it used, and the step and row outcome once the publish ran.</summary>
public sealed record WsusRowRun(PackagerExecutionResult Stage, WsusStepResult? Step, PublishRowOutcome? Outcome)
{
    /// <summary>True when the stage the publish needed failed, so nothing reached the server.</summary>
    public bool StageFailed => Step is null;
}

/// <summary>
/// The publish run of one row to one destination, for the Packages page and the
/// command-line build alike: the run gate, the WSUS publish with the stage it needs,
/// and the row status and Intune notes of a ConfigMgr or Intune run. Prompts stay
/// with the caller.
/// </summary>
public sealed class PublishRunService(PackagerExecutionService executionService, IWsusPublisher wsusPublisher)
{
    /// <summary>The settings the publish buttons depend on, as stored now.</summary>
    public static PublishReadiness ReadinessOf(AppSettings settings) => new(
        settings.Systems ?? new SystemsInUse(),
        ApplicationPackagerEngine.Services.ConfigMgrConsole.IsInstalled(),
        settings.SiteCode, settings.FileShareRoot,
        settings.Intune.TenantId, settings.Intune.ClientId, !string.IsNullOrWhiteSpace(settings.Intune.ClientSecretProtected),
        settings.Wsus.ServerName);

    /// <summary>The reason a publish run to <paramref name="target"/> cannot start, or null.</summary>
    public static string? RunGate(string target, AppSettings settings) => PublishActionState.RunGate(target, ReadinessOf(settings));

    /// <summary>The grid status of a ConfigMgr or Intune run row.</summary>
    public static string RowStatus(string target, int exitCode) =>
        PublishRowOutcome.For(target, exitCode, intune: exitCode == 0 ? IntuneRunOutcome.Published("", "") : null).Status;

    /// <summary>
    /// Publishes one row to WSUS. A Workbench snapshot names the sealed build the publish
    /// consumes; without one, the row's existing stage of its latest version is used, and
    /// the row is staged when it has none. A failed stage returns before the publish.
    /// </summary>
    public async Task<WsusRowRun> PublishWsusRowAsync(PackagerInfo p, RunSnapshot? workbenchSnapshot, Action<string> log, CancellationToken ct)
    {
        PackagerExecutionResult stage;
        if (workbenchSnapshot is not null)
        {
            // A Workbench publish consumes the sealed build its snapshot names.
            stage = new PackagerExecutionResult { ExitCode = 0 };
        }
        else if (executionService.ExistingStageManifest(p) is { } existing)
        {
            log($"  Stage manifest found: {existing}");
            stage = new PackagerExecutionResult { ExitCode = 0, OutLogPath = existing };
        }
        else
        {
            p.Status = "Staging...";
            stage = await executionService.StageAsync(p, line => log($"  {line}"), ct, null, workbenchSnapshot,
                PublishRunTargets.WsusOnly);
            if (stage.ExitCode != 0) return new WsusRowRun(stage, null, null);
        }

        p.Status = "Publishing to WSUS...";
        var step = await executionService.PublishWsusAsync(p, stage, workbenchSnapshot, wsusPublisher, line => log($"  {line}"), ct);
        var outcome = PublishRowOutcome.For(PublishRunTargets.WsusOnly, 0, wsus: step);
        p.Status = outcome.Status;
        log("WSUS publish: " + step.Message);
        return new WsusRowRun(stage, step, outcome);
    }

    /// <summary>The log line of a successful ConfigMgr or Intune run: its log file, else its own output.</summary>
    public static string PackagedLine(PackagerExecutionResult result)
    {
        var pkgRef = string.IsNullOrEmpty(result.OutLogPath) ? result.StdOut : $"Log: {Path.GetFileName(result.OutLogPath)}";
        return $"Packaged. {pkgRef}";
    }

    /// <summary>Mirrors the Intune post-step outcome into the run log and the row's note column.</summary>
    public static void LogIntuneNotes(PackagerInfo packager, PackagerExecutionResult result, Action<string> log)
    {
        var notes = new List<string>();

        if (!string.IsNullOrEmpty(result.IntuneWinNote))
        {
            log($"Intunewin: {result.IntuneWinNote}");
            notes.Add(result.IntuneWinNote);
        }
        if (!string.IsNullOrEmpty(result.IntunePublishNote))
        {
            log($"Intune publish: {result.IntunePublishNote}");
            notes.Add(result.IntunePublishNote);
        }

        packager.IntuneNote = string.Join(" — ", notes);
    }
}
