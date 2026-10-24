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

    /// <summary>The per-object results of one run so far; the fallback of an unexpected error keeps them.</summary>
    private sealed class RunState
    {
        public List<HygieneObjectOutcome> Results { get; } = [];
        public bool Started { get; set; }
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
            var objects = known.Concat(rest.Select(f => state.Started
                ? new HygieneObjectOutcome(f, HygieneResults.Failed, UnexpectedStop)
                : Skip(f, "the fix stopped with an unexpected error before the first write. Nothing was changed."))).ToList();
            outcome = new HygieneOutcome(change, false, "The fix stopped with an unexpected error. Scan again to see the current state. The log has the kind of failure.", objects, true);
        }
        progress.Report(outcome);
        return outcome;
    }

    private async Task<HygieneOutcome> ApplyCoreAsync(HygieneChange change, IProgress<HygieneOutcome> progress, RunState state, CancellationToken ct)
    {
        if (change.Findings.Count == 0) return Refused(change, "the plan has no finding.");
        if (string.IsNullOrEmpty(change.Fingerprint) || !_prepared.TryRemove(change.Fingerprint, out var prepared) || prepared.Kind != change.Kind)
            return Refused(change, "the plan was not prepared by this session, or it already ran. Prepare the plan again.");
        if (_utcNow() - prepared.PreparedUtc > MaxPlanAge)
            return Refused(change, $"the plan is older than {MaxPlanAge.TotalMinutes:0} minutes. Prepare it again on fresh data.");
        if (change.Findings.Count != prepared.Findings.Count || change.Findings.Any(f => !prepared.Findings.ContainsKey(f.Key)))
            return Refused(change, "the plan holds other findings than the prepared plan. Prepare the plan again.");
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
        var started = audit.Append(new HygieneAuditRecord(default, "", change.Kind.ToString(), HygieneAuditPhases.Started, "", runId,
            parts[0].Select(t => AuditObject(prepared.Findings[t.FindingKey], t, "")).ToList(), startMessage) { Site = "" });
        if (!started.Succeeded) return Refused(change, "the audit log could not be written, so nothing was changed. " + started.Message);
        foreach (var part in parts.Skip(1))
        {
            var listed = audit.Append(new HygieneAuditRecord(default, "", change.Kind.ToString(), HygieneAuditPhases.Objects, "", runId,
                part.Select(t => AuditObject(prepared.Findings[t.FindingKey], t, "")).ToList(), "The planned objects, continued."));
            if (listed.Succeeded) continue;
            var refused = Refused(change, "the audit log could not be written, so nothing was changed. " + listed.Message);
            WriteFinished(audit, change.Kind, runId, "Skipped", refused.Objects.Select(o => AuditObject(o.Finding, null, HygieneResults.Skipped)).ToList(),
                "The list of planned objects could not be written in full, so nothing was changed.");
            return refused;
        }
        state.Started = true;

        var results = state.Results;
        var auditObjects = new List<HygieneAuditObject>();
        var stopped = "";
        foreach (var f in findings)
        {
            if (stopped.Length > 0 || ct.IsCancellationRequested)
            {
                var why = stopped.Length > 0 ? stopped : "the run was cancelled before this object.";
                results.Add(Skip(f, why));
                auditObjects.Add(AuditObject(f, null, HygieneResults.Skipped));
                continue;
            }
            var planned = prepared.Targets.Where(t => string.Equals(t.FindingKey, f.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            HygieneObjectOutcome result;
            try
            {
                (result, var sessionFailed) = await ApplyFindingAsync(change.Kind, f, planned, prepared.Thresholds).ConfigureAwait(false);
                if (sessionFailed) stopped = "the run stopped after a ConfigMgr session failure. Scan again to see the current state.";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log?.Warning(LogArea, $"{change.Kind} stopped at an object with an unexpected error: {ex.GetType().Name}.");
                result = new HygieneObjectOutcome(f, HygieneResults.Failed, UnexpectedStop);
                stopped = StoppedBefore;
            }
            results.Add(result);
            auditObjects.Add(AuditObject(f, null, result.Result));
            progress.Report(new HygieneOutcome(change, false, $"Running: {N(results.Count)} of {N(findings.Count)} done.", results.ToList(), false));
        }

        var done = results.Count(r => r.Result == HygieneResults.Done);
        var attempted = results.Count(r => r.Result != HygieneResults.Skipped);
        var resultText = done == results.Count ? "Succeeded" : done > 0 ? "Partial" : attempted > 0 ? "Failed" : "Skipped";
        WriteFinished(audit, change.Kind, runId, resultText, auditObjects, Summary(results) + ".");
        _log?.Info(LogArea, $"{change.Kind}: {Summary(results)}.");
        var message = attempted == 0
            ? SkippedPrefix + "nothing was changed. " + Sentence(results.FirstOrDefault()?.Message.Replace(SkippedPrefix, "", StringComparison.Ordinal) ?? "")
            : $"{Sentence(Verb(change.Kind))}: {Summary(results)}.";
        return new HygieneOutcome(change, done == results.Count, message, results, true);
    }

    /// <summary>
    /// Writes the results of a run: the objects beyond one record go first in records of phase Objects, then
    /// exactly one Finished record with the last objects, written also when an earlier record fails.
    /// </summary>
    private void WriteFinished(HygieneAuditReservation audit, HygieneFixKind kind, string runId, string resultText, IReadOnlyList<HygieneAuditObject> objects, string message)
    {
        var parts = objects.Chunk(AuditObjectsPerRecord).ToList();
        if (parts.Count == 0) parts.Add([]);
        foreach (var part in parts.Take(parts.Count - 1))
        {
            var listed = audit.Append(new HygieneAuditRecord(default, "", kind.ToString(), HygieneAuditPhases.Objects, resultText, runId, part.ToList(), "The results, continued in the Finished record."));
            if (!listed.Succeeded) _log?.Warning(LogArea, "A Site Hygiene audit record with results could not be written.");
        }
        var note = parts.Count > 1 ? $" The results are listed in this record and {Count(parts.Count - 1, "record")} of phase {HygieneAuditPhases.Objects} with the same run ID." : "";
        var finished = audit.Append(new HygieneAuditRecord(default, "", kind.ToString(), HygieneAuditPhases.Finished, resultText, runId, parts[^1].ToList(), message + note));
        if (!finished.Succeeded) _log?.Warning(LogArea, "The Finished record of a Site Hygiene fix could not be written.");
    }

    private static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Summary(IReadOnlyList<HygieneObjectOutcome> results)
    {
        var parts = new[] { HygieneResults.Done, HygieneResults.Unchanged, HygieneResults.Failed, HygieneResults.Skipped }
            .Select(r => (Name: r, Count: results.Count(x => x.Result == r)))
            .Where(p => p.Count > 0 || p.Name is HygieneResults.Done or HygieneResults.Failed)
            .Select(p => $"{N(p.Count)} {p.Name.ToLowerInvariant()}");
        return string.Join(", ", parts);
    }

    private static HygieneAuditObject AuditObject(HygieneFinding f, HygieneTarget? t, string result) =>
        new(f.CheckId, t?.ObjectType ?? f.ObjectType, t?.ObjectKey ?? f.ObjectKey, t?.ObjectName ?? f.ObjectName, result);

    /// <summary>
    /// Re-evaluates one finding on fresh data, then writes each object the plan named that still holds, one
    /// write and one read-back each. An object that is new since Prepare is not written.
    /// </summary>
    private async Task<(HygieneObjectOutcome Result, bool SessionFailed)> ApplyFindingAsync(HygieneFixKind kind, HygieneFinding f, IReadOnlyList<HygieneTarget> planned, HygieneThresholds thresholds)
    {
        HygieneSiteRead fresh;
        try
        {
            fresh = await ReadStateAsync(kind, [f], CancellationToken.None).ConfigureAwait(false);
        }
        catch (HygieneServiceException)
        {
            return (new HygieneObjectOutcome(f, HygieneResults.Skipped, SkippedPrefix + "the current state could not be read before the write, so nothing was changed."), true);
        }
        var now = LocalNow();
        var e = HygieneFixRules.Evaluate(kind, f, fresh, now, thresholds);
        if (!e.Holds) return (Skip(f, "the finding no longer holds: " + e.Reason), false);
        var plannedKeys = planned.Select(t => t.ObjectKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var writes = e.Writes.Where(w => plannedKeys.Contains(w.Line.ObjectKey)).ToList();
        var extra = e.Writes.Count - writes.Count;
        if (writes.Count == 0) return (Skip(f, "the objects of the plan no longer exist or changed since the plan."), false);
        var changedId = writes.Where(w => planned.First(t => string.Equals(t.ObjectKey, w.Line.ObjectKey, StringComparison.OrdinalIgnoreCase)).Detail != w.Line.Detail).ToList();
        if (kind is HygieneFixKind.RemoveApplication && changedId.Count > 0)
            return (Skip(f, "the application has a new revision since the plan. Prepare the plan again."), false);

        var ruleBefore = kind == HygieneFixKind.RunAutoDeploymentRule ? fresh.Table(HygieneSets.AutoDeploymentRules).Records.FirstOrDefault().S("LastRunTime") : "";
        var done = 0;
        var failures = new List<string>();
        var unchanged = 0;
        var unverified = 0;
        foreach (var w in writes)
        {
            HygieneSiteRead applied;
            try
            {
                var parameters = new ConfigMgrParameters { { "Operation", "Apply" }, { "Kind", kind.ToString() } };
                parameters.AddSecret("Target", JsonSerializer.Serialize(w.Target));
                applied = Parse(await _session.InvokeAsync(Script, parameters, CancellationToken.None).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is ConfigMgrSessionException or HygieneServiceException)
            {
                _log?.Warning(LogArea, $"A {kind} write failed in the ConfigMgr session ({(ex as ConfigMgrSessionException)?.Failure.ToString() ?? "parse"}).");
                return (new HygieneObjectOutcome(f, HygieneResults.Failed, "The result is not known: the ConfigMgr session failed during the write. Scan again to see the current state."), true);
            }
            var (status, failure) = applied.Applied.FirstOrDefault();
            if (status != "done")
            {
                failures.Add(FailureText(failure));
                _log?.Warning(LogArea, $"{kind}: a write failed with kind {(failure.Length > 0 ? failure : "none")}.");
                continue;
            }
            switch (await ReadBackAsync(kind, f, w, ruleBefore).ConfigureAwait(false))
            {
                case ReadBack.Done: done++; break;
                case ReadBack.AsBefore: unchanged++; break;
                default: unverified++; break;
            }
        }
        var note = extra > 0 ? $" {Count(extra, "object")} that the plan did not name {(extra == 1 ? "was" : "were")} left as {(extra == 1 ? "it is" : "they are")}." : "";
        if (failures.Count > 0 && done == 0 && unchanged == 0 && unverified == 0) return (new HygieneObjectOutcome(f, HygieneResults.Failed, failures[0] + note), false);
        if (failures.Count > 0) return (new HygieneObjectOutcome(f, HygieneResults.Failed, $"{N(done)} of {N(writes.Count)} written; {failures[0]}{note}"), false);
        if (unverified > 0)
            return (new HygieneObjectOutcome(f, HygieneResults.Failed, (writes.Count == 1 ? ReadBackFailed : $"{N(unverified)} of {N(writes.Count)} writes ran, but their read-back failed, so their result is not known. Scan again to see the current state.") + note), false);
        if (unchanged > 0)
            return (new HygieneObjectOutcome(f, HygieneResults.Unchanged, kind == HygieneFixKind.RunAutoDeploymentRule
                ? $"The site accepted the run; the rule showed no new run time within {RuleRunWindow.TotalSeconds:0} seconds.{note}"
                : $"The read-back shows {(writes.Count == 1 ? "the object" : $"{N(unchanged)} of {N(writes.Count)} objects")} as before.{note}"), false);
        return (new HygieneObjectOutcome(f, HygieneResults.Done, DoneText(kind, writes.Count) + note), false);
    }

    private enum ReadBack { Done, AsBefore, NotVerified }

    private async Task<ReadBack> ReadBackAsync(HygieneFixKind kind, HygieneFinding f, HygieneWrite w, string ruleBefore)
    {
        var deadline = _utcNow() + (kind == HygieneFixKind.RunAutoDeploymentRule ? RuleRunWindow : TimeSpan.Zero);
        while (true)
        {
            try
            {
                var after = await ReadStateAsync(kind, [f], CancellationToken.None).ConfigureAwait(false);
                if (HygieneFixRules.ReadBackDone(kind, w, after, ruleBefore)) return ReadBack.Done;
            }
            catch (HygieneServiceException)
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
