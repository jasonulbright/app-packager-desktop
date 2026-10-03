using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Wsus;

/// <summary>
/// Stage and Package output without WSUS: a definition that declares no
/// Add/Remove Programs entry stages the manifest it staged before, and the
/// settings a file without a WSUS section loads leave WSUS off.
/// </summary>
public class WsusAbsentTests
{
    private static readonly string DefinitionsFolder = Path.Combine(AppContext.BaseDirectory, "Definitions");

    public static TheoryData<string> Definitions()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(DefinitionsFolder, "*.json").Select(Path.GetFileName).Order()) data.Add(file!);
        return data;
    }

    [Theory]
    [MemberData(nameof(Definitions))]
    public void OnlyADefinitionThatDeclaresAnEntryGainsAWsusDetection(string file)
    {
        var def = DefinitionFixture.Load(file);
        var entry = StagingService.BuildWsusDetection(def, "1.2.3");
        if (def.Detection.Type == ArpEntryDetection.TypeName)
        {
            Assert.StartsWith("aspnethostingbundle", file);
            Assert.NotNull(entry);
        }
        else
        {
            Assert.Null(entry);
        }
    }

    [Fact]
    public void AManifestWithoutAnEntrySerializesWithoutTheField()
    {
        var manifest = new StageManifest { AppName = "A", Detection = new StageDetection { RegistryKeyRelative = "SOFTWARE\\A", ExpectedValue = "1.0" } };
        Assert.DoesNotContain("WsusDetection", JsonSerializer.Serialize(manifest));
    }

    [Fact]
    public void SettingsWithoutAWsusSectionLeaveWsusOffAndUnconfigured()
    {
        var wsus = WsusSettings.FromStored(null);
        Assert.False(wsus.IsConfigured);
        Assert.False(SystemsInUse.Resolve(null, "", wsus.ServerName).Wsus);
        Assert.True(SystemsInUse.Resolve(null, "", wsus.ServerName).ConfigMgr);
    }
}
