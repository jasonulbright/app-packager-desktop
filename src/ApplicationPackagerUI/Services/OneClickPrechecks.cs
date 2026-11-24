using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>The three checks that open a One Click run, each over the tracked rows.</summary>
public interface IOneClickChecks
{
    /// <summary>Check Latest. Returns false when the operator cancelled.</summary>
    Task<bool> CheckLatestAsync(IReadOnlyList<PackagerInfo> rows);
    /// <summary>Check ConfigMgr. Returns false when the operator cancelled.</summary>
    Task<bool> CheckConfigMgrAsync(IReadOnlyList<PackagerInfo> rows);
    /// <summary>Check CVEs. Returns false when the operator cancelled.</summary>
    Task<bool> CheckCvesAsync(IReadOnlyList<PackagerInfo> rows);
}

/// <summary>
/// One Click always runs the whole chain (D-47): Check Latest, Check ConfigMgr and Check CVEs
/// first, so the plan shows the version of every row and never stops at "not checked". Stage and
/// package follow in the run.
/// </summary>
public static class OneClickPrechecks
{
    /// <summary>The order of the checks, for the log and for the tests.</summary>
    public static readonly IReadOnlyList<string> Order = ["Check Latest", "Check ConfigMgr", "Check CVEs"];

    /// <returns>True when every check ran; false when the operator cancelled one.</returns>
    public static async Task<bool> RunAsync(IReadOnlyList<PackagerInfo> rows, IOneClickChecks checks, Action<string> log)
    {
        log("One Click: " + string.Join(", ", Order) + ", stage and package run for each application.");
        if (!await checks.CheckLatestAsync(rows)) return false;
        if (!await checks.CheckConfigMgrAsync(rows)) return false;
        return await checks.CheckCvesAsync(rows);
    }
}
