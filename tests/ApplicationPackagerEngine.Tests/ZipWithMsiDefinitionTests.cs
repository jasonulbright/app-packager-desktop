using System.IO.Compression;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.Wsus;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A vendor that ships its MSI inside a ZIP: the stage extracts the MSI, and the wrappers,
/// the manifest and the WSUS check must treat the build as the MSI it installs.
/// </summary>
public sealed class ZipWithMsiDefinitionTests
{
    [Fact]
    public void AgentRansackDeclaresTheMsiItInstalls()
    {
        var definition = DefinitionFixture.Load("agentransack.json");

        Assert.Equal("MSI", definition.InstallerType);
        Assert.Equal("Yes", definition.WsusSupport);
        Assert.Contains("msiexec.exe", DefaultBuildWsusCheck.Scripts(definition)["install.ps1"]);
        Assert.DoesNotContain(WsusRefusalCodesOf(definition), code => code == WsusRefusalCodes.InstallerTypeUnsupported);
    }

    private static IReadOnlyList<string> WsusRefusalCodesOf(AppDefinition definition) =>
        Services.Wsus.WsusCompatibility.BlockingCodes(DefaultBuildWsusCheck.Findings(definition));

    [Fact]
    public async Task TheStageOfAgentRansackInstallsTheMsiFromItsArchiveWithWindowsInstaller()
    {
        var root = Path.Combine(Path.GetTempPath(), $"apkg-zipmsi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var definition = DefinitionFixture.Load("agentransack.json");
            var msi = Path.Combine(AppContext.BaseDirectory, "Fixtures", "WsusRules", "seven-zip.msi");
            var version = new MsiInspectionService().ReadProperties(msi).ProductVersion!;
            var content = Path.Combine(root, $"{definition.Application.Replace(" ", "", StringComparison.Ordinal)}-{definition.Architecture}", version);
            Directory.CreateDirectory(content);

            var archive = Path.Combine(content, "agentransack_x64_msi_3562.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                zip.CreateEntryFromFile(msi, "agentransack_x64_3562.msi");

            definition.VersionScraper.DownloadUrlQuery = null;
            definition.VersionScraper.Hops = null;
            definition.DownloadUrl = "https://download.mythicsoft.com/flp/3562/token/agentransack_x64_msi_3562.zip";
            definition.PayloadSha256 = StagingService.ComputeSha256(archive);

            var service = new StagingService(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(),
                new WrapperGeneratorService(), new ManifestService());
            var result = await service.StageAsync(definition, root, knownVersion: version);

            Assert.True(result.Success, result.ErrorMessage);
            var manifest = new ManifestService().Read(result.ManifestPath!);
            Assert.Equal("MSI", manifest.InstallerType);
            Assert.Equal("agentransack_x64_3562.msi", manifest.InstallerFile);
            Assert.False(string.IsNullOrWhiteSpace(manifest.ProductCode));
            Assert.Contains(manifest.ProductCode!, manifest.Detection.RegistryKeyRelative);
            var install = File.ReadAllText(Path.Combine(content, "install.ps1"));
            Assert.Contains("msiexec.exe", install);
            Assert.Contains("agentransack_x64_3562.msi", install);
            Assert.Contains("msiexec.exe", File.ReadAllText(Path.Combine(content, "uninstall.ps1")));
            Assert.False(File.Exists(archive));
        }
        finally
        {
            // A scanner can hold a file it has just seen extracted for a moment.
            for (var attempt = 0; attempt < 20 && Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(100); }
            }
        }
    }
}
