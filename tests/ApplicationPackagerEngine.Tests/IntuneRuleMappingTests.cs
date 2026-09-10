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
        var rule = Single(new StageDetection { Type = "Script", ScriptText = script, ScriptLanguage = "PowerShell" });

        Assert.Equal("#microsoft.graph.win32LobAppPowerShellScriptRule", (string?)rule["@odata.type"]);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(script)), (string?)rule["scriptContent"]);
        Assert.Equal("notConfigured", (string?)rule["operationType"]);
        Assert.Equal("notConfigured", (string?)rule["operator"]);
        Assert.False((bool)rule["enforceSignatureCheck"]!);
        Assert.False((bool)rule["runAs32Bit"]!);
    }

    /// <summary>
    /// MSIX staging writes the package version to ExpectedValue beside the body, so the
    /// canonical field has to win or the published rule is the version, not the script.
    /// </summary>
    [Fact]
    public void Script_WithBothFields_EncodesScriptTextNotTheVersion()
    {
        const string script = "$wanted = [version]'3.4.5.0'\nexit 0";
        var rule = Single(new StageDetection
        {
            Type = "Script",
            ScriptText = script,
            ScriptLanguage = "PowerShell",
            ExpectedValue = "3.4.5.0",
            Operator = "GreaterEquals"
        });

        Assert.Equal(script, ScriptOf(rule));
        Assert.NotEqual("3.4.5.0", ScriptOf(rule));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void Script_WithNoBody_Throws(string? scriptText)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => IntunePublishService.BuildRules(new StageDetection { Type = "Script", ScriptText = scriptText }));
        Assert.Contains("no ScriptText", ex.Message);
    }

    /// <summary>
    /// ExpectedValue is a comparison value on a Script detection, never a body, so a
    /// manifest carrying only one is refused whether or not it names an Operator.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("GreaterEquals")]
    public void Script_WithOnlyAComparisonValue_Throws(string? op)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => IntunePublishService.BuildRules(new StageDetection
            {
                Type = "Script",
                ExpectedValue = "3.4.5.0",
                Operator = op
            }));
        Assert.Contains("no ScriptText", ex.Message);
    }

    [Theory]
    [InlineData("VBScript")]
    [InlineData("JavaScript")]
    public void Script_InAnUnsupportedLanguage_Throws(string language)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => IntunePublishService.BuildRules(new StageDetection
            {
                Type = "Script",
                ScriptText = "wscript.echo \"x\"",
                ScriptLanguage = language
            }));
        Assert.Contains("has no Intune rule mapping", ex.Message);
        Assert.Contains(language, ex.Message);
    }

    /// <summary>A blank language keeps the PowerShell default the staging builder applies.</summary>
    [Fact]
    public void Script_WithNoLanguage_EncodesAsPowerShell()
    {
        var rule = Single(new StageDetection { Type = "Script", ScriptText = "exit 0" });

        Assert.Equal("#microsoft.graph.win32LobAppPowerShellScriptRule", (string?)rule["@odata.type"]);
        Assert.Equal("exit 0", ScriptOf(rule));
    }

    [Fact]
    public void UnknownType_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => IntunePublishService.BuildRules(new StageDetection { Type = "Msi" }));
        Assert.Contains("no Intune rule mapping", ex.Message);
    }

    // -- Operator table ------------------------------------------------

    [Theory]
    [InlineData("IsEquals", "equal")]
    [InlineData("NotEquals", "notEqual")]
    [InlineData("GreaterThan", "greaterThan")]
    [InlineData("GreaterEquals", "greaterThanOrEqual")]
    [InlineData("LessThan", "lessThan")]
    [InlineData("LessEquals", "lessThanOrEqual")]
    public void RegistryKeyValue_MapsEveryOrderingOperator(string declared, string graph)
    {
        var rule = Single(new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"SOFTWARE\Contoso",
            ExpectedValue = "1.2.3.4",
            Operator = declared
        });

        Assert.Equal(graph, (string?)rule["operator"]);
    }

    [Fact]
    public void RegistryKeyValue_WithoutAnOperator_DefaultsToEqual()
    {
        var rule = Single(new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"SOFTWARE\Contoso",
            ExpectedValue = "1.2.3.4"
        });

        Assert.Equal("equal", (string?)rule["operator"]);
    }

    [Fact]
    public void File_VersionProperty_WithoutAnOperator_DefaultsToGreaterThanOrEqual()
    {
        var rule = Single(new StageDetection
        {
            Type = "File",
            PropertyType = "Version",
            FilePath = @"C:\Program Files\Contoso",
            FileName = "app.exe",
            ExpectedValue = "9.1.0.0"
        });

        Assert.Equal("greaterThanOrEqual", (string?)rule["operator"]);
    }

    [Theory]
    [InlineData("RegistryKeyValue", null, "registry value")]
    [InlineData("File", "Version", "file version")]
    public void UnknownOperator_ThrowsNamingTheOperator(string type, string? propertyType, string context)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => IntunePublishService.BuildRules(new StageDetection
        {
            Type = type,
            PropertyType = propertyType,
            RegistryKeyRelative = @"SOFTWARE\Contoso",
            FilePath = @"C:\Program Files\Contoso",
            FileName = "app.exe",
            ExpectedValue = "1.0",
            Operator = "OneOf"
        }));

        Assert.Contains("'OneOf'", ex.Message);
        Assert.Contains(context, ex.Message);
    }

    // -- Prefix, suffix and substring comparisons --------------------------

    private static string ScriptOf(JsonObject rule) =>
        Encoding.UTF8.GetString(Convert.FromBase64String((string)rule["scriptContent"]!));

    [Fact]
    public void RegistryKeyValue_BeginsWith_MapsToAScriptRuleKeepingThePrefixComparison()
    {
        var rule = Single(new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            ValueName = "ProductName",
            ExpectedValue = "Windows",
            Operator = "BeginsWith",
            Is64Bit = true
        });

        Assert.Equal("#microsoft.graph.win32LobAppPowerShellScriptRule", (string?)rule["@odata.type"]);
        Assert.Equal("detection", (string?)rule["ruleType"]);
        Assert.Equal("notConfigured", (string?)rule["operationType"]);
        Assert.Equal("notConfigured", (string?)rule["operator"]);
        Assert.False((bool)rule["enforceSignatureCheck"]!);
        Assert.False((bool)rule["runAs32Bit"]!);
        Assert.Equal(string.Join("\r\n",
            "$base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)",
            @"$key = $base.OpenSubKey('SOFTWARE\Microsoft\Windows NT\CurrentVersion')",
            "if ($key) {",
            "    $value = [string]$key.GetValue('ProductName')",
            "    if ($value.StartsWith('Windows', [System.StringComparison]::OrdinalIgnoreCase)) { Write-Output 'Detected'; exit 0 }",
            "}",
            "exit 1"), ScriptOf(rule));
    }

    [Fact]
    public void RegistryKeyValue_EndsWith_ComparesTheSuffix()
    {
        var rule = Single(new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"SOFTWARE\Contoso",
            ExpectedValue = ".0",
            Operator = "EndsWith"
        });

        Assert.Contains("$value.EndsWith('.0', [System.StringComparison]::OrdinalIgnoreCase)", ScriptOf(rule));
    }

    [Fact]
    public void RegistryKeyValue_Contains_ComparesWithIndexOf()
    {
        // String.Contains(string, StringComparison) is absent from .NET Framework.
        var rule = Single(new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"SOFTWARE\Contoso",
            ExpectedValue = "beta",
            Operator = "Contains"
        });

        Assert.Contains("$value.IndexOf('beta', [System.StringComparison]::OrdinalIgnoreCase) -ge 0", ScriptOf(rule));
    }

    [Fact]
    public void RegistryValueScriptRule_TakesTheHiveAndViewFromTheDetection()
    {
        var rule = Single(new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"SOFTWARE\O'Vendor",
            ValueName = "DisplayVersion",
            ExpectedValue = "17.6",
            Operator = "BeginsWith",
            Hive = "CurrentUser",
            Is64Bit = false
        });

        var script = ScriptOf(rule);
        Assert.Contains("[Microsoft.Win32.RegistryHive]::CurrentUser", script);
        Assert.Contains("[Microsoft.Win32.RegistryView]::Registry32", script);
        Assert.Contains(@"$key = $base.OpenSubKey('SOFTWARE\O''Vendor')", script);
    }

    // -- Compound detections -----------------------------------------------

    [Fact]
    public void CompoundAnd_MapsOneRulePerClause()
    {
        var rules = IntunePublishService.BuildRules(new StageDetection
        {
            Type = "Compound",
            Connector = "And",
            Clauses =
            [
                new() { Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\Contoso", ExpectedValue = "1.2.3.4" },
                new() { Type = "File", PropertyType = "Version", FilePath = @"C:\Program Files\Contoso", FileName = "app.exe", ExpectedValue = "1.2.3.4" }
            ]
        });

        Assert.Equal(2, rules.Count);
        Assert.Equal("#microsoft.graph.win32LobAppRegistryRule", (string?)rules[0]!["@odata.type"]);
        Assert.Equal("#microsoft.graph.win32LobAppFileSystemRule", (string?)rules[1]!["@odata.type"]);
    }

    [Fact]
    public void CompoundOr_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => IntunePublishService.BuildRules(new StageDetection
        {
            Type = "Compound",
            Connector = "Or",
            Clauses =
            [
                new() { Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\A", ExpectedValue = "1" },
                new() { Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\B", ExpectedValue = "1" }
            ]
        }));

        Assert.Contains("OR-connected", ex.Message);
    }

    [Fact]
    public void CompoundWithAScriptClauseAlongsideAnother_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => IntunePublishService.BuildRules(new StageDetection
        {
            Type = "Compound",
            Clauses =
            [
                new() { Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\A", ExpectedValue = "1", Operator = "BeginsWith" },
                new() { Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\B", ExpectedValue = "1" }
            ]
        }));

        Assert.Contains("script rule", ex.Message);
    }
}
