using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Tests;

public sealed class DeploymentTargetStateTests
{
    [Theory]
    [InlineData(DeploymentTargets.Mecm)]
    [InlineData(DeploymentTargets.MecmAndIntune)]
    [InlineData(DeploymentTargets.MecmAndWsus)]
    [InlineData(DeploymentTargets.MecmAndIntuneAndWsus)]
    public void MecmTargetsKeepThePackageActionBar(string? target)
    {
        var state = DeploymentTargetActionState.For(target);

        Assert.True(state.CheckMecmEnabled);
        Assert.False(state.SkipMecmPreflight);
        Assert.Equal("Package", state.PackageLabel);
        Assert.Contains("ConfigMgr Application", state.PackageToolTip);
        Assert.Contains("currently deployed version", state.CheckMecmToolTip);
    }

    [Fact]
    public void IntuneOnlyDisablesCheckMecmAndRelabelsPackage()
    {
        var state = DeploymentTargetActionState.For(DeploymentTargets.IntuneOnly);

        Assert.False(state.CheckMecmEnabled);
        Assert.True(state.SkipMecmPreflight);
        Assert.Equal("Publish", state.PackageLabel);
        Assert.Contains("ConfigMgr as a destination", state.CheckMecmToolTip);
        Assert.Contains("Intune", state.PackageToolTip);
    }

    [Theory]
    [InlineData(DeploymentTargets.WsusOnly, "WSUS")]
    [InlineData(DeploymentTargets.IntuneAndWsus, "Intune and WSUS")]
    public void ATargetWithoutConfigMgrNamesItsOwnDestinationsAndNeverAConfigMgrSite(string target, string destinations)
    {
        var state = DeploymentTargetActionState.For(target);

        Assert.False(state.CheckMecmEnabled);
        Assert.True(state.SkipMecmPreflight);
        Assert.Contains(destinations, state.PackageToolTip);
        Assert.DoesNotContain("site", state.PackageToolTip);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    public void AnUnknownOrEmptyTargetIsNotConfigMgr(string? target)
    {
        var state = DeploymentTargetActionState.For(target);

        Assert.False(state.CheckMecmEnabled);
        Assert.True(state.SkipMecmPreflight);
        Assert.Contains("nowhere", state.PackageToolTip);
    }

    [Fact]
    public void MecmTargetIsBlockedByAMissingSiteCode()
    {
        var reason = DeploymentTargetGate.Validate(
            DeploymentTargets.Mecm, siteCode: "", fileShareRoot: @"\\srv\share",
            tenantId: "", clientId: "", hasClientSecret: false);

        Assert.NotNull(reason);
        Assert.Contains("Site Code", reason);
    }

    [Fact]
    public void MecmTargetIsBlockedByAMissingFileShareRoot()
    {
        var reason = DeploymentTargetGate.Validate(
            DeploymentTargets.Mecm, siteCode: "MCM", fileShareRoot: "  ",
            tenantId: "", clientId: "", hasClientSecret: false);

        Assert.NotNull(reason);
        Assert.Contains("File Share Root", reason);
    }

    [Fact]
    public void MecmTargetNeedsNoIntuneCredentials()
    {
        var reason = DeploymentTargetGate.Validate(
            DeploymentTargets.Mecm, siteCode: "MCM", fileShareRoot: @"\\srv\share",
            tenantId: "", clientId: "", hasClientSecret: false);

        Assert.Null(reason);
    }

    [Fact]
    public void IntuneOnlyRunIgnoresTheMecmFields()
    {
        var reason = DeploymentTargetGate.Validate(
            DeploymentTargets.IntuneOnly, siteCode: "", fileShareRoot: "",
            tenantId: "contoso.onmicrosoft.com", clientId: "app-id", hasClientSecret: true);

        Assert.Null(reason);
    }

    [Fact]
    public void IntuneOnlyRunRequiresIntuneCredentials()
    {
        var reason = DeploymentTargetGate.Validate(
            DeploymentTargets.IntuneOnly, siteCode: "", fileShareRoot: "",
            tenantId: "contoso.onmicrosoft.com", clientId: "app-id", hasClientSecret: false);

        Assert.NotNull(reason);
        Assert.Contains("Intune credentials", reason);
    }

    [Fact]
    public void MecmAndIntuneRequiresBothSides()
    {
        Assert.Contains("Site Code", DeploymentTargetGate.Validate(
            DeploymentTargets.MecmAndIntune, "", @"\\srv\share", "t", "c", true)!);

        Assert.Contains("Intune credentials", DeploymentTargetGate.Validate(
            DeploymentTargets.MecmAndIntune, "MCM", @"\\srv\share", "t", "", true)!);

        Assert.Null(DeploymentTargetGate.Validate(
            DeploymentTargets.MecmAndIntune, "MCM", @"\\srv\share", "t", "c", true));
    }

    [Theory]
    [InlineData(null, DeploymentTargets.ContentOnly)]
    [InlineData("", DeploymentTargets.ContentOnly)]
    [InlineData("nonsense", DeploymentTargets.ContentOnly)]
    [InlineData("MECM", DeploymentTargets.Mecm)]
    [InlineData("MECMAndIntune", DeploymentTargets.MecmAndIntune)]
    [InlineData("IntuneOnly", DeploymentTargets.IntuneOnly)]
    [InlineData("intuneonly", DeploymentTargets.IntuneOnly)]
    [InlineData("IntuneAndWSUS", DeploymentTargets.IntuneAndWsus)]
    [InlineData("configmgrandintuneandwsus", DeploymentTargets.MecmAndIntuneAndWsus)]
    public void NormalizeKeepsKnownNamesAndNeverReadsAnUnknownValueAsConfigMgr(string? stored, string expected)
        => Assert.Equal(expected, DeploymentTargets.Normalize(stored));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("nonsense")]
    [InlineData("ConfigMgrAndNothing")]
    public void AnEmptyOrUnknownTargetSelectsNoDestination(string? stored)
    {
        var set = DestinationSet.FromTarget(stored);

        Assert.False(set.ConfigMgr);
        Assert.False(set.Any);
        Assert.False(DeploymentTargets.IsKnown(stored));
        Assert.True(DeploymentTargets.SkipsMecm(stored));
        Assert.False(DeploymentTargets.PublishesToIntune(stored));
        Assert.False(DeploymentTargets.PublishesToWsus(stored));
        Assert.Null(DestinationSet.TryFromTarget(stored));
    }

    [Theory]
    [InlineData(DeploymentTargets.Mecm, true, false, false, "ConfigMgr")]
    [InlineData(DeploymentTargets.IntuneOnly, false, false, true, "Intune")]
    [InlineData(DeploymentTargets.WsusOnly, false, true, false, "WSUS")]
    [InlineData(DeploymentTargets.MecmAndIntune, true, false, true, "ConfigMgr and Intune")]
    [InlineData(DeploymentTargets.MecmAndWsus, true, true, false, "ConfigMgr and WSUS")]
    [InlineData(DeploymentTargets.IntuneAndWsus, false, true, true, "Intune and WSUS")]
    [InlineData(DeploymentTargets.MecmAndIntuneAndWsus, true, true, true, "ConfigMgr, Intune and WSUS")]
    [InlineData(DeploymentTargets.ContentOnly, false, false, false, "no destination")]
    public void EveryTargetNameMapsOneToOneToItsDestinationsAndLabel(string target, bool configMgr, bool wsus, bool intune, string label)
    {
        var set = new DestinationSet(configMgr, wsus, intune);

        Assert.Equal(set, DestinationSet.FromTarget(target));
        Assert.Equal(configMgr, !DeploymentTargets.SkipsMecm(target));
        Assert.Equal(wsus, DeploymentTargets.PublishesToWsus(target));
        Assert.Equal(intune, DeploymentTargets.PublishesToIntune(target));
        Assert.Equal(label, DeploymentTargets.Label(target));
        if (set.Any) Assert.Equal(target, set.ToTargetName());
    }

    [Fact]
    public void TheSevenNamesCoverEveryNonEmptyCombinationOnce()
    {
        var sets = DeploymentTargets.Names.Select(DestinationSet.FromTarget).ToList();

        Assert.Equal(7, sets.Distinct().Count());
        Assert.All(sets, s => Assert.True(s.Any));
    }

    [Fact]
    public void TheFiveEarlierNamesAreWrittenExactlyAsBefore()
    {
        Assert.Equal("ConfigMgr", new DestinationSet(true, false, false).ToTargetName());
        Assert.Equal("ConfigMgrAndIntune", new DestinationSet(true, false, true).ToTargetName());
        Assert.Equal("IntuneOnly", new DestinationSet(false, false, true).ToTargetName());
        Assert.Equal("ConfigMgrAndWSUS", new DestinationSet(true, true, false).ToTargetName());
        Assert.Equal("WSUSOnly", new DestinationSet(false, true, false).ToTargetName());
        Assert.Equal("IntuneAndWSUS", new DestinationSet(false, true, true).ToTargetName());
        Assert.Equal("ConfigMgrAndIntuneAndWSUS", new DestinationSet(true, true, true).ToTargetName());
    }

    [Theory]
    [InlineData(DeploymentTargets.IntuneOnly)]
    [InlineData(DeploymentTargets.WsusOnly)]
    [InlineData(DeploymentTargets.IntuneAndWsus)]
    public void ARunWithoutConfigMgrNeverAsksForASite(string target)
    {
        var reason = DeploymentTargetGate.Validate(target, siteCode: "", fileShareRoot: "", tenantId: "t", clientId: "c", hasClientSecret: true);

        Assert.Null(reason);
    }

    [Fact]
    public void AllThreeDestinationsAskForTheSiteAndForIntuneCredentials()
    {
        Assert.Contains("Site Code", DeploymentTargetGate.Validate(DeploymentTargets.MecmAndIntuneAndWsus, "", @"\\srv\share", "t", "c", true)!);
        Assert.Contains("Intune credentials", DeploymentTargetGate.Validate(DeploymentTargets.MecmAndIntuneAndWsus, "MCM", @"\\srv\share", "", "c", true)!);
        Assert.Null(DeploymentTargetGate.Validate(DeploymentTargets.MecmAndIntuneAndWsus, "MCM", @"\\srv\share", "t", "c", true));
    }

    [Fact]
    public void AnUnknownTargetIsNeverAskedForASiteAndNeverPublishes()
    {
        Assert.Null(DeploymentTargetGate.Validate("nonsense", "", "", "", "", false));
    }
}
