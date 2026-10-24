using System.Globalization;
using static ApplicationPackagerEngine.Services.HygieneSets;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// One write of a fix on one object, by its exact identifier. <paramref name="Target"/> is what the session
/// script receives; <paramref name="Line"/> names the object for the plan and the results.
/// </summary>
internal sealed record HygieneWrite(HygieneTarget Line, IReadOnlyDictionary<string, string> Target, HygieneCommand Command);

/// <summary>Whether a finding still holds on fresh site data, and the writes its fix makes now.</summary>
internal sealed record HygieneEvaluation(bool Holds, string Reason, IReadOnlyList<HygieneWrite> Writes)
{
    public static HygieneEvaluation No(string reason) => new(false, reason, []);
}

/// <summary>
/// The re-evaluation of each fix kind on the facts the State operation of the script reads: the rule of the
/// check that found the object, and for a delete every reference the site holds to the object, whether or
/// not the check looked at it.
/// </summary>
internal static class HygieneFixRules
{
    /// <summary>The identifiers the State read needs for a finding.</summary>
    public static IEnumerable<string> StateKeys(HygieneFixKind kind, HygieneFinding f)
    {
        var p = f.FixParameters;
        string Get(string n) => p.TryGetValue(n, out var v) ? v : "";
        switch (kind)
        {
            case HygieneFixKind.RemoveApplicationDeployment when Get(HygieneFixParameters.ModelName).Length == 0:
                foreach (var d in Get(HygieneFixParameters.DeploymentIds).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) yield return d;
                break;
            case HygieneFixKind.RemoveApplication or HygieneFixKind.RemoveApplicationDeployment or HygieneFixKind.RemoveApplicationRevisionHistory or HygieneFixKind.SetApplication:
                yield return Get(HygieneFixParameters.ModelName);
                break;
            case HygieneFixKind.RemoveMaintenanceWindow:
                yield return Get(HygieneFixParameters.CollectionId);
                break;
            default:
                yield return Get(HygieneFixParameters.Id);
                break;
        }
    }

    private static string P(HygieneFinding f, string name) => f.FixParameters.TryGetValue(name, out var v) ? v ?? "" : "";

    /// <summary>The checks whose findings each fix kind runs; a finding of another check is refused, whatever its parameters say.</summary>
    internal static readonly IReadOnlyDictionary<HygieneFixKind, string[]> ChecksOf = new Dictionary<HygieneFixKind, string[]>
    {
        [HygieneFixKind.RemoveApplication] = ["APP-01"],
        [HygieneFixKind.RemoveApplicationDeployment] = ["APP-02", "APP-03", "DPL-01"],
        [HygieneFixKind.RemoveApplicationRevisionHistory] = ["APP-05"],
        [HygieneFixKind.RemovePackage] = ["PKG-01"],
        [HygieneFixKind.RemoveCollection] = ["COL-01"],
        [HygieneFixKind.RemoveDevice] = ["DEV-01"],
        [HygieneFixKind.RemoveBootImage] = ["TSQ-02"],
        [HygieneFixKind.RemoveDriverPackage] = ["TSQ-02"],
        [HygieneFixKind.RemoveAutoDeploymentRule] = ["UPD-03"],
        [HygieneFixKind.RemoveMaintenanceWindow] = ["COL-10"],
        [HygieneFixKind.SetCollectionRefreshType] = ["COL-05", "COL-06"],
        [HygieneFixKind.SetApplication] = ["TSQ-03"],
        [HygieneFixKind.SetSiteMaintenanceTask] = ["MNT-01", "MNT-02"],
        [HygieneFixKind.RunAutoDeploymentRule] = ["UPD-03"]
    };

    /// <summary>
    /// The refusal of a finding whose check and parameters do not match the fix kind as the scan assigns
    /// it; null when they match. A finding can come from the stored last scan or another caller, so the
    /// service never trusts the pair.
    /// </summary>
    public static string? Gate(HygieneFixKind kind, HygieneFinding f)
    {
        var check = (f.CheckId ?? "").Trim().ToUpperInvariant();
        if (!ChecksOf.TryGetValue(kind, out var checks) || !checks.Contains(check, StringComparer.Ordinal))
            return $"check {(check.Length > 0 ? check : "(none)")} has no fix of this kind on this page.";
        var hasModel = P(f, HygieneFixParameters.ModelName).Length > 0;
        return (kind, check) switch
        {
            (HygieneFixKind.RemoveApplicationDeployment, "APP-02" or "APP-03") when !hasModel => "the finding names no application.",
            (HygieneFixKind.RemoveApplicationDeployment, "DPL-01") when hasModel || P(f, HygieneFixParameters.DeploymentIds).Split(',', StringSplitOptions.RemoveEmptyEntries).Length != 1
                => "an expired-deployment finding names exactly one deployment.",
            (HygieneFixKind.SetCollectionRefreshType, "COL-05") when P(f, HygieneFixParameters.RefreshType) != "Continuous" => "COL-05 sets the refresh type Continuous only.",
            (HygieneFixKind.SetCollectionRefreshType, "COL-06") when P(f, HygieneFixParameters.RefreshType) != "Manual" => "COL-06 sets the refresh type Manual only.",
            (HygieneFixKind.SetSiteMaintenanceTask, "MNT-01") when !HygieneChecks.RecommendedTasks.Contains(P(f, HygieneFixParameters.Id), StringComparer.OrdinalIgnoreCase)
                => "MNT-01 enables only the recommended cleanup tasks.",
            (HygieneFixKind.SetSiteMaintenanceTask, "MNT-02") when !HygieneChecks.BackupTasks.Contains(P(f, HygieneFixParameters.Id), StringComparer.OrdinalIgnoreCase)
                => "MNT-02 enables only the site backup task.",
            _ => null
        };
    }

    /// <summary>
    /// The day window a rule uses: the finding's value, but never less than the service threshold, so a
    /// finding cannot move a delete or run guard closer to now.
    /// </summary>
    private static int Days(HygieneFinding f, int threshold) =>
        int.TryParse(P(f, HygieneFixParameters.Days), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && d > threshold ? d : threshold;

    private static Dictionary<string, string> T(params (string Name, string Value)[] values) => values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

    private static HygieneWrite Write(HygieneFinding f, string type, string key, string name, string detail, HygieneFixKind kind, Dictionary<string, string> parameters, Dictionary<string, string> target) =>
        new(new HygieneTarget(f.Key, type, key, name, detail), target, HygieneFixText.Commands(kind, parameters).Single());

    public static HygieneEvaluation Evaluate(HygieneFixKind kind, HygieneFinding f, HygieneSiteRead s, DateTime now, HygieneThresholds t) => kind switch
    {
        HygieneFixKind.RemoveApplication => RemoveApplication(f, s, now, t),
        _ when Gate(kind, f) is { } refusal => HygieneEvaluation.No(refusal),
        HygieneFixKind.RemoveApplicationDeployment => string.Equals(f.CheckId, "DPL-01", StringComparison.OrdinalIgnoreCase) ? RemoveExpiredDeployment(f, s, now) : RemoveAppDeployments(f, s),
        HygieneFixKind.RemoveApplicationRevisionHistory => RemoveRevisions(f, s),
        HygieneFixKind.RemovePackage => RemovePackage(f, s),
        HygieneFixKind.RemoveBootImage or HygieneFixKind.RemoveDriverPackage => RemoveOsdObject(kind, f, s),
        HygieneFixKind.RemoveCollection => RemoveCollection(f, s),
        HygieneFixKind.RemoveDevice => RemoveDevice(f, s, now, t),
        HygieneFixKind.RemoveAutoDeploymentRule => RemoveRule(f, s),
        HygieneFixKind.RemoveMaintenanceWindow => RemoveWindow(f, s, now),
        HygieneFixKind.SetCollectionRefreshType => SetRefreshType(f, s),
        HygieneFixKind.SetApplication => SetAutoInstall(f, s),
        HygieneFixKind.SetSiteMaintenanceTask => EnableTask(f, s),
        HygieneFixKind.RunAutoDeploymentRule => RunRule(f, s, now, t),
        _ => HygieneEvaluation.No("this fix kind is report-only on this page.")
    };

    private static HygieneRow? Latest(HygieneSiteRead s, string model) =>
        s.Table(Applications).Records.Where(r => string.Equals(r.S("ModelName"), model, StringComparison.OrdinalIgnoreCase)).Select(r => (HygieneRow?)r).FirstOrDefault();

    private static HashSet<int> CiIds(HygieneSiteRead s, string model) =>
        s.Table(AppRevisions).Records.Where(r => string.Equals(r.S("ModelName"), model, StringComparison.OrdinalIgnoreCase)).Select(r => r.I("CI_ID"))
            .Concat(s.Table(Applications).Records.Where(r => string.Equals(r.S("ModelName"), model, StringComparison.OrdinalIgnoreCase)).Select(r => r.I("CI_ID")))
            .Where(i => i > 0).ToHashSet();

    private static List<HygieneRow> AssignmentsOf(HygieneSiteRead s, HygieneRow app) =>
        s.Table(AppDeployments).Records.Where(r => r.S("AppModelID") == app.S("ModelID")).OrderBy(r => r.L("AssignmentID")).ToList();

    private static HygieneEvaluation RemoveApplication(HygieneFinding f, HygieneSiteRead s, DateTime now, HygieneThresholds t)
    {
        var model = P(f, HygieneFixParameters.ModelName);
        if (Latest(s, model) is not { } app) return HygieneEvaluation.No("the application no longer exists.");
        var ci = CiIds(s, model);
        var deployments = AssignmentsOf(s, app).Count;
        if (deployments > 0) return HygieneEvaluation.No($"the application has {Count(deployments, "deployment")} now.");
        if (app.B("IsDeployed")) return HygieneEvaluation.No("the site still reports the application as deployed. The site updates this flag some time after a deployment is removed; scan again later.");
        if (app.B("IsSuperseded") || app.B("IsSuperseding")) return HygieneEvaluation.No("the application is part of a supersedence relationship now.");
        var dependents = s.Table(AppDependencies).Records.Count(r => ci.Contains(r.I("ToApplicationCIID")));
        var relations = s.Table(AppRelations).Records.Count(r => ci.Contains(r.I("ToApplicationCIID")));
        if (dependents + relations > 0) return HygieneEvaluation.No("another application depends on or supersedes this application now.");
        var sequences = s.Table(TsReferences).Records.Count(r => string.Equals(r.S("ObjectID"), model, StringComparison.OrdinalIgnoreCase));
        if (sequences > 0) return HygieneEvaluation.No($"{Count(sequences, "task sequence reference")} to the application exist now.");
        var minAge = Days(f, t.AppUnusedMinAgeDays);
        if (app.T("DateCreated") is { } created && created > now.AddDays(-minAge)) return HygieneEvaluation.No($"the application is younger than {minAge.ToString(CultureInfo.InvariantCulture)} days.");
        var id = app.S("CI_ID");
        var p = T((HygieneFixParameters.Id, id));
        return new(true, "", [Write(f, "Application", model, app.S("LocalizedDisplayName"), $"CI_ID {id}, revision {app.S("CIVersion")}", HygieneFixKind.RemoveApplication, p, T(("Id", id)))]);
    }

    private static HygieneEvaluation RemoveAppDeployments(HygieneFinding f, HygieneSiteRead s)
    {
        var model = P(f, HygieneFixParameters.ModelName);
        if (Latest(s, model) is not { } app) return HygieneEvaluation.No("the application no longer exists.");
        if (f.CheckId == "APP-02" && !app.B("IsExpired")) return HygieneEvaluation.No("the application is not retired any more.");
        if (f.CheckId == "APP-03" && (!app.B("IsSuperseded") || app.B("IsExpired"))) return HygieneEvaluation.No(app.B("IsExpired") ? "the application is retired now; APP-02 covers it." : "the application is not superseded any more.");
        var assignments = AssignmentsOf(s, app);
        if (assignments.Count == 0) return HygieneEvaluation.No("the application has no deployment now.");
        var writes = assignments.Select(a =>
        {
            var id = a.S("AssignmentUniqueID");
            return Write(f, "Deployment", a.S("AssignmentID"), $"{a.S("ApplicationName")} -> {a.S("CollectionName")}", $"Deployment {a.S("AssignmentID")} to {a.S("CollectionName")} ({a.S("TargetCollectionID")})",
                HygieneFixKind.RemoveApplicationDeployment, T((HygieneFixParameters.DeploymentIds, id)), T(("Id", id)));
        }).ToList();
        return new(true, "", writes);
    }

    private static HygieneEvaluation RemoveExpiredDeployment(HygieneFinding f, HygieneSiteRead s, DateTime now)
    {
        var id = P(f, HygieneFixParameters.DeploymentIds);
        var row = s.Table("AppDeploymentsById").Records.Where(r => string.Equals(r.S("AssignmentUniqueID"), id, StringComparison.OrdinalIgnoreCase)).Select(r => (HygieneRow?)r).FirstOrDefault();
        if (row is not { } a) return HygieneEvaluation.No("the deployment no longer exists.");
        if (a.T("ExpirationTime") is not { } expires || expires >= now) return HygieneEvaluation.No("the deployment has no past expiration time now.");
        return new(true, "", [Write(f, "Deployment", a.S("AssignmentID"), $"{a.S("ApplicationName")} -> {a.S("CollectionName")}", $"Deployment {a.S("AssignmentID")} to {a.S("CollectionName")} ({a.S("TargetCollectionID")})",
            HygieneFixKind.RemoveApplicationDeployment, T((HygieneFixParameters.DeploymentIds, a.S("AssignmentUniqueID"))), T(("Id", a.S("AssignmentUniqueID"))))]);
    }

    private static HygieneEvaluation RemoveRevisions(HygieneFinding f, HygieneSiteRead s)
    {
        var model = P(f, HygieneFixParameters.ModelName);
        var rows = s.Table(AppRevisions).Records.Where(r => string.Equals(r.S("ModelName"), model, StringComparison.OrdinalIgnoreCase)).ToList();
        var latest = rows.Where(r => r.B("IsLatest")).Select(r => (HygieneRow?)r).FirstOrDefault();
        if (latest is not { } l) return HygieneEvaluation.No("the application no longer exists.");
        var wanted = P(f, HygieneFixParameters.Revisions).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var old = rows.Where(r => !r.B("IsLatest") && wanted.Contains(r.S("CIVersion"))).OrderBy(r => r.I("CIVersion")).ToList();
        if (old.Count == 0) return HygieneEvaluation.No("none of the old revisions of the plan exists now.");
        var id = l.S("CI_ID");
        return new(true, "", old.Select(r => Write(f, "Revision", model + "|" + r.S("CIVersion"), $"{f.ObjectName} revision {r.S("CIVersion")}", $"Revision {r.S("CIVersion")} of {l.S("CIVersion")} (CI_ID {r.S("CI_ID")})",
            HygieneFixKind.RemoveApplicationRevisionHistory, T((HygieneFixParameters.Id, id), (HygieneFixParameters.Revisions, r.S("CIVersion"))), T(("Id", id), ("Revision", r.S("CIVersion"))))).ToList());
    }

    private static int ReferencesTo(HygieneSiteRead s, string id) =>
        s.Table(TsReferences).Records.Count(r => string.Equals(r.S("ObjectID"), id, StringComparison.OrdinalIgnoreCase))
        + s.Table(TaskSequences).Records.Count(r => string.Equals(r.S("BootImageID"), id, StringComparison.OrdinalIgnoreCase));

    private static HygieneEvaluation RemovePackage(HygieneFinding f, HygieneSiteRead s)
    {
        var id = P(f, HygieneFixParameters.Id);
        var row = s.Table("Objects").Records.Where(r => string.Equals(r.S("PackageID"), id, StringComparison.OrdinalIgnoreCase)).Select(r => (HygieneRow?)r).FirstOrDefault();
        if (row is not { } pkg || pkg.I("PackageType", 0) != 0) return HygieneEvaluation.No("the package no longer exists.");
        if (HygieneChecks.ClientPackageName.IsMatch(pkg.S("Name"))) return HygieneEvaluation.No("the package is a Configuration Manager client package.");
        var programs = s.Table(Programs).Records.Count(r => string.Equals(r.S("PackageID"), id, StringComparison.OrdinalIgnoreCase));
        if (programs > 0) return HygieneEvaluation.No($"the package has {Count(programs, "program")} now.");
        var deployments = s.Table(Deployments).Records.Count(r => string.Equals(r.S("PackageID"), id, StringComparison.OrdinalIgnoreCase));
        if (deployments > 0) return HygieneEvaluation.No($"the package has {Count(deployments, "deployment")} now.");
        var refs = ReferencesTo(s, id);
        if (refs > 0) return HygieneEvaluation.No($"{Count(refs, "task sequence reference")} to the package exist now.");
        return new(true, "", [Write(f, "Package", id, pkg.S("Name"), $"Package {id}", HygieneFixKind.RemovePackage, T((HygieneFixParameters.Id, id)), T(("Id", id)))]);
    }

    private static HygieneEvaluation RemoveOsdObject(HygieneFixKind kind, HygieneFinding f, HygieneSiteRead s)
    {
        var id = P(f, HygieneFixParameters.Id);
        var noun = kind == HygieneFixKind.RemoveBootImage ? "boot image" : "driver package";
        var row = s.Table("Objects").Records.Where(r => string.Equals(r.S("PackageID"), id, StringComparison.OrdinalIgnoreCase)).Select(r => (HygieneRow?)r).FirstOrDefault();
        if (row is not { } obj) return HygieneEvaluation.No($"the {noun} no longer exists.");
        if (kind == HygieneFixKind.RemoveBootImage && HygieneChecks.DefaultBootImage.IsMatch(obj.S("Name"))) return HygieneEvaluation.No("the boot image is a default boot image of the site.");
        var refs = ReferencesTo(s, id);
        if (refs > 0) return HygieneEvaluation.No($"{Count(refs, "task sequence reference")} to the {noun} exist now.");
        if (kind == HygieneFixKind.RemoveDriverPackage)
        {
            if (!s.IsRead(AutoApplySequences)) return HygieneEvaluation.No("the steps of the task sequences could not be read, so the page does not delete a driver package.");
            if (s.Table(AutoApplySequences).Records.Count() is > 0 and var auto)
                return HygieneEvaluation.No($"{Count(auto, "task sequence")} {(auto == 1 ? "has" : "have")} an Auto Apply Drivers step, which can use the content of any driver package without a reference.");
        }
        return new(true, "", [Write(f, kind == HygieneFixKind.RemoveBootImage ? "BootImage" : "DriverPackage", id, obj.S("Name"), $"{char.ToUpperInvariant(noun[0])}{noun[1..]} {id}", kind, T((HygieneFixParameters.Id, id)), T(("Id", id)))]);
    }

    private static HygieneEvaluation RemoveCollection(HygieneFinding f, HygieneSiteRead s)
    {
        var id = P(f, HygieneFixParameters.Id);
        if (HygieneChecks.IsBuiltIn(id)) return HygieneEvaluation.No("the collection is a built-in collection.");
        var row = s.Table(Collections).Records.Where(r => string.Equals(r.S("CollectionID"), id, StringComparison.OrdinalIgnoreCase)).Select(r => (HygieneRow?)r).FirstOrDefault();
        if (row is not { } col) return HygieneEvaluation.No("the collection no longer exists.");
        if (col.I("MemberCount", -1) != 0) return HygieneEvaluation.No($"the collection has {Count(Math.Max(0, col.I("MemberCount")), "member")} now.");
        var deployments = s.Table(Deployments).Records.Count(r => string.Equals(r.S("CollectionID"), id, StringComparison.OrdinalIgnoreCase));
        if (deployments > 0) return HygieneEvaluation.No($"{Count(deployments, "deployment")} target the collection now.");
        var refs = s.Table(CollectionDependencies).Records.Count(r => string.Equals(r.S("SourceCollectionID"), id, StringComparison.OrdinalIgnoreCase));
        if (refs > 0) return HygieneEvaluation.No($"{Count(refs, "collection")} include, exclude or limit to the collection now.");
        if (s.Table(CollectionSettings).Records.Any(r => string.Equals(r.S("CollectionID"), id, StringComparison.OrdinalIgnoreCase))) return HygieneEvaluation.No("the collection has variables or maintenance windows now.");
        foreach (var set in CollectionUsers)
            if (!s.IsRead(set)) return HygieneEvaluation.No("the objects that can use the collection could not be read, so the page does not delete it.");
        int Uses(string set, string column) => s.Table(set).Records.Count(r => string.Equals(r.S(column), id, StringComparison.OrdinalIgnoreCase));
        if (Uses(CollectionAssignments, "TargetCollectionID") is > 0 and var assignments) return HygieneEvaluation.No($"{Count(assignments, "deployment")} of an application, a software update, a baseline or a policy target the collection now.");
        if (Uses(CollectionRules, "CollectionID") is > 0 and var rules) return HygieneEvaluation.No($"{Count(rules, "automatic deployment rule")} {(rules == 1 ? "targets" : "target")} the collection.");
        if (Uses(CollectionClientSettings, "CollectionID") is > 0 and var settings) return HygieneEvaluation.No($"{Count(settings, "client settings deployment")} {(settings == 1 ? "targets" : "target")} the collection.");
        if (Uses(CollectionAdmins, "CategoryID") is > 0 and var admins) return HygieneEvaluation.No($"{Count(admins, "administrative user")} {(admins == 1 ? "has" : "have")} the collection in {(admins == 1 ? "its" : "their")} assigned collections.");
        return new(true, "", [Write(f, "Collection", id, col.S("Name"), $"Collection {id}", HygieneFixKind.RemoveCollection, T((HygieneFixParameters.Id, id)), T(("Id", id)))]);
    }

    /// <summary>The Heartbeat Discovery time of a device from SMS_R_System.AgentName and AgentTime; null when the site holds none.</summary>
    internal static DateTime? HeartbeatOf(HygieneRow agents)
    {
        var names = agents.List("AgentName");
        var times = agents.List("AgentTime");
        for (var i = 0; i < names.Length && i < times.Length; i++)
            if (string.Equals(names[i], "Heartbeat Discovery", StringComparison.OrdinalIgnoreCase)) return HygieneSiteRead.Time(times[i]);
        return null;
    }

    private static HygieneEvaluation RemoveDevice(HygieneFinding f, HygieneSiteRead s, DateTime now, HygieneThresholds t)
    {
        var id = P(f, HygieneFixParameters.Id);
        var days = Days(f, t.InactiveDeviceDays);
        var cutoff = now.AddDays(-days);
        var row = s.Table(Devices).Records.Where(r => r.S("ResourceID") == id).Select(r => (HygieneRow?)r).FirstOrDefault();
        if (row is not { } device) return HygieneEvaluation.No("the device record no longer exists.");
        if (!device.B("IsClient")) return HygieneEvaluation.No("the record has no client now; DEV-01 judges client records only.");
        if (device.T("LastActiveTime") is not { } last) return HygieneEvaluation.No("the site holds no last active time for the device now.");
        if (last >= cutoff) return HygieneEvaluation.No($"the client was active {last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}, inside the {days.ToString(CultureInfo.InvariantCulture)}-day window.");
        var agents = s.Table(DeviceAgents).Records.Where(r => r.S("ResourceId") == id || r.S("ResourceID") == id).Select(r => (HygieneRow?)r).FirstOrDefault();
        if (agents is { } a && HeartbeatOf(a) is { } heartbeat && heartbeat >= cutoff)
            return HygieneEvaluation.No($"the client sent a heartbeat {heartbeat.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}, inside the {days.ToString(CultureInfo.InvariantCulture)}-day window.");
        return new(true, "", [Write(f, "Device", id, device.S("Name"), $"ResourceID {id}, last active {last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", HygieneFixKind.RemoveDevice, T((HygieneFixParameters.Id, id)), T(("Id", id)))]);
    }

    private static HygieneRow? Rule(HygieneSiteRead s, string id) =>
        s.Table(AutoDeploymentRules).Records.Where(r => r.S("AutoDeploymentID") == id).Select(r => (HygieneRow?)r).FirstOrDefault();

    private static HygieneEvaluation RemoveRule(HygieneFinding f, HygieneSiteRead s)
    {
        var id = P(f, HygieneFixParameters.Id);
        if (Rule(s, id) is not { } r) return HygieneEvaluation.No("the automatic deployment rule no longer exists.");
        if (r.I("LastErrorCode") != 0) return HygieneEvaluation.No("the rule's last run ended with an error now; fix the error first.");
        if (r.S("AutoDeploymentEnabled").Length == 0 || r.B("AutoDeploymentEnabled")) return HygieneEvaluation.No("the rule is enabled now.");
        return new(true, "", [Write(f, "ADR", id, r.S("Name"), $"Rule {id}", HygieneFixKind.RemoveAutoDeploymentRule, T((HygieneFixParameters.Id, id)), T(("Id", id)))]);
    }

    private static HygieneEvaluation RunRule(HygieneFinding f, HygieneSiteRead s, DateTime now, HygieneThresholds t)
    {
        var id = P(f, HygieneFixParameters.Id);
        if (Rule(s, id) is not { } r) return HygieneEvaluation.No("the automatic deployment rule no longer exists.");
        if (r.I("LastErrorCode") != 0) return HygieneEvaluation.No("the rule's last run ended with an error now; fix the error first.");
        if (r.S("AutoDeploymentEnabled").Length > 0 && !r.B("AutoDeploymentEnabled")) return HygieneEvaluation.No("the rule is disabled now.");
        var days = Days(f, t.AdrStaleDays);
        if (r.T("LastRunTime") is not { } last || last >= now.AddDays(-days)) return HygieneEvaluation.No("the rule ran recently.");
        return new(true, "", [Write(f, "ADR", id, r.S("Name"), $"Rule {id}, last run {last.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}", HygieneFixKind.RunAutoDeploymentRule, T((HygieneFixParameters.Id, id)), T(("Id", id)))]);
    }

    private static HygieneEvaluation RemoveWindow(HygieneFinding f, HygieneSiteRead s, DateTime now)
    {
        var cid = P(f, HygieneFixParameters.CollectionId);
        var wid = P(f, HygieneFixParameters.WindowId);
        var row = s.Table(MaintenanceWindows).Records.Where(r => string.Equals(r.S("CollectionID"), cid, StringComparison.OrdinalIgnoreCase) && string.Equals(r.S("ServiceWindowID"), wid, StringComparison.OrdinalIgnoreCase)).Select(r => (HygieneRow?)r).FirstOrDefault();
        if (row is not { } w) return HygieneEvaluation.No("the maintenance window no longer exists.");
        if (w.I("RecurrenceType") != 1 || w.T("StartTime") is not { } start) return HygieneEvaluation.No("the window recurs now.");
        if (start.AddMinutes(w.I("Duration")).AddDays(1) >= now) return HygieneEvaluation.No("the window has not ended a day ago now.");
        return new(true, "", [Write(f, "MaintenanceWindow", cid + "|" + wid, w.S("Name"), $"Window {wid} of collection {cid}", HygieneFixKind.RemoveMaintenanceWindow,
            T((HygieneFixParameters.CollectionId, cid), (HygieneFixParameters.WindowId, wid)), T(("Id", cid), ("WindowId", wid)))]);
    }

    private static HygieneEvaluation SetRefreshType(HygieneFinding f, HygieneSiteRead s)
    {
        var id = P(f, HygieneFixParameters.Id);
        var wanted = P(f, HygieneFixParameters.RefreshType);
        var row = s.Table(Collections).Records.Where(r => string.Equals(r.S("CollectionID"), id, StringComparison.OrdinalIgnoreCase)).Select(r => (HygieneRow?)r).FirstOrDefault();
        if (row is not { } col) return HygieneEvaluation.No("the collection no longer exists.");
        var detail = s.Table(CollectionDetails).Records.Where(r => string.Equals(r.S("CollectionID"), id, StringComparison.OrdinalIgnoreCase)).Select(r => (HygieneRow?)r).FirstOrDefault();
        var refresh = col.I("RefreshType");
        if (wanted == "Continuous")
        {
            if (refresh != 6 || detail is not { } d || (d.I("MinuteSpan") <= 0 && d.I("HourSpan") <= 0)) return HygieneEvaluation.No("the collection has no sub-daily full schedule with incremental updates now.");
        }
        else if (wanted == "Manual")
        {
            if (refresh is not (2 or 6) || detail is not { } d || d.I("DirectRules") <= 0 || d.I("QueryRules") > 0) return HygieneEvaluation.No("the collection is not a scheduled direct-rule-only collection now.");
            if (s.Table(CollectionDependencies).Records.Any(r => string.Equals(r.S("DependentCollectionID"), id, StringComparison.OrdinalIgnoreCase) && r.I("RelationshipType") is 2 or 3))
                return HygieneEvaluation.No("the collection has include or exclude rules now.");
            var limit = s.Table("Limiting").Records.Where(r => string.Equals(r.S("CollectionID"), col.S("LimitToCollectionID"), StringComparison.OrdinalIgnoreCase)).Select(r => (HygieneRow?)r).FirstOrDefault();
            if (limit is { } l && l.I("RefreshType") is 4 or 6) return HygieneEvaluation.No("the limiting collection is incrementally updated now.");
        }
        else return HygieneEvaluation.No("the plan names no refresh type.");
        return new(true, "", [Write(f, "Collection", id, col.S("Name"), $"Collection {id}: refresh type {RefreshName(refresh)} to {wanted}", HygieneFixKind.SetCollectionRefreshType,
            T((HygieneFixParameters.Id, id), (HygieneFixParameters.RefreshType, wanted)), T(("Id", id), ("RefreshType", wanted)))]);
    }

    internal static string RefreshName(int refreshType) => refreshType switch
    {
        1 => "Manual",
        2 => "Periodic",
        4 => "Continuous",
        6 => "Both",
        _ => refreshType.ToString(CultureInfo.InvariantCulture)
    };

    private static HygieneEvaluation SetAutoInstall(HygieneFinding f, HygieneSiteRead s)
    {
        var model = P(f, HygieneFixParameters.ModelName);
        if (Latest(s, model) is not { } app) return HygieneEvaluation.No("the application no longer exists.");
        var def = s.Table(TsAppDefinitions).Records.Where(r => string.Equals(r.S("ModelName"), model, StringComparison.OrdinalIgnoreCase)).Select(r => (HygieneRow?)r).FirstOrDefault();
        if (def is not { } d) return HygieneEvaluation.No("the application definition could not be read.");
        if (d.B("AutoInstall")) return HygieneEvaluation.No("the application has the task sequence install setting now.");
        if (!s.Table(TsReferences).Records.Any(r => string.Equals(r.S("ObjectID"), model, StringComparison.OrdinalIgnoreCase))) return HygieneEvaluation.No("no task sequence installs the application now.");
        return new(true, "", [Write(f, "Application", model, app.S("LocalizedDisplayName"), $"CI_ID {app.S("CI_ID")}, revision {app.S("CIVersion")}: the change makes a new revision", HygieneFixKind.SetApplication,
            T((HygieneFixParameters.ModelName, model)), T(("Id", model)))]);
    }

    private static HygieneEvaluation EnableTask(HygieneFinding f, HygieneSiteRead s)
    {
        var name = P(f, HygieneFixParameters.Id);
        var site = P(f, HygieneFixParameters.SiteCode);
        var row = s.Table(MaintenanceTasks).Records.Where(r => string.Equals(r.S("ItemName"), name, StringComparison.OrdinalIgnoreCase)).Select(r => (HygieneRow?)r).FirstOrDefault();
        if (row is not { } task) return HygieneEvaluation.No("the site has no maintenance task of that name now.");
        if (task.B("Enabled")) return HygieneEvaluation.No("the task is enabled now.");
        var code = task.S("SiteCode").Length > 0 ? task.S("SiteCode") : site;
        return new(true, "", [Write(f, "MaintenanceTask", name, name, $"Task of site {code}", HygieneFixKind.SetSiteMaintenanceTask,
            T((HygieneFixParameters.Id, name), (HygieneFixParameters.SiteCode, code)), T(("Id", name)))]);
    }

    /// <summary>The read-back of one write: true when the object shows the result the write asked for.</summary>
    public static bool ReadBackDone(HygieneFixKind kind, HygieneWrite w, HygieneSiteRead s, string ruleRunBefore)
    {
        var id = w.Target["Id"];
        bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        return kind switch
        {
            HygieneFixKind.RemoveApplication => !s.Table(AppRevisions).Records.Any(r => r.S("CI_ID") == id) && !s.Table(Applications).Records.Any(r => r.S("CI_ID") == id),
            HygieneFixKind.RemoveApplicationDeployment => !s.Table(AppDeployments).Records.Concat(s.Table("AppDeploymentsById").Records).Any(r => Eq(r.S("AssignmentUniqueID"), id)),
            HygieneFixKind.RemoveApplicationRevisionHistory => !s.Table(AppRevisions).Records.Any(r => r.S("CIVersion") == w.Target["Revision"]),
            HygieneFixKind.RemovePackage or HygieneFixKind.RemoveBootImage or HygieneFixKind.RemoveDriverPackage => !s.Table("Objects").Records.Any(r => Eq(r.S("PackageID"), id)),
            HygieneFixKind.RemoveCollection => !s.Table(Collections).Records.Any(r => Eq(r.S("CollectionID"), id)),
            HygieneFixKind.RemoveDevice => !s.Table(Devices).Records.Any(r => r.S("ResourceID") == id) && !s.Table(DeviceAgents).Records.Any(r => r.S("ResourceId") == id),
            HygieneFixKind.RemoveAutoDeploymentRule => !s.Table(AutoDeploymentRules).Records.Any(r => r.S("AutoDeploymentID") == id),
            HygieneFixKind.RemoveMaintenanceWindow => !s.Table(MaintenanceWindows).Records.Any(r => Eq(r.S("ServiceWindowID"), w.Target["WindowId"])),
            HygieneFixKind.SetCollectionRefreshType => s.Table(Collections).Records.Any(r => Eq(r.S("CollectionID"), id) && r.I("RefreshType") == (w.Target["RefreshType"] == "Continuous" ? 4 : 1)),
            HygieneFixKind.SetApplication => s.Table(TsAppDefinitions).Records.Any(r => Eq(r.S("ModelName"), id) && r.B("AutoInstall")),
            HygieneFixKind.SetSiteMaintenanceTask => s.Table(MaintenanceTasks).Records.Any(r => Eq(r.S("ItemName"), id) && r.B("Enabled")),
            HygieneFixKind.RunAutoDeploymentRule => s.Table(AutoDeploymentRules).Records.Any(r => r.S("AutoDeploymentID") == id && r.S("LastRunTime").Length > 0 && r.S("LastRunTime") != ruleRunBefore),
            _ => false
        };
    }

    private static string Count(int n, string noun) => SiteHygieneService.Count(n, noun);
}
