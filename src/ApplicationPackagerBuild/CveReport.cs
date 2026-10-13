using System.Globalization;
using System.Text;
using System.Text.Json;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Vulnerability;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerBuild;

/// <summary>The CVE report run as the arguments ask for it. A null value takes the monitor configuration.</summary>
public sealed record CveReportOptions
{
    public IReadOnlyList<string> Apps { get; init; } = [];
    public bool SkipLatest { get; init; }
    public bool SkipMecm { get; init; }
    public bool SkipCve { get; init; }
    public string? ReportFolder { get; init; }
    public IReadOnlyList<string>? ReportFormats { get; init; }
    public string? WebhookUrl { get; init; }
    public string? DropFolder { get; init; }
    public bool? NotifyAlways { get; init; }
    public int? KeepReportDays { get; init; }
    public string? ConfigPath { get; init; }
}

public static class CveReportArguments
{
    /// <summary>Options that mean nothing to a build.</summary>
    public static readonly string[] OnlyOptions =
    [
        "apps", "skip-latest", "skip-mecm", "skip-cve", "report-folder", "report-formats", "webhook-url", "drop-folder",
        "notify-always", "keep-report-days", "config"
    ];

    private static readonly string[] BuildOnlyOptions =
    [
        "application", "profile", "version", "target", "stage", "package", "download-root", "estimated-minutes", "maximum-minutes",
        "file-server-path", "comment", "wsus-server", "wsus-port", "wsus-use-ssl", "wsus-classification", "wsus-approval-group",
        "wsus-decline-superseded", "on-existing", "local-source-folder"
    ];

    internal static ParsedArguments Parse(Dictionary<string, string> values)
    {
        if (BuildOnlyOptions.FirstOrDefault(values.ContainsKey) is { } buildOnly)
            return BuildArguments.Fail($"Option '--{buildOnly}' does not apply to --cve-report.");

        List<string>? formats = null;
        if (values.TryGetValue("report-formats", out var formatText))
        {
            formats = [];
            foreach (var name in formatText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (VulnerabilityReportFormats.Normalize(name) is not { } known)
                    return BuildArguments.Fail($"Unknown report format '{name}'; expected {string.Join(", ", VulnerabilityReportFormats.All)}.");
                if (!formats.Contains(known)) formats.Add(known);
            }
            if (formats.Count == 0) return BuildArguments.Fail("Option '--report-formats' names no format.");
        }

        int? keepDays = null;
        if (values.TryGetValue("keep-report-days", out var keepText))
        {
            if (!int.TryParse(keepText, NumberStyles.None, CultureInfo.InvariantCulture, out var days))
                return BuildArguments.Fail("Option '--keep-report-days' takes a whole number of days, 0 or more.");
            keepDays = days;
        }

        var webhook = BuildArguments.Text(values, "webhook-url");
        if (webhook is not null && !VulnerabilityText.IsHttps(webhook))
            return BuildArguments.Fail("Option '--webhook-url' takes an https URL.");

        return new ParsedArguments(new BuildOptions
        {
            PackagersRoot = BuildArguments.Text(values, "packagers-root"),
            LogFolder = BuildArguments.Text(values, "log-folder"),
            SiteCode = BuildArguments.Text(values, "site-code"),
            ProviderMachineName = BuildArguments.Text(values, "provider-machine-name"),
            DataRoot = BuildArguments.Text(values, "data-root"),
            CveReport = new CveReportOptions
            {
                Apps = values.TryGetValue("apps", out var apps)
                    ? apps.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    : [],
                SkipLatest = BuildArguments.Flag(values, "skip-latest") == true,
                SkipMecm = BuildArguments.Flag(values, "skip-mecm") == true,
                SkipCve = BuildArguments.Flag(values, "skip-cve") == true,
                ReportFolder = BuildArguments.Text(values, "report-folder"),
                ReportFormats = formats,
                WebhookUrl = webhook,
                DropFolder = BuildArguments.Text(values, "drop-folder"),
                NotifyAlways = BuildArguments.Flag(values, "notify-always"),
                KeepReportDays = keepDays,
                ConfigPath = BuildArguments.Text(values, "config")
            }
        }, null);
    }
}

/// <summary>monitor-config.json: the report and notification choices of a scheduled CVE report run.</summary>
public sealed class CveMonitorConfig
{
    public int SchemaVersion { get; set; } = 2;
    public ReportSection Report { get; set; } = new();
    public NotificationSection Notifications { get; set; } = new();

    public sealed class ReportSection
    {
        public string OutputFolder { get; set; } = "";
        public string[] Formats { get; set; } = VulnerabilityReportFormats.All;
        public int KeepReportDays { get; set; } = 90;
    }

    public sealed class NotificationSection
    {
        public bool Enabled { get; set; }
        public string DropFolder { get; set; } = "";
        public string WebhookUrl { get; set; } = "";
        public bool OnlyOnStale { get; set; } = true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Reads the file; a missing file is written with default values first.</summary>
    /// <exception cref="InvalidDataException">The file is not a monitor configuration.</exception>
    public static CveMonitorConfig Load(string path, Action<string>? log = null)
    {
        if (!File.Exists(path))
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.WriteAllText(path, JsonSerializer.Serialize(new CveMonitorConfig(), JsonOptions), new UTF8Encoding(false));
            log?.Invoke($"Wrote the default monitor configuration: {path}");
        }
        try
        {
            var config = JsonSerializer.Deserialize<CveMonitorConfig>(File.ReadAllText(path), JsonOptions) ?? new CveMonitorConfig();
            config.Report ??= new ReportSection();
            config.Report.Formats ??= VulnerabilityReportFormats.All;
            config.Notifications ??= new NotificationSection();
            return config;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The monitor configuration is not valid JSON: {path}: {ex.Message}", ex);
        }
    }

    /// <summary>The run options: an argument replaces the configured value.</summary>
    public CveMonitorOptions Resolve(CveReportOptions arguments, AppSettings settings, string defaultReportFolder)
    {
        var formats = arguments.ReportFormats
                      ?? Report.Formats.Select(VulnerabilityReportFormats.Normalize).OfType<string>().Distinct().ToList();
        if (formats.Count == 0) formats = VulnerabilityReportFormats.All;
        var notify = Notifications.Enabled;
        var folder = arguments.ReportFolder ?? (string.IsNullOrWhiteSpace(Report.OutputFolder) ? defaultReportFolder : Report.OutputFolder.Trim());
        return new CveMonitorOptions
        {
            Apps = arguments.Apps,
            SiteCode = settings.Systems is { ConfigMgr: false } ? "" : settings.SiteCode ?? "",
            ProviderMachineName = settings.ProviderMachine ?? "",
            SkipLatest = arguments.SkipLatest,
            SkipMecm = arguments.SkipMecm,
            SkipCve = arguments.SkipCve,
            NvdApiKey = NvdApiKeyStore.Resolve(settings),
            CacheTtlMinutes = NvdApiKeyStore.CacheTtlMinutes(settings),
            ReportFolder = Path.GetFullPath(folder),
            Formats = formats,
            WebhookUrl = arguments.WebhookUrl ?? (notify ? Notifications.WebhookUrl?.Trim() ?? "" : ""),
            DropFolder = arguments.DropFolder ?? (notify ? Notifications.DropFolder?.Trim() ?? "" : ""),
            NotifyAlways = arguments.NotifyAlways ?? (notify && !Notifications.OnlyOnStale),
            KeepReportDays = arguments.KeepReportDays ?? Math.Max(0, Report.KeepReportDays)
        };
    }
}

/// <summary>
/// The CVE report run of the command line: the applications the window lists, their
/// vendor and ConfigMgr queries through the services the window uses, and the monitor.
/// </summary>
public sealed class CveReportRunner(
    IRunSettingsReader<AppSettings> settingsService,
    PackagerDiscoveryService discoveryService,
    PackagerExecutionService executionService,
    MecmQueryService mecmQueryService,
    ApplicationInventoryService inventory,
    IBuildOutput output)
{
    private sealed class Sources(PackagerExecutionService execution, MecmQueryService mecm, IReadOnlyDictionary<string, PackagerInfo> rows)
        : ICveMonitorSources
    {
        public async Task<string> GetLatestVersionAsync(CveMonitorApp app, CancellationToken ct) =>
            await execution.GetLatestVersionAsync(rows[app.Uid], ct)
            ?? throw new InvalidOperationException("Could not determine the latest version.");

        public async Task<CveMonitorConfigMgrVersion> GetConfigMgrVersionAsync(CveMonitorApp app, CancellationToken ct)
        {
            var result = await mecm.QueryVersionAsync(app.CMName, ct);
            if (result.Failed) throw new InvalidOperationException(result.FailureReason ?? "The ConfigMgr query failed.");
            return new CveMonitorConfigMgrVersion(result.Found, result.SoftwareVersion ?? "");
        }
    }

    public static CveMonitorApp ToMonitorApp(PackagerInfo row) =>
        new(row.Uid, row.Application, row.Vendor, row.Script, row.CMName, row.Cpe, row.AdvisoryUrl);

    /// <summary>Runs the report. The sources, the lookup and the transport are the live ones unless a test supplies its own.</summary>
    public async Task<int> RunAsync(BuildOptions options, CancellationToken ct, ICveMonitorSources? sources = null,
        IVulnerabilityLookup? lookup = null, IVulnerabilityTransport? transport = null)
    {
        var arguments = options.CveReport ?? throw new ArgumentException("The options carry no CVE report run.", nameof(options));
        settingsService.ApplyRunOverrides(s =>
        {
            if (options.SiteCode is { } siteCode) s.SiteCode = siteCode;
            if (options.ProviderMachineName is { } provider) s.ProviderMachine = provider;
            if (options.PackagersRoot is { } packagers) s.PackagersRoot = packagers;
        });
        settingsService.ReadFileOncePerRun();
        try
        {
            var settings = settingsService.Load();
            if (settingsService.TakeLoadWarning() is { } warning) output.Error(warning);

            var config = CveMonitorConfig.Load(arguments.ConfigPath ?? VulnerabilityPaths.MonitorConfigPath, output.Info);
            var monitorOptions = config.Resolve(arguments, settings, VulnerabilityPaths.ReportFolder);
            if (monitorOptions.WebhookUrl.Length > 0 && !VulnerabilityText.IsHttps(monitorOptions.WebhookUrl))
                output.Info("WARNING: The webhook URL of the monitor configuration is not https; the summary is not posted.");

            var rows = (await discoveryService.DiscoverAsync(ct))
                .Where(r => r.Uid.Length > 0 && r.Application.Length > 0)
                .GroupBy(r => r.Uid, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .ToDictionary(r => r.Uid, StringComparer.OrdinalIgnoreCase);

            transport ??= new HttpVulnerabilityTransport(output.Info);
            lookup ??= new VulnerabilityLookupService(transport, output.Info);
            sources ??= new Sources(executionService, mecmQueryService, rows);
            var summary = await new CveMonitor(inventory, sources, lookup, transport, output.Info)
                .RunAsync(rows.Values.OrderBy(r => r.Application, StringComparer.OrdinalIgnoreCase).Select(ToMonitorApp), monitorOptions, ct);

            output.Info($"Applications  : {summary.Total}");
            output.Info($"Updates       : {summary.Updates}");
            output.Info($"LookedUp      : {summary.LookedUp}");
            output.Info($"CVEs          : {summary.Cves}");
            output.Info($"KEV           : {summary.Kev}");
            output.Info($"Failed        : {summary.Failed}");
            output.Info($"ExitCode      : {summary.ExitCode}");
            return summary.ExitCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            output.Error("Cancelled.");
            return 1;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            output.Error(ex.Message);
            return 1;
        }
    }
}
