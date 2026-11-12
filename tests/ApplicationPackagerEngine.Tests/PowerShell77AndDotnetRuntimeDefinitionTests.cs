using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// PowerShell 7.7.0 is the latest release on GitHub and ships no MSI for Windows (the assets are three zip files and one universal
/// msixbundle), so a definition that took the latest tag built a URL that answers 404, and the stage failed. The definition now takes
/// the highest release that carries the x64 MSI. The .NET runtime definitions of the same change follow the existing ones.
/// </summary>
public sealed class PowerShell77AndDotnetRuntimeDefinitionTests
{
    /// <summary>
    /// The releases list of the GitHub API of 2026-11-12 in its real order (v7.7.0, v7.6.10, v7.7.0-rc.1, v7.6.9, trimmed to the asset names).
    /// Each release body also names its files, as the real ones do, and a body can name a file that the release does not carry.
    /// </summary>
    private const string Releases = """
        [
          { "tag_name": "v7.7.0", "prerelease": false, "assets": [
              { "name": "PowerShell-7.7.0-win-arm64.zip", "browser_download_url": "https://github.com/PowerShell/PowerShell/releases/download/v7.7.0/PowerShell-7.7.0-win-arm64.zip" },
              { "name": "PowerShell-7.7.0-win-x64.zip", "browser_download_url": "https://github.com/PowerShell/PowerShell/releases/download/v7.7.0/PowerShell-7.7.0-win-x64.zip" },
              { "name": "PowerShell-7.7.0-win-x86.zip", "browser_download_url": "https://github.com/PowerShell/PowerShell/releases/download/v7.7.0/PowerShell-7.7.0-win-x86.zip" },
              { "name": "PowerShell-7.7.0.msixbundle", "browser_download_url": "https://github.com/PowerShell/PowerShell/releases/download/v7.7.0/PowerShell-7.7.0.msixbundle" } ] },
          { "tag_name": "v7.6.10", "prerelease": false, "assets": [
              { "name": "PowerShell-7.6.10-win-x64.msi", "browser_download_url": "https://github.com/PowerShell/PowerShell/releases/download/v7.6.10/PowerShell-7.6.10-win-x64.msi" },
              { "name": "PowerShell-7.6.10-win-x86.msi", "browser_download_url": "https://github.com/PowerShell/PowerShell/releases/download/v7.6.10/PowerShell-7.6.10-win-x86.msi" } ] },
          { "tag_name": "v7.7.0-rc.1", "prerelease": true, "assets": [
              { "name": "PowerShell-7.7.0-rc.1-win-x64.msi", "browser_download_url": "https://github.com/PowerShell/PowerShell/releases/download/v7.7.0-rc.1/PowerShell-7.7.0-rc.1-win-x64.msi" } ] },
          { "tag_name": "v7.6.9", "prerelease": false, "assets": [
              { "name": "PowerShell-7.6.9-win-x64.msi", "browser_download_url": "https://github.com/PowerShell/PowerShell/releases/download/v7.6.9/PowerShell-7.6.9-win-x64.msi" } ] }
        ]
        """;

    private static VersionScraperConfig Scraper() => DefinitionFixture.Load("powershell7.json").VersionScraper;

    [Fact]
    public void TheVersionIsTheHighestReleaseThatCarriesTheX64MsiAndNotTheLatestTag()
    {
        var config = Scraper();

        var version = HttpRegexScraper.ExtractVersion(Releases, config.Pattern, config.SelectHighestVersion);

        // 7.7.0 is the latest release and has no MSI; the release candidate of 7.7.0 is a prerelease and does not match.
        Assert.Equal("7.6.10", version);
    }

    [Fact]
    public void APrereleaseAnMsixbundleAnMsixAndABodyThatNamesAFileNeverGiveAVersion()
    {
        var config = Scraper();
        const string onlyUnsuitable = """
            [{ "body": "- PowerShell-7.8.0-win-x64.msi", "assets": [ { "name": "PowerShell-7.7.0-rc.1-win-x64.msi" }, { "name": "PowerShell-7.7.0.msixbundle" }, { "name": "PowerShell-7.7.1-win-x64.msix" }, { "name": "PowerShell-7.7.0-win-x64.zip" } ] }]
            """;

        Assert.Null(HttpRegexScraper.ExtractVersion(onlyUnsuitable, config.Pattern, config.SelectHighestVersion));
    }

    [Fact]
    public void ABackPublishedPatchOfAnOlderLineThatIsListedAboveANewerOneDoesNotWin()
    {
        // Synthetic: a 7.5 patch published after 7.6.10 is listed first and must not be taken.
        const string listed = """
            [{ "assets": [ { "name": "PowerShell-7.5.14-win-x64.msi" } ] }, { "assets": [ { "name": "PowerShell-7.6.10-win-x64.msi" } ] }]
            """;
        var config = Scraper();

        Assert.Equal("7.6.10", HttpRegexScraper.ExtractVersion(listed, config.Pattern, config.SelectHighestVersion));
    }

    [Fact]
    public void TheDownloadUrlOfTheChosenVersionNamesAnAssetThatExists()
    {
        var definition = DefinitionFixture.Load("powershell7.json");
        var version = HttpRegexScraper.ExtractVersion(Releases, definition.VersionScraper.Pattern, definition.VersionScraper.SelectHighestVersion)!;

        var url = definition.DownloadUrl.Replace("{display_version}", version).Replace("{version}", version);

        Assert.Contains($"\"browser_download_url\": \"{url}\"", Releases);
    }

    [Fact]
    public void TheDefinitionStillStagesAnMsiAndKeepsItsDetectionAndArp()
    {
        var definition = DefinitionFixture.Load("powershell7.json");

        Assert.Equal("MSI", definition.InstallerType);
        Assert.Equal("HttpRegex", definition.VersionScraper.Type);
        Assert.True(definition.VersionScraper.SelectHighestVersion);
        Assert.Equal("PowerShell 7-x64", definition.ArpName);
        Assert.Equal("RegistryKeyValue", definition.Detection.Type);
    }

    // ---- the .NET runtime definitions ----

    public static TheoryData<string, string, string, string, string> Runtimes => new()
    {
        // file, application, architecture, channel in the version query, a part of the download address
        { "Dotnet9x86.json", ".NET 9 Desktop Runtime (x86)", "x86", "9.0", "windowsdesktop-runtime-{version}-win-x86.exe" },
        { "Dotnet11x64.json", ".NET 11 Desktop Runtime (x64)", "x64", "11.0", "windowsdesktop-runtime-{version}-win-x64.exe" },
        { "aspnethostingbundle9.json", "ASP.NET 9 Server Hosting Bundle (x64)", "x64", "9.0", "dotnet-hosting-{version}-win.exe" },
        { "aspnethostingbundle11.json", "ASP.NET 11 Server Hosting Bundle (x64)", "x64", "11.0", "dotnet-hosting-{version}-win.exe" }
    };

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void TheNewRuntimeDefinitionFollowsTheExistingOnes(string file, string application, string architecture, string channel, string download)
    {
        var d = DefinitionFixture.Load(file);

        Assert.Equal(application, d.Application);
        Assert.Equal(architecture, d.Architecture);
        Assert.Equal("EXE", d.InstallerType);
        Assert.Equal("WixBurn", d.InstallerFramework);
        Assert.Equal("/install /quiet /norestart", d.InstallArgs);
        Assert.Equal("/uninstall /quiet /norestart", d.UninstallArgs);
        Assert.Contains($"channel-version={channel}]", d.VersionScraper.Pattern);
        Assert.EndsWith(download, d.DownloadUrl);
        Assert.Contains($"/download/dotnet/{channel}", d.VendorUrl);
        Assert.StartsWith($"cpe:2.3:a:microsoft:", d.Cpe);
        Assert.Contains($":{channel[..channel.IndexOf('.')]}.*:", d.Cpe);
    }

    [Fact]
    public void TheDesktopRuntimeX86LooksInTheX86FolderAndTheOthersInTheX64Folder()
    {
        Assert.Equal(@"C:\Program Files (x86)\dotnet\shared\Microsoft.WindowsDesktop.App\{version}", DefinitionFixture.Load("Dotnet9x86.json").Detection.FilePath);
        Assert.False(DefinitionFixture.Load("Dotnet9x86.json").Detection.Is64Bit);
        Assert.Equal(@"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\{version}", DefinitionFixture.Load("Dotnet11x64.json").Detection.FilePath);
        Assert.True(DefinitionFixture.Load("Dotnet11x64.json").Detection.Is64Bit);
    }

    [Fact]
    public void TheHostingBundlesDetectTheirOwnChannelInTheArpEntryAndCarryTheUninstallCommand()
    {
        foreach (var (file, prefix) in new[] { ("aspnethostingbundle9.json", "Microsoft .NET 9.0."), ("aspnethostingbundle11.json", "Microsoft .NET 11.0.") })
        {
            var d = DefinitionFixture.Load(file);
            Assert.Equal(prefix, d.Detection.ArpEntry!.DisplayNamePrefix);
            Assert.Equal(" - Windows Server Hosting", d.Detection.ArpEntry.DisplayNameSuffix);
            Assert.Contains("dotnet-hosting-*.exe", d.UninstallCommand);
        }
    }

    [Fact]
    public void TheShippedDefinitionsHaveUniqueIdsAndTheDesktopRuntime10HasNoX86Definition()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Definitions");
        var all = Directory.GetFiles(folder, "*.json")
            .Select(f => JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(f), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!)
            .ToList();

        Assert.Equal(all.Count, all.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(all, d => d.Application.StartsWith(".NET 10 Desktop Runtime", StringComparison.Ordinal) && d.Architecture == "x86");
        foreach (var name in new[] { ".NET 9 Desktop Runtime (x86)", ".NET 9 Desktop Runtime (x64)", ".NET 10 Desktop Runtime (x64)", ".NET 11 Desktop Runtime (x64)",
                                     "ASP.NET 9 Server Hosting Bundle (x64)", "ASP.NET 11 Server Hosting Bundle (x64)" })
            Assert.Single(all, d => d.Application == name);
    }
}
