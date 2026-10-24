using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using static ApplicationPackagerEngine.Services.HygieneSets;

namespace ApplicationPackagerEngine.Services;

internal static partial class HygieneChecks
{
    private static void AddOther(List<HygieneCheckFamily> list)
    {
        list.Add(new(["CNT-01", "CNT-02", "CNT-03"], ["Content"], [ContentStatus, Applications, Deployments], ContentDistribution));
        list.Add(new(["DEV-01"], ["Devices"], [Devices, MaintenanceTasks], DeviceInactive));
        list.Add(new(["DEV-02", "DEV-03"], ["Devices"], [Devices], DeviceRecords));
        list.Add(new(["BND-01", "BND-02", "BND-03"], ["Boundaries"], [Boundaries, BoundaryGroups], BoundaryChecks));
        list.Add(new(["TSQ-01", "TSQ-02"], ["TaskSequences"], [TaskSequences, TsReferences, Packages, BootImages, DriverPackages, UpdatePackages, OsImages, OsUpgradePackages, Applications], TaskSequenceReferences));
        list.Add(new(["TSQ-03"], ["TaskSequences"], [TaskSequences, TsReferences, Applications, TsAppDefinitions], TaskSequenceInstallSetting));
        list.Add(new(["TSQ-04"], ["TaskSequences"], [TaskSequences, TsReferences, Applications, ContentStatus], TaskSequenceContent));
        list.Add(new(["UPD-01", "UPD-04"], ["Updates"], [UpdateGroups], UpdateGroupChecks));
        list.Add(new(["UPD-03"], ["Updates"], [AutoDeploymentRules], AdrChecks));
        list.Add(new(["UPD-05"], ["UpdateContent"], [ExpiredUpdates, UpdateContentMap, UpdatePackageContent, UpdatePackages], UpdatePackageExpiredContent));
        list.Add(new(["MNT-01", "MNT-02"], ["Site"], [MaintenanceTasks], MaintenanceTaskChecks));
        list.Add(new(["DPT-01", "DPT-02"], ["DistributionPoints"], [DistributionPoints, BoundaryGroupSiteSystems, DistributionPointGroups], DistributionPointChecks));
        list.Add(new(["CFG-01", "CFG-02", "CFG-03"], ["Compliance"], [Baselines, ConfigurationItems, ClientSettings], ComplianceChecks));
        list.Add(new(["DRV-01"], ["Drivers"], [Drivers, DriverContainers], DriverUnpackaged));
        list.Add(new(["SEC-01"], ["Security"], [AdminUsers], AdminDeletedAccount));
    }

    private static readonly Dictionary<int, string> ContentTypes = new() { [0] = "Package", [3] = "DriverPackage", [4] = "TaskSequence", [5] = "UpdatePackage", [257] = "OSImage", [258] = "BootImage", [259] = "OSUpgradePackage", [512] = "Application" };

    private static string ContentType(HygieneRow r) => ContentTypes.TryGetValue(r.I("ObjectType", -1), out var t) ? t : "Content";

    private static IEnumerable<HygieneFinding> ContentDistribution(HygieneContext c)
    {
        var stuckDays = c.T.ContentStuckDays;
        var cutoff = c.Now.AddDays(-stuckDays);
        var rows = c.Rows(ContentStatus).ToList();
        foreach (var r in rows)
        {
            var name = r.S("SoftwareName");
            var id = r.S("PackageID");
            if (r.I("NumberErrors") > 0)
                yield return F("CNT-01", ContentType(r), id, name,
                    $"Distribution failed on {N(r.I("NumberErrors"))} of {N(r.I("Targeted"))} targeted distribution point(s) ({N(r.I("NumberSuccess"))} succeeded, {N(r.I("NumberInProgress"))} in progress). Clients in the boundaries those distribution points serve fall back to another source or fail with content not found.",
                    "Review the failed distribution points under Monitoring > Distribution Status > Content Status, fix the cause, then redistribute.",
                    guidance: $"Console: Monitoring > Distribution Status > Content Status > '{name}' ({id}) > View Status > Error > Redistribute");
            else if (r.I("NumberInProgress") > 0 && r.T("LastUpdateDate") is { } updated && updated < cutoff)
                yield return F("CNT-02", ContentType(r), id, name,
                    $"Distribution is still in progress on {N(r.I("NumberInProgress"))} of {N(r.I("Targeted"))} targeted distribution point(s), and the content was last updated {Day(updated)}, more than {N(stuckDays)} day(s) ago. A transfer that old is usually stalled, not slow.",
                    "Check the in-progress distribution points for a stalled transfer or an offline server; cancel and redistribute if nothing is moving.",
                    guidance: $"Console: Monitoring > Distribution Status > Content Status > '{name}' ({id}) > View Status > In Progress");
        }
        var undistributed = rows.Where(r => r.I("Targeted", -1) == 0 && r.L("SourceSize") > 0).ToList();
        if (undistributed.Count == 0) yield break;
        var deployedModels = Set(c.Rows(Applications).Where(a => a.B("IsDeployed") && !a.B("IsExpired")).Select(a => a.S("ModelName")));
        var deployedPackages = Set(c.Rows(Deployments).Where(d => d.I("FeatureType") == 2).Select(d => d.S("PackageID")));
        foreach (var r in undistributed)
        {
            var type = r.I("ObjectType", -1);
            var deployed = (type == 512 && deployedModels.Contains(r.S("ObjectID"))) || (type == 0 && deployedPackages.Contains(r.S("PackageID")));
            if (!deployed) continue;
            yield return F("CNT-03", ContentType(r), r.S("PackageID"), r.S("SoftwareName"),
                "This content has an active deployment and source files, but it is targeted to no distribution point. Every client that tries to install it fails to locate content.",
                "Distribute the content to the distribution point groups that serve the deployment's collection.",
                guidance: $"Console: Software Library > '{r.S("SoftwareName")}' > Distribute Content - choose the distribution point group(s) for the targeted clients");
        }
    }

    internal static readonly string[] CleanupTasks = ["Delete Inactive Client Discovery Data", "Delete Aged Discovery Data"];

    /// <summary>The maintenance tasks of the connected site; every row when the site code is not known.</summary>
    internal static IEnumerable<HygieneRow> SiteTasks(HygieneContext c) =>
        c.Rows(MaintenanceTasks).Where(r => c.SiteCode.Length == 0 || r.S("SiteCode").Length == 0 || string.Equals(r.S("SiteCode"), c.SiteCode, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<HygieneFinding> DeviceInactive(HygieneContext c)
    {
        var days = c.T.InactiveDeviceDays;
        var cutoff = c.Now.AddDays(-days);
        var cleanup = SiteTasks(c).Any(t => CleanupTasks.Contains(t.S("ItemName"), StringComparer.OrdinalIgnoreCase) && t.B("Enabled"));
        var severity = cleanup ? HygieneSeverity.Info : HygieneSeverity.Warning;
        var note = cleanup ? "discovery cleanup tasks are enabled and will eventually purge it" : "no discovery cleanup task is enabled, so the record stays";
        foreach (var d in c.Rows(Devices))
        {
            if (!d.B("IsClient") || d.T("LastActiveTime") is not { } last || last >= cutoff) continue;
            var id = d.S("ResourceID");
            yield return F("DEV-01", "Device", id, d.S("Name"),
                $"The client was last active {Day(last)}, more than {N(days)} days ago; {note}.",
                "Verify the machines are really gone, then let cleanup tasks purge them or delete the records.",
                HygieneFixKind.RemoveDevice, Params((HygieneFixParameters.Id, id), (HygieneFixParameters.Days, N(days))), severity: severity);
        }
    }

    private static readonly HashSet<string> PlaceholderGuids = new(StringComparer.OrdinalIgnoreCase) { "00000000-0000-0000-0000-000000000000", "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF" };

    private static IEnumerable<HygieneFinding> DeviceRecords(HygieneContext c)
    {
        var devices = c.Rows(Devices).ToList();
        foreach (var group in devices.Where(d => d.S("Name").Length > 0).GroupBy(d => d.S("Name").ToLowerInvariant()).Where(g => g.Count() > 1))
        {
            var first = group.First().S("Name");
            var ids = string.Join(", ", group.Select(d => d.L("ResourceID")).Order().Select(N));
            yield return F("DEV-02", "Device", ids, first,
                $"{N(group.Count())} device records share the name '{first}' (ResourceIDs {ids}); deployments and reports split across them.",
                "Keep the active record and delete the stale duplicates.",
                guidance: $"Review each: Get-CMDevice -Name '{PowerShellLiteral.EscapeSingleQuoted(first)}' | Select-Object ResourceID, LastActiveTime, ClientVersion");
        }
        var withGuid = devices.Where(d => !string.IsNullOrWhiteSpace(d.S("SMBIOSGUID")) && !PlaceholderGuids.Contains(d.S("SMBIOSGUID").Trim('{', '}')));
        foreach (var group in withGuid.GroupBy(d => d.S("SMBIOSGUID"), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var names = group.Select(d => d.S("Name")).Distinct().ToList();
            if (names.Count < 2) continue;
            yield return F("DEV-02", "Device", group.Key, string.Join(" / ", names),
                $"{N(group.Count())} device records with different names share SMBIOS GUID {group.Key}: {string.Join(", ", names)}. Usually a renamed or re-imaged machine leaving a stale record.",
                "Delete the record for the name the machine no longer uses.",
                guidance: "Review the LastActiveTime of each record before deleting.");
        }
        var clients = devices.Where(d => d.B("IsClient") && d.S("ClientVersion").Length > 0).ToList();
        var parsed = clients.Select(d => (Row: d, Version: Version.TryParse(d.S("ClientVersion"), out var v) ? v : null)).ToList();
        var newest = parsed.Where(p => p.Version is not null).Select(p => p.Version!).DefaultIfEmpty().Max();
        if (newest is null) yield break;
        var behind = parsed.Where(p => p.Version is not null && p.Version < newest).ToList();
        if (behind.Count == 0) yield break;
        var sample = string.Join(", ", behind.OrderBy(p => p.Row.S("Name"), StringComparer.OrdinalIgnoreCase).Take(15).Select(p => $"{p.Row.S("Name")} ({p.Row.S("ClientVersion")})"));
        yield return F("DEV-03", "Site", "Site", "Client version drift",
            $"{N(behind.Count)} of {N(clients.Count)} clients run a client version below the newest seen ({newest}). First 15: {sample}",
            "Check automatic client upgrade settings; long-tail old clients usually mean the upgrade never reaches them.",
            guidance: "Console: Administration > Site Configuration > Sites > Hierarchy Settings > Client Upgrade");
    }

    private static uint? Ipv4(string text) =>
        IPAddress.TryParse(text.Trim(), out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? (uint)(ip.GetAddressBytes()[0] << 24 | ip.GetAddressBytes()[1] << 16 | ip.GetAddressBytes()[2] << 8 | ip.GetAddressBytes()[3])
            : null;

    private static IEnumerable<HygieneFinding> BoundaryChecks(HygieneContext c)
    {
        var boundaries = c.Rows(Boundaries).ToList();
        foreach (var b in boundaries.Where(b => b.I("GroupCount", -1) == 0))
            yield return F("BND-01", "Boundary", b.S("Value"), b.S("DisplayName").Length > 0 ? b.S("DisplayName") : b.S("Value"),
                $"Boundary '{b.S("Value")}' belongs to no boundary group; clients inside it get no content location or site assignment from it.",
                "Add the boundary to the appropriate boundary group or delete it.",
                guidance: "Console: Administration > Hierarchy Configuration > Boundary Groups - add the boundary");
        foreach (var g in c.Rows(BoundaryGroups).Where(g => g.I("SiteSystemCount", -1) == 0))
            yield return F("BND-02", "BoundaryGroup", g.S("GroupID"), g.S("Name"),
                $"Boundary group '{g.S("Name")}' references no site systems; clients in it fall back to other groups for content, adding latency, unless it exists for site assignment only.",
                "Add the serving DP/MP site systems, or leave as-is if the group is assignment-only by design.",
                guidance: $"Console: Boundary Group '{g.S("Name")}' Properties > References - add site systems");
        var ranges = new List<(int Index, string Value, uint Start, uint End)>();
        foreach (var b in boundaries.Where(b => b.I("BoundaryType", -1) == 3))
        {
            var parts = b.S("Value").Split('-');
            if (parts.Length != 2 || Ipv4(parts[0]) is not { } start || Ipv4(parts[1]) is not { } end) continue;
            ranges.Add((ranges.Count, b.S("Value"), start, end));
        }
        // Sweep by start address: each range meets only the earlier ranges whose end reaches its start.
        var pairs = new List<(int A, int B)>();
        var active = new List<(int Index, string Value, uint Start, uint End)>();
        foreach (var r in ranges.OrderBy(r => r.Start).ThenBy(r => r.Index))
        {
            active.RemoveAll(a => a.End < r.Start);
            foreach (var a in active) pairs.Add(a.Index < r.Index ? (a.Index, r.Index) : (r.Index, a.Index));
            active.Add(r);
        }
        foreach (var (ia, ib) in pairs.OrderBy(p => p.A).ThenBy(p => p.B))
        {
            var a = ranges[ia].Value;
            var b = ranges[ib].Value;
            yield return F("BND-03", "Boundary", a + "|" + b, $"{a} overlaps {b}",
                $"IP ranges '{a}' and '{b}' overlap. Fine for content location, a problem for automatic site assignment if the overlapping groups assign different sites.",
                "Verify the overlap is intentional and both ranges route to compatible boundary groups.",
                guidance: "Console: Administration > Hierarchy Configuration > Boundaries");
        }
    }

    internal static readonly Regex DefaultBootImage = new(@"^Boot image \((x64|x86|arm64)\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The references of each task sequence: its reference rows and its boot image.</summary>
    internal static Dictionary<string, List<string>> TsRefsBySequence(HygieneContext c)
    {
        var map = c.Rows(TsReferences).GroupBy(r => r.S("PackageID"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => r.S("ObjectID")).Where(s => s.Length > 0).ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var ts in c.Rows(TaskSequences))
        {
            if (!map.TryGetValue(ts.S("PackageID"), out var list)) map[ts.S("PackageID")] = list = [];
            if (ts.S("BootImageID").Length > 0) list.Add(ts.S("BootImageID"));
        }
        return map;
    }

    private static IEnumerable<HygieneFinding> TaskSequenceReferences(HygieneContext c)
    {
        var known = Set(new[] { Packages, BootImages, DriverPackages, UpdatePackages, TaskSequences, OsImages, OsUpgradePackages }.SelectMany(s => c.Rows(s).Select(r => r.S("PackageID")))
            .Concat(c.Rows(Applications).Select(a => a.S("ModelName"))));
        var refs = TsRefsBySequence(c);
        foreach (var ts in c.Rows(TaskSequences))
        {
            var missing = (refs.TryGetValue(ts.S("PackageID"), out var l) ? l : []).Where(r => !known.Contains(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count == 0) continue;
            yield return F("TSQ-01", "TaskSequence", ts.S("PackageID"), ts.S("Name"),
                $"Task sequence references {N(missing.Count)} content id(s) that no longer exist: {string.Join(", ", missing)}. Runs fail at those steps.",
                "Open the task sequence editor and fix or remove the steps referencing deleted content.",
                guidance: $"Console: edit task sequence '{ts.S("Name")}' and repair the broken references");
        }
        var referenced = Set(refs.Values.SelectMany(v => v));
        foreach (var bi in c.Rows(BootImages))
        {
            if (DefaultBootImage.IsMatch(bi.S("Name")) || referenced.Contains(bi.S("PackageID"))) continue;
            yield return F("TSQ-02", "BootImage", bi.S("PackageID"), bi.S("Name"),
                "Custom boot image is referenced by no task sequence.",
                "Delete it if the task sequences that used it are gone; its content still occupies DPs.",
                HygieneFixKind.RemoveBootImage, Params((HygieneFixParameters.Id, bi.S("PackageID"))));
        }
        foreach (var dp in c.Rows(DriverPackages))
        {
            if (referenced.Contains(dp.S("PackageID"))) continue;
            yield return F("TSQ-02", "DriverPackage", dp.S("PackageID"), dp.S("Name"),
                "Driver package is referenced by no task sequence.",
                "Delete it if the hardware model is retired; driver packages are among the largest content on DPs.",
                HygieneFixKind.RemoveDriverPackage, Params((HygieneFixParameters.Id, dp.S("PackageID"))));
        }
    }

    /// <summary>The task sequence names that install each application, by ModelName.</summary>
    private static Dictionary<string, List<string>> SequencesByApplication(HygieneContext c)
    {
        var apps = Set(c.Rows(Applications).Select(a => a.S("ModelName")));
        var names = c.Rows(TaskSequences).GroupBy(t => t.S("PackageID"), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().S("Name"), StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in c.Rows(TsReferences))
        {
            var model = r.S("ObjectID");
            if (!apps.Contains(model) || !names.TryGetValue(r.S("PackageID"), out var ts)) continue;
            if (!map.TryGetValue(model, out var list)) map[model] = list = [];
            list.Add(ts);
        }
        return map;
    }

    private static IEnumerable<HygieneFinding> TaskSequenceInstallSetting(HygieneContext c)
    {
        var byModel = SequencesByApplication(c);
        var apps = c.Rows(Applications).GroupBy(a => a.S("ModelName"), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var def in c.Rows(TsAppDefinitions))
        {
            var model = def.S("ModelName");
            if (def.B("AutoInstall") || !byModel.TryGetValue(model, out var ts) || !apps.TryGetValue(model, out var app)) continue;
            yield return F("TSQ-03", "Application", model, app.S("LocalizedDisplayName"),
                $"Task sequence '{string.Join("', '", ts.Distinct().Order(StringComparer.OrdinalIgnoreCase))}' installs this application. The application does not have the setting 'Allow this application to be installed from the Install Application task sequence action without being deployed'.",
                "Set the option on the General tab of the application properties. The change makes a new application revision.",
                HygieneFixKind.SetApplication, Params((HygieneFixParameters.ModelName, model)));
        }
    }

    private static IEnumerable<HygieneFinding> TaskSequenceContent(HygieneContext c)
    {
        var byModel = SequencesByApplication(c);
        var apps = c.Rows(Applications).GroupBy(a => a.S("ModelName"), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in c.Rows(ContentStatus))
        {
            if (row.I("ObjectType", -1) != 512 || row.I("Targeted", -1) != 0 || row.L("SourceSize") <= 0) continue;
            var model = row.S("ObjectID");
            if (!byModel.TryGetValue(model, out var ts) || !apps.TryGetValue(model, out var app)) continue;
            yield return F("TSQ-04", "Application", model, app.S("LocalizedDisplayName"),
                $"Task sequence '{string.Join("', '", ts.Distinct().Order(StringComparer.OrdinalIgnoreCase))}' installs this application. The application has source files, and its content is on no distribution point. The task sequence cannot find the content.",
                "Distribute the content to the distribution point groups that serve the task sequence clients.",
                guidance: $"Console: Software Library > '{app.S("LocalizedDisplayName")}' > Distribute Content - select the distribution point groups");
        }
    }

    private static IEnumerable<HygieneFinding> UpdateGroupChecks(HygieneContext c)
    {
        var pctThreshold = c.T.SugExpiredPctThreshold;
        var max = c.T.SugMaxUpdates;
        foreach (var g in c.Rows(UpdateGroups))
        {
            var total = g.I("NumberOfUpdates");
            if (total > 0)
            {
                var pct = Math.Round(g.I("NumberOfExpiredUpdates") * 100.0 / total, 1);
                if (pct >= pctThreshold)
                    yield return F("UPD-01", "UpdateGroup", g.S("CI_ID"), g.S("LocalizedDisplayName"),
                        $"{N(g.I("NumberOfExpiredUpdates"))} of {N(total)} updates in the group are expired ({pct.ToString("0.#", CultureInfo.InvariantCulture)}%); compliance numbers computed from it are misleading.{(g.B("ContainsSupersededUpdates") ? " The group also contains superseded updates." : "")}",
                        "Clean the expired/superseded updates out of the group or rebuild it from a current search.",
                        guidance: $"Console: Software Library > Software Update Groups > '{g.S("LocalizedDisplayName")}' - remove expired/superseded members");
            }
            if (total > max)
                yield return F("UPD-04", "UpdateGroup", g.S("CI_ID"), g.S("LocalizedDisplayName"),
                    $"The group holds {N(total)} updates; a software update deployment supports at most {N(max)}. A deployment of this group fails or cannot be created.",
                    "Split the group, for example by year or product, and remove expired and superseded members.",
                    guidance: $"Console: Software Library > Software Update Groups > '{g.S("LocalizedDisplayName")}' - split the membership");
        }
    }

    private static IEnumerable<HygieneFinding> AdrChecks(HygieneContext c)
    {
        var staleDays = c.T.AdrStaleDays;
        var cutoff = c.Now.AddDays(-staleDays);
        foreach (var a in c.Rows(AutoDeploymentRules))
        {
            var id = a.S("AutoDeploymentID");
            var name = a.S("Name");
            var enabled = a.S("AutoDeploymentEnabled").Length == 0 || a.B("AutoDeploymentEnabled");
            if (a.I("LastErrorCode") != 0)
                yield return F("UPD-03", "ADR", id, name,
                    $"Automatic deployment rule's last run ended with error code {a.S("LastErrorCode")}; new updates are not being deployed by it.",
                    "Open the ADR run history (ruleengine.log on the site server) and fix the failure.",
                    guidance: $"Run it again after the fix: Invoke-CMSoftwareUpdateAutoDeploymentRule -Id {id}", severity: HygieneSeverity.Error);
            else if (!enabled)
                yield return F("UPD-03", "ADR", id, name,
                    "Automatic deployment rule is disabled.",
                    "Delete it if retired, or re-enable it if it should still run.",
                    HygieneFixKind.RemoveAutoDeploymentRule, Params((HygieneFixParameters.Id, id)), severity: HygieneSeverity.Info);
            else if (a.T("LastRunTime") is { } last && last < cutoff)
                yield return F("UPD-03", "ADR", id, name,
                    $"Automatic deployment rule is enabled but last ran {Minute(last)} (over {N(staleDays)} days ago); its schedule may be broken.",
                    "Check the ADR schedule and the last evaluation in ruleengine.log.",
                    HygieneFixKind.RunAutoDeploymentRule, Params((HygieneFixParameters.Id, id), (HygieneFixParameters.Days, N(staleDays))), severity: HygieneSeverity.Warning);
        }
    }

    private static IEnumerable<HygieneFinding> UpdatePackageExpiredContent(HygieneContext c)
    {
        var expired = c.Rows(ExpiredUpdates).Select(r => r.L("CI_ID")).ToHashSet();
        if (expired.Count == 0) yield break;
        var expiredContent = c.Rows(UpdateContentMap).Where(r => expired.Contains(r.L("CI_ID"))).Select(r => r.L("ContentID")).ToHashSet();
        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var hits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in c.Rows(UpdatePackageContent))
        {
            var pkg = r.S("PackageID");
            totals[pkg] = totals.GetValueOrDefault(pkg) + 1;
            if (expiredContent.Contains(r.L("ContentID"))) hits[pkg] = hits.GetValueOrDefault(pkg) + 1;
        }
        var names = c.Rows(UpdatePackages).GroupBy(p => p.S("PackageID"), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().S("Name"), StringComparer.OrdinalIgnoreCase);
        foreach (var (pkg, count) in hits.OrderByDescending(h => h.Value))
        {
            var name = names.TryGetValue(pkg, out var n) ? n : pkg;
            yield return F("UPD-05", "UpdatePackage", pkg, name,
                $"{N(count)} of {N(totals[pkg])} content item(s) in the package belong to expired updates. The files stay in the package source and on every distribution point that holds the package.",
                "Remove the expired updates from the package, or let the site remove them: expired updates leave packages only when no deployment references them.",
                guidance: $"Console: Software Library > Deployment Packages > '{name}' > Show Members - remove expired updates, then refresh distribution points");
        }
    }

    internal static readonly string[] RecommendedTasks = ["Delete Aged Inventory History", "Delete Aged Discovery Data", "Delete Obsolete Client Discovery Data", "Delete Aged Client Operations", "Delete Aged Log Data"];

    /// <summary>The site names the backup task "Backup SMS Site Server"; the source looks for "Backup Site Server".</summary>
    internal static readonly string[] BackupTasks = ["Backup SMS Site Server", "Backup Site Server"];

    private static IEnumerable<HygieneFinding> MaintenanceTaskChecks(HygieneContext c)
    {
        var tasks = SiteTasks(c).ToList();
        foreach (var t in tasks.Where(t => RecommendedTasks.Contains(t.S("ItemName"), StringComparer.OrdinalIgnoreCase) && !t.B("Enabled")))
            yield return F("MNT-01", "MaintenanceTask", t.S("ItemName"), t.S("ItemName"),
                $"Cleanup task '{t.S("ItemName")}' is disabled; the data it would prune accumulates in the site database.",
                "Enable it unless a deliberate retention policy keeps it off.",
                HygieneFixKind.SetSiteMaintenanceTask, Params((HygieneFixParameters.Id, t.S("ItemName")), (HygieneFixParameters.SiteCode, t.S("SiteCode").Length > 0 ? t.S("SiteCode") : c.SiteCode)));
        var backup = tasks.FirstOrDefault(t => BackupTasks.Contains(t.S("ItemName"), StringComparer.OrdinalIgnoreCase));
        if (backup.S("ItemName").Length > 0 && !backup.B("Enabled"))
            yield return F("MNT-02", "MaintenanceTask", backup.S("ItemName"), backup.S("ItemName"),
                $"The {backup.S("ItemName")} maintenance task is disabled.",
                "Enable it, or confirm an SQL-level backup of the site database (plus CD.Latest) covers recovery instead.",
                HygieneFixKind.SetSiteMaintenanceTask, Params((HygieneFixParameters.Id, backup.S("ItemName")), (HygieneFixParameters.SiteCode, backup.S("SiteCode").Length > 0 ? backup.S("SiteCode") : c.SiteCode)));
    }

    private static IEnumerable<HygieneFinding> DistributionPointChecks(HygieneContext c)
    {
        var inGroup = Set(c.Rows(BoundaryGroupSiteSystems).Select(r => r.S("ServerNALPath")));
        foreach (var dp in c.Rows(DistributionPoints))
        {
            var nal = dp.S("NALPath");
            if (inGroup.Contains(nal)) continue;
            var name = dp.S("Name").TrimStart('\\');
            yield return F("DPT-01", "DistributionPoint", nal, name,
                "The distribution point is a site system of no boundary group. Clients select content sources through boundary groups, so no client selects this server except through the default site boundary group fallback.",
                "Add the distribution point to the boundary group(s) for the locations it serves, or remove the role if the server is retired.",
                guidance: $"Set-CMBoundaryGroup -Name '<boundary group>' -AddSiteSystemServerName '{PowerShellLiteral.EscapeSingleQuoted(name)}'");
        }
        foreach (var g in c.Rows(DistributionPointGroups).Where(g => g.I("MembersCount", -1) == 0))
            yield return F("DPT-02", "DistributionPointGroup", g.S("GroupID"), g.S("Name"),
                $"The distribution point group has no members and {N(g.I("AssignedContentCount"))} content item(s) assigned. Content distributed to it reaches no server.",
                "Add the intended distribution points, or delete the group.",
                guidance: $"Review: Get-CMDistributionPointGroup -Name '{PowerShellLiteral.EscapeSingleQuoted(g.S("Name"))}'");
    }

    private static IEnumerable<HygieneFinding> ComplianceChecks(HygieneContext c)
    {
        foreach (var b in c.Rows(Baselines))
        {
            if (!b.B("IsUserDefined") || b.B("IsAssigned") || b.B("InUse")) continue;
            yield return F("CFG-01", "Baseline", b.S("ModelName").Length > 0 ? b.S("ModelName") : b.S("CI_ID"), b.S("LocalizedDisplayName"),
                "The configuration baseline has no deployment and no other baseline references it. No client evaluates it.",
                "Deploy the baseline, or delete it if it is no longer needed.",
                guidance: $"Review: Get-CMBaseline -Id {b.S("CI_ID")} -Fast");
        }
        foreach (var ci in c.Rows(ConfigurationItems))
        {
            if (!ci.B("IsUserDefined") || ci.B("InUse")) continue;
            yield return F("CFG-02", "ConfigurationItem", ci.S("ModelName").Length > 0 ? ci.S("ModelName") : ci.S("CI_ID"), ci.S("LocalizedDisplayName"),
                "No configuration baseline references this configuration item. No client evaluates it.",
                "Add the item to a baseline, or delete it if it is no longer needed.",
                guidance: $"Review: Get-CMConfigurationItem -Id {ci.S("CI_ID")} -Fast");
        }
        foreach (var s in c.Rows(ClientSettings).Where(s => s.I("AssignmentCount", -1) == 0))
            yield return F("CFG-03", "ClientSettings", s.S("SettingsID"), s.S("Name"),
                "The custom client settings object is deployed to no collection. It applies to no client.",
                "Deploy the settings to the intended collection, or delete them.",
                guidance: $"Review: Get-CMClientSetting -Name '{PowerShellLiteral.EscapeSingleQuoted(s.S("Name"))}'");
    }

    private static IEnumerable<HygieneFinding> DriverUnpackaged(HygieneContext c)
    {
        var contained = c.Rows(DriverContainers).Select(r => r.L("CI_ID")).ToHashSet();
        foreach (var d in c.Rows(Drivers))
        {
            if (contained.Contains(d.L("CI_ID"))) continue;
            yield return F("DRV-01", "Driver", d.S("CI_ID"), d.S("LocalizedDisplayName"),
                "The driver is in the catalog but in no driver package and no boot image. A task sequence can install a driver only from a driver package on a distribution point.",
                "Add the driver to a driver package, or delete it from the catalog.",
                guidance: $"Review: Get-CMDriver -Id {d.S("CI_ID")} -Fast");
        }
    }

    private static IEnumerable<HygieneFinding> AdminDeletedAccount(HygieneContext c)
    {
        foreach (var a in c.Rows(AdminUsers))
        {
            if (!a.B("IsDeleted")) continue;
            var roles = a.List("RoleNames").Where(r => r.Length > 0).ToList();
            yield return F("SEC-01", "AdministrativeUser", a.S("AdminID"), a.S("LogonName"),
                $"The site reports that the Active Directory account for this administrative user is deleted. Roles: {(roles.Count > 0 ? string.Join(", ", roles) : "none recorded")}.",
                "Remove the administrative user from the site.",
                guidance: $"Review, then remove: Get-CMAdministrativeUser -Name '{PowerShellLiteral.EscapeSingleQuoted(a.S("LogonName"))}'");
        }
    }
}
