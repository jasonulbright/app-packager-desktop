using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The stage folder is built from the Application and Architecture of a definition, which can come from a vendor feed.</summary>
public sealed class StageFolderTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "apd-stage-root");

    [Theory]
    [InlineData("7-Zip", "x64", "7-Zip-x64")]
    [InlineData("Mozilla Firefox", "x86", "MozillaFirefox-x86")]
    [InlineData("GN Audio A/S", "x64", "GNAudioA/S-x64")]
    [InlineData("MySQL Connector/NET", "ARM64", "MySQLConnector/NET-ARM64")]
    public void ASafeNameKeepsTheFolderThatItAlwaysHad(string application, string architecture, string expected) =>
        Assert.Equal(expected, StageFolder.Name(application, architecture));

    [Fact]
    public void EveryShippedDefinitionKeepsItsFolder()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Definitions");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var checkedCount = 0;
        foreach (var file in Directory.GetFiles(folder, "*.json"))
        {
            var def = JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(file), options);
            if (def is null || string.IsNullOrWhiteSpace(def.Application)) continue;
            Assert.Equal($"{def.FolderApplication().Replace(" ", "")}-{def.Architecture}", StageFolder.Name(def));
            if (def.StageFolderName is not null) Assert.NotEqual(def.Application, def.StageFolderName);
            checkedCount++;
        }
        Assert.True(checkedCount > 300);
    }

    [Fact]
    public void TheTitleOfThePowerShellAppDoesNotMoveTheStageOfAnApplicationThatWasStagedBefore()
    {
        var putty = JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Definitions", "putty.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal("PuTTY", putty.Application);
        Assert.Equal("PuTTY(x64)-x64", StageFolder.Name(putty));
    }

    [Theory]
    [InlineData(@"..\..\Windows", "x64")]
    [InlineData("../evil", "x64")]
    [InlineData(@"C:\Windows\System32", "x64")]
    [InlineData(@"\\server\share", "x64")]
    [InlineData("..", "")]
    [InlineData("a", @"..\..\x")]
    [InlineData("Dev:ice", "x64")]
    [InlineData("con", "")]
    [InlineData("trailing.", "")]
    [InlineData("", "")]
    public void ANameThatCouldLeaveTheRootBecomesAPlainNameUnderTheRoot(string application, string architecture)
    {
        var def = new AppDefinition { Application = application, Architecture = architecture };

        var path = Path.GetFullPath(StageFolder.Under(Root, def));

        Assert.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
        var name = StageFolder.Name(def);
        Assert.DoesNotContain(name.Split('/', '\\'), segment => segment.Trim('.').Length == 0);
        Assert.False(Path.IsPathRooted(name));
        Assert.DoesNotContain(':', name);
    }

    [Theory]
    [InlineData("Contoso Agent (x64)", "ContosoAgentx64")]
    [InlineData("A/S", "A_S")]
    [InlineData(@"..\x", "_x")]
    public void TheInstallerFileStemHoldsNoSeparator(string application, string expected) =>
        Assert.Equal(expected.Replace("..", "_"), StageFolder.FileStem(application).Replace("..", "_"));

    [Theory]
    [InlineData("catalog:x1", "Catalog", false)]
    [InlineData("", "Catalog", false)]
    [InlineData("CATALOG:X1", "Catalog", false)]
    [InlineData("custom:x1", "Catalog", false)]
    [InlineData("custom:x1", "Custom", false)]
    [InlineData("catalog:x1", "Custom", false)]
    [InlineData("imported:x1", "Imported", false)]
    [InlineData("catalog:x1", "Imported", true)]
    [InlineData("custom:x1", "Imported", true)]
    [InlineData("imported:x1", "Catalog", true)]
    [InlineData("catalog:shipped-1", "Imported", true)]
    [InlineData("catalog:other", "Catalog", true)]
    public void AStageMustCarryTheIdentityOfTheDefinitionThatPackagesIt(string manifestId, string origin, bool refused)
    {
        var def = new AppDefinition
        {
            Id = "x1", Application = "A", SourceOrigin = Enum.Parse<ApplicationPackagerEngine.Models.Workbench.ApplicationOrigin>(origin)
        };

        Assert.Equal(refused, StageFolder.IdentityRefusal(def, manifestId) is not null);
    }

    [Fact]
    public void ARunWithASnapshotAndARunWithoutOneStageAndPublishUnderTheSameIdentity()
    {
        // The stage writes StagingService.CatalogApplicationId (no snapshot) or the id of the row (snapshot); publish accepts both.
        foreach (var origin in new[] { ApplicationPackagerEngine.Models.Workbench.ApplicationOrigin.Catalog, ApplicationPackagerEngine.Models.Workbench.ApplicationOrigin.Custom,
                     ApplicationPackagerEngine.Models.Workbench.ApplicationOrigin.Imported })
        {
            var def = new AppDefinition { Id = "Contoso.Agent", Application = "A", SourceOrigin = origin };
            var withoutSnapshot = StagingService.CatalogApplicationId(def);
            var withSnapshot = ApplicationPackagerEngine.Models.Workbench.ApplicationIds.New(origin, name: def.Id);

            Assert.Null(StageFolder.IdentityRefusal(def, withoutSnapshot));
            Assert.Null(StageFolder.IdentityRefusal(def, withSnapshot));
        }
    }
}