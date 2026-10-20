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

    public static IReadOnlyList<string> CheckNames { get; } = [TargetExists, ContentDistributed, CollectionValid, CollectionSafe, NoDuplicate];

    public const string UndoNote = "Undo removes the deployment. Software that clients already installed stays installed.";
    public const string DistributeNoUndo = "No Undo: the content stays on the distribution points. An audit record is written for each request.";

    public static IReadOnlyList<DeploymentChoice> Notifications { get; } =
    [
        new("DisplayAll", "Display in Software Center and show all notifications"),
        new("DisplaySoftwareCenterOnly", "Display in Software Center only"),
        new(HideAll, "Hide in Software Center and all notifications")
    ];

    public static IReadOnlyList<DeploymentChoice> NetworkBehaviors { get; } =
    [
        new("DownloadContentFromDistributionPointAndRunLocally", "Download content from the distribution point and run locally"),
        new("RunProgramFromDistributionPoint", "Run the program from the distribution point")
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
        _ => name
    };

    public static string ChoiceText(IReadOnlyList<DeploymentChoice> choices, string? value) =>
        choices.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase))?.Text ?? value ?? "";

    public static string Time(DateTime value) => value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Basis(bool utc) => utc ? "UTC" : "local time";

    /// <summary>The object as one line: type, name, version or program, and ID.</summary>
    public static string Object(DeployableObject obj)
    {
        var extra = obj.Type == DeploymentObjectType.Package
            ? obj.Program.Length > 0 ? $", program {obj.Program}" : ""
            : obj.Version.Length > 0 ? $", version {obj.Version}" : "";
        return $"{TypeName(obj.Type)} {obj.Name}{extra} ({obj.Id})";
    }

    public static string Collection(DeploymentCollection collection) => $"{collection.Name} ({collection.CollectionId})";

    /// <summary>
    /// Why the schedule cannot be used, or null. <paramref name="now"/> is the clock of the
    /// deployment's time basis: UTC for a UTC deployment, local time otherwise.
    /// </summary>
    public static string? ScheduleProblem(DeploymentOptions options, DateTime now)
    {
        if (string.Equals(options.Purpose, Available, StringComparison.OrdinalIgnoreCase)
            && string.Equals(options.UserNotification, HideAll, StringComparison.OrdinalIgnoreCase))
            return "An Available deployment cannot hide all notifications: users could not find it in Software Center. Choose another notification.";
        if (!string.Equals(options.Purpose, Required, StringComparison.OrdinalIgnoreCase)) return null;
        if (options.Deadline is not { } deadline) return "A Required deployment needs a deadline.";
        if (deadline <= options.AvailableTime) return "The deadline must be later than the available time.";
        if (deadline <= now) return "The deadline is in the past. Set a deadline later than now.";
        return null;
    }

    /// <summary>True when the available time is earlier than now on the clock of the time basis.</summary>
    public static bool IsBackdated(DeploymentOptions options, DateTime now) => options.AvailableTime < now.AddMinutes(-1);

    /// <summary>The clock of the time basis.</summary>
    public static DateTime Now(TimeProvider clock, bool utc) => utc ? clock.GetUtcNow().UtcDateTime : clock.GetLocalNow().DateTime;

    /// <summary>The check lines a plan shows, in the order of the five checks.</summary>
    public static IEnumerable<string> CheckLines(IReadOnlyList<PreflightCheck> checks) =>
        checks.OrderBy(c => CheckNames.ToList().IndexOf(c.Name) is var i && i < 0 ? int.MaxValue : i)
            .Select(c => $"Check {CheckLabel(c.Name)}: {c.Result}.{(c.Message.Length > 0 ? " " + c.Message : "")}");

    /// <summary>
    /// The checks that block a create: a failed or unknown check. A content check that has not
    /// passed blocks nothing when the same plan distributes the content first.
    /// </summary>
    public static IReadOnlyList<PreflightCheck> Blocking(DeploymentChange create, bool distributesFirst) =>
        create.Checks.Where(c => c.Result is Fail or Unknown && !(distributesFirst && c.Name == ContentDistributed)).ToList();

    public static string OptionsLine(DeploymentObjectType type, DeploymentOptions options)
    {
        static string YesNo(bool value) => value ? "yes" : "no";
        return type switch
        {
            DeploymentObjectType.Application =>
                $"Options: override maintenance windows {YesNo(options.OverrideServiceWindow)}, restart outside maintenance windows {YesNo(options.RebootOutsideServiceWindow)}, metered connection {YesNo(options.AllowMetered)}.",
            DeploymentObjectType.Package =>
                $"Options: {ChoiceText(NetworkBehaviors, options.NetworkBehavior).ToLowerInvariant()}; {ChoiceText(RerunBehaviors, options.RerunBehavior).ToLowerInvariant()}; override maintenance windows {YesNo(options.OverrideServiceWindow)}, restart outside maintenance windows {YesNo(options.RebootOutsideServiceWindow)}, metered connection {YesNo(options.AllowMetered)}.",
            DeploymentObjectType.TaskSequence =>
                $"Options: show progress {YesNo(options.ShowProgress)}, fallback to unprotected distribution points {YesNo(options.AllowFallback)}, {ChoiceText(RerunBehaviors, options.RerunBehavior).ToLowerInvariant()}, override maintenance windows {YesNo(options.OverrideServiceWindow)}, restart outside maintenance windows {YesNo(options.RebootOutsideServiceWindow)}.",
            _ =>
                $"Options: fallback to neighbor boundary groups {YesNo(options.FallbackToDp)}, full scan after restart {YesNo(options.PostRebootScan)}, suppress restarts {YesNo(options.SuppressRestart)}, override maintenance windows {YesNo(options.OverrideServiceWindow)}, restart outside maintenance windows {YesNo(options.RebootOutsideServiceWindow)}, metered connection {YesNo(options.AllowMetered)}."
        };
    }

    public static string RequiredConfirmation(DeploymentChange create)
    {
        var options = create.Options!;
        var outside = new List<string>();
        if (options.OverrideServiceWindow) outside.Add("installs outside maintenance windows");
        if (options.RebootOutsideServiceWindow) outside.Add("restarts devices outside maintenance windows");
        var members = create.Collection is { } c ? Count(c.MemberCount, "device") : "an unknown number of devices";
        return $"I understand that this Required deployment installs {create.Object?.Name} on every device of {create.Collection?.Name} ({members} now) "
            + $"at the deadline {Time(options.Deadline ?? options.AvailableTime)} {Basis(options.UtcBasis)}"
            + (outside.Count > 0 ? $" and {string.Join(" and ", outside)}." : ".");
    }

    public static string BackdatedConfirmation(DeploymentOptions options) =>
        $"I understand that the available time {Time(options.AvailableTime)} {Basis(options.UtcBasis)} is in the past, so the deployment is available at once.";

    /// <summary>The detail lines of a create, made from the prepared change only.</summary>
    public static IReadOnlyList<string> CreateLines(DeploymentChange create, bool distributesFirst, bool backdated)
    {
        var options = create.Options!;
        var lines = new List<string> { $"Object: {Object(create.Object!)}." };
        if (create.Collection is { } collection)
            lines.Add($"Collection: {Collection(collection)}, {Count(collection.MemberCount, "member")} now.");
        var required = string.Equals(options.Purpose, Required, StringComparison.OrdinalIgnoreCase);
        lines.Add($"Purpose: {(required ? Required : Available)}. Available: {Time(options.AvailableTime)} {Basis(options.UtcBasis)}."
            + (required && options.Deadline is { } deadline ? $" Deadline: {Time(deadline)} {Basis(options.UtcBasis)}." : ""));
        lines.Add($"User notification: {ChoiceText(Notifications, options.UserNotification)}.");
        lines.Add(OptionsLine(create.Object!.Type, options));
        if (create.DpGroups.Count > 0) lines.Add($"Distribution point groups: {string.Join(", ", create.DpGroups)}.");
        lines.AddRange(CheckLines(create.Checks));
        lines.AddRange(create.Impact);
        if (distributesFirst)
            lines.Add(create.CreateNowContentDistributing
                ? "Content distribution in progress. The operator chose to create the deployment now, while the content still distributes."
                : "Content distribution in progress. The deployment is created when the content check passes.");
        if (required)
        {
            lines.Add($"Required: clients install at the deadline on every device of the collection ({Count(create.Collection?.MemberCount ?? 0, "device")} now).");
            lines.Add(options.OverrideServiceWindow ? "The installation runs outside maintenance windows." : "The installation waits for a maintenance window.");
            lines.Add(options.RebootOutsideServiceWindow ? "Devices can restart outside maintenance windows." : "Restarts wait for a maintenance window.");
        }
        if (backdated) lines.Add("Warning: the available time is in the past.");
        lines.Add(UndoNote);
        return lines;
    }

    /// <summary>
    /// The plan of a deploy, made from the prepared changes only: an optional distribution first,
    /// then the create. Null with the reason when a check blocks the create.
    /// </summary>
    public static (DeploymentPlan? Plan, string? Problem) BuildDeployPlan(IReadOnlyList<DeploymentChange> prepared, TimeProvider clock)
    {
        var distribute = prepared.FirstOrDefault(c => c.Kind == DeploymentChangeKind.Distribute);
        var create = prepared.FirstOrDefault(c => c.Kind == DeploymentChangeKind.Create);
        var entries = new List<DeploymentPlanEntry>();
        if (distribute is not null) entries.Add(DeploymentPlanEntry.Write(DistributeItem(distribute), distribute));
        string? extra = null;
        var required = false;
        if (create is not null)
        {
            if (create.Object is null || create.Options is null) return (null, "The deployment could not be prepared. Validate again.");
            var blocking = Blocking(create, distribute is not null);
            if (blocking.Count > 0)
                return (null, "The deployment cannot be created: " + string.Join(" ", blocking.Select(c => $"{CheckLabel(c.Name)}: {(c.Message.Length > 0 ? c.Message : c.Result + ".")}")));
            var now = Now(clock, create.Options.UtcBasis);
            if (ScheduleProblem(create.Options, now) is { } problem) return (null, problem);
            var backdated = IsBackdated(create.Options, now);
            required = DeploymentWriter.IsRequiredCreate(create);
            var confirmations = new List<string>();
            if (required) confirmations.Add(RequiredConfirmation(create));
            if (backdated) confirmations.Add(BackdatedConfirmation(create.Options));
            extra = confirmations.Count == 0 ? null : string.Join(" ", confirmations);
            var target = $"{Object(create.Object)} to {(create.Collection is { } c ? Collection(c) : "no collection")}";
            entries.Add(DeploymentPlanEntry.Write(new ChangePlanItem(required ? "Create Required deployment" : "Create deployment", target,
                string.Join(Environment.NewLine, CreateLines(create, distribute is not null, backdated))), create));
        }
        if (entries.Count == 0) return (null, "There is nothing to change.");
        var title = create is not null ? $"Deploy {create.Object!.Name}" : $"Distribute {distribute!.Object?.Name}";
        var action = create is null ? "Distribute content" : distribute is null ? "Create deployment" : "Distribute and deploy";
        var actionName = create is null ? "Distribute content" : required ? "Create Required deployment" : distribute is null ? "Create deployment" : "Distribute and deploy";
        return (new DeploymentPlan(action, title, actionName, entries, hasUndo: create is not null, extraConfirmation: extra), null);
    }

    public static ChangePlanItem DistributeItem(DeploymentChange distribute)
    {
        var lines = new List<string>();
        if (distribute.Object is { } obj) lines.Add($"Object: {Object(obj)}.");
        lines.AddRange(distribute.Impact.Count > 0 ? distribute.Impact : distribute.DpGroups.Select(g => $"Distribution point group {g}."));
        lines.Add(DistributeNoUndo);
        return new ChangePlanItem("Distribute content", $"{distribute.Object?.Name} to {Count(distribute.DpGroups.Count, "distribution point group")}",
            string.Join(Environment.NewLine, lines));
    }

    /// <summary>The plan that removes one deployment; the service decides at write time whether it can run.</summary>
    public static (DeploymentPlan? Plan, string? Problem) BuildRemovePlan(DeploymentChange remove)
    {
        if (remove.Existing is not { } existing) return (null, "The deployment is not known. Refresh the history.");
        var lines = new List<string>
        {
            $"Deployment ID: {existing.DeploymentId}.",
            $"Object: {TypeName(existing.Type)} {existing.ObjectName}{(existing.ObjectId.Length > 0 ? $" ({existing.ObjectId})" : "")}.",
            $"Collection: {existing.CollectionName}{(existing.CollectionId.Length > 0 ? $" ({existing.CollectionId})" : "")}.",
            $"Purpose: {existing.Purpose}. Available: {Time(existing.AvailableTime)}."
                + (existing.Deadline is { } deadline ? $" Deadline: {Time(deadline)}." : "")
        };
        lines.AddRange(remove.Impact);
        lines.Add("Removes the deployment only when it still has these values and this product created it. Software that clients already installed stays installed.");
        var item = new ChangePlanItem("Remove deployment", $"{existing.ObjectName} from {existing.CollectionName}", string.Join(Environment.NewLine, lines));
        return (new DeploymentPlan("Remove deployment", $"Remove the deployment of {existing.ObjectName}", "Remove deployment",
            [DeploymentPlanEntry.Write(item, remove)], isDestructive: true, hasUndo: false), null);
    }
}
