using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Supersedence;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The version rule, the Exact guard, the settings resolution, the blocked combination and the identity marker.</summary>
public sealed class SupersedenceVersionRuleTests
{
    private const string Version = "3.7.7";

    private static StageDetection File(string op = "GreaterEquals", string value = Version, string? propertyType = null) => new()
    {
        Type = "File", FilePath = @"%ProgramFiles%\Vendor", FileName = "app.exe", ExpectedValue = value, Operator = op, PropertyType = propertyType
    };

    private static StageDetection Registry(string op = "GreaterEquals", string value = Version, string key = @"SOFTWARE\Vendor\App") => new()
    {
        Type = "RegistryKeyValue", RegistryKeyRelative = key, ValueName = "DisplayVersion", ExpectedValue = value, Operator = op, PropertyType = "String"
    };

    private static StageManifest Manifest(StageDetection detection, string installerType = "EXE") => new()
    {
        AppName = "Vendor App", SoftwareVersion = Version, InstallerType = installerType, ApplicationId = "catalog:vendor-app",
        ProfileId = "default", Architecture = "x64", ProductCode = "{11111111-2222-3333-4444-555555555555}", InstallerFile = "setup.msi",
        Detection = detection
    };

    private static Func<StageDetection, ExactGuardVerdict> Allow => _ => ExactGuardVerdict.Allowed;

    // ---- the operator that the publish writes ----

    [Fact]
    public void ExactWritesIsEqualsToAVersionClauseAndMinimumChangesNothing()
    {
        var (exact, outcomes) = VersionRuleApplier.Apply(File(), Version, VersionRule.Exact, Allow);
        var (minimum, none) = VersionRuleApplier.Apply(File(), Version, VersionRule.Minimum, Allow);

        Assert.Equal("IsEquals", exact.Operator);
        Assert.Single(outcomes);
        Assert.Equal("GreaterEquals", minimum.Operator);
        Assert.Empty(none);
    }

    [Fact]
    public void AFileClauseWithNoOperatorIsTheDefaultGreaterEqualsAndChanges()
    {
        var detection = File(op: "");
        detection.Operator = null;

        Assert.Equal("IsEquals", VersionRuleApplier.Apply(detection, Version, VersionRule.Exact, Allow).Detection.Operator);
    }

    [Fact]
    public void AnMsiRegistryVersionClauseChangesLikeAnyOtherVersionClause()
    {
        var clause = Registry(key: @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{11111111-2222-3333-4444-555555555555}");

        Assert.Equal("IsEquals", VersionRuleApplier.Apply(clause, Version, VersionRule.Exact, Allow).Detection.Operator);
    }

    [Theory]
    [InlineData("IsEquals", Version)]
    [InlineData("GreaterThan", Version)]
    [InlineData("LessEquals", Version)]
    [InlineData("GreaterEquals", "9.9.9")]
    public void AClauseWithAnotherOperatorOrAnotherExpectedValueIsNotChanged(string op, string value)
    {
        var (changed, outcomes) = VersionRuleApplier.Apply(File(op, value), Version, VersionRule.Exact, Allow);

        Assert.Equal(op, changed.Operator);
        Assert.Empty(outcomes);
    }

    [Fact]
    public void AnExistenceClauseAndAScriptClauseAreNotChanged()
    {
        var existence = File(propertyType: "Existence");
        var script = new StageDetection { Type = "Script", ScriptText = "Write-Output 1", ExpectedValue = Version, Operator = "GreaterEquals" };

        Assert.Equal("GreaterEquals", VersionRuleApplier.Apply(existence, Version, VersionRule.Exact, Allow).Detection.Operator);
        Assert.Equal("GreaterEquals", VersionRuleApplier.Apply(script, Version, VersionRule.Exact, Allow).Detection.Operator);
    }

    [Fact]
    public void ACompoundDetectionChangesOnlyItsVersionClauses()
    {
        var compound = new StageDetection
        {
            Type = "Compound", Connector = "And",
            Clauses = [File(), File(op: "GreaterEquals", value: "other"), Registry(op: "IsEquals")]
        };

        var (changed, outcomes) = VersionRuleApplier.Apply(compound, Version, VersionRule.Exact, Allow);

        Assert.Equal(["IsEquals", "GreaterEquals", "IsEquals"], changed.Clauses!.Select(c => c.Operator));
        Assert.Single(outcomes);
    }

    [Fact]
    public void TheDefinitionIsNotChangedAndTheDeploymentTypeDetectionsAreCovered()
    {
        var manifest = Manifest(File());
        manifest.DeploymentTypes = [new StageDeploymentType { NameSuffix = "x64", Detection = Registry() }];

        var (copy, outcomes) = VersionRuleApplier.ApplyToManifest(manifest, VersionRule.Exact, Allow);

        Assert.Equal("GreaterEquals", manifest.Detection.Operator);
        Assert.Equal("GreaterEquals", manifest.DeploymentTypes[0].Detection!.Operator);
        Assert.Equal("IsEquals", copy.Detection.Operator);
        Assert.Equal("IsEquals", copy.DeploymentTypes![0].Detection!.Operator);
        Assert.Equal(2, outcomes.Count);
        Assert.Equal(manifest.ApplicationId, copy.ApplicationId);
    }

    // ---- the Exact guard ----

    private static ExactGuard Guard(string? msiVersion = null, Dictionary<string, string>? recorded = null, params string[] accepted) =>
        new(_ => msiVersion, recorded, accepted);

    [Fact]
    public void ExactIsAppliedOnlyWhenTheInstalledValueEqualsTheExpectedValue()
    {
        var manifest = Manifest(File());
        var recorded = new Dictionary<string, string> { [ExactGuard.RecordKey("catalog:vendor-app", Version)] = "3.7.7" };

        Assert.Equal(ClauseResult.ExactApplied, Guard(recorded: recorded).Evaluate(manifest, File(), "catalog:vendor-app").Result);
    }

    [Fact]
    public void AnInstalledValueWithMorePartsBlocksExact()
    {
        var manifest = Manifest(File());
        var recorded = new Dictionary<string, string> { [ExactGuard.RecordKey("catalog:vendor-app", Version)] = "3.7.7.1234" };

        var verdict = Guard(recorded: recorded).Evaluate(manifest, File(), "catalog:vendor-app");

        Assert.Equal(ClauseResult.ExactBlocked, verdict.Result);
        Assert.Contains("3.7.7.1234", verdict.Detail);
        Assert.Contains("3.7.7", verdict.Detail);
    }

    [Fact]
    public void NoInstalledValueBlocksExactAndSaysSo()
    {
        var verdict = Guard().Evaluate(Manifest(File()), File(), "catalog:vendor-app");

        Assert.Equal(ClauseResult.ExactBlocked, verdict.Result);
        Assert.Contains("No installed value", verdict.Detail);
    }

    [Fact]
    public void ZeroPartsAreNotIgnoredButSeparatorsAreNormalized()
    {
        Assert.False(ExactGuard.SameVersionText("3.7.7", "3.7.7.0"));
        Assert.True(ExactGuard.SameVersionText("3.7.7+1", "3.7.7.1"));
        Assert.True(ExactGuard.SameVersionText("3.7.7-2", "3.7.7.2"));
    }

    [Fact]
    public void TheMsiProductVersionIsTheInstalledValueOfTheMsiClause()
    {
        var clause = Registry(key: @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{11111111-2222-3333-4444-555555555555}");
        var manifest = Manifest(clause, installerType: "MSI");

        Assert.Equal(ClauseResult.ExactApplied, Guard(msiVersion: "3.7.7").Evaluate(manifest, clause, "catalog:vendor-app").Result);
        Assert.Equal(ClauseResult.ExactBlocked, Guard(msiVersion: "3.7.7.0").Evaluate(manifest, clause, "catalog:vendor-app").Result);
        Assert.Equal(ClauseResult.ExactBlocked, Guard(msiVersion: null).Evaluate(manifest, clause, "catalog:vendor-app").Result);
    }

    [Fact]
    public void AnAcceptedBlockHoldsForThatVersionOnlyAndWritesNoExact()
    {
        var accepted = ExactGuard.RecordKey("catalog:vendor-app", Version);
        var manifest = Manifest(File());

        Assert.Equal(ClauseResult.ExactAcceptedBlock, Guard(accepted: accepted).Evaluate(manifest, File(), "catalog:vendor-app").Result);

        var next = Manifest(File(value: "3.7.8"));
        next.SoftwareVersion = "3.7.8";
        Assert.Equal(ClauseResult.ExactBlocked, Guard(accepted: accepted).Evaluate(next, File(value: "3.7.8"), "catalog:vendor-app").Result);
    }

    // ---- the settings and the blocked combination ----

    [Fact]
    public void TheShippedDefaultsAreMinimumAndOff()
    {
        var settings = new SupersedenceSettings();

        Assert.Equal("Minimum", settings.ConfigMgrVersionRule);
        Assert.Equal("Minimum", settings.IntuneVersionRule);
        Assert.Equal("Off", settings.Mode);
        var resolved = SupersedenceResolver.Resolve(settings, "catalog:vendor-app", null);
        Assert.Equal(VersionRule.Minimum, resolved.ConfigMgrRule.Value);
        Assert.Equal(SupersedenceMode.Off, resolved.Mode.Value);
        Assert.Null(resolved.Blocked);
    }

    [Fact]
    public void EachSettingResolvesByItselfFromTheCommandLineThenTheTitleThenTheSite()
    {
        var settings = new SupersedenceSettings { ConfigMgrVersionRule = "Minimum", Mode = "Replace" };
        settings.TitleOverrides["catalog:vendor-app"] = new TitleSupersedenceOverride { VersionRule = "Exact" };

        var title = SupersedenceResolver.Resolve(settings, "catalog:vendor-app", null);
        var cli = SupersedenceResolver.Resolve(settings, "catalog:vendor-app", new SupersedenceCommandLine("minimum", "replace-uninstall"));
        var other = SupersedenceResolver.Resolve(settings, "catalog:other", null);

        Assert.Equal((VersionRule.Exact, SettingSource.TitleOverride), (title.ConfigMgrRule.Value, title.ConfigMgrRule.Source));
        Assert.Equal((SupersedenceMode.Replace, SettingSource.SiteDefault), (title.Mode.Value, title.Mode.Source));
        Assert.Equal((VersionRule.Minimum, SettingSource.CommandLine), (cli.ConfigMgrRule.Value, cli.ConfigMgrRule.Source));
        Assert.Equal((SupersedenceMode.ReplaceAndUninstall, SettingSource.CommandLine), (cli.Mode.Value, cli.Mode.Source));
        Assert.Equal((VersionRule.Minimum, SettingSource.SiteDefault), (other.ConfigMgrRule.Value, other.ConfigMgrRule.Source));
    }

    [Theory]
    [InlineData("Minimum", "ReplaceAndUninstall", true)]
    [InlineData("Exact", "ReplaceAndUninstall", false)]
    [InlineData("Minimum", "Replace", false)]
    [InlineData("Minimum", "Off", false)]
    public void ReplaceAndUninstallNeedsExact(string rule, string mode, bool blocked)
    {
        var text = SupersedenceResolver.BlockedCombination(Enum.Parse<VersionRule>(rule), Enum.Parse<SupersedenceMode>(mode));

        Assert.Equal(blocked, text is not null);
        if (blocked) Assert.StartsWith("Replace and uninstall needs the version rule Exact.", text);
    }

    [Fact]
    public void TheBlockIsCheckedAfterResolutionSoAnOverrideOfTheRuleAloneCanCauseIt()
    {
        var settings = new SupersedenceSettings { ConfigMgrVersionRule = "Exact", Mode = "Replace and uninstall" };
        settings.TitleOverrides["catalog:self-updating"] = new TitleSupersedenceOverride { VersionRule = "Minimum" };

        Assert.Null(SupersedenceResolver.Resolve(settings, "catalog:vendor-app", null).Blocked);
        Assert.NotNull(SupersedenceResolver.Resolve(settings, "catalog:self-updating", null).Blocked);
    }

    [Fact]
    public void AWordThatIsNotAllowedIsRefusedWithItsPlace()
    {
        var settings = new SupersedenceSettings { ConfigMgrVersionRule = "Sometimes" };

        var ex = Assert.Throws<InvalidOperationException>(() => SupersedenceResolver.Resolve(settings, "catalog:vendor-app", null));
        Assert.Contains("Sometimes", ex.Message);
        Assert.Contains("site default", ex.Message);
    }

    [Fact]
    public void ATitleWithoutADefinitionHasNoOverrideKey()
    {
        Assert.Equal("catalog:vendor-app", SupersedenceResolver.OverrideKey(" catalog:vendor-app "));
        Assert.Null(SupersedenceResolver.OverrideKey(""));
        Assert.Null(SupersedenceResolver.OverrideKey(null));
    }

    // ---- the plan of one publish ----

    [Fact]
    public void WithTheShippedDefaultsThePublishedScriptIsTheSameAndOnlyTheMarkerIsNew()
    {
        var manifest = Manifest(File());

        var plan = SupersedencePlanner.Plan(manifest, new SupersedenceSettings(), null, true, true, _ => null);

        Assert.Null(plan.Refusal);
        Assert.Equal("GreaterEquals", plan.IntuneManifest.Detection.Operator);
        Assert.Equal("APD-Line:catalog:vendor-app|default|x64", plan.Marker);
        // The published script of the copy equals the script of the original: the round trip of the manifest loses nothing.
        Assert.Equal(
            MecmTarget.BuildPackageScript(manifest, "Vendor App", @"\\s\c", "MCM", "", 15, 30),
            MecmTarget.BuildPackageScript(plan.ConfigMgrManifest, "Vendor App", @"\\s\c", "MCM", "", 15, 30));
    }

    [Fact]
    public void AGridStageOfTheDefaultProfileHasNoIdentifierSoTheRunGivesIt()
    {
        var manifest = Manifest(File());
        manifest.ApplicationId = "";
        var settings = new SupersedenceSettings();
        settings.TitleOverrides["catalog:vendor-app"] = new TitleSupersedenceOverride { Mode = "Replace" };

        var without = SupersedencePlanner.Plan(manifest, settings, null, true, false, _ => null);
        var with = SupersedencePlanner.Plan(manifest, settings, null, true, false, _ => null, "catalog:vendor-app");

        Assert.Null(without.Marker);
        Assert.Equal("APD-Line:catalog:vendor-app|default|x64", with.Marker);
        Assert.Contains(with.Log, l => l.Contains("Replace") && l.Contains("title override"));
    }

    [Fact]
    public void AnAcceptedBlockBlocksReplaceAndUninstallForTheTitle()
    {
        var settings = new SupersedenceSettings { ConfigMgrVersionRule = "Exact", Mode = "Replace and uninstall" };
        settings.AcceptedExactBlocks.Add(ExactGuard.RecordKey("catalog:vendor-app", Version));
        settings.TitleOverrides["catalog:vendor-app"] = new TitleSupersedenceOverride { Mode = "Replace and uninstall" };

        var plan = SupersedencePlanner.Plan(Manifest(File()), settings, null, true, false, _ => null);

        Assert.Contains("Replace and uninstall is blocked", plan.Refusal);
    }

    [Fact]
    public void ANullOverrideDictionaryFromAHandEditedFileDoesNotStopThePublish()
    {
        var settings = new SupersedenceSettings { TitleOverrides = null! };

        Assert.Null(SupersedencePlanner.Plan(Manifest(File()), settings, null, true, false, _ => null).Refusal);
    }

    [Fact]
    public void AnOverrideKeyMatchesWithoutRegardToCase()
    {
        var settings = new SupersedenceSettings { Mode = "Off" };
        settings.TitleOverrides["Catalog:Vendor-App"] = new TitleSupersedenceOverride { Mode = "Replace" };

        // The dictionary that a settings file load builds has the default comparer; the lookup must not depend on it.
        var loaded = new SupersedenceSettings { TitleOverrides = new Dictionary<string, TitleSupersedenceOverride>(settings.TitleOverrides) };

        Assert.Equal(SupersedenceMode.Replace, SupersedenceResolver.Resolve(loaded, "catalog:vendor-app", null).Mode.Value);
    }

    [Fact]
    public void ABlockedCombinationRefusesTheRowAndCreatesNothing()
    {
        var settings = new SupersedenceSettings { Mode = "Replace and uninstall" };

        var plan = SupersedencePlanner.Plan(Manifest(File()), settings, null, true, false, _ => null);

        Assert.Equal(SupersedenceResolver.BlockedText, plan.Refusal);
        Assert.Null(plan.Marker);
    }

    [Fact]
    public void AnIntuneOnlyRunIsNotBlockedByTheConfigMgrCombination()
    {
        var settings = new SupersedenceSettings { Mode = "Replace and uninstall" };

        Assert.Null(SupersedencePlanner.Plan(Manifest(File()), settings, null, false, true, _ => null).Refusal);
    }

    [Fact]
    public void ExactWithoutAnInstalledValueRefusesTheRowAndNamesTheWayOut()
    {
        var settings = new SupersedenceSettings { ConfigMgrVersionRule = "Exact" };

        var plan = SupersedencePlanner.Plan(Manifest(File()), settings, null, true, false, _ => null);

        Assert.Contains("No installed value", plan.Refusal);
        Assert.Contains("accept the block", plan.Refusal);
    }

    [Fact]
    public void ExactWithARecordedValueWritesIsEqualsToTheTargetThatAskedForIt()
    {
        var settings = new SupersedenceSettings { ConfigMgrVersionRule = "Exact", IntuneVersionRule = "Minimum" };
        settings.InstalledValues[ExactGuard.RecordKey("catalog:vendor-app", Version)] = "3.7.7";
        var manifest = Manifest(File());

        var plan = SupersedencePlanner.Plan(manifest, settings, null, true, true, _ => null);

        Assert.Null(plan.Refusal);
        Assert.Equal("IsEquals", plan.ConfigMgrManifest.Detection.Operator);
        Assert.Equal("GreaterEquals", plan.IntuneManifest.Detection.Operator);
        Assert.Equal("GreaterEquals", manifest.Detection.Operator);
    }

    [Fact]
    public void AnAcceptedBlockPublishesMinimumAndLogsIt()
    {
        var settings = new SupersedenceSettings { ConfigMgrVersionRule = "Exact" };
        settings.AcceptedExactBlocks.Add(ExactGuard.RecordKey("catalog:vendor-app", Version));

        var plan = SupersedencePlanner.Plan(Manifest(File()), settings, null, true, false, _ => null);

        Assert.Null(plan.Refusal);
        Assert.Equal("GreaterEquals", plan.ConfigMgrManifest.Detection.Operator);
        Assert.Contains(plan.Log, l => l.Contains("block accepted"));
    }

    [Fact]
    public void AnMsiTitleGetsExactFromTheProductVersionWithNoRecord()
    {
        var clause = Registry(key: @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{11111111-2222-3333-4444-555555555555}");
        var settings = new SupersedenceSettings { ConfigMgrVersionRule = "Exact" };

        var plan = SupersedencePlanner.Plan(Manifest(clause, "MSI"), settings, null, true, false, _ => "3.7.7");

        Assert.Null(plan.Refusal);
        Assert.Equal("IsEquals", plan.ConfigMgrManifest.Detection.Operator);
    }

    [Fact]
    public void ABadWordInTheSettingsRefusesTheRowWithTheMessage()
    {
        var plan = SupersedencePlanner.Plan(Manifest(File()), new SupersedenceSettings { Mode = "Maybe" }, null, true, false, _ => null);

        Assert.Contains("Maybe", plan.Refusal);
    }

    // ---- the identity marker ----

    [Fact]
    public void TheMarkerHasTheIdentityTheProfileAndTheArchitecture()
    {
        var manifest = Manifest(File());
        manifest.ProfileId = "pilot";
        manifest.Architecture = "ARM64";

        Assert.Equal("APD-Line:catalog:vendor-app|pilot|ARM64", IdentityMarker.For(manifest));
    }

    [Fact]
    public void ATitleWithoutADefinitionIdentifierHasNoMarker()
    {
        var manifest = Manifest(File());
        manifest.ApplicationId = "";

        Assert.Null(IdentityMarker.For(manifest));
    }

    [Fact]
    public void TheConfigMgrScriptWritesTheMarkerAfterTheApplicationExistsAndNeverRewritesKeywordsOfTheAdministrator()
    {
        var manifest = Manifest(File());

        var script = MecmTarget.BuildPackageScript(manifest, "Vendor App", @"\\s\c", "MCM", "", 15, 30, identityMarker: "APD-Line:catalog:o'neil|default|x64");

        Assert.Contains("Set-CMApplication -InputObject $markerApp -Keyword 'APD-Line:catalog:o''neil|default|x64'", script);
        Assert.Contains("Identity marker not written : ", script);
        Assert.Contains("-DisableWildcardHandling", script[script.IndexOf("$markerApp = Get-CMApplication", StringComparison.Ordinal)..]);
        // The marker is written only when the application has no keywords of its own.
        Assert.Contains("if ($markerOthers.Count -gt 0) { throw", script);
        Assert.True(script.IndexOf("$markerOthers", StringComparison.Ordinal) < script.IndexOf("Set-CMApplication -InputObject $markerApp", StringComparison.Ordinal));
        Assert.True(script.IndexOf("New-CMApplication -Name", StringComparison.Ordinal) < script.IndexOf("$markerApp", StringComparison.Ordinal));
        // The keywords are read before they are written; a failed read writes nothing.
        Assert.True(script.IndexOf("SccmSerializer", StringComparison.Ordinal) < script.IndexOf("Set-CMApplication -InputObject $markerApp", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutAMarkerTheScriptIsAsItWas()
    {
        var script = MecmTarget.BuildPackageScript(Manifest(File()), "Vendor App", @"\\s\c", "MCM", "", 15, 30);

        Assert.DoesNotContain("$markerApp", script);
    }

    [Fact]
    public void AMarkerThatCouldNotBeWrittenIsAWarningOfTheResult()
    {
        var warnings = MecmTarget.ReadWarnings("CI_ID: 1\nIdentity marker not written : The type was not found.\nPackaged: App\n");

        Assert.Single(warnings);
        Assert.Contains("identity marker was not written", warnings[0]);
        Assert.Contains("The type was not found.", warnings[0]);
    }
}
