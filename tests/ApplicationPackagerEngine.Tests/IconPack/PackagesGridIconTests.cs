using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.IconPack;

/// <summary>The Packages grid shows the icon that the staging step would take: one lookup for both.</summary>
public sealed class PackagesGridIconTests
{
    [Fact]
    public void TheGridLookupEqualsTheStagingLookupForADefinitionFileWithALocalIcon()
    {
        var root = Directory.CreateTempSubdirectory("apd-icon-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Icons"));
            var definition = Path.Combine(root, "tool.json");
            File.WriteAllText(definition, "{}");
            File.WriteAllBytes(Path.Combine(root, "Icons", "tool.png"), [0x89, 0x50, 0x4E, 0x47]);

            var staging = IconExtractionService.FindExternalIconPath(new AppDefinition { SourcePath = definition });
            var grid = IconExtractionService.FindExternalIconPathForFile(definition);
            Assert.NotNull(grid);
            Assert.Equal(staging, grid);
            Assert.EndsWith("tool.png", grid);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ADefinitionWithoutAnIconAndABlankPathGiveNone()
    {
        var root = Directory.CreateTempSubdirectory("apd-icon-").FullName;
        try
        {
            var definition = Path.Combine(root, "noicon-zz-test.json");
            File.WriteAllText(definition, "{}");
            Assert.Null(IconExtractionService.FindExternalIconPathForFile(definition));
            Assert.Null(IconExtractionService.FindExternalIconPathForFile(" "));
            Assert.Null(IconExtractionService.FindExternalIconPathForFile(null));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EveryShippedDefinitionFileResolvesThroughTheSameLookupAsStaging()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Definitions"), "*.json"))
            Assert.Equal(IconExtractionService.FindExternalIconPath(new AppDefinition { SourcePath = file }),
                IconExtractionService.FindExternalIconPathForFile(file));
    }
}
