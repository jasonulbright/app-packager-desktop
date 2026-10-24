using System.Globalization;
using static ApplicationPackagerEngine.Services.HygieneSets;

namespace ApplicationPackagerEngine.Services;

internal static partial class HygieneChecks
{
    /// <summary>SMS_AppRelation_Flat RelationType of a supersedence (ApplicationSuperSeded) and of a dependency (AppDependence).</summary>
    internal const int SupersedenceRelation = 15;
    internal const int DependencyRelation = 10;

    internal static string[] RelationSets => [AppRelations, AppDependencies, Applications, AppRevisions, DeploymentTypes];

    private static void AddRelationships(List<HygieneCheckFamily> list)
    {
        list.Add(new(["SUP-01", "SUP-02", "SUP-03", "SUP-04", "DEP-01", "DEP-02", "DEP-03", "DEP-04", "DEP-05", "REL-01"], ["AppRelationships"], RelationSets, RelationshipChecks));
        list.Add(new(["APP-04"], ["AppRelationships"], [DeploymentTypes, AppContent, Applications], ContentPathChecks)
        {
            Gate = c => c.ProbeResults is null
                ? "The check did not run: the content source probe is off. Switch it on in Options to probe each path over the network."
                : c.ProbeResults.Values.Any(v => v == HygieneProbe.NotProbed)
                    ? $"The check did not run: the site holds {N(c.ProbeResults.Count)} content source paths and one scan probes at most {N(SiteHygieneService.MaxProbePaths)}."
                    : null
        });
    }

    /// <summary>One supersedence or dependency edge between applications, resolved to ModelNames.</summary>
    internal sealed record AppEdge(string Kind, string FromModel, string FromName, string FromDt, string ToKey, string ToName, bool ToExists, string DependencyState);

    internal sealed record AppInfo(string ModelName, string Name, string Version, bool IsEnabled, bool IsExpired, bool HasContent, string Manufacturer);

    internal static Dictionary<string, AppInfo> AppsByModel(HygieneSiteRead data) =>
        data.Table(Applications).Records.Where(a => a.S("ModelName").Length > 0).GroupBy(a => a.S("ModelName"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g =>
            {
                var a = g.First();
                return new AppInfo(a.S("ModelName"), a.S("LocalizedDisplayName"), a.S("SoftwareVersion"), a.S("IsEnabled").Length == 0 || a.B("IsEnabled"), a.B("IsExpired"), a.B("HasContent"), a.S("Manufacturer"));
            }, StringComparer.OrdinalIgnoreCase);

    /// <summary>The direct edges of SMS_AppRelation_Flat, one per (kind, source application, deployment type, target application).</summary>
    internal static List<AppEdge> AppEdges(HygieneSiteRead data)
    {
        var models = new Dictionary<int, string>();
        foreach (var r in data.Table(Applications).Records.Concat(data.Table(AppRevisions).Records))
            if (r.I("CI_ID") is > 0 and var id && r.S("ModelName") is { Length: > 0 } m) models[id] = m;
        var apps = AppsByModel(data);
        var dtNames = data.Table(DeploymentTypes).Records.GroupBy(r => r.I("CI_ID")).ToDictionary(g => g.Key, g => g.First().S("LocalizedDisplayName"));
        var flags = data.Table(AppDependencies).Records
            .GroupBy(r => (r.I("FromApplicationCIID"), r.I("FromDeploymentTypeCIID"), r.I("ToApplicationCIID"), r.I("ToDeploymentTypeCIID")))
            .ToDictionary(g => g.Key, g => g.First().I("TypeFlag"));
        var edges = new List<AppEdge>();
        var seen = new HashSet<(string, string, string, string)>();
        foreach (var r in data.Table(AppRelations).Records)
        {
            var type = r.I("RelationType");
            if (type is not (SupersedenceRelation or DependencyRelation)) continue;
            var kind = type == SupersedenceRelation ? RelationKinds.Supersedes : RelationKinds.DependsOn;
            if (!models.TryGetValue(r.I("FromApplicationCIID"), out var fromModel)) continue;
            var fromName = apps.TryGetValue(fromModel, out var fa) ? fa.Name : fromModel;
            var dt = dtNames.TryGetValue(r.I("FromDeploymentTypeCIID"), out var n) ? n : "";
            var toCi = r.I("ToApplicationCIID");
            var toExists = models.TryGetValue(toCi, out var toModel) && apps.ContainsKey(toModel);
            var toKey = toExists ? toModel! : "CI_ID " + toCi.ToString(CultureInfo.InvariantCulture);
            var toName = toExists ? apps[toModel!].Name : $"Unknown ({toKey})";
            if (!seen.Add((kind, fromModel, dt, toKey))) continue;
            var state = "";
            if (kind == RelationKinds.DependsOn)
            {
                var key = (r.I("FromApplicationCIID"), r.I("FromDeploymentTypeCIID"), toCi, r.I("ToDeploymentTypeCIID"));
                state = flags.TryGetValue(key, out var f) && (f & 2) != 0 ? "Required" : "AppDependence";
            }
            edges.Add(new AppEdge(kind, fromModel, fromName, dt, toKey, toName, toExists, state));
        }
        return edges;
    }

    /// <summary>The edges of one kind that close a loop: both ends in one strongly connected component, or an edge to itself.</summary>
    internal static HashSet<AppEdge> CircularEdges(IEnumerable<AppEdge> edges)
    {
        var list = edges.Where(e => e.ToExists).ToList();
        var component = StrongComponents(Adjacency(list.Select(e => new CollectionEdge(e.FromModel, e.ToKey, e.Kind))));
        return list.Where(e => string.Equals(e.FromModel, e.ToKey, StringComparison.OrdinalIgnoreCase)
            || (component.TryGetValue(e.FromModel, out var a) && component.TryGetValue(e.ToKey, out var b) && a == b)).ToHashSet();
    }

    private static IEnumerable<HygieneFinding> RelationshipChecks(HygieneContext c)
    {
        var apps = AppsByModel(c.Data);
        var edges = AppEdges(c.Data);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var findings = new List<HygieneFinding>();
        void Add(HygieneFinding f) { if (keys.Add(f.Key)) findings.Add(f); }
        string Pair(AppEdge e) => $"'{e.FromName}' -> '{e.ToName}'";
        string Key(AppEdge e) => e.FromModel + "|" + e.ToKey;
        var sup = edges.Where(e => e.Kind == RelationKinds.Supersedes).ToList();
        var dep = edges.Where(e => e.Kind == RelationKinds.DependsOn).ToList();
        foreach (var e in sup)
        {
            if (!e.ToExists)
            {
                Add(F("SUP-01", "Supersedence", Key(e), Pair(e),
                    $"Supersedence on deployment type '{e.FromDt}' references {e.ToKey}, which no longer exists in the site.",
                    "Remove the broken supersedence reference.",
                    guidance: $"Console: '{e.FromName}' Properties > Supersedence tab - remove the reference to the deleted application"));
                continue;
            }
            apps.TryGetValue(e.FromModel, out var from);
            var to = apps[e.ToKey];
            if (to.IsExpired)
                Add(F("SUP-04", "Supersedence", Key(e), Pair(e),
                    $"Superseded application '{e.ToName}' is retired; the rule still exists but its target is inactive.",
                    "Remove the supersedence relationship or delete the retired application once nothing references it.",
                    guidance: $"Console: '{e.FromName}' Properties > Supersedence tab - review the reference to retired '{e.ToName}'"));
            else if (from is { IsEnabled: false })
                Add(F("SUP-03", "Supersedence", Key(e), Pair(e),
                    $"Superseding application '{e.FromName}' is disabled; the replacement cannot deploy while the rule stands.",
                    "Enable the superseding application or remove the supersedence relationship.",
                    guidance: $"Console: enable '{e.FromName}', or remove the supersedence relationship."));
        }
        foreach (var e in CircularEdges(sup))
            Add(F("SUP-02", "Supersedence", Key(e), Pair(e),
                "This supersedence edge is part of a loop: following the chain from the superseded application eventually returns to the superseding one.",
                "Break the loop by removing the relationship that closes it.",
                guidance: $"Console: review the supersedence chain starting at '{e.FromName}' and remove the looping reference"));
        foreach (var e in dep)
        {
            if (!e.ToExists)
            {
                Add(F("DEP-01", "Dependency", Key(e), Pair(e),
                    $"Dependency on deployment type '{e.FromDt}' references {e.ToKey}, which no longer exists in the site.",
                    "Remove the broken dependency; installs of the parent fail while it references a deleted application.",
                    guidance: $"Console: '{e.FromName}' > Deployment Types > '{e.FromDt}' > Dependencies - remove the broken reference"));
                continue;
            }
            var to = apps[e.ToKey];
            if (to.IsExpired)
                Add(F("DEP-04", "Dependency", Key(e), Pair(e),
                    $"Dependency target '{e.ToName}' is retired; {e.DependencyState} installs relying on it will fail.",
                    "Point the dependency at the current application or reinstate the target.",
                    guidance: $"Console: '{e.FromName}' > Deployment Types > '{e.FromDt}' > Dependencies - update the reference to retired '{e.ToName}'"));
            else if (!to.IsEnabled)
                Add(F("DEP-03", "Dependency", Key(e), Pair(e),
                    $"Dependency target '{e.ToName}' is disabled; automatic dependency installs will fail.",
                    "Enable the dependency target or remove the dependency.",
                    guidance: $"Console: enable '{e.ToName}', or remove the dependency."));
            else if (!to.HasContent)
                Add(F("DEP-05", "Dependency", Key(e), Pair(e),
                    $"Dependency target '{e.ToName}' reports HasContent=false. Contentless script deployment types can be valid, so this is not evidence of an installation or distribution failure.",
                    "Verify the target has an enabled deployment type and that its install command does not require packaged source content.",
                    guidance: $"Review the deployment types of '{e.ToName}'; no automatic fix is safe for HasContent=false."));
        }
        foreach (var e in CircularEdges(dep))
            Add(F("DEP-02", "Dependency", Key(e), Pair(e),
                "This dependency edge is part of a loop: the target eventually depends back on the source, which deadlocks automatic installs.",
                "Break the loop by removing one dependency in the cycle.",
                guidance: $"Console: review the dependency chain starting at '{e.FromName}' and remove the looping reference"));
        var participants = new HashSet<string>(edges.Select(e => e.FromModel).Concat(edges.Where(e => e.ToExists).Select(e => e.ToKey)), StringComparer.OrdinalIgnoreCase);
        foreach (var model in participants)
        {
            if (!apps.TryGetValue(model, out var app) || !string.IsNullOrWhiteSpace(app.Manufacturer)) continue;
            Add(F("REL-01", "Application", model, app.Name,
                "Application participates in supersedence/dependency relationships but has no Manufacturer set, which makes relationship views hard to audit.",
                "Fill in the Manufacturer field.",
                guidance: "Console: the application's General Information tab - fill in the publisher."));
        }
        return findings;
    }

    private static IEnumerable<HygieneFinding> ContentPathChecks(HygieneContext c)
    {
        var probes = c.ProbeResults!;
        var apps = AppsByModel(c.Data);
        foreach (var (appModel, dtModel, dtName, path) in ContentLocations(c.Data))
        {
            if (!probes.TryGetValue(path, out var state) || state == HygieneProbe.Ok) continue;
            var appName = apps.TryGetValue(appModel, out var a) ? a.Name : appModel;
            var key = appModel + "|" + dtModel;
            if (state == HygieneProbe.Unknown)
                yield return F("APP-04", "DeploymentType", key, $"{appName} / {dtName}",
                    $"Content source '{path}' did not answer within {N(c.ProbeTimeoutSeconds)}s from this workstation; its state is unknown, not missing.",
                    "Probe the path from the site server, where rights and routes may differ.",
                    guidance: $"From the site server: Test-Path -LiteralPath '{PowerShellLiteral.EscapeSingleQuoted(path)}'", severity: HygieneSeverity.Info);
            else
                yield return F("APP-04", "DeploymentType", key, $"{appName} / {dtName}",
                    $"Content source '{path}' is missing or unreachable from this workstation; the site server may still reach it, but content updates run from a session that cannot will fail.",
                    "Verify from the site server; restore the source folder, correct the deployment type content location, or fix share permissions.",
                    guidance: $"Console: '{appName}' > Deployment Types > '{dtName}' > Content - correct the content location");
        }
    }

    /// <summary>The content source path of each latest deployment type: SMS_DeploymentType.ContentId joined to SMS_Content.ContentUniqueID.</summary>
    internal static IReadOnlyList<(string AppModel, string DtModel, string DtName, string Path)> ContentLocations(HygieneSiteRead data)
    {
        var sources = data.Table(AppContent).Records
            .GroupBy(r => r.S("ContentUniqueID"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().S("ContentSource").Trim(), StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, string, string, string)>();
        foreach (var dt in data.Table(DeploymentTypes).Records)
        {
            if (!dt.B("IsLatest")) continue;
            if (!sources.TryGetValue(dt.S("ContentId"), out var path) || string.IsNullOrWhiteSpace(path)) continue;
            list.Add((dt.S("AppModelName"), dt.S("ModelName"), dt.S("LocalizedDisplayName"), path));
        }
        return list;
    }

    /// <summary>Every edge with its health, as the Relationships inventory shows it. Health precedence follows the SUP and DEP checks.</summary>
    internal static RelationInventory Inventory(HygieneSiteRead data)
    {
        var apps = AppsByModel(data);
        var edges = AppEdges(data);
        var circular = CircularEdges(edges.Where(e => e.Kind == RelationKinds.Supersedes)).Concat(CircularEdges(edges.Where(e => e.Kind == RelationKinds.DependsOn))).ToHashSet();
        var supAdj = Adjacency(edges.Where(e => e.Kind == RelationKinds.Supersedes && e.ToExists).Select(e => new CollectionEdge(e.FromModel, e.ToKey, "")));
        var depth = LongestChains(supAdj);
        var rows = new List<RelationEdge>(edges.Count);
        foreach (var e in edges)
        {
            apps.TryGetValue(e.FromModel, out var from);
            var to = e.ToExists ? apps[e.ToKey] : null;
            var health = !e.ToExists ? RelationHealth.Orphaned
                : to!.IsExpired ? RelationHealth.ExpiredTarget
                : e.Kind == RelationKinds.Supersedes && from is { IsEnabled: false } ? RelationHealth.DisabledSource
                : e.Kind == RelationKinds.DependsOn && !to.IsEnabled ? RelationHealth.DisabledTarget
                : e.Kind == RelationKinds.DependsOn && !to.HasContent ? RelationHealth.MissingContent
                : RelationHealth.Healthy;
            var isCircular = circular.Contains(e);
            if (isCircular && health == RelationHealth.Healthy) health = RelationHealth.Circular;
            rows.Add(new RelationEdge(e.FromModel, e.FromName, e.ToKey, e.ToName, e.Kind, health)
            {
                FromDeploymentType = e.FromDt,
                FromVersion = from?.Version ?? "",
                ToVersion = to?.Version ?? "",
                DependencyState = e.DependencyState,
                ToExists = e.ToExists,
                Circular = isCircular,
                ChainDepth = e.Kind == RelationKinds.Supersedes && e.ToExists ? (depth.TryGetValue(e.ToKey, out var d) ? d : 0) : 0
            });
        }
        var named = new HashSet<string>(edges.Select(e => e.FromModel).Concat(edges.Where(e => e.ToExists).Select(e => e.ToKey)), StringComparer.OrdinalIgnoreCase);
        return new RelationInventory(rows.OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.FromName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ToName, StringComparer.OrdinalIgnoreCase).ToList(), false, null)
        {
            Apps = named.Where(apps.ContainsKey).ToDictionary(m => m, m => { var a = apps[m]; return new RelationApp(m, a.Name, a.Version, a.IsEnabled, a.IsExpired, a.HasContent); }, StringComparer.OrdinalIgnoreCase)
        };
    }
}
