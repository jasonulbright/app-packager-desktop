using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Supersedence;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The supersedence redesign: the older version is found by title and publisher (no marker in the keywords), the confirmation has
/// three answers and a remembered one for one run, the setting is per application with a default, and the version in the title is
/// locked on while supersedence is on.
/// </summary>
public sealed class SupersedenceRedesignTests
{
    private static StageManifest Manifest(string name, string version, string publisher = "Vendor", string id = "catalog:vendor-app") => new()
    {
        AppName = name, SoftwareVersion = version, Publisher = publisher, ApplicationId = id
    };

    // ---- the key of the lookup ----

    [Fact]
    public void TheKeyIsTheTitleWithoutTheVersionAndThePublisher()
    {
        var key = SupersedenceKey.For(Manifest("Vendor App 2.0.1 (x64)", "2.0.1"));

        Assert.Equal(new SupersedenceKey("Vendor App (x64)", "Vendor"), key);
    }

    [Theory]
    [InlineData("Everything (x64) - 1.4.1.1032", "Everything (x64)", "Everything (x64) - 1.4.0.1000", true)]
    [InlineData("WizTree (x64) - 4.34", "WizTree (x64)", "WizTree (x64) - 4.30", true)]
    [InlineData("7-Zip - 26.06", "7-Zip", "7-Zip - 25.01 (x64)", true)]
    [InlineData("7-Zip - 26.06", "7-Zip", "7-Zip Extensions - 1.0", false)]
    public void TheKeyFindsTheOlderTitleByTheRuleOfCheckConfigMgr(string newName, string expectedBase, string olderTitle, bool found)
    {
        var key = SupersedenceKey.For(Manifest(newName, newName[(newName.LastIndexOf(' ') + 1)..]));

        Assert.Equal(expectedBase, key!.BaseTitle);
        Assert.Equal(found, MecmApplicationTitleRule.Matches(key.BaseTitle, olderTitle));
    }

    [Fact]
    public void ATitleThatIsOnlyItsVersionHasNoKey() =>
        Assert.Null(SupersedenceKey.For(Manifest("2.0.1", "2.0.1")));

    [Fact]
    public void TheLookupScriptWritesNothingToTheSiteAndUsesTheTitleRuleOfCheckConfigMgr()
    {
        var script = SupersedenceScripts.BuildLookupScript("MCM", null, new SupersedenceKey("Vendor App", "Vendor"), "Vendor App - 2.0");

        Assert.Contains("function Test-MecmApplicationTitle", script);
        Assert.Contains("Test-MecmApplicationTitle $baseTitle", script);
        Assert.DoesNotContain("$publisher =", script);
        Assert.DoesNotContain("Set-CMApplication", script);
        Assert.DoesNotContain("APD-Line", script);
    }

    [Fact]
    public void TheMarkerIsNotWrittenByAPublishScript()
    {
        var manifest = new StageManifest
        {
            AppName = "Vendor App", Publisher = "Vendor", SoftwareVersion = "1.0",
            Detection = new StageDetection { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\Vendor" }
        };

        var script = MecmTarget.BuildPackageScript(manifest, "Vendor App", @"\\server\share\App", "P01", "c", 15, 30);

        Assert.DoesNotContain("APD-Line", script);
        Assert.DoesNotContain("Identity marker", script);
    }

    [Fact]
    public void ThePlannerGivesTheKeyToEachTargetAndTheIntuneModeToIntuneOnly()
    {
        var manifest = Manifest("Vendor App - 2.0", "2.0");
        var settings = new SupersedenceSettings { Mode = "Replace" };

        var configMgr = SupersedencePlanner.Plan(manifest, settings, null, true, false, _ => null);
        var intune = SupersedencePlanner.Plan(manifest, settings, null, false, true, _ => null);

        Assert.Equal(new SupersedenceKey("Vendor App", "Vendor"), configMgr.Key);
        Assert.Equal(configMgr.Key, intune.Key);
        Assert.Equal(SupersedenceMode.Replace, intune.IntuneMode);
        Assert.Equal(SupersedenceMode.Off, configMgr.IntuneMode);
        Assert.Null(SupersedencePlanner.Plan(manifest, settings, null, false, false, _ => null).Key);
    }

    // ---- the confirmation ----

    private static SupersedencePrompt Prompt(string title = "Vendor App - 2.0") => new(title, "2.0", "Vendor App - 1.0", "1.0", false);

    [Theory]
    [InlineData(SupersedenceAnswer.Yes)]
    [InlineData(SupersedenceAnswer.Skip)]
    public async Task YesAndSkipAreReturnedAsGiven(SupersedenceAnswer answer)
    {
        var confirmer = SupersedenceConfirmer.Fixed(answer);

        Assert.Equal(answer, await confirmer.ConfirmAsync(Prompt()));
        Assert.False(confirmer.Declined);
    }

    [Fact]
    public async Task NoStopsTheRunAndEveryLaterAnswerIsNo()
    {
        var stops = 0;
        var asked = 0;
        var confirmer = new SupersedenceConfirmer(_ => { asked++; return Task.FromResult(new SupersedenceDecision(SupersedenceAnswer.No, false)); }, () => stops++);

        Assert.Equal(SupersedenceAnswer.No, await confirmer.ConfirmAsync(Prompt()));
        Assert.Equal(SupersedenceAnswer.No, await confirmer.ConfirmAsync(Prompt("Other - 1.0")));

        Assert.True(confirmer.Declined);
        Assert.Equal(1, stops);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task RememberAppliesTheAnswerToTheOtherApplicationsOfTheRunAndAsksNoMore()
    {
        var asked = 0;
        var confirmer = new SupersedenceConfirmer(_ => { asked++; return Task.FromResult(new SupersedenceDecision(SupersedenceAnswer.Skip, true)); });

        Assert.Equal(SupersedenceAnswer.Skip, await confirmer.ConfirmAsync(Prompt()));
        Assert.Equal(SupersedenceAnswer.Skip, await confirmer.ConfirmAsync(Prompt("Other - 2.0")));
        Assert.Equal(SupersedenceAnswer.Skip, await confirmer.ConfirmAsync(Prompt("Third - 3.0")));

        Assert.Equal(1, asked);
        Assert.Equal(SupersedenceAnswer.Skip, confirmer.Remembered);
    }

    [Fact]
    public async Task AnAnswerWithoutRememberIsAskedAgainForTheNextApplication()
    {
        var asked = 0;
        var confirmer = new SupersedenceConfirmer(_ => { asked++; return Task.FromResult(new SupersedenceDecision(SupersedenceAnswer.Yes, false)); });

        await confirmer.ConfirmAsync(Prompt());
        await confirmer.ConfirmAsync(Prompt("Other - 2.0"));

        Assert.Equal(2, asked);
        Assert.Null(confirmer.Remembered);
    }

    [Fact]
    public async Task ARunWithNoOneToAskAnswersSkip() =>
        Assert.Equal(SupersedenceAnswer.Skip, await new SupersedenceConfirmer(null).ConfirmAsync(Prompt()));

    // ---- the setting of an application ----

    [Fact]
    public void SupersedenceIsOffForEveryApplicationUntilItIsChosen()
    {
        var settings = new SupersedenceSettings();

        Assert.Equal(SupersedenceMode.Off, SupersedenceSettingsEditor.EffectiveMode(settings, "catalog:a"));
        Assert.False(SupersedenceSettingsEditor.LocksVersionInTitle(settings, "catalog:a"));
    }

    [Fact]
    public void AnOverrideTurnsItOnForOneApplicationOnly()
    {
        var settings = new SupersedenceSettings();

        SupersedenceSettingsEditor.SetApplication(settings, "catalog:a", SupersedenceMode.Replace);

        Assert.Equal(SupersedenceMode.Replace, SupersedenceSettingsEditor.EffectiveMode(settings, "catalog:a"));
        Assert.Equal(SupersedenceMode.Off, SupersedenceSettingsEditor.EffectiveMode(settings, "catalog:b"));
        Assert.Equal("Replace", settings.TitleOverrides["catalog:a"].Mode);
    }

    [Fact]
    public void AModeEqualToTheDefaultClearsTheOverride()
    {
        var settings = new SupersedenceSettings();
        SupersedenceSettingsEditor.SetApplication(settings, "catalog:a", SupersedenceMode.Replace);

        SupersedenceSettingsEditor.SetApplication(settings, "catalog:a", SupersedenceMode.Off);

        Assert.Empty(settings.TitleOverrides);
    }

    [Fact]
    public void AnApplicationCanBeSwitchedOffWhileTheDefaultIsOn()
    {
        var settings = new SupersedenceSettings { Mode = "Replace" };

        SupersedenceSettingsEditor.SetApplication(settings, "catalog:a", SupersedenceMode.Off);

        Assert.Equal(SupersedenceMode.Off, SupersedenceSettingsEditor.EffectiveMode(settings, "catalog:a"));
        Assert.Equal(SupersedenceMode.Replace, SupersedenceSettingsEditor.EffectiveMode(settings, "catalog:b"));
    }

    [Fact]
    public void SelectedApplicationsGetTheirOwnOverrideAndTheDefaultStaysOff()
    {
        var settings = new SupersedenceSettings();

        var changed = SupersedenceSettingsEditor.SetSelected(settings, ["catalog:a", "catalog:b", null], SupersedenceMode.ReplaceAndUninstall);

        Assert.Equal(2, changed);
        Assert.Equal("Off", settings.Mode);
        Assert.Equal(SupersedenceMode.ReplaceAndUninstall, SupersedenceSettingsEditor.EffectiveMode(settings, "catalog:b"));
        Assert.Equal(SupersedenceMode.Off, SupersedenceSettingsEditor.EffectiveMode(settings, "catalog:c"));
    }

    [Fact]
    public void AllApplicationsSetsTheDefaultAndClearsTheSingleOverridesButKeepsTheirVersionRule()
    {
        var settings = new SupersedenceSettings();
        settings.TitleOverrides["catalog:a"] = new TitleSupersedenceOverride { Mode = "Off" };
        settings.TitleOverrides["catalog:b"] = new TitleSupersedenceOverride { Mode = "Replace", VersionRule = "Exact" };

        SupersedenceSettingsEditor.SetAll(settings, SupersedenceMode.Replace);

        Assert.Equal("Replace", settings.Mode);
        Assert.False(settings.TitleOverrides.ContainsKey("catalog:a"));
        Assert.Null(settings.TitleOverrides["catalog:b"].Mode);
        Assert.Equal("Exact", settings.TitleOverrides["catalog:b"].VersionRule);
        Assert.Equal(SupersedenceMode.Replace, SupersedenceSettingsEditor.EffectiveMode(settings, "catalog:a"));
    }

    [Fact]
    public void NoApplicationsTurnsEveryApplicationOff()
    {
        var settings = new SupersedenceSettings { Mode = "Replace" };
        SupersedenceSettingsEditor.SetApplication(settings, "catalog:a", SupersedenceMode.ReplaceAndUninstall);

        SupersedenceSettingsEditor.SetAll(settings, SupersedenceMode.Off);

        Assert.Equal("Off", settings.Mode);
        Assert.Equal(SupersedenceMode.Off, SupersedenceSettingsEditor.EffectiveMode(settings, "catalog:a"));
        Assert.Empty(settings.TitleOverrides);
    }

    [Fact]
    public void AnApplicationWithoutAnIdentifierCannotHaveAnOverride()
    {
        var settings = new SupersedenceSettings();

        Assert.False(SupersedenceSettingsEditor.SetApplication(settings, "", SupersedenceMode.Replace));
        Assert.Empty(settings.TitleOverrides);
    }

    // ---- the version in the title ----

    private static AppSettings Settings(SupersedenceSettings supersedence) => new() { Supersedence = supersedence };

    [Fact]
    public void TheTitleKeepsItsModeWhileSupersedenceIsOff()
    {
        var title = PackagerExecutionService.RunApplicationTitle(Manifest("Vendor App 2.0", "2.0"), "NoVersion", Settings(new()), "catalog:vendor-app");

        Assert.DoesNotContain("2.0", title);
    }

    [Fact]
    public void TheVersionIsForcedIntoTheTitleWhileSupersedenceIsOnWhateverTheTitleModeSays()
    {
        var supersedence = new SupersedenceSettings();
        SupersedenceSettingsEditor.SetApplication(supersedence, "catalog:vendor-app", SupersedenceMode.Replace);

        var title = PackagerExecutionService.RunApplicationTitle(Manifest("Vendor App", "2.0"), "NoVersion", Settings(supersedence), "catalog:vendor-app");

        Assert.Contains("2.0", title);
    }

    [Fact]
    public void AnotherApplicationIsNotLockedByTheOverrideOfThisOne()
    {
        var supersedence = new SupersedenceSettings();
        SupersedenceSettingsEditor.SetApplication(supersedence, "catalog:vendor-app", SupersedenceMode.Replace);

        var title = PackagerExecutionService.RunApplicationTitle(Manifest("Other App", "2.0", "Vendor", "catalog:other"), "NoVersion", Settings(supersedence), "catalog:other");

        Assert.DoesNotContain("2.0", title);
    }

    [Fact]
    public void TheCommandLineModeLocksTheTitleToo()
    {
        var title = PackagerExecutionService.RunApplicationTitle(
            Manifest("Vendor App", "2.0"), "NoVersion", Settings(new()), "catalog:vendor-app", new SupersedenceCommandLine(null, "replace"));

        Assert.Contains("2.0", title);
    }
}
