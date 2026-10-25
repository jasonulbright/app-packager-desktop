using System.Globalization;

namespace ApplicationPackagerEngine.Services;

/// <param name="Error">Why the schedule blocks the deployment; null when it does not.</param>
/// <param name="Backdated">The available time lies more than <see cref="DeploymentScheduleRules.BackdateTolerance"/> in the past.</param>
public sealed record ScheduleVerdict(string? Error, bool Backdated, IReadOnlyList<string> Warnings)
{
    public bool Blocked => Error is not null;
}

/// <summary>
/// The schedule and option rules of a deployment. Times are wall-clock values in the time basis
/// of the deployment, and "now" is read in the same basis: with a UTC basis a deadline that passed
/// in UTC is in the past even when the local clock is behind UTC.
/// </summary>
public static class DeploymentScheduleRules
{
    public static readonly TimeSpan BackdateTolerance = TimeSpan.FromMinutes(5);

    public static readonly IReadOnlyList<string> Notifications = ["DisplayAll", "DisplaySoftwareCenterOnly", "HideAll"];
    public static readonly IReadOnlyList<string> FastNetwork = ["DownloadContentFromDistributionPointAndRunLocally", "RunProgramFromDistributionPoint"];

    /// <summary>
    /// The slow network values New-CMPackageDeployment accepts on a site. The cmdlet documentation
    /// also lists RunProgramFromDistributionPoint, which the cmdlet refuses.
    /// </summary>
    public static readonly IReadOnlyList<string> SlowNetwork = ["DoNotRunProgram", "DownloadContentFromDistributionPointAndLocally"];

    public static readonly IReadOnlyList<string> Rerun =
        ["NeverRerunDeployedProgram", "AlwaysRerunProgram", "RerunIfFailedPreviousAttempt", "RerunIfSucceededOnPreviousAttempt"];

    public static readonly IReadOnlyList<string> TaskSequenceOptions =
        ["DownloadContentLocallyWhenNeededByRunningTaskSequence", "DownloadAllContentLocallyBeforeStartingTaskSequence", "RunFromDistributionPoint"];

    public static readonly IReadOnlyList<string> TaskSequenceAvailability = ["Clients", "ClientsMediaAndPxe", "MediaAndPxe", "MediaAndPxeHidden"];

    /// <summary>The current wall-clock time in the time basis, with an unspecified kind.</summary>
    public static DateTime NowIn(bool utcBasis, DateTime utcNow, TimeZoneInfo? local = null)
    {
        var utc = utcNow.Kind == DateTimeKind.Utc ? utcNow : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var value = utcBasis ? utc : TimeZoneInfo.ConvertTimeFromUtc(utc, local ?? TimeZoneInfo.Local);
        return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
    }

    public static bool HasNotification(DeploymentObjectType type) => type is DeploymentObjectType.Application or DeploymentObjectType.SoftwareUpdateGroup;

    public static ScheduleVerdict Check(DeploymentObjectType type, DeploymentOptions options, DateTime nowInBasis)
    {
        ArgumentNullException.ThrowIfNull(options);
        var warnings = new List<string>();
        var available = options.AvailableTime;
        var backdated = available < nowInBasis - BackdateTolerance;
        if (backdated)
            warnings.Add($"The available time ({Text(available)}) is in the past. Clients get the deployment at once.");
        if (available > nowInBasis.AddYears(5))
            warnings.Add($"The available time ({Text(available)}) is more than five years ahead.");

        string? error = null;
        if (options.Purpose is not (DeploymentPurposes.Available or DeploymentPurposes.Required))
        {
            error = "Select the purpose: Available or Required.";
        }
        else if (options.Purpose == DeploymentPurposes.Required)
        {
            if (options.Deadline is not { } deadline)
                error = "A Required deployment needs a deadline.";
            else if (deadline <= available)
                error = $"The deadline ({Text(deadline)}) must be later than the available time ({Text(available)}). A deadline on or before the available time enforces at once.";
            else if (deadline <= nowInBasis)
                error = $"The deadline ({Text(deadline)}) is in the past. A Required deployment with a past deadline installs at once on every device of the collection.";
        }
        else if (HasNotification(type) && options.UserNotification == "HideAll")
        {
            error = "Hide all does not work with Available: the deployment would not show in Software Center and would never install. Change the purpose or the notification.";
        }
        return new ScheduleVerdict(error, backdated, warnings);
    }

    /// <summary>A plain reason when an option of the type has a value ConfigMgr does not accept; null otherwise.</summary>
    public static string? OptionProblem(DeploymentObjectType type, DeploymentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (HasNotification(type) && !Notifications.Contains(options.UserNotification))
            return "Select the user notification: Display all, Software Center only or Hide all.";
        switch (type)
        {
            case DeploymentObjectType.Package:
                if (!FastNetwork.Contains(options.NetworkBehavior)) return "Select the fast network option of the package.";
                if (!SlowNetwork.Contains(options.SlowNetworkBehavior))
                    return "Select the slow network option of the package: do not run the program, or download the content and run it locally.";
                if (!Rerun.Contains(options.RerunBehavior)) return "Select the rerun behavior.";
                break;
            case DeploymentObjectType.TaskSequence:
                if (!TaskSequenceOptions.Contains(options.NetworkBehavior)) return "Select how clients get the content of the task sequence.";
                if (!TaskSequenceAvailability.Contains(options.TaskSequenceAvailability)) return "Select where the task sequence is available.";
                if (!Rerun.Contains(options.RerunBehavior)) return "Select the rerun behavior.";
                if (options.NetworkBehavior == "DownloadAllContentLocallyBeforeStartingTaskSequence" && options.TaskSequenceAvailability != "Clients")
                    return "Download all content before the task sequence starts works only when the task sequence is available to clients only.";
                break;
        }
        return null;
    }

    private static string Text(DateTime value) => value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
