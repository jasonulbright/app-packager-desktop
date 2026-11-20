using static ApplicationPackagerEngine.Services.HygieneSets;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Retired and superseded applications. The rule: no active application depends on or supersedes a retired application, and no task
/// sequence installs a retired or superseded one. APP-07 is a retired application that holds rules of its own toward an application
/// that exists and is not retired (a retired target is SUP-04 or DEP-04, a deleted one SUP-01 or DEP-01). APP-08 is a superseded
/// application that nothing but the supersedence of its replacement refers to. TSQ-05 is a task sequence step that installs a retired
/// or superseded application. TSQ-06 is a stale reference: reference rows of a task sequence that the site does not return.
/// DPL-06 is a deployment older than the age threshold. All of them read data that a scan holds already.
/// </summary>
internal static partial class HygieneChecks
{
    private static void AddRetired(List<HygieneCheckFamily> list)
    {
        list.Add(new(["APP-07", "APP-08", "TSQ-05", "TSQ-06"], ["AppRelationships"], RetiredSets, RetiredSuperseded));
        list.Add(new(["DPL-06"], ["Deployments"], [Deployments], OldDeployments));
    }

    internal static string[] RetiredSets => [Applications, AppRevisions, AppRelations, AppDependencies, DeploymentTypes, TaskSequences, TsReferences, TsSteps];

    private sealed record AppFacts(string Model, string Name, bool Retired, bool Superseded, bool Deployed);

    private static Dictionary<string, AppFacts> Facts(HygieneContext c) =>
        c.Rows(Applications).Where(a => a.S("ModelName").Length > 0).GroupBy(a => a.S("ModelName"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => { var a = g.First(); return new AppFacts(a.S("ModelName"), a.S("LocalizedDisplayName"), a.B("IsExpired"), a.B("IsSuperseded"), a.B("IsDeployed")); }, StringComparer.OrdinalIgnoreCase);

    /// <summary>The task sequences that name each application: from the reference rows and from the steps, each task sequence once.</summary>
    internal static Dictionary<string, List<(string Id, string Name)>> TaskSequencesOf(HygieneContext c)
    {
        var names = c.Rows(TaskSequences).GroupBy(t => t.S("PackageID"), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().S("Name"), StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in c.Rows(TsReferences).Concat(c.Rows(TsSteps)))
        {
            var model = r.S("ObjectID");
            if (!model.Contains("/Application_", StringComparison.Ordinal) || !names.TryGetValue(r.S("PackageID"), out var name)) continue;
            if (!map.TryGetValue(model, out var list)) map[model] = list = [];
            if (!list.Any(x => string.Equals(x.Item1, r.S("PackageID"), StringComparison.OrdinalIgnoreCase))) list.Add((r.S("PackageID"), name));
        }
        return map;
    }

    /// <summary>
    /// Every object that refers to an application and so blocks its delete, for each application that has one, from the data the scan holds already
    /// (no query): deployments, task sequences (and the task sequences that hold them), stale reference rows, dependencies on it and supersedence rules
    /// that name it. A task sequence or a collection that the account may not see is not in that data. Null when the applications were not read.
    /// </summary>
    internal static (IReadOnlyDictionary<string, IReadOnlyList<HygieneBlocker>>? Blockers, string Note) BlockersOf(HygieneContext c)
    {
        var d = c.Data;
        if (!d.IsRead(Applications)) return (null, "");
        var missing = new List<string>();
        bool Has(string set, string what)
        {
            if (d.IsRead(set)) return true;
            missing.Add(what);
            return false;
        }
        var haveDeployments = Has(Deployments, "deployments");
        var haveEdges = Has(AppRelations, "relations") & Has(AppDependencies, "dependencies") & Has(DeploymentTypes, "deployment types") & Has(AppRevisions, "old revisions");
        var haveSequences = Has(TaskSequences, "task sequences") & Has(TsReferences, "task sequence references") & Has(TsSteps, "task sequence steps");

        var map = new Dictionary<string, List<HygieneBlocker>>(StringComparer.OrdinalIgnoreCase);
        void Add(string model, HygieneBlocker blocker)
        {
            if (!map.TryGetValue(model, out var list)) map[model] = list = [];
            list.Add(blocker);
        }
        var facts = Facts(c);
        if (haveDeployments)
        {
            var byName = facts.Values.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(a => a.Model).ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var r in c.Rows(Deployments).Where(r => r.I("FeatureType") == 1))
            {
                List<string> models = facts.ContainsKey(r.S("ModelName")) ? new() { r.S("ModelName") } : byName.TryGetValue(r.S("SoftwareName"), out var named) ? named : new();
                foreach (var m in models) Add(m, new(HygieneBlockerKinds.Deployment, r.S("CollectionName"), $"Collection {r.S("CollectionName")} ({r.S("CollectionID")})"));
            }
        }
        if (haveSequences)
        {
            var references = c.Rows(TsReferences).Concat(c.Rows(TsSteps)).ToList();
            var holders = references.Where(r => !r.S("ObjectID").Contains("/Application_", StringComparison.Ordinal) && r.S("ObjectID").Length > 0)
                .GroupBy(r => r.S("ObjectID"), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(r => r.S("PackageID")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
            var names = c.Rows(TaskSequences).GroupBy(t => t.S("PackageID"), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().S("Name"), StringComparer.OrdinalIgnoreCase);
            foreach (var (model, list) in TaskSequencesOf(c))
                foreach (var (id, name) in list)
                {
                    Add(model, new(HygieneBlockerKinds.TaskSequence, name, $"Package {id}"));
                    if (holders.TryGetValue(id, out var outer))
                        foreach (var o in outer.Where(o => names.ContainsKey(o)))
                            Add(model, new(HygieneBlockerKinds.NestedTaskSequence, names[o], $"Package {o}; it holds the task sequence {name}, which installs the application"));
                }
            foreach (var r in references.Where(r => r.S("PackageID").Length > 0 && !names.ContainsKey(r.S("PackageID")) && r.S("ObjectID").Contains("/Application_", StringComparison.Ordinal))
                .GroupBy(r => (Model: r.S("ObjectID"), Package: r.S("PackageID")), new ModelPackageComparer()).Select(g => g.Key))
                Add(r.Model, new(HygieneBlockerKinds.StaleReference, r.Package, "A reference row of a task sequence that the site does not return. It still blocks the delete."));
        }
        if (haveEdges)
            foreach (var e in AppEdges(d).Where(e => e.ToExists))
                Add(e.ToKey, e.Kind == RelationKinds.DependsOn
                    ? new(HygieneBlockerKinds.Dependency, e.FromName, $"Deployment type '{e.FromDt}' depends on it")
                    : new(HygieneBlockerKinds.Supersedence, e.FromName, $"Deployment type '{e.FromDt}' supersedes it"));

        var order = new[] { HygieneBlockerKinds.Deployment, HygieneBlockerKinds.TaskSequence, HygieneBlockerKinds.NestedTaskSequence, HygieneBlockerKinds.StaleReference, HygieneBlockerKinds.Dependency, HygieneBlockerKinds.Supersedence };
        var result = map.ToDictionary(p => p.Key,
            p => (IReadOnlyList<HygieneBlocker>)p.Value.Distinct().OrderBy(b => Array.IndexOf(order, b.Kind)).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Detail, StringComparer.OrdinalIgnoreCase).ToList(),
            StringComparer.OrdinalIgnoreCase);
        var note = missing.Count == 0 ? "" : "These were not read, so the list can miss them: " + string.Join(", ", missing) + ".";
        return (result, note);
    }

    private sealed class ModelPackageComparer : IEqualityComparer<(string Model, string Package)>
    {
        public bool Equals((string Model, string Package) x, (string Model, string Package) y) =>
            string.Equals(x.Model, y.Model, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Package, y.Package, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Model, string Package) v) => HashCode.Combine(v.Model.ToUpperInvariant(), v.Package.ToUpperInvariant());
    }

    /// <summary>The ModelNames at the target end of the edges (or at the source end), each once, in ordinal order, comma separated.</summary>
    private static string ModelList(IEnumerable<AppEdge> edges, bool fromSide = false) =>
        string.Join(",", edges.Select(e => fromSide ? e.FromModel : e.ToKey).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal));

    private static IEnumerable<HygieneFinding> RetiredSuperseded(HygieneContext c)
    {
        var facts = Facts(c);
        var edges = AppEdges(c.Data);
        var onTs = TaskSequencesOf(c);
        var knownTs = Set(c.Rows(TaskSequences).Select(t => t.S("PackageID")));
        var staleRows = c.Rows(TsReferences).Concat(c.Rows(TsSteps)).Where(r => r.S("PackageID").Length > 0 && !knownTs.Contains(r.S("PackageID"))).ToList();

        // APP-07: a retired application with rules of its own toward an application that exists and is not retired.
        foreach (var app in facts.Values.Where(a => a.Retired).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            var own = edges.Where(e => string.Equals(e.FromModel, app.Model, StringComparison.OrdinalIgnoreCase) && e.ToExists && !(facts.TryGetValue(e.ToKey, out var t) && t.Retired)).ToList();
            if (own.Count == 0) continue;
            var parts = own.Select(e => e.Kind == RelationKinds.Supersedes ? $"supersedes '{e.ToName}' (deployment type '{e.FromDt}')" : $"depends on '{e.ToName}' (deployment type '{e.FromDt}')");
            var incoming = edges.Count(e => string.Equals(e.ToKey, app.Model, StringComparison.OrdinalIgnoreCase));
            yield return F("APP-07", "Application", app.Model, app.Name,
                $"The application is retired and still {string.Join("; and ", parts)}.{(incoming > 0 ? $" {Count(incoming, "other rule")} point{(incoming == 1 ? "s" : "")} at it: see SUP-04 and DEP-04." : "")}",
                "A retired application must not supersede or depend on an active one. Remove the rules. Each removal makes a new revision of the retired application; the fix can delete the old revisions it creates.",
                HygieneFixKind.ClearRetiredApplicationRules,
                Params((HygieneFixParameters.ModelName, app.Model),
                    (HygieneFixParameters.SupersedesModels, ModelList(own.Where(e => e.Kind == RelationKinds.Supersedes))),
                    (HygieneFixParameters.DependsOnModels, ModelList(own.Where(e => e.Kind == RelationKinds.DependsOn)))),
                guidance: $"Console: '{app.Name}' Properties > Supersedence tab and each deployment type > Dependencies - remove the rules.");
        }

        // APP-08: a superseded application that nothing but the supersedence of its replacement refers to. A retired one is SUP-04.
        foreach (var app in facts.Values.Where(a => a.Superseded && !a.Retired && !a.Deployed).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (edges.Any(e => e.Kind == RelationKinds.DependsOn && string.Equals(e.ToKey, app.Model, StringComparison.OrdinalIgnoreCase))) continue;
            if (onTs.ContainsKey(app.Model) || staleRows.Any(r => string.Equals(r.S("ObjectID"), app.Model, StringComparison.OrdinalIgnoreCase))) continue;
            var by = edges.Where(e => e.Kind == RelationKinds.Supersedes && e.ToExists && string.Equals(e.ToKey, app.Model, StringComparison.OrdinalIgnoreCase)).Select(e => $"'{e.FromName}'").Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (by.Count == 0) continue;
            yield return F("APP-08", "Application", app.Model, app.Name,
                $"The application is superseded by {string.Join(", ", by)}. It has no deployment, no dependency on it and no task sequence that refers to it (also no stale reference row). Blocked by: nothing.",
                "The application qualifies for deletion. The fix removes the supersedence rule of the replacement, then deletes the application. The site refuses a delete that something still blocks.",
                HygieneFixKind.RemoveSupersededApplication,
                Params((HygieneFixParameters.ModelName, app.Model),
                    (HygieneFixParameters.ReplacementModels, ModelList(edges.Where(e => e.Kind == RelationKinds.Supersedes && e.ToExists && string.Equals(e.ToKey, app.Model, StringComparison.OrdinalIgnoreCase)), fromSide: true))),
                guidance: $"Console: remove the supersedence rule of {string.Join(", ", by)}, then delete '{app.Name}'.");
        }

        // TSQ-05: a task sequence installs a retired or superseded application.
        foreach (var (model, list) in onTs.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!facts.TryGetValue(model, out var app) || !(app.Retired || app.Superseded)) continue;
            var by = edges.Where(e => e.Kind == RelationKinds.Supersedes && e.ToExists && string.Equals(e.ToKey, model, StringComparison.OrdinalIgnoreCase)).Select(e => $"'{e.FromName}'").Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var (id, name) in list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                yield return F("TSQ-05", "TaskSequence", id + "|" + model, $"{name} ({id}) -> {app.Name}",
                    $"Task sequence '{name}' (package {id}) has a step that installs the {(app.Retired ? "retired" : "superseded")} application '{app.Name}'.{(by.Count > 0 ? $" It is superseded by {string.Join(", ", by)}." : "")}",
                    "Edit the task sequence in the console and point the Install Application step at the active application. No fix runs from here: a task sequence is never edited without you.",
                    guidance: $"Console: Task Sequences > '{name}' > Edit - replace the application '{app.Name}' in the Install Application step");
        }

        // TSQ-06: reference rows of a task sequence that the site does not return.
        foreach (var g in staleRows.GroupBy(r => r.S("PackageID"), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var names = g.Select(r => r.S("ObjectID")).Distinct(StringComparer.OrdinalIgnoreCase).Select(m => facts.TryGetValue(m, out var a) ? a.Name : m).Take(8).ToList();
            yield return F("TSQ-06", "TaskSequence", g.Key, g.Key,
                $"Stale reference: the site returns {Count(g.Count(), "reference row")} of task sequence {g.Key}, but the task sequence itself is not returned (deleted, or outside your security scope). The rows still block the delete of what they name: {string.Join(", ", names)}.",
                "Check the task sequence in the console with an account that sees all objects. When it is gone, the rows are stale: the site removes them with its own cleanup, or a site repair is needed. No fix runs from here.",
                guidance: $"No supported cmdlet removes a reference row of task sequence {g.Key}.");
        }
    }

    /// <summary>DPL-06: a deployment created more than the age threshold ago. A deployment that DPL-03 reports is left to DPL-03.</summary>
    private static IEnumerable<HygieneFinding> OldDeployments(HygieneContext c)
    {
        var days = c.T.DeploymentOldDays;
        if (days <= 0) yield break;
        var cutoff = c.Now.AddDays(-days);
        var unusedCutoff = c.Now.AddDays(-c.T.AvailableUnusedMinAgeDays);
        foreach (var d in c.Rows(Deployments))
        {
            if (d.T("CreationTime") is not { } created || created > cutoff) continue;
            if (d.I("DeploymentIntent") == 2 && created <= unusedCutoff && d.I("NumberTargeted") > 0 && d.I("NumberSuccess") + d.I("NumberInProgress") == 0) continue;
            yield return F("DPL-06", "Deployment", d.S("DeploymentID"), $"{d.S("SoftwareName")} -> {d.S("CollectionName")}",
                $"The deployment was created {Minute(created)}, more than {Count(days, "day")} ago. It targets {Count(d.I("NumberTargeted"), "client")}: {N(d.I("NumberSuccess"))} installed, {N(d.I("NumberInProgress"))} in progress, {N(d.I("NumberErrors"))} failed.",
                "Review whether the deployment is still needed. Nothing is deleted from here.",
                guidance: $"Review: the deployment of '{d.S("SoftwareName")}' to '{d.S("CollectionName")}'.");
        }
    }
}
