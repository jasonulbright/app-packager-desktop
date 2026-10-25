using System.Text.RegularExpressions;
using static ApplicationPackagerEngine.Services.HygieneSets;

namespace ApplicationPackagerEngine.Services;

internal static partial class HygieneChecks
{
    private static void AddApplications(List<HygieneCheckFamily> list)
    {
        list.Add(new(["APP-01"], ["Applications"], [Applications, AppRevisions, TsReferences, TsSteps, AppDependencies], AppNoReferences));
        list.Add(new(["APP-02", "APP-03"], ["Applications"], [Applications, AppDeployments], AppStillDeployed));
        list.Add(new(["APP-05"], ["Applications"], [Applications, AppRevisions], AppOldRevisions));
        list.Add(new(["PKG-01"], ["Packages"], [Packages, Programs, Deployments, TsReferences, TsSteps], PackageUnused));
    }

    /// <summary>The ModelName of every application CI_ID, latest and older revisions.</summary>
    internal static Dictionary<int, string> ModelByCiId(HygieneContext c)
    {
        var map = new Dictionary<int, string>();
        foreach (var r in c.Rows(Applications).Concat(c.Rows(AppRevisions)))
            if (r.I("CI_ID") is > 0 and var id && r.S("ModelName") is { Length: > 0 } m) map[id] = m;
        return map;
    }

    /// <summary>The PackageIDs and ModelNames the steps of every task sequence use: the reference rows and the References of each sequence.</summary>
    internal static HashSet<string> TsReferenced(HygieneContext c) => Set(c.Rows(TsReferences).Concat(c.Rows(TsSteps)).Select(r => r.S("ObjectID")));

    private static IEnumerable<HygieneFinding> AppNoReferences(HygieneContext c)
    {
        var tsRefs = TsReferenced(c);
        var models = ModelByCiId(c);
        var depTargets = Set(c.Rows(AppDependencies).Select(r => models.TryGetValue(r.I("ToApplicationCIID"), out var m) ? m : ""));
        var minAge = c.T.AppUnusedMinAgeDays;
        var cutoff = c.Now.AddDays(-minAge);
        foreach (var app in c.Rows(Applications))
        {
            if (app.B("IsDeployed") || app.B("IsSuperseding") || app.B("IsSuperseded")) continue;
            // The site keeps NumberOfDependentTS for some minutes after a task sequence stops using the application and refuses the delete until then.
            if (app.I("NumberOfDependentTS") > 0) continue;
            var model = app.S("ModelName");
            if (depTargets.Contains(model) || tsRefs.Contains(model)) continue;
            var created = app.T("DateCreated");
            if (created is { } d && d > cutoff) continue;
            yield return F("APP-01", "Application", model, app.S("LocalizedDisplayName"),
                $"No deployments, no task sequence references, not part of any supersedence relationship, not a dependency target; created {Day(created)}, older than the {N(minAge)}-day grace window.",
                "Candidate for retirement and removal. Verify no out-of-band use (scripts, documentation) before deleting.",
                HygieneFixKind.RemoveApplication,
                Params((HygieneFixParameters.Id, app.S("CI_ID")), (HygieneFixParameters.ModelName, model), (HygieneFixParameters.Days, N(minAge))));
        }
    }

    /// <summary>The AssignmentUniqueIDs of the application deployments of each ModelID, in AssignmentID order.</summary>
    internal static Dictionary<string, List<string>> DeploymentsByModelId(HygieneContext c) =>
        c.Rows(AppDeployments).GroupBy(r => r.S("AppModelID")).ToDictionary(g => g.Key, g => g.OrderBy(r => r.L("AssignmentID")).Select(r => r.S("AssignmentUniqueID")).ToList());

    private static IEnumerable<HygieneFinding> AppStillDeployed(HygieneContext c)
    {
        var byModelId = DeploymentsByModelId(c);
        foreach (var app in c.Rows(Applications))
        {
            if (!app.B("IsDeployed")) continue;
            var model = app.S("ModelName");
            var ids = byModelId.TryGetValue(app.S("ModelID"), out var l) ? string.Join(",", l) : "";
            var p = Params((HygieneFixParameters.ModelName, model), (HygieneFixParameters.DeploymentIds, ids));
            if (app.B("IsExpired"))
                yield return F("APP-02", "Application", model, app.S("LocalizedDisplayName"),
                    "Application is retired (expired) but still has active deployments; clients targeted by them cannot install it.",
                    "Remove the deployments, or reinstate the application if retiring it was a mistake.",
                    HygieneFixKind.RemoveApplicationDeployment, p);
            else if (app.B("IsSuperseded"))
                yield return F("APP-03", "Application", model, app.S("LocalizedDisplayName"),
                    "Application is superseded by a newer application but its own deployments are still active.",
                    "Deploy the superseding application and retire these deployments so clients converge on the replacement.",
                    HygieneFixKind.RemoveApplicationDeployment, p);
        }
    }

    private static IEnumerable<HygieneFinding> AppOldRevisions(HygieneContext c)
    {
        var old = c.Rows(AppRevisions).Where(r => r.S("ModelName").Length > 0 && r.I("CIVersion") > 0)
            .GroupBy(r => r.S("ModelName"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => r.I("CIVersion")).Distinct().Order().ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var app in c.Rows(Applications))
        {
            var model = app.S("ModelName");
            if (model.Length == 0 || !old.TryGetValue(model, out var revisions) || revisions.Count == 0) continue;
            var text = string.Join(", ", revisions.Select(r => N(r)));
            yield return F("APP-05", "Application", model, app.S("LocalizedDisplayName"),
                $"The application holds {Count(revisions.Count, "old revision")}: {text}. Clients use the current revision only. The site keeps the definition of each old revision.",
                "Delete the old revisions. Keep one only if you plan to restore it.",
                HygieneFixKind.RemoveApplicationRevisionHistory,
                Params((HygieneFixParameters.Id, app.S("CI_ID")), (HygieneFixParameters.ModelName, model), (HygieneFixParameters.Revisions, string.Join(",", revisions.Select(r => N(r))))));
        }
    }

    internal static readonly Regex ClientPackageName = new("^Configuration Manager Client", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Standard packages only: Get-CMPackage returns PackageType 0.</summary>
    internal static IEnumerable<HygieneRow> StandardPackages(HygieneContext c) => c.Rows(Packages).Where(r => r.I("PackageType", 0) == 0);

    private static IEnumerable<HygieneFinding> PackageUnused(HygieneContext c)
    {
        var withPrograms = Set(c.Rows(Programs).Select(r => r.S("PackageID")));
        var deployed = Set(c.Rows(Deployments).Select(r => r.S("PackageID")));
        var tsRefs = TsReferenced(c);
        foreach (var pkg in StandardPackages(c))
        {
            var name = pkg.S("Name");
            if (ClientPackageName.IsMatch(name)) continue;
            var id = pkg.S("PackageID");
            if (withPrograms.Contains(id) || deployed.Contains(id) || tsRefs.Contains(id)) continue;
            yield return F("PKG-01", "Package", id, name,
                "Package has no programs, no deployments, and no task sequence references.",
                "Candidate for deletion; its content still occupies distribution points and the content library.",
                HygieneFixKind.RemovePackage, Params((HygieneFixParameters.Id, id)));
        }
    }
}
