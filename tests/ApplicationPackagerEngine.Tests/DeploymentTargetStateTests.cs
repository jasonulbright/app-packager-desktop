using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Tests;

public sealed class DeploymentTargetStateTests
{
    [Theory]
    [InlineData(DeploymentTargets.Mecm)]
    [InlineData(DeploymentTargets.MecmAndIntune)]
    [InlineData(null)]
    [InlineData("nonsense")]
    public void MecmTargetsKeepThePackageActionBar(string? target)
    {
        var state = DeploymentTargetActionState.For(target);

        Assert.True(state.CheckMecmEnabled);
        Assert.False(state.SkipMecmPreflight);
        Assert.Equal("Package", state.PackageLabel);
        Assert.Contains("MECM Application", state.PackageToolTip);
        Assert.Contains("currently deployed version", state.CheckMecmToolTip);
    }

    [Fact]
    public void IntuneOnlyDisablesCheckMecmAndRelabelsPackage()
    {
        var state = DeploymentTargetActionState.For(DeploymentTargets.IntuneOnly);

        Assert.False(state.CheckMecmEnabled);
        Assert.True(state.SkipMecmPreflight);
        Assert.Equal("Publish", state.PackageLabel);
        Assert.Contains("ConfigMgr site", state.CheckMecmToolTip);
        Assert.Contains("Intune", state.PackageToolTip);
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
    [InlineData(null, DeploymentTargets.Mecm)]
    [InlineData("", DeploymentTargets.Mecm)]
    [InlineData("MECM", DeploymentTargets.Mecm)]
    [InlineData("MECMAndIntune", DeploymentTargets.MecmAndIntune)]
    [InlineData("IntuneOnly", DeploymentTargets.IntuneOnly)]
    [InlineData("intuneonly", DeploymentTargets.Mecm)]
    public void NormalizeMapsUnknownValuesOntoMecm(string? stored, string expected)
        => Assert.Equal(expected, DeploymentTargets.Normalize(stored));
}
