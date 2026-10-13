using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Wsus;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Definitions whose detection, uninstall or version source a vendor change made wrong:
/// CCleaner, CutePDF Writer, Jabra Direct, Windows Admin Center, UltraVNC, Unity Hub,
/// LibreOffice, SMath Studio, Firefox ESR and Liberica JDK 21. Page and feed text is
/// served from a loopback socket; nothing leaves the machine.
/// </summary>
public sealed class VendorFixDefinitionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apkg-vendor-fix-" + Guid.NewGuid().ToString("N"));

    public VendorFixDefinitionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>Runs a script in Windows PowerShell and returns its output lines.</summary>
    private string[] RunPowerShell(string script)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(path, script, new UTF8Encoding(true));
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path }) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}: {output}{stderr.Result}");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
    }

    private static string Literal(string text) => "@'\r\n" + text + "\r\n'@";

    private static JsonObject IntuneRule(StageDetection detection) => (JsonObject)IntunePublishService.BuildRules(detection)[0]!;

    // CCleaner

    [Theory]
    [InlineData("7.3.1234", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\CCleaner 7")]
    [InlineData("8.0.1", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\CCleaner 8")]
    public void CCleanerDetectsTheUninstallKeyThatCarriesTheMajorVersion(string version, string key)
    {
        var def = DefinitionFixture.Load("ccleaner.json");
        var staged = StagingService.BuildDetection(def, null, version, version, null, null, new StageTokens { Version = version });

        Assert.Equal(key, staged.RegistryKeyRelative);
        Assert.Equal("DisplayVersion", staged.ValueName);
        Assert.Equal("GreaterEquals", staged.Operator);
        Assert.True(staged.Is64Bit);
    }

    [Fact]
    public void TheCCleanerUninstallCommandParsesAndChoosesTheArgumentsOfEachUninstaller()
    {
        var command = DefinitionFixture.Load("ccleaner.json").UninstallCommand!;
        var start = command.IndexOf("if ($cmd -match", StringComparison.Ordinal);
        var end = command.IndexOf("if (-not (Test-Path", StringComparison.Ordinal);
        Assert.True(start > -1 && end > start);
        var arguments = command.Split("\r\n").Single(l => l.StartsWith("$arguments = ", StringComparison.Ordinal));

        var lines = RunPowerShell($$"""
            $errors = $null; $tokens = $null
            [void][System.Management.Automation.Language.Parser]::ParseInput({{Literal(command)}}, [ref]$tokens, [ref]$errors)
            "parse errors: $(@($errors).Count)"
            foreach ($cmd in @(
                '"C:\Program Files\Common Files\Piriform\Icarus\piriform-ccl\icarus.exe" /manual_update /uninstall:piriform-ccl',
                '"C:\Program Files\CCleaner\uninst.exe"',
                'C:\Program Files\Common Files\Piriform\Icarus\piriform-ccl\icarus.exe /manual_update /uninstall:piriform-ccl')) {
                {{command[start..end]}}
                {{arguments}}
                (Split-Path -Leaf $exe) + ' | ' + ($arguments -join ' ; ')
            }
            (@('CCleaner', 'CCleaner 7') | Sort-Object -Descending)[0]
            """);

        Assert.Equal(
            ["parse errors: 0",
             "icarus.exe | /manual_update /uninstall:piriform-ccl ; /silent",
             "uninst.exe | /S",
             "icarus.exe | /manual_update /uninstall:piriform-ccl ; /silent",
             "CCleaner 7"],
            lines);
        Assert.Contains("Sort-Object -Property PSChildName -Descending", command);
        Assert.Matches(@"uninstaller not found[^\r\n]*exit 1", command);
        Assert.Contains("'^CCleaner( \\d+)?$'", command);
    }

    // CutePDF Writer

    [Fact]
    public void CutePdfWriterDetectsTheExistenceOfThe64BitUninstallKeyAndComparesNoValue()
    {
        var def = DefinitionFixture.Load("cutepdfwriter.json");
        var staged = StagingService.BuildDetection(def, null, "4.0.1.5");

        Assert.Equal("RegistryKey", staged.Type);
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\CutePDF Writer Installation", staged.RegistryKeyRelative);
        Assert.True(staged.Is64Bit);
        Assert.True(string.IsNullOrEmpty(staged.ValueName));
        Assert.True(string.IsNullOrEmpty(staged.ExpectedValue));

        var rule = IntuneRule(staged);
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\CutePDF Writer Installation", (string?)rule["keyPath"]);
        Assert.Equal("exists", (string?)rule["operationType"]);
        Assert.False((bool)rule["check32BitOn64System"]!);
    }

    // Jabra Direct

    [Fact]
    public void JabraDirectComparesTheVersionOfTheInstalledExecutableAsTheReleasePagePrintsIt()
    {
        var def = DefinitionFixture.Load("jabradirect.json");
        var staged = StagingService.BuildDetection(def, null, "8.3.26701");

        Assert.Equal("File", staged.Type);
        Assert.Equal(@"C:\Program Files\Jabra\Direct6", staged.FilePath);
        Assert.Equal("jabra-direct.exe", staged.FileName);
        Assert.Equal("Version", staged.PropertyType);
        Assert.Equal("GreaterEquals", staged.Operator);
        Assert.Equal("8.3.26701", staged.ExpectedValue);
        Assert.True(staged.Is64Bit);

        var rule = IntuneRule(staged);
        Assert.Equal("jabra-direct.exe", (string?)rule["fileOrFolderName"]);
        Assert.Equal("version", (string?)rule["operationType"]);
        Assert.Equal("greaterThanOrEqual", (string?)rule["operator"]);
        Assert.Equal("8.3.26701", (string?)rule["comparisonValue"]);
    }

    [Fact]
    public void TheJabraDirectUninstallCommandFindsTheBundleEntryByNameOrUpgradeCodeAndNoKeyByBundleId()
    {
        var command = DefinitionFixture.Load("jabradirect.json").UninstallCommand!;
        Assert.DoesNotMatch(@"Uninstall\\\{", command);

        var lines = RunPowerShell($$"""
            $errors = $null; $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseInput({{Literal(command)}}, [ref]$tokens, [ref]$errors)
            "parse errors: $(@($errors).Count)"
            $where = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Where-Object' }, $true)
            $filter = $where.CommandElements[1].ScriptBlock.GetScriptBlock()
            $quiet = '"C:\Cache\{a}\JabraDirectSetup.exe" /uninstall /quiet'
            $entries = @(
                [pscustomobject]@{ Name = 'by display name'; DisplayName = 'Jabra Direct'; QuietUninstallString = $quiet; BundleUpgradeCode = $null }
                [pscustomobject]@{ Name = 'by upgrade code list'; DisplayName = 'Renamed'; QuietUninstallString = $quiet; BundleUpgradeCode = @('{00000000-0000-0000-0000-000000000000}', '{356870DC-69C0-4757-B09A-22C1786C104C}') }
                [pscustomobject]@{ Name = 'by upgrade code text'; DisplayName = 'Renamed'; QuietUninstallString = $quiet; BundleUpgradeCode = '{356870DC-69C0-4757-B09A-22C1786C104C}' }
                [pscustomobject]@{ Name = 'a package inside the bundle'; DisplayName = 'Jabra Direct'; QuietUninstallString = $null; UninstallString = 'MsiExec.exe /X{00000000-0000-0000-0000-000000000000}'; BundleUpgradeCode = $null }
                [pscustomobject]@{ Name = 'another product'; DisplayName = 'Other Product'; QuietUninstallString = '"C:\Cache\{b}\Other.exe" /uninstall /quiet'; BundleUpgradeCode = '{11111111-1111-1111-1111-111111111111}' }
            )
            foreach ($entry in $entries) { $entry.Name + ': ' + @($entry | Where-Object $filter).Count }
            """);

        Assert.Equal(
            ["parse errors: 0", "by display name: 1", "by upgrade code list: 1", "by upgrade code text: 1",
             "a package inside the bundle: 0", "another product: 0"],
            lines);
    }

    // Windows Admin Center

    [Fact]
    public void WindowsAdminCenterComparesTheVersionOnThe64BitInnoUninstallEntry()
    {
        var def = DefinitionFixture.Load("windowsadmincenter.json");
        var staged = StagingService.BuildDetection(def, null, "2.7.5.21", "2.7.5.21");

        Assert.Equal("RegistryKeyValue", staged.Type);
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\9B27DF2F-5386-41DF-B52B-5DF81914B043_is1", staged.RegistryKeyRelative);
        Assert.Equal("DisplayVersion", staged.ValueName);
        Assert.Equal("Version", staged.PropertyType);
        Assert.Equal("GreaterEquals", staged.Operator);
        Assert.Equal("2.7.5.21", staged.ExpectedValue);
        Assert.True(staged.Is64Bit);

        var rule = IntuneRule(staged);
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\9B27DF2F-5386-41DF-B52B-5DF81914B043_is1", (string?)rule["keyPath"]);
        Assert.Equal("version", (string?)rule["operationType"]);
        Assert.Equal("greaterThanOrEqual", (string?)rule["operator"]);
        Assert.False((bool)rule["check32BitOn64System"]!);

        // The stage reads the uninstaller hint from this key; the wrapper names the product's own entry.
        Assert.Equal("9B27DF2F-5386-41DF-B52B-5DF81914B043_is1", WrapperGeneratorService.ResolveUninstallHints(staged).ArpKeyName);
    }

    // UltraVNC

    [Fact]
    public void TheUltraVncUninstallCommandFindsTheUninstallerThroughItsUninstallEntry()
    {
        var command = DefinitionFixture.Load("ultravnc.json").UninstallCommand!;

        Assert.Contains("Ultravnc2_is1", command);
        Assert.Contains("UninstallString", command);
        Assert.DoesNotMatch("Program Files|ProgramFiles|unins000", command);
        Assert.Matches(@"Test-Path -LiteralPath \$exe\)\) \{ Write-Error .*exit 1", command);
        Assert.Equal(["parse errors: 0"], RunPowerShell($$"""
            $errors = $null; $tokens = $null
            [void][System.Management.Automation.Language.Parser]::ParseInput({{Literal(command)}}, [ref]$tokens, [ref]$errors)
            "parse errors: $(@($errors).Count)"
            """));
    }

    [Theory]
    [InlineData("<a href=\"/all/summary/3-setup/506-ultravnc-1830-x86-setup.html\">x86</a><a href=\"/all/summary/3-setup/508-ultravnc-1830-x64-setup.html\">x64</a><a href=\"/all/summary/3-setup/509-ultravnc-1830-msi-x64.html\">msi</a>",
        "3-setup/508-ultravnc-1830-x64-setup.html")]
    [InlineData("<a href=\"/component/jdownloads/summary/3-setup/508-ultravnc-1830-x64-setup.html\">x64</a>", "3-setup/508-ultravnc-1830-x64-setup.html")]
    [InlineData("<a href=\"/component/jdownloads/summary/3-setup/508-ultravnc-1830-x64-setup.html?Itemid=101\">x64</a>", "3-setup/508-ultravnc-1830-x64-setup.html?Itemid=101")]
    [InlineData("<a href=\"/all/summary/3-setup/506-ultravnc-1830-x86-setup.html\">x86</a><a href=\"/all/summary/3-setup/509-ultravnc-1830-msi-x64.html\">msi</a>", null)]
    [InlineData("<a href=\"https://example.test/all/summary/3-setup/508-ultravnc-1830-x64-setup.html\">x64</a>", null)]
    [InlineData("", null)]
    public void UltraVncReadsTheX64SetupEntryFromEitherLinkForm(string html, string? expected)
    {
        var pattern = DefinitionFixture.Load("ultravnc.json").VersionScraper.Hops![1].Pattern;
        var match = System.Text.RegularExpressions.Regex.Match(html, pattern);

        Assert.Equal(expected, match.Success ? match.Groups["capture"].Value : null);
    }

    // Unity Hub

    [Theory]
    [InlineData("version: 3.22.2\nfiles:\n  - url: 3.22.2/UnityHubSetup-3.22.2-x64.exe\n    sha512: AAAA\n    size: 1\n  - url: 3.22.2/UnityHubSetup-3.22.2-arm64.exe\n    sha512: BBBB\n    size: 1\nreleaseDate: 2026-10-02T20:18:59.543Z\n",
        "3.22.2", "3.22.2/UnityHubSetup-3.22.2-x64.exe")]
    [InlineData("version: 3.22.2\r\nfiles:\r\n  - url: 3.22.2/UnityHubSetup-3.22.2-x64.exe\r\n", "3.22.2", "3.22.2/UnityHubSetup-3.22.2-x64.exe")]
    [InlineData("version: 3.22.2\nfiles:\n  - url: 3.22.2/UnityHubSetup-3.22.2-arm64.exe\n", "3.22.2", null)]
    public async Task UnityHubReadsTheX64InstallerFromTheFilesListOfTheFeed(string feed, string version, string? path)
    {
        var scraper = DefinitionFixture.Load("unityhub.json").VersionScraper;
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["hub/prod/latest.yml"] = Encoding.UTF8.GetBytes(feed) });
        var config = new VersionScraperConfig
        {
            Type = scraper.Type, Url = server.Url("hub/prod/latest.yml"), Pattern = scraper.Pattern, DownloadUrlQuery = scraper.DownloadUrlQuery
        };

        var (foundVersion, url) = await new HttpRegexScraper().GetVersionAndDownloadUrlAsync(config);

        Assert.Equal(version, foundVersion);
        Assert.Equal(path, url);
        Assert.Equal("https://public-cdn.cloud.unity3d.com/hub/prod/{version}/UnityHubSetup-{version}-x64.exe", DefinitionFixture.Load("unityhub.json").DownloadUrl);
    }

    // LibreOffice

    private static VersionScraperConfig LibreOfficeConfig(LoopbackFileServer server)
    {
        var scraper = DefinitionFixture.Load("libreoffice.json").VersionScraper;
        Assert.True(scraper.SelectHighestVersion);
        Assert.Equal("https://download.documentfoundation.org/libreoffice/stable/{version}/win/x86_64/LibreOffice_{version}_Win_x86-64.msi", scraper.ServedUrlTemplate);
        return new VersionScraperConfig
        {
            Type = scraper.Type, Url = server.Url("stable/"), Pattern = scraper.Pattern, SelectHighestVersion = true,
            ServedUrlTemplate = server.Url("stable/{version}/win/x86_64/LibreOffice_{version}_Win_x86-64.msi")
        };
    }

    private const string LibreOfficeIndex = "<a href=\"25.8.7/\">25.8.7/</a><a href=\"26.2.6/\">26.2.6/</a><a href=\"26.8.0/\">26.8.0/</a><a href=\"26.8.1/\">26.8.1/</a>";

    [Fact]
    public async Task LibreOfficeTakesTheNewestListedVersionWhoseInstallerIsServed()
    {
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]>
        {
            ["stable/"] = Encoding.UTF8.GetBytes(LibreOfficeIndex),
            ["stable/26.8.0/win/x86_64/LibreOffice_26.8.0_Win_x86-64.msi"] = [1],
            ["stable/26.2.6/win/x86_64/LibreOffice_26.2.6_Win_x86-64.msi"] = [1]
        });

        Assert.Equal("26.8.0", await new HttpRegexScraper().GetLatestVersionAsync(LibreOfficeConfig(server)));
        Assert.Contains(server.Requests, r => r.Contains("LibreOffice_26.8.1_Win_x86-64.msi", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Requests, r => r.Contains("LibreOffice_26.2.6_Win_x86-64.msi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LibreOfficeWithNoServedInstallerHasNoLatestVersion()
    {
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["stable/"] = Encoding.UTF8.GetBytes(LibreOfficeIndex) });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new HttpRegexScraper().GetLatestVersionAsync(LibreOfficeConfig(server)));
        Assert.Contains("No listed version has a downloadable installer", ex.Message);
    }

    // SMath Studio

    [Fact]
    public void SMathStudioRepeatsATimedOutPageFetchTwiceAndStopsEachAttemptAfterTwentySeconds()
    {
        var scraper = DefinitionFixture.Load("smathstudio.json").VersionScraper;

        Assert.Equal(2, scraper.FetchRetries);
        Assert.Equal(20, scraper.FetchMaxTimeSeconds);
        Assert.Equal(["--max-time", "20", "--retry", "2", "--retry-delay", "5"], HttpRegexScraper.FetchLimitArguments(scraper.FetchRetries, scraper.FetchMaxTimeSeconds));
        Assert.Equal(["--max-time", "25"], HttpRegexScraper.FetchLimitArguments(0, 0));
        Assert.Equal(TimeSpan.FromSeconds(75), HttpRegexScraper.FetchBound(2, 20));
        Assert.Equal(TimeSpan.FromSeconds(30), HttpRegexScraper.FetchBound(0, 0));
    }

    [Fact]
    public async Task APageFetchThatCurlEndsWithAnErrorNamesTheCurlExitCode()
    {
        string url;
        using (var server = new LoopbackFileServer(new Dictionary<string, byte[]>())) url = server.Url("download");
        var config = new VersionScraperConfig { Type = "HttpRegex", Url = url, Pattern = "(\\d+)" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new HttpRegexScraper().GetLatestVersionAsync(config));

        Assert.Equal($"Failed to fetch {url} (curl exit 7).", ex.Message);
    }

    // Firefox ESR and Liberica JDK 21

    [Fact]
    public async Task FirefoxEsrRecordsTheNumericVersionAndDownloadsTheReleaseTrain()
    {
        var def = DefinitionFixture.Load("firefoxesr.json");
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]>
        {
            ["versions.json"] = "{ \"FIREFOX_ESR\": \"140.17.0esr\", \"LATEST_FIREFOX_VERSION\": \"151.0\" }"u8.ToArray()
        });
        var config = new VersionScraperConfig
        {
            Type = def.VersionScraper.Type, Url = server.Url("versions.json"), Pattern = def.VersionScraper.Pattern,
            VersionTransform = def.VersionScraper.VersionTransform
        };

        var (raw, display) = await new ScraperRegistry().GetLatestVersionWithTransformAsync(config);

        Assert.Equal("140.17.0esr", raw);
        Assert.Equal("140.17.0", display);
        Assert.NotNull(ApplicationPackagerUI.Services.PackagerExecutionService.VersionLine(display + "\r\n"));
        Assert.Equal("https://releases.mozilla.org/pub/firefox/releases/{version}/win64/en-US/Firefox%20Setup%20{version}.msi", def.DownloadUrl);
        Assert.Equal(0, VersionCompare.Compare("140.17.0esr", display));
    }

    [Fact]
    public async Task LibericaJdk21KeepsTheBuildSuffixAsTheVendorPrintsIt()
    {
        var def = DefinitionFixture.Load("liberica-jdk21.json");
        var feed = "[{\"filename\":\"bellsoft-jdk21.0.12+10-windows-amd64.msi\",\"downloadUrl\":\"https://example.invalid/a.msi\",\"version\":\"21.0.12+10\"}," +
                   "{\"filename\":\"bellsoft-jdk21.0.12.1+1-windows-amd64.msi\",\"downloadUrl\":\"https://example.invalid/b.msi\",\"version\":\"21.0.12.1+1\"}]";
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["releases"] = Encoding.UTF8.GetBytes(feed) });
        var config = new VersionScraperConfig
        {
            Type = def.VersionScraper.Type, Url = server.Url("releases"), Pattern = def.VersionScraper.Pattern,
            DownloadUrlQuery = def.VersionScraper.DownloadUrlQuery
        };

        var (raw, display) = await new ScraperRegistry().GetLatestVersionWithTransformAsync(config);

        Assert.Equal("21.0.12.1+1", raw);
        Assert.Equal("21.0.12.1+1", display);
        Assert.Null(def.DisplayVersionOverride);
        Assert.Equal("21.0.12.1+1", ApplicationPackagerUI.Services.PackagerExecutionService.VersionLine(display + "\r\n"));
        Assert.Equal(0, VersionCompare.Compare("21.0.12.1.1", display));
    }

    // WSUS

    [Fact]
    public void WsusCannotTakeTheBuildsWhoseWrapperOrDetectionItCannotCarry()
    {
        Assert.Equal("No (CustomInstall)", Wsus.DefaultBuildWsusCheck.Compute(DefinitionFixture.Load("ccleaner.json")).Text);
        Assert.Equal("No (DetectionNotMappable)", Wsus.DefaultBuildWsusCheck.Compute(DefinitionFixture.Load("cutepdfwriter.json")).Text);
        Assert.Equal("Yes", Wsus.DefaultBuildWsusCheck.Compute(DefinitionFixture.Load("jabradirect.json")).Text);
        Assert.Equal("Yes", Wsus.DefaultBuildWsusCheck.Compute(DefinitionFixture.Load("windowsadmincenter.json")).Text);
        Assert.Equal("Yes", Wsus.DefaultBuildWsusCheck.Compute(DefinitionFixture.Load("ultravnc.json")).Text);
    }
}
