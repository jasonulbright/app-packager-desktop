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
    public void ALineThatAlreadyHoldsAHigherVersionIsNamedAndNotSuperseded()
    {
        var apps = new[] { App("1", "Vendor App 1.0", "1.0.0"), App("2", "Vendor App 2.0", "2.0.0") };

        var result = SupersedenceLookup.Choose(apps, "Vendor App 1.5", "1.5.0");

        Assert.Equal("1", result.Old!.CiId);
        Assert.Contains(result.Notes, n => n.Contains("Vendor App 2.0") && n.Contains("not superseded"));
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
    public void TheStepScriptSetsOneRelationForEachPairByNameAndReadsTheRelationsFirst()
    {
        var pairs = new[] { new DeploymentTypePair(Dt("91", "New - x64"), Dt("11", "Old - x64")), new DeploymentTypePair(Dt("92", "New - x86"), Dt("12", "Old - x86")) };

        var uninstall = SupersedenceScripts.BuildStepScript("MCM", "SRV1", "New", "Old", pairs, uninstall: true);
        var keep = SupersedenceScripts.BuildStepScript("MCM", null, "New", "Old", pairs, uninstall: false);

        // Names, not identifiers: every save of an application makes a new revision with a new CI_ID.
        Assert.Contains("Set-CMApplicationSupersedence -Name $newApplicationName -CurrentDeploymentTypeName $newDtName -SupersededApplicationName $oldApplicationName -OldDeploymentTypeName $oldDtName -IsUninstall:$true", uninstall);
        Assert.Equal(2, uninstall.Split("Set-CMApplicationSupersedence").Length - 1);
        Assert.Contains("$newDtName = 'New - x86'", uninstall);
        Assert.Contains("$oldDtName = 'Old - x86'", uninstall);
        Assert.DoesNotContain("-CurrentDeploymentTypeId", uninstall);
        Assert.Contains("-IsUninstall:$false", keep);
        Assert.DoesNotContain("-IsUninstall:$true", keep);
        Assert.Contains("'SRV1'", uninstall);
        // The relations are read before the write, and again after a failed write; a text of the provider decides nothing.
        Assert.Contains("Get-CMDeploymentTypeSupersedence -InputObject $dt", uninstall);
        Assert.DoesNotContain("-match 'already'", uninstall);
        Assert.True(uninstall.IndexOf("if (Test-Supersedes", StringComparison.Ordinal) < uninstall.IndexOf("Set-CMApplicationSupersedence", StringComparison.Ordinal));
        Assert.Contains("if ($supersedenceFailed) { exit 1 }", uninstall);
    }

    [Fact]
    public void ANameWithDoubleQuotesIsOnlyEverInsideSingleQuotedStrings()
    {
        var pairs = new[] { new DeploymentTypePair(Dt("91", "N\"ew $(calc) - x64"), Dt("11", "O“ld - x64")) };

        var script = SupersedenceScripts.BuildStepScript("MCM", null, "A\"pp", "B”pp", pairs, true);

        Assert.DoesNotContain("Write-Host \"", script);
        Assert.Contains("-f 'N\"ew $(calc) - x64 supersedes O“ld - x64'", script);
    }

    [Fact]
    public void TheLookupScriptRefusesAnIdThatIsNotANumber()
    {
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
        // The mode is on and the marker is there, but the CI_ID was not read: reported, not silent.
        Assert.Equal(SupersedenceStepStatus.NotApplied, noId.Status);
        Assert.Contains("CI_ID", noId.Message);
    }
}
