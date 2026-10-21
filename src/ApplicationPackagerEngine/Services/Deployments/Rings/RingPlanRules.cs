using System.Globalization;

namespace ApplicationPackagerEngine.Services;

/// <summary>The rule results of the rows of a ring run: errors block the run, warnings need a confirmation.</summary>
/// <param name="Errors">Per ring index (0 for the run), the plain reasons that block it.</param>
public sealed record RingRuleResult(IReadOnlyList<(int Index, string Message)> Errors, IReadOnlyList<(int Index, string Message)> Warnings)
{
    public bool Blocked => Errors.Count > 0;
}

/// <summary>Whether a ring's success rate opens Promote of the next ring.</summary>
/// <param name="Percent">The success rate rounded to one decimal; null when not known.</param>
public sealed record RingThreshold(bool Met, double? Percent, string Reason);

/// <summary>
/// The pure rules of ring plans and runs: plan validation, expansion to absolute times, the rules
/// of the rows a run submits, the Promote shift and the success threshold. Times are wall-clock
/// values in the plan's time basis; "now" must be read in the same basis.
/// </summary>
public static class RingPlanRules
{
    public const int MaxRings = 50;
    public const int MaxOffsetDays = 3650;
    public const int MaxNameLength = 64;
    public const string LocalTime = "LocalTime";
    public const string Utc = "Utc";
    public const string CheckName = "RingRules";

    public const string DefaultFastNetwork = "DownloadContentFromDistributionPointAndRunLocally";
    public const string DefaultSlowNetwork = "DoNotRunProgram";
    public const string DefaultRerun = "NeverRerunDeployedProgram";

    /// <summary>An available time earlier than now by more than this is reported as in the past.</summary>
    public static readonly TimeSpan BackdateTolerance = TimeSpan.FromMinutes(5);

    public static string Label(int index, string? name) => $"Ring {index.ToString(CultureInfo.InvariantCulture)} '{name}'";

    private static string Text(DateTime value) => value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The problems that stop the plan from loading or being saved; empty when it loads. An empty collection ID is allowed: such a plan loads and cannot run.</summary>
    public static IReadOnlyList<string> Validate(RingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(plan.Name)) errors.Add("The plan has no name.");
        if (plan.TimeBasedOn is not (LocalTime or Utc)) errors.Add($"The time basis '{plan.TimeBasedOn}' is not LocalTime or Utc.");
        var rings = plan.Rings ?? [];
        if (rings.Count == 0) errors.Add("The plan has no rings.");
        if (rings.Count > MaxRings)
        {
            errors.Add($"The plan has {rings.Count.ToString(CultureInfo.InvariantCulture)} rings. A plan can have at most {MaxRings.ToString(CultureInfo.InvariantCulture)} rings.");
            return errors;
        }
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int? lastDeadline = null;
        var lastLabel = "";
        for (var i = 0; i < rings.Count; i++)
        {
            var r = rings[i];
            var label = Label(i + 1, r.Name);
            if (string.IsNullOrWhiteSpace(r.Name)) errors.Add($"Ring {(i + 1).ToString(CultureInfo.InvariantCulture)} has no name.");
            else if (r.Name.Length > MaxNameLength || r.Name.Any(char.IsControl)) errors.Add($"{label} needs a name of at most {MaxNameLength} characters on one line.");
            var id = (r.CollectionId ?? "").Trim().ToUpperInvariant();
            if (id.Length > 0)
            {
                if (DeploymentService.IsBuiltInCollection(id)) errors.Add($"{label} targets built-in collection {id}. Built-in collections are blocked.");
                else if (id.Length != 8 || !id.All(char.IsAsciiLetterOrDigit)) errors.Add($"{label} collection ID '{r.CollectionId}' is not an 8-character collection ID.");
                if (seen.TryGetValue(id, out var other)) errors.Add($"{label} targets {id}, which {other} also targets. Rings must not share a collection.");
                else seen[id] = label;
            }
            if (r.Purpose is not (DeploymentPurposes.Available or DeploymentPurposes.Required)) errors.Add($"{label} purpose '{r.Purpose}' is not Available or Required.");
            var availableOk = r.AvailableOffsetDays is >= 0 and <= MaxOffsetDays;
            if (!availableOk) errors.Add($"{label} available offset must be 0 to {MaxOffsetDays.ToString(CultureInfo.InvariantCulture)} days.");
            if (r.Purpose == DeploymentPurposes.Required)
            {
                if (r.DeadlineOffsetDays is not { } deadline || deadline < 0 || deadline > MaxOffsetDays)
                    errors.Add($"{label} is Required and needs a deadline offset of 0 to {MaxOffsetDays.ToString(CultureInfo.InvariantCulture)} days.");
                else
                {
                    if (availableOk && deadline <= r.AvailableOffsetDays)
                        errors.Add($"{label} deadline offset ({deadline.ToString(CultureInfo.InvariantCulture)}) must be later than its available offset ({r.AvailableOffsetDays.ToString(CultureInfo.InvariantCulture)}).");
                    if (lastDeadline is { } previous && deadline <= previous)
                        errors.Add($"{label} deadline offset ({deadline.ToString(CultureInfo.InvariantCulture)}) must be later than the {lastLabel} deadline offset ({previous.ToString(CultureInfo.InvariantCulture)}).");
                    lastDeadline = deadline;
                    lastLabel = label;
                }
            }
            if (!DeploymentScheduleRules.Notifications.Contains(r.UserNotification)) errors.Add($"{label} user notification '{r.UserNotification}' is not valid.");
            else if (r.Purpose == DeploymentPurposes.Available && r.UserNotification == "HideAll") errors.Add($"{label} is Available with HideAll: users never see it and it never installs.");
            if (!DeploymentScheduleRules.TaskSequenceAvailability.Contains(r.TaskSequenceAvailability)) errors.Add($"{label} task sequence availability '{r.TaskSequenceAvailability}' is not valid.");
            if (r.TaskSequenceDeploymentOption is { } option && !DeploymentScheduleRules.TaskSequenceOptions.Contains(option))
                errors.Add($"{label} task sequence content option '{option}' is not valid.");
            if (r.FastNetworkOption is { } fast && !DeploymentScheduleRules.FastNetwork.Contains(fast)) errors.Add($"{label} fast network option '{fast}' is not valid.");
            if (r.SlowNetworkOption is { } slow && !DeploymentScheduleRules.SlowNetwork.Contains(slow))
                errors.Add(slow == "RunProgramFromDistributionPoint"
                    ? $"{label} slow network option RunProgramFromDistributionPoint is refused by ConfigMgr. Use DoNotRunProgram or DownloadContentFromDistributionPointAndLocally."
                    : $"{label} slow network option '{slow}' is not valid.");
            if (r.RerunBehavior is { } rerun && !DeploymentScheduleRules.Rerun.Contains(rerun)) errors.Add($"{label} rerun behavior '{rerun}' is not valid.");
            if (r.SuccessThresholdPercent is < 0 or > 100) errors.Add($"{label} success threshold must be empty or 0 to 100.");
            if ((r.DpGroup ?? "").IndexOfAny(['*', '?', '[', ']']) >= 0 || (r.DpGroup ?? "").Any(char.IsControl))
                errors.Add($"{label} DP group '{r.DpGroup}' has a wildcard or control character in its name.");
        }
        return errors;
    }

    /// <summary>
    /// One row per ring: the start plus the offsets in whole calendar days at the start's time of
    /// day. For a LocalTime plan a time that a daylight saving change skips in <paramref name="local"/>
    /// moves later by the length of the gap, as Windows reads such a time, and a time that occurs
    /// twice is kept; both get a warning check. Available rings get no deadline.
    /// </summary>
    public static IReadOnlyList<RingExpansionRow> Expand(RingPlan plan, DateTime start, TimeZoneInfo? local = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var zone = local ?? TimeZoneInfo.Local;
        var wall = DateTime.SpecifyKind(new DateTime(start.Ticks - start.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Unspecified);
        var localPlan = plan.TimeBasedOn != Utc;
        var rows = new List<RingExpansionRow>();
        for (var i = 0; i < (plan.Rings ?? []).Count; i++)
        {
            var r = plan.Rings![i];
            var checks = new List<PreflightCheck>();
            var available = Adjust(wall.AddDays(r.AvailableOffsetDays), localPlan, zone, Label(i + 1, r.Name), "available time", checks);
            DateTime? deadline = r.Purpose == DeploymentPurposes.Required && r.DeadlineOffsetDays is { } d
                ? Adjust(wall.AddDays(d), localPlan, zone, Label(i + 1, r.Name), "deadline", checks)
                : null;
            rows.Add(new RingExpansionRow(i + 1, r.Name, (r.CollectionId ?? "").Trim().ToUpperInvariant(), r.CollectionName ?? "", 0, r.Purpose, available, deadline,
                r.DpGroup ?? "", r.SuccessThresholdPercent, checks) { Definition = r });
        }
        return rows;
    }

    private static DateTime Adjust(DateTime value, bool localPlan, TimeZoneInfo zone, string label, string what, List<PreflightCheck> checks)
    {
        if (!localPlan) return value;
        if (zone.IsInvalidTime(value))
        {
            var before = zone.GetUtcOffset(value.AddHours(-12));
            var moved = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value - before, DateTimeKind.Utc), zone), DateTimeKind.Unspecified);
            checks.Add(new PreflightCheck(CheckName, DeploymentCheckResults.Warn,
                $"{label} {what} {Text(value)} does not exist in the time zone of this computer (a daylight saving change skips it). It moved to {Text(moved)}."));
            return moved;
        }
        if (zone.IsAmbiguousTime(value))
            checks.Add(new PreflightCheck(CheckName, DeploymentCheckResults.Warn,
                $"{label} {what} {Text(value)} occurs twice in the time zone of this computer (a daylight saving change repeats the hour). Clients can use either time."));
        return value;
    }

    /// <summary>
    /// The rules of the rows a run submits: a target collection per ring, no built-in collection,
    /// no shared collection, a Required deadline later than the available time, not in the past and
    /// later than the deadline of the nearest earlier Required ring, no Available ring with HideAll,
    /// and a warning for an available time in the past.
    /// </summary>
    public static RingRuleResult CheckRows(IReadOnlyList<RingExpansionRow> rows, DateTime nowInBasis)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var errors = new List<(int, string)>();
        var warnings = new List<(int, string)>();
        if (rows.Count == 0) errors.Add((0, "There are no rings to run."));
        if (rows.Count > MaxRings) errors.Add((0, $"A run can have at most {MaxRings.ToString(CultureInfo.InvariantCulture)} rings."));
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        DateTime? lastDeadline = null;
        var lastLabel = "";
        foreach (var r in rows)
        {
            var label = Label(r.Index, r.Name);
            var id = (r.CollectionId ?? "").Trim().ToUpperInvariant();
            if (id.Length == 0) errors.Add((r.Index, $"{label} has no target collection."));
            else if (DeploymentService.IsBuiltInCollection(id)) errors.Add((r.Index, $"{label} targets built-in collection {id}. Built-in collections are blocked."));
            else if (seen.TryGetValue(id, out var other)) errors.Add((r.Index, $"{label} targets {id}, which {other} also targets. Rings must not share a collection."));
            else seen[id] = label;
            if (r.Purpose == DeploymentPurposes.Required)
            {
                if (r.Deadline is not { } deadline) errors.Add((r.Index, $"{label} is Required and has no deadline."));
                else
                {
                    if (deadline <= r.AvailableTime)
                        errors.Add((r.Index, $"{label} deadline {Text(deadline)} must be later than its available time {Text(r.AvailableTime)}."));
                    if (deadline <= nowInBasis)
                        errors.Add((r.Index, $"{label} deadline {Text(deadline)} is in the past; the deployment would enforce at once."));
                    if (lastDeadline is { } previous && deadline <= previous)
                        errors.Add((r.Index, $"{label} deadline {Text(deadline)} must be later than the {lastLabel} deadline {Text(previous)}."));
                    lastDeadline = deadline;
                    lastLabel = label;
                }
            }
            else if (r.Purpose != DeploymentPurposes.Available) errors.Add((r.Index, $"{label} purpose '{r.Purpose}' is not Available or Required."));
            if (r.Purpose == DeploymentPurposes.Available && r.Definition?.UserNotification == "HideAll")
                errors.Add((r.Index, $"{label} is Available with HideAll: users never see it and it never installs."));
            if (r.AvailableTime < nowInBasis - BackdateTolerance)
                warnings.Add((r.Index, $"{label} available time {Text(r.AvailableTime)} is in the past; clients see it at once."));
        }
        return new RingRuleResult(errors, warnings);
    }

    /// <summary>
    /// The minutes a Held ring whose deadline passed moves later so that it becomes available at the
    /// next whole minute and keeps its available-to-deadline gap; 0 when the deadline is ahead or
    /// there is none.
    /// </summary>
    public static int PromoteShift(DateTime available, DateTime? deadline, DateTime nowInBasis)
    {
        if (deadline is not { } d || d > nowInBasis) return 0;
        var start = nowInBasis.Date.AddHours(nowInBasis.Hour).AddMinutes(nowInBasis.Minute + 1);
        return (int)Math.Ceiling((start - available).TotalMinutes);
    }

    /// <summary>
    /// Whether the live counts of a ring reach its threshold. No threshold is always met; counts
    /// that are not known, no targeted clients and a rate below the threshold are not.
    /// </summary>
    public static RingThreshold Threshold(RingLiveCounts? counts, int? thresholdPercent)
    {
        if (thresholdPercent is not { } t) return new RingThreshold(true, null, "");
        var shown = t.ToString(CultureInfo.InvariantCulture);
        if (counts is null || counts.State is not (RingLiveStates.Found or RingLiveStates.Summarized))
            return new RingThreshold(false, null, $"No live counts; the {shown}% threshold cannot be checked.");
        if (!counts.CountsKnown)
            return new RingThreshold(false, null, $"The site has not summarized this ring yet; the {shown}% threshold cannot be checked.");
        if (counts.Targeted <= 0) return new RingThreshold(false, 0, $"No clients targeted yet; the threshold is {shown}%.");
        var percent = Math.Round(counts.Success * 100.0 / counts.Targeted, 1, MidpointRounding.ToEven);
        var text = percent.ToString("0.#", CultureInfo.InvariantCulture);
        return percent >= t
            ? new RingThreshold(true, percent, "")
            : new RingThreshold(false, percent, $"Success {text}% is below the {shown}% threshold.");
    }

    /// <summary>The part 1 options of a ring: the purpose and times of the row, the ring's options, and the defaults the plan files leave out.</summary>
    public static DeploymentOptions Options(DeploymentObjectType type, RingDefinition ring, DateTime available, DateTime? deadline, bool utc)
    {
        ArgumentNullException.ThrowIfNull(ring);
        var required = ring.Purpose == DeploymentPurposes.Required;
        var availability = ring.TaskSequenceAvailability;
        return type switch
        {
            DeploymentObjectType.Package => new DeploymentOptions(ring.Purpose, available, required ? deadline : null, utc, ring.UserNotification,
                ring.OverrideServiceWindow, ring.RebootOutsideServiceWindow, ring.AllowMetered, ring.FastNetworkOption ?? DefaultFastNetwork, ring.RerunBehavior ?? DefaultRerun,
                false, false, false, false, false, ring.SlowNetworkOption ?? DefaultSlowNetwork),
            DeploymentObjectType.TaskSequence => new DeploymentOptions(ring.Purpose, available, required ? deadline : null, utc, ring.UserNotification,
                ring.OverrideServiceWindow, ring.RebootOutsideServiceWindow, ring.AllowMetered,
                ring.TaskSequenceDeploymentOption ?? (availability == "Clients" ? "DownloadAllContentLocallyBeforeStartingTaskSequence" : "DownloadContentLocallyWhenNeededByRunningTaskSequence"),
                ring.RerunBehavior ?? DefaultRerun, ring.ShowTaskSequenceProgress, false, false, false, false, TaskSequenceAvailability: availability),
            DeploymentObjectType.SoftwareUpdateGroup => new DeploymentOptions(ring.Purpose, available, required ? deadline : null, utc, ring.UserNotification,
                ring.OverrideServiceWindow, ring.RebootOutsideServiceWindow, ring.AllowMetered, "", "", false, false, ring.AllowBoundaryFallback, ring.RequirePostRebootFullScan, false,
                AllowMicrosoftUpdate: ring.AllowMicrosoftUpdate),
            _ => new DeploymentOptions(ring.Purpose, available, required ? deadline : null, utc, ring.UserNotification,
                ring.OverrideServiceWindow, ring.RebootOutsideServiceWindow, ring.AllowMetered, "", "", false, false, false, false, false)
        };
    }

    /// <summary>The current wall-clock time in the plan's time basis.</summary>
    public static DateTime NowIn(string timeBasedOn, DateTime utcNow, TimeZoneInfo? local = null) =>
        DeploymentScheduleRules.NowIn(timeBasedOn == Utc, utcNow, local);
}
