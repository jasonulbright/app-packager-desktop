using System.Text.Json.Nodes;
using ApplicationPackagerBuild;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Vulnerability;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerEngine.Tests.Vulnerability;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests.CommandLine;

public sealed class CveReportArgumentsTests
{
    private static BuildOptions Parsed(params string[] args)
    {
        var parsed = BuildArguments.Parse(args);
        Assert.True(parsed.Ok, parsed.Error);
        return parsed.Options!;
    }

    private static string Refused(params string[] args)
    {
        var parsed = BuildArguments.Parse(args);
        Assert.False(parsed.Ok);
        return parsed.Error!;
    }

    [Fact]
    public void TheReportRunNeedsNoApplicationAndNoPhase()
    {
        var options = Parsed("--cve-report");
        var cve = Assert.IsType<CveReportOptions>(options.CveReport);
        Assert.Empty(cve.Apps);
        Assert.False(cve.SkipLatest || cve.SkipMecm || cve.SkipCve);
        Assert.Null(cve.ReportFolder);
        Assert.Null(cve.ReportFormats);
        Assert.Null(cve.WebhookUrl);
        Assert.Null(cve.DropFolder);
        Assert.Null(cve.NotifyAlways);
        Assert.Null(cve.KeepReportDays);
        Assert.Null(cve.ConfigPath);
        Assert.Null(Parsed("--application", "putty", "--stage").CveReport);
    }

    [Fact]
    public void ReadsEveryReportOption()
    {
        var options = Parsed("--cve-report", "--apps", "putty, package-7zip.ps1,,chrome-x64", "--skip-latest", "--skip-mecm", "--skip-cve",
            "--report-folder", @"C:\reports", "--report-formats", "html,CSV,xlsx,html", "--webhook-url", "https://hooks.example.test/x",
            "--drop-folder", @"\\server\share\cve", "--notify-always", "--keep-report-days", "30", "--config", @"C:\cfg\monitor-config.json",
            "--site-code", "MCM", "--provider-machine-name", "cm01", "--packagers-root", @"C:\p", "--data-root", @"C:\d", "--log-folder", @"C:\l");
        var cve = options.CveReport!;
        Assert.Equal(["putty", "package-7zip.ps1", "chrome-x64"], cve.Apps);
        Assert.True(cve.SkipLatest && cve.SkipMecm && cve.SkipCve);
        Assert.Equal(@"C:\reports", cve.ReportFolder);
        Assert.Equal(["Html", "Csv", "Xlsx"], cve.ReportFormats);
        Assert.Equal("https://hooks.example.test/x", cve.WebhookUrl);
        Assert.Equal(@"\\server\share\cve", cve.DropFolder);
        Assert.True(cve.NotifyAlways);
        Assert.Equal(30, cve.KeepReportDays);
        Assert.Equal(@"C:\cfg\monitor-config.json", cve.ConfigPath);
        Assert.Equal(("MCM", "cm01", @"C:\p", @"C:\d", @"C:\l"), (options.SiteCode, options.ProviderMachineName, options.PackagersRoot, options.DataRoot, options.LogFolder));
        Assert.False(Parsed("--cve-report", "--notify-always=false").CveReport!.NotifyAlways);
    }

    [Theory]
    [InlineData("--report-formats", "Html,Pdf", "Unknown report format 'Pdf'")]
    [InlineData("--report-formats", ",", "names no format")]
    [InlineData("--keep-report-days", "-1", "--keep-report-days")]
    [InlineData("--keep-report-days", "x", "--keep-report-days")]
    [InlineData("--webhook-url", "http://hooks.example.test/x", "https URL")]
    [InlineData("--webhook-url", "", "empty value")]
    [InlineData("--report-folder", " ", "empty value")]
    [InlineData("--drop-folder", "", "empty value")]
    [InlineData("--config", "", "empty value")]
    [InlineData("--apps", "", "empty value")]
    public void RefusesAReportOptionItCannotUse(string name, string value, string expected) =>
        Assert.Contains(expected, Refused("--cve-report", $"{name}={value}"));

    [Fact]
    public void KeepsBuildOptionsAndReportOptionsApart()
    {
        Assert.Contains("applies to --cve-report only", Refused("--application", "putty", "--stage", "--skip-cve"));
        Assert.Contains("'--apps' applies to --cve-report only", Refused("--application", "putty", "--stage", "--apps", "putty"));
        Assert.Contains("'--application' does not apply to --cve-report", Refused("--cve-report", "--application", "putty"));
        Assert.Contains("'--stage' does not apply to --cve-report", Refused("--cve-report", "--stage"));
        Assert.Contains("'--target' does not apply to --cve-report", Refused("--cve-report", "--target", "MECM"));
        Assert.Contains("--application is required", Refused("--cve-report=false", "--stage"));
        Assert.Contains("--cve-report", BuildArguments.Usage);
        foreach (var option in CveReportArguments.OnlyOptions) Assert.Contains("--" + option, BuildArguments.Usage);
    }

    [Fact]
    public void AWebhookTokenIsNotEchoedWhenTheUrlIsRefused() =>
        Assert.DoesNotContain("secret-token", Refused("--cve-report", "--webhook-url", "http://hooks.example.test/secret-token"));
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class CveReportRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-cve-cli-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved = [];
    private readonly RecordingOutput _output = new();
    private readonly string _reports;

    public CveReportRunnerTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, WorkbenchPaths.DataRootVariable, SettingsService.LauncherSiteCodeVariable, SettingsService.LauncherProviderVariable, NvdApiKeyStore.EnvironmentVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
        Environment.SetEnvironmentVariable(WorkbenchPaths.DataRootVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, null);
        Environment.SetEnvironmentVariable(NvdApiKeyStore.EnvironmentVariable, null);
        Directory.CreateDirectory(AppDataPaths.Root);
        WriteSettings(siteCode: "");
        _reports = Path.Combine(_root, "out");
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void WriteSettings(string siteCode, string? protectedKey = null) =>
        File.WriteAllText(SettingsService.SettingsPath, new JsonObject
        {
            ["SiteCode"] = siteCode,
            ["SiteCodeSaved"] = true,
            ["ProviderMachine"] = "",
            ["FileShareRoot"] = "",
            ["DownloadRoot"] = Path.Combine(_root, "dl"),
            ["FirstRunCompleted"] = true,
            ["Vulnerability"] = new JsonObject { ["NvdApiKeyProtected"] = protectedKey ?? "", ["CacheTtlMinutes"] = 45 }
        }.ToJsonString());

    private sealed class RecordingOutput : IBuildOutput
    {
        public List<string> Lines { get; } = [];
        public List<string> Errors { get; } = [];
        void IBuildOutput.Info(string line) => Lines.Add(line);
        void IBuildOutput.Error(string line) => Errors.Add(line);
    }

    private sealed class Sources : ICveMonitorSources
    {
        public List<string> LatestCalls { get; } = [];
        public List<string> ConfigMgrCalls { get; } = [];

        public Task<string> GetLatestVersionAsync(CveMonitorApp app, CancellationToken ct)
        {
            LatestCalls.Add(app.Uid);
            return Task.FromResult("0.83");
        }

        public Task<CveMonitorConfigMgrVersion> GetConfigMgrVersionAsync(CveMonitorApp app, CancellationToken ct)
        {
            ConfigMgrCalls.Add(app.CMName);
            return Task.FromResult(new CveMonitorConfigMgrVersion(true, "0.78"));
        }
    }

    private async Task<(int ExitCode, ApplicationInventoryService Inventory)> RunAsync(ICveMonitorSources? sources, IVulnerabilityTransport transport, params string[] args)
    {
        var parsed = BuildArguments.Parse(["--cve-report", .. args]);
        Assert.True(parsed.Ok, parsed.Error);
        await using var services = new ServiceCollection()
            .AddPackagerServices()
            .AddSingleton<IBuildOutput>(_output)
            .AddSingleton<CveReportRunner>()
            .BuildServiceProvider();
        var lookup = new VulnerabilityLookupService(transport, _output.Lines.Add, VulnerabilityFixtures.Pacer());
        var exit = await services.GetRequiredService<CveReportRunner>().RunAsync(parsed.Options!, CancellationToken.None, sources, lookup, transport);
        return (exit, services.GetRequiredService<ApplicationInventoryService>());
    }

    [Fact]
    public async Task WritesTheVersionReportForEveryApplicationWithoutAnyQuery()
    {
        var transport = new FakeVulnerabilityTransport { Handler = url => throw new InvalidOperationException("no request expected: " + url) };
        var sources = new Sources();
        var (exit, _) = await RunAsync(sources, transport, "--skip-latest", "--skip-mecm", "--skip-cve", "--report-folder", _reports, "--report-formats", "Csv,Json");
        Assert.Equal(0, exit);
        Assert.Empty(_output.Errors);
        Assert.Empty(sources.LatestCalls);
        Assert.Empty(sources.ConfigMgrCalls);
        Assert.Empty(transport.Calls);
        Assert.Empty(transport.Posts);

        var csv = Assert.Single(Directory.GetFiles(_reports, "cve-report-*-applications.csv"));
        var rows = VulnerabilityFixtures.ReadCsv(csv);
        Assert.True(rows.Count >= 300, rows.Count.ToString());
        Assert.Equal(VulnerabilityFixtures.PuttyCpe, rows.Single(r => r["Application"] == "PuTTY (x64)")["CPE"]);
        Assert.Single(Directory.GetFiles(_reports, "*.json"));
        Assert.Empty(Directory.GetFiles(_reports, "*.html"));

        // The default configuration is written beside the settings on the first run.
        var config = JsonNode.Parse(File.ReadAllText(VulnerabilityPaths.MonitorConfigPath))!;
        Assert.Equal(2, config["SchemaVersion"]!.GetValue<int>());
        Assert.Equal(90, config["Report"]!["KeepReportDays"]!.GetValue<int>());
        Assert.False(config["Notifications"]!["Enabled"]!.GetValue<bool>());
        Assert.True(config["Notifications"]!["OnlyOnStale"]!.GetValue<bool>());
        Assert.Null(config["NVD"]);
        Assert.Contains(_output.Lines, line => line.StartsWith("Applications  : "));
        Assert.Contains("ExitCode      : 0", _output.Lines);
    }

    [Fact]
    public async Task RunsOneApplicationThroughTheLookupAndTheNotificationWithTheConfiguredValues()
    {
        const string key = "0f8e7d6c-5b4a-4321-9abc-def012345678";
        WriteSettings("ZZ1", NvdApiKeyStore.Protect(key));
        var drop = Directory.CreateDirectory(Path.Combine(_root, "drop")).FullName;
        var configPath = Path.Combine(_root, "cfg", "monitor.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, new JsonObject
        {
            ["SchemaVersion"] = 2,
            ["Report"] = new JsonObject { ["OutputFolder"] = _reports, ["Formats"] = new JsonArray("Html", "Csv", "bogus"), ["KeepReportDays"] = 7 },
            ["Notifications"] = new JsonObject { ["Enabled"] = true, ["DropFolder"] = drop, ["WebhookUrl"] = "https://hooks.example.test/secret-token", ["OnlyOnStale"] = true }
        }.ToJsonString());
        Directory.CreateDirectory(_reports);
        var old = Path.Combine(_reports, "cve-report-20200101-000000.html");
        File.WriteAllText(old, "x");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-30));

        var transport = new FakeVulnerabilityTransport();
        var kevFolder = Directory.CreateDirectory(VulnerabilityPaths.DataFolder).FullName;
        File.WriteAllText(Path.Combine(kevFolder, "kev-catalog.json"), VulnerabilityFixtures.Read("kev-three.json"));
        var sources = new Sources();
        var (exit, inventory) = await RunAsync(sources, transport, "--apps", "putty", "--config", configPath);

        Assert.Equal(0, exit);
        Assert.Equal(["656d16fead444d4e"], sources.LatestCalls);
        Assert.Equal(["PuTTY"], sources.ConfigMgrCalls);
        Assert.Equal(key, transport.NvdHeaders!["apiKey"]);
        Assert.Equal(1, transport.Count(FakeVulnerabilityTransport.IsNvd));
        Assert.All(transport.Calls, url => Assert.True(FakeVulnerabilityTransport.IsNvd(url) || FakeVulnerabilityTransport.IsEpss(url), url));

        // Formats and folders come from the configuration; an unknown format name is dropped.
        Assert.Equal(3, Directory.GetFiles(_reports, "cve-report-*").Length);
        Assert.Equal(3, Directory.GetFiles(drop).Length);
        Assert.False(File.Exists(old));
        var row = VulnerabilityFixtures.ReadCsv(Directory.GetFiles(_reports, "*-applications.csv").Single()).Single();
        Assert.Equal(("PuTTY (x64)", "0.78", "0.83", "2", "3", "Update available (2 CVEs)"),
            (row["Application"], row["ConfigMgrVersion"], row["LatestVersion"], row["CVEs"], row["StillInLatest"], row["Status"]));

        var (url, body) = Assert.Single(transport.Posts);
        Assert.Equal("https://hooks.example.test/secret-token", url);
        Assert.Contains("1 application(s), 1 with an update, 2 CVE(s) fixed by updating", body);

        // The window reads the same inventory and cache on its next launch.
        var site = InventoryEnvironmentIdentity.ConfigMgr("ZZ1", "");
        Assert.Equal("0.83", inventory.GetVendorState("656d16fead444d4e")!.LatestVersion);
        Assert.Equal("0.78", inventory.GetEnvironmentState("656d16fead444d4e", site)!.LatestObservedVersion);
        Assert.Equal(2, inventory.GetVulnerabilitySummary("656d16fead444d4e", site)!.Count);
        Assert.NotNull(VulnerabilityCache.Find(VulnerabilityFixtures.PuttyCpe, "0.78", "0.83"));
        Assert.StartsWith(AppDataPaths.Root, VulnerabilityPaths.CachePath);

        var everything = string.Join("\n", _output.Lines.Concat(_output.Errors));
        Assert.DoesNotContain(key, everything);
        Assert.DoesNotContain("secret-token", everything);
    }

    [Fact]
    public async Task AnArgumentReplacesTheConfiguredValueAndAFailedQueryGivesExitCodeOne()
    {
        WriteSettings("ZZ1");
        var configPath = Path.Combine(_root, "monitor.json");
        File.WriteAllText(configPath, """
            { "Report": { "OutputFolder": "C:\\never-used", "Formats": ["Html"], "KeepReportDays": 0 },
              "Notifications": { "Enabled": true, "WebhookUrl": "https://hooks.example.test/from-config", "OnlyOnStale": false } }
            """);
        var transport = new FakeVulnerabilityTransport { Handler = _ => throw FakeVulnerabilityTransport.Failure(503) };
        var (exit, _) = await RunAsync(new Sources(), transport, "--apps", "putty", "--config", configPath, "--report-folder", _reports,
            "--report-formats", "Markdown", "--webhook-url", "https://hooks.example.test/from-argument");
        Assert.Equal(1, exit);
        Assert.Equal(["cve-report"], Directory.GetFiles(_reports).Select(f => Path.GetFileName(f)[..10]).Distinct());
        Assert.EndsWith(".md", Assert.Single(Directory.GetFiles(_reports)));
        Assert.Equal("https://hooks.example.test/from-argument", Assert.Single(transport.Posts).Url);
        Assert.Contains("ExitCode      : 1", _output.Lines);
        Assert.False(Directory.Exists(@"C:\never-used"));
    }

    [Fact]
    public async Task AConfigurationThatIsNotJsonStopsTheRun()
    {
        var configPath = Path.Combine(_root, "monitor.json");
        File.WriteAllText(configPath, "{ not json");
        var (exit, _) = await RunAsync(new Sources(), new FakeVulnerabilityTransport(), "--config", configPath, "--report-folder", _reports);
        Assert.Equal(1, exit);
        Assert.Contains("monitor configuration is not valid JSON", Assert.Single(_output.Errors));
        Assert.False(Directory.Exists(_reports));
    }

    [Fact]
    public void TheConfigurationResolvesNotificationsOnlyWhenEnabled()
    {
        var settings = new AppSettings { SiteCode = "MCM", ProviderMachine = "cm01" };
        var config = new CveMonitorConfig();
        config.Notifications.DropFolder = @"C:\drop";
        config.Notifications.WebhookUrl = "https://hooks.example.test/x";
        var resolved = config.Resolve(new CveReportOptions(), settings, @"C:\default\Reports");
        Assert.Equal(("", "", false, 90, @"C:\default\Reports", "MCM", "cm01", 360),
            (resolved.WebhookUrl, resolved.DropFolder, resolved.NotifyAlways, resolved.KeepReportDays, resolved.ReportFolder, resolved.SiteCode, resolved.ProviderMachineName, resolved.CacheTtlMinutes));
        Assert.Equal(VulnerabilityReportFormats.All, resolved.Formats);

        config.Notifications.Enabled = true;
        config.Notifications.OnlyOnStale = false;
        resolved = config.Resolve(new CveReportOptions { KeepReportDays = 0, NotifyAlways = false }, settings, @"C:\default\Reports");
        Assert.Equal(("https://hooks.example.test/x", @"C:\drop", false, 0), (resolved.WebhookUrl, resolved.DropFolder, resolved.NotifyAlways, resolved.KeepReportDays));
        Assert.True(config.Resolve(new CveReportOptions(), settings, @"C:\default\Reports").NotifyAlways);

        // ConfigMgr not in use: the run queries no site.
        settings.Systems = new ApplicationPackagerEngine.Models.SystemsInUse { ConfigMgr = false };
        Assert.Equal("", config.Resolve(new CveReportOptions(), settings, @"C:\default\Reports").SiteCode);
    }
}
