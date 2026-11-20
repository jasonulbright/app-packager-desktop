using System.Text;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Supersedence;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The findings of the independent review of the supersedence redesign: the architecture of the older version, a retired older version,
/// the vendor compare, the confirmation that fails, and the menu that would set a refused combination.
/// </summary>
public sealed class SupersedenceReviewFixTests
{
    private static LineApplication App(string id, string title, string version, string publisher = "Vendor", bool expired = false) =>
        new(id, title, version, expired, [], publisher);

    private static SupersedenceKey Key(string publisher = "Vendor", string architecture = "") => new("Foo", publisher, architecture);

    // ---- 3: the vendor compare (same rule as the PowerShell app) ----

    [Theory]
    [InlineData("Contoso", "CONTOSO", true)]
    [InlineData("Contoso Inc.", "Contoso", true)]
    [InlineData("Contoso, Inc", "contoso corporation", true)]
    [InlineData("Bitwarden Inc", "Bitwarden, Inc.", true)]
    [InlineData("Contoso GmbH", "Contoso Ltd.", true)]
    [InlineData("Contoso Software", "Contoso", false)]
    [InlineData("Contoso", "Fabrikam", false)]
    [InlineData("Inc", "Inc.", true)]
    public void TheVendorCompareIgnoresCaseAndLegalSuffixes(string a, string b, bool same) =>
        Assert.Equal(same, VendorKey.Of(a) == VendorKey.Of(b));

    [Fact]
    public void ANewApplicationWithoutAVendorComparesWithEveryone_ACandidateWithoutAVendorMatchesNoNamedVendor()
    {
        Assert.True(VendorKey.Same("", "Anyone"));
        Assert.True(VendorKey.Same(null, ""));
        Assert.False(VendorKey.Same("Bitwarden Inc", ""));
        Assert.False(VendorKey.Same("Bitwarden Inc", null));
    }

    [Fact]
    public void TheLookupTakesTheOlderVersionOfTheSameVendorWithAnotherSpellingAndNeverAnotherVendor()
    {
        var apps = new[]
        {
            App("1", "Foo - 1.0", "1.0", "Bitwarden, Inc."),
            App("2", "Foo - 1.5", "1.5", "Other Corp"),
            App("3", "Foo - 1.7", "1.7", ""),
        };
        var found = SupersedenceLookup.Choose(apps, "Foo - 2.0", "2.0", Key("Bitwarden Inc"));
        Assert.Equal("1", found.Old?.CiId);
    }

    [Fact]
    public void TheSiteScriptReturnsTheManufacturerAndTheParserReadsIt()
    {
        string B(string t) => Convert.ToBase64String(Encoding.UTF8.GetBytes(t));
        var output = $"APDAPP|7|0|1.0|{B("Foo - 1.0")}|{B("Bitwarden, Inc.")}\nAPDAPP|8|0|1.1|{B("Foo - 1.1")}\nAPDDT|7|70|{B("Foo - 1.0")}";
        var apps = SupersedenceLookup.ParseSiteApplications(output);
        Assert.Equal("Bitwarden, Inc.", apps[0].Publisher);
        Assert.Equal("", apps[1].Publisher);
        Assert.Single(apps[0].DeploymentTypes);
    }

    // ---- 1: the architecture ----

    [Fact]
    public void AnOlderVersionOfAnotherArchitectureIsNotTheOlderVersion()
    {
        var apps = new[] { App("x86", "Foo 1.0 (x86)", "1.0"), App("x64", "Foo 0.9 (x64)", "0.9") };
        var found = SupersedenceLookup.Choose(apps, "Foo 2.0 (x64)", "2.0", Key());
        Assert.Equal("x64", found.Old?.CiId);
    }

    [Fact]
    public void ThePayloadArchitectureOfTheKeyDecidesWhenTheNewTitleHasNoTag()
    {
        var apps = new[] { App("x86", "Foo 1.0 (x86)", "1.0"), App("none", "Foo 0.5", "0.5") };
        var found = SupersedenceLookup.Choose(apps, "Foo 2.0", "2.0", Key(architecture: "x64"));
        Assert.Equal("none", found.Old?.CiId);
    }

    [Fact]
    public void OnlyAnotherArchitectureMeansNoOlderVersion()
    {
        var found = SupersedenceLookup.Choose([App("x86", "Foo 1.0 (x86)", "1.0")], "Foo 2.0 (x64)", "2.0", Key());
        Assert.Null(found.Old);
    }

    [Theory]
    [InlineData("Foo 2.0 (x64)", "x64")]
    [InlineData("Foo (AMD64)", "x64")]
    [InlineData("Foo (32-bit) - 1", "x86")]
    [InlineData("Foo (arm64)", "arm64")]
    [InlineData("Foo 2.0", null)]
    public void TheArchitectureOfATitleIsReadFromItsTag(string title, string? expected) =>
        Assert.Equal(expected, TitleArchitecture.Of(title));

    [Fact]
    public void TheKeyCarriesTheArchitectureOfTheManifest()
    {
        var manifest = new StageManifest { AppName = "Foo 2.0", SoftwareVersion = "2.0", Publisher = "Vendor", Architecture = "x64" };
        Assert.Equal("x64", SupersedenceKey.For(manifest)!.Architecture);
        manifest.Architecture = "";
        Assert.Equal("", SupersedenceKey.For(manifest)!.Architecture);
    }

    // ---- 2: a retired older version ----

    [Fact]
    public void ARetiredHighestOlderVersionMeansNoOlderVersion_NeverN2()
    {
        var apps = new[] { App("n2", "Foo - 0.9", "0.9"), App("n1", "Foo - 1.5", "1.5", expired: true) };
        var found = SupersedenceLookup.Choose(apps, "Foo - 2.0", "2.0", Key());
        Assert.Null(found.Old);
        Assert.Contains(found.Notes, n => n.Contains("1.5") && n.Contains("retired"));
    }

    [Fact]
    public void ARetiredLowerVersionDoesNotHideTheLiveHighestOne()
    {
        var apps = new[] { App("n2", "Foo - 0.9", "0.9", expired: true), App("n1", "Foo - 1.5", "1.5") };
        Assert.Equal("n1", SupersedenceLookup.Choose(apps, "Foo - 2.0", "2.0", Key()).Old?.CiId);
    }

    [Fact]
    public void AnUnreadableVersionIsNamedAndSkippedAndTheHighestReadableOneIsKept()
    {
        var apps = new[] { App("bad", "Foo - nightly", "nightly"), App("n1", "Foo - 1.5", "1.5") };
        var found = SupersedenceLookup.Choose(apps, "Foo - 2.0", "2.0", Key());
        Assert.Equal("n1", found.Old?.CiId);
        Assert.Contains(found.Notes, n => n.Contains("nightly") && n.Contains("cannot be read"));
    }

    // ---- 5: a confirmation that fails ----

    [Fact]
    public async Task AFailingConfirmationIsLoggedOnceAndCountsAsSkip()
    {
        var confirmer = new SupersedenceConfirmer(_ => throw new InvalidOperationException("window failed"));
        var log = new List<string>();
        var answer = await confirmer.ConfirmSafeAsync(new SupersedencePrompt("A 2", "2", "A 1", "1", false), log.Add);
        Assert.Equal(SupersedenceAnswer.Skip, answer);
        var line = Assert.Single(log);
        Assert.Contains("window failed", line);
        Assert.False(confirmer.Declined);
    }

    [Fact]
    public async Task ACancelledConfirmationStillCancels()
    {
        var confirmer = new SupersedenceConfirmer(_ => throw new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            confirmer.ConfirmSafeAsync(new SupersedencePrompt("A 2", "2", "A 1", "1", false), null));
    }

    [Fact]
    public async Task AnAnswerStillPassesThroughTheSafeCall()
    {
        var confirmer = SupersedenceConfirmer.Fixed(SupersedenceAnswer.Yes);
        Assert.Equal(SupersedenceAnswer.Yes, await confirmer.ConfirmSafeAsync(new SupersedencePrompt("A 2", "2", "A 1", "1", false), null));
    }

    // ---- 4: the question is not asked for an application that a Skip or Fail run leaves alone ----

    [Theory]
    [InlineData(OnExistingBehavior.Skip, true)]
    [InlineData(OnExistingBehavior.Fail, true)]
    [InlineData(OnExistingBehavior.Overwrite, false)]
    public void OnlyARunThatLeavesTheExistingApplicationAloneProbesBeforeTheQuestion(OnExistingBehavior behavior, bool probes) =>
        Assert.Equal(probes, MecmTarget.ProbesBeforeContentCopy(behavior));

    // ---- 6: the menu does not set a combination that every publish refuses ----

    [Fact]
    public void ReplaceAndUninstallIsBlockedForAnApplicationWhoseVersionRuleIsMinimum()
    {
        var settings = new SupersedenceSettings();
        Assert.NotNull(SupersedenceSettingsEditor.UninstallBlockedFor(settings, "catalog:foo", SupersedenceMode.ReplaceAndUninstall));
        Assert.Null(SupersedenceSettingsEditor.UninstallBlockedFor(settings, "catalog:foo", SupersedenceMode.Replace));
        Assert.Null(SupersedenceSettingsEditor.UninstallBlockedFor(settings, "catalog:foo", SupersedenceMode.Off));
    }

    [Fact]
    public void ReplaceAndUninstallIsAllowedWhenTheApplicationOrTheDefaultHasTheRuleExact()
    {
        var own = new SupersedenceSettings();
        own.TitleOverrides["catalog:foo"] = new TitleSupersedenceOverride { VersionRule = "Exact" };
        Assert.Null(SupersedenceSettingsEditor.UninstallBlockedFor(own, "catalog:foo", SupersedenceMode.ReplaceAndUninstall));
        Assert.NotNull(SupersedenceSettingsEditor.UninstallBlockedFor(own, "catalog:bar", SupersedenceMode.ReplaceAndUninstall));

        var site = new SupersedenceSettings { ConfigMgrVersionRule = "Exact" };
        Assert.Null(SupersedenceSettingsEditor.UninstallBlockedFor(site, "catalog:bar", SupersedenceMode.ReplaceAndUninstall));
    }

    [Fact]
    public void TheDefaultForAllApplicationsFollowsTheSiteVersionRule()
    {
        Assert.NotNull(SupersedenceSettingsEditor.UninstallBlockedForAll(new SupersedenceSettings(), SupersedenceMode.ReplaceAndUninstall));
        Assert.Null(SupersedenceSettingsEditor.UninstallBlockedForAll(new SupersedenceSettings { ConfigMgrVersionRule = "Exact" }, SupersedenceMode.ReplaceAndUninstall));
        Assert.Null(SupersedenceSettingsEditor.UninstallBlockedForAll(new SupersedenceSettings(), SupersedenceMode.Replace));
    }
}

public sealed class SupersedenceRefusedCombinationTests
{
    [Fact]
    public void TheSavedSettingsListWhatEveryPublishWouldRefuse()
    {
        var settings = new SupersedenceSettings { Mode = "Replace and uninstall" };
        settings.TitleOverrides["catalog:foo"] = new TitleSupersedenceOverride { Mode = "Replace and uninstall" };
        settings.TitleOverrides["catalog:bar"] = new TitleSupersedenceOverride { Mode = "Replace and uninstall", VersionRule = "Exact" };
        settings.TitleOverrides["catalog:baz"] = new TitleSupersedenceOverride { Mode = "Replace" };

        var refused = SupersedenceSettingsEditor.RefusedCombinations(settings);

        Assert.Equal(["the default for all applications", "catalog:foo"], refused);
    }

    [Fact]
    public void NothingIsRefusedWithTheRuleExactOrWithoutTheUninstallMode()
    {
        Assert.Empty(SupersedenceSettingsEditor.RefusedCombinations(new SupersedenceSettings { Mode = "Replace and uninstall", ConfigMgrVersionRule = "Exact" }));
        Assert.Empty(SupersedenceSettingsEditor.RefusedCombinations(new SupersedenceSettings()));
    }
}
