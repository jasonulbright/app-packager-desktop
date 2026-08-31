using System.Text;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public class IntuneRuleMappingTests
{
    private static JsonObject Single(StageDetection d) => (JsonObject)IntunePublishService.BuildRules(d)[0]!;

    [Fact]
    public void RegistryKeyValue_VersionExpected_MapsToVersionOperation()
    {
        var rule = Single(new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{GUID}",
            ValueName = "DisplayVersion",
            ExpectedValue = "1.2.3.4",
            Operator = "GreaterEquals",
            Is64Bit = true
        });

        Assert.Equal("#microsoft.graph.win32LobAppRegistryRule", (string?)rule["@odata.type"]);
        Assert.Equal("detection", (string?)rule["ruleType"]);
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{GUID}", (string?)rule["keyPath"]);
        Assert.Equal("DisplayVersion", (string?)rule["valueName"]);
        Assert.Equal("version", (string?)rule["operationType"]);
        Assert.Equal("greaterThanOrEqual", (string?)rule["operator"]);
        Assert.Equal("1.2.3.4", (string?)rule["comparisonValue"]);
        Assert.False((bool)rule["check32BitOn64System"]!);
    }

    [Fact]
    public void RegistryKeyValue_NonVersionExpected_MapsToStringEqual()
    {
        var rule = Single(new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"SOFTWARE\Contoso",
            ExpectedValue = "2024-R2",
            Is64Bit = false
        });

        Assert.Equal("string", (string?)rule["operationType"]);
        Assert.Equal("equal", (string?)rule["operator"]);
        Assert.Equal("DisplayVersion", (string?)rule["valueName"]);
        Assert.True((bool)rule["check32BitOn64System"]!);
    }

    [Fact]
    public void RegistryKey_MapsToExistsRule()
    {
        var rule = Single(new StageDetection { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\Contoso" });

        Assert.Equal("#microsoft.graph.win32LobAppRegistryRule", (string?)rule["@odata.type"]);
        Assert.Equal("exists", (string?)rule["operationType"]);
        Assert.Equal("notConfigured", (string?)rule["operator"]);
        Assert.Null(rule["valueName"]);
        Assert.Null(rule["comparisonValue"]);
    }

    [Fact]
    public void File_VersionProperty_MapsToFileSystemVersionRule()
    {
        var rule = Single(new StageDetection
        {
            Type = "File",
            PropertyType = "Version",
            FilePath = @"C:\Program Files\Contoso",
            FileName = "app.exe",
            ExpectedValue = "9.1.0.0",
            Operator = "GreaterEquals"
        });

        Assert.Equal("#microsoft.graph.win32LobAppFileSystemRule", (string?)rule["@odata.type"]);
        Assert.Equal(@"C:\Program Files\Contoso", (string?)rule["path"]);
        Assert.Equal("app.exe", (string?)rule["fileOrFolderName"]);
        Assert.Equal("version", (string?)rule["operationType"]);
        Assert.Equal("greaterThanOrEqual", (string?)rule["operator"]);
        Assert.Equal("9.1.0.0", (string?)rule["comparisonValue"]);
    }

    [Fact]
    public void File_WithoutVersionProperty_MapsToExistsRule()
    {
        var rule = Single(new StageDetection
        {
            Type = "File",
            FilePath = @"C:\Program Files\Contoso",
            FileName = "app.exe"
        });

        Assert.Equal("#microsoft.graph.win32LobAppFileSystemRule", (string?)rule["@odata.type"]);
        Assert.Equal("exists", (string?)rule["operationType"]);
        Assert.Equal("notConfigured", (string?)rule["operator"]);
        Assert.Null(rule["comparisonValue"]);
    }

    [Fact]
    public void Script_MapsToBase64PowerShellRule()
    {
        const string script = "if (Test-Path 'C:\\Contoso') { Write-Output 'ok' }";
        var rule = Single(new StageDetection { Type = "Script", ExpectedValue = script });

        Assert.Equal("#microsoft.graph.win32LobAppPowerShellScriptRule", (string?)rule["@odata.type"]);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(script)), (string?)rule["scriptContent"]);
        Assert.Equal("notConfigured", (string?)rule["operationType"]);
        Assert.Equal("notConfigured", (string?)rule["operator"]);
        Assert.False((bool)rule["enforceSignatureCheck"]!);
        Assert.False((bool)rule["runAs32Bit"]!);
    }

    [Fact]
    public void UnknownType_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => IntunePublishService.BuildRules(new StageDetection { Type = "Msi" }));
        Assert.Contains("no Intune rule mapping", ex.Message);
    }
}
