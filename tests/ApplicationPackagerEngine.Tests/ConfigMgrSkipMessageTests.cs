using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A Skip run that finds the title on the site succeeds without creating anything. When the site holds another version than the
/// one in the stage (a title without a version, for example), the log must say that the new version was not published.
/// </summary>
public sealed class ConfigMgrSkipMessageTests
{
    [Fact]
    public void ADifferentVersionIsNamedAsNotPublished()
    {
        var message = MecmTarget.ExistingApplicationSkipMessage("Contoso App", "1.2.3", "1.2.4");

        Assert.Contains("'Contoso App'", message);
        Assert.Contains("version 1.2.3", message);
        Assert.Contains("version 1.2.4 was NOT published", message);
        Assert.Contains("OnExisting=Skip", message);
        Assert.Contains("Overwrite", message);
    }

    [Fact]
    public void TheSameVersionSaysNothingChanged()
    {
        var message = MecmTarget.ExistingApplicationSkipMessage("Contoso App", "1.2.3", "1.2.3");

        Assert.Contains("already holds 'Contoso App' at version 1.2.3", message);
        Assert.Contains("Nothing was changed", message);
        Assert.DoesNotContain("NOT published", message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void AnUnreadVersionOnTheSiteIsStillReportedAsNotPublished(string siteVersion)
    {
        var message = MecmTarget.ExistingApplicationSkipMessage("Contoso App", siteVersion, "2.0");

        Assert.Contains("version 2.0 was NOT published", message);
        Assert.DoesNotContain("at version  ", message);
    }
}
