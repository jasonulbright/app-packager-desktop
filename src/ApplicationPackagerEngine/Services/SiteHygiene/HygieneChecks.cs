using System.Globalization;

namespace ApplicationPackagerEngine.Services;

/// <summary>The read set names the script and the checks share.</summary>
internal static class HygieneSets
{
    public const string Applications = "Applications";
    public const string AppRevisions = "AppRevisions";
    public const string Packages = "Packages";
    public const string Programs = "Programs";
    public const string TaskSequences = "TaskSequences";
    public const string TsReferences = "TsReferences";
    public const string TsAppDefinitions = "TsAppDefinitions";
    public const string Devices = "Devices";
    public const string DeviceAgents = "DeviceAgents";
    public const string Boundaries = "Boundaries";
    public const string BoundaryGroups = "BoundaryGroups";
    public const string BootImages = "BootImages";
    public const string OsImages = "OsImages";
    public const string OsUpgradePackages = "OsUpgradePackages";
    public const string DriverPackages = "DriverPackages";
    public const string UpdatePackages = "UpdatePackages";
    public const string UpdateGroups = "UpdateGroups";
    public const string AutoDeploymentRules = "AutoDeploymentRules";
    public const string MaintenanceTasks = "MaintenanceTasks";
    public const string CollectionSettings = "CollectionSettings";
    public const string AppDependencies = "AppDependencies";
    public const string AppRelations = "AppRelations";
    public const string DeploymentTypes = "DeploymentTypes";
    public const string AppContent = "AppContent";
    public const string CollectionDependencies = "CollectionDependencies";
    public const string Collections = "Collections";
    public const string CollectionDetails = "CollectionDetails";
    public const string Deployments = "Deployments";
    public const string CollectionEvalFull = "CollectionEvalFull";
    public const string CollectionEvalIncremental = "CollectionEvalIncremental";
    public const string ContentStatus = "ContentStatus";
    public const string DistributionPoints = "DistributionPoints";
    public const string BoundaryGroupSiteSystems = "BoundaryGroupSiteSystems";
    public const string DistributionPointGroups = "DistributionPointGroups";
    public const string Baselines = "Baselines";
    public const string ConfigurationItems = "ConfigurationItems";
    public const string ClientSettings = "ClientSettings";
    public const string Drivers = "Drivers";
    public const string DriverContainers = "DriverContainers";
    public const string AdminUsers = "AdminUsers";
    public const string ExpiredUpdates = "ExpiredUpdates";
    public const string UpdateContentMap = "UpdateContentMap";
    public const string UpdatePackageContent = "UpdatePackageContent";
    public const string MaintenanceWindows = "MaintenanceWindows";
    public const string AppDeployments = "AppDeployments";

    /// <summary>State sets of a collection delete: deployments by target collection (SMS_CIAssignmentBaseClass), automatic deployment rules, client settings deployments and administrative users' collections.</summary>
    public const string CollectionAssignments = "CollectionAssignments";
    public const string CollectionRules = "CollectionRules";
    public const string CollectionClientSettings = "CollectionClientSettings";
    public const string CollectionAdmins = "CollectionAdmins";

    public static readonly IReadOnlyList<string> CollectionUsers = [CollectionAssignments, CollectionRules, CollectionClientSettings, CollectionAdmins];

    /// <summary>
    /// The References property of each task sequence: PackageID, ObjectID (a PackageID or the ModelName of an
    /// application) and Type (0 package, 1 application). It lists an application without content, which
    /// <see cref="TsReferences"/> leaves out.
    /// </summary>
    public const string TsSteps = "TsSteps";

    /// <summary>The task sequences with an Auto Apply Drivers step; the read of <see cref="TsSteps"/> writes it too.</summary>
    public const string AutoApplySequences = "AutoApplySequences";

    /// <summary>The collections each client settings deployment targets.</summary>
    public const string ClientSettingsAssignments = "ClientSettingsAssignments";

    /// <summary>The target collection of each deployment of an application, a software update, a baseline or a policy.</summary>
    public const string CiAssignments = "CiAssignments";

    /// <summary>The sets that cost one provider read per object; their caps are <see cref="SiteHygieneService.MaxPerObjectReads"/>.</summary>
    public static readonly IReadOnlySet<string> PerObject = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { CollectionDetails, MaintenanceWindows, TsAppDefinitions, TsSteps, AutoApplySequences };
}

/// <summary>What a check family reads and judges: the fresh data of one scan.</summary>
internal sealed class HygieneContext(HygieneSiteRead data, HygieneThresholds thresholds, DateTime now, string siteCode)
{
    public HygieneSiteRead Data { get; } = data;
    public HygieneThresholds T { get; } = thresholds;
    /// <summary>The local time of this computer. The provider returns its time values unconverted, so they are compared with the local clock.</summary>
    public DateTime Now { get; } = now;
    public string SiteCode { get; } = siteCode;

    /// <summary>APP-04: the state of each content source path (Ok, Missing or Unknown); null when the probe is off.</summary>
    public IReadOnlyDictionary<string, string>? ProbeResults { get; init; }

    public int ProbeTimeoutSeconds { get; init; } = 3;

    /// <summary>The rows the checks enumerated, counted for the linear-work test.</summary>
    public long RowReads { get; private set; }

    public IEnumerable<HygieneRow> Rows(string set)
    {
        foreach (var r in Data.Table(set).Records)
        {
            RowReads++;
            yield return r;
        }
    }
}

/// <summary>A group of checks that read the same sets and run together.</summary>
internal sealed record HygieneCheckFamily(
    IReadOnlyList<string> CheckIds,
    IReadOnlyList<string> Areas,
    IReadOnlyList<string> Requires,
    Func<HygieneContext, IEnumerable<HygieneFinding>> Run)
{
    /// <summary>A reason the family cannot run although its sets were read; null when it can.</summary>
    public Func<HygieneContext, string?>? Gate { get; init; }

    /// <summary>A reason an option of the scan leaves the family out (state NotSelected); null when no option does.</summary>
    public Func<HygieneContext, string?>? OptionGate { get; init; }
}

/// <summary>The checks, one family per group of IDs that share their reads.</summary>
internal static partial class HygieneChecks
{
    private static readonly Dictionary<string, HygieneCatalog.Check> CatalogById = HygieneCatalog.Checks.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

    public static HygieneCatalog.Check Catalog(string id) => CatalogById[id];

    public static readonly IReadOnlyList<HygieneCheckFamily> Families = BuildFamilies();

    private static List<HygieneCheckFamily> BuildFamilies()
    {
        var list = new List<HygieneCheckFamily>();
        AddApplications(list);
        AddCollections(list);
        AddOther(list);
        AddRelationships(list);
        return list;
    }

    /// <summary>The sets the selected areas need, in the order the scan reads them.</summary>
    public static IReadOnlyList<string> RequiredSets(IReadOnlySet<string> areas) =>
        Families.Where(f => f.Areas.Any(areas.Contains)).SelectMany(f => f.Requires).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    internal static HygieneFinding F(
        string checkId, string objectType, string objectKey, string objectName, string evidence, string recommendation,
        HygieneFixKind kind = HygieneFixKind.None, Dictionary<string, string>? parameters = null, string guidance = "", HygieneSeverity? severity = null)
    {
        var c = Catalog(checkId);
        var p = (IReadOnlyDictionary<string, string>?)parameters ?? new Dictionary<string, string>();
        return new HygieneFinding(
            CheckId: checkId,
            Category: c.Category,
            Severity: severity ?? c.Severity,
            ObjectType: objectType,
            ObjectKey: objectKey,
            ObjectName: objectName,
            Evidence: evidence,
            Recommendation: recommendation,
            FixKind: kind,
            FixParameters: p,
            FixText: HygieneFixText.Text(kind, p),
            IsNew: false,
            IsSuppressed: false)
        { Guidance = guidance };
    }

    internal static Dictionary<string, string> Params(params (string Name, string Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

    internal static string N(long n) => n.ToString(CultureInfo.InvariantCulture);

    internal static string Day(DateTime? t) => t is { } v ? v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";

    internal static string Minute(DateTime? t) => t is { } v ? v.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";

    /// <summary>The first 15 names in ordinal order.</summary>
    internal static string Sample(IEnumerable<string> names) => string.Join(", ", names.Order(StringComparer.OrdinalIgnoreCase).Take(15));

    internal static HashSet<string> Set(IEnumerable<string> values) => new(values.Where(v => v.Length > 0), StringComparer.OrdinalIgnoreCase);
}
