using System.Globalization;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>Whether Promote can run for a run now, the ring it creates and the reason when it cannot.</summary>
public sealed record RingPromoteState(RingRunRing? Next, bool Open, string Reason);

/// <summary>The plain texts and the plans of the Rings tab, made from the prepared change only.</summary>
public static class RingText
{
    public const string Held = "Held";
    public const string Creating = "Creating";
    public const string Created = "Created";
    public const string Removed = "Removed";
    public const string Cancelled = "Cancelled";
    public const string Utc = "Utc";
    public const string LocalTime = "LocalTime";
    public const string UnknownCount = "unknown";

    public const string UndoRunNote = "Undo run in Open runs removes the deployments this run created. Software that clients already installed stays installed.";
    public const string RunFileNote = "The run file records the rings; ConfigMgr does not know that the deployments form a sequence.";
    public const string ProofLimit = "Each removal is checked against the site; a process of the same account that forges the run file and the audit log cannot be stopped.";

    public static bool IsUtc(string timeBasedOn) => string.Equals(timeBasedOn, Utc, StringComparison.OrdinalIgnoreCase);

    public static string Basis(string timeBasedOn) => DeploymentText.Basis(IsUtc(timeBasedOn));

    public static bool IsRequired(string purpose) => string.Equals(purpose, DeploymentText.Required, StringComparison.OrdinalIgnoreCase);

    public static string Time(DateTime? value) => value is { } v ? DeploymentText.Time(v) : "";

    public static string Threshold(int? percent) => percent is { } p ? $"{p.ToString(CultureInfo.CurrentCulture)} %" : "";

    /// <summary>The ring statuses of a run as one short line, for example "1 created, 2 held".</summary>
    public static string StatusLine(RingRun run)
    {
        var order = new[] { Created, Creating, Held, Removed, Cancelled };
        var parts = run.Rings.GroupBy(r => r.Status, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => Array.FindIndex(order, o => string.Equals(o, g.Key, StringComparison.OrdinalIgnoreCase)) is var i && i < 0 ? int.MaxValue : i)
            .Select(g => $"{g.Count().ToString(CultureInfo.CurrentCulture)} {g.Key.ToLowerInvariant()}");
        return string.Join(", ", parts) + (run.IsClosed ? " (closed)" : "");
    }

    /// <summary>The ring's own success rate as a percentage; null when the counts are not known or nothing is targeted.</summary>
    public static double? SuccessRate(RingLiveCounts? counts) =>
        counts is { CountsKnown: true, Targeted: > 0 } c ? c.Success * 100.0 / c.Targeted : null;

    /// <summary>
    /// The next held ring and whether its Promote can run: the ring before it must be Created, and a
    /// success threshold on that ring needs its live counts at or above the threshold. The service
    /// reads the counts again before the plan; this state only decides the button.
    /// </summary>
    public static RingPromoteState PromoteState(RingRun? run, IReadOnlyDictionary<int, RingLiveCounts> counts)
    {
        if (run is null) return new RingPromoteState(null, false, "Select a run.");
        if (run.IsClosed) return new RingPromoteState(null, false, "The run is closed. Nothing can be promoted.");
        var rings = run.Rings.OrderBy(r => r.Index).ToList();
        var index = rings.FindIndex(r => string.Equals(r.Status, Held, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return new RingPromoteState(null, false, "No ring of this run is held.");
        var next = rings[index];
        if (index == 0) return new RingPromoteState(next, true, $"Promote creates the deployment of ring {next.Name}.");
        var previous = rings[index - 1];
        if (!string.Equals(previous.Status, Created, StringComparison.OrdinalIgnoreCase))
            return new RingPromoteState(next, false,
                $"Ring {previous.Name} is {previous.Status.ToLowerInvariant()}, not created, so ring {next.Name} cannot be promoted. Select Refresh and reconcile.");
        if (previous.SuccessThresholdPercent is { } threshold)
        {
            counts.TryGetValue(previous.Index, out var live);
            if (SuccessRate(live) is not { } rate)
                return new RingPromoteState(next, false,
                    $"Ring {previous.Name} needs {Threshold(threshold)} success before ring {next.Name}, and its live counts are not known yet. Select Refresh and reconcile, then try again.");
            if (rate < threshold)
                return new RingPromoteState(next, false,
                    $"Ring {previous.Name} has {rate.ToString("0", CultureInfo.CurrentCulture)} % success ({live!.Success.ToString(CultureInfo.CurrentCulture)} of {live.Targeted.ToString(CultureInfo.CurrentCulture)}); ring {next.Name} needs {Threshold(threshold)}.");
        }
        return new RingPromoteState(next, true, $"Promote creates the deployment of ring {next.Name}. The live counts are read again before the plan.");
    }

    /// <summary>The checks that stop a ring change: every failed or unknown check of the change and of the rings it creates now.</summary>
    public static IReadOnlyList<(string Ring, PreflightCheck Check)> Blocking(RingChange change, IEnumerable<RingExpansionRow> rows) =>
        change.Checks.Where(DeploymentText.Blocks).Select(c => ("Run", c))
            .Concat(rows.SelectMany(r => r.Checks.Where(DeploymentText.Blocks).Select(c => (r.Name, c))))
            .Concat(change.RingChanges.SelectMany(c => c.Checks.Where(DeploymentText.Blocks).Select(k => (c.Collection?.Name ?? "", k))))
            .ToList();

    public static string BlockingText(IReadOnlyList<(string Ring, PreflightCheck Check)> blocking) =>
        string.Join(" ", blocking.Select(b => $"{(b.Ring == "Run" ? "" : $"Ring {b.Ring}: ")}{DeploymentText.CheckLabel(b.Check.Name)}: {DeploymentText.CheckMessage(b.Check, null)}"));

    private static DateTime Now(TimeProvider clock, string timeBasedOn) => DeploymentText.Now(clock, IsUtc(timeBasedOn));

    /// <summary>The rings a run creates now: every ring, or the first ring when the later rings are held.</summary>
    public static IReadOnlyList<RingExpansionRow> CreatedNow(IReadOnlyList<RingExpansionRow> rows, bool hold) =>
        hold ? rows.OrderBy(r => r.Index).Take(1).ToList() : rows.OrderBy(r => r.Index).ToList();

    private static string RingLines(RingExpansionRow row, string basis, bool created)
    {
        var lines = new List<string>
        {
            $"Collection: {row.CollectionName} ({row.CollectionId}), {DeploymentText.Count(row.MemberCount, "member")} now.",
            $"Purpose: {(IsRequired(row.Purpose) ? DeploymentText.Required : DeploymentText.Available)}. Available: {Time(row.AvailableTime)} {basis}."
                + (IsRequired(row.Purpose) && row.Deadline is { } deadline ? $" Deadline: {Time(deadline)} {basis}." : "")
        };
        if (row.DpGroup.Length > 0)
            lines.Add($"Distribution point group: {row.DpGroup}. The distribution is requested first and has no Undo. " + DeploymentText.CreateNowHint);
        if (row.SuccessThresholdPercent is { } threshold)
            lines.Add($"Success threshold: {Threshold(threshold)}. The next ring can be promoted when this ring reaches it.");
        lines.AddRange(DeploymentText.CheckLines(row.Checks));
        if (created && IsRequired(row.Purpose))
            lines.Add($"Required: clients install at the deadline on every device of the collection ({DeploymentText.Count(row.MemberCount, "device")} now).");
        if (!created) lines.Add("Held: the run file records this ring. Promote shows its own plan and creates it later; the checks run again then.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The text of the second check box of a run or a Promote: the number of Required rings it
    /// creates now with their devices in total, and the past available times, as they apply.
    /// </summary>
    public static string SecondConfirmation(int requiredRings, int devices, RingExpansionRow? single, IReadOnlyList<RingExpansionRow> backdated, string basis)
    {
        var parts = new List<string>();
        if (requiredRings > 0)
            parts.Add(requiredRings == 1 && single is not null
                ? $"the Required ring {single.Name} installs software on every device of {single.CollectionName} ({DeploymentText.Count(devices, "device")} now) at the deadline {Time(single.Deadline)} {basis}"
                : $"{requiredRings.ToString(CultureInfo.CurrentCulture)} Required rings enforce on {DeploymentText.Count(devices, "device")} in total at their deadlines");
        if (backdated.Count > 0)
            parts.Add(backdated.Count == 1
                ? $"the available time of ring {backdated[0].Name} is in the past, so its deployment is available at once"
                : $"the available times of rings {string.Join(", ", backdated.Select(r => r.Name))} are in the past, so their deployments are available at once");
        return parts.Count == 0 ? "" : $"I understand that {string.Join(", and that ", parts)}.";
    }

    private static IReadOnlyList<RingExpansionRow> Backdated(IReadOnlyList<RingExpansionRow> rows, RingChange prepared, DateTime now) =>
        rows.Where(r => r.AvailableTime < now.AddMinutes(-1)
            || prepared.RingChanges.Any(c => c.NeedsBackdatedAcknowledgement && string.Equals(c.Collection?.CollectionId, r.CollectionId, StringComparison.OrdinalIgnoreCase))).ToList();

    private static string ObjectText(DeployableObject? obj) => obj is null ? "" : DeploymentText.Object(obj);

    /// <summary>
    /// The plan of a new run: one line for each ring, the rings it creates now and the rings it
    /// holds. Null with the reasons when a check blocks the run or a ring it creates now.
    /// </summary>
    public static (RingRunPlan? Plan, string? Problem) BuildCreateRunPlan(RingChange prepared, TimeProvider clock)
    {
        if (prepared.Kind != RingChangeKind.CreateRun || prepared.Plan is not { } plan || prepared.Object is not { } obj || prepared.Rows.Count == 0)
            return (null, "The run could not be prepared. Select Expand and Validate again.");
        var rows = prepared.Rows.OrderBy(r => r.Index).ToList();
        var now = CreatedNow(rows, plan.HoldLaterRings);
        var blocking = Blocking(prepared, now);
        if (blocking.Count > 0 || prepared.Blocked) return (null, "The run cannot be created: " + BlockingText(blocking));
        var basis = Basis(plan.TimeBasedOn);
        var clockNow = Now(clock, plan.TimeBasedOn);
        var past = rows.Where(r => IsRequired(r.Purpose) && r.Deadline is { } d && d <= clockNow).ToList();
        if (past.Count > 0) return (null, $"The deadline of ring {past[0].Name} is in the past. Choose a later plan start and select Expand again.");
        var requiredRows = now.Where(r => IsRequired(r.Purpose)).ToList();
        var requiredRings = prepared.RingChanges.Count > 0 ? prepared.RequiredRings : requiredRows.Count;
        var requiredDevices = prepared.RingChanges.Count > 0 ? prepared.RequiredDevices : requiredRows.Sum(r => r.MemberCount);
        var backdated = Backdated(now, prepared, clockNow);
        var entries = rows.Select(row =>
        {
            var created = now.Contains(row);
            var action = created ? IsRequired(row.Purpose) ? "Create Required deployment" : "Create deployment" : "Hold ring";
            var item = new ChangePlanItem(action, $"Ring {row.Name}: {obj.Name} to {row.CollectionName} ({row.CollectionId})", RingLines(row, basis, created));
            return new RingPlanEntry(item, created ? RingEntryKind.Create : RingEntryKind.Hold, row.CollectionId);
        }).ToList();
        var first = entries[0].Item;
        entries[0] = entries[0] with
        {
            Item = first with
            {
                Detail = string.Join(Environment.NewLine, new[] { $"Object: {ObjectText(obj)}.", $"Plan: {plan.Name}, {DeploymentText.Count(rows.Count, "ring")}, "
                    + (plan.HoldLaterRings ? "later rings held for Promote." : "every ring created now with its own dates."), RunFileNote, UndoRunNote, first.Detail ?? "" })
            }
        };
        var extra = requiredRings > 0 || backdated.Count > 0 || prepared.NeedsBackdatedAcknowledgement
            ? SecondConfirmation(requiredRings, requiredDevices, requiredRows.Count == 1 ? requiredRows[0] : null, backdated, basis)
            : null;
        var distributes = now.Any(r => r.DpGroup.Length > 0);
        var actionName = requiredRings > 0 ? $"Create run with {DeploymentText.Count(requiredRings, "Required ring")}" : "Create run";
        return (new RingRunPlan("Create ring run", $"Ring run of {obj.Name} with {plan.Name}", actionName, prepared, entries,
            extraConfirmation: extra, optionalChoice: distributes ? DeploymentText.CreateNowChoice : null)
        {
            NeedsRequiredConfirmation = requiredRings > 0 || prepared.NeedsRequiredConfirmation,
            AcknowledgesBackdated = backdated.Count > 0 || prepared.NeedsBackdatedAcknowledgement
        }, null);
    }

    /// <summary>The plan of a Promote: the ring, its collection with the member count now, the deadline shift and the checks.</summary>
    public static (RingRunPlan? Plan, string? Problem) BuildPromotePlan(RingChange prepared, TimeProvider clock, string timeBasedOn)
    {
        if (prepared.Kind != RingChangeKind.Promote || prepared.Rows.Count != 1) return (null, "The ring could not be prepared. Select Refresh and reconcile, then Promote again.");
        var row = prepared.Rows[0];
        var blocking = Blocking(prepared, prepared.Rows);
        if (blocking.Count > 0 || prepared.Blocked) return (null, $"Ring {row.Name} cannot be promoted: " + BlockingText(blocking));
        var basis = Basis(timeBasedOn);
        var obj = prepared.RingChanges.FirstOrDefault()?.Object ?? prepared.Object;
        var lines = new List<string>();
        if (obj is not null) lines.Add($"Object: {ObjectText(obj)}.");
        lines.Add(RingLines(row, basis, created: true));
        lines.AddRange(prepared.Impact);
        lines.Add(UndoRunNote);
        var required = prepared.NeedsRequiredConfirmation || IsRequired(row.Purpose);
        var devices = prepared.RingChanges.Count > 0 ? prepared.RequiredDevices : row.MemberCount;
        var backdated = Backdated([row], prepared, Now(clock, timeBasedOn));
        if (prepared.ShiftMinutes > 0)
            lines.Insert(1, $"The deadline of this ring passed: this ring and every later held ring move {DeploymentText.Count(prepared.ShiftMinutes, "minute")} later, so each keeps its time from available to deadline.");
        var action = required ? "Create Required deployment" : "Create deployment";
        var item = new ChangePlanItem(action, $"Ring {row.Name}: {obj?.Name} to {row.CollectionName} ({row.CollectionId})", string.Join(Environment.NewLine, lines));
        var extra = required || backdated.Count > 0 || prepared.NeedsBackdatedAcknowledgement
            ? SecondConfirmation(required ? 1 : 0, devices, row, backdated, basis)
            : null;
        return (new RingRunPlan("Promote ring", $"Promote ring {row.Name}", required ? "Promote Required ring" : "Promote ring", prepared,
            [new RingPlanEntry(item, RingEntryKind.Promote, row.CollectionId)], extraConfirmation: extra,
            optionalChoice: row.DpGroup.Length > 0 ? DeploymentText.CreateNowChoice : null)
        {
            NeedsRequiredConfirmation = required, AcknowledgesBackdated = backdated.Count > 0 || prepared.NeedsBackdatedAcknowledgement
        }, null);
    }

    /// <summary>
    /// The destructive plan of Undo run: each deployment the run created with its proof state, the
    /// held rings it cancels and the close of the run. A deployment whose check fails is kept and named.
    /// </summary>
    public static (RingRunPlan? Plan, string? Problem) BuildUndoRunPlan(RingChange prepared, string runName)
    {
        if (prepared.Kind != RingChangeKind.UndoRun) return (null, "The run could not be prepared. Select Refresh and reconcile, then Undo run again.");
        var entries = new List<RingPlanEntry>();
        foreach (var removal in prepared.RingChanges)
        {
            var existing = removal.Site ?? removal.Existing;
            var id = removal.Existing?.DeploymentId ?? removal.CreatedId ?? "";
            var target = existing is null ? id : $"{existing.ObjectName} from {existing.CollectionName} ({existing.CollectionId})";
            var lines = new List<string> { $"Deployment ID: {id}." };
            lines.AddRange(DeploymentText.CheckLines(removal.Checks, existing?.Type));
            lines.AddRange(removal.Impact);
            if (DeploymentText.Blocking(removal).Count > 0)
            {
                var reason = $"{DeploymentWriter.SkippedPrefix}: kept because a check did not pass. {DeploymentText.BlockingText(removal)}";
                entries.Add(new RingPlanEntry(new ChangePlanItem("Keep deployment", target, string.Join(Environment.NewLine, lines.Append(reason))), RingEntryKind.Keep, id, reason));
                continue;
            }
            lines.Add("Removes the deployment only when it still has these values and this run created it. Software that clients already installed stays installed.");
            entries.Add(new RingPlanEntry(new ChangePlanItem("Remove deployment", target, string.Join(Environment.NewLine, lines)), RingEntryKind.Remove, id));
        }
        foreach (var held in prepared.Rows.OrderBy(r => r.Index))
            entries.Add(new RingPlanEntry(new ChangePlanItem("Cancel held ring", $"Ring {held.Name}: {held.CollectionName} ({held.CollectionId})",
                "The run file marks the ring Cancelled. Nothing changes in ConfigMgr."), RingEntryKind.Cancel, held.CollectionId));
        var removals = entries.Where(e => e.Kind == RingEntryKind.Remove).ToList();
        var closeLines = prepared.Impact.Append("The run file moves to the closed folder.").Append(ProofLimit);
        entries.Add(new RingPlanEntry(new ChangePlanItem("Close run", runName, string.Join(Environment.NewLine, closeLines)), RingEntryKind.Close, prepared.RunId ?? ""));
        var question = removals.Count == 0 ? null
            : DeploymentWriter.RemovalQuestion(prepared.RingChanges.Where(c => DeploymentText.Blocking(c).Count == 0).ToList());
        return (new RingRunPlan("Undo ring run", $"Undo the ring run {runName}", removals.Count > 0 ? $"Remove {DeploymentText.Count(removals.Count, "deployment")}" : "Undo run",
            prepared, entries, isDestructive: true, removalQuestion: question), null);
    }

    /// <summary>The plan of Close run: the run file moves to the closed folder; no ConfigMgr change.</summary>
    public static (RingRunPlan? Plan, string? Problem) BuildClosePlan(RingChange prepared, string runName)
    {
        if (prepared.Kind != RingChangeKind.CloseRun) return (null, "The run could not be prepared. Select Refresh and reconcile, then Close run again.");
        var blocking = Blocking(prepared, []);
        if (blocking.Count > 0) return (null, "The run cannot be closed: " + BlockingText(blocking));
        var lines = prepared.Impact.Concat(
        [
            "The run file moves to the closed folder. No deployment is changed.",
            "Held rings are not created any more. Created deployments stay in ConfigMgr; Show closed runs lists the run."
        ]);
        return (new RingRunPlan("Close ring run", $"Close the ring run {runName}", "Close run", prepared,
            [new RingPlanEntry(new ChangePlanItem("Close run", runName, string.Join(Environment.NewLine, lines)), RingEntryKind.Close, prepared.RunId ?? "")]), null);
    }
}
