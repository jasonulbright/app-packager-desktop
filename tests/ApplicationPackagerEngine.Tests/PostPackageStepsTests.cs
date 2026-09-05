using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Content layout paths and the two scripts that run after an MECM application
/// exists: distribution to a DP group and the test-collection deployment.
/// </summary>
public class PostPackageStepsTests
{
    [Fact]
    public void ContentLayout_NestedAndFlatFolders()
    {
        Assert.Equal(@"\\fs\sccm$\Applications\KDE e.V\KDiff3 (x64)\1.12.4",
            ContentLayouts.ContentPath(@"\\fs\sccm$", ContentLayouts.Nested, "KDE e.V", "KDiff3 (x64)", "1.12.4"));
        Assert.Equal(@"\\fs\sccm$\Applications\KDE e.V-KDiff3 (x64)-1.12.4",
            ContentLayouts.ContentPath(@"\\fs\sccm$", ContentLayouts.Flat, "KDE e.V", "KDiff3 (x64)", "1.12.4"));
    }

    [Theory]
    [InlineData(null, "Nested")]
    [InlineData("", "Nested")]
    [InlineData("flat", "Flat")]
    [InlineData("Flat", "Flat")]
    [InlineData("anything", "Nested")]
    public void ContentLayout_UnknownValuesFallBackToNested(string? stored, string expected)
    {
        Assert.Equal(expected, ContentLayouts.Normalize(stored));
    }

    [Fact]
    public void DistributionScript_TreatsAnAlreadyTargetedGroupAsDone()
    {
        var script = MecmTarget.BuildDistributionScript("Test App - 1.0 (x64)", "MCM", "All DPs", "cm01");

        Assert.Contains("Start-CMContentDistribution -ApplicationName 'Test App - 1.0 (x64)' -DistributionPointGroupName 'All DPs' -ErrorAction Stop", script);
        Assert.Contains("-match 'already been targeted|already distributed'", script);
        Assert.Contains("} else { throw }", script);
        Assert.Contains("New-PSDrive -Name 'MCM' -PSProvider CMSite -Root 'cm01'", script);
    }

    [Fact]
    public void TestDeploymentScript_CreatesTheCollectionOnlyWhenAsked()
    {
        var without = MecmTarget.BuildTestCollectionDeploymentScript("Test App", "MCM", "Pilot", false, null);
        var with = MecmTarget.BuildTestCollectionDeploymentScript("Test App", "MCM", "Pilot", true, null);

        Assert.DoesNotContain("New-CMDeviceCollection", without);
        Assert.Contains("New-CMDeviceCollection -Name 'Pilot' -LimitingCollectionName 'All Systems' -ErrorAction Stop", with);
        // A missing collection ends the script cleanly instead of failing the run.
        Assert.Contains("Test deployment skipped: collection Pilot not found", without);
        Assert.Contains("exit 0", without);
        // The deployment is Available and effective now; an existing one counts as done.
        Assert.Contains("-DeployAction Install -DeployPurpose Available -AvailableDateTime (Get-Date)", with);
        Assert.Contains("-match 'already (exists|deployed|been deployed)'", with);
    }

    [Fact]
    public void Scripts_EscapeSingleQuotesInNames()
    {
        var script = MecmTarget.BuildTestCollectionDeploymentScript("O'Brien's Tool", "MCM", "Pilot 'A'", true, null);

        Assert.Contains("-Name 'O''Brien''s Tool'", script);
        Assert.Contains("-CollectionName 'Pilot ''A'''", script);
    }
}
