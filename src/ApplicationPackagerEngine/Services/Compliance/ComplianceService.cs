using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Reads and changes configuration items, baselines, their deployments and settings through the
/// ConfigMgr session, and deploys catalog entries through their builders. Each change re-reads
/// the object it changes in the same script run as the write, and reads the stored result back.
/// A write runs to its end once started: cancellation takes effect between changes, and for a
/// catalog deploy it also ends the running builder.
/// </summary>
public sealed partial class ComplianceService : IComplianceService
{
    public const string LogArea = "Compliance";

    /// <summary>Starts the message of an outcome that changed nothing on purpose.</summary>
    public const string SkippedPrefix = "Skipped: ";

    internal const string ReadResource = "ApplicationPackagerEngine.Services.Compliance.Scripts.ReadCompliance.ps1";
    internal const string WriteResource = "ApplicationPackagerEngine.Services.Compliance.Scripts.WriteCompliance.ps1";

    internal const string AccessHint =
        "The account needs a ConfigMgr security role with Compliance Settings Manager rights (configuration items, baselines and their deployments) and read rights on collections.";

    internal const string RerunMessage =
        "On a second run a builder creates a second configuration item and baseline with the same names, adds the new item to the existing baseline too and deploys both baselines (seen on a ConfigMgr site), so the page does not run it while objects with these names exist. Remove or rename them first.";

    private static readonly ConfigMgrScript ReadScript = ConfigMgrScript.FromResource(ReadResource);
    private static readonly ConfigMgrScript WriteScript = ConfigMgrScript.FromResource(WriteResource);
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IConfigMgrSession _session;
    private readonly ICatalogService _catalog;
    private readonly ICatalogBuilderRunner _runner;
    private readonly Func<ConfigMgrConnection> _connection;
    private readonly IAppLog? _log;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<string> _tempSuffix;
    private readonly Func<CatalogEntry, byte[]?> _builderBytes;

    public ComplianceService(
        IConfigMgrSession session,
        ICatalogService catalog,
        ICatalogBuilderRunner runner,
        Func<ConfigMgrConnection> connection,
        IAppLog? log = null,
        Func<DateTime>? utcNow = null,
        Func<string>? tempSuffix = null,
        Func<CatalogEntry, byte[]?>? builderBytes = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _tempSuffix = tempSuffix ?? (() => Guid.NewGuid().ToString("N")[..8]);
        _builderBytes = builderBytes ?? EmbeddedBuilder;
    }

    /// <summary>The longest time one builder may run.</summary>
    public TimeSpan BuilderTimeout { get; init; } = CatalogBuilderRunner.DefaultTimeout;

    [GeneratedRegex("^[0-9]{1,10}$")]
    private static partial Regex NumberRegex();

    [GeneratedRegex("^[A-Za-z0-9]{1,8}$")]
    private static partial Regex CollectionIdRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex SettingIdRegex();

    public static bool IsBuiltInCollection(string? id) => id is not null && id.StartsWith("SMS", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the summary is more than <see cref="ComplianceRules.StaleDays"/> days older than <paramref name="nowUtc"/>; a deployment that was never summarized is not stale.</summary>
    public static bool IsStale(DateTime? lastEvaluatedUtc, DateTime nowUtc) =>
        lastEvaluatedUtc is { } last && last < nowUtc.AddDays(-ComplianceRules.StaleDays);

    // ---- Script output shapes ----------------------------------------------------------------

    internal sealed record ItemRow(string? Kind, string? CiId, string? Name, string? Description, int Revision, int BaselineCount, int Deployments, DateTime? Modified);

    internal sealed record BaselineReadRow(
        string? Kind, string? BaselineId, string? Name, string? Description, int Revision, int ItemCount, int DeploymentCount,
        int Compliant, int NonCompliant, int Failed, DateTime? Modified);

    internal sealed record DeploymentReadRow(
        string? Kind, string? AssignmentId, string? UniqueId, string? BaselineId, string? BaselineName, string? CollectionId, string? CollectionName,
        bool Enforcement, string? Schedule, bool OverrideWindows, bool MonitoredByScom, bool GenerateAlert, int AlertThreshold,
        int Compliant, int NonCompliant, int Failed, int Unknown, DateTime? Summarized);

    internal sealed record SettingReadRow(
        string? Kind, string? SettingId, string? Name, string? Description, string? SettingKind, string? DataType, int RuleCount, bool HasValueRule,
        string? Operator, string[]? Values, string? RuleName, string? Severity, bool Remediate, bool Report, string? Hive, string? KeyName, string? ValueName,
        bool Is64Bit, bool RemediateDword, string? DiscoveryLanguage, string? DiscoveryScript, string? RemediationLanguage, string? RemediationScript);

    internal sealed record FoundRow(string? Kind, string? Id, string? Name, int Revision, string? Description, string? BaselineId, string? CollectionId, int CollectionType, int Members);

    internal sealed record WriteResult(string? Kind, string? Status, string? Detail, string[]? Steps, string? CreatedId, string[]? References, string? CiId, int Revision);

    // ---- Reads --------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ConfigurationItemRow>> ListConfigurationItemsAsync(CancellationToken ct)
    {
        var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "Items" } }, ct).ConfigureAwait(false);
        var rows = Rows<ItemRow>(output, "Item")
            .Select(r => new ConfigurationItemRow(r.CiId ?? "", r.Name ?? "", r.Description ?? "", r.Revision, -1, r.BaselineCount, r.Modified))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _log?.Info(LogArea, $"Listed {rows.Count} configuration items.");
        return rows;
    }

    public async Task<IReadOnlyList<BaselineRow>> ListBaselinesAsync(CancellationToken ct)
    {
        var rows = (await ReadBaselinesAsync(ct).ConfigureAwait(false)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _log?.Info(LogArea, $"Listed {rows.Count} configuration baselines.");
        return rows;
    }

    private async Task<IReadOnlyList<BaselineRow>> ReadBaselinesAsync(CancellationToken ct)
    {
        var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "Baselines" } }, ct).ConfigureAwait(false);
        return Rows<BaselineReadRow>(output, "Baseline").Select(r =>
        {
            var tag = DriftTag.Parse(r.Description);
            var valid = tag is { Valid: true };
            return new BaselineRow(r.BaselineId ?? "", r.Name ?? "", r.Description ?? "", r.Revision, valid ? tag!.Value.Id : null, valid ? tag!.Value.Version : null,
                r.ItemCount, r.DeploymentCount, r.Modified, r.Compliant, r.NonCompliant, r.Failed);
        }).ToList();
    }

    public async Task<IReadOnlyList<BaselineDeploymentRow>> ListDeploymentsAsync(string? baselineId, CancellationToken ct)
    {
        var parameters = new ConfigMgrParameters { { "Operation", "Deployments" } };
        if (baselineId is not null) parameters["BaselineId"] = CheckedNumber(baselineId, "Select a baseline.");
        var rows = (await ReadDeploymentsAsync(parameters, ct).ConfigureAwait(false))
            .OrderBy(r => r.BaselineName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.CollectionName, StringComparer.OrdinalIgnoreCase).ToList();
        _log?.Info(LogArea, $"Listed {rows.Count} baseline deployments.");
        return rows;
    }

    private async Task<IReadOnlyList<BaselineDeploymentRow>> ReadDeploymentsAsync(ConfigMgrParameters parameters, CancellationToken ct)
    {
        var output = await ReadAsync(parameters, ct).ConfigureAwait(false);
        return Rows<DeploymentReadRow>(output, "Deployment").Select(ToDeployment).ToList();
    }

    private static BaselineDeploymentRow ToDeployment(DeploymentReadRow r) => new(
        r.AssignmentId ?? "", r.BaselineId ?? "", r.BaselineName ?? "", r.CollectionId ?? "", r.CollectionName ?? "", r.Enforcement,
        ScheduleText(r.Schedule), r.Compliant, r.NonCompliant, r.Failed, r.Unknown, r.Summarized,
        new BaselineDeploymentSettings(r.Schedule ?? "", r.OverrideWindows, r.GenerateAlert, r.AlertThreshold, null, r.MonitoredByScom));

    public async Task<IReadOnlyList<CiSetting>> ListSettingsAsync(string ciId, CancellationToken ct)
    {
        var (_, settings) = await ReadSettingsAsync(CheckedNumber(ciId, "Select a configuration item."), ct).ConfigureAwait(false);
        _log?.Info(LogArea, $"Read {settings.Count} settings of one configuration item.");
        return settings;
    }

    private async Task<(ItemRow? Item, IReadOnlyList<CiSetting> Settings)> ReadSettingsAsync(string ciId, CancellationToken ct)
    {
        var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "Settings" }, { "CiId", long.Parse(ciId, CultureInfo.InvariantCulture) } }, ct).ConfigureAwait(false);
        var item = Rows<ItemRow>(output, "Item").FirstOrDefault();
        return (item, Rows<SettingReadRow>(output, "Setting").Select(ToSetting).ToList());
    }

    internal static CiSetting ToSetting(SettingReadRow r)
    {
        var kind = r.SettingKind ?? "";
        var type = SettingRules.DataTypeName(r.DataType ?? "");
        var values = r.Values ?? [];
        var editable = kind is "Registry" or "Script" && r.RuleCount == 1 && r.HasValueRule;
        var expected = r.HasValueRule ? string.Join("\n", values) : null;
        var detail = kind switch
        {
            "Registry" => $@"{SettingRules.HiveName(r.Hive ?? "")}\{r.KeyName}{(string.IsNullOrEmpty(r.ValueName) ? "" : @"\" + r.ValueName)}",
            "Script" => $"{r.DiscoveryLanguage} script, {Lines(r.DiscoveryScript)}" + (string.IsNullOrEmpty(r.RemediationScript) ? ", no remediation script" : ", with a remediation script"),
            _ => "A setting kind this page does not change"
        };
        return new CiSetting(r.SettingId ?? "", r.Name ?? "", kind, type, detail, expected, r.Description ?? "", SettingRules.HiveName(r.Hive ?? ""), r.KeyName ?? "", r.ValueName ?? "",
            r.Is64Bit, r.DiscoveryLanguage ?? "", r.DiscoveryScript ?? "", r.RemediationLanguage ?? "", r.RemediationScript ?? "", r.RuleName ?? "",
            SettingRules.OperatorName(r.Operator ?? ""), r.Remediate, r.Report, r.Severity ?? "", editable, r.RemediateDword);
    }

    private static string Lines(string? text)
    {
        var count = string.IsNullOrEmpty(text) ? 0 : text.TrimEnd('\r', '\n').Split('\n').Length;
        return count == 1 ? "1 line" : $"{count.ToString(CultureInfo.InvariantCulture)} lines";
    }

    public async Task<IReadOnlyList<DriftRow>> GetDriftAsync(CancellationToken ct)
    {
        var baselines = await ReadBaselinesAsync(ct).ConfigureAwait(false);
        var rows = baselines.Select(b =>
        {
            var tag = DriftTag.Parse(b.Description);
            var entry = tag is { } t ? _catalog.Find(t.Id) : null;
            return new DriftRow(b.Name, b.BaselineId, tag?.Id, tag?.Version, entry?.Version, DriftTag.State(b.Description, entry));
        }).OrderBy(r => r.BaselineName, StringComparer.OrdinalIgnoreCase).ToList();
        _log?.Info(LogArea, $"Drift of {rows.Count} baselines: {rows.Count(r => r.State == "Current")} current, {rows.Count(r => r.State == "Behind")} behind, {rows.Count(r => r.State == "Untagged")} untagged.");
        return rows;
    }

    public async Task<IReadOnlyList<ComplianceRow>> GetComplianceAsync(CancellationToken ct)
    {
        var now = _utcNow();
        var deployments = await ReadDeploymentsAsync(new ConfigMgrParameters { { "Operation", "Deployments" } }, ct).ConfigureAwait(false);
        var rows = deployments.Select(d => new ComplianceRow(d.BaselineName, d.CollectionName, d.Compliant, d.NonCompliant, d.Error, d.Unknown,
                d.LastEvaluated, IsStale(d.LastEvaluated, now), d.BaselineId, d.CollectionId, d.DeploymentId))
            .OrderBy(r => r.BaselineName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.CollectionName, StringComparer.OrdinalIgnoreCase).ToList();
        _log?.Info(LogArea, $"Compliance of {rows.Count} deployments; {rows.Count(r => r.Stale)} stale.");
        return rows;
    }

    // ---- Apply --------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ComplianceOutcome>> ApplyAsync(
        IReadOnlyList<ComplianceChange> changes, IProgress<ComplianceOutcome> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(progress);
        var outcomes = new List<ComplianceOutcome>(changes.Count);
        var skipped = 0;
        foreach (var change in changes)
        {
            ComplianceOutcome outcome;
            if (ct.IsCancellationRequested)
            {
                outcome = new ComplianceOutcome(change, false, SkippedPrefix + "the run was cancelled before this change.", null, []);
            }
            else
            {
                try
                {
                    outcome = await ApplyOneAsync(change, progress, ct).ConfigureAwait(false) with { Change = change, InProgress = false };
                }
                catch (Exception ex)
                {
                    _log?.Warning(LogArea, $"{change.Kind} stopped with an unexpected error: {ex.GetType().Name}.");
                    outcome = Fail(change, "The change stopped with an unexpected error. Refresh the lists to see the current state. The log has the details.");
                }
                _log?.Info(LogArea, $"{change.Kind}: {(outcome.Succeeded ? "succeeded" : "not applied")}{(outcome.UndoChange is null ? "" : ", with undo")}.");
            }
            if (outcome.Message.StartsWith(SkippedPrefix, StringComparison.Ordinal)) skipped++;
            outcomes.Add(outcome);
            progress.Report(outcome);
        }
        var succeeded = outcomes.Count(o => o.Succeeded);
        _log?.Info(LogArea, $"Applied {changes.Count} changes: {succeeded} succeeded, {outcomes.Count - succeeded - skipped} failed, {skipped} skipped.");
        return outcomes;
    }

    /// <summary>"Every N days" and similar for a simple interval token; the token itself otherwise.</summary>
    public static string ScheduleText(string? token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        var interval = CollectionService.IntervalOf(token);
        if (interval is null) return "Custom schedule";
        var unit = interval.Unit.ToLowerInvariant();
        return interval.Count == 1 ? $"Every {unit.TrimEnd('s')}" : $"Every {interval.Count.ToString(CultureInfo.InvariantCulture)} {unit}";
    }

    // ---- Session helpers ----------------------------------------------------------------------

    private static readonly HashSet<string> LoggedParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Operation", "CollectionId", "ToCollectionId", "ExpectedSchedule", "ScheduleToken", "SettingId", "EditMode", "SettingKind", "DataType", "Hive",
        "DiscoveryLanguage", "RemediationLanguage", "Operator", "Severity"
    };

    /// <summary>
    /// The session logs every parameter that is not secret and masks secret values in the error
    /// text it returns, so names, descriptions, setting values and script text travel as secret values.
    /// </summary>
    private static ConfigMgrParameters Masked(ConfigMgrParameters parameters)
    {
        var masked = new ConfigMgrParameters();
        foreach (var (name, value) in parameters)
        {
            if (value is string text && !LoggedParameters.Contains(name)) masked.AddSecret(name, text);
            else masked.Add(name, value);
        }
        return masked;
    }

    private async Task<ConfigMgrCommandResult> ReadAsync(ConfigMgrParameters parameters, CancellationToken ct)
    {
        try
        {
            return await _session.InvokeAsync(ReadScript, Masked(parameters), ct).ConfigureAwait(false);
        }
        catch (ConfigMgrSessionException ex)
        {
            _log?.Warning(LogArea, $"Reading compliance data failed in the ConfigMgr session: {ex.Failure}, {CollectionService.FailureKinds(ex.Message)}.");
            throw new ComplianceServiceException(ex.Failure == ConfigMgrFailure.ScriptFailed
                ? "The compliance data could not be read from ConfigMgr. The log has the details."
                : SessionProblem(ex.Failure), ComplianceFailureKind.Session);
        }
    }

    private IReadOnlyList<T> Rows<T>(ConfigMgrCommandResult result, string kind)
    {
        try
        {
            return result.Output
                .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("Kind", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == kind)
                .Select(e => e.Deserialize<T>(ReadOptions)!)
                .ToList();
        }
        catch (JsonException ex)
        {
            _log?.Warning(LogArea, $"ConfigMgr returned output that could not be read ({ex.GetType().Name}).");
            throw new ComplianceServiceException("ConfigMgr returned data that could not be read. The log has the details.", ComplianceFailureKind.Session);
        }
    }

    /// <summary>Fixed text for a session failure that happens before any command runs.</summary>
    internal static string SessionProblem(ConfigMgrFailure failure) => failure switch
    {
        ConfigMgrFailure.AccessDenied => $"The SMS Provider denied access. {AccessHint}",
        ConfigMgrFailure.InvalidSiteCode => $"The site code is not valid. Correct it in {ConfigMgrSessionHost.SettingsLocation}.",
        ConfigMgrFailure.PowerShellNotStarted => "Windows PowerShell 5.1 (powershell.exe) could not start. ConfigMgr commands need it.",
        ConfigMgrFailure.ModuleNotFound => "The ConfigurationManager PowerShell module was not found. Install the Configuration Manager console on this computer.",
        ConfigMgrFailure.DriveNotConnected => $"The connection to the ConfigMgr site failed. Check the site code and the SMS Provider in {ConfigMgrSessionHost.SettingsLocation}.",
        ConfigMgrFailure.Timeout => "ConfigMgr did not answer in time. Try again.",
        ConfigMgrFailure.ProcessDied => "The Windows PowerShell session for ConfigMgr ended. Try again; the next command starts a new session.",
        ConfigMgrFailure.RequestTooLarge => "The values are too large for one ConfigMgr command.",
        _ => "The ConfigMgr session failed. The log has the details."
    };

    private static string CheckedNumber(string? id, string message)
    {
        var text = (id ?? "").Trim();
        if (!NumberRegex().IsMatch(text) || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > int.MaxValue)
            throw new ComplianceServiceException(message);
        return text;
    }

    private static string CheckedCollectionId(string? id, string message)
    {
        var text = (id ?? "").Trim().ToUpperInvariant();
        if (!CollectionIdRegex().IsMatch(text)) throw new ComplianceServiceException(message);
        return text;
    }

    private static byte[]? EmbeddedBuilder(CatalogEntry entry)
    {
        using var stream = typeof(ComplianceService).Assembly.GetManifestResourceStream(entry.BuilderResource);
        if (stream is null) return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n.ToString(CultureInfo.InvariantCulture)} {noun}s";

    private static string Shown(string? name, string? id) =>
        string.IsNullOrWhiteSpace(name) ? id ?? "" : string.IsNullOrWhiteSpace(id) ? name : $"{name} ({id})";

    private static string Deployment(BaselineDeploymentRow d) =>
        $"the deployment of baseline {Shown(d.BaselineName, d.BaselineId)} to collection {Shown(d.CollectionName, d.CollectionId)}";

    private static string Mode(bool remediate) => remediate ? "remediate" : "detect only";
}
