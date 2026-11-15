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
            Assert.Equal($"{def.Application.Replace(" ", "")}-{def.Architecture}", StageFolder.Name(def));
            checkedCount++;
        }
        Assert.True(checkedCount > 300);
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
    [InlineData("catalog:x1", "catalog:x1", false)]
    [InlineData("", "catalog:x1", false)]
    [InlineData("CATALOG:X1", "catalog:x1", false)]
    [InlineData("catalog:shipped-1", "imported:x1", true)]
    public void AStageMustCarryTheIdentityOfTheDefinitionThatPackagesIt(string manifestId, string definitionId, bool refused)
    {
        var origin = definitionId.StartsWith("imported", StringComparison.Ordinal) ? ApplicationPackagerEngine.Models.Workbench.ApplicationOrigin.Imported
            : ApplicationPackagerEngine.Models.Workbench.ApplicationOrigin.Catalog;
        var def = new AppDefinition { Id = definitionId[(definitionId.IndexOf(':') + 1)..], Application = "A", SourceOrigin = origin };

        Assert.Equal(refused, StageFolder.IdentityRefusal(def, manifestId) is not null);
    }
}