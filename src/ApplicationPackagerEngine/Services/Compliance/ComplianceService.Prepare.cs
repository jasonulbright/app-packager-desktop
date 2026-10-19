using System.Globalization;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Services;

public sealed partial class ComplianceService
{
    /// <summary>The Values key that carries the CI revision a setting change was planned against.</summary>
    public const string RevisionKey = "CiRevision";

    /// <summary>The Values key that says how an Edit is written: InPlace keeps the setting ID, Replace gives a new one.</summary>
    public const string EditModeKey = "EditMode";

    /// <summary>The Values key of a CatalogDeploy that asks for a missing dependency to be created.</summary>
    public const string CreateDependencyKey = "CreateDependency";

    internal const string CreateDependencyMessage =
        "No shipped builder creates a collection that a catalog entry depends on. Create the collection on the Collections tab (the plan and Undo there apply), then deploy the entry.";

    internal sealed record CatalogPlan(
        CatalogEntry Entry, IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Arguments,
        IReadOnlyList<(string Parameter, string Name)> Collections, bool DetectOnly, bool Deploys);

    public async Task<ComplianceChange> PrepareAsync(ComplianceChange request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var impact = (request.Impact ?? []).ToList();
        switch (request.Kind)
        {
            case ComplianceChangeKind.CatalogDeploy:
                return await PrepareCatalogAsync(request, impact, ct).ConfigureAwait(false);
            case ComplianceChangeKind.SetRemediation:
            {
                var current = await RequireDeploymentAsync(request, ct).ConfigureAwait(false);
                if (request.RemediateAfter is not { } after) throw new ComplianceServiceException("Select remediation on or off.");
                if (after == current.RemediateNoncompliant)
                    throw new ComplianceServiceException($"Remediation is already {(after ? "on" : "off")} for {Deployment(current)}.");
                var members = await MembersAsync(current.CollectionId, ct).ConfigureAwait(false);
                impact.Add($"Sets {Deployment(current)} from {Mode(current.RemediateNoncompliant)} to {Mode(after)}.");
                if (after)
                    impact.Add($"Remediation changes the settings of baseline {current.BaselineName} on every device of collection {Shown(current.CollectionName, current.CollectionId)} ({Count(members, "member")} now) at the next evaluation.");
                impact.Add($"Undo sets {Mode(current.RemediateNoncompliant)} again.");
                return request with { DeploymentBefore = current, Impact = impact };
            }
            case ComplianceChangeKind.Redeploy:
            {
                var current = await RequireDeploymentAsync(request, ct).ConfigureAwait(false);
                impact.Add($"Creates {Deployment(current)} again with its settings ({Mode(current.RemediateNoncompliant)}, {current.Schedule}), then removes the old deployment.");
                impact.Add("The new deployment is made first, so the collection is never without the deployment. It has a new ID.");
                impact.Add("The old deployment is removed only when the new one holds the captured settings; otherwise both stay and the result says so.");
                if (current.RemediateNoncompliant) impact.Add("The new deployment remediates noncompliant settings, as the old one does.");
                if (OverrideLost(current) is { } lost) impact.Add(lost);
                impact.Add("Undo is not applicable after a full redeploy: the deployment holds the same settings with a new ID. When both deployments stay, Undo removes the new one.");
                return request with { DeploymentBefore = current, Impact = impact };
            }
            case ComplianceChangeKind.Reassign:
            {
                var current = await RequireDeploymentAsync(request, ct).ConfigureAwait(false);
                var to = CheckedCollectionId(request.ToCollectionId, "Select the collection to move the deployment to.");
                if (string.Equals(to, current.CollectionId, StringComparison.OrdinalIgnoreCase))
                    throw new ComplianceServiceException("Select another collection than the one the deployment targets now.");
                var target = await RequireTargetCollectionAsync(to, ct, allowBuiltIn: Restores(request)).ConfigureAwait(false);
                var existing = await ReadDeploymentsAsync(new ConfigMgrParameters { { "Operation", "Deployments" }, { "BaselineId", long.Parse(current.BaselineId, CultureInfo.InvariantCulture) } }, ct).ConfigureAwait(false);
                if (existing.Any(d => string.Equals(d.CollectionId, to, StringComparison.OrdinalIgnoreCase)))
                    throw new ComplianceServiceException($"Baseline {current.BaselineName} is already deployed to collection {Shown(target.Name, to)}.");
                impact.Add($"Deploys baseline {Shown(current.BaselineName, current.BaselineId)} to collection {Shown(target.Name, to)} ({Count(target.Members, "member")} now) with the same settings ({Mode(current.RemediateNoncompliant)}, {current.Schedule}).");
                impact.Add($"Then removes the deployment to collection {Shown(current.CollectionName, current.CollectionId)}. If the new deployment fails or does not hold the captured settings, the old one stays.");
                if (OverrideLost(current) is { } lost) impact.Add(lost);
                if (IsBuiltInCollection(current.CollectionId))
                    impact.Add($"Collection {Shown(current.CollectionName, current.CollectionId)} is a built-in collection. Undo moves the deployment back to it.");
                impact.Add("Undo moves the deployment back. The deployments have new IDs.");
                return request with { DeploymentBefore = current, ToCollectionId = to, Impact = impact };
            }
            case ComplianceChangeKind.DeleteDeployment:
            {
                var current = await RequireDeploymentAsync(request, ct).ConfigureAwait(false);
                impact.Add($"Removes {Deployment(current)}.");
                impact.Add(current.CountsKnown
                    ? $"Compliance now: {current.Compliant} compliant, {current.NonCompliant} non-compliant, {current.Error} error, {current.Unknown} unknown."
                    : "Compliance now: not known (ConfigMgr has no compliance summary for the deployment).");
                impact.Add("Undo creates the deployment again with the same settings. The new deployment has a new ID; the compliance history of the old one is not restored.");
                if (IsBuiltInCollection(current.CollectionId))
                    impact.Add($"Collection {Shown(current.CollectionName, current.CollectionId)} is a built-in collection. Undo deploys the baseline to it again; new deployments to it are not offered.");
                if (OverrideLost(current) is { } lost) impact.Add(lost);
                return request with { DeploymentBefore = current, Impact = impact };
            }
            case ComplianceChangeKind.CreateDeployment:
            {
                var row = request.DeploymentBefore ?? throw new ComplianceServiceException("The deployment to create is missing.");
                CheckedNumber(row.BaselineId, "Select a baseline.");
                var target = await RequireTargetCollectionAsync(CheckedCollectionId(row.CollectionId, "Select a collection."), ct, allowBuiltIn: IsCaptured(row)).ConfigureAwait(false);
                impact.Add($"Deploys baseline {Shown(row.BaselineName, row.BaselineId)} to collection {Shown(target.Name, target.Id)} ({Count(target.Members, "member")} now), {Mode(row.RemediateNoncompliant)}, {row.Schedule}.");
                impact.Add("Undo removes the new deployment.");
                return request with { Impact = impact };
            }
            case ComplianceChangeKind.AddSetting:
            case ComplianceChangeKind.EditSetting:
            case ComplianceChangeKind.RemoveSetting:
                return await PrepareSettingAsync(request, impact, ct).ConfigureAwait(false);
            case ComplianceChangeKind.RemoveCatalogObjects:
            {
                var ids = await ReclaimAsync(request, ct).ConfigureAwait(false);
                var (deployments, baselines, items) = SplitCreated(ids);
                if (deployments.Count + baselines.Count + items.Count == 0)
                    throw new ComplianceServiceException(IsReclaim(request) ? NothingReclaimedMessage : "There is nothing to remove.");
                if ((request.CatalogId is { } catalogId ? _catalog.Find(catalogId) : null) is { } entry
                    && (await SnapshotAsync(entry, [], ct, withIds: true).ConfigureAwait(false)).Info is { } now)
                {
                    deployments = deployments.Where(id => (now.DeploymentIds ?? []).Contains(id, StringComparer.Ordinal)).ToList();
                    baselines = baselines.Where(id => (now.BaselineIds ?? []).Contains(id, StringComparer.Ordinal)).ToList();
                    items = items.Where(id => (now.ItemIds ?? []).Contains(id, StringComparer.Ordinal)).ToList();
                }
                impact.Clear();
                impact.Add($"Removes {Count(deployments.Count, "deployment")}, {Count(baselines.Count, "baseline")} and {Count(items.Count, "configuration item")} that the catalog deploy created, in that order.");
                if (IsReclaim(request)) impact.Add(ReclaimText);
                impact.Add("Objects that existed before the deploy are not touched. An object that something else uses now is not removed; the result names it.");
                impact.Add("Undo is not available. Deploy the catalog entry again instead.");
                return request with { CreatedIds = ids, Impact = impact };
            }
            default:
                throw new ComplianceServiceException("The change kind is not known.");
        }
    }

    // ---- Catalog --------------------------------------------------------------------------------

    /// <summary>The builder parameter values with the site of the connection, and the reasons they are refused.</summary>
    internal CatalogPlan CheckCatalogValues(ComplianceChange request)
    {
        var entry = (request.CatalogId is { } id ? _catalog.Find(id) : null)
            ?? throw new ComplianceServiceException("Select a catalog entry.");
        if (request.Values?.ContainsKey(CreateDependencyKey) == true)
            throw new ComplianceServiceException(CreateDependencyMessage);
        var connection = _connection();
        var values = new Dictionary<string, string>(request.Values ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        var site = connection.NormalizedSiteCode;
        var server = connection.NormalizedProvider.Length > 0 ? connection.NormalizedProvider : Environment.MachineName;
        if (site.Length == 0) throw new ComplianceServiceException($"Set the site code in {ConfigMgrSessionHost.SettingsLocation} first.");
        foreach (var (name, value) in new[] { ("SiteCode", site), ("SiteServer", server) })
        {
            if (entry.Parameters.All(p => p.Name != name)) continue;
            if (values.TryGetValue(name, out var given) && given.Length > 0 && !string.Equals(given, value, StringComparison.OrdinalIgnoreCase))
                throw new ComplianceServiceException($"{name} must be the site of the connection ({value}). The builder runs against that site only.");
            values[name] = value;
        }
        var (arguments, errors) = CatalogArguments.Build(entry, values);
        if (errors.Count > 0) throw new ComplianceServiceException(string.Join(" ", errors));
        var detectOnly = entry.Parameters.Any(p => p.Name == "DetectOnly") && CatalogArguments.IsOn(CatalogArguments.Effective(entry.Parameters.First(p => p.Name == "DetectOnly"), values));
        var skip = entry.Parameters.FirstOrDefault(p => p.Name == "SkipDeploy") is { } s && CatalogArguments.IsOn(CatalogArguments.Effective(s, values));
        var collections = skip
            ? []
            : entry.CollectionParameters.Select(p => (p, CatalogArguments.Effective(entry.Parameters.First(x => x.Name == p), values) ?? ""))
                .Where(c => c.Item2.Length > 0).ToList();
        var twice = collections.GroupBy(c => c.Item2, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (twice is not null)
            throw new ComplianceServiceException($"{string.Join(" and ", twice.Select(c => c.Item1))} name the same collection {twice.Key}. The baselines of this entry need different collections.");
        return new CatalogPlan(entry, values, arguments, collections, detectOnly, collections.Count > 0);
    }

    private async Task<ComplianceChange> PrepareCatalogAsync(ComplianceChange request, List<string> impact, CancellationToken ct)
    {
        var plan = CheckCatalogValues(request);
        var entry = plan.Entry;
        var dependencies = entry.Dependencies
            .Where(d => d.Type == "Collection")
            .Where(d => !entry.Parameters.Any(p => entry.CollectionParameters.Contains(p.Name) && p.Default == d.Name
                                                  && CatalogArguments.Effective(p, plan.Values) != d.Name))
            .ToList();
        var snapshot = await SnapshotAsync(entry, plan.Collections.Select(c => c.Name).Concat(dependencies.Select(d => d.Name)), ct).ConfigureAwait(false);

        var existing = snapshot.Items.Select(i => $"configuration item {Shown(i.Name, i.Id)}")
            .Concat(snapshot.Baselines.Select(b => $"baseline {Shown(b.Name, b.Id)}")).ToList();
        if (existing.Count > 0)
            throw new ComplianceServiceException($"ConfigMgr already holds {string.Join(", ", existing)}. {RerunMessage}");

        var targets = new List<FoundRow>();
        foreach (var (parameter, name) in plan.Collections)
            targets.Add(RequireNamedCollection(snapshot.Collections, name, parameter));
        foreach (var dependency in dependencies)
        {
            if (snapshot.Collections.Any(c => string.Equals(c.Name, dependency.Name, StringComparison.OrdinalIgnoreCase) && c.CollectionType == 2)) continue;
            throw new ComplianceServiceException(
                $"Collection {dependency.Name} must exist before entry {entry.DisplayName} is deployed. No shipped builder creates it: create it on the Collections tab, or select another collection.");
        }

        impact.Add($"Runs the builder of catalog entry {entry.DisplayName} ({entry.Id} {entry.Version}) against site {plan.Values.GetValueOrDefault("SiteCode", "")}.");
        impact.Add($"Creates configuration {(entry.ProducesConfigurationItems.Count == 1 ? "item" : "items")} {string.Join(", ", entry.ProducesConfigurationItems)}.");
        impact.Add($"Creates {(entry.ProducesBaselines.Count == 1 ? "baseline" : "baselines")} {string.Join(", ", entry.ProducesBaselines)}.");
        impact.Add("No configuration item or baseline with these names exists now.");
        if (plan.Deploys)
            foreach (var (target, (parameter, _)) in targets.Zip(plan.Collections))
                impact.Add($"Deploys to collection {Shown(target.Name, target.Id)} ({Count(target.Members, "member")} now), evaluated every day ({parameter}).");
        else
            impact.Add("Creates no deployment.");
        if (plan.DetectOnly)
            impact.Add("Mode: detect only. Clients report compliance and change nothing.");
        else
        {
            impact.Add("Mode: REMEDIATE. Clients change these registry values to the catalog values at each evaluation:");
            foreach (var surface in entry.RegistrySurface)
                impact.Add($@"  {surface.Hive}\{surface.KeyPath}: {string.Join(", ", surface.Values)}");
            impact.Add("Remediation needs a second, separate confirmation.");
        }
        impact.Add(entry.RebootRequired ? "The settings take effect after the devices restart." : "No restart is needed.");
        impact.Add($"Adds the drift tag {DriftTag.Text(entry.Id, entry.Version)} to the description of each baseline.");
        impact.Add("Undo removes the deployments, baselines and configuration items this run creates. Objects that existed before are not touched.");
        return request with { Values = plan.Values, TargetCollectionIds = targets.Select(t => t.Id ?? "").ToList(), Impact = impact };
    }

    private static FoundRow RequireNamedCollection(IReadOnlyList<FoundRow> found, string name, string parameter)
    {
        var matches = found.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) throw new ComplianceServiceException($"Collection {name} ({parameter}) does not exist. Create it first or select another collection.", ComplianceFailureKind.NotFound);
        if (matches.Count > 1) throw new ComplianceServiceException($"More than one collection is named {name}. The deployment cmdlet would not know which one to use.");
        var c = matches[0];
        if (c.CollectionType != 2) throw new ComplianceServiceException($"Collection {Shown(c.Name, c.Id)} is not a device collection. Baselines from the catalog target devices.");
        if (IsBuiltInCollection(c.Id)) throw new ComplianceServiceException($"Collection {Shown(c.Name, c.Id)} is a built-in collection. Select a collection that holds the devices to evaluate.");
        return c;
    }

    /// <param name="Info">With the IDs of every CI revision, baseline and baseline deployment, the clock and the account; null unless asked for.</param>
    internal sealed record Snapshot(IReadOnlyList<FoundRow> Items, IReadOnlyList<FoundRow> Baselines, IReadOnlyList<FoundRow> Deployments, IReadOnlyList<FoundRow> Collections, SnapshotInfo? Info = null);

    private async Task<Snapshot> SnapshotAsync(CatalogEntry entry, IEnumerable<string> collectionNames, CancellationToken ct, bool withIds = false)
    {
        var parameters = new ConfigMgrParameters
        {
            { "Operation", "Snapshot" },
            { "ItemNames", entry.ProducesConfigurationItems.ToArray() },
            { "BaselineNames", entry.ProducesBaselines.ToArray() },
            { "CollectionNames", collectionNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() }
        };
        if (withIds) parameters["WithIds"] = true;
        var output = await ReadAsync(parameters, ct).ConfigureAwait(false);
        return new Snapshot(Rows<FoundRow>(output, "FoundItem"), Rows<FoundRow>(output, "FoundBaseline"), Rows<FoundRow>(output, "FoundDeployment"), Rows<FoundRow>(output, "FoundCollection"),
            withIds ? Rows<SnapshotInfo>(output, "SnapshotInfo").FirstOrDefault() : null);
    }

    // ---- Deployments ----------------------------------------------------------------------------

    private async Task<BaselineDeploymentRow> RequireDeploymentAsync(ComplianceChange request, CancellationToken ct)
    {
        var before = request.DeploymentBefore ?? throw new ComplianceServiceException("Select a deployment.");
        var id = CheckedNumber(before.DeploymentId, "Select a deployment.");
        var current = (await ReadDeploymentsAsync(new ConfigMgrParameters { { "Operation", "Deployments" }, { "AssignmentId", long.Parse(id, CultureInfo.InvariantCulture) } }, ct).ConfigureAwait(false))
            .FirstOrDefault()
            ?? throw new ComplianceServiceException($"{Capital(Deployment(before))} does not exist any more. Refresh the list.", ComplianceFailureKind.NotFound);
        if (before.BaselineId.Length > 0 && (current.BaselineId != before.BaselineId
                                             || !string.Equals(current.CollectionId, before.CollectionId, StringComparison.OrdinalIgnoreCase)
                                             || current.RemediateNoncompliant != before.RemediateNoncompliant
                                             || (before.Settings is { } s && current.Settings is { } c && !SameSettings(s, c))))
            throw new ComplianceServiceException($"{Capital(Deployment(before))} changed since it was read. Refresh the list and try again.", ComplianceFailureKind.Stale);
        return current;
    }

    /// <summary>A plan line when a re-create cannot keep the captured maintenance window override; null otherwise.</summary>
    private static string? OverrideLost(BaselineDeploymentRow row) =>
        row.Settings is { OverrideServiceWindow: true } && !row.RemediateNoncompliant
            ? "The deployment allows remediation outside maintenance windows while remediation is off. ConfigMgr does not store that on a new deployment without remediation, so the new deployment has it off; it has no effect while remediation is off."
            : null;

    /// <summary>True when the two hold the same stored deployment values; the alert values count only while there is an alert.</summary>
    internal static bool SameSettings(BaselineDeploymentSettings a, BaselineDeploymentSettings b) =>
        string.Equals(a.ScheduleToken, b.ScheduleToken, StringComparison.OrdinalIgnoreCase)
        && a.OverrideServiceWindow == b.OverrideServiceWindow
        && a.GenerateAlert == b.GenerateAlert
        && (!a.GenerateAlert || (a.AlertThresholdPercent == b.AlertThresholdPercent && a.MonitoredByScom == b.MonitoredByScom && UtcText(a.AlertAfter) == UtcText(b.AlertAfter)));

    private async Task<FoundRow> RequireTargetCollectionAsync(string collectionId, CancellationToken ct, bool allowBuiltIn = false)
    {
        var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "Collection" }, { "CollectionId", collectionId } }, ct).ConfigureAwait(false);
        var found = Rows<FoundRow>(output, "FoundCollection").FirstOrDefault()
            ?? throw new ComplianceServiceException($"Collection {collectionId} does not exist. Refresh the list.", ComplianceFailureKind.NotFound);
        if (found.CollectionType != 2) throw new ComplianceServiceException($"Collection {Shown(found.Name, found.Id)} is not a device collection.");
        if (IsBuiltInCollection(found.Id) && !allowBuiltIn) throw new ComplianceServiceException($"Collection {Shown(found.Name, found.Id)} is a built-in collection. Select a collection that holds the devices to evaluate.");
        return found;
    }

    private async Task<int> MembersAsync(string collectionId, CancellationToken ct)
    {
        if (!CollectionIdRegex().IsMatch(collectionId ?? "")) return 0;
        var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "Collection" }, { "CollectionId", collectionId } }, ct).ConfigureAwait(false);
        return Rows<FoundRow>(output, "FoundCollection").FirstOrDefault()?.Members ?? 0;
    }

    // ---- Settings -------------------------------------------------------------------------------

    private async Task<ComplianceChange> PrepareSettingAsync(ComplianceChange request, List<string> impact, CancellationToken ct)
    {
        var ciId = CheckedNumber(request.CiId, "Select a configuration item.");
        var (item, settings, remediating) = await ReadItemAsync(ciId, ct).ConfigureAwait(false);
        if (item is null) throw new ComplianceServiceException("The configuration item does not exist any more. Refresh the list.", ComplianceFailureKind.NotFound);
        var ci = Shown(item.Name, item.CiId);
        var values = new Dictionary<string, string>(request.Values ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            [RevisionKey] = item.Revision.ToString(CultureInfo.InvariantCulture)
        };
        CiSetting? before = null;
        if (request.Kind != ComplianceChangeKind.AddSetting)
        {
            var wanted = request.SettingBefore ?? throw new ComplianceServiceException("Select a setting.");
            before = settings.FirstOrDefault(s => s.SettingId == wanted.SettingId)
                     ?? throw new ComplianceServiceException($"Setting {wanted.Name} does not exist on configuration item {ci} any more. Refresh the list.", ComplianceFailureKind.NotFound);
            if (!SettingRules.Same(before, wanted))
                throw new ComplianceServiceException($"Setting {wanted.Name} on configuration item {ci} changed since it was read. Refresh the list and try again.", ComplianceFailureKind.Stale);
            if (!before.IsEditable)
                throw new ComplianceServiceException($"Setting {before.Name} is not a registry or script setting with one value rule. This page does not change it, because Undo could not create it again.");
            if (SettingRules.Check(before) is { } unrestorable)
                throw new ComplianceServiceException($"Undo could not create setting {before.Name} again as it is now, so it is not changed here. {unrestorable}");
            if (settings.Count(s => string.Equals(s.Name, before.Name, StringComparison.OrdinalIgnoreCase)) > 1)
                throw new ComplianceServiceException($"More than one setting on configuration item {ci} is named {before.Name}. The ConfigMgr cmdlet would change or remove all of them; rename them in the console first.");
        }
        CiSetting? after = null;
        if (request.Kind != ComplianceChangeKind.RemoveSetting)
        {
            after = request.SettingAfter ?? throw new ComplianceServiceException("Enter the setting.");
            if (SettingRules.Check(after) is { } problem) throw new ComplianceServiceException(problem);
            if (settings.Any(s => string.Equals(s.Name, after.Name, StringComparison.OrdinalIgnoreCase) && s.SettingId != before?.SettingId))
                throw new ComplianceServiceException($"Configuration item {ci} already has a setting named {after.Name}.");
        }

        var revisions = 1;
        switch (request.Kind)
        {
            case ComplianceChangeKind.AddSetting:
                impact.Add($"Adds {after!.Kind.ToLowerInvariant()} setting {after.Name} to configuration item {ci}.");
                impact.AddRange(SettingLines(after, null));
                impact.Add("Undo removes the setting.");
                break;
            case ComplianceChangeKind.EditSetting:
            {
                if (SettingRules.Same(before!, after! with { SettingId = before!.SettingId }))
                    throw new ComplianceServiceException("The setting has the same values in ConfigMgr. Nothing to change.");
                var inPlace = SettingRules.CanChangeInPlace(before!, after!);
                values[EditModeKey] = inPlace ? "InPlace" : "Replace";
                impact.Add($"Changes setting {before!.Name} of configuration item {ci}.");
                impact.AddRange(SettingLines(after!, before));
                if (!inPlace)
                {
                    revisions = 3;
                    impact.Add("ConfigMgr cannot change the data type, the rule or the expected value of a setting in place. The new setting is added first under a temporary name, then the old one is removed and the new one takes the name. The setting gets a new ID.");
                }
                impact.Add(inPlace ? "Undo sets the old values back." : "Undo sets the old values back; the restored setting has a new ID.");
                break;
            }
            case ComplianceChangeKind.RemoveSetting:
                impact.Add($"Removes setting {before!.Name} and its rule from configuration item {ci}.");
                impact.AddRange(SettingLines(before, null));
                impact.Add("Undo adds the setting again with the same values. It gets a new ID.");
                break;
        }
        impact.Add($"ConfigMgr stores this as a new revision of the configuration item: revision {item.Revision} becomes {item.Revision + revisions}{(revisions > 1 ? " (one per step)" : "")}, and the item can get a new CI ID.");
        if (item.Deployments > 0)
            impact.Add($"The item is in a deployed baseline ({Count(item.Deployments, "deployment")}). Clients evaluate the new revision at their next evaluation.");
        foreach (var d in remediating)
            impact.Add($"The deployment of baseline {Shown(d.BaselineName, d.BaselineId)} to collection {Shown(d.CollectionName, d.CollectionId)} remediates noncompliant settings ({Members(d.Members)}).");
        if (remediating.Count == 0 && item.Deployments > 0) impact.Add(NoRemediatingDeploymentLine);
        var held = remediating.Count > 0 && RemediatingSetting(request.Kind, after);
        if (held) impact.Add(SettingRemediationLine);
        return request with
        {
            CiId = item.CiId, CiName = item.Name, SettingBefore = before, SettingAfter = after, Values = values, Impact = impact, InRemediatingDeployment = remediating.Count > 0
        };
    }

    public const string NoRemediatingDeploymentLine = "No deployment of a baseline with this item remediates noncompliant settings.";

    public const string SettingRemediationLine =
        "This setting remediates: clients of the remediating deployments change the value at their next evaluation. Remediation needs a second, separate confirmation.";

    internal static bool RemediatingSetting(ComplianceChangeKind kind, CiSetting? after) =>
        kind is ComplianceChangeKind.AddSetting or ComplianceChangeKind.EditSetting && after?.Remediate == true;

    private static string Members(int? members) => members is { } n ? Count(n, "member") + " now" : "member count not known";

    private static IEnumerable<string> SettingLines(CiSetting s, CiSetting? old)
    {
        IEnumerable<(string Label, string New, string? Old)> lines =
        [
            ("Name", s.Name, old?.Name),
            ("Data type", s.DataType, old?.DataType),
            (s.Kind == "Registry" ? "Registry value" : "Script", s.Kind == "Registry" ? $@"{s.Hive}\{s.KeyName}\{s.ValueName}" + (s.Is64Bit ? " (64-bit)" : " (32-bit)") : $"{s.DiscoveryLanguage}, {Lines(s.DiscoveryScript)}" + (s.RemediationScript.Length > 0 ? $", remediation {s.RemediationLanguage}, {Lines(s.RemediationScript)}" : ", no remediation script"),
                old is null ? null : old.Kind == "Registry" ? $@"{old.Hive}\{old.KeyName}\{old.ValueName}" + (old.Is64Bit ? " (64-bit)" : " (32-bit)") : $"{old.DiscoveryLanguage}, {Lines(old.DiscoveryScript)}" + (old.RemediationScript.Length > 0 ? $", remediation {old.RemediationLanguage}, {Lines(old.RemediationScript)}" : ", no remediation script")),
            ("Rule", $"{s.RuleName}: {s.Operator} {string.Join(", ", SettingRules.Values(s))}", old is null ? null : $"{old.RuleName}: {old.Operator} {string.Join(", ", SettingRules.Values(old))}"),
            ("Remediate", s.Remediate ? "Yes" : "No", old is null ? null : old.Remediate ? "Yes" : "No"),
            ("Severity", s.Severity, old?.Severity)
        ];
        foreach (var (label, now, before) in lines)
            yield return before is null ? $"{label}: {now}" : before == now ? $"{label}: {now} (unchanged)" : $"{label}: {before} -> {now}";
        var description = s.Description ?? "";
        yield return old is null ? $"Description: {(description.Length == 0 ? "(none)" : Count(description.Length, "character"))}"
            : description == (old.Description ?? "") ? "Description: (unchanged)" : "Description: changed.";
        if (s.DataType == "DateTime") yield return "ConfigMgr stores the date and time in UTC.";
        if (s.Kind == "Script" && old is not null && (s.DiscoveryScript != old.DiscoveryScript || s.RemediationScript != old.RemediationScript))
            yield return "The script text changes; the dialog shows the full text.";
    }

    private static (List<string> Deployments, List<string> Baselines, List<string> Items) SplitCreated(IReadOnlyList<string>? created)
    {
        List<string> deployments = [], baselines = [], items = [];
        foreach (var id in created ?? [])
        {
            var parts = (id ?? "").Split(':', 2);
            if (parts.Length != 2 || !NumberRegex().IsMatch(parts[1])) continue;
            switch (parts[0])
            {
                case "Deployment": deployments.Add(parts[1]); break;
                case "Baseline": baselines.Add(parts[1]); break;
                case "ConfigurationItem": items.Add(parts[1]); break;
            }
        }
        return (deployments, baselines, items);
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
