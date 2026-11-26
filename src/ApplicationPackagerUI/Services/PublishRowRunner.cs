using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// What one row's publish run produced: the per-destination cells, the package result of the
/// ConfigMgr and Intune part (null when the target has none) and the WSUS run (null when the
/// target has no WSUS).
/// </summary>
public sealed record PublishRowRun(PublishRowResult Result, PackagerExecutionResult? Package, WsusRowRun? Wsus)
{
    /// <summary>The WSUS step, when it ran far enough to have one.</summary>
    public ApplicationPackagerEngine.Models.WsusStepResult? WsusStep => Wsus?.Step;
}

/// <summary>
/// One row of a Publish All run for any of the seven targets. ConfigMgr and Intune run through
/// one package call, in the order of the package path; WSUS then runs through its own publish,
/// the order One Click uses. A destination that fails does not stop the next one: each leaves its
/// own cell, and what already reached a destination stays published. A cancelled run stops
/// before the next destination and throws.
/// </summary>
public static class PublishRowRunner
{
    /// <summary>The target of the package call: the target without WSUS, or null when only WSUS is left.</summary>
    public static string? PackageTarget(string target)
    {
        var set = DestinationSet.FromTarget(target);
        if (!set.ConfigMgr && !set.Intune) return null;
        return new DestinationSet(set.ConfigMgr, false, set.Intune).ToTargetName();
    }

    /// <param name="package">Runs the ConfigMgr and Intune part for the given target. The caller may answer a conflict inside it.</param>
    /// <param name="wsus">Runs the WSUS publish of the row.</param>
    public static async Task<PublishRowRun> RunAsync(
        string uid, string version, string target,
        Func<string, Task<PackagerExecutionResult>> package,
        Func<Task<WsusRowRun>> wsus,
        Action<string> log, CancellationToken ct, Func<bool>? stopAfterPackage = null, List<PublishDestinationResult>? collected = null)
    {
        var set = DestinationSet.FromTarget(target);
        // The caller keeps this list when a cancelled run throws, so what already reached a destination stays in the results.
        var cells = collected ?? [];
        const string stopped = "The run stopped during this publish. Check the system.";
        PackagerExecutionResult? packageResult = null;
        WsusRowRun? wsusRun = null;

        if (PackageTarget(target) is { } packageTarget)
        {
            try
            {
                packageResult = await package(packageTarget);
                if (set.ConfigMgr) cells.Add(PublishDestinationResult.FromConfigMgr(packageResult));
                if (set.Intune) cells.Add(PublishDestinationResult.FromIntune(packageResult));
            }
            // A cancellation that the operator did not ask for (a request that timed out) is a failure of this destination.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (set.ConfigMgr) cells.Add(new(OneClickDestinations.ConfigMgr, PublishDestinationKind.Failed, "cancel", stopped));
                if (set.Intune) cells.Add(new(OneClickDestinations.Intune, PublishDestinationKind.Failed, "cancel", stopped));
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log($"ERROR: {ex.Message}");
                // The call may have written before it threw, so the step says that the state is not known.
                if (set.ConfigMgr) cells.Add(new(OneClickDestinations.ConfigMgr, PublishDestinationKind.Failed, "error", ex.Message));
                if (set.Intune) cells.Add(new(OneClickDestinations.Intune, PublishDestinationKind.Failed, "error", ex.Message));
            }
        }

        if (set.Wsus && stopAfterPackage?.Invoke() == true)
            cells.Add(PublishDestinationResult.NotAttempted(OneClickDestinations.Wsus, PublishDestinationKind.Skipped, "The run was stopped before WSUS."));
        else if (set.Wsus)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                wsusRun = await wsus();
                cells.Add(wsusRun.Step is { } step
                    ? PublishDestinationResult.FromWsus(step)
                    : new(OneClickDestinations.Wsus, PublishDestinationKind.Failed, "stage", wsusRun.Stage.StdErr.Trim(), null));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                cells.Add(new(OneClickDestinations.Wsus, PublishDestinationKind.Failed, "cancel", stopped));
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log($"ERROR: {ex.Message}");
                cells.Add(new(OneClickDestinations.Wsus, PublishDestinationKind.Failed, "error", ex.Message));
            }
        }

        return new PublishRowRun(new PublishRowResult(uid, version, cells), packageResult, wsusRun);
    }
}
