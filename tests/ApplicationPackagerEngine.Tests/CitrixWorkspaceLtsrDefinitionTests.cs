using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The LTSR builds read their version, download path and SHA-256 from the
/// Citrix update catalog. The catalog serves the x86 build only, so the x64
/// and ARM64 definitions stage from a local folder and never download.
/// </summary>
public sealed class CitrixWorkspaceLtsrDefinitionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-cwaltsr-" + Guid.NewGuid().ToString("N"));

    public CitrixWorkspaceLtsrDefinitionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Catalog(params string[] installers) =>
        "<Catalog platform=\"win32\"><Installers name=\"Receiver\">" + string.Concat(installers) + "</Installers></Catalog>";

    private static string Installer(string stream, string version, string? hash = null) =>
        $"<Installer><Admin>Prefer</Admin><DownloadURL>/Receiver/Win/CitrixWorkspaceApp{version}.exe</DownloadURL><Hash>{hash ?? new string('a', 64)}</Hash>" +
        $"<InstallType>exe</InstallType><Stream>{stream}</Stream><Version>{version}</Version><Size>1</Size></Installer>";

    private static string? Version(string xml) =>
        HttpRegexScraper.ExtractVersion(xml, DefinitionFixture.Load("citrixworkspace-ltsr-x86.json").VersionScraper.Pattern, selectHighest: true);

    [Fact]
    public void TheNewestLtsrEntryWinsAndTheCurrentStreamIsIgnored()
    {
        var hash = string.Concat(Enumerable.Repeat("3D", 32));
        var xml = Catalog(Installer("Current", "26.9.0.100"), Installer("LTSR", "26.7.0.269", hash), Installer("LTSR", "25.3.10.12"));
        var config = DefinitionFixture.Load("citrixworkspace-ltsr-x86.json").VersionScraper;

        Assert.Equal("26.7.0.269", Version(xml));
        var url = Regex.Match(xml, config.DownloadUrlQuery!.Replace("{version}", Regex.Escape("26.7.0.269")));
        Assert.Equal("Receiver/Win/CitrixWorkspaceApp26.7.0.269.exe", url.Groups[1].Value);
        Assert.Equal(hash, StagingService.ExtractFeedHash(xml, config, "26.7.0.269"));
    }

    [Fact]
    public void ACatalogWithoutAnLtsrEntryYieldsNoVersion()
    {
        Assert.Null(Version(Catalog(Installer("Current", "26.9.0.100"))));
    }

    [Fact]
    public void AnLtsrEntryWithoutAValidHashIsSkipped()
    {
        Assert.Equal("25.3.10.12", Version(Catalog(Installer("LTSR", "26.7.0.269", "not-a-hash"), Installer("LTSR", "25.3.10.12"))));
    }

    [Fact]
    public void TheDownloadPathResolvesBelowTheCatalogFolder()
    {
        var def = DefinitionFixture.Load("citrixworkspace-ltsr-x86.json");
        var baseUrl = def.VersionScraper.Url[..(def.VersionScraper.Url.LastIndexOf('/') + 1)];

        Assert.Equal("https://downloadplugins.citrix.com/ReceiverUpdates/Prod/Receiver/Win/CitrixWorkspaceApp26.7.0.269.exe",
            baseUrl + "Receiver/Win/CitrixWorkspaceApp26.7.0.269.exe");
        Assert.True(def.RequirePayloadHash);
    }

    [Theory]
    [InlineData("citrixworkspace-ltsr-x86.json", "x86", "Optional", true, false)]
    [InlineData("citrixworkspace-ltsr-x64.json", "x64", "Required", false, true)]
    [InlineData("citrixworkspace-ltsr-arm64.json", "ARM64", "Required", false, true)]
    public void EachBuildCarriesItsArchitectureSourceAndRegistryView(string file, string arch, string mode, bool downloads, bool is64)
    {
        var def = DefinitionFixture.Load(file);

        Assert.Equal(arch, def.Architecture);
        Assert.Equal($"Citrix Workspace LTSR {arch}", def.CMName);
        Assert.Equal(mode, def.LocalSource!.Mode);
        Assert.Equal(downloads, !string.IsNullOrEmpty(def.DownloadUrl));
        Assert.Equal(is64, def.Detection.Is64Bit);
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\CitrixOnlinePluginPackWeb", def.Detection.RegistryKeyTemplate);
        Assert.Equal(["SelfService", "AuthManSvr", "Receiver"], def.RunningProcess);
    }

    [Theory]
    [InlineData("citrixworkspace-ltsr-x86.json", new[] { "CitrixWorkspaceApp.exe", "CitrixWorkspaceFullInstaller (1).exe" })]
    [InlineData("citrixworkspace-ltsr-x64.json", new[] { "CitrixWorkspaceApp_x64.exe" })]
    [InlineData("citrixworkspace-ltsr-arm64.json", new[] { "CitrixWorkspaceFullInstaller_ARM64.exe" })]
    public void EachBuildSelectsOnlyItsOwnInstallers(string file, string[] expected)
    {
        foreach (var n in new[] { "CitrixWorkspaceApp.exe", "CitrixWorkspaceFullInstaller (1).exe", "CitrixWorkspaceApp_x64.exe", "CitrixWorkspaceFullInstaller_ARM64.exe" })
            File.WriteAllText(Path.Combine(_dir, n), "x");
        var source = DefinitionFixture.Load(file).LocalSource!;

        var selected = Directory.GetFiles(_dir, source.Filter)
            .Select(Path.GetFileName)
            .Where(n => !source.Exclude.Any(x => LocalSourceResolver.WildcardMatch(n!, x)))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, selected);
    }

    [Theory]
    [InlineData("CitrixWorkspaceFullInstaller_x64.exe", "CitrixWorkspaceFullInstaller-LTSR-x64-26.7.0.269.exe")]
    [InlineData("CitrixWorkspaceApp_x64.exe", "CitrixWorkspaceApp-LTSR-x64-26.7.0.269.exe")]
    public void TheStagedNameRecordsTheInstallerKindArchitectureAndVersion(string source, string expected)
    {
        var def = DefinitionFixture.Load("citrixworkspace-ltsr-x64.json");
        Assert.Equal(expected, LocalSourceResolver.StagedName(def.LocalSource!, source, "26.7.0.269", def.InstallerFileName!));
    }

    [Fact]
    public void UninstallRunsTheStagedInstallerAndFailsWhenItIsGone()
    {
        var command = DefinitionFixture.Load("citrixworkspace-ltsr-arm64.json").UninstallCommand!;

        Assert.Contains("-Filter 'CitrixWorkspace*-LTSR-ARM64-*.exe'", command);
        Assert.Contains("if (-not $exe) { exit 1 }", command);
        Assert.Contains("@('/silent', '/uninstall')", command);
    }

    [Fact]
    public void TheCurrentReleaseDropsNoReboot()
    {
        Assert.DoesNotContain("/noreboot", DefinitionFixture.Load("citrixworkspacecr.json").InstallArgs);
    }
}
