namespace ApplicationPackagerEngine.Services;

/// <param name="Type">String or Switch. The schema also allows Int and StringArray; no shipped entry uses them.</param>
/// <param name="Default">The default value as text: null for none, "True" or "False" for a switch.</param>
public sealed record CatalogParameter(string Name, string Type, bool Required, string? Default, string Description);

/// <param name="Type">Collection: a device collection that must exist before the entry is deployed.</param>
/// <param name="CreatedBy">The builder that can create the dependency; null when no shipped builder can.</param>
public sealed record CatalogDependency(string Type, string Name, string? CreatedBy);

/// <param name="References">KB numbers and Microsoft Learn addresses.</param>
/// <param name="BuilderResource">The logical name of the embedded builder script.</param>
/// <param name="BuilderSha256">The SHA-256 of the embedded builder script, upper-case hexadecimal.</param>
/// <param name="TenablePlugins">Vulnerability scanner plugin IDs the entry addresses.</param>
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
    string BuilderSha256)
{
    /// <summary>The names of the parameters that take a collection name, in parameter order.</summary>
    public IReadOnlyList<string> CollectionParameters =>
        Parameters.Where(p => p.Type == "String" && p.Name.EndsWith("CollectionName", StringComparison.Ordinal)).Select(p => p.Name).ToList();
}

public sealed record RegistrySurfaceItem(string Hive, string KeyPath, IReadOnlyList<string> Values);

/// <param name="Revision">The CI version ConfigMgr stores; every setting change adds one.</param>
/// <param name="SettingCount">The number of settings; -1 when the list read does not hold it.</param>
/// <param name="BaselineCount">The number of baselines that hold the CI.</param>
public sealed record ConfigurationItemRow(string CiId, string Name, string Description, int Revision, int SettingCount, int BaselineCount, DateTime? Modified);

/// <param name="CatalogId">The catalog entry ID of the drift tag; null when the description has no valid tag.</param>
/// <param name="CatalogVersion">The catalog version of the drift tag; null when the description has no valid tag.</param>
/// <param name="Compliant">The number of compliant devices over all deployments of the baseline.</param>
/// <param name="NonCompliant">The number of non-compliant devices over all deployments of the baseline.</param>
/// <param name="Failed">The number of devices with an evaluation error over all deployments of the baseline.</param>
/// <param name="CountsKnown">False when ConfigMgr returned no value for a count; the counts are then 0 and mean unknown, not zero.</param>
public sealed record BaselineRow(
    string BaselineId,
    string Name,
    string Description,
    int Revision,
    string? CatalogId,
    string? CatalogVersion,
    int ItemCount,
    int DeploymentCount,
    DateTime? Modified,
    int Compliant = 0,
    int NonCompliant = 0,
    int Failed = 0,
    bool CountsKnown = true);

/// <param name="Schedule">The evaluation schedule as plain text.</param>
/// <param name="LastEvaluated">The time (UTC) ConfigMgr last summarized the compliance state of the deployment; null when it never did.</param>
/// <param name="Settings">The values a re-create needs; filled by every read.</param>
/// <param name="CountsKnown">False when the deployment has no compliance summary yet or ConfigMgr returned no value for a count; the counts are then 0 and mean unknown, not zero.</param>
public sealed record BaselineDeploymentRow(
    string DeploymentId,
    string BaselineId,
    string BaselineName,
    string CollectionId,
    string CollectionName,
    bool RemediateNoncompliant,
    string Schedule,
    int Compliant,
    int NonCompliant,
    int Error,
    int Unknown,
    DateTime? LastEvaluated,
    BaselineDeploymentSettings? Settings = null,
    bool CountsKnown = true);

/// <summary>The stored values of a baseline deployment that a re-create sets again.</summary>
/// <param name="ScheduleToken">The ConfigMgr schedule token of the evaluation schedule.</param>
/// <param name="OverrideServiceWindow">
/// Remediation may run outside maintenance windows. ConfigMgr stores it on a new deployment only
/// when remediation is on; an existing deployment keeps it when remediation is turned off.
/// </param>
/// <param name="GenerateAlert">An alert is raised below <paramref name="AlertThresholdPercent"/>.</param>
/// <param name="AlertThresholdPercent">The compliance percentage below which the alert is raised; 0 when there is no alert.</param>
/// <param name="AlertAfter">The time (UTC) after which the alert is raised; null when there is none.</param>
/// <param name="MonitoredByScom">Operations Manager alerts are raised for the deployment; ConfigMgr stores it on the alert, so it needs <paramref name="GenerateAlert"/>.</param>
public sealed record BaselineDeploymentSettings(
    string ScheduleToken,
    bool OverrideServiceWindow,
    bool GenerateAlert,
    int AlertThresholdPercent,
    DateTime? AlertAfter,
    bool MonitoredByScom);

/// <summary>
/// One compliance setting of a configuration item with its rule. The optional values describe
/// the setting completely, so an Add with them creates an equal setting.
/// </summary>
/// <param name="SettingId">The logical name ConfigMgr gives the setting; empty for a setting that is not created yet.</param>
/// <param name="Kind">Registry or Script; any other kind is shown read-only.</param>
/// <param name="DataType">String, Integer, DateTime, FloatingPoint, Version, StringArray (registry) or Boolean (script).</param>
/// <param name="Detail">The hive, key and value name of a registry setting, or the script language and line count of a script setting.</param>
/// <param name="ExpectedValue">The value the rule expects; null when the setting has no value rule.</param>
/// <param name="Hive">LocalMachine, CurrentUser, ClassesRoot, CurrentConfig or Users; registry settings only.</param>
/// <param name="Is64Bit">True when the setting reads the 64-bit registry view or runs the script as a 64-bit process.</param>
/// <param name="Operator">The rule operator, for example IsEquals.</param>
/// <param name="Severity">None, Informational, Warning, Critical or CriticalWithEvent.</param>
/// <param name="IsEditable">False for a setting this page shows but does not change: a kind other than Registry or Script, or a rule other than one value rule.</param>
/// <param name="RemediateDword">A registry setting remediates the value as a DWORD.</param>
public sealed record CiSetting(
    string SettingId,
    string Name,
    string Kind,
    string DataType,
    string Detail,
    string? ExpectedValue,
    string Description = "",
    string Hive = "",
    string KeyName = "",
    string ValueName = "",
    bool Is64Bit = true,
    string DiscoveryLanguage = "",
    string DiscoveryScript = "",
    string RemediationLanguage = "",
    string RemediationScript = "",
    string RuleName = "",
    string Operator = "IsEquals",
    bool Remediate = false,
    bool ReportNoncompliance = true,
    string Severity = "Warning",
    bool IsEditable = true,
    bool RemediateDword = false);

/// <param name="State">Current, Behind, Untagged or Unknown (a tag whose ID is not in the catalog, or a version newer than the catalog).</param>
public sealed record DriftRow(string BaselineName, string BaselineId, string? CatalogId, string? DeployedVersion, string? CatalogVersion, string State);

/// <param name="LastEvaluated">The time (UTC) ConfigMgr last summarized the deployment; null when it never did.</param>
/// <param name="Stale">
/// True when <paramref name="LastEvaluated"/> is more than <see cref="ComplianceRules.StaleDays"/> days old,
/// and when the deployment was never summarized (the counts are then not data).
/// </param>
/// <param name="CountsKnown">False when the deployment has no compliance summary yet or ConfigMgr returned no value for a count; the counts are then 0 and mean unknown, not zero.</param>
public sealed record ComplianceRow(
    string BaselineName,
    string CollectionName,
    int Compliant,
    int NonCompliant,
    int Error,
    int Unknown,
    DateTime? LastEvaluated,
    bool Stale,
    string BaselineId = "",
    string CollectionId = "",
    string DeploymentId = "",
    bool CountsKnown = true);

public enum ComplianceChangeKind
{
    CatalogDeploy,
    SetRemediation,
    Redeploy,
    Reassign,
    DeleteDeployment,
    AddSetting,
    EditSetting,
    RemoveSetting,

    /// <summary>Creates a baseline deployment from <see cref="ComplianceChange.DeploymentBefore"/>: the Undo of a delete or of a failed redeploy.</summary>
    CreateDeployment,

    /// <summary>Removes the deployments, baselines and configuration items in <see cref="ComplianceChange.CreatedIds"/>, in that order: the Undo of a catalog deploy.</summary>
    RemoveCatalogObjects
}

/// <summary>
/// One change. What each kind reads:
/// CatalogDeploy runs the builder of entry <see cref="CatalogId"/> with <see cref="Values"/> (builder
/// parameter name to value; collection parameters hold collection names; SiteCode and SiteServer
/// come from the connection when absent). SetRemediation sets <see cref="RemediateAfter"/> on
/// <see cref="DeploymentBefore"/>. Redeploy removes <see cref="DeploymentBefore"/> and creates it
/// again. Reassign creates the deployment of <see cref="DeploymentBefore"/> on <see cref="ToCollectionId"/>
/// and then removes the old one. DeleteDeployment removes <see cref="DeploymentBefore"/>.
/// CreateDeployment creates <see cref="DeploymentBefore"/>. AddSetting adds <see cref="SettingAfter"/>
/// to CI <see cref="CiId"/>; EditSetting replaces <see cref="SettingBefore"/> with
/// <see cref="SettingAfter"/>; RemoveSetting removes <see cref="SettingBefore"/>.
/// RemoveCatalogObjects removes <see cref="CreatedIds"/>.
/// </summary>
/// <param name="TargetCollectionIds">For CatalogDeploy, the collections of the collection parameters in parameter order; filled by PrepareAsync.</param>
/// <param name="Impact">Plain lines for the plan, filled by <see cref="IComplianceService.PrepareAsync"/>.</param>
/// <param name="CreatedIds">
/// For RemoveCatalogObjects, the objects to remove as "Deployment:&lt;id&gt;", "Baseline:&lt;CI_ID&gt;" and
/// "ConfigurationItem:&lt;CI_ID&gt;"; filled from the read-back of a catalog deploy.
/// </param>
/// <param name="RemediationConfirmed">
/// True when the operator confirmed remediation a second time. A CatalogDeploy without DetectOnly,
/// a SetRemediation to on and a re-create with remediation on are refused without it.
/// </param>
/// <param name="CiName">The name of CI <see cref="CiId"/>, for messages.</param>
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
    IReadOnlyList<string> CreatedIds,
    bool RemediationConfirmed = false,
    string? CiName = null);

/// <param name="Message">Plain text for the operator; never raw exception text. Starts with "Skipped: " when the change was not attempted.</param>
/// <param name="UndoChange">The change that reverses this one; set on every outcome that changed ConfigMgr and can be reversed.</param>
/// <param name="Steps">Plain lines, one per step done, in order.</param>
/// <param name="InProgress">
/// True for an interim report of a catalog deploy that is still running: one per step, with Succeeded true and the step line as Message;
/// the final outcome of every change has false.
/// </param>
public sealed record ComplianceOutcome(
    ComplianceChange Change,
    bool Succeeded,
    string Message,
    ComplianceChange? UndoChange,
    IReadOnlyList<string> Steps,
    bool InProgress = false);

public interface ICatalogService
{
    /// <summary>The shipped catalog entries that passed validation, in subcategory and display name order.</summary>
    IReadOnlyList<CatalogEntry> All();

    CatalogEntry? Find(string id);

    /// <summary>A plain message about entries that failed validation; null when every entry loaded.</summary>
    string? Message { get; }
}

public interface IComplianceService
{
    Task<IReadOnlyList<ConfigurationItemRow>> ListConfigurationItemsAsync(CancellationToken ct);

    Task<IReadOnlyList<BaselineRow>> ListBaselinesAsync(CancellationToken ct);

    /// <summary>The deployments of one baseline, or of all baselines when <paramref name="baselineId"/> is null.</summary>
    Task<IReadOnlyList<BaselineDeploymentRow>> ListDeploymentsAsync(string? baselineId, CancellationToken ct);

    Task<IReadOnlyList<CiSetting>> ListSettingsAsync(string ciId, CancellationToken ct);

    Task<IReadOnlyList<DriftRow>> GetDriftAsync(CancellationToken ct);

    Task<IReadOnlyList<ComplianceRow>> GetComplianceAsync(CancellationToken ct);

    /// <summary>
    /// Reads the current state and returns the change with the captured values and the Impact
    /// lines filled. Throws <see cref="ComplianceServiceException"/> with a plain message when the
    /// change cannot be made in the current state.
    /// </summary>
    Task<ComplianceChange> PrepareAsync(ComplianceChange request, CancellationToken ct);

    /// <summary>
    /// Applies the changes in order and reports each outcome through <paramref name="progress"/>
    /// as soon as it is known; a catalog deploy also reports interim outcomes per step.
    /// Cancellation stops between changes and stops a running builder; the changes not started
    /// are returned as skipped outcomes.
    /// </summary>
    Task<IReadOnlyList<ComplianceOutcome>> ApplyAsync(
        IReadOnlyList<ComplianceChange> changes,
        IProgress<ComplianceOutcome> progress,
        CancellationToken ct);
}

public interface IComplianceReportWriter
{
    /// <summary>Writes the drift rows and the compliance rows to one new CSV file, one section each, and returns its path.</summary>
    string WriteCsv(IReadOnlyList<DriftRow> drift, IReadOnlyList<ComplianceRow> compliance);

    /// <summary>Writes the drift rows and the compliance rows to one new HTML file, one table each, and returns its path.</summary>
    string WriteHtml(IReadOnlyList<DriftRow> drift, IReadOnlyList<ComplianceRow> compliance);
}
/// <summary>Fixed rules shared by the service and the page.</summary>
public static class ComplianceRules
{
    /// <summary>
    /// A deployment whose compliance summary is older than this many days is stale. The summary
    /// time is the freshness a site holds: a device reports its compliance again only when the
    /// state changes, so the newest device report can be weeks old on a healthy deployment.
    /// </summary>
    public const int StaleDays = 7;

    /// <summary>
    /// How far the clock of this computer and the clock of the site database may differ when a
    /// catalog deploy decides that an object was created during its run.
    /// </summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    /// <summary>The longest value a catalog builder parameter accepts.</summary>
    public const int MaxParameterLength = 256;
}

/// <summary>Why a compliance operation could not run.</summary>
public enum ComplianceFailureKind
{
    /// <summary>The request is not valid or the rules refuse it.</summary>
    Refused,

    /// <summary>The object changed since it was read.</summary>
    Stale,

    /// <summary>The object does not exist.</summary>
    NotFound,

    /// <summary>The ConfigMgr session or the provider failed, or returned data that could not be read.</summary>
    Session
}

/// <summary>A compliance operation that could not run; the message is plain text for the operator.</summary>
public sealed class ComplianceServiceException(string message, ComplianceFailureKind kind = ComplianceFailureKind.Refused) : Exception(message)
{
    public ComplianceFailureKind Kind { get; } = kind;
}
