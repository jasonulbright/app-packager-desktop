using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A definition names its stage flow by several fields, and the first matching branch wins. A definition whose
/// fields name two flows used to stage by one and ignore the other without a message; it is now refused.
/// </summary>
public sealed class DefinitionFlowConflictTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void AnExtractSetupDefinitionWithAnMsiInstallerTypeIsRefusedWithTheFieldsNamed()
    {
        var def = new AppDefinition { InstallerType = "MSI", ExtractSetup = new ExtractSetupConfig() };

        var message = StagingService.FlowConflict(def);

        Assert.NotNull(message);
        Assert.Contains("ExtractSetup", message);
        Assert.Contains("MSI", message);
    }

    [Fact]
    public void TwoOfBootstrapperLayoutZipPayloadAndExtractSetupAreRefused()
    {
        var def = new AppDefinition { InstallerType = "EXE", ZipPayload = new ZipPayloadConfig(), ExtractSetup = new ExtractSetupConfig() };

        var message = StagingService.FlowConflict(def);

        Assert.NotNull(message);
        Assert.Contains("ZipPayload", message);
        Assert.Contains("ExtractSetup", message);
    }

    [Fact]
    public async Task StagingRefusesAConflictingDefinitionBeforeAnyDownload()
    {
        var def = new AppDefinition { InstallerType = "MSI", ExtractSetup = new ExtractSetupConfig(), DownloadUrl = "https://example.test/setup.exe" };
        var root = Path.Combine(Path.GetTempPath(), "apkg-flow-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await new StagingService(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(),
                new WrapperGeneratorService(), new ManifestService()).StageAsync(def, root);

            Assert.False(result.Success);
            Assert.Contains("ExtractSetup", result.ErrorMessage);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData("EXE", false)]
    [InlineData("MSI", false)]
    public void ADefinitionWithOneFlowHasNoConflict(string type, bool extract)
    {
        var def = new AppDefinition { InstallerType = type, ExtractSetup = extract ? new ExtractSetupConfig() : null };

        Assert.Null(StagingService.FlowConflict(def));
    }

    [Fact]
    public void NoShippedDefinitionNamesTwoFlows()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Definitions");
        var files = Directory.GetFiles(folder, "*.json");
        Assert.NotEmpty(files);
        var conflicts = files
            .Select(file => (File: Path.GetFileName(file), Def: JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(file), Options)))
            .Where(item => item.Def is not null && StagingService.FlowConflict(item.Def) is not null)
            .Select(item => $"{item.File}: {StagingService.FlowConflict(item.Def!)}")
            .ToList();
        Assert.True(conflicts.Count == 0, string.Join(Environment.NewLine, conflicts));
    }
}
