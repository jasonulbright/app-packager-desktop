using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>
/// The data CM01 returned on 2026-10-24 at 14:56 (Eastern) with the SHL- clutter in place: the output of
/// the native script (one JSON object per line, as the session host writes them) and the findings the
/// source module produced on the same site one minute later.
/// </summary>
internal static class SiteHygieneFixtures
{
    /// <summary>The local time of the capture on CM01.</summary>
    public static readonly DateTime CaptureLocal = new(2026, 10, 24, 14, 56, 0, DateTimeKind.Unspecified);

    /// <summary>A UTC clock whose local time on this computer is the capture time.</summary>
    public static DateTime CaptureUtc => TimeZoneInfo.ConvertTimeToUtc(CaptureLocal, TimeZoneInfo.Local);

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SiteHygiene");

    public static IReadOnlyList<JsonElement> NativeOutput() =>
        File.ReadAllLines(Path.Combine(Folder, "cm01-native-read.jsonl")).Where(l => l.Trim().Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    public static HygieneSiteRead NativeRead() => HygieneSiteRead.Parse(NativeOutput());

    public sealed record SourceFinding(string CheckId, string Severity, string ObjectType, string ObjectId, string ObjectName, string FixScript);

    public static IReadOnlyList<SourceFinding> SourceFindings()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "cm01-source-findings.json")));
        return doc.RootElement.EnumerateArray().Select(e => new SourceFinding(
            Str(e, "CheckId"), Str(e, "Severity"), Str(e, "ObjectType"), Str(e, "ObjectId"), Str(e, "ObjectName"), Str(e, "FixScript"))).ToList();
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>The provider class each bulk set reads, and the values the WHERE clause of the set requires.</summary>
    private static readonly Dictionary<string, (string Class, (string Column, JsonNode Value)[] Extra)> Classes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Applications"] = ("SMS_Application", [("IsLatest", true)]),
        ["AppRevisions"] = ("SMS_Application", [("IsLatest", false)]),
        ["Packages"] = ("SMS_Package", []),
        ["Programs"] = ("SMS_Program", []),
        ["TaskSequences"] = ("SMS_TaskSequencePackage", []),
        ["TsReferences"] = ("SMS_TaskSequencePackageReference_All", []),
        ["Devices"] = ("SMS_CM_RES_COLL_SMS00001", []),
        ["DeviceAgents"] = ("SMS_R_System", []),
        ["Boundaries"] = ("SMS_Boundary", []),
        ["BoundaryGroups"] = ("SMS_BoundaryGroup", []),
        ["BootImages"] = ("SMS_BootImagePackage", []),
        ["OsImages"] = ("SMS_ImagePackage", []),
        ["OsUpgradePackages"] = ("SMS_OperatingSystemInstallPackage", []),
        ["DriverPackages"] = ("SMS_DriverPackage", []),
        ["UpdatePackages"] = ("SMS_SoftwareUpdatesPackage", []),
        ["UpdateGroups"] = ("SMS_AuthorizationList", []),
        ["AutoDeploymentRules"] = ("SMS_AutoDeployment", []),
        ["MaintenanceTasks"] = ("SMS_SCI_SQLTask", []),
        ["CollectionSettings"] = ("SMS_CollectionSettings", []),
        ["AppDependencies"] = ("SMS_AppDependenceRelation", []),
        ["AppRelations"] = ("SMS_AppRelation_Flat", [("Level", 1)]),
        ["DeploymentTypes"] = ("SMS_DeploymentType", []),
        ["AppContent"] = ("SMS_Content", []),
        ["CollectionDependencies"] = ("SMS_CollectionDependencies", []),
        ["Collections"] = ("SMS_Collection", []),
        ["Deployments"] = ("SMS_DeploymentSummary", []),
        ["CollectionEvalFull"] = ("SMS_CollectionEvaluationFull", []),
        ["CollectionEvalIncremental"] = ("SMS_CollectionEvaluationIncremental", []),
        ["ContentStatus"] = ("SMS_ObjectContentInfo", []),
        ["DistributionPoints"] = ("SMS_DistributionPointInfo", []),
        ["BoundaryGroupSiteSystems"] = ("SMS_BoundaryGroupSiteSystems", []),
        ["DistributionPointGroups"] = ("SMS_DPGroupInfo", []),
        ["Baselines"] = ("SMS_ConfigurationBaselineInfo", []),
        ["ConfigurationItems"] = ("SMS_ConfigurationItemLatest", [("CIType_ID", 3)]),
        ["ClientSettings"] = ("SMS_ClientSettings", []),
        ["Drivers"] = ("SMS_Driver", []),
        ["DriverContainers"] = ("SMS_DriverContainer", []),
        ["AdminUsers"] = ("SMS_Admin", []),
        ["ExpiredUpdates"] = ("SMS_SoftwareUpdate", [("IsExpired", true)]),
        ["UpdateContentMap"] = ("SMS_CIToContent", [("ContentDownloaded", true)]),
        ["UpdatePackageContent"] = ("SMS_PackageToContent", [("PackageType", 5)]),
        ["AppDeployments"] = ("SMS_ApplicationAssignment", [])
    };

    /// <summary>A stub site that serves the CM01 rows through the real script: each set's rows become rows of the class the set reads.</summary>
    public static JsonObject StubState()
    {
        var read = NativeRead();
        var classes = new JsonObject();
        JsonArray Rows(string cls) => classes[cls] as JsonArray ?? (JsonArray)(classes[cls] = new JsonArray());
        foreach (var (set, (cls, extra)) in Classes)
        {
            var table = read.Table(set);
            foreach (var row in table.Rows)
            {
                var o = new JsonObject();
                for (var i = 0; i < table.Columns.Count && i < row.Length; i++)
                {
                    var name = table.Columns[i];
                    o[name] = name is "AgentName" or "AgentTime" or "RoleNames"
                        ? new JsonArray(row[i].Split('|', StringSplitOptions.RemoveEmptyEntries).Select(v => (JsonNode)JsonValue.Create(v)!).ToArray())
                        : row[i];
                }
                foreach (var (column, value) in extra) o[column] = value.DeepClone();
                Rows(cls).Add(o);
            }
        }
        var details = new JsonObject();
        foreach (var r in read.Table("CollectionDetails").Records)
        {
            var rules = new JsonArray();
            for (var i = 0; i < r.I("DirectRules"); i++) rules.Add("SMS_CollectionRuleDirect");
            for (var i = 0; i < r.I("QueryRules"); i++) rules.Add("SMS_CollectionRuleQuery");
            details[r.S("CollectionID")] = new JsonObject
            {
                ["Rules"] = rules,
                ["Schedule"] = new JsonObject { ["DaySpan"] = r.I("DaySpan"), ["HourSpan"] = r.I("HourSpan"), ["MinuteSpan"] = r.I("MinuteSpan"), ["StartTime"] = r.S("StartTime") }
            };
        }
        var windows = new JsonObject();
        foreach (var g in read.Table("MaintenanceWindows").Records.GroupBy(r => r.S("CollectionID")))
            windows[g.Key] = new JsonArray(g.Select(r => (JsonNode)new JsonObject
            {
                ["ServiceWindowID"] = r.S("ServiceWindowID"),
                ["Name"] = r.S("Name"),
                ["RecurrenceType"] = r.I("RecurrenceType"),
                ["StartTime"] = r.S("StartTime"),
                ["Duration"] = r.I("Duration"),
                ["IsEnabled"] = r.B("IsEnabled"),
                ["ServiceWindowType"] = r.I("ServiceWindowType")
            }).ToArray());
        var auto = new JsonObject();
        foreach (var r in read.Table("TsAppDefinitions").Records) auto[r.S("ModelName")] = r.B("AutoInstall");
        return new JsonObject
        {
            ["Options"] = new JsonObject(),
            ["Now"] = CaptureLocal.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            ["Classes"] = classes,
            ["Details"] = details,
            ["Windows"] = windows,
            ["AutoInstall"] = auto
        };
    }
}
