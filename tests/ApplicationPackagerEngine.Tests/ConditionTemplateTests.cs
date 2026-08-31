using System.Text;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public sealed class ConditionTemplateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "packr-cond-" + Guid.NewGuid().ToString("N"));

    public ConditionTemplateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private ConditionTemplateService Service() =>
        new(Path.Combine(_root, "condition-templates.json"));

    [Fact]
    public void Defaults_CarryTheThreeShippedConditions()
    {
        var doc = ConditionTemplateService.Defaults();

        var arch = doc.Conditions.Single(c => c.Id == "cpu-arch");
        Assert.Equal("Wql", arch.Kind);
        Assert.Equal("Win32_Processor", arch.Class);
        Assert.Equal("Architecture", arch.Property);
        Assert.Equal("9", arch.Values!["x64"]);
        Assert.Equal("12", arch.Values["ARM64"]);

        var lang = doc.Conditions.Single(c => c.Id == "os-language");
        Assert.Equal("BuiltIn", lang.Kind);
        Assert.Equal("Operating System Language", lang.GlobalConditionName);
        Assert.Equal(1, lang.PlatformType);

        var vpn = doc.Conditions.Single(c => c.Id == "vpn-connected");
        Assert.Equal("Script", vpn.Kind);
        Assert.Equal("Boolean", vpn.RuleType);
    }

    [Fact]
    public void Load_MissingFile_FallsBackToDefaults()
    {
        var warnings = new List<string>();
        var doc = Service().Load(warnings.Add);

        Assert.Equal(3, doc.Conditions.Length);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Load_MalformedFile_WarnsAndFallsBackToDefaults()
    {
        var service = Service();
        File.WriteAllText(service.TemplatesPath, "{ not json");
        var warnings = new List<string>();

        var doc = service.Load(warnings.Add);

        Assert.Equal(3, doc.Conditions.Length);
        Assert.Contains(warnings, w => w.Contains("unreadable"));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAnOverride()
    {
        var service = Service();
        var doc = ConditionTemplateService.Defaults();
        doc.Conditions.Single(c => c.Id == "vpn-connected").AdapterPatterns = ["Contoso VPN"];
        service.Save(doc);

        var reloaded = service.Load();
        Assert.Equal(["Contoso VPN"], reloaded.Conditions.Single(c => c.Id == "vpn-connected").AdapterPatterns!);
    }

    [Fact]
    public void VpnScriptText_QuotesPatterns_AndEmitsExactlyOneBoolean()
    {
        var text = ConditionTemplateService.BuildVpnScriptText(["Zscaler", " ", "O'Brien VPN"], "vpn");

        Assert.Contains("$patterns = @('Zscaler', 'O''Brien VPN')", text);
        Assert.Contains("Win32_NetworkAdapterConfiguration -Filter \"IPEnabled='True'\"", text);
        Assert.Contains("$ip.InterfaceAlias -like '*vpn*'", text);
        Assert.EndsWith("$found", text);
        Assert.Equal("$found", text.Split("\r\n").Last());
    }

    [Fact]
    public void VpnScriptText_WithoutAliasPattern_OmitsTheAliasPass()
    {
        var text = ConditionTemplateService.BuildVpnScriptText(["Zscaler"], "");

        Assert.DoesNotContain("InterfaceAlias", text);
        Assert.EndsWith("$found", text);
    }

    [Fact]
    public void ResolveScriptText_PrefersExplicitScriptText()
    {
        var template = new ConditionTemplate { Kind = "Script", ScriptText = ["$a = 1", "$a -eq 1"] };

        Assert.Equal("$a = 1\r\n$a -eq 1", ConditionTemplateService.ResolveScriptText(template));
    }

    [Fact]
    public void Resolve_MapsArchitectureValueAndThrowsOnUnknownOnes()
    {
        var doc = ConditionTemplateService.Defaults();

        var resolved = RequirementRuleBuilder.Resolve(
            [new StageRequirement { ConditionId = "cpu-arch", Value = "ARM64" }], doc);
        Assert.Equal("12", Assert.Single(resolved).Value);

        var ex = Assert.Throws<InvalidOperationException>(() => RequirementRuleBuilder.Resolve(
            [new StageRequirement { ConditionId = "cpu-arch", Value = "x86" }], doc));
        Assert.Contains("no mapping for value 'x86'", ex.Message);
    }

    [Fact]
    public void Resolve_UnknownConditionId_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RequirementRuleBuilder.Resolve(
            [new StageRequirement { ConditionId = "no-such-condition" }], ConditionTemplateService.Defaults()));
        Assert.Contains("unknown condition id", ex.Message);
    }

    [Fact]
    public void Resolve_OperatingSystemLanguageWithoutCultures_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RequirementRuleBuilder.Resolve(
            [new StageRequirement { ConditionId = "os-language", Cultures = [] }], ConditionTemplateService.Defaults()));
        Assert.Contains("at least one culture code", ex.Message);
    }

    [Fact]
    public void Resolve_BooleanValue_ParsesStringFormAndRejectsNonBooleans()
    {
        var doc = ConditionTemplateService.Defaults();

        // [bool]'false' is true in PowerShell; the string form must parse.
        var resolved = RequirementRuleBuilder.Resolve(
            [new StageRequirement { ConditionId = "vpn-connected", Value = "false" }], doc);
        Assert.False(Assert.Single(resolved).BooleanValue);

        Assert.Throws<InvalidOperationException>(() => RequirementRuleBuilder.Resolve(
            [new StageRequirement { ConditionId = "vpn-connected", Value = "yes" }], doc));
    }

    [Fact]
    public void GlobalConditionFunction_NarrowsByPlatformTypeAndReadsBackAfterCreation()
    {
        var text = RequirementRuleBuilder.BuildGlobalConditionFunction();

        Assert.Contains("[int]$_.PlatformType -eq $PlatformType", text);
        Assert.Contains("matches $($found.Count) site conditions", text);
        Assert.Contains("$created = @(Get-CMGlobalCondition -Name $Name)", text);
        Assert.Contains("the provider refused the write", text);
        Assert.Contains("Built-in global condition '$Name' was not found on the site.", text);
    }

    [Fact]
    public void AppendRules_EmitsTheThreeRuleCmdletShapes()
    {
        var doc = ConditionTemplateService.Defaults();
        var resolved = RequirementRuleBuilder.Resolve(
        [
            new StageRequirement { ConditionId = "cpu-arch", Value = "x64" },
            new StageRequirement { ConditionId = "os-language", Cultures = ["de-DE", "fr-FR"] },
            new StageRequirement { ConditionId = "vpn-connected", Value = true }
        ], doc);

        var sb = new StringBuilder();
        var names = RequirementRuleBuilder.AppendRules(sb, resolved, "dt0");
        var text = sb.ToString();

        Assert.Equal(["$dt0_req0", "$dt0_req1", "$dt0_req2"], names);
        Assert.Contains("New-CMRequirementRuleCommonValue -InputObject $dt0_gc0 -RuleOperator IsEquals -Value1 '9'", text);
        Assert.Contains("New-CMRequirementRuleOperatingSystemLanguageValue -InputObject $dt0_gc1 -RuleOperator OneOf -Culture @([System.Globalization.CultureInfo]::GetCultureInfo('de-DE'), [System.Globalization.CultureInfo]::GetCultureInfo('fr-FR'))", text);
        Assert.Contains("New-CMRequirementRuleBooleanValue -InputObject $dt0_gc2 -Value $true", text);
        // The built-in language condition carries its platform narrowing.
        Assert.Contains("-Name 'Operating System Language' -PlatformType 1 -Kind 'BuiltIn'", text);
        Assert.Contains("-PlatformType 0 -Kind 'Wql'", text);
        // The VPN script body travels inline with the creation call.
        Assert.Contains("$patterns = @(''Zscaler''", text);
    }
}
