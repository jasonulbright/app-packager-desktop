using System.Text;

namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>What the rewrite needs to know about the line it works on.</summary>
public sealed record RewriteScope(string Marker, string TitleKey, string SiteCode, string? ProviderMachine, SupersedenceSettings Settings);

/// <summary>The result of an apply or a restore: what was written, and the failures.</summary>
public sealed record RewriteOutcome(IReadOnlyList<RewriteLogEntry> Written, IReadOnlyList<string> Failures);

/// <summary>
/// Plans, applies and restores the rewrite of the version clauses of the old applications of one line. Nothing changes on the site
/// until <see cref="ApplyAsync"/> runs, and the plan that it runs comes from <see cref="PlanAsync"/> and a confirmation of the caller.
/// </summary>
public sealed class OldVersionRewriteRunner
{
    private static string Decode(string text)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(text)); }
        catch (FormatException) { return ""; }
    }

    /// <summary>The old applications of the line: every application with the marker except the newest version.</summary>
    public static IReadOnlyList<LineApplication> OldOnes(IReadOnlyList<LineApplication> line)
    {
        var readable = line.Where(a => Version.TryParse(VersionCompare.Normalize(a.SoftwareVersion), out _) && !a.IsExpired).ToList();
        if (readable.Count == 0) return [];
        // Every application that holds the newest version is current, also under another title (1.0 and 1.0.0 are the same version).
        var newest = readable.OrderByDescending(a => Version.Parse(VersionCompare.Normalize(a.SoftwareVersion))).First();
        return readable.Where(a => VersionCompare.CompareStrict(a.SoftwareVersion, newest.SoftwareVersion) < 0)
            .OrderByDescending(a => Version.Parse(VersionCompare.Normalize(a.SoftwareVersion))).ToList();
    }

    private static async Task<IReadOnlyList<LineApplication>> ReadLineAsync(RewriteScope scope, CancellationToken ct)
    {
        var lookup = await WindowsPowerShellHost.RunAsync(
            SupersedenceScripts.BuildLookupScript(scope.SiteCode, scope.ProviderMachine, scope.Marker, ""), null, ct);
        if (lookup.ExitCode != 0)
            throw new InvalidOperationException($"The site could not be read: {SupersedenceScripts.FailureText(lookup.StdOut, lookup.StdErr)}");
        return SupersedenceLookup.ParseSiteApplications(lookup.StdOut);
    }

    private static async Task<Dictionary<string, StoredDetection>> ReadDetectionsAsync(RewriteScope scope, string application, CancellationToken ct)
    {
        var read = await WindowsPowerShellHost.RunAsync(ChainCheck.BuildDetectionScript(scope.SiteCode, scope.ProviderMachine, application), null, ct);
        if (read.ExitCode != 0)
            throw new InvalidOperationException($"The detection of '{application}' could not be read: {SupersedenceScripts.FailureText(read.StdOut, read.StdErr)}");
        var detections = new Dictionary<string, StoredDetection>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in read.StdOut.Split('\n'))
        {
            var parts = raw.Trim().Split('|');
            if (parts.Length == 3 && parts[0] == ChainCheck.DetectionLine)
                detections[Decode(parts[1])] = OldVersionRewrite.ReadDetection(Decode(parts[2]));
        }
        return detections;
    }

    public async Task<RewritePlan> PlanAsync(RewriteScope scope, CancellationToken ct)
    {
        var old = OldOnes(await ReadLineAsync(scope, ct));
        var detections = new Dictionary<string, StoredDetection>(StringComparer.OrdinalIgnoreCase);
        var required = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in old)
        {
            foreach (var (name, detection) in await ReadDetectionsAsync(scope, app.Title, ct)) detections[RewritePlanner.Key(app.Title, name)] = detection;
            var deployments = await WindowsPowerShellHost.RunAsync(OldVersionRewrite.BuildDeploymentScript(scope.SiteCode, scope.ProviderMachine, app.Title), null, ct);
            if (deployments.ExitCode != 0)
                throw new InvalidOperationException($"The deployments of '{app.Title}' could not be read: {SupersedenceScripts.FailureText(deployments.StdOut, deployments.StdErr)}");
            // A script that printed no end marker did not finish its answer; the plan never reads that as "no required deployment".
            if (!OldVersionRewrite.DeploymentsComplete(deployments.StdOut))
                throw new InvalidOperationException($"The deployments of '{app.Title}' were not read to the end, so the plan does not know whether it has a required deployment.");
            required[app.Title] = OldVersionRewrite.RequiredCollections(deployments.StdOut);
        }
        return RewritePlanner.Plan(old, detections, required, (app, clause) => Guard(scope, app, clause));
    }

    /// <summary>
    /// The Exact guard for an old application: the installed value that a test install recorded for the title and the version of that
    /// old application must equal the expected value of the clause. Without a value, the clause is not rewritten.
    /// </summary>
    public static ExactGuardVerdict Guard(RewriteScope scope, LineApplication app, StoredClause clause)
    {
        var key = ExactGuard.RecordKey(scope.TitleKey, app.SoftwareVersion);
        if (scope.Settings.InstalledValues is { } values && values.TryGetValue(key, out var installed) && !string.IsNullOrWhiteSpace(installed))
        {
            if (ExactGuard.SameVersionText(installed, clause.ExpectedValue)) return ExactGuardVerdict.Allowed;
            return new ExactGuardVerdict(ClauseResult.ExactBlocked,
                $"the installed value '{installed}' differs from the expected value '{clause.ExpectedValue}'");
        }
        return new ExactGuardVerdict(ClauseResult.ExactBlocked, $"no installed value is recorded for version {app.SoftwareVersion}");
    }

    /// <summary>
    /// Writes the plan: one script for each deployment type. The log is written ahead: before a script runs, the intent is persisted, so
    /// a stop at any point (a cancel, a failure, a crash) leaves an entry for everything that the site may have changed; when the script
    /// fails, a restore entry cancels the intent at once. A failure of one type is named and the other types are still tried.
    /// </summary>
    /// <param name="persist">Appends one entry to the log and flushes it before it returns.</param>
    public async Task<RewriteOutcome> WriteRewriteAsync(RewritePlan plan, RewriteScope scope, Action<RewriteLogEntry> persist, Action<string>? log, CancellationToken ct)
    {
        var written = new List<RewriteLogEntry>();
        var failures = new List<string>();
        foreach (var item in plan.Items.Where(i => i.Rewrites))
        {
            ct.ThrowIfCancellationRequested();
            var clause = item.Clause!;
            var intent = new RewriteLogEntry(DateTimeOffset.UtcNow, item.Application, item.DeploymentType,
                OldVersionRewrite.CmdletOperator(clause.Operator), "IsEquals", clause.LogicalName);
            try
            {
                var script = OldVersionRewrite.BuildRewriteScript(scope.SiteCode, scope.ProviderMachine, item.Application, item.DeploymentType, clause, "IsEquals");
                persist(intent);
                var result = await WindowsPowerShellHost.RunAsync(script, null, ct);
                var entries = RewriteLog.FromScriptOutput(result.StdOut, DateTimeOffset.UtcNow);
                if (result.ExitCode == 0 && entries.Count > 0)
                {
                    written.Add(intent);
                    log?.Invoke($"Rewritten: {item.Application} / {item.DeploymentType}: GreaterEquals -> IsEquals");
                }
                else
                {
                    persist(intent with { At = DateTimeOffset.UtcNow, OldOperator = "IsEquals", NewOperator = "GreaterEquals", Restored = true });
                    failures.Add($"{item.Application} / {item.DeploymentType}: {SupersedenceScripts.FailureText(result.StdOut, result.StdErr)}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The log cannot be written: nothing more is changed on the site.
                failures.Add($"{item.Application} / {item.DeploymentType}: {ex.Message}");
                return new RewriteOutcome(written, failures);
            }
            catch (OperationCanceledException)
            {
                // The intent stays in the log: whether the script ran is not known, and Restore writes the old operator in either case.
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add($"{item.Application} / {item.DeploymentType}: {ex.Message}");
            }
        }
        return new RewriteOutcome(written, failures);
    }

    /// <summary>
    /// Writes the old operator back for the deployment types that the log says were rewritten and not restored. A restore entry is
    /// persisted after each success. A log entry for an application that the line no longer holds is named and left.
    /// </summary>
    public async Task<RewriteOutcome> RestoreAsync(IReadOnlyList<RewriteLogEntry> log, RewriteScope scope, Action<RewriteLogEntry> persist, Action<string>? report, CancellationToken ct)
    {
        var line = await ReadLineAsync(scope, ct);
        var titles = line.Select(a => a.Title).ToList();
        var written = new List<RewriteLogEntry>();
        var failures = new List<string>();
        foreach (var entry in RewriteLog.ToRestore(log, log.Select(e => e.Application).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
        {
            ct.ThrowIfCancellationRequested();
            if (!titles.Contains(entry.Application, StringComparer.OrdinalIgnoreCase))
            {
                failures.Add($"{entry.Application} / {entry.DeploymentType}: the application is not in this line on the site any more (renamed, removed or without the marker), so it was not restored.");
                continue;
            }
            try
            {
                var detections = await ReadDetectionsAsync(scope, entry.Application, ct);
                if (!detections.TryGetValue(entry.DeploymentType, out var current) || current.Clause is null)
                {
                    failures.Add($"{entry.Application} / {entry.DeploymentType}: the detection is not a single version clause any more, so it was not restored.");
                    continue;
                }
                var script = OldVersionRewrite.BuildRewriteScript(scope.SiteCode, scope.ProviderMachine, entry.Application, entry.DeploymentType,
                    current.Clause, OldVersionRewrite.CmdletOperator(entry.OldOperator));
                var result = await WindowsPowerShellHost.RunAsync(script, null, ct);
                var read = RewriteLog.FromScriptOutput(result.StdOut, DateTimeOffset.UtcNow);
                if (result.ExitCode == 0 && read.Count > 0)
                {
                    var done = read[0] with { Restored = true };
                    persist(done);
                    written.Add(done);
                    report?.Invoke($"Restored: {entry.Application} / {entry.DeploymentType}: back to {entry.OldOperator}");
                }
                else failures.Add($"{entry.Application} / {entry.DeploymentType}: {SupersedenceScripts.FailureText(result.StdOut, result.StdErr)}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add($"{entry.Application} / {entry.DeploymentType}: {ex.Message}");
            }
        }
        return new RewriteOutcome(written, failures);
    }
}