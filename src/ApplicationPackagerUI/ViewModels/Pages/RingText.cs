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

    /// <summary>
    /// The success rate the page shows, as the ring services round it: one decimal, and never at or
    /// above a threshold the counts do not meet (2000 of 2001 is 99.9, not 100). Without a threshold
    /// the rate is rounded against 100, so 100 means every targeted client succeeded. Null when the
    /// counts are not known or nothing is targeted.
    /// </summary>
    public static double? ShownRate(RingLiveCounts? counts, int? threshold) =>
        counts is { CountsKnown: true, Targeted: > 0 } ? RingPlanRules.Threshold(counts, threshold ?? 100).Percent : null;

    public static string Percent(double rate) => $"{rate.ToString("0.#", CultureInfo.CurrentCulture)} %";

    /// <summary>The problems of a run file that block Promote and Undo run, as one line; empty when the run has none.</summary>
    public static string ProblemText(RingRun? run) =>
        run is { Problems.Count: > 0 } r ? "Promote and Undo run are not available until the run file is corrected: " + string.Join(" ", r.Problems) : "";

    /// <summary>
    /// The next held ring and whether its Promote can run: the ring before it must be Created, and a
    /// success threshold on that ring needs its live counts to meet it by the rule of the ring services.
    /// The service reads the counts again before the plan; this state only decides the button.
    /// </summary>
    public static RingPromoteState PromoteState(RingRun? run, IReadOnlyDictionary<int, RingLiveCounts> counts)
    {
        if (run is null) return new RingPromoteState(null, false, "Select a run.");
        if (run.IsClosed) return new RingPromoteState(null, false, "The run is closed. Nothing can be promoted.");
        if (run.Problems.Count > 0) return new RingPromoteState(null, false, ProblemText(run));
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
            var gate = RingPlanRules.Threshold(live, threshold);
            if (!gate.Met)
                return ShownRate(live, threshold) is { } rate
                    ? new RingPromoteState(next, false,
                        $"Ring {previous.Name} has {Percent(rate)} success ({live!.Success.ToString(CultureInfo.CurrentCulture)} of {live.Targeted.ToString(CultureInfo.CurrentCulture)}); ring {next.Name} needs {Threshold(threshold)}.")
                    : new RingPromoteState(next, false,
                        $"Ring {previous.Name} needs {Threshold(threshold)} success before ring {next.Name}, and its live counts are not known yet. Select Refresh and reconcile, then try again.");
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

    /// <summary>The prepared create of a ring: by the ring index the service sets, else by the collection.</summary>
    public static DeploymentChange? ChangeOf(RingChange prepared, RingExpansionRow row) =>
        prepared.RingChanges.FirstOrDefault(c => c.Ring?.RingIndex == row.Index)
        ?? prepared.RingChanges.FirstOrDefault(c => c.Ring is null && string.Equals(c.Collection?.CollectionId, row.CollectionId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The deployment settings a ring gets: from the prepared create of a ring created now, else from the
    /// ring of the plan with the defaults the ring services apply, so a held ring shows its settings too.
    /// </summary>
    public static IReadOnlyList<string> OptionLines(DeploymentObjectType type, DeploymentOptions options)
    {
        var lines = new List<string>();
        if (DeploymentText.HasNotification(type)) lines.Add($"User notification: {DeploymentText.ChoiceText(DeploymentText.Notifications, options.UserNotification)}.");
        lines.Add(DeploymentText.OptionsLine(type, options));
        if (IsRequired(options.Purpose))
        {
            lines.Add(options.OverrideServiceWindow ? "The installation runs outside maintenance windows." : "The installation waits for a maintenance window.");
            lines.Add(options.RebootOutsideServiceWindow ? "Devices can restart outside maintenance windows." : "Restarts wait for a maintenance window.");
        }
        return lines;
    }

    private static DeploymentOptions? OptionsOf(RingChange prepared, RingExpansionRow row, DeploymentChange? change, bool utc)
    {
        if (change?.Options is { } options) return options;
        if (prepared.Object is not { } obj) return null;
        var ring = row.Definition ?? (prepared.Plan is { } plan && row.Index >= 1 && row.Index <= plan.Rings.Count ? plan.Rings[row.Index - 1] : null);
        return ring is null ? null : RingPlanRules.Options(obj.Type, ring with { Purpose = row.Purpose }, row.AvailableTime, row.Deadline, utc);
    }

    private static string RingLines(RingExpansionRow row, string basis, bool created, RingChange? prepared = null, DeploymentChange? change = null)
    {
        var lines = new List<string>
        {
            $"Collection: {row.CollectionName} ({row.CollectionId}), {DeploymentText.Count(row.MemberCount, "member")} now.",
            $"Purpose: {(IsRequired(row.Purpose) ? DeploymentText.Required : DeploymentText.Available)}. Available: {Time(row.AvailableTime)} {basis}."
                + (IsRequired(row.Purpose) && row.Deadline is { } deadline ? $" Deadline: {Time(deadline)} {basis}." : "")
        };
        var type = change?.Object?.Type ?? prepared?.Object?.Type;
        var utc = change?.Options?.UtcBasis ?? (prepared?.Plan is { } plan && IsUtc(plan.TimeBasedOn));
        if (prepared is not null && type is { } t && OptionsOf(prepared, row, change, utc) is { } options)
            lines.AddRange(OptionLines(t, options));
        IReadOnlyList<string> groups = change is not null ? change.DpGroups : row.DpGroup.Length > 0 && type != DeploymentObjectType.SoftwareUpdateGroup ? [row.DpGroup] : [];
        if (groups.Count > 0)
            lines.Add($"Distribution point group: {string.Join(", ", groups)}. The distribution is requested first and has no Undo. " + DeploymentText.CreateNowHint);
        if (change is not null) lines.AddRange(change.Impact);
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
    /// <summary>A check box text names at most this many rings; more are counted, so the box stays a few lines high.</summary>
    public const int MaxNamedRings = 5;

    public const string BackdatedSentence = "the available time of a ring this change creates is in the past, so its deployment is available at once";

    /// <param name="anyBackdated">True when the service reports an available time in the past that <paramref name="backdated"/> does not name; the sentence then names it in general.</param>
    public static string SecondConfirmation(int requiredRings, int devices, RingExpansionRow? single, IReadOnlyList<RingExpansionRow> backdated, string basis, bool anyBackdated = false)
    {
        var parts = new List<string>();
        if (requiredRings > 0)
            parts.Add(requiredRings == 1 && single is not null
                ? $"the Required ring {single.Name} installs software on every device of {single.CollectionName} ({DeploymentText.Count(devices, "device")} now) at the deadline {Time(single.Deadline)} {basis}"
                : $"{requiredRings.ToString(CultureInfo.CurrentCulture)} Required rings enforce on {DeploymentText.Count(devices, "device")} in total at their deadlines");
        if (backdated.Count > 0)
            parts.Add(backdated.Count == 1
                ? $"the available time of ring {backdated[0].Name} is in the past, so its deployment is available at once"
                : backdated.Count <= MaxNamedRings
                    ? $"the available times of rings {string.Join(", ", backdated.Select(r => r.Name))} are in the past, so their deployments are available at once"
                    : $"the available times of {backdated.Count.ToString(CultureInfo.CurrentCulture)} rings are in the past (first ring {backdated[0].Name}), so their deployments are available at once");
        else if (anyBackdated) parts.Add(BackdatedSentence);
        return parts.Count == 0 ? "" : $"I understand that {string.Join(", and that ", parts)}.";
    }

    private static IReadOnlyList<RingExpansionRow> Backdated(IReadOnlyList<RingExpansionRow> rows, RingChange prepared, DateTime now) =>
        rows.Where(r => r.AvailableTime < now.AddMinutes(-1)
            || prepared.RingChanges.Any(c => c.NeedsBackdatedAcknowledgement && string.Equals(c.Collection?.CollectionId, r.CollectionId, StringComparison.OrdinalIgnoreCase))).ToList();

    /// <summary>
    /// True when a ring the change creates now distributes content first: the prepared creates name a DP group.
    /// The ring services leave the DP group out for a software update group, so such a ring never offers the choice.
    /// </summary>
    private static bool Distributes(RingChange prepared, IReadOnlyList<RingExpansionRow> now, DeploymentObjectType? type) =>
        prepared.RingChanges.Count > 0
            ? now.Any(r => ChangeOf(prepared, r) is { DpGroups.Count: > 0 })
            : type != DeploymentObjectType.SoftwareUpdateGroup && now.Any(r => r.DpGroup.Length > 0);

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
        var requiredRings = prepared.RequiredRings;
        var requiredDevices = prepared.RequiredDevices;
        var backdated = Backdated(now, prepared, clockNow);
        var entries = rows.Select(row =>
        {
            var created = now.Contains(row);
            var action = created ? IsRequired(row.Purpose) ? "Create Required deployment" : "Create deployment" : "Hold ring";
            var item = new ChangePlanItem(action, $"Ring {row.Name}: {obj.Name} to {row.CollectionName} ({row.CollectionId})",
                RingLines(row, basis, created, prepared, created ? ChangeOf(prepared, row) : null));
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
            ? SecondConfirmation(requiredRings, requiredDevices, requiredRows.Count == 1 ? requiredRows[0] : null, backdated, basis, prepared.NeedsBackdatedAcknowledgement)
            : null;
        if (string.IsNullOrEmpty(extra)) extra = null;
        var distributes = Distributes(prepared, now, obj.Type);
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
        var change = ChangeOf(prepared, row) ?? prepared.RingChanges.FirstOrDefault();
        lines.Add(RingLines(row, basis, created: true, prepared, change));
        lines.AddRange(prepared.Impact);
        lines.Add(UndoRunNote);
        var required = prepared.NeedsRequiredConfirmation;
        var devices = prepared.RequiredDevices;
        var backdated = Backdated([row], prepared, Now(clock, timeBasedOn));
        if (prepared.ShiftMinutes > 0)
            lines.Insert(1, $"The deadline of this ring passed: this ring and every later held ring move {DeploymentText.Count(prepared.ShiftMinutes, "minute")} later, so each keeps its time from available to deadline.");
        var action = required ? "Create Required deployment" : "Create deployment";
        var item = new ChangePlanItem(action, $"Ring {row.Name}: {obj?.Name} to {row.CollectionName} ({row.CollectionId})", string.Join(Environment.NewLine, lines));
        var extra = required || backdated.Count > 0 || prepared.NeedsBackdatedAcknowledgement
            ? SecondConfirmation(required ? 1 : 0, devices, row, backdated, basis, prepared.NeedsBackdatedAcknowledgement)
            : null;
        if (string.IsNullOrEmpty(extra)) extra = null;
        return (new RingRunPlan("Promote ring", $"Promote ring {row.Name}", required ? "Promote Required ring" : "Promote ring", prepared,
            [new RingPlanEntry(item, RingEntryKind.Promote, row.CollectionId)], extraConfirmation: extra,
            optionalChoice: (change is not null ? change.DpGroups.Count > 0 : Distributes(prepared, [row], obj?.Type)) ? DeploymentText.CreateNowChoice : null)
        {
            NeedsRequiredConfirmation = required, AcknowledgesBackdated = backdated.Count > 0 || prepared.NeedsBackdatedAcknowledgement
        }, null);
    }

    /// <summary>
    /// The destructive plan of Undo run, one line per ring it touches: "Remove deployment" for each
    /// created deployment that passes its checks (with its proof state), "Keep deployment" for each one a
    /// check keeps (with the reason), "Cancel held ring" for each held ring, and "Close run" only when
    /// the undo closes the run: an open run without a kept deployment. A closed run stays closed; a run
    /// with a kept deployment stays open.
    /// </summary>
    public static (RingRunPlan? Plan, string? Problem) BuildUndoRunPlan(RingChange prepared, string runName, RingRun run)
    {
        if (prepared.Kind != RingChangeKind.UndoRun) return (null, "The run could not be prepared. Select Refresh and reconcile, then Undo run again.");
        var blocking = Blocking(prepared, []);
        if (blocking.Count > 0 || prepared.Blocked) return (null, "The run cannot be undone: " + BlockingText(blocking));
        RingRunRing? RingOf(DeploymentChange c) =>
            run.Rings.FirstOrDefault(r => c.Ring is { } ring && r.Index == ring.RingIndex)
            ?? run.Rings.FirstOrDefault(r => r.DeploymentId is { Length: > 0 } id && string.Equals(id, c.Existing?.DeploymentId ?? c.CreatedId, StringComparison.OrdinalIgnoreCase));
        var entries = new List<RingPlanEntry>();
        var handled = new HashSet<int>();
        foreach (var removal in prepared.RingChanges)
        {
            var ring = RingOf(removal);
            if (ring is not null) handled.Add(ring.Index);
            var existing = removal.Site ?? removal.Existing;
            var id = removal.Existing?.DeploymentId ?? removal.CreatedId ?? "";
            var target = (ring is null ? "" : $"Ring {ring.Name}: ") + (existing is null ? id : $"{existing.ObjectName} from {existing.CollectionName} ({existing.CollectionId})");
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
        foreach (var index in prepared.KeptRings.Where(i => !handled.Contains(i)).Order())
        {
            var ring = run.Rings.FirstOrDefault(r => r.Index == index);
            var row = prepared.Rows.FirstOrDefault(r => r.Index == index);
            var checks = row?.Checks ?? [];
            var failed = DeploymentText.Ordered(checks).Where(DeploymentText.Blocks).Select(c => $"{DeploymentText.CheckLabel(c.Name)}: {DeploymentText.CheckMessage(c, run.ObjectType)}");
            var reason = $"{DeploymentWriter.SkippedPrefix}: kept because a check did not pass. {string.Join(" ", failed)}".TrimEnd();
            var name = ring?.Name ?? row?.Name ?? index.ToString(CultureInfo.CurrentCulture);
            var lines = new List<string> { $"Deployment ID: {ring?.DeploymentId}." };
            lines.AddRange(DeploymentText.CheckLines(checks, run.ObjectType));
            lines.Add(reason);
            entries.Add(new RingPlanEntry(new ChangePlanItem("Keep deployment", $"Ring {name}: {ring?.CollectionName ?? row?.CollectionName} ({ring?.CollectionId ?? row?.CollectionId})",
                string.Join(Environment.NewLine, lines)), RingEntryKind.Keep, ring?.DeploymentId ?? "", reason));
        }
        var cancelled = prepared.CancelledRings.Select(i => run.Rings.FirstOrDefault(r => r.Index == i)).OfType<RingRunRing>();
        foreach (var held in cancelled.OrderBy(r => r.Index))
            entries.Add(new RingPlanEntry(new ChangePlanItem("Cancel held ring", $"Ring {held.Name}: {held.CollectionName} ({held.CollectionId})",
                "The run file marks the ring Cancelled. Nothing changes in ConfigMgr."), RingEntryKind.Cancel, held.CollectionId));
        var removals = entries.Count(e => e.Kind == RingEntryKind.Remove);
        var kept = entries.Count(e => e.Kind == RingEntryKind.Keep);
        var closes = !run.IsClosed && kept == 0;
        var finalLines = prepared.Impact.ToList();
        finalLines.Add(closes ? "The run file moves to the closed folder."
            : run.IsClosed ? "The run is closed and stays closed."
            : $"The run stays open because {DeploymentText.Count(kept, "deployment")} is kept.");
        finalLines.Add(ProofLimit);
        if (closes) entries.Add(new RingPlanEntry(new ChangePlanItem("Close run", runName, string.Join(Environment.NewLine, finalLines)), RingEntryKind.Close, prepared.RunId ?? ""));
        if (!entries.Any(e => e.Writes))
            return (null, "Nothing of this run can be undone: " + string.Join(" ", entries.Select(e => e.Reason).OfType<string>()));
        if (!closes)
        {
            var last = entries[^1];
            entries[^1] = last with { Item = last.Item with { Detail = string.Join(Environment.NewLine, new[] { last.Item.Detail ?? "" }.Concat(finalLines)) } };
        }
        var question = removals == 0 ? null
            : DeploymentWriter.RemovalQuestion(prepared.RingChanges.Where(c => DeploymentText.Blocking(c).Count == 0).ToList());
        return (new RingRunPlan("Undo ring run", $"Undo the ring run {runName}", removals > 0 ? $"Remove {DeploymentText.Count(removals, "deployment")}" : "Undo run",
            prepared, entries, isDestructive: true, removalQuestion: question), null);
    }

    /// <summary>The plan of Close run: the run file moves to the closed folder; no ConfigMgr change.</summary>
    public static (RingRunPlan? Plan, string? Problem) BuildClosePlan(RingChange prepared, string runName)
    {
        if (prepared.Kind != RingChangeKind.CloseRun) return (null, "The run could not be prepared. Select Refresh and reconcile, then Close run again.");
        var blocking = Blocking(prepared, []);
        if (blocking.Count > 0 || prepared.Blocked) return (null, "The run cannot be closed: " + BlockingText(blocking));
        var lines = prepared.Impact.Concat(
        [
            "The run file moves to the closed folder. No deployment is changed.",
            "Held rings are not created any more. Created deployments stay in ConfigMgr; Show closed runs lists the run."
        ]);
        return (new RingRunPlan("Close ring run", $"Close the ring run {runName}", "Close run", prepared,
            [new RingPlanEntry(new ChangePlanItem("Close run", runName, string.Join(Environment.NewLine, lines)), RingEntryKind.Close, prepared.RunId ?? "")]), null);
    }

    private static string CollectionText(string name, string id) => name.Length > 0 ? $"{name} ({id})" : id;

    /// <summary>
    /// The plan of Abandon ring: the ring in Creating returns to Held. Its own check box names the risk
    /// that a deployment of the ring exists and stays outside the run; the change runs only with it.
    /// </summary>
    public static (RingRunPlan? Plan, string? Problem) BuildAbandonPlan(RingChange prepared, string runName)
    {
        if (prepared.Kind != RingChangeKind.AbandonRing || prepared.Rows.Count != 1)
            return (null, "The ring could not be prepared. Select Refresh and reconcile, then Abandon ring again.");
        var row = prepared.Rows[0];
        var blocking = Blocking(prepared, []);
        if (blocking.Count > 0 || prepared.Blocked) return (null, $"Ring {row.Name} cannot be abandoned: " + BlockingText(blocking));
        var collection = CollectionText(row.CollectionName, row.CollectionId);
        var lines = prepared.Impact.Concat(
        [
            $"Run: {runName}.",
            "The run file marks the ring Held. Nothing is created or removed in ConfigMgr."
        ]);
        var extra = $"I understand that a deployment of ring {row.Name} may exist in {collection}, that the run does not remove it, and that I check the collection in the console.";
        return (new RingRunPlan("Abandon ring", $"Abandon the create of ring {row.Name}", "Abandon ring", prepared,
            [new RingPlanEntry(new ChangePlanItem("Abandon ring", $"Ring {row.Name}: {collection}", string.Join(Environment.NewLine, lines)), RingEntryKind.Abandon, row.CollectionId)],
            extraConfirmation: extra)
        {
            RequiresExtraConfirmation = true
        }, null);
    }
}
