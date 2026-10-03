using System.Diagnostics;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The optional definition fields added for request headers, post-execution
/// behavior, payload version checks, checksum files, MSI argument expansion
/// and ZIP payloads. Each is inert when absent: a definition that declares
/// none of them stages what it staged before.
/// </summary>
public sealed class DefinitionEngineFieldTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-fields-" + Guid.NewGuid().ToString("N"));

    public DefinitionEngineFieldTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static StagingService Service() =>
        new(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(),
            new WrapperGeneratorService(), new ManifestService());

    // ── Request headers ─────────────────────────────────────────

    [Fact]
    public void NoHeadersAddNoCurlArguments()
    {
        Assert.Equal("", CurlHeaders.Arguments(null));
        Assert.Equal("", CurlHeaders.Arguments([]));
    }

    [Fact]
    public void HeadersBecomeQuotedCurlArgumentsWithCompression()
    {
        Assert.Equal(" --compressed -H \"User-Agent: Mozilla/5.0\" -H \"Referer: https://www.example.com/\"",
            CurlHeaders.Arguments(["User-Agent: Mozilla/5.0", " Referer: https://www.example.com/ "]));
    }

    [Theory]
    [InlineData("no colon")]
    [InlineData(": empty name")]
    [InlineData("X-Test: a\"b")]
    [InlineData("X-Test: a\r\nX-Other: b")]
    public void AMalformedHeaderIsRefused(string header)
    {
        Assert.Throws<InvalidOperationException>(() => CurlHeaders.Arguments([header]));
    }

    [Fact]
    public async Task TheDownloadSendsTheDefinitionHeaders()
    {
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["payload.bin"] = [1, 2, 3] });

        await new DownloadService().DownloadAsync(server.Url("payload.bin"), Path.Combine(_dir, "payload.bin"), retryCount: 0,
            headers: ["User-Agent: Mozilla/5.0", "Referer: https://www.mulesoft.com/lp/dl/anypoint-mule-studio"]);

        var request = Assert.Single(server.Requests);
        Assert.Contains("User-Agent: Mozilla/5.0\r\n", request);
        Assert.Contains("Referer: https://www.mulesoft.com/lp/dl/anypoint-mule-studio\r\n", request);
    }

    [Fact]
    public async Task TheVersionRequestSendsTheDefinitionHeadersInPlaceOfTheDefaultUserAgent()
    {
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["notes"] = "<span data-testid=\"release-version\">8.3.26701</span>"u8.ToArray() });
        var config = new VersionScraperConfig
        {
            Type = "HttpRegex",
            Url = server.Url("notes"),
            Pattern = DefinitionFixture.Load("jabradirect.json").VersionScraper.Pattern,
            RequestHeaders = ["User-Agent: Mozilla/5.0"]
        };

        Assert.Equal("8.3.26701", await new HttpRegexScraper().GetLatestVersionAsync(config));
        var request = Assert.Single(server.Requests);
        Assert.Contains("User-Agent: Mozilla/5.0\r\n", request);
        Assert.DoesNotContain("Chrome", request);
    }

    // ── Post-execution behavior ─────────────────────────────────

    [Theory]
    [InlineData("forcereboot", "ForceReboot")]
    [InlineData("BasedOnExitCode", "BasedOnExitCode")]
    [InlineData(" NoAction ", "NoAction")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void PostExecutionBehaviorTakesTheCmdletSpelling(string? value, string? expected)
    {
        Assert.Equal(expected, StagingService.NormalizePostExecutionBehavior(value));
    }

    [Fact]
    public void AnUnknownPostExecutionBehaviorIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => StagingService.NormalizePostExecutionBehavior("Restart"));
    }

    [Fact]
    public void AManifestWithoutPostExecutionBehaviorWritesNoSuchField()
    {
        var path = Path.Combine(_dir, "stage-manifest.json");
        new ManifestService().Write(path, new StageManifest { AppName = "Widget" });

        Assert.DoesNotContain("PostExecutionBehavior", File.ReadAllText(path));
    }

    [Fact]
    public void ForceRebootReachesTheDeploymentType()
    {
        var manifest = new StageManifest
        {
            AppName = "VC++",
            InstallerType = "EXE",
            PostExecutionBehavior = "ForceReboot",
            Detection = new StageDetection { Type = "File", FilePath = @"%SystemRoot%\System32", FileName = "vcruntime140.dll", ExpectedValue = "14.51.36247.0" }
        };

        var withBehavior = MecmTarget.BuildPackageScript(manifest, "VC++", @"\\s\c", "MCM", "", 15, 30);
        manifest.PostExecutionBehavior = null;
        var without = MecmTarget.BuildPackageScript(manifest, "VC++", @"\\s\c", "MCM", "", 15, 30);

        Assert.Contains(" -RebootBehavior ForceReboot", withBehavior);
        Assert.DoesNotContain("RebootBehavior", without);
        Assert.Equal(without, withBehavior.Replace(" -RebootBehavior ForceReboot", ""));
    }

    // ── Grouped detection ───────────────────────────────────────

    [Fact]
    public void AOneClauseSecondGroupTakesTheOrConnectorAlone()
    {
        var det = new StageDetection
        {
            Type = "Compound",
            GroupSizes = [2, 1],
            Clauses =
            [
                new() { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\A" },
                new() { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\B" },
                new() { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\C" }
            ]
        };

        var args = MecmTarget.BuildDetectionClauseConnectorArgs(det);

        Assert.DoesNotContain("-GroupDetectionClauses", args);
        Assert.Equal(" -DetectionClauseConnector @(@{LogicalName=$clause2.Setting.LogicalName;Connector='OR'})", args);
    }

    // ── Payload version ─────────────────────────────────────────

    [Fact]
    public void NoExpectedPayloadVersionChecksNothing()
    {
        var path = Path.Combine(_dir, "plain.exe");
        File.WriteAllText(path, "x");
        StagingService.VerifyPayloadVersion(new AppDefinition(), path, null, false, "1.0");
    }

    [Fact]
    public void AnExePayloadMustReportTheScrapedVersion()
    {
        var exe = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var fileVersion = FileVersionInfo.GetVersionInfo(exe).FileVersion!.Trim();
        var def = new AppDefinition { ExpectedPayloadVersion = "{version}" };

        StagingService.VerifyPayloadVersion(def, exe, null, false, fileVersion);
        var ex = Assert.Throws<InvalidOperationException>(() => StagingService.VerifyPayloadVersion(def, exe, null, false, "8.3.26701"));
        Assert.Contains("does not match the expected version '8.3.26701'", ex.Message);
    }

    [Fact]
    public void AnMsiPayloadComparesItsProductVersionWithTheVersionParts()
    {
        var def = new AppDefinition { ExpectedPayloadVersion = "{version_parts:3}" };

        StagingService.VerifyPayloadVersion(def, "slido.msi", "1.23.0", true, "1.23.0.8116");
        Assert.Throws<InvalidOperationException>(() => StagingService.VerifyPayloadVersion(def, "slido.msi", "1.22.0", true, "1.23.0.8116"));
    }

    // ── MSI argument expansion ──────────────────────────────────

    [Fact]
    public void WithoutExpansionTheMsiWrapperPassesTheArgumentsAsOneLiteral()
    {
        new WrapperGeneratorService().GenerateMsiWrappers(_dir, "app.msi", null, "/qn /norestart ALLUSERS=1");

        Assert.Equal(
            "$msiPath = Join-Path $PSScriptRoot 'app.msi'\n" +
            "$proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/i', \"`\"$msiPath`\"\", '/qn /norestart ALLUSERS=1') -Wait -PassThru -NoNewWindow\n" +
            "if (-not $proc) { exit 1 }\n" +
            "$proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline\n" +
            "exit $proc.ExitCode",
            File.ReadAllText(Path.Combine(_dir, "install.ps1")).Replace("\r\n", "\n"));
    }

    // ── A definition with none of the new fields ────────────────

    [Fact]
    public async Task APlainDefinitionStagesTheSameFilesAndManifestFieldsAsBefore()
    {
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["widget-1.2.3.exe"] = [(byte)'M', (byte)'Z', .. new byte[64]] });
        var def = new AppDefinition
        {
            Id = "plainwidget",
            Vendor = "Contoso",
            Application = "Widget (x64)",
            InstallerType = "EXE",
            InstallerFramework = "NSIS",
            InstallArgs = "/S",
            UninstallArgs = "/S",
            VersionScraper = new VersionScraperConfig { Type = "HttpRegex", Url = server.Url("page"), Pattern = "(x)" },
            DownloadUrl = server.Url("widget-{version}.exe"),
            Detection = new DetectionConfig
            {
                Type = "RegistryKeyValue",
                RegistryKeyTemplate = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Widget",
                PropertyType = "Version",
                Operator = "GreaterEquals"
            }
        };

        var result = await Service().StageAsync(def, Path.Combine(_dir, "root"), knownVersion: "1.2.3");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(["install.bat", "install.ps1", "stage-manifest.json", "uninstall.bat", "uninstall.ps1", "widget-1.2.3.exe"],
            Directory.GetFiles(result.ContentPath).Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            "$exePath = Join-Path $PSScriptRoot 'widget-1.2.3.exe'\r\n" +
            "$proc = Start-Process -FilePath $exePath -ArgumentList '/S' -Wait -PassThru -NoNewWindow\r\n" +
            "if (-not $proc) { exit 1 }\r\n" +
            "$proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline\r\n" +
            "exit $proc.ExitCode",
            File.ReadAllText(Path.Combine(result.ContentPath, "install.ps1")));

        using var doc = JsonDocument.Parse(File.ReadAllText(result.ManifestPath));
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(
            ["SchemaVersion", "AppName", "DisplayName", "Publisher", "SoftwareVersion", "InstallerFile", "InstallerType", "Architecture",
             "InstallContext", "InstallMode", "InstallArgs", "UninstallArgs", "ProductCode", "RunningProcess", "Detection", "StagedAt",
             "FileHashes", "Icon", "UninstallCommand", "DetectedEngine", "AdHocSource", "DeploymentTypes", "CommandOverrides", "MsixPackage",
             "BuildId", "ApplicationId", "ProfileId", "ProfileRevision", "Timing", "Execution", "DetectionSource", "CustomAssets",
             "RunOverrides", "ScriptSigning", "PlanDigest"],
            names);
        Assert.Equal("1.2.3", doc.RootElement.GetProperty("Detection").GetProperty("ExpectedValue").GetString());
        Assert.Equal("/S", doc.RootElement.GetProperty("InstallArgs").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("Icon").ValueKind);
    }
}
