using System.Text.Json;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

public sealed partial class SiteHygieneService
{
    private static HygieneOutcome Refused(HygieneChange change, string reason) =>
        new(change, false, SkippedPrefix + reason, change.Findings.Select(f => Skip(f, reason)).ToList(), true);

    internal const string UnexpectedStop = "The result is not known: the fix stopped with an unexpected error. Scan again to see the current state.";
    internal const string StoppedBefore = "the run stopped with an unexpected error before this object. Scan again to see the current state.";
    internal const string ReadBackFailed = "The write ran, but the read-back failed, so the result is not known. Scan again to see the current state.";
    internal const string Cancelled = "the run was cancelled before this object.";
    internal const string SessionStopped = "the run stopped after a ConfigMgr session failure. Scan again to see the current state.";
    internal const string WriteSessionFailed = "The result is not known: the ConfigMgr session failed during the write. Scan again to see the current state.";

    /// <summary>The per-object results of one run so far; the fallback of an unexpected error keeps them.</summary>
    private sealed class RunState
    {
        public List<HygieneObjectOutcome> Results { get; } = [];

        /// <summary>The Started record is in the audit log.</summary>
        public bool Started { get; set; }

        /// <summary>The first write may have run.</summary>
        public bool Writing { get; set; }

        public bool Finished { get; set; }

        /// <summary>The targets of the finding in work that have a result.</summary>
        public List<HygieneTargetOutcome> Current { get; } = [];
    }

    public async Task<HygieneOutcome> ApplyAsync(HygieneChange change, IProgress<HygieneOutcome> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(progress);
        HygieneOutcome outcome;
        var state = new RunState();
        try
        {
            outcome = await ApplyCoreAsync(change, progress, state, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.Warning(LogArea, $"{change.Kind} stopped with an unexpected error: {ex.GetType().Name}.");
            var known = state.Results.ToList();
            var rest = change.Findings.Where(f => known.All(k => !string.Equals(k.Finding.Key, f.Key, StringComparison.OrdinalIgnoreCase))).ToList();
            var objects = known.Concat(rest.Select(f => state.Writing
                ? new HygieneObjectOutcome(f, HygieneResults.Failed, UnexpectedStop)
                : Skip(f, "the fix stopped with an unexpected error before the first write. Nothing was changed."))).ToList();
            var missing = state.Started && !state.Finished;
            outcome = new HygieneOutcome(change, false, "The fix stopped with an unexpected error. Scan again to see the current state. The log has the kind of failure."
                + (missing ? AuditMissing() : ""), objects, true) { AuditIncomplete = missing, AuditPath = missing ? _audit.Path : "" };
        }
        progress.Report(outcome);
        return outcome;
    }

    private string AuditMissing() =>
        $" The audit log has no Finished record for this run: the record could not be written to {_audit.Path}. The results shown are the results on the site.";

    private async Task<HygieneOutcome> ApplyCoreAsync(HygieneChange change, IProgress<HygieneOutcome> progress, RunState state, CancellationToken ct)
    {
        if (change.Findings.Count == 0) return Refused(change, "the plan has no finding.");
        if (string.IsNullOrEmpty(change.Fingerprint) || !_prepared.TryRemove(change.Fingerprint, out var prepared) || prepared.Kind != change.Kind)
            return Refused(change, "the plan was not prepared by this session, or it already ran. Prepare the plan again.");
        if (_utcNow() - prepared.PreparedUtc > MaxPlanAge)
            return Refused(change, $"the plan is older than {MaxPlanAge.TotalMinutes:0} minutes. Prepare it again on fresh data.");
        if (change.Findings.Count != prepared.Findings.Count || change.Findings.Any(f => !prepared.Findings.ContainsKey(f.Key)))
            return Refused(change, "the plan holds other findings than the prepared plan. Prepare the plan again.");
        if (!SameConnection(prepared.Connection, Connection()))
        {
            _log?.Warning(LogArea, $"{change.Kind} refused: the site or SMS Provider of the settings changed since the plan.");
            return Refused(change, ConnectionChanged);
        }
        if (change.Blocked) return Refused(change, "a check of the plan failed: " + string.Join(" ", change.Checks.Where(c => c.Result is DeploymentCheckResults.Fail or DeploymentCheckResults.Unknown).Select(c => c.Message)));
        if (prepared.Targets.Count > Cap(change.Kind)) return Refused(change, $"the plan writes more than {N(Cap(change.Kind))} objects.");
        if (!change.ChangeConfirmed) return Refused(change, "the plan was not confirmed. Nothing was changed.");
        if (change.IsDelete && !change.DeleteConfirmed) return Refused(change, "a delete needs the second confirmation. Nothing was deleted.");

        using var audit = _audit is HygieneAuditLog real ? real.Reserve() : new HygieneAuditReservation(_audit);
        if (!audit.Probe.Writable) return Refused(change, "the audit log cannot be written, so nothing was changed. " + audit.Probe.Message);
        var runId = Guid.NewGuid().ToString("N");
        var findings = change.Findings.Select(f => prepared.Findings[f.Key]).ToList();
        var parts = prepared.Targets.Chunk(AuditObjectsPerRecord).ToList();
        var startMessage = $"{Verb(change.Kind)}: {Count(prepared.Targets.Count, "object")} of {Count(findings.Count, "finding")}."
            + (parts.Count > 1 ? $" The objects are listed in this record and {Count(parts.Count - 1, "record")} of phase {HygieneAuditPhases.Objects} with the same run ID." : "");
        var started = audit.Append(Record(prepared, change.Kind, HygieneAuditPhases.Started, "", runId,
            parts[0].Select(t => AuditObject(prepared.Findings[t.FindingKey], t, "")).ToList(), startMessage));
        if (!started.Succeeded) return Refused(change, "the audit log could not be written, so nothing was changed. " + started.Message);
        state.Started = true;
        foreach (var part in parts.Skip(1))
        {
            var listed = audit.Append(Record(prepared, change.Kind, HygieneAuditPhases.Objects, "", runId,
                part.Select(t => AuditObject(prepared.Findings[t.FindingKey], t, "")).ToList(), "The planned objects, continued."));
            if (listed.Succeeded) continue;
            var refused = Refused(change, "the audit log could not be written, so nothing was changed. " + listed.Message);
            var closed = WriteFinished(audit, prepared, change.Kind, runId, "Skipped", refused.Objects.Select(o => AuditObject(o.Finding, null, HygieneResults.Skipped)).ToList(),
                "The list of planned objects could not be written in full, so nothing was changed.");
            state.Finished = true;
            return closed ? refused : refused with { Message = refused.Message + AuditMissing(), AuditIncomplete = true, AuditPath = _audit.Path };
        }

        state.Writing = true;
        var results = state.Results;
        var stopped = "";
        foreach (var f in findings)
        {
            var planned = prepared.Targets.Where(t => string.Equals(t.FindingKey, f.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (stopped.Length == 0 && ct.IsCancellationRequested) stopped = Cancelled;
            if (stopped.Length > 0)
            {
                results.Add(SkipAll(f, planned, stopped));
                continue;
            }
            HygieneObjectOutcome result;
            state.Current.Clear();
            try
            {
                (result, stopped) = await ApplyFindingAsync(change.Kind, f, planned, prepared, state.Current, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log?.Warning(LogArea, $"{change.Kind} stopped at an object with an unexpected error: {ex.GetType().Name}.");
                var known = state.Current.ToList();
                var targets = known.Concat(planned.Skip(known.Count).Select(t => new HygieneTargetOutcome(t, HygieneResults.Failed, UnexpectedStop))).ToList();
                result = known.Count == 0 || targets.Count == 0
                    ? new HygieneObjectOutcome(f, HygieneResults.Failed, UnexpectedStop) { Targets = targets }
                    : Combine(change.Kind, f, targets, "");
                stopped = StoppedBefore;
            }
            results.Add(result);
            progress.Report(new HygieneOutcome(change, false, $"Running: {N(results.Count)} of {N(findings.Count)} done.", results.ToList(), false));
        }

        var done = results.Count(r => r.Result == HygieneResults.Done);
        var partial = results.Count(r => r.Result == HygieneResults.Partial);
        var attempted = results.Count(r => r.Result != HygieneResults.Skipped);
        var resultText = done == results.Count ? "Succeeded" : done + partial > 0 ? "Partial" : attempted > 0 ? "Failed" : "Skipped";
        var finished = WriteFinished(audit, prepared, change.Kind, runId, resultText, results.SelectMany(AuditObjects).ToList(), Summary(results) + ".");
        state.Finished = true;
        _log?.Info(LogArea, $"{change.Kind}: {Summary(results)}.");
        var message = attempted == 0
            ? SkippedPrefix + "nothing was changed. " + Sentence(results.FirstOrDefault()?.Message.Replace(SkippedPrefix, "", StringComparison.Ordinal) ?? "")
            : $"{Sentence(Verb(change.Kind))}: {Summary(results)}.";
        return new HygieneOutcome(change, done == results.Count, finished ? message : message + AuditMissing(), results, true)
        {
            AuditIncomplete = !finished,
            AuditPath = finished ? "" : _audit.Path
        };
    }

    private static HygieneAuditRecord Record(PreparedChange prepared, HygieneFixKind kind, string phase, string result, string runId, IReadOnlyList<HygieneAuditObject> objects, string message) =>
        new(Timestamp: default, User: "", Event: kind.ToString(), Phase: phase, Result: result, RunId: runId, Objects: objects, Message: message)
        {
            Site = prepared.Site,
            Provider = prepared.Provider
        };

    /// <summary>The audit objects of one finding result: one per object of the plan, or the finding when the run named no object.</summary>
    private static IEnumerable<HygieneAuditObject> AuditObjects(HygieneObjectOutcome o) =>
        o.Targets.Count > 0 ? o.Targets.Select(t => AuditObject(o.Finding, t.Target, t.Result)) : [AuditObject(o.Finding, null, o.Result)];

    /// <summary>A finding skipped as a whole: each object of the plan is skipped with the same reason.</summary>
    private static HygieneObjectOutcome SkipAll(HygieneFinding f, IReadOnlyList<HygieneTarget> planned, string reason) =>
        Skip(f, reason) with { Targets = planned.Select(t => new HygieneTargetOutcome(t, HygieneResults.Skipped, SkippedPrefix + reason)).ToList() };

    /// <summary>
    /// Writes the results of a run: the objects beyond one record go first in records of phase Objects, then
    /// exactly one Finished record with the last objects, written also when an earlier record fails. False
    /// when a record could not be written: the audit log then does not hold every result of the run.
    /// </summary>
    private bool WriteFinished(HygieneAuditReservation audit, PreparedChange prepared, HygieneFixKind kind, string runId, string resultText, IReadOnlyList<HygieneAuditObject> objects, string message)
    {
        var parts = objects.Chunk(AuditObjectsPerRecord).ToList();
        if (parts.Count == 0) parts.Add([]);
        var all = true;
        foreach (var part in parts.Take(parts.Count - 1))
        {
            var listed = audit.Append(Record(prepared, kind, HygieneAuditPhases.Objects, resultText, runId, part.ToList(), "The results, continued in the Finished record."));
            if (listed.Succeeded) continue;
            all = false;
            _log?.Warning(LogArea, "A Site Hygiene audit record with results could not be written.");
        }
        var note = parts.Count > 1 ? $" The results are listed in this record and {Count(parts.Count - 1, "record")} of phase {HygieneAuditPhases.Objects} with the same run ID." : "";
        var finished = audit.Append(Record(prepared, kind, HygieneAuditPhases.Finished, resultText, runId, parts[^1].ToList(), message + note));
        if (finished.Succeeded) return all;
        _log?.Warning(LogArea, "The Finished record of a Site Hygiene fix could not be written.");
        return false;
    }

    private static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Summary(IReadOnlyList<HygieneObjectOutcome> results)
    {
        var parts = new[] { HygieneResults.Done, HygieneResults.Partial, HygieneResults.Unchanged, HygieneResults.Failed, HygieneResults.Skipped }
            .Select(r => (Name: r, Count: results.Count(x => x.Result == r)))
            .Where(p => p.Count > 0 || p.Name is HygieneResults.Done or HygieneResults.Failed)
            .Select(p => $"{N(p.Count)} {p.Name.ToLowerInvariant()}");
        return string.Join(", ", parts);
    }

    private static HygieneAuditObject AuditObject(HygieneFinding f, HygieneTarget? t, string result) =>
        new(f.CheckId, t?.ObjectType ?? f.ObjectType, t?.ObjectKey ?? f.ObjectKey, t?.ObjectName ?? f.ObjectName, result);

    /// <summary>
    /// Re-evaluates one finding on fresh data, then writes each object the plan named that still holds, one
    /// write and one read-back each. An object that is new since Prepare is not written. Cancel and a
    /// change of the connection take effect before each write; a write and its read-back always finish,
    /// so the result of an object that was written is known. Returns the reason the run stops after this
    /// finding; empty when it goes on.
    /// </summary>
    private async Task<(HygieneObjectOutcome Result, string Stop)> ApplyFindingAsync(HygieneFixKind kind, HygieneFinding f, IReadOnlyList<HygieneTarget> planned, PreparedChange prepared,
        List<HygieneTargetOutcome> outcomes, CancellationToken ct)
    {
        HygieneSiteRead fresh;
        try
        {
            fresh = await ReadStateAsync(kind, [f], prepared.Connection, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (SkipAll(f, planned, Cancelled), Cancelled);
        }
        catch (OtherConnectionException)
        {
            _log?.Warning(LogArea, $"{kind} stopped: the site or SMS Provider changed since the plan.");
            return (SkipAll(f, planned, ConnectionChanged), ConnectionChanged);
        }
        catch (HygieneServiceException)
        {
            return (SkipAll(f, planned, "the current state could not be read before the write, so nothing was changed."), SessionStopped);
        }
        var now = LocalNow();
        var e = HygieneFixRules.Evaluate(kind, f, fresh, now, prepared.Thresholds);
        if (!e.Holds) return (SkipAll(f, planned, "the finding no longer holds: " + e.Reason), "");
        var plannedKeys = planned.Select(t => t.ObjectKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var writes = e.Writes.Where(w => plannedKeys.Contains(w.Line.ObjectKey)).ToList();
        var extra = e.Writes.Count - writes.Count;
        if (writes.Count == 0) return (SkipAll(f, planned, "the objects of the plan no longer exist or changed since the plan."), "");
        var changedId = writes.Where(w => planned.First(t => string.Equals(t.ObjectKey, w.Line.ObjectKey, StringComparison.OrdinalIgnoreCase)).Detail != w.Line.Detail).ToList();
        if (kind is HygieneFixKind.RemoveApplication && changedId.Count > 0)
            return (SkipAll(f, planned, "the application has a new revision since the plan. Prepare the plan again."), "");

        var ruleBefore = kind == HygieneFixKind.RunAutoDeploymentRule ? fresh.Table(HygieneSets.AutoDeploymentRules).Records.FirstOrDefault().S("LastRunTime") : "";
        var stop = "";
        foreach (var t in planned)
        {
            if (stop.Length > 0)
            {
                outcomes.Add(new HygieneTargetOutcome(t, HygieneResults.Skipped, SkippedPrefix + stop));
                continue;
            }
            var w = writes.FirstOrDefault(x => string.Equals(x.Line.ObjectKey, t.ObjectKey, StringComparison.OrdinalIgnoreCase));
            if (w is null)
            {
                outcomes.Add(new HygieneTargetOutcome(t, HygieneResults.Skipped, SkippedPrefix + "the object no longer exists or changed since the plan."));
                continue;
            }
            if (ct.IsCancellationRequested) stop = Cancelled;
            else if (!SameConnection(prepared.Connection, Connection())) stop = ConnectionChanged;
            if (stop.Length > 0)
            {
                outcomes.Add(new HygieneTargetOutcome(t, HygieneResults.Skipped, SkippedPrefix + stop));
                continue;
            }
            var (target, after) = await WriteAsync(kind, f, w, prepared, ruleBefore).ConfigureAwait(false);
            outcomes.Add(target);
            stop = after;
        }
        var note = extra > 0 ? $" {Count(extra, "object")} that the plan did not name {(extra == 1 ? "was" : "were")} left as {(extra == 1 ? "it is" : "they are")}." : "";
        return (Combine(kind, f, outcomes.ToList(), note), stop);
    }

    /// <summary>
    /// One write and its read-back. The write runs without the cancel token: a cancel ends the session
    /// process, and a write cut in the middle leaves its result unknown. Returns the reason the run stops
    /// after this object; empty when it goes on.
    /// </summary>
    private async Task<(HygieneTargetOutcome Result, string Stop)> WriteAsync(HygieneFixKind kind, HygieneFinding f, HygieneWrite w, PreparedChange prepared, string ruleBefore)
    {
        var t = w.Line;
        HygieneSiteRead applied;
        try
        {
            var parameters = new ConfigMgrParameters { { "Operation", "Apply" }, { "Kind", kind.ToString() } };
            parameters.AddSecret("Target", JsonSerializer.Serialize(w.Target));
            applied = Parse(await _session.InvokeAsync(Script, Bound(parameters, prepared.Connection), CancellationToken.None).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is ConfigMgrSessionException or HygieneServiceException)
        {
            _log?.Warning(LogArea, $"A {kind} write failed in the ConfigMgr session ({(ex as ConfigMgrSessionException)?.Failure.ToString() ?? "parse"}).");
            return (new HygieneTargetOutcome(t, HygieneResults.Failed, WriteSessionFailed), SessionStopped);
        }
        if (applied.OtherConnection)
        {
            _log?.Warning(LogArea, $"{kind} stopped: the ConfigMgr session is on another site or SMS Provider than the plan.");
            return (new HygieneTargetOutcome(t, HygieneResults.Skipped, SkippedPrefix + ConnectionChanged), ConnectionChanged);
        }
        var (status, failure) = applied.Applied.FirstOrDefault();
        if (status == "in-use")
        {
            _log?.Info(LogArea, $"{kind}: a delete was refused in the delete command: the object is in use ({failure}).");
            return (new HygieneTargetOutcome(t, HygieneResults.Skipped, SkippedPrefix + InUseText(failure)), "");
        }
        if (status != "done")
        {
            _log?.Warning(LogArea, $"{kind}: a write failed with kind {(failure.Length > 0 ? failure : "none")}.");
            return (new HygieneTargetOutcome(t, HygieneResults.Failed, FailureText(failure)), "");
        }
        return await ReadBackAsync(kind, f, w, prepared, ruleBefore).ConfigureAwait(false) switch
        {
            ReadBack.Done => (new HygieneTargetOutcome(t, HygieneResults.Done, DoneText(kind, 1)), ""),
            ReadBack.AsBefore => (new HygieneTargetOutcome(t, HygieneResults.Unchanged, AsBeforeText(kind, 1, 1)), ""),
            _ => (new HygieneTargetOutcome(t, HygieneResults.Failed, ReadBackFailed), "")
        };
    }

    /// <summary>The result of a finding from the results of its objects: their common result, or Partial when they differ.</summary>
    private static HygieneObjectOutcome Combine(HygieneFixKind kind, HygieneFinding f, IReadOnlyList<HygieneTargetOutcome> targets, string note)
    {
        if (targets.Count == 0) return new HygieneObjectOutcome(f, HygieneResults.Skipped, SkippedPrefix + "the plan named no object for the finding.");
        var kinds = targets.Select(t => t.Result).Distinct(StringComparer.Ordinal).ToList();
        var n = targets.Count;
        if (kinds.Count == 1)
        {
            var only = kinds[0];
            var message = n == 1 ? targets[0].Message
                : only == HygieneResults.Done ? DoneText(kind, n)
                : only == HygieneResults.Unchanged ? AsBeforeText(kind, n, n)
                : only == HygieneResults.Failed && targets.All(t => t.Message == ReadBackFailed) ? $"{N(n)} of {N(n)} writes ran, but their read-back failed, so their result is not known. Scan again to see the current state."
                : targets[0].Message;
            return new HygieneObjectOutcome(f, only, message + note) { Targets = targets };
        }
        var done = targets.Count(t => t.Result == HygieneResults.Done);
        var first = targets.First(t => t.Result != HygieneResults.Done);
        var counts = string.Join(", ", new[] { HygieneResults.Done, HygieneResults.Unchanged, HygieneResults.Failed, HygieneResults.Skipped }
            .Select(r => (Name: r, Count: targets.Count(t => t.Result == r))).Where(p => p.Count > 0).Select(p => $"{N(p.Count)} {p.Name.ToLowerInvariant()}"));
        return new HygieneObjectOutcome(f, HygieneResults.Partial,
            $"{N(done)} of {N(n)} objects {(kind is HygieneFixKind.SetCollectionRefreshType or HygieneFixKind.SetApplication or HygieneFixKind.SetSiteMaintenanceTask or HygieneFixKind.RunAutoDeploymentRule ? "changed" : "deleted")} ({counts}). "
            + $"First object not done: {first.Target.ObjectName} ({first.Target.ObjectKey}): {first.Message} The result of each object shows which objects remain.{note}")
        { Targets = targets };
    }

    private static string AsBeforeText(HygieneFixKind kind, int unchanged, int writes) => kind == HygieneFixKind.RunAutoDeploymentRule
        ? $"The site accepted the run; the rule showed no new run time within {RuleRunWindow.TotalSeconds:0} seconds."
        : $"The read-back shows {(writes == 1 ? "the object" : $"{N(unchanged)} of {N(writes)} objects")} as before.";

    internal static string InUseText(string use) => "the delete command found that the object is in use now" + use switch
    {
        "deployments" => ": a deployment targets it or uses it",
        "collections" => ": another collection includes, excludes or limits to it",
        "collection-settings" => ": it has collection variables or maintenance windows",
        "rules" => ": an automatic deployment rule targets it",
        "client-settings" => ": a client settings deployment targets it",
        "admins" => ": an administrative user has it in the assigned collections",
        "dependents" => ": another application depends on it or supersedes it",
        "supersedence" => ": it is part of a supersedence relationship",
        "task-sequences" => ": a task sequence references it",
        "auto-apply-drivers" => ": a task sequence has an Auto Apply Drivers step",
        "enabled" => ": the rule is enabled",
        _ => ""
    } + ". It was not deleted.";

    private enum ReadBack { Done, AsBefore, NotVerified }

    /// <summary>The read-back after a write; it runs without the cancel token, so the result of a write that ran is known.</summary>
    private async Task<ReadBack> ReadBackAsync(HygieneFixKind kind, HygieneFinding f, HygieneWrite w, PreparedChange prepared, string ruleBefore)
    {
        var deadline = _utcNow() + (kind == HygieneFixKind.RunAutoDeploymentRule ? RuleRunWindow : TimeSpan.Zero);
        while (true)
        {
            try
            {
                var after = await ReadStateAsync(kind, [f], prepared.Connection, CancellationToken.None).ConfigureAwait(false);
                if (HygieneFixRules.ReadBackDone(kind, w, after, ruleBefore)) return ReadBack.Done;
            }
            catch (Exception ex) when (ex is HygieneServiceException or OtherConnectionException)
            {
                _log?.Warning(LogArea, "A read-back after a fix could not be made.");
                return ReadBack.NotVerified;
            }
            if (_utcNow() >= deadline) return ReadBack.AsBefore;
            await _delay(RuleRunPoll, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static string DoneText(HygieneFixKind kind, int writes) => kind switch
    {
        HygieneFixKind.SetCollectionRefreshType => "The collection shows the new refresh type.",
        HygieneFixKind.SetApplication => "The application has the task sequence install setting in a new revision.",
        HygieneFixKind.SetSiteMaintenanceTask => "The task is enabled.",
        HygieneFixKind.RunAutoDeploymentRule => "The rule ran: it shows a new last run time.",
        _ => writes == 1 ? "Deleted. The site no longer lists the object." : $"Deleted {N(writes)} objects. The site no longer lists them."
    };

    internal static string FailureText(string failure) => failure switch
    {
        "access" => "ConfigMgr refused the change: the account has no right to change this object.",
        "not-found" => "ConfigMgr did not find the object.",
        "referenced" => "ConfigMgr refused the delete: another object references this object.",
        "invalid" => "The object identifier is not valid.",
        "unsupported" => "This kind of fix is not supported.",
        _ => "ConfigMgr reported an error. The log has the kind of failure."
    };
}
