namespace ApplicationPackagerEngine.Models;

/// <summary>
/// The destination set of one publish run. The site-free targets stop at
/// Stage: no site connection, no share copy, no ConfigMgr application.
/// </summary>
public static class PublishRunTargets
{
    public const string ConfigMgr = "MECM";
    public const string ConfigMgrAndIntune = "MECMAndIntune";
    public const string IntuneOnly = "IntuneOnly";
    public const string ConfigMgrAndWsus = "MECMAndWSUS";
    public const string WsusOnly = "WSUSOnly";

    public static readonly IReadOnlyList<string> Names = [ConfigMgr, ConfigMgrAndIntune, IntuneOnly, ConfigMgrAndWsus, WsusOnly];

    public static bool SkipsSite(string? target) => target is IntuneOnly or WsusOnly;

    public static bool PublishesToWsus(string? target) => target is ConfigMgrAndWsus or WsusOnly;
}

/// <summary>What became of the WSUS half of a run.</summary>
public enum WsusStepStatus
{
    /// <summary>The run did not publish to WSUS.</summary>
    NotRun,
    /// <summary>A new update was published.</summary>
    Published,
    /// <summary>The update for this version was already on the server; approval and decline were reapplied.</summary>
    AlreadyPublished,
    /// <summary>The application cannot be carried by WSUS; nothing reached the server.</summary>
    Refused,
    /// <summary>A server call failed.</summary>
    Failed
}

/// <summary>
/// The WSUS half of a run. Refusal codes are set only for a refusal;
/// warnings only after a publish that reached the server.
/// </summary>
public sealed record WsusStepResult(
    WsusStepStatus Status,
    string Message,
    IReadOnlyList<string> RefusalCodes,
    IReadOnlyList<string> Warnings,
    string PackageId = "")
{
    public static readonly WsusStepResult NotRun = new(WsusStepStatus.NotRun, "", [], []);

    /// <summary>True when the update is on the server, new or already there.</summary>
    public bool Ok => Status is WsusStepStatus.Published or WsusStepStatus.AlreadyPublished;

    public bool Refused => Status == WsusStepStatus.Refused;

    /// <summary>The short form a result cell shows for a refusal: "not supported: Code[,Code]".</summary>
    public string NotSupportedReason => Refused ? "not supported: " + string.Join(",", RefusalCodes) : "";
}

/// <summary>How a package run itself ended before any publish step was judged.</summary>
public enum PackageRunEnd
{
    Completed,
    Skipped,
    Canceled
}

public enum PublishRowOutcomeKind
{
    Success,
    Skipped,
    NotSupported,
    Failed
}

/// <summary>
/// The row status after one application's publish run. An Intune or WSUS run
/// succeeds only when its publish succeeds; a ConfigMgr run that also
/// publishes to WSUS carries the WSUS result as a status suffix. A WSUS run
/// states its result in the words of the One Click result cell, and Detail
/// holds the full server or refusal text behind a short status.
/// </summary>
public sealed record PublishRowOutcome(PublishRowOutcomeKind Kind, string Status, bool Published, string Detail = "")
{
    public static PublishRowOutcome For(
        string? target, int exitCode, PackageRunEnd end = PackageRunEnd.Completed,
        WsusStepResult? wsus = null, IntuneRunOutcome? intune = null)
    {
        wsus ??= WsusStepResult.NotRun;
        var wsusRan = wsus.Status != WsusStepStatus.NotRun;
        var wsusWarned = wsus.Ok && wsus.Warnings.Count > 0;
        var wsusSuffix = wsus.Refused ? ", WSUS not supported"
            : wsusRan && !wsus.Ok ? ", WSUS failed"
            : wsusWarned ? ", WSUS warning"
            : "";

        if (end is PackageRunEnd.Skipped or PackageRunEnd.Canceled)
            return new(PublishRowOutcomeKind.Skipped, end + wsusSuffix, false);
        if (exitCode != 0)
            return new(PublishRowOutcomeKind.Failed, PublishRunTargets.SkipsSite(target) ? "Publish error" : "Package error", false);
        if (target == PublishRunTargets.WsusOnly)
        {
            if (wsus.Ok)
            {
                var status = wsus.Status == WsusStepStatus.AlreadyPublished ? "Already on the server" : "Published";
                return wsusWarned
                    ? new(PublishRowOutcomeKind.Success, status + ", warning", true, wsus.Message)
                    : new(PublishRowOutcomeKind.Success, status, true);
            }
            if (wsus.Refused) return new(PublishRowOutcomeKind.NotSupported, wsus.NotSupportedReason, false, wsus.Message);
            return new(PublishRowOutcomeKind.Failed, "Publish error", false, wsus.Message);
        }
        if (target == PublishRunTargets.IntuneOnly)
        {
            return intune?.Succeeded == true
                ? new(PublishRowOutcomeKind.Success, "Published", true)
                : new(PublishRowOutcomeKind.Failed, "Publish error", false);
        }
        return new(PublishRowOutcomeKind.Success, "Packaged" + wsusSuffix, false);
    }
}
