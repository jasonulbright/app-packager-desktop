using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A deployment-type step that the site refuses must not leave a half-built application behind. An application that the run
/// created is removed. For an application that was already there, only the deployment types that this run created are removed,
/// and none once an old deployment type has been removed, because then the new ones are the replacement.
/// </summary>
public sealed class ConfigMgrFailedDeploymentTypeTests
{
    private const string Unc = @"\\server\share\Apps\Vendor\App\1.0";

    private static StageManifest Manifest() => new()
    {
        AppName = "App", Publisher = "Vendor", SoftwareVersion = "1.0",
        Detection = new StageDetection { Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\Vendor\App", ValueName = "DisplayVersion", ExpectedValue = "1.0" }
    };

    private static string Build(OnExistingBehavior onExisting) =>
        MecmTarget.BuildPackageScript(Manifest(), "App", Unc, "P01", "comment", 15, 30, null, null, null, null, null, onExisting);

    [Theory]
    [InlineData(OnExistingBehavior.Skip)]
    [InlineData(OnExistingBehavior.Overwrite)]
    public void TheDeploymentTypeStepsRunInATryBlockThatCallsTheCleanupAndThrowsTheOriginalError(OnExistingBehavior onExisting)
    {
        var script = Build(onExisting);

        var tryAt = script.LastIndexOf("try {", script.IndexOf("Adding deployment type", StringComparison.Ordinal), StringComparison.Ordinal);
        var catchAt = script.IndexOf("} catch {", script.IndexOf("Renamed deployment type", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(tryAt > script.IndexOf("Creating application", StringComparison.Ordinal), "The try block starts after the application was created.");
        Assert.True(catchAt > tryAt, "The catch block follows the rename of the deployment types.");

        var cleanup = script[catchAt..script.IndexOf("Updated application version", StringComparison.Ordinal)];
        Assert.Contains("Remove-ApkgIncompleteWork -ApplicationName 'App' -SiteCode 'P01' -CreatedApp $createdApp -CreatedStaging $createdStaging -OldDtRemoved $oldDtRemoved", cleanup);
        Assert.Contains("throw $stepError", cleanup);
        Assert.Contains(MecmTarget.IncompleteWorkCleanupFunction.ReplaceLineEndings(), script);
    }

    [Fact]
    public void TheRunRecordsWhatItCreatedAndRemoved()
    {
        var script = Build(OnExistingBehavior.Overwrite);

        // The flag is set in the branch that creates the application and nowhere else.
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(script, @"\$createdApp = \$true").Count);
        var createAt = script.IndexOf("Creating application", StringComparison.Ordinal);
        Assert.InRange(script.IndexOf("$createdApp = $true", StringComparison.Ordinal) - createAt, 1, 400);

        // A deployment type is recorded right after it was added, and the removal of an old one is recorded after the removal.
        var addAt = script.IndexOf("Add-CMScriptDeploymentType", StringComparison.Ordinal);
        var recordAt = script.IndexOf("$createdStaging += $dtCreateName0", StringComparison.Ordinal);
        Assert.True(recordAt > addAt, "A deployment type is recorded after the add.");
        var removeAt = script.IndexOf("Remove-CMDeploymentType -ApplicationName 'App' -DeploymentTypeName $oldDt", StringComparison.Ordinal);
        Assert.True(script.IndexOf("$oldDtRemoved = $true", removeAt, StringComparison.Ordinal) > removeAt);
    }

    /// <summary>
    /// The cleanup function runs against stand-ins for the cmdlets. It is given the names of the deployment types that this run
    /// created, and it never asks the site for others: an operator's deployment type, or one that an earlier run left, is out of
    /// its reach even when it is named like a staging type.
    /// </summary>
    [Theory]
    [InlineData("created-app", true, false, "Removed the incomplete application", "REMOVED-APP")]
    [InlineData("created-app-refused", true, false, "The incomplete application was not removed: App is in use", "")]
    [InlineData("staging", false, false, "Removed deployment type of this run: New (staging)", "REMOVED-DT New (staging)")]
    [InlineData("old-removed", false, true, "An old deployment type was already removed", "")]
    public async Task TheCleanupRemovesOnlyWhatThisRunCreated(string case_, bool createdApp, bool oldRemoved, string expectedOutput, string expectedRemoval)
    {
        var standIns = $$"""
            function Set-Location { param($Path) }
            function Get-CMDeploymentType { Write-Host 'QUERIED-SITE'; [pscustomobject]@{ LocalizedDisplayName = 'Operator (staging)' } }
            function Remove-CMApplication {
                [CmdletBinding()] param($Name, [switch]$Force)
                if ('{{case_}}' -eq 'created-app-refused') { throw 'App is in use' }
                Write-Host 'REMOVED-APP'
            }
            function Remove-CMDeploymentType {
                [CmdletBinding()] param($ApplicationName, $DeploymentTypeName, [switch]$Force)
                Write-Host "REMOVED-DT $DeploymentTypeName"
            }
            """;
        var call = $"Remove-ApkgIncompleteWork -ApplicationName 'App' -SiteCode 'P01' -CreatedApp ${createdApp.ToString().ToLowerInvariant()} " +
                   $"-CreatedStaging @('New (staging)') -OldDtRemoved ${oldRemoved.ToString().ToLowerInvariant()}";

        var result = await WindowsPowerShellHost.RunAsync(
            standIns + Environment.NewLine + MecmTarget.IncompleteWorkCleanupFunction + Environment.NewLine + call, null, default);

        Assert.True(result.ExitCode == 0, result.StdErr);
        Assert.Contains(expectedOutput, result.StdOut);
        if (expectedRemoval.Length > 0) Assert.Contains(expectedRemoval, result.StdOut);
        else Assert.DoesNotContain("REMOVED-", result.StdOut);
        Assert.DoesNotContain("QUERIED-SITE", result.StdOut);
        Assert.DoesNotContain("Operator (staging)", result.StdOut);
        Assert.Equal(expectedRemoval.StartsWith("REMOVED-DT") ? 1 : 0, result.StdOut.Split('\n').Count(l => l.Contains("REMOVED-DT")));
    }
}
