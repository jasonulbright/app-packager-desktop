using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Jabra Direct, Rtools, Slido, the ASP.NET Core hosting bundles, the Visual
/// C++ runtimes, Tableau and PyCharm: version sources, payload checks,
/// detection and wrappers as their definitions declare them.
/// </summary>
public sealed class VendorPageDefinitionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-vendor-" + Guid.NewGuid().ToString("N"));

    public VendorPageDefinitionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string? Transform(string raw, string transform) =>
        typeof(ScraperRegistry).GetMethod("ApplyTransform",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [raw, transform]) as string;

    [Fact]
    public void JabraReadsTheFirstReleaseVersionElement()
    {
        var pattern = DefinitionFixture.Load("jabradirect.json").VersionScraper.Pattern;
        var html = "<h3 data-testid=\"version-label\">Release version</h3><span data-testid=\"release-version\">8.2.23201</span>" +
                   "<span data-testid=\"release-version\">8.1.14601</span>";
        Assert.Equal("8.2.23201", HttpRegexScraper.ExtractVersion(html, pattern, false));
        Assert.Equal("6.27.03702", HttpRegexScraper.ExtractVersion("<span data-testid=\"release-version\">\n 6.27.03702 \n</span>", pattern, false));
    }

    [Theory]
    [InlineData("<html>maintenance</html>")]
    [InlineData("<span data-testid=\"release-version\">latest</span>")]
    [InlineData("")]
    public void JabraYieldsNoVersionWithoutTheMarker(string html)
    {
        Assert.Null(HttpRegexScraper.ExtractVersion(html, DefinitionFixture.Load("jabradirect.json").VersionScraper.Pattern, false));
    }

    [Fact]
    public void JabraChecksThePayloadVersionAndDetectsTheInstalledExecutable()
    {
        var def = DefinitionFixture.Load("jabradirect.json");
        var staged = StagingService.BuildDetection(def, null, "8.3.26701");

        Assert.Equal("{version}", def.ExpectedPayloadVersion);
        Assert.Equal("JabraDirectSetup.exe", def.InstallerFileName);
        Assert.Equal(["User-Agent: Mozilla/5.0"], def.VersionScraper.RequestHeaders!);
        // A Burn bundle id changes with each release, so no uninstall key name is pinned.
        Assert.Equal("File", staged.Type);
        Assert.Null(staged.RegistryKeyRelative);
        Assert.Contains("QuietUninstallString", def.UninstallCommand);
        Assert.Equal(["jabra-direct"], def.RunningProcess);
    }

    [Fact]
    public void RtoolsTakesTheHighestLineAndTheX64Installer()
    {
        var hops = DefinitionFixture.Load("rtools.json").VersionScraper.Hops!;
        var index = "<a href=\"rtools44/rtools.html\">4.4</a><a href=\"rtools45/rtools.html\">4.5</a><a href=\"rtools43/rtools.html\">4.3</a>";
        var page = "<a href=\"files/rtools45-aarch64-6768-6492.exe\">arm</a><a href=\"files/rtools45-6768-6492.exe\">x64</a>";

        var lines = System.Text.RegularExpressions.Regex.Matches(index, hops[0].Pattern).Select(m => m.Groups["capture"].Value).ToList();
        Assert.Equal("rtools45", LooseVersion.SelectHighest(lines));
        var file = System.Text.RegularExpressions.Regex.Match(page, hops[1].Pattern);
        Assert.Equal("files/rtools45-6768-6492.exe", file.Groups["url"].Value);
        Assert.Equal("rtools45-6768-6492", file.Groups["version"].Value);
    }

    [Fact]
    public void RtoolsCarriesTheLineInItsKeyFolderAndExpectedValue()
    {
        var def = DefinitionFixture.Load("rtools.json");
        var version = Transform("rtools45-6768-6492", def.VersionScraper.VersionTransform!)!;
        Assert.Equal("4.5.6768.6492", version);

        var staged = StagingService.BuildDetection(def, null, version, version);
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Rtools45_is1", staged.RegistryKeyRelative);
        Assert.Equal("4.5.6768", staged.ExpectedValue);
        Assert.True(staged.Is64Bit);
        Assert.Equal(@"C:\rtools45\unins000.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            StageTokens.ApplyVersionParts(def.UninstallCommand, version));
        Assert.Equal("{version}", def.ExpectedPayloadVersion);
    }

    [Fact]
    public void SlidoReadsTheAdminMsiFromThePackageApi()
    {
        var def = DefinitionFixture.Load("slido.json");
        var json = """{ "version": "1.23.0.8116", "publicUrl": "https://download.slido.com/slido-for-windows-admin64/260916080650_1.23.0.8116/SlidoAdmin_x64_v1.23.0.msi" }""";

        Assert.Equal("1.23.0.8116", StagingService.ExtractFeedHash(json, new VersionScraperConfig { Type = "JsonQuery", PayloadHashQuery = def.VersionScraper.Pattern }, null));
        Assert.EndsWith(".msi", StagingService.ExtractFeedHash(json, new VersionScraperConfig { Type = "JsonQuery", PayloadHashQuery = def.VersionScraper.DownloadUrlQuery }, null));
        Assert.Equal("{version_parts:3}", def.ExpectedPayloadVersion);
        Assert.Equal("/qn /norestart DISABLE_UPDATE_CHECKS=1", def.InstallArgs);
    }

    [Fact]
    public void SlidoDetectsSlidoExeAgainstTheFourPartVersion()
    {
        var staged = StagingService.BuildDetection(DefinitionFixture.Load("slido.json"), "{P}", "1.23.0.8116", "1.23.0");

        Assert.Equal(@"C:\Program Files (x86)\Slido\Slido for Windows", staged.FilePath);
        Assert.Equal("Slido.exe", staged.FileName);
        Assert.Equal("1.23.0.8116", staged.ExpectedValue);
        Assert.True(staged.Is64Bit);
    }

    /// <summary>The expected detection script for one ArpEntry, line for line.</summary>
    private static string ArpEntryScript(string prefix, string suffix, string publisher, string minimum)
    {
        var lines = new List<string>
        {
            $"$minimum = [version]'{minimum}'",
            "foreach ($view in @([Microsoft.Win32.RegistryView]::Registry32)) {",
            "    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)",
            "    $uninstall = $base.OpenSubKey('SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall')",
            "    if (-not $uninstall) { continue }",
            "    foreach ($name in $uninstall.GetSubKeyNames()) {",
            "        $key = $null",
            "        try {",
            "            $key = $uninstall.OpenSubKey($name)",
            "            if (-not $key) { continue }",
            "            $displayName = [string]$key.GetValue('DisplayName')",
            "            $publisher = [string]$key.GetValue('Publisher')",
            "            $displayVersion = [string]$key.GetValue('DisplayVersion')",
            "            $windowsInstaller = $key.GetValue('WindowsInstaller')",
            "        }",
            "        catch { continue }",
            "        finally { if ($key) { $key.Close() } }",
            $"        if (-not $displayName.StartsWith('{prefix}', [StringComparison]::OrdinalIgnoreCase)) {{ continue }}",
            $"        if (-not $displayName.EndsWith('{suffix}', [StringComparison]::OrdinalIgnoreCase)) {{ continue }}",
            $"        if (-not [string]::Equals($publisher, '{publisher}', [StringComparison]::OrdinalIgnoreCase)) {{ continue }}",
            "        $parts = @($displayVersion.Trim().Split('.'))",
            "        if ($parts.Count -gt 4) { continue }",
            "        $numbers = @(foreach ($part in $parts) { $n = 0; if ([int]::TryParse($part, [ref]$n) -and $n -ge 0) { $n } })",
            "        if ($numbers.Count -ne $parts.Count) { continue }",
            "        while ($numbers.Count -lt 4) { $numbers += 0 }",
            "        if ((New-Object System.Version $numbers[0], $numbers[1], $numbers[2], $numbers[3]) -ge $minimum) { Write-Output 'Installed'; exit 0 }",
            "    }",
            "    $uninstall.Close()",
            "}",
            "exit 0"
        };
        return string.Join("\r\n", lines);
    }

    [Theory]
    [InlineData("aspnethostingbundle10.json", "10.0", "Microsoft .NET 10", "10.0.12")]
    [InlineData("aspnethostingbundle8.json", "8.0", "Microsoft .NET 8", "8.0.31")]
    public void TheHostingBundleDetectsItsOwn32BitArpEntry(string file, string channel, string cmName, string version)
    {
        var def = DefinitionFixture.Load(file);
        var staged = StagingService.BuildDetection(def, null, version);

        Assert.Equal(cmName, def.CMName);
        Assert.Equal("Script", staged.Type);
        Assert.Equal("PowerShell", staged.ScriptLanguage);
        Assert.Equal(ArpEntryScript($"Microsoft .NET {channel}.", " - Windows Server Hosting", "Microsoft Corporation", version + ".0"), staged.ScriptText);
        Assert.DoesNotContain("Registry64", staged.ScriptText);
        Assert.Null(def.Detection.GroupSizes);
        Assert.Null(def.Detection.Clauses);
    }

    [Fact]
    public void TheDotNet10BundleTakesTheLtsChannel()
    {
        Assert.Equal("releases-index[?channel-version=10.0,release-type=lts].latest-runtime",
            DefinitionFixture.Load("aspnethostingbundle10.json").VersionScraper.Pattern);
    }

    [Theory]
    [InlineData("msvcruntimesx64.json", "x64", @"%SystemRoot%\System32")]
    [InlineData("msvcruntimesx86.json", "x86", @"%SystemRoot%\SysWOW64")]
    public void TheRuntimeDetectsVcruntime140ThroughTheNativeViewAndForcesAReboot(string file, string arch, string folder)
    {
        var def = DefinitionFixture.Load(file);
        var staged = StagingService.BuildDetection(def, null, "14.51.36247", "14.51.36247.0");

        Assert.Equal($"Microsoft Visual C++ v14 Redistributable ({arch})", def.CMName);
        Assert.Equal(folder, staged.FilePath);
        Assert.Equal("vcruntime140.dll", staged.FileName);
        Assert.Equal("Version", staged.PropertyType);
        Assert.Equal("GreaterEquals", staged.Operator);
        Assert.Equal("14.51.36247.0", staged.ExpectedValue);
        Assert.True(staged.Is64Bit);
        Assert.Equal("ForceReboot", def.PostExecutionBehavior);
        Assert.Equal("/install /quiet /norestart", def.InstallArgs);
    }

    [Theory]
    [InlineData("msvcruntimesx64.json", "vc_redist.x64-14.51.36247.exe")]
    [InlineData("msvcruntimesx86.json", "vc_redist.x86-14.51.36247.exe")]
    public void SuccessIsReportedAs3010AndFailuresPassThrough(string file, string installer)
    {
        var def = DefinitionFixture.Load(file);

        new WrapperGeneratorService().GenerateExeWrappers(_dir, installer, def.InstallArgs, def.UninstallArgs,
            def.InstallerFramework, def.Application, def.UninstallCommand, StagingService.ResolvePostInstallScript(def, null));
        var install = File.ReadAllText(Path.Combine(_dir, "install.ps1"));
        var uninstall = File.ReadAllText(Path.Combine(_dir, "uninstall.ps1"));

        Assert.True(install.IndexOf("if ($proc.ExitCode -ne 0) { exit $proc.ExitCode }", StringComparison.Ordinal)
            < install.IndexOf("exit 3010", StringComparison.Ordinal));
        Assert.Contains("@('/uninstall', '/quiet', '/norestart')", uninstall);
        Assert.Contains("if ($proc.ExitCode -eq 0) { '3010' | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline; exit 3010 }", uninstall);
        Assert.True(uninstall.IndexOf("exit 3010", StringComparison.Ordinal) < uninstall.IndexOf("exit $proc.ExitCode", StringComparison.Ordinal));
    }

    public static TheoryData<string, string, bool, string, string> TableauCases() => new()
    {
        { "tableaureader.json", "Tableau Reader*", false, "https://downloads.tableau.com/tssoftware/TableauReader-64bit-2026-2-2.exe", "tabreader" },
        { "tableauprep.json", "Tableau Prep*", false, "https://downloads.tableau.com/tssoftware/TableauPrep-2026-2-2.exe", "tabprep" },
        { "tableaudesktop.json", "Tableau 20*", true, "https://downloads.tableau.com/esdalt/2026.2.2/TableauDesktop-64bit-2026-2-2.exe", "tableau" }
    };

    [Fact]
    public void TableauReadsTheHighestReleaseAndPadsAMissingPatch()
    {
        var config = DefinitionFixture.Load("tableaudesktop.json").VersionScraper;
        string? Highest(string html) => HttpRegexScraper.ExtractVersion(html, config.Pattern, config.SelectHighestVersion) is { } v
            ? Transform(v, config.VersionTransform!) : null;

        Assert.Equal("2026.2.2", Highest("<a href=\"/desktop/2026.1.3\">2026.1.3</a> <a href=\"/desktop/2026.2\">2026.2</a> <a href=\"/desktop/2026.2.2\">2026.2.2</a>"));
        Assert.Equal("2026.3.0", Highest("<a>2026.3</a> <a>2026.2.9</a>"));
        Assert.Null(Highest("no releases"));
    }

    [Theory]
    [MemberData(nameof(TableauCases))]
    public void TableauBuildsItsUrlSwitchesAndScripts(string file, string pattern, bool removeInstalled, string url, string process)
    {
        var def = DefinitionFixture.Load(file);

        Assert.Equal(url, StagingService.ApplyDashedVersion(def.DownloadUrl.Replace("{display_version}", "2026.2.2"), "2026.2.2"));
        Assert.Null(def.LocalSource);
        Assert.True(def.RequireValidSignature);
        Assert.Equal(removeInstalled, def.InstallArgs.Contains("REMOVEINSTALLEDAPP=1"));
        Assert.StartsWith("/install /quiet /norestart ACCEPTEULA=1", def.InstallArgs);
        Assert.EndsWith("SENDTELEMETRY=0", def.InstallArgs);
        Assert.Equal([process], def.RunningProcess);
        Assert.Contains("Referer: https://www.tableau.com/", def.VersionScraper.RequestHeaders!);
        Assert.Equal(9, def.VersionScraper.RequestHeaders!.Length);

        var detection = StagingService.BuildDetection(def, null, "2026.2.2",
            tokens: new StageTokens { Version = "2026.2.2", ProductVersion = "26.2.1782" });
        Assert.Contains("RegistryView]::Registry64", detection.ScriptText);
        Assert.DoesNotContain("Registry32", detection.ScriptText);
        Assert.Contains($"-like '{pattern}'", detection.ScriptText);
        Assert.Contains("[version]'26.2.1782'", detection.ScriptText);

        Assert.Contains("QuietUninstallString", def.UninstallCommand);
        Assert.Contains("RegistryView]::Registry64", def.UninstallCommand);
        Assert.DoesNotContain("Registry32", def.UninstallCommand);
        Assert.Contains("-ArgumentList $m.Groups['args'].Value -Wait", def.UninstallCommand);
        Assert.DoesNotContain("/norestart", def.UninstallCommand);
        Assert.Equal("", def.UninstallArgs);
        Assert.DoesNotContain("msiexec", def.UninstallCommand, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PyCharmReadsTheUnifiedProductFromTheReleasesApi()
    {
        var def = DefinitionFixture.Load("pycharm.json");
        var json = """{ "PCP": [ { "version": "2026.2.3", "downloads": { "windows": { "link": "https://download.jetbrains.com/python/pycharm-2026.2.3.exe" }, "windowsARM64": { "link": "https://x/arm.exe" } } } ] }""";

        Assert.Equal("b1a2c3d4e5f60004", def.Id);
        Assert.Equal("PyCharm", def.CMName);
        Assert.Contains("code=PCP", def.VersionScraper.Url);
        Assert.Equal("2026.2.3", StagingService.ExtractFeedHash(json, new VersionScraperConfig { Type = "JsonQuery", PayloadHashQuery = def.VersionScraper.Pattern }, null));
        Assert.Equal("https://download.jetbrains.com/python/pycharm-2026.2.3.exe",
            StagingService.ExtractFeedHash(json, new VersionScraperConfig { Type = "JsonQuery", PayloadHashQuery = def.VersionScraper.DownloadUrlQuery }, null));
    }

    [Theory]
    [InlineData("corretto-jdk8-x64.json", "Amazon Corretto 8 (x64)")]
    [InlineData("corretto-jdk8-x86.json", "Amazon Corretto 8 (x86)")]
    [InlineData("corretto-jdk11-x64.json", "Amazon Corretto 11 (x64)")]
    [InlineData("corretto-jdk11-x86.json", "Amazon Corretto 11 (x86)")]
    [InlineData("corretto-jdk17-x64.json", "Amazon Corretto 17 (x64)")]
    [InlineData("corretto-jdk21-x64.json", "Amazon Corretto 21 (x64)")]
    [InlineData("corretto-jdk25-x64.json", "Amazon Corretto 25 (x64)")]
    [InlineData("git.json", "Git")]
    [InlineData("nodejs.json", "Node.js")]
    [InlineData("rstudio.json", "RStudio")]
    [InlineData("thunderbird.json", "Mozilla Thunderbird (x64 en-US)")]
    [InlineData("webex.json", "Webex")]
    [InlineData("dbeaver.json", "DBeaver")]
    [InlineData("nextcloud.json", "Nextcloud Desktop Client")]
    [InlineData("citrixworkspacecr.json", "Citrix Workspace CR")]
    public void TheConsoleNameMatchesTheApplicationTitleInConfigMgr(string file, string cmName)
    {
        Assert.Equal(cmName, DefinitionFixture.Load(file).CMName);
    }

    [Fact]
    public void EveryDefinitionIdIsUnique()
    {
        var ids = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Definitions"), "*.json")
            .Select(f => (File: Path.GetFileName(f), Id: DefinitionFixture.Load(Path.GetFileName(f)).Id))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.File))}")
            .ToList();

        Assert.Empty(ids);
    }
}
