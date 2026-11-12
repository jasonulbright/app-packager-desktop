using System.Text;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public class IntuneRequirementRuleTests
{
    private static readonly ConditionTemplateDocument Defaults = ConditionTemplateService.Defaults();

    private static IntuneRequirementTranslation One(StageRequirement spec, ConditionTemplateDocument? doc = null) =>
        IntuneRequirementRules.Translate([spec], doc ?? Defaults).Single();

    private static string Script(IntuneRequirementTranslation t) =>
        Encoding.UTF8.GetString(Convert.FromBase64String((string)t.Rule!["scriptContent"]!));

    private static StageManifest Manifest(params StageRequirement[] requirements) => new()
    {
        AppName = "Contoso Tool",
        Architecture = "x64",
        Detection = new StageDetection { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\Contoso" },
        Requirements = requirements
    };

    [Fact]
    public void TheCpuArchitectureConditionBecomesAScriptRuleThatComparesTheProcessorArchitecture()
    {
        var t = One(new StageRequirement { ConditionId = "cpu-arch", Value = "ARM64" });

        Assert.True(t.Translated, t.Gap);
        var rule = t.Rule!;
        Assert.Equal("#microsoft.graph.win32LobAppPowerShellScriptRule", (string?)rule["@odata.type"]);
        Assert.Equal("requirement", (string?)rule["ruleType"]);
        Assert.Equal("APKG - CPU Architecture", (string?)rule["displayName"]);
        Assert.Equal("string", (string?)rule["operationType"]);
        Assert.Equal("equal", (string?)rule["operator"]);
        Assert.Equal("True", (string?)rule["comparisonValue"]);
        Assert.Equal("system", (string?)rule["runAsAccount"]);
        Assert.False((bool)rule["runAs32Bit"]!);
        Assert.False((bool)rule["enforceSignatureCheck"]!);
        var script = Script(t);
        Assert.Contains("-Namespace 'root\\cimv2' -ClassName 'Win32_Processor'", script);
        Assert.Contains("[string]$_.Architecture", script);
        Assert.Contains("$values -contains '12'", script);
    }

    [Fact]
    public void TheX64ValueIsMappedLikeTheConfigMgrRule()
    {
        Assert.Contains("$values -contains '9'", Script(One(new StageRequirement { ConditionId = "cpu-arch", Value = "x64" })));
    }

    [Fact]
    public void TheOperatingSystemLanguageConditionListsTheCultures()
    {
        var t = One(new StageRequirement { ConditionId = "os-language", Cultures = ["de-DE", "fr-FR"] });

        Assert.True(t.Translated, t.Gap);
        var script = Script(t);
        Assert.Contains("$cultures = @('de-DE', 'fr-FR')", script);
        Assert.Contains("InstalledUICulture.Name", script);
        Assert.Contains("$cultures -contains $name", script);
        Assert.Equal("True", (string?)t.Rule!["comparisonValue"]);
    }

    [Theory]
    [InlineData(true, "True")]
    [InlineData(false, "False")]
    public void TheVpnConditionRunsItsScriptAndExpectsTheValueOfTheSpec(bool value, string expected)
    {
        var t = One(new StageRequirement { ConditionId = "vpn-connected", Value = value });

        Assert.True(t.Translated, t.Gap);
        Assert.Equal(expected, (string?)t.Rule!["comparisonValue"]);
        Assert.Equal(ConditionTemplateService.ResolveScriptText(Defaults.Conditions.Single(c => c.Id == "vpn-connected")), Script(t));
    }

    [Theory]
    [InlineData("no-such-condition", "not in the condition templates")]
    [InlineData("", "not in the condition templates")]
    public void AnUnknownConditionHasAGapThatNamesIt(string id, string reason)
    {
        var t = One(new StageRequirement { ConditionId = id, Value = "x" });

        Assert.False(t.Translated);
        Assert.Contains(reason, t.Gap);
    }

    [Fact]
    public void AValueThatTheTemplateDoesNotMapHasAGap()
    {
        var t = One(new StageRequirement { ConditionId = "cpu-arch", Value = "mips" });

        Assert.False(t.Translated);
        Assert.Contains("mips", t.Gap);
    }

    [Fact]
    public void ACultureListThatIsEmptyHasAGap()
    {
        Assert.False(One(new StageRequirement { ConditionId = "os-language", Cultures = [] }).Translated);
    }

    [Theory]
    [InlineData("BuiltIn", "CommonValue")]
    [InlineData("Wql", "Boolean")]
    [InlineData("Script", "CommonValue")]
    [InlineData("Wql", "OperatingSystemLanguage")]
    public void AConditionOfAKindThatHasNoScriptFormHasAGap(string kind, string ruleType)
    {
        var doc = new ConditionTemplateDocument
        {
            Conditions = [new ConditionTemplate { Id = "custom", GlobalConditionName = "Custom", Kind = kind, RuleType = ruleType, Namespace = "root\\cimv2", Class = "C", Property = "P" }]
        };

        var t = One(new StageRequirement { ConditionId = "custom", Value = true, Cultures = ["de-DE"] }, doc);

        Assert.False(t.Translated);
        Assert.Contains("no Intune requirement rule", t.Gap);
    }

    [Fact]
    public void AWmiPropertyNameThatIsNotAPlainNameHasAGapAndNeverReachesAScript()
    {
        var doc = new ConditionTemplateDocument
        {
            Conditions = [new ConditionTemplate { Id = "evil", GlobalConditionName = "Evil", Kind = "Wql", RuleType = "CommonValue", Namespace = "root\\cimv2", Class = "Win32_Processor", Property = "Architecture; calc" }]
        };

        var t = One(new StageRequirement { ConditionId = "evil", Value = "1" }, doc);

        Assert.False(t.Translated);
        Assert.Contains("not a plain name", t.Gap);
    }

    [Fact]
    public void AValueWithAQuoteIsEscapedInTheScript()
    {
        var doc = new ConditionTemplateDocument
        {
            Conditions = [new ConditionTemplate { Id = "q", GlobalConditionName = "Q", Kind = "Wql", RuleType = "CommonValue", Namespace = "root\\cimv2", Class = "Win32_Processor", Property = "Name" }]
        };

        var script = Script(One(new StageRequirement { ConditionId = "q", Value = "it's" }, doc));

        Assert.Contains("$values -contains 'it''s'", script);
    }

    [Fact]
    public void ALongConditionNameIsClippedToTheLengthOfAGraphDisplayName()
    {
        var doc = new ConditionTemplateDocument
        {
            Conditions = [new ConditionTemplate { Id = "l", GlobalConditionName = new string('N', 300), Kind = "Wql", RuleType = "CommonValue", Namespace = "root\\cimv2", Class = "Win32_Processor", Property = "Architecture", Values = new() { ["a"] = "1" } }]
        };

        Assert.Equal(100, ((string)One(new StageRequirement { ConditionId = "l", Value = "a" }, doc).Rule!["displayName"]!).Length);
    }

    // ---- findings ----

    private static string[] Codes(IReadOnlyList<IntuneCompatibilityFinding> findings) =>
        [.. findings.Where(f => f.Code.StartsWith("Requirement")).Select(f => $"{f.Severity}:{f.Code}")];

    [Fact]
    public void ATranslatedRequirementIsInformationOnlyAndBlocksNothing()
    {
        var findings = IntuneCompatibilityFindings.Build(Manifest(new StageRequirement { ConditionId = "cpu-arch", Value = "x64" }));

        Assert.Equal(["Info:RequirementTranslated"], Codes(findings));
        Assert.Null(IntuneCompatibilityFindings.RefusalMessage(findings));
    }

    [Fact]
    public void ARequirementThatTheRunAppliesBesideTheManifestIsTranslatedToo()
    {
        var findings = IntuneCompatibilityFindings.Build(Manifest(), [new StageRequirement { ConditionId = "vpn-connected", Value = true }]);

        Assert.Equal(["Info:RequirementTranslated"], Codes(findings));
    }

    [Fact]
    public void ARequirementWithNoIntuneRuleBlocksThePublish()
    {
        var findings = IntuneCompatibilityFindings.Build(Manifest(new StageRequirement { ConditionId = "no-such-condition" }));

        Assert.Equal(["Blocking:RequirementNotTranslatable"], Codes(findings));
        var refusal = IntuneCompatibilityFindings.RefusalMessage(findings);
        Assert.NotNull(refusal);
        Assert.Contains("no-such-condition", refusal);
    }

    [Fact]
    public void TheAcceptedDropOfThatRuleForThatApplicationTurnsTheBlockIntoAReviewFinding()
    {
        var m = Manifest(new StageRequirement { ConditionId = "no-such-condition" });
        var findings = IntuneCompatibilityFindings.Build(m, null, null, [IntuneRequirementPlan.DropKey(Manifest(), "no-such-condition")]);

        Assert.Equal(["Review:RequirementDropped"], Codes(findings));
        Assert.Null(IntuneCompatibilityFindings.RefusalMessage(findings));
    }

    [Theory]
    [InlineData("AppPackager:legacy:Other-Tool/default|no-such-condition")]
    [InlineData("AppPackager:legacy:Contoso-Tool/default|other-condition")]
    [InlineData("Contoso Tool|no-such-condition")]
    [InlineData("no-such-condition")]
    public void AnAcceptedDropOfAnotherRuleOrAnotherApplicationDoesNotCount(string key)
    {
        var findings = IntuneCompatibilityFindings.Build(Manifest(new StageRequirement { ConditionId = "no-such-condition" }), null, null, [key]);

        Assert.Equal(["Blocking:RequirementNotTranslatable"], Codes(findings));
    }

    [Fact]
    public void TheAcceptedDropIsCaseInsensitive()
    {
        var key = IntuneRequirementPlan.DropKey(Manifest(), "no-such-condition").ToUpperInvariant();
        var findings = IntuneCompatibilityFindings.Build(Manifest(new StageRequirement { ConditionId = "no-such-condition" }), null, null, [key]);

        Assert.Equal(["Review:RequirementDropped"], Codes(findings));
    }

    [Fact]
    public void TheBlockingFindingPrintsTheExactEntryToAccept()
    {
        var m = Manifest(new StageRequirement { ConditionId = "no-such-condition" });
        var blocking = IntuneCompatibilityFindings.Build(m).Single(f => f.Code == "RequirementNotTranslatable");

        Assert.Contains(IntuneRequirementPlan.DropKey(m, "no-such-condition"), blocking.Message);
        Assert.Contains("Intune.AcceptedRequirementDrops", blocking.Message);
    }

    [Fact]
    public void TheBlockingFindingCarriesTheKeyThatTheAcceptButtonStores()
    {
        var m = Manifest(new StageRequirement { ConditionId = "no-such-condition" });
        var blocking = IntuneCompatibilityFindings.Build(m).Single(f => f.Code == "RequirementNotTranslatable");

        Assert.Equal(IntuneRequirementPlan.DropKey(m, "no-such-condition"), blocking.DropKey);
        Assert.True(blocking.CanAcceptDrop);
        // The stored key turns the blocking finding into the accepted one.
        var accepted = IntuneCompatibilityFindings.Build(m, null, null, [blocking.DropKey!]);
        Assert.Equal(["Review:RequirementDropped"], Codes(accepted));
        Assert.All(accepted, f => Assert.False(f.CanAcceptDrop));
    }

    [Fact]
    public void TheKeyDoesNotChangeWithTheVersionOrTheTitleOfAProfiledApplication()
    {
        var a = Manifest();
        a.ApplicationId = "app-1";
        a.AppName = "Contoso Tool - 1.0 (x64)";
        var b = Manifest();
        b.ApplicationId = "app-1";
        b.AppName = "Contoso Tool - 2.0 (x64)";

        Assert.Equal(IntuneRequirementPlan.DropKey(a, "vpn-connected"), IntuneRequirementPlan.DropKey(b, "vpn-connected"));
        b.ProfileId = "pilot";
        Assert.NotEqual(IntuneRequirementPlan.DropKey(a, "vpn-connected"), IntuneRequirementPlan.DropKey(b, "vpn-connected"));
    }

    [Fact]
    public void ACultureCodeThatDoesNotExistIsAGapAndNotAnException()
    {
        var t = One(new StageRequirement { ConditionId = "os-language", Cultures = ["german"] });

        Assert.False(t.Translated);
        Assert.False(string.IsNullOrWhiteSpace(t.Gap));
        var findings = IntuneCompatibilityFindings.Build(Manifest(new StageRequirement { ConditionId = "os-language", Cultures = ["de DE"] }));
        Assert.Equal(["Blocking:RequirementNotTranslatable"], Codes(findings));
    }

    [Fact]
    public void TheSavedConditionsOfARunReplaceTheRequirementRulesOfTheManifest()
    {
        var m = Manifest(new StageRequirement { ConditionId = "cpu-arch", Value = "x64" });
        var plan = IntuneRequirementPlan.Of([new StageRequirement { ConditionId = "cpu-arch", Value = "ARM64" }], null, null);

        Assert.Equal(["ARM64"], plan.AllSpecs(m).Select(s => s.Value?.ToString()));
        Assert.Equal(["x64"], IntuneRequirementPlan.Of(null, null, null).AllSpecs(m).Select(s => s.Value?.ToString()));
    }

    [Fact]
    public void WithoutASigningPolicyAScriptConditionIsPublishedUnsignedAndNotEnforced()
    {
        var t = One(new StageRequirement { ConditionId = "vpn-connected", Value = true });

        Assert.False((bool)t.Rule!["enforceSignatureCheck"]!);
    }

    [Fact]
    public void ARequiredRequirementSignatureThatIsNotProducedIsNotAGapThatADropCouldAccept()
    {
        var policy = new ApplicationPackagerEngine.Models.Signing.SigningPolicy { RequireRequirements = true };

        Assert.Throws<IntuneRequirementSigningException>(() =>
            IntuneRequirementRules.Translate([new StageRequirement { ConditionId = "vpn-connected", Value = true }], Defaults, policy));
    }

    [Fact]
    public void TheScriptsThatTheProgramWritesItselfAreNotSignedUnderARequiredPolicy()
    {
        var policy = new ApplicationPackagerEngine.Models.Signing.SigningPolicy { RequireRequirements = true };

        var t = IntuneRequirementRules.Translate([new StageRequirement { ConditionId = "cpu-arch", Value = "x64" }], Defaults, policy).Single();

        Assert.True(t.Translated, t.Gap);
        Assert.False((bool)t.Rule!["enforceSignatureCheck"]!);
    }

    [Fact]
    public void OneAcceptedDropDoesNotHideASecondRuleThatHasNoRule()
    {
        var m = Manifest(new StageRequirement { ConditionId = "no-such-condition" }, new StageRequirement { ConditionId = "another-missing" });
        var findings = IntuneCompatibilityFindings.Build(m, null, null, [IntuneRequirementPlan.DropKey(Manifest(), "no-such-condition")]);

        Assert.Equal(["Review:RequirementDropped", "Blocking:RequirementNotTranslatable"], Codes(findings));
    }
}
