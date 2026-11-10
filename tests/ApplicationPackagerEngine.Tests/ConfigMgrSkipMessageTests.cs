using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A Skip run that finds the title on the site succeeds without creating anything. When the site holds another version than the
/// one in the stage (a title without a version, for example), the log must say that the new version was not published.
/// </summary>
public sealed class ConfigMgrSkipMessageTests
{
    private const string NotPublished = "was NOT published by this Skip run. Choose Overwrite to replace the deployment types.";

    [Fact]
    public void ADifferentVersionIsNamedAsNotPublished() =>
        Assert.Equal(
            "ConfigMgr holds 'Contoso App' at version 1.2.3; version 1.2.4 " + NotPublished,
            MecmTarget.ExistingApplicationSkipMessage("Contoso App", "1.2.3", "1.2.4"));

    [Fact]
    public void TheSameVersionSaysNothingChanged() =>
        Assert.Equal(
            "ConfigMgr already holds 'Contoso App' at version 1.2.3. Nothing was changed (OnExisting=Skip).",
            MecmTarget.ExistingApplicationSkipMessage("Contoso App", "1.2.3", "1.2.3"));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void AnUnreadVersionOnTheSiteIsStillReportedAsNotPublished(string siteVersion) =>
        Assert.Equal(
            "ConfigMgr holds 'Contoso App'; version 2.0 " + NotPublished,
            MecmTarget.ExistingApplicationSkipMessage("Contoso App", siteVersion, "2.0"));

    [Fact]
    public void AnEmptyStagedVersionDoesNotLeaveADoubleSpace() =>
        Assert.Equal(
            "ConfigMgr holds 'Contoso App' at version 1.2.3; the staged version " + NotPublished,
            MecmTarget.ExistingApplicationSkipMessage("Contoso App", "1.2.3", ""));

    // ---- the decision after the probe: the order of the arguments and the branches ----------------------------------

    [Fact]
    public void NoTitleOnTheSiteLetsTheRunGoOn()
    {
        Assert.Null(MecmTarget.DecideAfterProbe(OnExistingBehavior.Skip, false, "Contoso App", "", "1.2.4"));
        Assert.Null(MecmTarget.DecideAfterProbe(OnExistingBehavior.Fail, false, "Contoso App", "", "1.2.4"));
    }

    [Fact]
    public void SkipEndsTheRunWithSuccessKeepsTheApplicationAndLogsTheSiteVersionFirst()
    {
        var decision = MecmTarget.DecideAfterProbe(OnExistingBehavior.Skip, true, "Contoso App", "1.2.3", "1.2.4");

        Assert.NotNull(decision);
        Assert.True(decision.Result.Success);
        Assert.True(decision.Result.AlreadyExists);
        Assert.Equal("Contoso App", decision.Result.ApplicationName);
        Assert.Null(decision.Result.ErrorMessage);
        // The site version comes first, the staged version second: swapped arguments would turn this line around.
        Assert.Equal("ConfigMgr holds 'Contoso App' at version 1.2.3; version 1.2.4 " + NotPublished, decision.LogLine);
    }

    [Fact]
    public void FailEndsTheRunWithTheRefusalAndLogsNoSkipLine()
    {
        var decision = MecmTarget.DecideAfterProbe(OnExistingBehavior.Fail, true, "Contoso App", "1.2.3", "1.2.4");

        Assert.NotNull(decision);
        Assert.False(decision.Result.Success);
        Assert.False(decision.Result.AlreadyExists);
        Assert.Equal(MecmTarget.ExistingApplicationFailMessage("Contoso App", "1.2.3"), decision.Result.ErrorMessage);
        Assert.Null(decision.LogLine);
    }
}