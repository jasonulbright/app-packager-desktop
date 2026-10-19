using System.Globalization;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>Plain texts of the Compliance section.</summary>
public static class ComplianceText
{
    public const string DetectOnly = "Detect only";
    public const string Remediate = "Remediate";
    public const string AllChoice = "";

    public static string Mode(bool remediate) => remediate ? Remediate : DetectOnly;

    public static string Date(DateTime? value) =>
        value is { } v ? (v.Kind == DateTimeKind.Utc ? v.ToLocalTime() : v).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";

    public static string Count(int n, string noun, string? plural = null) => CollectionsText.Count(n, noun, plural);

    public static string Target(string name, string id) => CollectionsText.Target(name, id);

    public static string DriftTag(CatalogEntry entry) => $"[ccm:{entry.Id}@{entry.Version}]";

    public static string Registry(RegistrySurfaceItem item) =>
        item.Values.Count == 0 ? $@"{item.Hive}\{item.KeyPath}" : $@"{item.Hive}\{item.KeyPath} ({string.Join(", ", item.Values)})";

    public static string Os(CatalogEntry entry)
    {
        var parts = new List<string>();
        if (entry.ClientOs.Count > 0) parts.Add(string.Join(", ", entry.ClientOs));
        if (entry.ServerOs.Count > 0) parts.Add(string.Join(", ", entry.ServerOs));
        return parts.Count == 0 ? "(none)" : string.Join("; ", parts);
    }

    public static string Lines(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines.Where(l => !string.IsNullOrWhiteSpace(l)));

    public static string OrNone(IReadOnlyList<string> values) => values.Count == 0 ? "(none)" : string.Join(", ", values);

    /// <summary>The stored deployment values a re-create sets again, in plain words; empty when the row holds none.</summary>
    public static string DeploymentSettings(BaselineDeploymentRow row) => row.Settings is not { } s ? ""
        : $"Kept values: schedule {row.Schedule}, remediation outside maintenance windows {(s.OverrideServiceWindow ? "allowed" : "not allowed")}, "
          + $"alert {(s.GenerateAlert ? $"below {s.AlertThresholdPercent}% compliance" : "off")}, Operations Manager alerts {(s.MonitoredByScom ? "on" : "off")}.";


}

/// <summary>One catalog entry in the Catalog grid.</summary>
public sealed record CatalogGridRow(CatalogEntry Entry)
{
    public string DisplayName => Entry.DisplayName;
    public string Subcategory => Entry.Subcategory;
    public string Severity => Entry.Severity;
    public string Status => Entry.Status;
    public string Version => Entry.Version;
    public string Produces => ComplianceText.Count(Entry.ProducesBaselines.Count, "baseline");

    public override string ToString() => $"{DisplayName}, {Subcategory}, {Severity}";
}

/// <summary>One configuration item in the Live CI/CB grid.</summary>
public sealed record ConfigurationItemGridRow(ConfigurationItemRow Row)
{
    public string Name => Row.Name;
    public string CiId => Row.CiId;
    public int Revision => Row.Revision;

    /// <summary>The number of settings; empty when the list read does not hold it.</summary>
    public string SettingCount => Row.SettingCount < 0 ? "" : Row.SettingCount.ToString(CultureInfo.InvariantCulture);
    public int BaselineCount => Row.BaselineCount;
    public string Modified => ComplianceText.Date(Row.Modified);

    public override string ToString() => ComplianceText.Target(Name, CiId);
}

/// <summary>One baseline in the Live CI/CB grid.</summary>
public sealed record BaselineGridRow(BaselineRow Row)
{
    public string Name => Row.Name;
    public string BaselineId => Row.BaselineId;
    public int Revision => Row.Revision;
    public string Catalog => Row.CatalogId is null ? "" : $"{Row.CatalogId} {Row.CatalogVersion}".Trim();
    public int ItemCount => Row.ItemCount;
    public int DeploymentCount => Row.DeploymentCount;
    public string Compliance => $"{Row.Compliant} compliant, {Row.NonCompliant} non-compliant, {Row.Failed} error";
    public string Modified => ComplianceText.Date(Row.Modified);

    public override string ToString() => ComplianceText.Target(Name, BaselineId);
}

/// <summary>One baseline deployment in the deployments grid.</summary>
public sealed record DeploymentGridRow(BaselineDeploymentRow Row)
{
    public string BaselineName => Row.BaselineName;
    public string Collection => ComplianceText.Target(Row.CollectionName, Row.CollectionId);
    public string Mode => ComplianceText.Mode(Row.RemediateNoncompliant);
    public int Compliant => Row.Compliant;
    public int NonCompliant => Row.NonCompliant;
    public int Error => Row.Error;
    public int Unknown => Row.Unknown;
    public string LastEvaluated => ComplianceText.Date(Row.LastEvaluated);

    public override string ToString() => $"{BaselineName} on {Collection}, {Mode}";
}

/// <summary>One setting of a configuration item in the Editor grid.</summary>
public sealed record SettingGridRow(CiSetting Setting)
{
    public string Name => Setting.Name;
    public string Kind => Setting.IsEditable ? Setting.Kind : $"{Setting.Kind}, read-only";
    public string DataType => Setting.DataType;
    public string Detail => Setting.Detail;
    public string Rule => Setting.ExpectedValue is null ? "" : $"{Setting.Operator} {string.Join(", ", SettingRules.Values(Setting))}";
    public string ExpectedValue => Setting.ExpectedValue ?? "";
    public string Severity => Setting.Severity;

    public override string ToString() => $"{Name}, {Kind}, {DataType}";
}

/// <summary>One baseline in the Drift grid.</summary>
public sealed record DriftGridRow(DriftRow Row)
{
    public string BaselineName => Row.BaselineName;
    public string BaselineId => Row.BaselineId;
    public string CatalogId => Row.CatalogId ?? "";
    public string DeployedVersion => Row.DeployedVersion ?? "";
    public string CatalogVersion => Row.CatalogVersion ?? "";
    public string State => Row.State;

    public override string ToString() => $"{BaselineName}, {State}";
}

/// <summary>One baseline and collection in the Compliance grid.</summary>
public sealed record ComplianceGridRow(ComplianceRow Row)
{
    public string BaselineName => Row.BaselineName;
    public string CollectionName => Row.CollectionName;
    public int Compliant => Row.Compliant;
    public int NonCompliant => Row.NonCompliant;
    public int Error => Row.Error;
    public int Unknown => Row.Unknown;
    public string LastEvaluated => ComplianceText.Date(Row.LastEvaluated);
    public string Freshness => Row.Stale ? "Stale" : "Current";

    public override string ToString() => $"{BaselineName} on {CollectionName}, {Freshness}";
}

/// <summary>The requests the Compliance section sends for a plan; the service fills in the current state.</summary>
public static class ComplianceRequests
{
    /// <summary>The value key that names the dependency a catalog deploy creates instead of running the entry's builder.</summary>
    public const string CreateDependencyKey = "CreateDependency";

    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();

    private static ComplianceChange New(ComplianceChangeKind kind, string? catalogId = null, IReadOnlyDictionary<string, string>? values = null,
        IReadOnlyList<string>? targets = null, BaselineDeploymentRow? deployment = null, bool? remediate = null, string? toCollection = null,
        string? ciId = null, CiSetting? before = null, CiSetting? after = null) =>
        new(kind, catalogId, values ?? NoValues, targets ?? [], deployment, remediate, toCollection, ciId, before, after, [], []);

    public static ComplianceChange Deploy(CatalogEntry entry, IReadOnlyDictionary<string, string> values, IReadOnlyList<string> targetCollectionIds, bool remediate) =>
        New(ComplianceChangeKind.CatalogDeploy, entry.Id, values, targetCollectionIds, remediate: remediate);

    public static ComplianceChange CreateDependency(CatalogEntry entry, CatalogDependency dependency) =>
        New(ComplianceChangeKind.CatalogDeploy, entry.Id, new Dictionary<string, string> { [CreateDependencyKey] = dependency.Name });

    public static ComplianceChange SetRemediation(BaselineDeploymentRow deployment, bool remediate) =>
        New(ComplianceChangeKind.SetRemediation, deployment: deployment, remediate: remediate);

    public static ComplianceChange Redeploy(BaselineDeploymentRow deployment) => New(ComplianceChangeKind.Redeploy, deployment: deployment);

    public static ComplianceChange Reassign(BaselineDeploymentRow deployment, string toCollectionId) =>
        New(ComplianceChangeKind.Reassign, deployment: deployment, toCollection: toCollectionId);

    public static ComplianceChange Delete(BaselineDeploymentRow deployment) => New(ComplianceChangeKind.DeleteDeployment, deployment: deployment);

    public static ComplianceChange AddSetting(string ciId, CiSetting setting) => New(ComplianceChangeKind.AddSetting, ciId: ciId, after: setting);

    public static ComplianceChange EditSetting(string ciId, CiSetting before, CiSetting after) =>
        New(ComplianceChangeKind.EditSetting, ciId: ciId, before: before, after: after);

    public static ComplianceChange RemoveSetting(string ciId, CiSetting setting) => New(ComplianceChangeKind.RemoveSetting, ciId: ciId, before: setting);
}
