using System.Text;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Supersedence;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The lookup of the old application, the pairing of deployment types, the step script and the separate outcome.</summary>
public sealed class SupersedenceStepTests
{
    private static LineDeploymentType Dt(string id, string name) => new(id, name);

    private static LineApplication App(string id, string title, string version, bool expired = false, params LineDeploymentType[] dts) =>
        new(id, title, version, expired, dts.Length > 0 ? dts : [Dt(id + "1", title + " - x64")]);

    // ---- the lookup ----

    [Fact]
    public void TheNewestOlderApplicationIsChosenAndTheOthersAreNamed()
    {
        var apps = new[] { App("1", "Vendor App 1.0", "1.0.0"), App("2", "Vendor App 1.5", "1.5.0"), App("3", "Vendor App 2.0", "2.0.0") };

        var result = SupersedenceLookup.Choose(apps, "Vendor App 3.0", "3.0.0");

        Assert.Equal("3", result.Old!.CiId);
        Assert.Contains(result.Notes, n => n.Contains("Vendor App 1.5"));
    }

    [Fact]
    public void AnApplicationWithTheTitleOfTheNewOneIsNeverReturned()
    {
        var apps = new[] { App("1", "Vendor App", "1.0.0") };

        var result = SupersedenceLookup.Choose(apps, "vendor app", "2.0.0");

        Assert.Null(result.Old);
        Assert.StartsWith("No older version found", result.Summary);
    }

    [Theory]
    [InlineData("2.0.0")]
    [InlineData("3.0.0")]
    public void ANewerOrEqualVersionIsNotAnOldApplication(string version)
    {
        Assert.Null(SupersedenceLookup.Choose([App("1", "Vendor App 2", version)], "Vendor App 2.0", "2.0.0").Old);
    }

    [Fact]
    public void ARetiredApplicationIsSkippedAndTheNextLowerOneIsTaken()
    {
        var apps = new[] { App("1", "Vendor App 1.0", "1.0.0"), App("2", "Vendor App 1.5", "1.5.0", expired: true) };

        var result = SupersedenceLookup.Choose(apps, "Vendor App 2.0", "2.0.0");

        Assert.Equal("1", result.Old!.CiId);
        Assert.Contains(result.Notes, n => n.Contains("retired"));
    }

    [Fact]
    public void AVersionThatCannotBeParsedIsSkippedAndNamed()
    {
        var apps = new[] { App("1", "Vendor App old", "banana"), App("2", "Vendor App 1.0", "1.0.0") };

        var result = SupersedenceLookup.Choose(apps, "Vendor App 2.0", "2.0.0");

        Assert.Equal("2", result.Old!.CiId);
        Assert.Contains(result.Notes, n => n.Contains("banana"));
    }

    [Fact]
    public void ANewVersionThatCannotBeParsedGivesNoOldApplication()
    {
        var result = SupersedenceLookup.Choose([App("1", "Vendor App 1.0", "1.0.0")], "Vendor App", "not-a-version");

        Assert.Null(result.Old);
        Assert.Contains("cannot be read", result.Summary);
    }

    // ---- the pairing ----

    [Fact]
    public void OneAndOneAreOnePair()
    {
        var pairing = SupersedenceLookup.Pair(App("9", "New", "2.0"), App("1", "Old", "1.0"));

        Assert.Single(pairing.Pairs);
        Assert.Equal(("91", "11"), (pairing.Pairs[0].New.CiId, pairing.Pairs[0].Old.CiId));
        Assert.Empty(pairing.Warnings);
    }

    [Fact]
    public void ManyTypesPairBySuffixAndAnUnmatchedOneGetsAWarningAndNoRelation()
    {
        var newApp = App("9", "App 2", "2.0", false, Dt("91", "App 2 - x64"), Dt("92", "App 2 - x86"), Dt("93", "App 2 - ARM64"));
        var oldApp = App("1", "App 1", "1.0", false, Dt("11", "App 1 - x64"), Dt("12", "App 1 - x86"));

        var pairing = SupersedenceLookup.Pair(newApp, oldApp);

        Assert.Equal([("91", "11"), ("92", "12")], pairing.Pairs.Select(p => (p.New.CiId, p.Old.CiId)));
        Assert.Contains(pairing.Warnings, w => w.Contains("ARM64") && w.Contains("no partner"));
    }

    [Fact]
    public void ArchitecturesAreNeverCrossLinkedWhenTheSuffixesDiffer()
    {
        var newApp = App("9", "App 2", "2.0", false, Dt("91", "App 2 - x64"), Dt("92", "App 2 - x86"));
        var oldApp = App("1", "App 1", "1.0", false, Dt("11", "App 1 - ARM64"), Dt("12", "App 1 - arm"));

        Assert.Empty(SupersedenceLookup.Pair(newApp, oldApp).Pairs);
    }

    [Fact]
    public void AnOldApplicationWithNoDeploymentTypeSupersedesNothingAndSaysSo()
    {
        var pairing = SupersedenceLookup.Pair(App("9", "New", "2.0"), new LineApplication("1", "Old", "1.0", false, []));

        Assert.Empty(pairing.Pairs);
        Assert.Contains("no deployment type", pairing.Warnings.Single());
    }

    // ---- the script output ----

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void TheSiteScriptOutputIsParsedWithTitlesThatHoldASeparator()
    {
        var output = $"APDAPP|10|0|1.2.3|{B64("A|B app")}\nAPDDT|10|101|{B64("A|B app - x64")}\nnoise\nAPDAPP|11|1|2.0|{B64("Retired")}\n";

        var apps = SupersedenceLookup.ParseSiteApplications(output);

        Assert.Equal(2, apps.Count);
        Assert.Equal(("10", "A|B app", "1.2.3", false), (apps[0].CiId, apps[0].Title, apps[0].SoftwareVersion, apps[0].IsExpired));
        Assert.Equal("A|B app - x64", apps[0].DeploymentTypes.Single().Name);
        Assert.True(apps[1].IsExpired);
        Assert.Empty(apps[1].DeploymentTypes);
    }

    [Fact]
    public void TheLookupScriptMatchesTheMarkerInTheXmlAndAlwaysListsTheNewApplication()
    {
        var script = SupersedenceScripts.BuildLookupScript("MCM", null, "APD-Line:catalog:o'neil&co|default|x64", "123");

        Assert.Contains("'>APD-Line:catalog:o''neil&amp;co|default|x64<'", script);
        Assert.Contains("$newApplicationCiId = 123", script);
        Assert.Contains("$candidate.CI_ID -ne $newApplicationCiId -and", script);
        Assert.Contains("Get-CMDeploymentType -ApplicationName $candidate.LocalizedDisplayName -DisableWildcardHandling", script);
    }

    [Fact]
    public void TheStepScriptSetsOneRelationForEachPairWithTheIdsAndTheUninstallOption()
    {
        var pairs = new[] { new DeploymentTypePair(Dt("91", "New - x64"), Dt("11", "Old - x64")), new DeploymentTypePair(Dt("92", "New - x86"), Dt("12", "Old - x86")) };

        var uninstall = SupersedenceScripts.BuildStepScript("MCM", "SRV1", "9", "1", pairs, uninstall: true);
        var keep = SupersedenceScripts.BuildStepScript("MCM", null, "9", "1", pairs, uninstall: false);

        Assert.Contains("Set-CMApplicationSupersedence -Id 9 -CurrentDeploymentTypeId 91 -SupersededApplicationId 1 -OldDeploymentTypeId 11 -IsUninstall $true", uninstall);
        Assert.Contains("Set-CMApplicationSupersedence -Id 9 -CurrentDeploymentTypeId 92 -SupersededApplicationId 1 -OldDeploymentTypeId 12 -IsUninstall $true", uninstall);
        Assert.Contains("-IsUninstall $false", keep);
        Assert.DoesNotContain("-IsUninstall $true", keep);
        Assert.Contains("'SRV1'", uninstall);
        // A relation that exists is a success; the script tries every pair and exits 1 when one failed.
        Assert.Contains("-match 'already'", uninstall);
        Assert.Contains("if ($supersedenceFailed) { exit 1 }", uninstall);
    }

    [Fact]
    public void TheStepScriptRefusesAnIdThatIsNotANumber()
    {
        var pairs = new[] { new DeploymentTypePair(Dt("91", "N"), Dt("11", "O")) };

        Assert.ThrowsAny<FormatException>(() => SupersedenceScripts.BuildStepScript("MCM", null, "9; calc", "1", pairs, false));
        Assert.ThrowsAny<FormatException>(() => SupersedenceScripts.BuildLookupScript("MCM", null, "m", "x"));
    }

    [Fact]
    public void TheFailureTextIsTheReasonOfTheFirstFailedRelation()
    {
        var output = "Supersedence added          : A supersedes B\nSupersedence failed         : C supersedes D: access denied\n";

        Assert.Equal("C supersedes D: access denied", SupersedenceScripts.FailureText(output, "ignored"));
        Assert.Equal("boom", SupersedenceScripts.FailureText("", "boom\nat line 1"));
        Assert.Equal("the site gave no reason.", SupersedenceScripts.FailureText("", ""));
    }

    // ---- the outcome ----

    [Fact]
    public void AFailedSupersedenceStepIsASeparateOutcomeAndNotPartial()
    {
        var outcome = PackagingOutcomeComposer.ConfigMgrInventoryOutcome(true, true, false, null, "access denied");

        Assert.Equal(InventoryOutcome.PublishedSupersedenceNotApplied, outcome);
        Assert.Equal("Published, supersedence not applied: access denied",
            PackagingOutcomeComposer.ConfigMgrDetail(null, null, null, "access denied"));
    }

    [Fact]
    public void AFailedContentStepOutranksAFailedSupersedenceStep()
    {
        Assert.Equal(InventoryOutcome.Partial, PackagingOutcomeComposer.ConfigMgrInventoryOutcome(true, true, false, "distribution failed", "x"));
        Assert.Equal("distribution failed", PackagingOutcomeComposer.ConfigMgrDetail(null, "distribution failed", null, "x"));
    }

    [Fact]
    public void ARunWithoutAFailedSupersedenceStepIsUnchanged()
    {
        Assert.Equal(InventoryOutcome.Succeeded, PackagingOutcomeComposer.ConfigMgrInventoryOutcome(true, true, false, null, null));
        Assert.Null(PackagingOutcomeComposer.ConfigMgrDetail(null, null, null, null));
    }

    [Fact]
    public void TheRunSummaryKeepsTheSeparateStateWhenEveryDestinationElseSucceeded()
    {
        Assert.Equal(InventoryOutcome.PublishedSupersedenceNotApplied,
            PackagingOutcomeComposer.SummarizeDestinations([InventoryOutcome.PublishedSupersedenceNotApplied, InventoryOutcome.Succeeded]));
        Assert.Equal(InventoryOutcome.Partial,
            PackagingOutcomeComposer.SummarizeDestinations([InventoryOutcome.PublishedSupersedenceNotApplied, InventoryOutcome.Failed]));
    }

    [Fact]
    public void ThePlanCarriesTheResolvedMode()
    {
        var manifest = new StageManifest { AppName = "A", SoftwareVersion = "1.0", ApplicationId = "catalog:a" };
        var settings = new SupersedenceSettings { ConfigMgrVersionRule = "Exact", Mode = "Replace" };
        settings.InstalledValues[ExactGuard.RecordKey("catalog:a", "1.0")] = "1.0";
        manifest.Detection = new StageDetection { Type = "File", FilePath = "p", FileName = "f", ExpectedValue = "1.0", Operator = "GreaterEquals" };

        var plan = SupersedencePlanner.Plan(manifest, settings, null, true, false, _ => null);

        Assert.Equal(SupersedenceMode.Replace, plan.Mode);
    }

    [Fact]
    public async Task AModeOfOffOrAMissingMarkerAsksNothingOfTheSite()
    {
        var target = new MecmTarget();

        var off = await target.RunSupersedenceAsync("App", "5", "1.0", "m", SupersedenceMode.Off, "MCM", null, null, CancellationToken.None);
        var none = await target.RunSupersedenceAsync("App", "5", "1.0", "", SupersedenceMode.Replace, "MCM", null, null, CancellationToken.None);
        var noId = await target.RunSupersedenceAsync("App", "", "1.0", "m", SupersedenceMode.Replace, "MCM", null, null, CancellationToken.None);

        Assert.Equal(SupersedenceStepStatus.NotRun, off.Status);
        Assert.Equal(SupersedenceStepStatus.NotRun, none.Status);
        Assert.Equal(SupersedenceStepStatus.NotRun, noId.Status);
    }
}
