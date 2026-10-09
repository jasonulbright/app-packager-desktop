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
        Assert.Contains("New-CMDeviceCollection -Name 'Pilot' -LimitingCollectionId 'SMS00001' -ErrorAction Stop", with);
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
        Assert.Contains("Get-CMCollection -CollectionType Device -Name 'Pilot ''A''' -DisableWildcardHandling", script);
        Assert.Contains("-CollectionId $collection.CollectionID", script);
    }

    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(true, 1, false)]
    [InlineData(false, 0, true)]
    [InlineData(true, 0, true)]
    [InlineData(true, 0, false)]
    [InlineData(true, 2, true)]
    [InlineData(false, -1, true)]
    [InlineData(true, -1, true)]
    public async Task TestDeploymentResolvesOneExactCollectionBeforeDeploying(bool users, int matches, bool create)
    {
        var kind = users ? "User" : "Device";
        var limitingId = users ? "SMS00002" : "SMS00001";
        var standIns = $$"""
            $env:SMS_ADMIN_UI_PATH = $env:TEMP
            function Import-Module { param($Name) }
            function Get-PSDrive { [CmdletBinding()] param($Name) return 'MCM' }
            function Set-Location { param($Path) }
            function Get-CMCollection {
                [CmdletBinding()] param($CollectionType, $Name, [switch]$DisableWildcardHandling)
                if ($CollectionType -ne '{{kind}}') { throw 'Wrong collection type' }
                if ($Name -ne "Pilot [A]'s" -or -not $DisableWildcardHandling) { throw 'Name was not treated literally' }
                if ({{matches}} -lt 0) { throw 'Lookup denied' }
                for ($i = 0; $i -lt {{matches}}; $i++) { [pscustomobject]@{ CollectionID = "MCM0001$i" } }
            }
            function New-CM{{kind}}Collection {
                [CmdletBinding()] param($Name, $LimitingCollectionId)
                if ($LimitingCollectionId -ne '{{limitingId}}') { throw 'Wrong limiting collection' }
                Write-Host 'CREATED {{kind}}'
                [pscustomobject]@{ CollectionID = 'MCM00010' }
            }
            function New-CMApplicationDeployment {
                [CmdletBinding()] param($Name, $CollectionId, $DeployAction, $DeployPurpose, $AvailableDateTime)
                if ($Name -ne "O'Brien's Tool" -or $CollectionId -ne 'MCM00010') { throw 'Wrong application or target ID' }
                if ($DeployAction -ne 'Install' -or $DeployPurpose -ne 'Available') { throw 'Wrong deployment policy' }
                Write-Host 'DEPLOYED MCM00010'
            }
            """;
        var script = MecmTarget.BuildTestCollectionDeploymentScript("O'Brien's Tool", "MCM", "Pilot [A]'s", create, null, users);

        var result = await WindowsPowerShellHost.RunAsync(standIns + Environment.NewLine + script, null, default);

        if (matches < 0 || matches > 1)
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(matches < 0 ? "Lookup denied" : "Multiple", result.StdErr);
            Assert.DoesNotContain("CREATED", result.StdOut);
            Assert.DoesNotContain("DEPLOYED", result.StdOut);
        }
        else
        {
            Assert.True(result.ExitCode == 0, result.StdErr);
            Assert.Equal(matches == 0 && create, result.StdOut.Contains("CREATED " + kind));
            Assert.Equal(matches == 1 || create, result.StdOut.Contains("DEPLOYED MCM00010"));
        }
    }
}
