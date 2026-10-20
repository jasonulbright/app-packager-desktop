using System.Globalization;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>A value of a choice list and the text the page shows for it.</summary>
public sealed record DeploymentChoice(string Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>The plain texts, choice lists, schedule rules and plans of the Deployment Helper page.</summary>
public static class DeploymentText
{
    public const string Available = "Available";
    public const string Required = "Required";
    public const string HideAll = "HideAll";
    public const string TimeFormat = "HH:mm";

    public const string Pass = "Pass";
    public const string Fail = "Fail";
    public const string Warn = "Warn";
    public const string Unknown = "Unknown";

    public const string TargetExists = "TargetExists";
    public const string ContentDistributed = "ContentDistributed";
    public const string CollectionValid = "CollectionValid";
    public const string CollectionSafe = "CollectionSafe";
    public const string NoDuplicate = "NoDuplicate";
    public const string Schedule = "Schedule";
    public const string AuditLog = "AuditLog";
    public const string Signing = "Signing";
    public const string DpGroup = "DpGroup";
    public const string DeploymentUnchanged = "DeploymentUnchanged";
    public const string CreatedHere = "CreatedHere";

    /// <summary>The five checks of a create, in the order the validation panel lists them first.</summary>
    public static IReadOnlyList<string> CheckNames { get; } = [TargetExists, ContentDistributed, CollectionValid, CollectionSafe, NoDuplicate];

    /// <summary>The order of every check the service reports; a check not named here follows in the order it came.</summary>
    private static readonly IReadOnlyList<string> CheckOrder =
        [TargetExists, ContentDistributed, CollectionValid, CollectionSafe, NoDuplicate, Schedule, AuditLog, Signing, DpGroup, DeploymentUnchanged, CreatedHere];

    public const string UndoNote = "Undo removes the deployment. Software that clients already installed stays installed.";
    public const string DistributeNoUndo = "No Undo: the content stays on the distribution points. An audit record is written for each request.";
    public const string CreateNowChoice = "Create the deployment now, while the content is still distributing.";
    public const string CreateNowHint = "Without the box 'Create the deployment now' the deployment is created only when the content check passes. Otherwise the create is skipped and can run again later.";
    public const string SuppressRestartLabel = "Suppress restarts on servers and workstations (devices do not restart after the installation)";

    public static IReadOnlyList<DeploymentChoice> Notifications { get; } =
    [
        new("DisplayAll", "Display in Software Center and show all notifications"),
        new("DisplaySoftwareCenterOnly", "Display in Software Center only"),
        new(HideAll, "Hide in Software Center and all notifications")
    ];

    /// <summary>The fast network options of a package.</summary>
    public static IReadOnlyList<DeploymentChoice> NetworkBehaviors { get; } =
    [
        new("DownloadContentFromDistributionPointAndRunLocally", "Download content from the distribution point and run locally"),
        new("RunProgramFromDistributionPoint", "Run the program from the distribution point")
    ];

    /// <summary>The slow network options of a package; ConfigMgr refuses running the program from the distribution point on a slow network.</summary>
    public static IReadOnlyList<DeploymentChoice> SlowNetworkBehaviors { get; } =
    [
        new("DoNotRunProgram", "Do not run the program"),
        new("DownloadContentFromDistributionPointAndLocally", "Download content from the distribution point and run locally")
    ];

    /// <summary>How clients get the content of a task sequence; ConfigMgr ignores running it from the distribution point.</summary>
    public static IReadOnlyList<DeploymentChoice> TaskSequenceContents { get; } =
    [
        new("DownloadContentLocallyWhenNeededByRunningTaskSequence", "Download content locally when the task sequence needs it"),
        new("DownloadAllContentLocallyBeforeStartingTaskSequence", "Download all content locally before the task sequence starts")
    ];

    public static IReadOnlyList<DeploymentChoice> TaskSequenceAvailabilities { get; } =
    [
        new("Clients", "Configuration Manager clients only"),
        new("ClientsMediaAndPxe", "Clients, media and PXE"),
        new("MediaAndPxe", "Media and PXE only"),
        new("MediaAndPxeHidden", "Media and PXE only (hidden)")
    ];

    public static IReadOnlyList<DeploymentChoice> RerunBehaviors { get; } =
    [
        new("NeverRerunDeployedProgram", "Never rerun the deployed program"),
        new("AlwaysRerunProgram", "Always rerun the program"),
        new("RerunIfFailedPreviousAttempt", "Rerun if the previous attempt failed"),
        new("RerunIfSucceededOnPreviousAttempt", "Rerun if the previous attempt succeeded")
    ];

    public static string Count(int count, string singular, string? plural = null) =>
        count == 1 ? $"1 {singular}" : $"{count.ToString(CultureInfo.CurrentCulture)} {plural ?? singular + "s"}";

    public static string TypeName(DeploymentObjectType type) => type switch
    {
        DeploymentObjectType.Application => "Application",
        DeploymentObjectType.Package => "Package",
        DeploymentObjectType.TaskSequence => "Task sequence",
        _ => "Software update group"
    };

    public static string TypePlural(DeploymentObjectType type) => type switch
    {
        DeploymentObjectType.Application => "Applications",
        DeploymentObjectType.Package => "Packages",
        DeploymentObjectType.TaskSequence => "Task Sequences",
        _ => "Software Update Groups"
    };

    public static string CheckLabel(string name) => name switch
    {
        TargetExists => "Object exists",
        ContentDistributed => "Content distributed",
        CollectionValid => "Collection is a device collection",
        CollectionSafe => "Collection is not built in",
        NoDuplicate => "No duplicate deployment",
        Schedule => "Schedule and options",
        AuditLog => "Audit log",
        Signing => "Script signing",
        DpGroup => "Distribution point group",
        DeploymentUnchanged => "Deployment unchanged",
        CreatedHere => "Created by this product",
        _ => name
    };

    /// <summary>True for a check that stops the change: it failed or could not be made.</summary>
    public static bool Blocks(PreflightCheck check) => check.Result is Fail or Unknown;

    /// <summary>
    /// The plain message of a check. A failed duplicate check also says what ConfigMgr allows for
    /// the type: a second deployment of an application to one collection is refused by ConfigMgr,
    /// a second deployment of the other types is allowed and refused by this page.
    /// </summary>
    public static string CheckMessage(PreflightCheck check, DeploymentObjectType? type)
    {
        var message = check.Message.Length > 0 ? check.Message : check.Result + ".";
        if (check.Name != NoDuplicate || check.Result != Fail || type is not { } t) return message;
        return message + " " + (t == DeploymentObjectType.Application
            ? "ConfigMgr does not allow a second deployment of an application to the same collection."
            : "ConfigMgr allows a second deployment of a package, a task sequence or a software update group to the same collection; this page blocks it.");
    }

    /// <summary>The checks in the order of the validation panel.</summary>
    public static IReadOnlyList<PreflightCheck> Ordered(IReadOnlyList<PreflightCheck> checks) =>
        checks.Select((c, i) => (Check: c, Index: i))
            .OrderBy(x => CheckOrder.ToList().IndexOf(x.Check.Name) is var i && i < 0 ? int.MaxValue : i)
            .ThenBy(x => x.Index)
            .Select(x => x.Check).ToList();

    public static string ChoiceText(IReadOnlyList<DeploymentChoice> choices, string? value) =>
        choices.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase))?.Text ?? value ?? "";

    public static string Time(DateTime value) => value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Basis(bool utc) => utc ? "UTC" : "local time";

    /// <summary>The object as one line: type, name, version or program, and ID.</summary>
    public static string Object(DeployableObject obj)
    {
        var extra = obj.Type switch
        {
            DeploymentObjectType.Package => obj.Program.Length > 0 ? $", program {obj.Program}" : "",
            DeploymentObjectType.SoftwareUpdateGroup => obj.UpdateCount > 0 ? $", {Count(obj.UpdateCount, "update")}" : "",
            _ => obj.Version.Length > 0 ? $", version {obj.Version}" : ""
        };
        return $"{TypeName(obj.Type)} {obj.Name}{extra} ({obj.Id})";
    }

    public static string Collection(DeploymentCollection collection) => $"{collection.Name} ({collection.CollectionId})";

    /// <summary>True for the types whose deployments have a user notification setting.</summary>
    public static bool HasNotification(DeploymentObjectType type) => DeploymentScheduleRules.HasNotification(type);

    /// <summary>
    /// Why the schedule or the options cannot be used, or null. <paramref name="now"/> is the clock
    /// of the deployment's time basis: UTC for a UTC deployment, local time otherwise.
    /// </summary>
    public static string? ScheduleProblem(DeploymentObjectType type, DeploymentOptions options, DateTime now)
    {
        if (string.Equals(options.Purpose, Available, StringComparison.OrdinalIgnoreCase) && HasNotification(type)
            && string.Equals(options.UserNotification, HideAll, StringComparison.OrdinalIgnoreCase))
            return "An Available deployment cannot hide all notifications: users could not find it in Software Center. Choose another notification.";
        if (string.Equals(options.Purpose, Required, StringComparison.OrdinalIgnoreCase))
        {
            if (options.Deadline is not { } deadline) return "A Required deployment needs a deadline.";
            if (deadline <= options.AvailableTime) return "The deadline must be later than the available time.";
            if (deadline <= now) return "The deadline is in the past. Set a deadline later than now.";
        }
        return DeploymentScheduleRules.OptionProblem(type, options);
    }

    /// <summary>True when the available time is earlier than now on the clock of the time basis.</summary>
    public static bool IsBackdated(DeploymentOptions options, DateTime now) => options.AvailableTime < now.AddMinutes(-1);

    /// <summary>The clock of the time basis.</summary>
    public static DateTime Now(TimeProvider clock, bool utc) => utc ? clock.GetUtcNow().UtcDateTime : clock.GetLocalNow().DateTime;

    /// <summary>The check lines a plan shows: every check of the prepared change, a warning included.</summary>
    public static IEnumerable<string> CheckLines(IReadOnlyList<PreflightCheck> checks, DeploymentObjectType? type = null) =>
        Ordered(checks).Select(c => $"Check {CheckLabel(c.Name)}: {c.Result}. {CheckMessage(c, type)}".TrimEnd());

    /// <summary>The checks that stop a change: every check that failed or could not be made.</summary>
    public static IReadOnlyList<PreflightCheck> Blocking(DeploymentChange change) => Ordered(change.Checks).Where(Blocks).ToList();

    /// <summary>The reasons of the blocking checks as one line.</summary>
    public static string BlockingText(DeploymentChange change) =>
        string.Join(" ", Blocking(change).Select(c => $"{CheckLabel(c.Name)}: {CheckMessage(c, change.Object?.Type ?? change.Existing?.Type)}"));

    private static string YesNo(bool value) => value ? "yes" : "no";

    /// <summary>The options of the type; the settings ConfigMgr ignores for Available are named only for Required.</summary>
    public static string OptionsLine(DeploymentObjectType type, DeploymentOptions options)
    {
        var required = string.Equals(options.Purpose, Required, StringComparison.OrdinalIgnoreCase);
        var parts = new List<string>();
        switch (type)
        {
            case DeploymentObjectType.Package:
                parts.Add($"fast network: {ChoiceText(NetworkBehaviors, options.NetworkBehavior).ToLowerInvariant()}");
                parts.Add($"slow network: {ChoiceText(SlowNetworkBehaviors, options.SlowNetworkBehavior).ToLowerInvariant()}");
                parts.Add($"fallback to the default site boundary group {YesNo(options.AllowFallback)}");
                break;
            case DeploymentObjectType.TaskSequence:
                parts.Add($"available to: {ChoiceText(TaskSequenceAvailabilities, options.TaskSequenceAvailability).ToLowerInvariant()}");
                parts.Add($"content: {ChoiceText(TaskSequenceContents, options.NetworkBehavior).ToLowerInvariant()}");
                parts.Add($"show progress {YesNo(options.ShowProgress)}");
                parts.Add($"fallback to the default site boundary group {YesNo(options.AllowFallback)}");
                break;
            case DeploymentObjectType.SoftwareUpdateGroup:
                parts.Add($"full scan after restart {YesNo(options.PostRebootScan)}");
                break;
        }
        if (required)
        {
            if (type is DeploymentObjectType.Package or DeploymentObjectType.TaskSequence)
                parts.Add(ChoiceText(RerunBehaviors, options.RerunBehavior).ToLowerInvariant());
            parts.Add($"install outside maintenance windows {YesNo(options.OverrideServiceWindow)}");
            parts.Add($"restart outside maintenance windows {YesNo(options.RebootOutsideServiceWindow)}");
            parts.Add($"metered connection {YesNo(options.AllowMetered)}");
            if (type == DeploymentObjectType.SoftwareUpdateGroup)
            {
                parts.Add($"fallback to the default site boundary group {YesNo(options.FallbackToDp)}");
                parts.Add($"download from Microsoft Update {YesNo(options.AllowMicrosoftUpdate)}");
                parts.Add($"suppress restarts on servers and workstations {YesNo(options.SuppressRestart)}");
            }
        }
        var line = parts.Count == 0 ? "Options: none for this type." : $"Options: {string.Join(", ", parts)}.";
        return required ? line : line + " The rerun, maintenance window, metered connection and restart settings apply to Required deployments only.";
    }

    /// <summary>
    /// The text of the second check box: one sentence that names the Required deadline, the past
    /// available time, or both, as they apply.
    /// </summary>
    public static string SecondConfirmation(DeploymentChange create, bool required, bool backdated)
    {
        var options = create.Options!;
        var parts = new List<string>();
        if (required)
        {
            var outside = new List<string>();
            if (options.OverrideServiceWindow) outside.Add("installs outside maintenance windows");
            if (options.RebootOutsideServiceWindow) outside.Add("restarts devices outside maintenance windows");
            var members = create.Collection is { } c ? Count(c.MemberCount, "device") : "an unknown number of devices";
            parts.Add($"this Required deployment installs {create.Object?.Name} on every device of {create.Collection?.Name} ({members} now) "
                + $"at the deadline {Time(options.Deadline ?? options.AvailableTime)} {Basis(options.UtcBasis)}"
                + (outside.Count > 0 ? $" and {string.Join(" and ", outside)}" : ""));
        }
        if (backdated)
            parts.Add($"the available time {Time(options.AvailableTime)} {Basis(options.UtcBasis)} is in the past, so the deployment is available at once");
        return parts.Count == 0 ? "" : $"I understand that {string.Join(", and that ", parts)}.";
    }

    /// <summary>The detail lines of a create, made from the prepared change only.</summary>
    public static IReadOnlyList<string> CreateLines(DeploymentChange create, bool backdated)
    {
        var options = create.Options!;
        var lines = new List<string> { $"Object: {Object(create.Object!)}." };
        if (create.Collection is { } collection)
            lines.Add($"Collection: {Collection(collection)}, {Count(collection.MemberCount, "member")} now.");
        var required = string.Equals(options.Purpose, Required, StringComparison.OrdinalIgnoreCase);
        lines.Add($"Purpose: {(required ? Required : Available)}. Available: {Time(options.AvailableTime)} {Basis(options.UtcBasis)}."
            + (required && options.Deadline is { } deadline ? $" Deadline: {Time(deadline)} {Basis(options.UtcBasis)}." : ""));
        if (HasNotification(create.Object!.Type)) lines.Add($"User notification: {ChoiceText(Notifications, options.UserNotification)}.");
        lines.Add(OptionsLine(create.Object!.Type, options));
        if (create.DpGroups.Count > 0)
        {
            lines.Add($"Distribution point groups: {string.Join(", ", create.DpGroups)}. The distribution is requested first.");
            lines.Add(CreateNowHint);
        }
        lines.AddRange(CheckLines(create.Checks, create.Object.Type));
        lines.AddRange(create.Impact);
        if (required)
        {
            lines.Add($"Required: clients install at the deadline on every device of the collection ({Count(create.Collection?.MemberCount ?? 0, "device")} now).");
            lines.Add(options.OverrideServiceWindow ? "The installation runs outside maintenance windows." : "The installation waits for a maintenance window.");
            lines.Add(options.RebootOutsideServiceWindow ? "Devices can restart outside maintenance windows." : "Restarts wait for a maintenance window.");
        }
        if (backdated) lines.Add("Warning: the available time is in the past.");
        if (!create.Impact.Contains(UndoNote)) lines.Add(UndoNote);
        return lines;
    }

    /// <summary>
    /// The plan of a deploy or a distribution, made from the prepared change only. A create that
    /// distributes first is one change: the service requests the distribution, then creates.
    /// Null with the reason when a check blocks the change.
    /// </summary>
    public static (DeploymentPlan? Plan, string? Problem) BuildDeployPlan(IReadOnlyList<DeploymentChange> prepared, TimeProvider clock)
    {
        var distribute = prepared.FirstOrDefault(c => c.Kind == DeploymentChangeKind.Distribute);
        var create = prepared.FirstOrDefault(c => c.Kind == DeploymentChangeKind.Create);
        if (create is null)
        {
            if (distribute is null) return (null, "There is nothing to change.");
            if (Blocking(distribute).Count > 0) return (null, "The content cannot be distributed: " + BlockingText(distribute));
            return (new DeploymentPlan("Distribute content", $"Distribute {distribute.Object?.Name}", "Distribute content",
                [DeploymentPlanEntry.Write(DistributeItem(distribute), distribute)], hasUndo: false), null);
        }
        if (create.Object is null || create.Options is null) return (null, "The deployment could not be prepared. Validate again.");
        if (Blocking(create).Count > 0) return (null, "The deployment cannot be created: " + BlockingText(create));
        var now = Now(clock, create.Options.UtcBasis);
        if (ScheduleProblem(create.Object.Type, create.Options, now) is { } problem) return (null, problem);
        var backdated = create.NeedsBackdatedAcknowledgement || IsBackdated(create.Options, now);
        var required = DeploymentWriter.IsRequiredCreate(create);
        var extra = required || backdated ? SecondConfirmation(create, required, backdated) : null;
        var distributes = create.DpGroups.Count > 0;
        var itemAction = (distributes ? "Distribute and create " : "Create ") + (required ? "Required deployment" : "deployment");
        var target = $"{Object(create.Object)} to {(create.Collection is { } c ? Collection(c) : "no collection")}";
        var item = new ChangePlanItem(char.ToUpperInvariant(itemAction[0]) + itemAction[1..], target, string.Join(Environment.NewLine, CreateLines(create, backdated)));
        var action = distributes ? "Distribute and deploy" : "Create deployment";
        var actionName = required ? "Create Required deployment" : action;
        return (new DeploymentPlan(action, $"Deploy {create.Object.Name}", actionName, [DeploymentPlanEntry.Write(item, create)],
            hasUndo: true, extraConfirmation: extra, optionalChoice: distributes ? CreateNowChoice : null) { AcknowledgesBackdated = backdated }, null);
    }

    public static ChangePlanItem DistributeItem(DeploymentChange distribute)
    {
        var lines = new List<string>();
        if (distribute.Object is { } obj) lines.Add($"Object: {Object(obj)}.");
        lines.AddRange(distribute.Impact.Count > 0 ? distribute.Impact : distribute.DpGroups.Select(g => $"Distribution point group {g}."));
        lines.AddRange(CheckLines(distribute.Checks, distribute.Object?.Type));
        lines.Add(DistributeNoUndo);
        return new ChangePlanItem("Distribute content", $"{distribute.Object?.Name} to {Count(distribute.DpGroups.Count, "distribution point group")}",
            string.Join(Environment.NewLine, lines));
    }

    /// <summary>The plan that removes one deployment; null with the reasons when a check of the prepared removal blocks it.</summary>
    public static (DeploymentPlan? Plan, string? Problem) BuildRemovePlan(DeploymentChange remove)
    {
        if (remove.Existing is not { } existing) return (null, "The deployment is not known. Refresh the history.");
        if (Blocking(remove).Count > 0) return (null, "The deployment cannot be removed: " + BlockingText(remove));
        var lines = new List<string>
        {
            $"Deployment ID: {existing.DeploymentId}.",
            $"Object: {TypeName(existing.Type)} {existing.ObjectName}{(existing.ObjectId.Length > 0 ? $" ({existing.ObjectId})" : "")}"
                + (existing.Program.Length > 0 ? $", program {existing.Program}." : "."),
            $"Collection: {existing.CollectionName}{(existing.CollectionId.Length > 0 ? $" ({existing.CollectionId})" : "")}.",
            $"Purpose: {existing.Purpose}. Available: {Time(existing.AvailableTime)} {Basis(existing.UtcBasis)}."
                + (existing.Deadline is { } deadline ? $" Deadline: {Time(deadline)} {Basis(existing.UtcBasis)}." : "")
        };
        lines.AddRange(CheckLines(remove.Checks, existing.Type));
        lines.AddRange(remove.Impact);
        lines.Add("Removes the deployment only when it still has these values and this product created it. Software that clients already installed stays installed.");
        var item = new ChangePlanItem("Remove deployment", $"{existing.ObjectName} from {existing.CollectionName}", string.Join(Environment.NewLine, lines));
        return (new DeploymentPlan("Remove deployment", $"Remove the deployment of {existing.ObjectName}", "Remove deployment",
            [DeploymentPlanEntry.Write(item, remove)], isDestructive: true, hasUndo: false), null);
    }
}
