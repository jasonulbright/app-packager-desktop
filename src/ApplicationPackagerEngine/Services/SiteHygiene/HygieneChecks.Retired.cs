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
                "A retired application must not supersede or depend on an active one. Remove the rules.",
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
                "The application qualifies for deletion. Remove the supersedence rule of the replacement, then delete the application. The site refuses a delete that something still blocks.",
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
                $"The deployment was created {Minute(created)}, more than {Count(days, "day")} ago. It targets {Count(d.I("NumberTargeted"), "client")}: {N(d.I("NumberSuccess"))} installed, {N(d.I("NumberInProgress"))} in progress, {N(d.I("NumberErrors"))} with errors.",
                "Review whether the deployment is still needed. Nothing is deleted from here.",
                guidance: $"Review: the deployment of '{d.S("SoftwareName")}' to '{d.S("CollectionName")}'.");
        }
    }
}
