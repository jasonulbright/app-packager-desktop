using ApplicationPackagerEngine.Services.ConfigMgrSession;
using System.Globalization;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

public sealed partial class PivotsScriptsService
{
    public static class CheckNames
    {
        public const string Targets = "Targets";
        public const string Cap = "Cap";
        public const string BuiltIn = "BuiltIn";
        public const string Offline = "Offline";
        public const string NonClients = "NonClients";
        public const string Query = "Query";
        public const string Script = "Script";
        public const string Approved = "Approved";
        public const string Parameters = "Parameters";
        public const string AuditLog = "AuditLog";
        public const string Name = "Name";
        public const string Author = "Author";
        public const string Unmatched = "Unmatched";
        public const string EveryDevice = "EveryDevice";
        public const string Provider = "Provider";
    }

    internal const string NoUndo = "A run cannot be undone: clients that receive the request run it, and there is no Undo. The audit log and the saved session are the record.";

    public async Task<PivotsChange> PrepareAsync(PivotsChange request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var checks = new List<PreflightCheck>();
        var impact = new List<string>();
        var prepared = request with { RunConfirmed = false, EveryDeviceAcknowledged = false, ChangeConfirmed = false, Checks = [], Impact = [], Fingerprint = null };
        switch (request.Kind)
        {
            case PivotsChangeKind.RunPivot:
            case PivotsChangeKind.RunScript:
                prepared = await PrepareRunAsync(prepared, checks, impact, ct).ConfigureAwait(false);
                break;
            case PivotsChangeKind.SubmitScript:
                prepared = await PrepareSubmitAsync(prepared, checks, impact, ct).ConfigureAwait(false);
                break;
            default:
                prepared = await PrepareScriptActionAsync(prepared, checks, impact, ct).ConfigureAwait(false);
                break;
        }
        var probe = _audit.Probe();
        checks.Add(new PreflightCheck(CheckNames.AuditLog, probe.Writable ? DeploymentCheckResults.Pass : DeploymentCheckResults.Fail,
            probe.Writable ? probe.Message : "The action is blocked: " + probe.Message));
        prepared = prepared with { Checks = checks, Impact = impact };
        if (!prepared.Blocked)
        {
            // A fresh token per preparation: an older copy of an equal plan cannot use a later preparation.
            prepared = prepared with { Fingerprint = FingerprintOf(prepared) + "." + Guid.NewGuid().ToString("N") };
            Remember(prepared);
        }
        _log?.Info(LogArea, $"Prepared {request.Kind}: {checks.Count(c => c.Result == DeploymentCheckResults.Fail)} failed checks, {prepared.DeviceCount} devices.");
        return prepared;
    }

    /// <summary>The wait of ApplyAsync: zero for a send-only change, otherwise the window of the change within the bounds.</summary>
    private static TimeSpan WindowOf(PivotsChange change) => change.IsSendOnly ? TimeSpan.Zero : WithinBounds(change.ResultWindow ?? DefaultResultWindow);

    internal static TimeSpan WithinBounds(TimeSpan w) => w < MinResultWindow ? MinResultWindow : w > MaxResultWindow ? MaxResultWindow : w;

    private async Task<PivotsChange> PrepareRunAsync(PivotsChange change, List<PreflightCheck> checks, List<string> impact, CancellationToken ct)
    {
        if (change.Targets is null || string.IsNullOrWhiteSpace(change.Targets.Text))
        {
            checks.Add(new PreflightCheck(CheckNames.Targets, DeploymentCheckResults.Fail, "Resolve the targets first."));
            return change;
        }
        // The targets are resolved again, so the plan holds the devices the site holds now.
        var targets = await ResolveTargetsAsync(new RunTarget(change.Targets.Kind, change.Targets.Text), ct).ConfigureAwait(false);
        var clients = targets.Devices.Where(d => d.IsClient).ToList();
        change = change with { Targets = targets, DeviceCount = clients.Count };
        checks.AddRange(TargetChecks(targets, clients));
        var window = WindowOf(change);
        var what = change.Kind == PivotsChangeKind.RunPivot ? "the CMPivot query" : "the script";
        impact.Add($"Run {what} on {Count(clients.Count, "device")}: {targets.Description}.");
        if (targets.CollectionId is { } cid)
            impact.Add($"The site receives one operation for the fixed list of devices in the plan, not for collection {cid}. The page reads the members again just before it sends: it refuses the run when a device is a member that the plan does not hold, and it does not send to a device that left the collection.");
        else impact.Add("The site receives one operation for the fixed list of devices in the plan.");
        if (change.Kind == PivotsChangeKind.RunPivot && !AdminServiceClient.IsHostName(_admin.Provider))
            checks.Add(new PreflightCheck(CheckNames.Provider, DeploymentCheckResults.Fail, AdminServiceClient.NotAHostMessage));
        if (targets.NonClientCount > 0) impact.Add($"{Count(targets.NonClientCount, "device")} without a ConfigMgr client {(targets.NonClientCount == 1 ? "is" : "are")} left out.");
        if (change.Kind == PivotsChangeKind.RunPivot)
        {
            var query = (change.QueryText ?? "").Trim();
            if (query.Length == 0) checks.Add(new PreflightCheck(CheckNames.Query, DeploymentCheckResults.Fail, "The query is empty."));
            else if (query.Length > MaxQueryLength) checks.Add(new PreflightCheck(CheckNames.Query, DeploymentCheckResults.Fail, $"The query has more than {MaxQueryLength.ToString("N0", CultureInfo.InvariantCulture)} characters."));
            else checks.Add(new PreflightCheck(CheckNames.Query, DeploymentCheckResults.Pass, "The site checks the query syntax when it receives the run. A query with an error is refused, and nothing reaches a client."));
            change = change with { QueryText = query };
            impact.Add("CMPivot reads data on each client. The query changes nothing on the client, but it runs on every device of the plan.");
        }
        else
        {
            change = await PrepareScriptRunAsync(change, checks, impact, ct).ConfigureAwait(false);
        }
        impact.Add(NoUndo);
        var every = PollInterval.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
        impact.Add(change.IsSendOnly
            ? $"After the send, the page reads the status of the devices that did not answer yet every {every} seconds, for the wait time set in Options. Stop waiting ends the wait on this page only: clients that received the request still run it."
            : $"The page waits up to {Minutes(window)} for results and reads the status of the devices that did not answer yet every {every} seconds. Stop waiting ends the wait on this page only: clients that received the request still run it.");
        impact.Add("The session (targets, the query or the script identity and hash, the parameter names with a SHA-256 hash of each value) is saved and the audit record is written before the request goes out. Parameter values are not stored. A run is refused when either cannot be written.");
        return change;
    }

    private static string Minutes(TimeSpan t) => t.TotalMinutes >= 1 && t.TotalSeconds % 60 == 0
        ? Count((int)t.TotalMinutes, "minute")
        : Count((int)t.TotalSeconds, "second");

    private static IEnumerable<PreflightCheck> TargetChecks(ResolvedTargets targets, IReadOnlyList<ResolvedDevice> clients)
    {
        if (targets.Truncated)
            yield return new PreflightCheck(CheckNames.Cap, DeploymentCheckResults.Fail, $"The target matches {Count(targets.TotalMatched, "device")}, more than the page reads. Narrow the target.");
        if (clients.Count == 0)
            yield return new PreflightCheck(CheckNames.Targets, DeploymentCheckResults.Fail, "No device of the target has a ConfigMgr client.");
        else if (clients.Count > MaxDevicesPerRun)
            yield return new PreflightCheck(CheckNames.Cap, DeploymentCheckResults.Fail,
                $"The target has {Count(clients.Count, "device")}. A run takes at most {MaxDevicesPerRun.ToString("N0", CultureInfo.InvariantCulture)} devices. Narrow the collection or the list.");
        else yield return new PreflightCheck(CheckNames.Targets, DeploymentCheckResults.Pass, $"{Count(clients.Count, "device")} with a ConfigMgr client.");
        if (targets.IsBuiltInCollection)
            yield return new PreflightCheck(CheckNames.BuiltIn, DeploymentCheckResults.Warn,
                $"{targets.CollectionId} is a collection the site creates. The run needs the box \"This runs on every device of the site\".");
        else if (targets.CoversEverySiteDevice)
            yield return new PreflightCheck(CheckNames.EveryDevice, DeploymentCheckResults.Warn,
                $"The target holds every client of the site ({Count(clients.Count, "device")}; the site has {Count(targets.SiteClientCount, "client")}). The run needs the box \"This runs on every device of the site\".");
        var offline = clients.Count(d => d.IsOnline == false);
        if (offline > 0)
            yield return new PreflightCheck(CheckNames.Offline, DeploymentCheckResults.Warn,
                $"{Count(offline, "device")} {(offline == 1 ? "is" : "are")} offline now. An offline device does not receive the request; it shows as No response.");
        if (targets.Unmatched.Count > 0)
            yield return new PreflightCheck(CheckNames.Unmatched, DeploymentCheckResults.Warn,
                $"{Count(targets.Unmatched.Count, "name")} of the list {(targets.Unmatched.Count == 1 ? "is" : "are")} not a device of the site and {(targets.Unmatched.Count == 1 ? "is" : "are")} left out: {string.Join(", ", targets.Unmatched.Take(10))}{(targets.Unmatched.Count > 10 ? ", ..." : "")}.");
    }

    private async Task<PivotsChange> PrepareScriptRunAsync(PivotsChange change, List<PreflightCheck> checks, List<string> impact, CancellationToken ct)
    {
        var script = await ReadScriptForPlanAsync(change, checks, ct).ConfigureAwait(false);
        if (script is null) return change;
        if (!script.IsApproved)
            checks.Add(new PreflightCheck(CheckNames.Approved, DeploymentCheckResults.Fail, $"The script is {script.State.ToLowerInvariant()} on the site. Only an approved script runs."));
        else checks.Add(new PreflightCheck(CheckNames.Approved, DeploymentCheckResults.Pass, $"Approved by {script.Approver}."));
        var problems = ParameterProblems(script, change.Parameters);
        checks.Add(problems.Count == 0
            ? new PreflightCheck(CheckNames.Parameters, DeploymentCheckResults.Pass, (change.Parameters?.Count ?? 0) == 0 ? "No value is sent; the script defaults apply." : $"{Count(change.Parameters!.Count, "value")} match the site's parameter definitions.")
            : new PreflightCheck(CheckNames.Parameters, DeploymentCheckResults.Fail, string.Join(" ", problems)));
        impact.Add($"Script {script.Name} ({script.Guid}), version {script.Version.ToString(CultureInfo.InvariantCulture)}, SHA-256 {script.Hash}. The page reads the script again just before it sends and refuses the run when the text changed.");
        impact.Add((change.Parameters?.Count ?? 0) == 0
            ? "Parameters: none; the script defaults apply."
            : "Parameters: " + string.Join("; ", change.Parameters!.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Key} = {p.Value}")) + ".");
        impact.Add("The script runs as the local system account on each client. Its exit code is not reported by the site: a script reads as Failed only when it stops with an error.");
        impact.Add("The run needs a second confirmation that states the device count.");
        return change with { Script = script, ScriptGuid = script.Guid, ScriptHash = script.Hash, Parameters = Normalized(change.Parameters) };
    }

    private static IReadOnlyDictionary<string, string> Normalized(IReadOnlyDictionary<string, string>? values) =>
        (values ?? new Dictionary<string, string>()).ToDictionary(p => p.Key, p => p.Value ?? "", StringComparer.Ordinal);

    /// <summary>The script of a plan read from the site with its text; null after a failed check.</summary>
    private async Task<SiteScript?> ReadScriptForPlanAsync(PivotsChange change, List<PreflightCheck> checks, CancellationToken ct)
    {
        if (!GuidRegex().IsMatch(change.ScriptGuid ?? ""))
        {
            checks.Add(new PreflightCheck(CheckNames.Script, DeploymentCheckResults.Fail, "Pick a script of the site."));
            return null;
        }
        var script = await LoadScriptAsync(change.ScriptGuid!, ct).ConfigureAwait(false);
        if (script is null)
        {
            checks.Add(new PreflightCheck(CheckNames.Script, DeploymentCheckResults.Fail, "The site holds no such script, or it belongs to a ConfigMgr feature that this page does not change."));
            return null;
        }
        if (script.Text is null)
        {
            checks.Add(new PreflightCheck(CheckNames.Script, DeploymentCheckResults.Fail, "The text of the script could not be read from the site."));
            return null;
        }
        if (!string.IsNullOrEmpty(change.ScriptHash) && !string.Equals(change.ScriptHash, script.Hash, StringComparison.OrdinalIgnoreCase))
            checks.Add(new PreflightCheck(CheckNames.Script, DeploymentCheckResults.Fail, "The script text on the site changed since the page read it. Load the script again and check the text."));
        else checks.Add(new PreflightCheck(CheckNames.Script, DeploymentCheckResults.Pass, $"Read from the site: {script.Name}, SHA-256 {script.Hash}."));
        return script;
    }

    /// <summary>
    /// The breaks of the Run Scripts rules for the values: a name the site does not define (the site
    /// drops its value), a value that is not a whole number for an integer parameter (the site drops
    /// it), a quote (a single quote fails on the client, a double quote fails at the provider), a
    /// value that is too long, and a required parameter without a value or a default.
    /// </summary>
    internal static List<string> ParameterProblems(SiteScript script, IReadOnlyDictionary<string, string>? values)
    {
        var problems = new List<string>();
        if (script.ParametersUnreadable)
        {
            problems.Add("The parameter definitions of the script on the site cannot be read.");
            return problems;
        }
        var defined = script.Parameters.GroupBy(p => p.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var (name, value) in values ?? new Dictionary<string, string>())
        {
            if (!defined.TryGetValue(name, out var p))
            {
                problems.Add(defined.Count == 0
                    ? $"The script has no parameter definitions on the site, so the site drops every value (\"{name}\")."
                    : $"The script does not define the parameter \"{name}\"; the site drops its value.");
                continue;
            }
            var v = value ?? "";
            if (v.Length > MaxParameterLength) problems.Add($"The value of {name} has more than {MaxParameterLength.ToString(CultureInfo.InvariantCulture)} characters.");
            if (v.Contains('\'') || v.Contains('‘') || v.Contains('’')) problems.Add($"The value of {name} holds a single quote, which fails on the client.");
            if (v.Contains('"') || v.Contains('“') || v.Contains('”')) problems.Add($"The value of {name} holds a double quote, which the site refuses.");
            if (p.Type == "System.Int32" && !int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) problems.Add($"{name} takes a whole number.");
            if (p.Type is not ("System.Int32" or "System.String")) problems.Add($"{name} has the type {p.Type}, which Run Scripts does not take.");
        }
        foreach (var p in script.Parameters)
            if (p.Required && string.IsNullOrEmpty(p.Default) && (values is null || !values.TryGetValue(p.Name, out var v) || string.IsNullOrEmpty(v)))
                problems.Add($"{p.Name} needs a value.");
        return problems;
    }

    private async Task<PivotsChange> PrepareSubmitAsync(PivotsChange change, List<PreflightCheck> checks, List<string> impact, CancellationToken ct)
    {
        var name = (change.SubmitName ?? "").Trim();
        if (name.Length is 0 or > 200)
            checks.Add(new PreflightCheck(CheckNames.Name, DeploymentCheckResults.Fail, "Enter a script name of 1 to 200 characters."));
        var check = await CheckScriptAsync(change.SubmitText ?? "", ct).ConfigureAwait(false);
        if (!check.Valid) checks.Add(new PreflightCheck(CheckNames.Script, DeploymentCheckResults.Fail, "The script has errors: " + string.Join(" ", check.Errors.Take(5))));
        else if (check.Problems.Count > 0) checks.Add(new PreflightCheck(CheckNames.Parameters, DeploymentCheckResults.Fail, string.Join(" ", check.Problems)));
        else checks.Add(new PreflightCheck(CheckNames.Script, DeploymentCheckResults.Pass, $"The script is valid PowerShell with {Count(check.Parameters.Count, "parameter")}."));
        if (name.Length > 0)
        {
            var existing = await LoadScriptsAsync(ct).ConfigureAwait(false);
            if (existing.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                checks.Add(new PreflightCheck(CheckNames.Name, DeploymentCheckResults.Warn, $"The site holds a script with the name {name} already. The new script gets its own GUID."));
        }
        impact.Add($"Create the script {name} on the site. It waits for approval; it does not run anywhere.");
        if (check.Parameters.Count > 0)
            impact.Add("Store the parameter definitions with it: " + string.Join(", ", check.Parameters.Select(p => $"{p.Name} ({(p.Type == "System.Int32" ? "integer" : "string")}{(p.Required ? ", required" : "")})")) + ". This is a second request; when it fails, the page names the created script and offers to remove only that script.");
        impact.Add("Undo: remove the script with Remove.");
        return change with { SubmitName = name, SubmitParameters = check.Parameters };
    }

    private async Task<PivotsChange> PrepareScriptActionAsync(PivotsChange change, List<PreflightCheck> checks, List<string> impact, CancellationToken ct)
    {
        var script = await ReadScriptForPlanAsync(change, checks, ct).ConfigureAwait(false);
        if (script is null) return change;
        var comment = (change.Comment ?? "").Trim();
        if (comment.Length > 2000) checks.Add(new PreflightCheck(CheckNames.Name, DeploymentCheckResults.Fail, "The comment has more than 2,000 characters."));
        switch (change.Kind)
        {
            case PivotsChangeKind.ApproveScript:
                if (script.IsApproved) checks.Add(new PreflightCheck(CheckNames.Approved, DeploymentCheckResults.Fail, "The script is approved already."));
                if (string.Equals(script.Author, _user(), StringComparison.OrdinalIgnoreCase))
                    checks.Add(await AuthorCheckAsync(ct).ConfigureAwait(false));
                impact.Add($"Approve the script {script.Name} ({script.Guid}), SHA-256 {script.Hash}. After approval, anyone with Run Script on a collection can run it there.");
                impact.Add("Undo: deny the script.");
                break;
            case PivotsChangeKind.DenyScript:
                if (script.State == SiteScriptStates.Declined) checks.Add(new PreflightCheck(CheckNames.Approved, DeploymentCheckResults.Fail, "The script is denied already."));
                if (string.Equals(script.Author, _user(), StringComparison.OrdinalIgnoreCase)) checks.Add(await AuthorCheckAsync(ct).ConfigureAwait(false));
                impact.Add($"Deny the script {script.Name} ({script.Guid}). A denied script does not run.");
                impact.Add("Undo: approve the script.");
                break;
            case PivotsChangeKind.RemoveScript:
                impact.Add($"Remove the script {script.Name} ({script.Guid}) from the site. Its run history on the site goes with it.");
                impact.Add("This cannot be undone. The saved sessions of this page keep their results.");
                break;
        }
        return change with { Script = script, ScriptGuid = script.Guid, ScriptHash = script.Hash, Comment = comment };
    }

    /// <summary>
    /// The author check of an approval decision: the hierarchy setting "Script authors require
    /// additional script approver" (TwoKeyApproval) decides whether the site accepts it. The page
    /// reads the setting and never changes it.
    /// </summary>
    private async Task<PreflightCheck> AuthorCheckAsync(CancellationToken ct)
    {
        int? twoKey = null;
        try
        {
            var output = await RunAsync(new ConfigMgrParameters { { "Operation", "Approval" } }, ct, ReadTimeout).ConfigureAwait(false);
            var a = Kind(output, "Approval").FirstOrDefault();
            if (a.ValueKind == System.Text.Json.JsonValueKind.Object && int.TryParse(S(a, "TwoKey"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) twoKey = v;
        }
        catch (PivotsServiceException)
        {
        }
        return twoKey switch
        {
            0 => new PreflightCheck(CheckNames.Author, DeploymentCheckResults.Warn, "You are the author of the script. The site lets authors decide on their own scripts (the hierarchy setting \"Script authors require additional script approver\" is off)."),
            1 => new PreflightCheck(CheckNames.Author, DeploymentCheckResults.Warn, "You are the author of the script, and the site requires another approver (the hierarchy setting \"Script authors require additional script approver\" is on). The site will refuse this decision; another administrator with Approve on SMS Scripts must make it."),
            _ => new PreflightCheck(CheckNames.Author, DeploymentCheckResults.Warn, "You are the author of the script. By default the site refuses a decision by the author; the page reports a refusal and does not work around it.")
        };
    }
}