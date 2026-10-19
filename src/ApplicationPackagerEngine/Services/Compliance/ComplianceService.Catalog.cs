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
        var started = _utcNow();
        var announced = new List<(string Kind, string Name)>();
        CatalogBuilderRun run;
        try
        {
            run = await _runner.RunAsync(entry, bytes, plan.Arguments, line =>
            {
                if (BuilderStep(line) is { } text) Step(text);
                if (BuilderMarker(line) is { } marker) lock (announced) announced.Add(marker);
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

        var targets = change.TargetCollectionIds ?? [];
        IReadOnlyList<string> missing = [];
        if (run.Cancelled || run.TimedOut)
        {
            List<(string Kind, string Name)> marks;
            lock (announced) marks = announced.ToList();
            (after, missing) = await ReconcileAsync(entry, plan, targets, marks, after).ConfigureAwait(false);
        }

        var claim = Claim(entry, plan.Deploys, targets, before.Info!, runStart, after);
        var (items, baselines, deployments) = (claim.Items, claim.Baselines, claim.Deployments);
        var created = deployments.Select(d => "Deployment:" + d.Id).Concat(baselines.Select(b => "Baseline:" + b.Id)).Concat(items.Select(i => "ConfigurationItem:" + i.Id)).ToList();
        var reclaim = missing.Count == 0
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>
            {
                [ReclaimStartKey] = runStart.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                [ReclaimUntilKey] = (runStart.ToUniversalTime() + (_utcNow() - started).Duration() + ReclaimLimit).ToString("o", CultureInfo.InvariantCulture),
                [ReclaimUserKey] = before.Info!.User ?? "",
                [ReclaimDeploysKey] = plan.Deploys ? "True" : "False"
            };
        var undo = created.Count == 0 && missing.Count == 0
            ? null
            : new ComplianceChange(ComplianceChangeKind.RemoveCatalogObjects, entry.Id, reclaim, targets, null, null, null, null, null, null,
                [$"Removes the {Count(deployments.Count, "deployment")}, {Count(baselines.Count, "baseline")} and {Count(items.Count, "configuration item")} that the deploy of {entry.DisplayName} created."], created);
        var made = $"{Count(items.Count, "configuration item")}, {Count(baselines.Count, "baseline")} and {Count(deployments.Count, "deployment")}";
        var unclear = claim.NotClaimed;
        var unclearText = unclear.Count == 0 ? "" : $" Not claimed as made by this run, so not part of the Undo: {string.Join("; ", unclear)}.";
        var missingText = missing.Count == 0
            ? ""
            : $" {string.Join(" ", missing.Select(m => $"A {m} may still appear: check ConfigMgr."))} {ReclaimText}";

        if (run.ExitCode != 0)
        {
            var why = run.Cancelled ? "was cancelled" : run.TimedOut ? "did not finish in time" : "stopped with an error";
            if (created.Count == 0 && missing.Count == 0)
                return Fail(change, $"The builder {why}. ConfigMgr holds no object from it.{unclearText}") with { Steps = steps.ToList() };
            return created.Count == 0
                ? Partial(change, $"The builder {why}. ConfigMgr holds no object from it yet.{missingText}{unclearText}", undo, steps.ToList())
                : Partial(change, $"The builder {why} after it made {made}. These exist now; Undo removes them.{missingText}{unclearText}", undo, steps.ToList());
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
    /// <param name="until">For a later Undo of a cancelled run: an object created after this time is not claimed.</param>
    internal static ClaimResult Claim(CatalogEntry entry, bool deploys, IReadOnlyList<string> targets, SnapshotInfo info, DateTime runStart, Snapshot after, DateTime? until = null)
    {
        var notClaimed = new List<string>();
        var earliest = runStart.ToUniversalTime() - ComplianceRules.ClockTolerance;
        HashSet<string> Known(string[]? ids) => (ids ?? []).ToHashSet(StringComparer.Ordinal);

        string? Doubt(FoundRow row, HashSet<string> known)
        {
            if (known.Contains(row.Id ?? "")) return "it existed before the run";
            if (row.Created is not { } created) return "ConfigMgr returned no creation time";
            if (created.ToUniversalTime() < earliest) return "it was created before the run";
            if (until is { } last && created.ToUniversalTime() > last.ToUniversalTime()) return "it was created after the run ended";
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
                         ?? (!deploys || !targets.Contains(d.CollectionId ?? "", StringComparer.OrdinalIgnoreCase) ? "its collection is not a target of the run" : null);
            if (reason is null) deployments.Add(d);
            else notClaimed.Add($"deployment {d.Id} to collection {d.CollectionId} ({reason})");
        }
        return new ClaimResult(items, baselines, deployments, notClaimed);
    }

    /// <summary>A plain progress line for a builder output line that marks a step; null for other lines.</summary>
    internal static string? BuilderStep(string line) => BuilderMarker(line) switch
    {
        ("Item", var item) => $"Creating configuration item {item}.",
        ("Baseline", var baseline) => $"Creating baseline {baseline}.",
        ("Deployment", var collection) => $"Deploying to collection {collection}.",
        _ => null
    };

    /// <summary>The object a builder output line says the builder starts to make: Item or Baseline with its name, Deployment with the collection name.</summary>
    internal static (string Kind, string Name)? BuilderMarker(string line)
    {
        var text = line.Trim();
        static string? After(string text, string marker) =>
            text.StartsWith(marker, StringComparison.OrdinalIgnoreCase) ? text[marker.Length..].Trim() : null;
        if (After(text, "Creating Configuration Item:") is { } item) return ("Item", item);
        if (After(text, "Creating Configuration Baseline:") is { } baseline) return ("Baseline", baseline);
        if (After(text, "Deploying baseline to:") is { } collection) return ("Deployment", collection);
        return null;
    }

    /// <summary>
    /// The SMS Provider can finish a create after the builder process is ended. After a cancel or a
    /// timeout the names are read again every <see cref="ReconcileInterval"/> until two reads in a
    /// row are equal and every object the builder started is there, for at most <see cref="ReconcileWindow"/>.
    /// </summary>
    internal static readonly TimeSpan ReconcileWindow = TimeSpan.FromSeconds(15);

    internal static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long after the end of a cancelled run a later Undo still claims an object with a name the
    /// entry makes; the other proofs of <see cref="Claim"/> apply as well.
    /// </summary>
    internal static readonly TimeSpan ReclaimLimit = TimeSpan.FromMinutes(10);

    internal const string ReclaimStartKey = "ReclaimRunStart";
    internal const string ReclaimUntilKey = "ReclaimUntil";
    internal const string ReclaimUserKey = "ReclaimUser";
    internal const string ReclaimDeploysKey = "ReclaimDeploys";

    internal const string ReclaimText =
        "Undo looks for these names again when it runs and also removes an object that this account made after the run start, up to 10 minutes after the run ended.";

    internal const string NothingReclaimedMessage = "ConfigMgr holds no object from the cancelled run now. Check ConfigMgr, and try Undo again when an object appears.";

    internal static bool IsReclaim(ComplianceChange change) => change.Values?.ContainsKey(ReclaimStartKey) == true;

    /// <summary>
    /// The objects a RemoveCatalogObjects change removes: <see cref="ComplianceChange.CreatedIds"/>, and
    /// for the Undo of a cancelled run also the objects with the entry's names that <see cref="Claim"/>
    /// gives to that run now, with its start, account and targets and the <see cref="ReclaimUntilKey"/> limit.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReclaimAsync(ComplianceChange change, CancellationToken ct)
    {
        var created = change.CreatedIds ?? [];
        if (!IsReclaim(change)) return created;
        var values = change.Values!;
        if (!DateTime.TryParse(values[ReclaimStartKey], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start)
            || !values.TryGetValue(ReclaimUntilKey, out var untilText)
            || !DateTime.TryParse(untilText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var until)
            || !values.TryGetValue(ReclaimUserKey, out var user) || string.IsNullOrWhiteSpace(user)
            || (change.CatalogId is { } id ? _catalog.Find(id) : null) is not { } entry)
            throw new ComplianceServiceException(NotPreparedMessage);
        var now = await SnapshotAsync(entry, [], ct).ConfigureAwait(false);
        var claim = Claim(entry, values.GetValueOrDefault(ReclaimDeploysKey) == "True", change.TargetCollectionIds ?? [],
            new SnapshotInfo("SnapshotInfo", null, user, [], [], []), start, now, until);
        return created
            .Concat(claim.Deployments.Select(d => "Deployment:" + d.Id))
            .Concat(claim.Baselines.Select(b => "Baseline:" + b.Id))
            .Concat(claim.Items.Select(i => "ConfigurationItem:" + i.Id))
            .Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The wait between two reads of <see cref="ReconcileAsync"/>; tests replace it.</summary>
    internal Func<TimeSpan, Task> Delay { get; set; } = t => Task.Delay(t);

    /// <returns>The last read, and "configuration item", "baseline" or "deployment" for each started object that is still missing.</returns>
    private async Task<(Snapshot After, IReadOnlyList<string> Missing)> ReconcileAsync(
        CatalogEntry entry, CatalogPlan plan, IReadOnlyList<string> targets, IReadOnlyList<(string Kind, string Name)> announced, Snapshot first)
    {
        string? TargetOf(string collectionName)
        {
            for (var i = 0; i < plan.Collections.Count && i < targets.Count; i++)
                if (string.Equals(plan.Collections[i].Name, collectionName, StringComparison.OrdinalIgnoreCase)) return targets[i];
            return null;
        }

        List<string> Missing(Snapshot s)
        {
            var missing = new List<string>();
            foreach (var (kind, name) in announced)
            {
                var present = kind switch
                {
                    "Item" => s.Items.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)),
                    "Baseline" => s.Baselines.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)),
                    _ => TargetOf(name) is { } target && s.Deployments.Any(d => string.Equals(d.CollectionId, target, StringComparison.OrdinalIgnoreCase))
                };
                if (!present) missing.Add(kind switch { "Item" => "configuration item", "Baseline" => "baseline", _ => "deployment" });
            }
            return missing;
        }

        static string Key(Snapshot s) => string.Join("|",
            s.Items.Select(i => "I" + i.Id).Concat(s.Baselines.Select(b => "B" + b.Id)).Concat(s.Deployments.Select(d => "D" + d.Id)).Order(StringComparer.Ordinal));

        var current = first;
        for (var waited = TimeSpan.Zero; waited < ReconcileWindow; waited += ReconcileInterval)
        {
            await Delay(ReconcileInterval).ConfigureAwait(false);
            Snapshot next;
            try
            {
                next = await SnapshotAsync(entry, [], CancellationToken.None).ConfigureAwait(false);
            }
            catch (ComplianceServiceException)
            {
                break;
            }
            var stable = Key(next) == Key(current);
            current = next;
            if (stable && Missing(current).Count == 0) break;
        }
        var left = Missing(current);
        _log?.Info(LogArea, $"The state after the builder was read again; {left.Count} started objects are missing.");
        return (current, left.Distinct().ToList());
    }
}
