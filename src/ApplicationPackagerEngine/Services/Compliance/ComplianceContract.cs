namespace ApplicationPackagerEngine.Services;

/// <param name="Type">String or Switch.</param>
public sealed record CatalogParameter(string Name, string Type, bool Required, string? Default, string Description);

public sealed record CatalogDependency(string Type, string Name, string? CreatedBy);

public sealed record RegistrySurfaceItem(string Hive, string KeyPath, IReadOnlyList<string> Values);

public sealed record CatalogEntry(
    string Id,
    string Version,
    string DisplayName,
    string Subcategory,
    string Status,
    string Description,
    string Severity,
    IReadOnlyList<string> Cves,
    IReadOnlyList<int> TenablePlugins,
    IReadOnlyList<string> References,
    IReadOnlyList<string> ClientOs,
    IReadOnlyList<string> ServerOs,
    bool RebootRequired,
    IReadOnlyList<RegistrySurfaceItem> RegistrySurface,
    IReadOnlyList<CatalogParameter> Parameters,
    IReadOnlyList<CatalogDependency> Dependencies,
    IReadOnlyList<string> ProducesConfigurationItems,
    IReadOnlyList<string> ProducesBaselines,
    string BuilderResource,
    string BuilderSha256);

public sealed record ConfigurationItemRow(string CiId, string Name, string Description, int Revision, int SettingCount, int BaselineCount, DateTime? Modified);

/// <param name="CatalogId">From the drift tag in the description; null without a tag.</param>
public sealed record BaselineRow(string BaselineId, string Name, string Description, int Revision, string? CatalogId, string? CatalogVersion,
    int ItemCount, int DeploymentCount, DateTime? Modified);

public sealed record BaselineDeploymentRow(string DeploymentId, string BaselineId, string BaselineName, string CollectionId, string CollectionName,
    bool RemediateNoncompliant, string Schedule, int Compliant, int NonCompliant, int Error, int Unknown, DateTime? LastEvaluated);

/// <param name="Kind">Registry or Script.</param>
/// <param name="Detail">The hive, key and value name, or a summary of the script.</param>
public sealed record CiSetting(string SettingId, string Name, string Kind, string DataType, string Detail, string? ExpectedValue,
    string? Hive = null, string? KeyPath = null, string? ValueName = null,
    string? ScriptLanguage = null, string? DiscoveryScript = null, string? RemediationScript = null);

/// <param name="State">Current, Behind, Untagged or Unknown.</param>
public sealed record DriftRow(string BaselineName, string BaselineId, string? CatalogId, string? DeployedVersion, string? CatalogVersion, string State);

public sealed record ComplianceRow(string BaselineName, string CollectionName, int Compliant, int NonCompliant, int Error, int Unknown, DateTime? LastEvaluated, bool Stale);

public enum ComplianceChangeKind { CatalogDeploy, SetRemediation, Redeploy, Reassign, DeleteDeployment, AddSetting, EditSetting, RemoveSetting }

/// <param name="Impact">Plain lines filled by <see cref="IComplianceService.PrepareAsync"/>.</param>
/// <param name="CreatedIds">Filled by the run; Undo uses them.</param>
public sealed record ComplianceChange(
    ComplianceChangeKind Kind,
    string? CatalogId,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string> TargetCollectionIds,
    BaselineDeploymentRow? DeploymentBefore,
    bool? RemediateAfter,
    string? ToCollectionId,
    string? CiId,
    CiSetting? SettingBefore,
    CiSetting? SettingAfter,
    IReadOnlyList<string> Impact,
    IReadOnlyList<string> CreatedIds);

/// <param name="Message">Plain text without raw exception text; starts with "Skipped" when the change was not attempted.</param>
public sealed record ComplianceOutcome(ComplianceChange Change, bool Succeeded, string Message, ComplianceChange? UndoChange, IReadOnlyList<string> Steps);

/// <summary>A failure with a plain message for the operator.</summary>
public sealed class ComplianceServiceException(string message) : Exception(message);

public interface ICatalogService
{
    IReadOnlyList<CatalogEntry> All();
    CatalogEntry? Find(string id);
    string? Message { get; }
}

public interface IComplianceService
{
    Task<IReadOnlyList<ConfigurationItemRow>> ListConfigurationItemsAsync(CancellationToken ct);
    Task<IReadOnlyList<BaselineRow>> ListBaselinesAsync(CancellationToken ct);
    Task<IReadOnlyList<BaselineDeploymentRow>> ListDeploymentsAsync(string? baselineId, CancellationToken ct);
    Task<IReadOnlyList<CiSetting>> ListSettingsAsync(string ciId, CancellationToken ct);
    Task<IReadOnlyList<DriftRow>> GetDriftAsync(CancellationToken ct);
    Task<IReadOnlyList<ComplianceRow>> GetComplianceAsync(CancellationToken ct);
    Task<ComplianceChange> PrepareAsync(ComplianceChange request, CancellationToken ct);
    Task<IReadOnlyList<ComplianceOutcome>> ApplyAsync(IReadOnlyList<ComplianceChange> changes, IProgress<ComplianceOutcome> progress, CancellationToken ct);
}

public interface IComplianceReportWriter
{
    string WriteCsv(IReadOnlyList<DriftRow> drift, IReadOnlyList<ComplianceRow> compliance);
    string WriteHtml(IReadOnlyList<DriftRow> drift, IReadOnlyList<ComplianceRow> compliance);
}
