using System.Globalization;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

public sealed partial class ComplianceService
{
    private async Task<ComplianceOutcome> ApplyCatalogAsync(ComplianceChange change, IProgress<ComplianceOutcome> progress, CancellationToken ct)
    {
        CatalogPlan plan;
        try
        {
            plan = CheckCatalogValues(change);
        }
        catch (ComplianceServiceException ex)
        {
            return Refused(change, ex.Message);
        }
        var entry = plan.Entry;
        if (!plan.DetectOnly && !change.RemediationConfirmed) return Refused(change, RemediationNotConfirmedMessage);
        if (plan.Collections.Count != (change.TargetCollectionIds?.Count ?? 0)) return Refused(change, NotPreparedMessage);
        var bytes = _builderBytes(entry);
        if (bytes is null) return Refused(change, "The builder of this catalog entry is missing from the application, so nothing was changed.");

        var steps = new List<string>();
        void Step(string line)
        {
            lock (steps)
            {
                steps.Add(line);
                progress.Report(new ComplianceOutcome(change, true, line, null, steps.ToList(), InProgress: true));
            }
        }

        // The state is read again right before the run: a name taken since the plan would make the
        // builder change the existing objects (see RerunMessage).
        Snapshot before;
        try
        {
            before = await SnapshotAsync(entry, plan.Collections.Select(c => c.Name), CancellationToken.None, withIds: true).ConfigureAwait(false);
        }
        catch (ComplianceServiceException ex)
        {
            return Refused(change, ex.Message);
        }
        if (before.Info?.Now is not { } runStart) return Refused(change, "ConfigMgr returned data that could not be read. The log has the details.");
        if (before.Items.Count + before.Baselines.Count > 0)
            return Refused(change, $"A configuration item or baseline with a name this entry creates exists now. {RerunMessage}");
        for (var i = 0; i < plan.Collections.Count; i++)
        {
            var (parameter, name) = plan.Collections[i];
            var matches = before.Collections.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1 || !string.Equals(matches[0].Id, change.TargetCollectionIds![i], StringComparison.OrdinalIgnoreCase) || matches[0].CollectionType != 2)
                return Refused(change, $"Collection {name} ({parameter}) changed since the plan was shown, so nothing was changed. Refresh and try again.");
            if (IsBuiltInCollection(matches[0].Id))
                return Refused(change, $"Collection {Shown(matches[0].Name, matches[0].Id)} is a built-in collection. Select a collection that holds the devices to evaluate.");
        }
        Step($"Checked the site: no object with these names exists; {Count(plan.Collections.Count, "target collection")} found.");

        Step($"Started the builder of {entry.DisplayName}.");
        CatalogBuilderRun run;
        try
        {
            run = await _runner.RunAsync(entry, bytes, plan.Arguments, line =>
            {
                if (BuilderStep(line) is { } text) Step(text);
            }, BuilderTimeout, ct).ConfigureAwait(false);
        }
        catch (CatalogBuilderRefusedException ex)
        {
            return Refused(change, ex.Message + " Nothing was changed.") with { Steps = steps.ToList() };
        }
        catch (OperationCanceledException)
        {
            run = new CatalogBuilderRun(null, false, true, []);
        }
        _log?.Info(LogArea, $"A catalog builder ended: exit {(run.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none")}, timed out {run.TimedOut}, cancelled {run.Cancelled}.");
        Step(run.Cancelled ? "The builder was cancelled; its process tree was ended."
            : run.TimedOut ? "The builder did not finish in time; its process tree was ended."
            : run.ExitCode == 0 ? "The builder finished." : "The builder stopped with an error.");

        Snapshot after;
        try
        {
            after = await SnapshotAsync(entry, [], CancellationToken.None).ConfigureAwait(false);
        }
        catch (ComplianceServiceException)
        {
            return Fail(change,
                $"The result of the catalog deploy is not known: ConfigMgr could not be read after the builder. Check in ConfigMgr whether {string.Join(", ", entry.ProducesConfigurationItems.Concat(entry.ProducesBaselines))} exist. Undo is not available.") with { Steps = steps.ToList() };
        }

        var claim = Claim(entry, plan, change.TargetCollectionIds ?? [], before.Info!, runStart, after);
        var (items, baselines, deployments) = (claim.Items, claim.Baselines, claim.Deployments);
        var created = deployments.Select(d => "Deployment:" + d.Id).Concat(baselines.Select(b => "Baseline:" + b.Id)).Concat(items.Select(i => "ConfigurationItem:" + i.Id)).ToList();
        var undo = created.Count == 0
            ? null
            : new ComplianceChange(ComplianceChangeKind.RemoveCatalogObjects, entry.Id, new Dictionary<string, string>(), [], null, null, null, null, null, null,
                [$"Removes the {Count(deployments.Count, "deployment")}, {Count(baselines.Count, "baseline")} and {Count(items.Count, "configuration item")} that the deploy of {entry.DisplayName} created."], created);
        var made = $"{Count(items.Count, "configuration item")}, {Count(baselines.Count, "baseline")} and {Count(deployments.Count, "deployment")}";
        var unclear = claim.NotClaimed;
        var unclearText = unclear.Count == 0 ? "" : $" Not claimed as made by this run, so not part of the Undo: {string.Join("; ", unclear)}.";

        if (run.ExitCode != 0)
        {
            var why = run.Cancelled ? "was cancelled" : run.TimedOut ? "did not finish in time" : "stopped with an error";
            return created.Count == 0
                ? Fail(change, $"The builder {why}. ConfigMgr holds no object from it.{unclearText}") with { Steps = steps.ToList() }
                : Partial(change, $"The builder {why} after it made {made}. These exist now; Undo removes them.{unclearText}", undo, steps.ToList());
        }

        var tagFailures = new List<string>();
        foreach (var baseline in baselines)
        {
            var tagged = DriftTag.Apply(baseline.Description, entry.Id, entry.Version);
            if (tagged.Length > DriftTag.MaxDescriptionLength)
            {
                tagFailures.Add(baseline.Name ?? "");
                continue;
            }
            var tagRun = await RunWriteAsync(change, new ConfigMgrParameters
            {
                { "Operation", "Tag" }, { "BaselineId", long.Parse(baseline.Id!, CultureInfo.InvariantCulture) },
                { "ExpectedDescription", baseline.Description ?? "" }, { "NewDescription", tagged }
            }).ConfigureAwait(false);
            if (tagRun.Result?.Status == "ok") Step($"Wrote the drift tag {DriftTag.Text(entry.Id, entry.Version)} to baseline {baseline.Name}.");
            else tagFailures.Add(baseline.Name ?? "");
        }

        var expectedDeployments = plan.Deploys ? plan.Collections.Count : 0;
        var complete = items.Count == entry.ProducesConfigurationItems.Count && baselines.Count == entry.ProducesBaselines.Count
                       && deployments.Count == expectedDeployments;
        if (!complete)
            return Partial(change, $"The builder finished, but ConfigMgr holds {made} where the entry makes {Count(entry.ProducesConfigurationItems.Count, "configuration item")}, {Count(entry.ProducesBaselines.Count, "baseline")} and {Count(expectedDeployments, "deployment")}.{unclearText} Undo removes what this run made.", undo, steps.ToList());
        if (tagFailures.Count > 0)
            return Partial(change, $"Deployed {entry.DisplayName} ({made}), but the drift tag was not written to baseline {string.Join(", ", tagFailures)}. Undo removes what this run made.{unclearText}", undo, steps.ToList());
        return Done(change, $"Deployed {entry.DisplayName} {entry.Version}: {made}, {(plan.DetectOnly ? "detect only" : "remediate")}. Undo removes them.{unclearText}", undo, steps.ToList());
    }

    internal sealed record ClaimResult(List<FoundRow> Items, List<FoundRow> Baselines, List<FoundRow> Deployments, List<string> NotClaimed);

    /// <summary>
    /// The objects that the run provably made: an ID that was not there before the run, a name the
    /// entry produces (a deployment: a claimed baseline and a target of the run), a creation time
    /// not before the run start less <see cref="ComplianceRules.ClockTolerance"/>, and the account
    /// of the run as creator when ConfigMgr names one. Every other object with such a name, and every
    /// other deployment of a claimed baseline, is named in <see cref="ClaimResult.NotClaimed"/>.
    /// </summary>
    internal static ClaimResult Claim(CatalogEntry entry, CatalogPlan plan, IReadOnlyList<string> targets, SnapshotInfo info, DateTime runStart, Snapshot after)
    {
        var notClaimed = new List<string>();
        var earliest = runStart.ToUniversalTime() - ComplianceRules.ClockTolerance;
        HashSet<string> Known(string[]? ids) => (ids ?? []).ToHashSet(StringComparer.Ordinal);

        string? Doubt(FoundRow row, HashSet<string> known)
        {
            if (known.Contains(row.Id ?? "")) return "it existed before the run";
            if (row.Created is not { } created) return "ConfigMgr returned no creation time";
            if (created.ToUniversalTime() < earliest) return "it was created before the run";
            if (!string.IsNullOrEmpty(row.CreatedBy) && !string.IsNullOrEmpty(info.User) && !string.Equals(row.CreatedBy, info.User, StringComparison.OrdinalIgnoreCase))
                return "another account made or changed it";
            return null;
        }

        List<FoundRow> Owned(IReadOnlyList<FoundRow> found, IReadOnlyList<string> names, HashSet<string> known, string noun)
        {
            var owned = new List<FoundRow>();
            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var candidates = new List<FoundRow>();
                foreach (var row in found.Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    if (Doubt(row, known) is { } reason) notClaimed.Add($"{noun} {Shown(row.Name, row.Id)} ({reason})");
                    else candidates.Add(row);
                }
                if (candidates.Count == 1) owned.Add(candidates[0]);
                else notClaimed.AddRange(candidates.Select(c => $"{noun} {Shown(c.Name, c.Id)} (more than one object has this name)"));
            }
            return owned;
        }

        var items = Owned(after.Items, entry.ProducesConfigurationItems, Known(info.ItemIds), "configuration item");
        var baselines = Owned(after.Baselines, entry.ProducesBaselines, Known(info.BaselineIds), "baseline");
        var baselineIds = baselines.Select(b => b.Id ?? "").ToHashSet(StringComparer.Ordinal);
        var knownDeployments = Known(info.DeploymentIds);
        var deployments = new List<FoundRow>();
        foreach (var d in after.Deployments.Where(d => baselineIds.Contains(d.BaselineId ?? "")))
        {
            var reason = Doubt(d, knownDeployments)
                         ?? (!plan.Deploys || !targets.Contains(d.CollectionId ?? "", StringComparer.OrdinalIgnoreCase) ? "its collection is not a target of the run" : null);
            if (reason is null) deployments.Add(d);
            else notClaimed.Add($"deployment {d.Id} to collection {d.CollectionId} ({reason})");
        }
        return new ClaimResult(items, baselines, deployments, notClaimed);
    }

    /// <summary>A plain progress line for a builder output line that marks a step; null for other lines.</summary>
    internal static string? BuilderStep(string line)
    {
        var text = line.Trim();
        static string? After(string text, string marker) =>
            text.StartsWith(marker, StringComparison.OrdinalIgnoreCase) ? text[marker.Length..].Trim() : null;
        if (After(text, "Creating Configuration Item:") is { } item) return $"Creating configuration item {item}.";
        if (After(text, "Creating Configuration Baseline:") is { } baseline) return $"Creating baseline {baseline}.";
        if (After(text, "Deploying baseline to:") is { } collection) return $"Deploying to collection {collection}.";
        return null;
    }
}
