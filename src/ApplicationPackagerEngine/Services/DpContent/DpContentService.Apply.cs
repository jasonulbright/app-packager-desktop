using ApplicationPackagerEngine.Services.Logging;
using System.Globalization;
using System.Text.Json;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Services;

public sealed partial class DpContentService
{
    public async Task<IReadOnlyList<DpContentOutcome>> ApplyAsync(IReadOnlyList<DpContentChange> changes, IProgress<DpContentOutcome> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(progress);
        var outcomes = new List<DpContentOutcome>(changes.Count);
        foreach (var change in changes)
        {
            DpContentOutcome outcome;
            if (change is null) continue;
            if (ct.IsCancellationRequested)
                outcome = Refused(change, "the run was cancelled before this change.");
            else
            {
                try
                {
                    outcome = await ApplyOneAsync(change, progress, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log?.Warning(LogArea, $"{change.Kind} stopped with an unexpected error: {ex.GetType().Name}.");
                    outcome = new DpContentOutcome(change, false, "The change stopped with an unexpected error. Refresh the list to see the current state. The log has the kind of failure.",
                        change.Pairs.Select(p => new DpContentPairOutcome(p, DpContentResults.Failed, "The result is not known: the change stopped with an unexpected error.", null)).ToList(), null);
                }
            }
            outcomes.Add(outcome);
            progress.Report(outcome);
        }
        return outcomes;
    }

    private static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static DpContentOutcome Refused(DpContentChange change, string reason) =>
        new(change, false, SkippedPrefix + reason, change.Pairs.Select(p => new DpContentPairOutcome(p, DpContentResults.Skipped, SkippedPrefix + reason, null)).ToList(), null);

    /// <summary>The first result of a pair after its chunk ran, before the read-back.</summary>
    private sealed record Attempt(PairRow Pair, string Status, string Failure, int StateNow, string BeforeRefresh, int BeforeMessage, string BeforeMessageTime)
    {
        public bool Written => Status is "requested" or "unknown";
    }

    private async Task<DpContentOutcome> ApplyOneAsync(DpContentChange change, IProgress<DpContentOutcome> progress, CancellationToken ct)
    {
        if (change.Pairs.Count == 0) return Refused(change, "the plan has no pair.");
        if (change.Pairs.Count > MaxPairsPerRun) return Refused(change, $"the plan has {Count(change.Pairs.Count, "pair")}; a run takes at most {MaxPairsPerRun.ToString(CultureInfo.InvariantCulture)}.");
        if (string.IsNullOrEmpty(change.Fingerprint) || !_prepared.TryGetValue(change.Fingerprint, out var prepared) || prepared.Kind != change.Kind)
            return Refused(change, "the plan was not prepared by this session. Prepare the plan again.");
        foreach (var pair in change.Pairs)
            if (!prepared.Pairs.TryGetValue(pair.Key, out var planned) || planned.State != pair.State)
                return Refused(change, "the plan holds a pair that the prepared plan does not hold. Prepare the plan again.");
        if (change.Pairs.Select(p => p.Key).Distinct().Count() != change.Pairs.Count) return Refused(change, "the plan holds a pair twice. Prepare the plan again.");
        if (change.Blocked) return Refused(change, "a check of the plan failed: " + string.Join(" ", change.Checks.Where(c => c.Result is DeploymentCheckResults.Fail or DeploymentCheckResults.Unknown).Select(c => c.Message)));
        if (change.Kind == DpContentChangeKind.Remove && !change.RemovalConfirmed)
            return Refused(change, "a removal needs the second confirmation. Nothing was removed.");

        using var audit = _audit is DpContentAuditLog real ? real.Reserve() : new DpContentAuditReservation(_audit);
        if (change.Kind == DpContentChangeKind.Remove)
        {
            if (!audit.Probe.Writable) return Refused(change, "the audit log cannot be written, so nothing was removed. " + audit.Probe.Message);
            var lastCopy = await LastCopiesAsync(change.Pairs, ct).ConfigureAwait(false);
            if (lastCopy.Count > 0 && !change.LastCopyAcknowledged)
                return Refused(change, $"{Count(lastCopy.Count, "content")} would have no distribution point with the content installed ({string.Join(", ", lastCopy.Take(5))}). Select the box that accepts this. Nothing was removed.");
        }

        var runId = Guid.NewGuid().ToString("N");
        var chunks = change.Pairs.Chunk(ChunkSize).ToList();
        var attempts = new List<Attempt>(change.Pairs.Count);
        var stopped = "";
        for (var c = 0; c < chunks.Count; c++)
        {
            var chunk = chunks[c];
            if (stopped.Length > 0 || ct.IsCancellationRequested)
            {
                var why = stopped.Length > 0 ? stopped : "the run was cancelled before this part.";
                attempts.AddRange(chunk.Select(p => new Attempt(p, "skipped", why, -1, "", -1, "")));
                continue;
            }
            if (change.Kind == DpContentChangeKind.Remove)
            {
                var started = audit.Append(Record(change.Kind, "Started", "", runId, c + 1, chunk.Select(p => AuditPair(p, "")).ToList(),
                    $"Removing {Count(chunk.Length, "pair")} (part {c + 1} of {chunks.Count})."));
                if (!started.Succeeded)
                {
                    stopped = "the audit log could not be written, so nothing more was removed. " + started.Message;
                    attempts.AddRange(chunk.Select(p => new Attempt(p, "skipped", stopped, -1, "", -1, "")));
                    continue;
                }
            }
            var done = await RunChunkAsync(change.Kind, chunk).ConfigureAwait(false);
            attempts.AddRange(done.Attempts);
            if (done.SessionFailed) stopped = "the run stopped after a ConfigMgr session failure. Refresh the list to see the current state.";
            if (change.Kind == DpContentChangeKind.Remove)
            {
                var finished = audit.Append(Record(change.Kind, "Finished", ChunkResult(done.Attempts), runId, c + 1,
                    done.Attempts.Select(a => AuditPair(a.Pair, FirstResult(a))).ToList(), $"Part {c + 1} of {chunks.Count}: {Summary(done.Attempts)}."));
                if (!finished.Succeeded) _log?.Warning(LogArea, "A removal audit record after a part could not be written.");
            }
            progress.Report(new DpContentOutcome(change, false, $"Running: part {c + 1} of {chunks.Count} done.", attempts.Select(Interim).ToList(), null) { Final = false });
            _log?.Info(LogArea, $"{change.Kind} part {c + 1} of {chunks.Count}: {done.Attempts.Count(a => a.Written)} requested, {done.Attempts.Count(a => a.Status == "failed")} failed, {done.Attempts.Count(a => a.Status is "stale" or "missing")} skipped.");
        }

        var results = await ReadBackAsync(change.Kind, attempts, ct).ConfigureAwait(false);
        if (change.Kind != DpContentChangeKind.Remove)
        {
            foreach (var (part, index) in (attempts.Any(a => a.Written) ? results : []).Chunk(ChunkSize).Select((p, i) => (p, i)))
            {
                var after = audit.Append(Record(change.Kind, "Finished", OutcomeResult(part), runId, index + 1, part.Select(r => AuditPair(r.Pair, r.Result)).ToList(), Summary(part) + "."));
                if (!after.Succeeded) _log?.Warning(LogArea, "An audit record after a change could not be written.");
            }
        }
        else
        {
            foreach (var (part, index) in results.Chunk(ChunkSize).Select((p, i) => (p, i)))
                if (part.Any(r => r.Result != DpContentResults.Skipped))
                {
                    var after = audit.Append(Record(change.Kind, "ReadBack", OutcomeResult(part), runId, index + 1, part.Select(r => AuditPair(r.Pair, r.Result)).ToList(), Summary(part) + "."));
                    if (!after.Succeeded) _log?.Warning(LogArea, "A removal audit record after the read-back could not be written.");
                }
        }

        var attempted = results.Count(r => r.Result != DpContentResults.Skipped);
        var succeeded = attempted > 0 && results.All(r => r.Result is DpContentResults.Confirmed or DpContentResults.Requested or DpContentResults.Skipped);
        var message = attempted == 0
            ? SkippedPrefix + "no pair was changed. " + Sentence(results.FirstOrDefault()?.Message.Replace(SkippedPrefix, "", StringComparison.Ordinal) ?? "")
            : $"{Verb(change.Kind)}: {Summary(results)}.";
        DpContentChange? undo = null;
        DpContentChange? retry = null;
        if (change.Kind == DpContentChangeKind.Remove)
        {
            var restore = results.Where(r => r.Result is DpContentResults.Confirmed or DpContentResults.Requested && DpContentStates.IsOk(r.Pair.State) && !r.Pair.HasErrorMessage)
                .Select(r => r.Pair).ToList();
            if (restore.Count > 0) undo = new DpContentChange(DpContentChangeKind.DistributeAgain, restore, false, false, [], [], null);
        }
        if (change.Kind == DpContentChangeKind.DistributeAgain)
        {
            var again = results.Where(r => r.Result is DpContentResults.Unchanged or DpContentResults.Failed).Select(r => r.Pair).ToList();
            if (again.Count > 0) retry = new DpContentChange(DpContentChangeKind.DistributeAgain, again, false, false, [], [], null);
        }
        if (attempted > 0) _prepared.TryRemove(change.Fingerprint!, out _);
        _log?.Info(LogArea, $"{change.Kind}: {results.Count(r => r.Result == DpContentResults.Confirmed)} confirmed, {results.Count(r => r.Result == DpContentResults.Requested)} requested, {results.Count(r => r.Result == DpContentResults.Unchanged)} unchanged, {results.Count(r => r.Result == DpContentResults.Failed)} failed, {results.Count(r => r.Result == DpContentResults.Skipped)} skipped.");
        return new DpContentOutcome(change, succeeded, message, results, undo) { RetryChange = retry };
    }

    /// <summary>The contents the removal would leave with no distribution point that has them installed, read from the site now.</summary>
    private async Task<List<string>> LastCopiesAsync(IReadOnlyList<PairRow> pairs, CancellationToken ct)
    {
        var read = await ReadStateAsync(pairs.Select(p => p.PackageId), false, ct).ConfigureAwait(false);
        var removed = pairs.Select(p => p.Key).ToHashSet();
        var held = DpContentAggregator.Pairs(read).Where(r => !removed.Contains(r.Key) && DpContentStates.IsOk(r.State) && !r.HasErrorMessage)
            .Select(r => r.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return pairs.GroupBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
            .Where(g => !held.Contains(g.Key))
            .Select(g => $"{g.First().ContentName} ({g.Key.ToUpperInvariant()})")
            .ToList();
    }

    private sealed record ChunkRun(IReadOnlyList<Attempt> Attempts, bool SessionFailed);

    private async Task<ChunkRun> RunChunkAsync(DpContentChangeKind kind, IReadOnlyList<PairRow> chunk)
    {
        var payload = JsonSerializer.Serialize(chunk.Select(p => new
        {
            P = p.PackageId.ToUpperInvariant(),
            N = p.NalPath,
            S = kind == DpContentChangeKind.DistributeAgain ? -1 : p.State,
            T = p.ContentType
        }));
        var parameters = new ConfigMgrParameters { { "Operation", "Apply" }, { "Kind", kind.ToString() } };
        parameters.AddSecret("Pairs", payload);
        DpContentSiteRead read;
        try
        {
            read = Parse(await _session.InvokeAsync(Script, parameters, CancellationToken.None).ConfigureAwait(false));
        }
        catch (ConfigMgrSessionException ex)
        {
            _log?.Warning(LogArea, $"A {kind} part failed in the ConfigMgr session: {ex.Failure}.");
            return new ChunkRun(chunk.Select(p => new Attempt(p, "unknown", "session", -1, "", -1, "")).ToList(), true);
        }
        catch (DpContentServiceException)
        {
            return new ChunkRun(chunk.Select(p => new Attempt(p, "unknown", "session", -1, "", -1, "")).ToList(), true);
        }
        var byKey = read.AppliedRows.GroupBy(a => new DpPairKey(a.PackageId, a.Nal)).ToDictionary(g => g.Key, g => g.First());
        var attempts = new List<Attempt>(chunk.Count);
        foreach (var pair in chunk)
            attempts.Add(byKey.TryGetValue(pair.Key, out var a)
                ? new Attempt(pair, a.Status, a.Failure, a.StateNow, a.BeforeRefresh, a.BeforeMessage, a.BeforeMessageTime)
                : new Attempt(pair, "unknown", "no-row", -1, "", -1, ""));
        foreach (var failure in attempts.Where(a => a.Status == "failed").GroupBy(a => a.Failure))
            _log?.Warning(LogArea, $"{kind}: {failure.Count()} pairs failed with kind {failure.Key}.");
        return new ChunkRun(attempts, false);
    }

    private static string FirstResult(Attempt a) => a.Status switch
    {
        "requested" => DpContentResults.Requested,
        "failed" => DpContentResults.Failed,
        "unknown" => "Unknown",
        _ => DpContentResults.Skipped
    };

    private static DpContentPairOutcome Interim(Attempt a) => a.Status switch
    {
        "requested" => new DpContentPairOutcome(a.Pair, DpContentResults.Requested, "The site accepted the request. The read-back follows.", null),
        "unknown" => new DpContentPairOutcome(a.Pair, DpContentResults.Requested, "The request may have reached the site. The read-back follows.", null),
        "failed" => new DpContentPairOutcome(a.Pair, DpContentResults.Failed, FailureText(a.Failure), a.StateNow < 0 ? null : a.StateNow),
        _ => new DpContentPairOutcome(a.Pair, DpContentResults.Skipped, SkippedText(a), a.StateNow < 0 ? null : a.StateNow)
    };

    private static string SkippedText(Attempt a) => a.Status switch
    {
        "stale" when a.StateNow < 0 => SkippedPrefix + "the site no longer lists the pair. Nothing was changed.",
        "stale" => SkippedPrefix + $"the pair changed before the write: it is {DpContentStates.Name(a.StateNow)} now. Nothing was changed.",
        "missing" => SkippedPrefix + "the site no longer holds the pair on the distribution point. Nothing was changed.",
        _ => a.Failure.StartsWith(SkippedPrefix, StringComparison.Ordinal) ? a.Failure : SkippedPrefix + a.Failure
    };

    internal static string FailureText(string failure) => failure switch
    {
        "access" => "ConfigMgr refused the change: the account has no right to change this content on the distribution point.",
        "not-found" => "ConfigMgr did not find the content or the distribution point.",
        "returned" => "ConfigMgr refused the validation request.",
        "no-dp" => "The distribution point is not in the site's list of distribution points.",
        "unsupported" => "This content type cannot be distributed from this page.",
        "invalid" => "The pair is not valid.",
        _ => "ConfigMgr reported an error. The log has the kind of failure."
    };

    private static DpContentAuditPair AuditPair(PairRow p, string result) =>
        new(p.PackageId.ToUpperInvariant(), p.ContentName, p.DpName, p.NalPath, p.State < 0 ? null : p.State, result);

    private static DpContentAuditRecord Record(DpContentChangeKind kind, string phase, string result, string runId, int chunk, IReadOnlyList<DpContentAuditPair> pairs, string message) =>
        new(default, "", kind.ToString(), phase, result, runId, chunk, pairs, message);

    private static string ChunkResult(IReadOnlyList<Attempt> attempts) =>
        attempts.All(a => a.Written) ? "Requested" : attempts.Any(a => a.Written) ? "Partial" : attempts.Any(a => a.Status == "failed") ? "Failed" : "Skipped";

    private static string OutcomeResult(IReadOnlyList<DpContentPairOutcome> results) =>
        results.All(r => r.Result is DpContentResults.Confirmed or DpContentResults.Requested) ? "Succeeded"
        : results.Any(r => r.Result is DpContentResults.Confirmed or DpContentResults.Requested) ? "Partial"
        : results.Any(r => r.Result is DpContentResults.Failed or DpContentResults.Unchanged) ? "Failed" : "Skipped";

    private static string Summary(IReadOnlyList<Attempt> attempts) =>
        $"{attempts.Count(a => a.Written)} requested, {attempts.Count(a => a.Status == "failed")} failed, {attempts.Count(a => !a.Written && a.Status != "failed")} skipped";

    private static string Summary(IReadOnlyList<DpContentPairOutcome> results)
    {
        var parts = new List<string>();
        foreach (var name in new[] { DpContentResults.Confirmed, DpContentResults.Requested, DpContentResults.Unchanged, DpContentResults.Failed, DpContentResults.Skipped })
        {
            var n = results.Count(r => r.Result == name);
            if (n > 0 || name is DpContentResults.Confirmed or DpContentResults.Failed) parts.Add($"{n.ToString(CultureInfo.InvariantCulture)} {name.ToLowerInvariant()}");
        }
        return string.Join(", ", parts);
    }

    // ---- Read-back ----------------------------------------------------------------------------

    /// <summary>
    /// Reads the written pairs again until each shows a final result or the window ends. Each poll
    /// is one script run for all pairs still open. Cancellation ends the polls; open pairs keep
    /// the result of the last read.
    /// </summary>
    private async Task<List<DpContentPairOutcome>> ReadBackAsync(DpContentChangeKind kind, IReadOnlyList<Attempt> attempts, CancellationToken ct)
    {
        var results = attempts.Select(Interim).ToList();
        var open = attempts.Select((a, i) => (a, i)).Where(x => x.a.Written).ToList();
        if (open.Count == 0) return results;
        var deadline = _utcNow() + ReadBackWindow;
        var first = true;
        while (open.Count > 0)
        {
            if (!first)
            {
                if (_utcNow() >= deadline || ct.IsCancellationRequested) break;
                try
                {
                    await _delay(PollInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            first = false;
            DpContentSiteRead read;
            try
            {
                read = await ReadStateAsync(open.Select(x => x.a.Pair.PackageId), false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (DpContentServiceException)
            {
                _log?.Warning(LogArea, "A read-back after a change could not be made.");
                break;
            }
            var summary = read.Summaries.GroupBy(s => new DpPairKey(s.PackageId, s.Nal)).ToDictionary(g => g.Key, g => g.First());
            var instances = read.Instances.GroupBy(s => new DpPairKey(s.PackageId, s.Nal)).ToDictionary(g => g.Key, g => g.First());
            var messages = read.Messages.GroupBy(s => new DpPairKey(s.PackageId, s.Nal)).ToDictionary(g => g.Key, g => g.First());
            var still = new List<(Attempt a, int i)>();
            foreach (var (a, i) in open)
            {
                summary.TryGetValue(a.Pair.Key, out var s);
                instances.TryGetValue(a.Pair.Key, out var inst);
                messages.TryGetValue(a.Pair.Key, out var m);
                results[i] = Classify(kind, a, s, inst, m);
                if (results[i].Result is not (DpContentResults.Confirmed or DpContentResults.Failed)) still.Add((a, i));
            }
            open = still;
        }
        foreach (var (a, i) in open)
            if (a.Status == "unknown" && results[i].Result == DpContentResults.Unchanged)
                results[i] = results[i] with { Result = DpContentResults.Failed, Message = "The request could not be confirmed: the ConfigMgr session failed during the write and the read-back shows no sign of it." };
        return results;
    }

    /// <summary>The result of one written pair from what the site holds now. The rules follow the state sequences CM01 shows for each kind.</summary>
    internal static DpContentPairOutcome Classify(DpContentChangeKind kind, PairRowBefore before, DpContentSiteRead.Summary? s, DpContentSiteRead.Instance? inst, DpContentSiteRead.Message? m)
    {
        var pair = before.Pair;
        int? state = s?.State;
        var newMessage = m is not null && (m.MessageId != before.BeforeMessage || !string.Equals(m.Time, before.BeforeMessageTime, StringComparison.Ordinal));
        var errorNow = m is not null && m.MessageState is 3 or 4 && newMessage;
        DpContentPairOutcome R(string result, string text) => new(pair, result, text, state);
        string Code(DpContentSiteRead.Message x) => x.MessageId.ToString(CultureInfo.InvariantCulture);
        switch (kind)
        {
            case DpContentChangeKind.Redistribute:
            {
                if (s is null && inst is null) return R(DpContentResults.Failed, "The site no longer lists the pair.");
                if (errorNow) return R(DpContentResults.Failed, $"The distribution point reported an error after the request (status message {Code(m!)}).");
                var processed = inst is not null && !inst.RefreshNow && !string.Equals(inst.LastRefresh, before.BeforeRefresh, StringComparison.Ordinal);
                if (processed && state is { } st && DpContentStates.IsOk(st)) return R(DpContentResults.Confirmed, "The site copied the content to the distribution point again.");
                if (processed && state is { } sf && DpContentStates.IsFailed(sf)) return R(DpContentResults.Failed, $"The site processed the request; the distribution point still reports {DpContentStates.Name(sf)}.");
                if (inst is { RefreshNow: true } || inst?.Status is 1 or 2 || state is { } sp && DpContentStates.IsInProgress(sp) || processed)
                    return R(DpContentResults.Requested, "The site accepted the request and is copying the content.");
                return R(DpContentResults.Unchanged, "The site shows no sign of the request.");
            }
            case DpContentChangeKind.Validate:
            {
                if (s is null && inst is null) return R(DpContentResults.Failed, "The site no longer lists the pair.");
                if (errorNow) return R(DpContentResults.Failed, $"The validation reported a problem (status message {Code(m!)}).");
                if (newMessage && m!.MessageState == 1 && state is { } st && DpContentStates.IsOk(st)) return R(DpContentResults.Confirmed, $"The site validated the content (status message {Code(m)}).");
                if (state is { } sf && DpContentStates.IsFailed(sf)) return R(DpContentResults.Failed, $"The distribution point reports {DpContentStates.Name(sf)}.");
                if (inst?.Status == 4 || newMessage) return R(DpContentResults.Requested, "The site is validating the content.");
                return R(DpContentResults.Unchanged, "The site shows no sign of the request.");
            }
            case DpContentChangeKind.Remove:
            {
                if (s is null && inst is null) return R(DpContentResults.Confirmed, "The site no longer lists the pair. The distribution point deletes the files later.");
                if (state == DpContentStates.RemovalFailed) return R(DpContentResults.Failed, "The distribution point reports Removal failed.");
                if (state is DpContentStates.RemovalPending or DpContentStates.RemovalRetrying || inst?.Status == 3) return R(DpContentResults.Requested, "The removal is pending.");
                return R(DpContentResults.Unchanged, $"The site still lists the pair as {(state is { } x ? DpContentStates.Name(x) : "present")}.");
            }
            default:
            {
                if (errorNow) return R(DpContentResults.Failed, $"The distribution point reported an error (status message {Code(m!)}).");
                if (state is { } st && DpContentStates.IsOk(st)) return R(DpContentResults.Confirmed, "The content is installed on the distribution point again.");
                if (state is { } sf && DpContentStates.IsFailed(sf)) return R(DpContentResults.Failed, $"The distribution point reports {DpContentStates.Name(sf)}.");
                if (s is not null || inst is not null) return R(DpContentResults.Requested, "The site is copying the content.");
                return R(DpContentResults.Unchanged, "The site shows no distribution for the pair. A request made while the site still removes the same content can be ignored: run it again later.");
            }
        }
    }

    private static DpContentPairOutcome Classify(DpContentChangeKind kind, Attempt a, DpContentSiteRead.Summary? s, DpContentSiteRead.Instance? inst, DpContentSiteRead.Message? m) =>
        Classify(kind, new PairRowBefore(a.Pair, a.BeforeRefresh, a.BeforeMessage, a.BeforeMessageTime), s, inst, m);

    /// <summary>A pair with the values the write read just before it changed the pair.</summary>
    internal sealed record PairRowBefore(PairRow Pair, string BeforeRefresh, int BeforeMessage, string BeforeMessageTime);
}
