using System.Globalization;
using System.Text.Json;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

public sealed partial class PivotsScriptsService
{
    /// <summary>A run that was sent: what the wait needs to read and report its results.</summary>
    private sealed class RunState
    {
        public required PivotsChange Change { get; init; }
        public required string OperationId { get; init; }
        public required PivotsSessionRecord Session { get; set; }
        public required IReadOnlyList<ResolvedDevice> Devices { get; init; }
        public required DateTime SentUtc { get; init; }
        public required TimeSpan Window { get; init; }
        public required string RunId { get; init; }
        public Dictionary<int, DeviceResult> Results { get; } = [];
        public HashSet<int> FullFetched { get; } = [];
        public List<PivotsRawRecord> Raw { get; } = [];
        public TimeSpan? FirstResultAfter { get; set; }
    }

    /// <summary>The content part of a plan token: the fingerprint of what the plan runs, without the preparation token.</summary>
    internal static string ContentOf(string? token) => (token ?? "") is var t && t.IndexOf('.') is var dot and > 0 ? t[..dot] : "";

    private const string UsedMessage = "the plan was used already: a plan runs once. Prepare the plan again.";

    private static PivotsOutcome Refused(PivotsChange change, string reason) =>
        new(change, false, SkippedPrefix + reason, null, [], false, null) { Sent = false };

    public async Task<PivotsOutcome> ApplyAsync(PivotsChange change, IProgress<PivotsOutcome> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(progress);
        PivotsOutcome outcome;
        try
        {
            outcome = await ApplyOneAsync(change, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = Refused(change, "the action was cancelled before anything was sent.");
        }
        catch (PivotsServiceException ex)
        {
            outcome = Refused(change, ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.Warning(LogArea, $"{change.Kind} stopped with an unexpected error: {ex.GetType().Name}.");
            outcome = new PivotsOutcome(change, false, "The action stopped with an unexpected error. Refresh to see the current state. The log has the kind of failure.", null, [], true, null);
        }
        progress.Report(outcome);
        return outcome;
    }

    /// <summary>The checks every action passes before anything is sent; a reason to refuse, or null.</summary>
    private string? Gate(PivotsChange change)
    {
        if (!string.IsNullOrEmpty(change.Fingerprint) && !_prepared.ContainsKey(change.Fingerprint) && _used.ContainsKey(change.Fingerprint)) return UsedMessage;
        if (string.IsNullOrEmpty(change.Fingerprint) || !_prepared.TryGetValue(change.Fingerprint, out var prepared))
            return "the plan was not prepared by this session. Prepare the plan again.";
        if (!string.Equals(FingerprintOf(change), ContentOf(change.Fingerprint), StringComparison.Ordinal) || prepared.Change.Kind != change.Kind)
            return "the plan differs from the prepared plan. Prepare the plan again.";
        if (change.Blocked) return "a check of the plan failed: " + string.Join(" ", change.Checks.Where(c => c.Result is DeploymentCheckResults.Fail or DeploymentCheckResults.Unknown).Select(c => c.Message));
        if (prepared.Change.Blocked) return "a check of the prepared plan failed. Prepare the plan again.";
        if (change.IsRun)
        {
            if (change.Targets is null || change.DeviceCount <= 0) return "the plan has no device.";
            if (change.DeviceCount > MaxDevicesPerRun) return $"the plan has {Count(change.DeviceCount, "device")}; a run takes at most {MaxDevicesPerRun.ToString("N0", CultureInfo.InvariantCulture)}.";
            if (prepared.Change.Targets?.IsBuiltInCollection == true && !change.EveryDeviceAcknowledged)
                return "a run on a collection the site creates needs the box \"This runs on every device of the site\". Nothing was sent.";
            if (change.Kind == PivotsChangeKind.RunScript && !change.RunConfirmed)
                return $"a script run needs the second confirmation for {Count(change.DeviceCount, "device")}. Nothing was sent.";
        }
        return null;
    }

    private async Task<PivotsOutcome> ApplyOneAsync(PivotsChange change, IProgress<PivotsOutcome> progress, CancellationToken ct)
    {
        if (Gate(change) is { } reason) return Refused(change, reason);
        // A prepared plan runs once: a second Apply of the same plan (a double click) is refused.
        if (!_prepared.TryRemove(change.Fingerprint!, out var prepared)) return Refused(change, UsedMessage);
        _used[change.Fingerprint!] = _utcNow();
        foreach (var old in _used.Where(u => u.Value < _utcNow().AddDays(-1)).Select(u => u.Key).ToList()) _used.TryRemove(old, out _);
        // The prepared copy decides what runs; the caller's copy only adds the confirmations.
        var planned = prepared.Change with { RunConfirmed = change.RunConfirmed, EveryDeviceAcknowledged = change.EveryDeviceAcknowledged };
        return planned.Kind switch
        {
            PivotsChangeKind.RunPivot or PivotsChangeKind.RunScript => await RunAsync(planned, progress, ct).ConfigureAwait(false),
            PivotsChangeKind.SubmitScript => await SubmitAsync(planned, ct).ConfigureAwait(false),
            _ => await ScriptActionAsync(planned, ct).ConfigureAwait(false)
        };
    }

    // ---- Runs ---------------------------------------------------------------------------------

    private async Task<PivotsOutcome> RunAsync(PivotsChange change, IProgress<PivotsOutcome> progress, CancellationToken ct)
    {
        var targets = change.Targets!;
        var devices = targets.Devices.Where(d => d.IsClient).ToList();
        var planned = devices.Select(d => d.ResourceId).ToHashSet();

        // What the site holds now is read again: the collection members and the script.
        if (targets.CollectionId is { } cid)
        {
            var now = await MembersAsync(cid, ct).ConfigureAwait(false);
            var added = now.Where(d => d.IsClient && !planned.Contains(d.ResourceId)).ToList();
            if (added.Count > 0)
                return Refused(change, $"the collection {cid} has {Count(added.Count, "device")} that the plan does not hold ({string.Join(", ", added.Take(5).Select(d => d.Name))}{(added.Count > 5 ? ", ..." : "")}). The site would run on them too. Prepare the plan again. Nothing was sent.");
        }
        if (change.Kind == PivotsChangeKind.RunScript)
        {
            var script = await LoadScriptAsync(change.ScriptGuid!, ct).ConfigureAwait(false);
            if (script is null) return Refused(change, "the site no longer holds the script. Nothing was sent.");
            if (!string.Equals(script.Hash, change.ScriptHash, StringComparison.OrdinalIgnoreCase) || !string.Equals(script.SiteHash, change.Script?.SiteHash, StringComparison.OrdinalIgnoreCase))
                return Refused(change, "the script text on the site changed after the plan was made. Load the script again, check the text and prepare a new plan. Nothing was sent.");
            if (!script.IsApproved) return Refused(change, $"the script is {script.State.ToLowerInvariant()} on the site now. Only an approved script runs. Nothing was sent.");
            var problems = ParameterProblems(script, change.Parameters);
            if (problems.Count > 0) return Refused(change, "the parameter values do not match the script on the site: " + string.Join(" ", problems) + " Nothing was sent.");
        }
        ct.ThrowIfCancellationRequested();

        using var audit = _audit is PivotsAuditLog real ? real.Reserve() : new PivotsAuditReservation(_audit);
        if (!audit.Probe.Writable) return Refused(change, "the audit log cannot be written, so nothing was sent. " + audit.Probe.Message);
        var runId = Guid.NewGuid().ToString("N");
        var (site, provider) = _connection();
        var textHash = change.Kind == PivotsChangeKind.RunPivot ? HashOf(change.QueryText) : change.ScriptHash;
        var session = new PivotsSessionRecord(
            Id: PivotsSessionStore.NewId(),
            CreatedUtc: _utcNow(),
            User: _user(),
            Site: site,
            Provider: provider,
            Kind: change.Kind,
            TargetDescription: targets.Description,
            CollectionId: targets.CollectionId,
            Devices: devices,
            QueryText: change.QueryText,
            ScriptGuid: change.ScriptGuid,
            ScriptName: change.Script?.Name,
            ScriptHash: textHash,
            Parameters: change.Parameters ?? new Dictionary<string, string>());
        try
        {
            _sessions.Save(session);
        }
        catch (PivotsServiceException ex)
        {
            return Refused(change, ex.Message);
        }
        var started = audit.Append(new PivotsAuditRecord(default, _user(), change.Kind.ToString(), "Started", "", runId, targets.Description, devices.Count, change.ScriptGuid, textHash, null,
            $"Sending to {Count(devices.Count, "device")}.") { SessionId = session.Id });
        if (!started.Succeeded)
        {
            TryUpdate(session with { Status = "Skipped", Message = "The audit record could not be written, so nothing was sent." });
            return Refused(change, "the audit log could not be written, so nothing was sent. " + started.Message);
        }
        if (ct.IsCancellationRequested)
        {
            audit.Append(Finished(change, runId, targets.Description, devices.Count, textHash, null, session.Id, "Skipped", "Cancelled before the send."));
            TryUpdate(session with { Status = "Skipped", Message = "Cancelled before the send." });
            return Refused(change, "the run was cancelled before anything was sent.");
        }

        // The send is not cancelled once it starts: a request cut in flight can still reach the site,
        // and its result would not be known.
        var send = change.Kind == PivotsChangeKind.RunPivot
            ? await SendPivotAsync(change, devices).ConfigureAwait(false)
            : await SendScriptAsync(change, devices).ConfigureAwait(false);
        var sentUtc = _utcNow();
        var raw = new List<PivotsRawRecord> { new(sentUtc, "Submit", send.Status, send.Raw) };
        if (send.OperationId is null)
        {
            var state = send.MayHaveReached ? PivotsDeviceStates.NoResponse : PivotsDeviceStates.Failed;
            var message = send.MayHaveReached
                ? send.Message + " The request can have reached the site: check Monitoring > Client Operations or Script Status before you run it again."
                : send.Message;
            var results = devices.Select(d => new DeviceResult(d.ResourceId, d.Name, state, "", message)).ToList();
            audit.Append(Finished(change, runId, targets.Description, devices.Count, textHash, null, session.Id, send.MayHaveReached ? "Partial" : "Failed", message));
            TryUpdate(session with { Status = send.MayHaveReached ? "Unknown" : "Failed", Message = message, Raw = raw, Results = results, FinishedUtc = _utcNow(), Partial = send.MayHaveReached });
            _log?.Warning(LogArea, $"{change.Kind} was not accepted by the site.");
            return new PivotsOutcome(change, false, message, null, results, send.MayHaveReached, session.Id) { Sent = send.MayHaveReached };
        }

        var run = new RunState
        {
            Change = change,
            OperationId = send.OperationId,
            Session = session with { OperationId = send.OperationId, Status = "Sent", Raw = raw },
            Devices = devices,
            SentUtc = sentUtc,
            Window = WindowOf(change),
            RunId = runId
        };
        run.Raw.AddRange(raw);
        foreach (var d in devices) run.Results[d.ResourceId] = new DeviceResult(d.ResourceId, d.Name, PivotsDeviceStates.Sent, "", "The site accepted the run; the device has not reported yet.");
        TryUpdate(run.Session with { Results = run.Results.Values.ToList() });
        RememberRun(run);
        _log?.Info(LogArea, $"{change.Kind} sent to {devices.Count} devices.");
        progress.Report(Snapshot(run, false, "Sent. Waiting for results.", final: false));
        var outcome = await WaitAsync(run, progress, ct).ConfigureAwait(false);
        audit.Append(Finished(change, runId, targets.Description, devices.Count, textHash, run.OperationId, session.Id, outcome.Partial ? "Partial" : outcome.Succeeded ? "Succeeded" : "Failed", outcome.Message));
        return outcome;
    }

    private PivotsAuditRecord Finished(PivotsChange change, string runId, string target, int count, string? hash, string? op, string sessionId, string result, string message) =>
        new(default, _user(), change.Kind.ToString(), "Finished", result, runId, target, count, change.ScriptGuid, hash, op, message) { SessionId = sessionId };

    private void TryUpdate(PivotsSessionRecord record)
    {
        try
        {
            _sessions.Update(record);
        }
        catch (PivotsServiceException)
        {
            _log?.Warning(LogArea, "A session file could not be updated.");
        }
    }

    private sealed record SendResult(string? OperationId, int? Status, string Raw, string Message, bool MayHaveReached);

    private async Task<SendResult> SendPivotAsync(PivotsChange change, IReadOnlyList<ResolvedDevice> devices)
    {
        var targets = change.Targets!;
        var (path, body) = targets.CollectionId is { } cid
            ? ($"v1.0/Collections('{cid}')/AdminService.RunCMPivot", JsonSerializer.Serialize(new { InputQuery = change.QueryText }))
            : ("v1.0/SMS_CMPivotStatus/AdminService.RunCMPivot", JsonSerializer.Serialize(new { InputQuery = change.QueryText, ResourceIds = string.Join(",", devices.Select(d => d.ResourceId.ToString(CultureInfo.InvariantCulture))) }));
        AdminServiceResponse response;
        try
        {
            response = await _admin.SendAsync(HttpMethod.Post, path, body, CancellationToken.None).ConfigureAwait(false);
        }
        catch (AdminServiceUnreachableException ex)
        {
            return new SendResult(null, null, "", ex.Message, ex.MayHaveReachedSite);
        }
        if (response.Status != 200)
            return new SendResult(null, response.Status, Bounded(response.Body), HttpMessage(response.Status, PivotRights) + " Nothing reached a client.", false);
        var op = PivotOperationId(response.Body);
        return op is null
            ? new SendResult(null, response.Status, Bounded(response.Body), "The site accepted the run but returned no operation ID, so its results cannot be read.", true)
            : new SendResult(op, response.Status, Bounded(response.Body), "", true);
    }

    /// <summary>The OperationId of a RunCMPivot answer: at the top for a collection, inside "value" for a device list.</summary>
    internal static string? PivotOperationId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object) root = v;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("OperationId", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetInt64(out var id) && id > 0)
                return id.ToString(CultureInfo.InvariantCulture);
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static string Bounded(string body) => body.Length <= 64 * 1024 ? body : body[..(64 * 1024)] + "...";

    private async Task<SendResult> SendScriptAsync(PivotsChange change, IReadOnlyList<ResolvedDevice> devices)
    {
        var p = new ConfigMgrParameters { { "Operation", "Run" }, { "ScriptGuid", change.ScriptGuid } };
        var values = change.Parameters ?? new Dictionary<string, string>();
        p.Add("ParameterNames", values.Keys.ToArray());
        p.Add("ParameterValues", values.Keys.Select(k => values[k]).ToArray());
        if (change.Targets!.CollectionId is { } cid) p.Add("CollectionId", cid);
        else p.Add("ResourceIds", devices.Select(d => d.ResourceId).ToArray());
        IReadOnlyList<JsonElement> output;
        try
        {
            var result = await _session.InvokeAsync(Script, p, CancellationToken.None, ReadTimeout).ConfigureAwait(false);
            output = result.Output;
        }
        catch (ConfigMgrSessionException ex)
        {
            _log?.Warning(LogArea, $"A script run failed in the ConfigMgr session: {ex.Failure}.");
            var reached = ex.Failure is ConfigMgrFailure.Timeout or ConfigMgrFailure.ProcessDied or ConfigMgrFailure.Unexpected;
            return new SendResult(null, null, "", SessionMessage(ex, ScriptRights) + (reached ? "" : " Nothing reached a client."), reached);
        }
        var raw = string.Join("\n", output.Select(o => o.GetRawText()));
        if (Kind(output, "Missing").FirstOrDefault() is { ValueKind: JsonValueKind.Object } missing)
            return new SendResult(null, null, raw, $"The site holds {I(missing, "Found")} of the {I(missing, "Wanted")} devices of the plan now. Resolve the targets again. Nothing was sent.", false);
        var started = Kind(output, "Started").FirstOrDefault();
        var op = started.ValueKind == JsonValueKind.Object && long.TryParse(S(started, "Operation"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0 ? id.ToString(CultureInfo.InvariantCulture) : null;
        return op is null
            ? new SendResult(null, null, raw, "The site accepted the run but returned no operation ID, so its results cannot be read.", true)
            : new SendResult(op, null, raw, "", true);
    }

    private void RememberRun(RunState run)
    {
        if (_runs.Count >= MaxRuns)
            foreach (var old in _runs.OrderBy(r => r.Value.SentUtc).Take(_runs.Count - MaxRuns + 1).Select(r => r.Key).ToList())
                _runs.TryRemove(old, out _);
        _runs[run.OperationId] = run;
    }

    // ---- Waiting ------------------------------------------------------------------------------

    public async Task<PivotsOutcome> WaitForResultsAsync(string operationId, IProgress<PivotsOutcome> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!_runs.TryGetValue(operationId ?? "", out var run))
            throw new PivotsServiceException("This session did not send that run, so the page cannot wait for it. Open its saved session to see the results it holds.");
        var outcome = await WaitAsync(run, progress, ct, again: true).ConfigureAwait(false);
        progress.Report(outcome);
        return outcome;
    }

    private async Task<PivotsOutcome> WaitAsync(RunState run, IProgress<PivotsOutcome> progress, CancellationToken ct, bool again = false)
    {
        var deadline = (again ? _utcNow() : run.SentUtc) + run.Window;
        var lastError = "";
        while (true)
        {
            if (ct.IsCancellationRequested) return Finish(run, stopped: true, lastError);
            try
            {
                var changed = await PollAsync(run, ct).ConfigureAwait(false);
                lastError = "";
                if (changed) progress.Report(Snapshot(run, false, $"Waiting: {Count(run.Results.Values.Count(r => r.IsFinal), "device")} of {run.Devices.Count.ToString("N0", CultureInfo.InvariantCulture)} answered.", final: false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Finish(run, stopped: true, lastError);
            }
            catch (Exception ex) when (ex is PivotsServiceException or AdminServiceUnreachableException)
            {
                lastError = ex.Message;
                _log?.Warning(LogArea, "A status read failed; the wait goes on.");
            }
            if (run.Results.Values.All(r => r.IsFinal)) return Finish(run, stopped: false, lastError);
            if (_utcNow() >= deadline) return Finish(run, stopped: false, lastError, timedOut: true);
            try
            {
                await _delay(PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Finish(run, stopped: true, lastError);
            }
        }
    }

    /// <summary>One status read for all devices of the run; true when a device result changed.</summary>
    private async Task<bool> PollAsync(RunState run, CancellationToken ct)
    {
        var rows = run.Change.Kind == PivotsChangeKind.RunPivot ? await PivotStatusAsync(run, ct).ConfigureAwait(false) : await ScriptStatusAsync(run, ct).ConfigureAwait(false);
        var changed = false;
        foreach (var row in rows)
        {
            if (!run.Results.TryGetValue(row.ResourceId, out var before)) continue;
            if (before.IsFinal || (before.State == row.State && before.Output == row.Output)) continue;
            var result = row with { Name = before.Name.Length > 0 ? before.Name : row.Name };
            if (run.Change.Kind == PivotsChangeKind.RunPivot && result.State == PivotsDeviceStates.Completed && result.MoreResults && run.FullFetched.Add(result.ResourceId))
                result = await FullPivotRowsAsync(run, result, ct).ConfigureAwait(false);
            run.Results[row.ResourceId] = result;
            if (result.IsFinal && run.FirstResultAfter is null) run.FirstResultAfter = _utcNow() - run.SentUtc;
            changed = true;
        }
        return changed;
    }

    private async Task<List<DeviceResult>> PivotStatusAsync(RunState run, CancellationToken ct)
    {
        var list = new List<DeviceResult>();
        var path = $"v1.0/SMS_CMPivotStatus?$filter=ClientOperationId eq {run.OperationId}";
        for (var page = 0; path is not null && page < 100; page++)
        {
            var response = await _admin.SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
            if (response.Status != 200) throw new PivotsServiceException(HttpMessage(response.Status, PivotRights));
            path = null;
            using var doc = JsonDocument.Parse(response.Body);
            if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                foreach (var r in value.EnumerateArray()) list.Add(PivotRow(r));
            if (doc.RootElement.TryGetProperty("@odata.nextLink", out var next) && next.ValueKind == JsonValueKind.String)
                path = NextPath(next.GetString());
        }
        return list;
    }

    /// <summary>The relative path of a next link on the same service; null for a link to another host.</summary>
    internal string? NextPath(string? link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, _admin.Provider, StringComparison.OrdinalIgnoreCase)) return null;
        var marker = uri.PathAndQuery.IndexOf("/AdminService/", StringComparison.OrdinalIgnoreCase);
        return marker < 0 ? null : uri.PathAndQuery[(marker + "/AdminService/".Length)..];
    }

    /// <summary>
    /// A status row of SMS_CMPivotStatus. ScriptExecutionState 1 with no error is a result; 2, an
    /// ErrorMessage or a non-zero exit code is a failure; another state is a device still working.
    /// </summary>
    internal static DeviceResult PivotRow(JsonElement r)
    {
        var id = I(r, "ResourceId");
        var name = S(r, "DeviceName");
        var state = I(r, "ScriptExecutionState", -1);
        var exit = int.TryParse(S(r, "ScriptExitCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? e : (int?)null;
        var error = S(r, "ErrorMessage");
        var output = S(r, "ScriptOutput");
        var updated = PivotsResults.Utc(S(r, "LastUpdateTime"));
        if (state == 1 && string.IsNullOrWhiteSpace(error) && (exit ?? 0) == 0)
        {
            var (rows, more, readable) = PivotsResults.PivotRows(output);
            return new DeviceResult(id, name, PivotsDeviceStates.Completed, PivotsResults.Cut(output), readable ? (more ? "The site holds more rows than this answer carries." : "") : "The result could not be read as CMPivot rows.")
            { ExitCode = exit, Rows = rows, MoreResults = more, LastUpdateUtc = updated };
        }
        // The site's error text stays in the output (the Raw view); the message is fixed.
        if (state is 1 or 2 || !string.IsNullOrWhiteSpace(error))
            return new DeviceResult(id, name, PivotsDeviceStates.Failed, PivotsResults.Cut(string.IsNullOrWhiteSpace(error) ? output : error.Trim()),
                $"The query failed on the device (exit code {exit?.ToString(CultureInfo.InvariantCulture) ?? "not given"}). The output column holds the text the site returned.")
            { ExitCode = exit, LastUpdateUtc = updated };
        return new DeviceResult(id, name, PivotsDeviceStates.Running, "", "The device reported; the result has not arrived.") { ExitCode = exit, LastUpdateUtc = updated };
    }

    /// <summary>The whole result of one device whose status row carries part of it: one request.</summary>
    private async Task<DeviceResult> FullPivotRowsAsync(RunState run, DeviceResult partial, CancellationToken ct)
    {
        try
        {
            var response = await _admin.SendAsync(HttpMethod.Get, $"v1.0/Device({partial.ResourceId.ToString(CultureInfo.InvariantCulture)})/AdminService.CMPivotResult(OperationId={run.OperationId})", null, ct).ConfigureAwait(false);
            if (response.Status != 200) return partial;
            using var doc = JsonDocument.Parse(response.Body);
            if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object) return partial;
            var more = value.TryGetProperty("MoreResult", out var m) && m.ValueKind == JsonValueKind.True;
            var rows = new List<IReadOnlyDictionary<string, string>>();
            if (value.TryGetProperty("Result", out var result) && result.ValueKind == JsonValueKind.Array)
                foreach (var item in result.EnumerateArray())
                {
                    if (rows.Count >= PivotsResults.MaxRowsPerDevice) { more = true; break; }
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var row = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var p in item.EnumerateObject())
                        if (p.Name != "Device") row[p.Name] = PivotsResults.Cut(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ValueKind == JsonValueKind.Null ? "" : p.Value.GetRawText());
                    rows.Add(row);
                }
            return partial with { Rows = rows, MoreResults = more, Message = more ? "The site holds more rows than it returns for this device." : "" };
        }
        catch (Exception ex) when (ex is PivotsServiceException or AdminServiceUnreachableException or JsonException)
        {
            return partial;
        }
    }

    private async Task<List<DeviceResult>> ScriptStatusAsync(RunState run, CancellationToken ct)
    {
        var output = await RunAsync(new ConfigMgrParameters { { "Operation", "Status" }, { "OperationId", long.Parse(run.OperationId, CultureInfo.InvariantCulture) } }, ct, ReadTimeout, ScriptRights).ConfigureAwait(false);
        return Kind(output, "R").Select(ScriptRow).ToList();
    }

    /// <summary>
    /// A status row of SMS_ScriptsExecutionStatus. State 1 is a result (the site reports exit code 0
    /// even after exit N); state 2 is a script that stopped with an error.
    /// </summary>
    internal static DeviceResult ScriptRow(JsonElement r)
    {
        var id = I(r, "Id");
        var name = S(r, "N");
        var state = I(r, "State", -1);
        var exit = int.TryParse(S(r, "Exit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? e : (int?)null;
        var output = PivotsResults.ScriptOutputText(S(r, "Output"));
        var updated = PivotsResults.Utc(S(r, "Updated"));
        return state switch
        {
            1 => new DeviceResult(id, name, PivotsDeviceStates.Completed, PivotsResults.Cut(output), "") { ExitCode = exit, Rows = PivotsResults.ScriptRows(output), LastUpdateUtc = updated },
            2 => new DeviceResult(id, name, PivotsDeviceStates.Failed, PivotsResults.Cut(output), $"The script stopped with an error on the device (exit code {exit?.ToString(CultureInfo.InvariantCulture) ?? "not given"}).") { ExitCode = exit, LastUpdateUtc = updated },
            _ => new DeviceResult(id, name, PivotsDeviceStates.Running, "", "The device reported; the result has not arrived.") { ExitCode = exit, LastUpdateUtc = updated }
        };
    }

    private PivotsOutcome Finish(RunState run, bool stopped, string lastError, bool timedOut = false)
    {
        foreach (var (id, r) in run.Results.ToList())
        {
            if (r.IsFinal) continue;
            if (stopped)
                run.Results[id] = r with { Message = r.State == PivotsDeviceStates.Running ? "Stopped waiting. The device received the request and can still finish it." : "Stopped waiting. A device that received the request still runs it." };
            else if (timedOut)
                run.Results[id] = r.State == PivotsDeviceStates.Running
                    ? r with { State = PivotsDeviceStates.TimedOut, Message = "The device reported but no result arrived in the wait time. The device can still finish it." }
                    : r with { State = PivotsDeviceStates.NoResponse, Message = (lastError.Length > 0 ? "The status could not be read: " + lastError + " " : "") + "No answer from the device in the wait time. An offline device does not get the request; a device that gets it later can still run it." };
        }
        var list = Ordered(run);
        var completed = list.Count(r => r.State == PivotsDeviceStates.Completed);
        var failed = list.Count(r => r.State == PivotsDeviceStates.Failed);
        var partial = list.Any(r => !r.IsFinal || r.State is PivotsDeviceStates.TimedOut or PivotsDeviceStates.NoResponse);
        var summary = $"{completed.ToString("N0", CultureInfo.InvariantCulture)} completed, {failed.ToString("N0", CultureInfo.InvariantCulture)} failed, {list.Count(r => r.State == PivotsDeviceStates.TimedOut).ToString("N0", CultureInfo.InvariantCulture)} timed out, {list.Count(r => r.State == PivotsDeviceStates.NoResponse).ToString("N0", CultureInfo.InvariantCulture)} no response, {list.Count(r => !r.IsFinal).ToString("N0", CultureInfo.InvariantCulture)} not answered yet";
        var message = stopped
            ? $"Stopped waiting: {summary}. Clients that received the request still run it; the results on this page are partial."
            : partial ? $"The wait ended: {summary}. The results are partial." : $"Done: {summary}.";
        var status = stopped ? "Stopped waiting" : partial ? "Partial" : failed > 0 ? "Completed with failures" : "Completed";
        run.Session = run.Session with { Status = status, Message = message, Results = list, Raw = run.Raw.ToList(), FinishedUtc = _utcNow(), Partial = partial };
        TryUpdate(run.Session);
        _log?.Info(LogArea, $"{run.Change.Kind} wait ended: {completed} completed, {failed} failed, {list.Count - completed - failed} without a result.");
        return Snapshot(run, !partial && failed == 0, message, final: true) with { Partial = partial };
    }

    private static List<DeviceResult> Ordered(RunState run) => run.Results.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ResourceId).ToList();

    private static PivotsOutcome Snapshot(RunState run, bool succeeded, string message, bool final)
    {
        var list = Ordered(run);
        return new PivotsOutcome(run.Change, succeeded, message, run.OperationId, list, list.Any(r => !r.IsFinal || r.State is PivotsDeviceStates.TimedOut or PivotsDeviceStates.NoResponse), run.Session.Id)
        {
            Final = final,
            Sent = true,
            SentUtc = run.SentUtc,
            FirstResultAfter = run.FirstResultAfter
        };
    }

    // ---- Script actions -----------------------------------------------------------------------

    private async Task<PivotsOutcome> SubmitAsync(PivotsChange change, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var audit = _audit is PivotsAuditLog real ? real.Reserve() : new PivotsAuditReservation(_audit);
        if (!audit.Probe.Writable) return Refused(change, "the audit log cannot be written, so nothing was created. " + audit.Probe.Message);
        var runId = Guid.NewGuid().ToString("N");
        var hash = HashOf(change.SubmitText);
        var started = audit.Append(new PivotsAuditRecord(default, _user(), change.Kind.ToString(), "Started", "", runId, "", 0, null, hash, null, $"Creating the script {change.SubmitName}."));
        if (!started.Succeeded) return Refused(change, "the audit log could not be written, so nothing was created. " + started.Message);
        IReadOnlyList<JsonElement> created;
        try
        {
            created = await RunAsync(new ConfigMgrParameters { { "Operation", "Create" }, { "ScriptName", change.SubmitName }, { "ScriptText", change.SubmitText } }, CancellationToken.None, ReadTimeout, ScriptRights).ConfigureAwait(false);
        }
        catch (PivotsServiceException ex)
        {
            audit.Append(new PivotsAuditRecord(default, _user(), change.Kind.ToString(), "Finished", "Failed", runId, "", 0, null, hash, null, ex.Message));
            return new PivotsOutcome(change, false, "The script was not created. " + ex.Message, null, [], false, null);
        }
        var guid = Kind(created, "Created").Select(c => S(c, "Guid")).FirstOrDefault(g => GuidRegex().IsMatch(g));
        if (guid is null)
        {
            audit.Append(new PivotsAuditRecord(default, _user(), change.Kind.ToString(), "Finished", "Failed", runId, "", 0, null, hash, null, "The site returned no GUID for the new script."));
            return new PivotsOutcome(change, false, "The site returned no GUID for the new script. Check the scripts list: a script can have been created.", null, [], true, null);
        }
        guid = guid.ToUpperInvariant();
        SiteScript? after = null;
        string? defineError = null;
        if (change.SubmitParameters.Count > 0)
        {
            try
            {
                var defined = await RunAsync(new ConfigMgrParameters { { "Operation", "Define" }, { "ScriptGuid", guid }, { "ScriptText", change.SubmitText } }, CancellationToken.None, ReadTimeout, ScriptRights).ConfigureAwait(false);
                var d = Kind(defined, "Defined").FirstOrDefault();
                if (d.ValueKind != JsonValueKind.Object || I(d, "Return", -1) != 0 || B(d, "Match") != true) defineError = "The site did not store the parameter definitions as sent.";
                after = Kind(defined, "S").Select(s => ToScript(s)).FirstOrDefault(s => s is not null);
            }
            catch (PivotsServiceException ex)
            {
                defineError = ex.Message;
            }
        }
        else
        {
            try { after = await LoadScriptAsync(guid, CancellationToken.None).ConfigureAwait(false); } catch (PivotsServiceException) { }
        }
        if (defineError is not null)
        {
            var cleanup = new PivotsChange(Kind: PivotsChangeKind.RemoveScript, Targets: null, QueryText: null, ScriptGuid: guid, ScriptHash: null, Parameters: null, SubmitName: null, SubmitText: null,
                Comment: null, RunConfirmed: false, EveryDeviceAcknowledged: false, Impact: [], Checks: [], Fingerprint: null);
            var message = $"The script {change.SubmitName} was created on the site with the GUID {guid}, but its parameter definitions were not stored: {defineError} Without them the script cannot take values. Remove only this script with the plan the page offers, or add its parameters in the ConfigMgr console.";
            audit.Append(new PivotsAuditRecord(default, _user(), change.Kind.ToString(), "Finished", "Partial", runId, "", 0, guid, hash, null, message));
            return new PivotsOutcome(change, false, message, null, [], true, null) { CreatedScriptGuid = guid, CleanupChange = cleanup, ScriptAfter = after };
        }
        var done = $"The script {change.SubmitName} was created with the GUID {guid}. It waits for approval.";
        audit.Append(new PivotsAuditRecord(default, _user(), change.Kind.ToString(), "Finished", "Succeeded", runId, "", 0, guid, hash, null, done));
        _log?.Info(LogArea, "A script was submitted.");
        return new PivotsOutcome(change, true, done, null, [], false, null) { CreatedScriptGuid = guid, ScriptAfter = after };
    }

    private async Task<PivotsOutcome> ScriptActionAsync(PivotsChange change, CancellationToken ct)
    {
        var now = await LoadScriptAsync(change.ScriptGuid!, ct).ConfigureAwait(false);
        if (now is null)
            return change.Kind == PivotsChangeKind.RemoveScript
                ? Refused(change, "the site no longer holds the script; nothing to remove.")
                : Refused(change, "the site no longer holds the script. Nothing was changed.");
        if (!string.Equals(now.Hash, change.ScriptHash, StringComparison.OrdinalIgnoreCase))
            return Refused(change, "the script text on the site changed after the plan was made. Load the script again and check the text. Nothing was changed.");
        ct.ThrowIfCancellationRequested();
        using var audit = _audit is PivotsAuditLog real ? real.Reserve() : new PivotsAuditReservation(_audit);
        if (!audit.Probe.Writable) return Refused(change, "the audit log cannot be written, so nothing was changed. " + audit.Probe.Message);
        var runId = Guid.NewGuid().ToString("N");
        var verb = change.Kind switch { PivotsChangeKind.ApproveScript => "Approve", PivotsChangeKind.DenyScript => "Deny", _ => "Remove" };
        var started = audit.Append(new PivotsAuditRecord(default, _user(), change.Kind.ToString(), "Started", "", runId, "", 0, change.ScriptGuid, change.ScriptHash, null, $"{verb} the script {now.Name}."));
        if (!started.Succeeded) return Refused(change, "the audit log could not be written, so nothing was changed. " + started.Message);
        var p = new ConfigMgrParameters { { "Operation", verb }, { "ScriptGuid", change.ScriptGuid } };
        if (change.Kind != PivotsChangeKind.RemoveScript) p.Add("Comment", change.Comment ?? "");
        string message;
        bool ok;
        SiteScript? after = null;
        try
        {
            var output = await RunAsync(p, CancellationToken.None, ReadTimeout, ScriptRights).ConfigureAwait(false);
            if (change.Kind == PivotsChangeKind.RemoveScript)
            {
                var removed = Kind(output, "Removed").FirstOrDefault();
                ok = removed.ValueKind == JsonValueKind.Object && I(removed, "Left", -1) == 0;
                message = ok ? $"The script {now.Name} and its run history were removed from the site." : "The site did not confirm the removal. Refresh the scripts list.";
            }
            else
            {
                after = Kind(output, "S").Select(s => ToScript(s)).FirstOrDefault(s => s is not null);
                var want = change.Kind == PivotsChangeKind.ApproveScript ? SiteScriptStates.Approved : SiteScriptStates.Declined;
                ok = after?.State == want;
                message = ok ? $"The script {now.Name} is {want.ToLowerInvariant()} now." : $"The site did not change the state of the script; it reads {after?.State.ToLowerInvariant() ?? "unknown"}.";
            }
        }
        catch (PivotsServiceException ex)
        {
            ok = false;
            message = AuthorRefusal(change, now) ?? ex.Message;
        }
        audit.Append(new PivotsAuditRecord(default, _user(), change.Kind.ToString(), "Finished", ok ? "Succeeded" : "Failed", runId, "", 0, change.ScriptGuid, change.ScriptHash, null, message));
        _log?.Info(LogArea, $"{change.Kind}: {(ok ? "succeeded" : "failed")}.");
        return new PivotsOutcome(change, ok, message, null, [], false, null) { ScriptAfter = after };
    }

    /// <summary>The plain message when the site refuses an approval decision by the author of the script.</summary>
    private string? AuthorRefusal(PivotsChange change, SiteScript script) =>
        change.Kind is PivotsChangeKind.ApproveScript or PivotsChangeKind.DenyScript && string.Equals(script.Author, _user(), StringComparison.OrdinalIgnoreCase)
            ? $"The site refused to let the author {(change.Kind == PivotsChangeKind.ApproveScript ? "approve" : "deny")} the script. The hierarchy setting \"Script authors require additional script approver\" is on: another administrator with Approve on SMS Scripts must decide. The page does not change that setting."
            : null;
}
