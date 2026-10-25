using System.Globalization;
using static ApplicationPackagerEngine.Services.HygieneSets;

namespace ApplicationPackagerEngine.Services;

internal static partial class HygieneChecks
{
    private static void AddCollections(List<HygieneCheckFamily> list)
    {
        list.Add(new(["COL-01"], ["Collections"], [Collections, Deployments, CollectionSettings, CollectionDependencies, AutoDeploymentRules, ClientSettingsAssignments, CiAssignments], CollectionEmptyUnused));
        list.Add(new(["COL-02"], ["Collections"], [Collections, Deployments], DeploymentEmptyCollection));
        list.Add(new(["COL-03"], ["Collections"], [Collections], IncrementalCeiling));
        list.Add(new(["COL-04"], ["Collections"], [CollectionEvalFull, CollectionEvalIncremental], EvaluationRunTime));
        list.Add(new(["COL-05", "COL-06", "COL-09"], ["CollectionSchedules"], [Collections, CollectionDependencies, CollectionDetails], CollectionSchedules));
        list.Add(new(["COL-07", "COL-08"], ["Collections", "CollectionSchedules"], [Collections, CollectionDependencies], CollectionReferences));
        list.Add(new(["COL-10"], ["MaintenanceWindows"], [MaintenanceWindows, Collections], MaintenanceWindowExpired));
        list.Add(new(["DPL-01"], ["Deployments"], [AppDeployments], DeploymentExpired));
        list.Add(new(["DPL-02", "DPL-03", "DPL-04"], ["Deployments"], [Deployments], DeploymentStates));
        list.Add(new(["DPL-05"], ["Deployments"], [Deployments, Programs, TaskSequences], DeployedDisabledObject));
    }

    internal static bool IsBuiltIn(string collectionId) => collectionId.StartsWith("SMS", StringComparison.OrdinalIgnoreCase);

    /// <summary>A reference edge: Dependent uses Source. Kind is limit, include or exclude (SMS_CollectionDependencies RelationshipType 1, 2, 3).</summary>
    internal readonly record struct CollectionEdge(string From, string To, string Kind);

    internal static List<CollectionEdge> CollectionEdges(HygieneContext c) =>
        c.Rows(CollectionDependencies).Select(r => new CollectionEdge(r.S("DependentCollectionID"), r.S("SourceCollectionID"), r.I("RelationshipType") switch
        {
            1 => "limit",
            2 => "include",
            3 => "exclude",
            var n => "type" + n.ToString(CultureInfo.InvariantCulture)
        })).ToList();

    private static IEnumerable<HygieneFinding> CollectionEmptyUnused(HygieneContext c)
    {
        var targets = Set(c.Rows(Deployments).Select(r => r.S("CollectionID")));
        var referenced = Set(CollectionEdges(c).Select(e => e.To).Concat(c.Rows(Collections).Select(r => r.S("LimitToCollectionID"))));
        var withSettings = Set(c.Rows(CollectionSettings).Select(r => r.S("CollectionID")));
        var users = CollectionUsersOf(c);
        foreach (var col in c.Rows(Collections))
        {
            var id = col.S("CollectionID");
            if (IsBuiltIn(id) || col.I("MemberCount", -1) != 0) continue;
            if (targets.Contains(id) || referenced.Contains(id) || withSettings.Contains(id)) continue;
            var finding = F("COL-01", "Collection", id, col.S("Name"),
                "Empty collection with no deployments, no include/exclude references from other collections, not used as a limiting collection, and no collection variables.",
                "Delete unless it is a staging container someone still fills by hand.",
                users.TryGetValue(id, out var use) ? HygieneFixKind.None : HygieneFixKind.RemoveCollection, use is null ? Params((HygieneFixParameters.Id, id)) : null);
            yield return use is null ? finding : finding with
            {
                Evidence = finding.Evidence + " " + use + ".",
                FixRefusal = use + ", so the page does not delete the collection. Remove that use first."
            };
        }
    }

    /// <summary>
    /// The first use of each collection the delete rule of the page refuses and the deployment summary does
    /// not show: an automatic deployment rule, a client settings deployment, or a deployment of an
    /// application, a software update, a baseline or a policy.
    /// </summary>
    private static Dictionary<string, string> CollectionUsersOf(HygieneContext c)
    {
        var uses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(IEnumerable<string> ids, string noun, string plural)
        {
            foreach (var g in ids.Where(i => i.Length > 0).GroupBy(i => i, StringComparer.OrdinalIgnoreCase))
                uses.TryAdd(g.Key, g.Count() == 1 ? $"1 {noun} targets the collection" : $"{N(g.Count())} {plural} target the collection");
        }
        Add(c.Rows(AutoDeploymentRules).Select(r => r.S("CollectionID")), "automatic deployment rule", "automatic deployment rules");
        Add(c.Rows(ClientSettingsAssignments).Select(r => r.S("CollectionID")), "client settings deployment", "client settings deployments");
        Add(c.Rows(CiAssignments).Select(r => r.S("TargetCollectionID")), "deployment of an application, a software update, a baseline or a policy", "deployments of applications, software updates, baselines or policies");
        return uses;
    }

    private static IEnumerable<HygieneFinding> DeploymentEmptyCollection(HygieneContext c)
    {
        var empty = c.Rows(Collections).Where(r => r.I("MemberCount", -1) == 0).ToDictionary(r => r.S("CollectionID"), r => r.S("Name"), StringComparer.OrdinalIgnoreCase);
        foreach (var d in c.Rows(Deployments))
        {
            var cid = d.S("CollectionID");
            if (cid.Length == 0 || !empty.ContainsKey(cid)) continue;
            yield return F("COL-02", "Deployment", d.S("DeploymentID"), $"{d.S("SoftwareName")} -> {d.S("CollectionName")}",
                $"Deployment targets collection '{d.S("CollectionName")}' ({cid}), which currently has zero members - it can never apply.",
                "Remove the deployment, or fix the collection membership if it emptied by accident.",
                guidance: $"Review in the console: the deployment of '{d.S("SoftwareName")}' to collection '{d.S("CollectionName")}'.");
        }
    }

    private static IEnumerable<HygieneFinding> IncrementalCeiling(HygieneContext c)
    {
        var ceiling = c.T.IncrementalCeiling;
        var incremental = c.Rows(Collections).Where(r => r.I("RefreshType") is 4 or 6).Select(r => r.S("Name")).ToList();
        if (incremental.Count <= ceiling) yield break;
        yield return F("COL-03", "Site", "Site", "Incremental evaluation load",
            $"{N(incremental.Count)} collections use incremental evaluation; above the recommended ceiling of {N(ceiling)}, evaluation latency degrades site-wide. First 15 by name: {Sample(incremental)}",
            "Switch rarely-changing collections to scheduled-only refresh until the count sits under the ceiling.",
            guidance: "Per collection: Set-CMCollection -CollectionId <CollectionID> -RefreshType Periodic");
    }

    private static IEnumerable<HygieneFinding> EvaluationRunTime(HygieneContext c)
    {
        var limit = c.T.ColEvalSlowMs;
        var slow = new Dictionary<string, (string Name, long Worst, List<string> Parts)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var (kind, set) in new[] { ("full", CollectionEvalFull), ("incremental", CollectionEvalIncremental) })
            foreach (var row in c.Rows(set))
            {
                var ms = row.L("Length");
                if (ms <= limit) continue;
                var id = row.S("CollectionID");
                if (!slow.TryGetValue(id, out var entry)) { entry = (row.S("CollectionName"), 0, []); order.Add(id); }
                var changes = row.L("MemberChanges");
                var changeText = changes == 0 ? "no membership change" : $"{N(changes)} membership change(s)";
                entry.Parts.Add($"last {kind} evaluation ran {(ms / 1000.0).ToString("N1", CultureInfo.InvariantCulture)}s with {changeText}");
                slow[id] = (entry.Name, Math.Max(entry.Worst, ms), entry.Parts);
            }
        foreach (var id in order.OrderByDescending(i => slow[i].Worst))
        {
            var (name, _, parts) = slow[id];
            var text = string.Join("; ", parts);
            text = text.Length > 0 && text[0] == 'l' ? "L" + text[1..] : text;
            yield return F("COL-04", "Collection", id, name,
                $"{text} (threshold {(limit / 1000.0).ToString("N1", CultureInfo.InvariantCulture)}s). Evaluations run one at a time per queue, so a slow collection delays every collection queued behind it.",
                "Review the membership query: avoid LIKE wildcards and nested subselects on large classes, limit to the smallest suitable collection, and drop incremental updates where membership rarely changes.",
                guidance: $"Review: (Get-CMCollection -Id '{id}').CollectionRules | Select-Object RuleName, QueryExpression");
        }
    }

    private static IEnumerable<HygieneFinding> CollectionSchedules(HygieneContext c)
    {
        var byId = c.Rows(Collections).GroupBy(r => r.S("CollectionID"), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var details = c.Rows(CollectionDetails).GroupBy(r => r.S("CollectionID"), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var composed = Set(CollectionEdges(c).Where(e => e.Kind is "include" or "exclude").Select(e => e.From));
        string NameOf(string id) => byId.TryGetValue(id, out var r) ? r.S("Name") : id;
        var scheduled = new List<(int Hour, string Name)>();
        foreach (var col in c.Rows(Collections))
        {
            var id = col.S("CollectionID");
            if (IsBuiltIn(id)) continue;
            var refresh = col.I("RefreshType");
            details.TryGetValue(id, out var d);
            int day = 0, hour = 0, minute = 0, direct = 0, query = 0, startHour = -1;
            if (details.ContainsKey(id))
            {
                day = d.I("DaySpan"); hour = d.I("HourSpan"); minute = d.I("MinuteSpan");
                direct = d.I("DirectRules"); query = d.I("QueryRules");
                if (d.T("StartTime") is { } start) startHour = start.Hour;
            }
            var freq = minute > 0 ? $"every {N(minute)} minute(s)" : hour > 0 ? $"every {N(hour)} hour(s)" : day > 0 ? $"every {N(day)} day(s)" : "on an unrecognized schedule";
            if (refresh == 6 && (minute > 0 || hour > 0))
                yield return F("COL-05", "Collection", id, col.S("Name"),
                    $"Incremental updates are enabled AND a full evaluation runs {freq}. A full update on an incremental collection is only a backup evaluation; running it sub-daily duplicates work the incremental cycle already does, and each full evaluation also evaluates every dependent collection.",
                    "Keep incremental updates and drop the full schedule back to a rare backup, or remove it.",
                    HygieneFixKind.SetCollectionRefreshType, Params((HygieneFixParameters.Id, id), (HygieneFixParameters.RefreshType, "Continuous")));
            if (refresh is 2 or 6 && direct > 0 && query == 0 && !composed.Contains(id))
            {
                var limitId = col.S("LimitToCollectionID");
                var limitIncremental = byId.TryGetValue(limitId, out var limiting) && limiting.I("RefreshType") is 4 or 6;
                if (!limitIncremental)
                    yield return F("COL-06", "Collection", id, col.S("Name"),
                        $"Membership is {N(direct)} direct rule(s) only and the limiting collection '{NameOf(limitId)}' is not incrementally updated. Direct membership never changes on its own, so the scheduled evaluation re-computes an identical result every cycle.",
                        "Disable the update schedule; direct-rule collections update when the rule changes.",
                        HygieneFixKind.SetCollectionRefreshType, Params((HygieneFixParameters.Id, id), (HygieneFixParameters.RefreshType, "Manual")));
            }
            if (refresh is 2 or 6 && startHour >= 0) scheduled.Add((startHour, col.S("Name")));
        }
        var hot = c.T.ColFullEvalHotSpotCount;
        foreach (var group in scheduled.GroupBy(s => s.Hour).Where(g => g.Count() >= hot))
        {
            var hh = group.Key.ToString("00", CultureInfo.InvariantCulture);
            var names = string.Join(", ", group.Take(5).Select(s => $"'{s.Name}'"));
            yield return F("COL-09", "Schedule", hh, $"{N(group.Count())} full updates starting at {hh}:00",
                $"{N(group.Count())} collections schedule their full evaluation in the same start hour ({hh}:00), including {names}. Clustered full updates create an evaluation hot spot; spreading them out and preferring off-peak hours reduces contention.",
                "Stagger the full-update start times across off-peak hours.",
                guidance: "Per collection: Set-CMCollection -CollectionId <ID> -RefreshSchedule (New-CMSchedule -Start <off-peak time> -RecurInterval Days -RecurCount 1)");
        }
    }

    private static IEnumerable<HygieneFinding> CollectionReferences(HygieneContext c)
    {
        var names = c.Rows(Collections).GroupBy(r => r.S("CollectionID"), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().S("Name"), StringComparer.OrdinalIgnoreCase);
        string NameOf(string id) => names.TryGetValue(id, out var n) ? n : id;
        var edges = CollectionEdges(c);
        var refAdj = Adjacency(edges.Where(e => e.Kind != "limit"));
        var depth = LongestChains(refAdj);
        var max = c.T.ColRefDepthMax;
        foreach (var col in c.Rows(Collections))
        {
            var id = col.S("CollectionID");
            if (IsBuiltIn(id)) continue;
            var d = depth.TryGetValue(id, out var v) ? v : 0;
            if (d <= max) continue;
            yield return F("COL-07", "Collection", id, col.S("Name"),
                $"Include/exclude references chain {N(d)} levels deep (threshold {N(max)}). Every referenced collection is evaluated to build this membership, and a change anywhere in the chain re-evaluates the whole branch.",
                "Flatten the chain: replace nested include/exclude rules with a WQL query rule using a ResourceId subselect against the target collection.",
                guidance: $"Replace include/exclude rules on '{col.S("Name")}' with a query rule, for example: select * from SMS_R_System where SMS_R_System.ResourceId in (select ResourceID from SMS_CM_RES_COLL_<CollectionID>)");
        }
        var component = StrongComponents(Adjacency(edges));
        var flagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in edges)
        {
            if (flagged.Contains(e.From) || IsBuiltIn(e.From)) continue;
            var inCycle = string.Equals(e.From, e.To, StringComparison.OrdinalIgnoreCase)
                || (component.TryGetValue(e.From, out var a) && component.TryGetValue(e.To, out var b) && a == b);
            if (!inCycle) continue;
            flagged.Add(e.From);
            yield return F("COL-08", "Collection", e.From, NameOf(e.From),
                $"The {e.Kind} reference from '{NameOf(e.From)}' to '{NameOf(e.To)}' is part of a loop: following limiting/include/exclude references from '{NameOf(e.To)}' leads back to '{NameOf(e.From)}'. Every evaluation of any collection in the loop re-enters the loop, which hammers collection evaluation and the site database.",
                "Break the loop by removing or re-pointing one of the references in the cycle.",
                guidance: $"Console: review the {e.Kind} rule on '{NameOf(e.From)}' that references '{NameOf(e.To)}' and break the cycle.");
        }
    }

    internal static Dictionary<string, List<string>> Adjacency(IEnumerable<CollectionEdge> edges)
    {
        var adj = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in edges)
        {
            if (!adj.TryGetValue(e.From, out var list)) adj[e.From] = list = [];
            list.Add(e.To);
        }
        return adj;
    }

    /// <summary>
    /// The longest reference chain below each node, iteratively (a deep chain cannot overflow the stack).
    /// A node on the current path counts 0, so a loop stops the count; loops report through COL-08.
    /// </summary>
    internal static Dictionary<string, int> LongestChains(Dictionary<string, List<string>> adj)
    {
        var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var onPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in adj.Keys)
        {
            if (depth.ContainsKey(root)) continue;
            var stack = new Stack<(string Node, int Next, int Best)>();
            stack.Push((root, 0, 0));
            onPath.Add(root);
            while (stack.Count > 0)
            {
                var (node, next, best) = stack.Pop();
                var children = adj.TryGetValue(node, out var l) ? l : [];
                if (next < children.Count)
                {
                    var child = children[next];
                    stack.Push((node, next + 1, best));
                    if (depth.TryGetValue(child, out var known)) { var top = stack.Pop(); stack.Push((top.Node, top.Next, Math.Max(top.Best, 1 + known))); }
                    else if (onPath.Contains(child)) { var top = stack.Pop(); stack.Push((top.Node, top.Next, Math.Max(top.Best, 1))); }
                    else { onPath.Add(child); stack.Push((child, 0, 0)); }
                    continue;
                }
                onPath.Remove(node);
                depth[node] = best;
                if (stack.Count > 0) { var parent = stack.Pop(); stack.Push((parent.Node, parent.Next, Math.Max(parent.Best, 1 + best))); }
            }
        }
        return depth;
    }

    /// <summary>The strongly connected component of each node (iterative Tarjan): an edge is in a loop when both ends share a component.</summary>
    internal static Dictionary<string, int> StrongComponents(Dictionary<string, List<string>> adj)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var low = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        var component = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var counter = 0;
        var components = 0;
        var nodes = adj.Keys.Concat(adj.Values.SelectMany(v => v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var start in nodes)
        {
            if (index.ContainsKey(start)) continue;
            var work = new Stack<(string Node, int Next)>();
            work.Push((start, 0));
            index[start] = low[start] = counter++;
            stack.Push(start);
            onStack.Add(start);
            while (work.Count > 0)
            {
                var (node, next) = work.Pop();
                var children = adj.TryGetValue(node, out var l) ? l : [];
                if (next < children.Count)
                {
                    work.Push((node, next + 1));
                    var child = children[next];
                    if (!index.ContainsKey(child))
                    {
                        index[child] = low[child] = counter++;
                        stack.Push(child);
                        onStack.Add(child);
                        work.Push((child, 0));
                    }
                    else if (onStack.Contains(child)) low[node] = Math.Min(low[node], index[child]);
                    continue;
                }
                if (low[node] == index[node])
                {
                    string member;
                    do
                    {
                        member = stack.Pop();
                        onStack.Remove(member);
                        component[member] = components;
                    } while (!string.Equals(member, node, StringComparison.OrdinalIgnoreCase));
                    components++;
                }
                if (work.Count > 0) { var parent = work.Peek().Node; low[parent] = Math.Min(low[parent], low[node]); }
            }
        }
        // A single node with no self-edge is its own component but in no loop; give it a unique number.
        foreach (var (node, list) in adj)
            if (!list.Any(t => component.TryGetValue(t, out var c) && c == component[node])) component[node] = -1 - component[node];
        return component;
    }

    private static IEnumerable<HygieneFinding> MaintenanceWindowExpired(HygieneContext c)
    {
        var names = c.Rows(Collections).GroupBy(r => r.S("CollectionID"), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().S("Name"), StringComparer.OrdinalIgnoreCase);
        foreach (var w in c.Rows(MaintenanceWindows))
        {
            if (w.I("RecurrenceType") != 1 || w.T("StartTime") is not { } start) continue;
            var end = start.AddMinutes(w.I("Duration"));
            if (end.AddDays(1) >= c.Now) continue;
            var cid = w.S("CollectionID");
            var colName = names.TryGetValue(cid, out var n) ? n : cid;
            yield return F("COL-10", "MaintenanceWindow", cid + "|" + w.S("ServiceWindowID"), $"{w.S("Name")} on {colName}",
                $"The one-time maintenance window ended {Minute(end)} and never recurs. A collection with any maintenance window, including an expired one, blocks required deployments outside a window.",
                "Delete the expired window. If the collection needs no window at all, delete every window on it.",
                HygieneFixKind.RemoveMaintenanceWindow, Params((HygieneFixParameters.CollectionId, cid), (HygieneFixParameters.WindowId, w.S("ServiceWindowID"))));
        }
    }

    private static IEnumerable<HygieneFinding> DeploymentExpired(HygieneContext c)
    {
        foreach (var d in c.Rows(AppDeployments))
        {
            if (d.T("ExpirationTime") is not { } expired || expired >= c.Now) continue;
            yield return F("DPL-01", "Deployment", d.S("AssignmentID"), $"{d.S("ApplicationName")} -> {d.S("CollectionName")}",
                $"Deployment expired {Minute(expired)} and no longer offers to clients; it is clutter in every deployment view.",
                "Remove the expired deployment.",
                HygieneFixKind.RemoveApplicationDeployment, Params((HygieneFixParameters.DeploymentIds, d.S("AssignmentUniqueID"))));
        }
    }

    private static readonly Dictionary<string, string> BroadIds = new(StringComparer.OrdinalIgnoreCase) { ["SMS00001"] = "All Systems", ["SMS00002"] = "All Users", ["SMSDM003"] = "All Desktop and Server Clients" };
    private static readonly HashSet<string> BroadNames = new(StringComparer.OrdinalIgnoreCase) { "All Systems", "All Users", "All User Groups", "All Users and User Groups", "All Desktop and Server Clients" };

    private static IEnumerable<HygieneFinding> DeploymentStates(HygieneContext c)
    {
        var grace = c.T.DeadlineGraceDays;
        var pctThreshold = c.T.FailurePctThreshold;
        var deadlineCutoff = c.Now.AddDays(-grace);
        var minAge = c.T.AvailableUnusedMinAgeDays;
        var createdCutoff = c.Now.AddDays(-minAge);
        foreach (var d in c.Rows(Deployments))
        {
            var intent = d.I("DeploymentIntent");
            var name = $"{d.S("SoftwareName")} -> {d.S("CollectionName")}";
            var key = d.S("DeploymentID");
            var targeted = d.I("NumberTargeted");
            if (intent == 1 && d.T("EnforcementDeadline") is { } deadline && deadline <= deadlineCutoff && targeted > 0 && d.I("NumberErrors") > 0)
            {
                var pct = Math.Round(d.I("NumberErrors") * 100.0 / targeted, 1);
                if (pct >= pctThreshold)
                    yield return F("DPL-02", "Deployment", key, name,
                        $"Required deployment passed its deadline {Minute(deadline)} (over {N(grace)} days ago) with {N(d.I("NumberErrors"))} of {N(targeted)} targeted clients in error ({pct.ToString("0.#", CultureInfo.InvariantCulture)}%).",
                        "Investigate the client errors; a deployment stuck past deadline at this failure rate needs a fix or retirement, not more time.",
                        guidance: $"Triage first: the deployment status of '{d.S("SoftwareName")}' on collection '{d.S("CollectionName")}' in the console or with Get-CMDeployment.");
            }
            if (intent == 2 && d.T("CreationTime") is { } created && created <= createdCutoff && targeted > 0 && d.I("NumberSuccess") + d.I("NumberInProgress") == 0)
                yield return F("DPL-03", "Deployment", key, name,
                    $"Available deployment created {Minute(created)} (over {N(minAge)} days ago) targets {N(targeted)} clients with zero installs and nothing in progress.",
                    "Nobody is opting in; remove the deployment or rethink how it is offered.",
                    guidance: $"Review: the deployment of '{d.S("SoftwareName")}' to '{d.S("CollectionName")}'.");
            if (intent == 1)
            {
                var id = d.S("CollectionID").ToUpperInvariant();
                var broad = BroadIds.TryGetValue(id, out var n) ? n : id.StartsWith("SMS", StringComparison.Ordinal) && BroadNames.Contains(d.S("CollectionName")) ? d.S("CollectionName") : null;
                if (broad is not null)
                    yield return F("DPL-04", "Deployment", key, $"{d.S("SoftwareName")} -> {broad}",
                        $"Required deployment of '{d.S("SoftwareName")}' targets the built-in collection '{broad}' ({id}). Every current and future resource in the site receives it, with no pilot group and no way to exclude a resource.",
                        "Re-target the deployment to a custom collection limited to the intended resources, then remove this deployment.",
                        guidance: $"Review first: Get-CMDeployment -CollectionName '{broad}' | Where-Object SoftwareName -eq '{PowerShellLiteral.EscapeSingleQuoted(d.S("SoftwareName"))}'");
            }
        }
    }

    internal const long DisabledFlag = 0x1000;

    private static IEnumerable<HygieneFinding> DeployedDisabledObject(HygieneContext c)
    {
        var disabledTs = c.Rows(TaskSequences).Where(r => (r.L("ProgramFlags") & DisabledFlag) != 0).GroupBy(r => r.S("PackageID"), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().S("Name"), StringComparer.OrdinalIgnoreCase);
        var disabledPrograms = Set(c.Rows(Programs).Where(r => (r.L("ProgramFlags") & DisabledFlag) != 0).Select(r => r.S("PackageID") + "|" + r.S("ProgramName")));
        foreach (var d in c.Rows(Deployments))
        {
            var pkg = d.S("PackageID");
            if (pkg.Length == 0) continue;
            string? what = null;
            if (d.I("FeatureType") == 7 && disabledTs.TryGetValue(pkg, out var ts)) what = $"task sequence '{ts}'";
            else if (d.I("FeatureType") == 2 && disabledPrograms.Contains(pkg + "|" + d.S("ProgramName"))) what = $"program '{d.S("ProgramName")}' of package {pkg}";
            if (what is null) continue;
            yield return F("DPL-05", "Deployment", d.S("DeploymentID"), $"{d.S("SoftwareName")} -> {d.S("CollectionName")}",
                $"The deployment to '{d.S("CollectionName")}' runs the {what}, which is disabled. Clients receive the policy and never run it.",
                "Enable the object if the deployment is still wanted; otherwise remove the deployment.",
                guidance: $"Review: Get-CMDeployment -CollectionName '{PowerShellLiteral.EscapeSingleQuoted(d.S("CollectionName"))}' | Where-Object PackageID -eq '{pkg}'");
        }
    }
}
