using System.Text;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A Script detection travels from the shipped definition through staging into a Graph
/// rule. The staged body lives in ScriptText, so a mapping that reads ExpectedValue
/// publishes either nothing or the comparison value MSIX staging writes beside it.
/// These tests run the whole path and decode what Graph would receive.
/// </summary>
public class ScriptDetectionIntunePortTests
{
    private static string ScriptOf(JsonObject rule) =>
        Encoding.UTF8.GetString(Convert.FromBase64String((string)rule["scriptContent"]!));

    private static JsonObject MapStaged(AppDefinition def, string version)
    {
        var staged = StagingService.BuildDetection(def, null, version);
        return (JsonObject)IntunePublishService.BuildRules(staged)[0]!;
    }

    /// <summary>An ARP-key scan across both registry views, the shape a definition script takes.</summary>
    private static AppDefinition ArpScanDefinition() => new()
    {
        Application = "Test App",
        CMName = "Test App",
        InstallerType = "EXE",
        Detection = new DetectionConfig
        {
            Type = "Script",
            ScriptLanguage = "PowerShell",
            ScriptText = "$keys = @(\n    'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\ocenaudio',\n    'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\ocenaudio'\n)\n$wanted = [version]'{version}'\nforeach ($key in $keys) {\n    if (-not (Test-Path -LiteralPath $key)) { continue }\n    $entry = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue\n    if (-not $entry) { continue }\n    $found = $null\n    if (-not [version]::TryParse([string]$entry.DisplayVersion, [ref]$found)) { continue }\n    if ($found -ge $wanted) { Write-Output 'Installed'; exit 0 }\n}\nexit 0"
        }
    };

    [Theory]
    [InlineData("3.14.5")]
    [InlineData("5.9")]
    [InlineData("4.0.0.2")]
    public void AScriptDefinitionSurvivesStagingIntoTheGraphRule(string version)
    {
        var def = ArpScanDefinition();
        var staged = StagingService.BuildDetection(def, null, version);

        // Staging is what puts the body in ScriptText; ExpectedValue stays empty for
        // a definition that carries no comparison value.
        Assert.Equal("Script", staged.Type);
        Assert.False(string.IsNullOrWhiteSpace(staged.ScriptText));

        var rule = (JsonObject)IntunePublishService.BuildRules(staged)[0]!;

        Assert.Equal("#microsoft.graph.win32LobAppPowerShellScriptRule", (string?)rule["@odata.type"]);
        Assert.Equal(staged.ScriptText, ScriptOf(rule));
    }

    /// <summary>
    /// The staged body carries the version that was staged, so the published script
    /// compares against that build and not against the token.
    /// </summary>
    [Fact]
    public void TheStagedVersionReachesThePublishedScript()
    {
        var rule = MapStaged(ArpScanDefinition(), "3.14.5");
        var script = ScriptOf(rule);

        Assert.Contains("$wanted = [version]'3.14.5'", script);
        Assert.DoesNotContain("{version}", script);
        Assert.Contains("Uninstall\\ocenaudio", script);
        Assert.Contains("Write-Output 'Installed'", script);
    }

    /// <summary>The body is the definition's own script, not a rebuilt approximation.</summary>
    [Fact]
    public void ThePublishedScriptIsTheDefinitionBodyWithTheTokenSubstituted()
    {
        var def = ArpScanDefinition();
        var expected = def.Detection.ScriptText!.Replace("{version}", "3.14.5", StringComparison.OrdinalIgnoreCase);

        Assert.Equal(expected, ScriptOf(MapStaged(def, "3.14.5")));
    }

    /// <summary>
    /// An MSIX definition never reaches the generic detection builder: staging replaces
    /// the definition's detection with a generated provisioning-store query. That builder
    /// writes the package version to ExpectedValue beside the body, so reading
    /// ExpectedValue would publish the bare version as the detection script.
    /// </summary>
    [Fact]
    public void TheGeneratedMsixDetectionPublishesItsScriptRatherThanItsComparisonValue()
    {
        var package = new StageMsixPackage
        {
            Name = "ohmyposh.cli",
            Version = "31.1.1.0",
            Publisher = "CN=Vendor",
            ProcessorArchitecture = "x64",
            SignerThumbprint = "AABBCC"
        };

        var staged = StagingService.BuildMsixScriptDetection(package);

        // The builder populates both fields; the comparison value is the trap.
        Assert.Equal("31.1.1.0", staged.ExpectedValue);
        Assert.False(string.IsNullOrWhiteSpace(staged.ScriptText));

        var script = ScriptOf((JsonObject)IntunePublishService.BuildRules(staged)[0]!);

        Assert.Equal(staged.ScriptText, script);
        Assert.NotEqual("31.1.1.0", script);
        Assert.Contains("$wanted = [version]'31.1.1.0'", script);
        Assert.Contains("Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue", script);
        Assert.Contains("$_.DisplayName -eq 'ohmyposh.cli'", script);
        Assert.Contains("if ($found -ge $wanted) { Write-Output 'Installed'; exit 0 }", script);
    }

    /// <summary>An x86 package maps to a rule that checks the 32-bit view.</summary>
    [Fact]
    public void TheGeneratedMsixDetectionForAnX86PackageStillCarriesItsScript()
    {
        var package = new StageMsixPackage
        {
            Name = "Contoso.App",
            Version = "2.0.0.0",
            Publisher = "CN=Contoso",
            ProcessorArchitecture = "x86",
            SignerThumbprint = "DDEEFF"
        };

        var staged = StagingService.BuildMsixScriptDetection(package);
        var script = ScriptOf((JsonObject)IntunePublishService.BuildRules(staged)[0]!);

        Assert.False(staged.Is64Bit);
        Assert.Equal(staged.ScriptText, script);
        Assert.Contains("$_.DisplayName -eq 'Contoso.App'", script);
    }

    /// <summary>
    /// The generic builder guards a non-MSIX Script definition that declares no body, so
    /// an empty script is refused at staging rather than published as an empty rule.
    /// </summary>
    [Fact]
    public void ANonMsixScriptDefinitionWithNoBodyIsRefusedAtStaging()
    {
        var def = ArpScanDefinition();
        def.Detection.ScriptText = "   ";

        var ex = Assert.Throws<InvalidOperationException>(
            () => StagingService.BuildDetection(def, null, "3.14.5"));
        Assert.Contains("ScriptText", ex.Message);
    }
}
