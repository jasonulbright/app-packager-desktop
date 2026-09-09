using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// AutoInstall makes an application available to a task sequence, which runs
/// with no logged-on user. It is application-wide, so a deployment type that
/// installs into a user profile makes the flag invalid for the whole object.
/// </summary>
public sealed class TaskSequenceEligibilityTests
{
    private const string Content = @"\\server\share\Apps\Vendor\App\1.0";

    private static StageManifest Manifest(string installContext) => new()
    {
        AppName = "App",
        Publisher = "Vendor",
        SoftwareVersion = "1.0",
        InstallContext = installContext,
        Detection = new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"SOFTWARE\Vendor\App",
            ValueName = "DisplayVersion",
            ExpectedValue = "1.0"
        }
    };

    private static string Build(string installContext, OnExistingBehavior onExisting = OnExistingBehavior.Skip) =>
        MecmTarget.BuildPackageScript(Manifest(installContext), "App", Content, "P01", "comment", 15, 30,
            null, null, null, null, null, onExisting);

    [Fact]
    public void AMachineInstallStaysTaskSequenceEligible()
    {
        var script = Build("Machine");

        Assert.Contains("-AutoInstall $true", script);
        Assert.DoesNotContain("-AutoInstall $false", script);
        Assert.Contains("-InstallationBehaviorType InstallForSystem", script);
    }

    [Fact]
    public void AUserInstallIsCreatedWithoutTaskSequenceEligibility()
    {
        var script = Build("User");

        Assert.Contains("New-CMApplication -Name 'App'", script);
        Assert.Contains("-AutoInstall $false", script);
        Assert.DoesNotContain("-AutoInstall $true", script);
        Assert.Contains("-InstallationBehaviorType InstallForUser", script);
    }

    [Fact]
    public void AUserInstallRepairsAnExistingApplicationBeforeAddingDeploymentTypes()
    {
        var script = Build("User", OnExistingBehavior.Overwrite);

        Assert.Contains("Set-CMApplication -Name 'App' -AutoInstall $false -ErrorAction Stop | Out-Null", script);
        Assert.Contains("Task-sequence install        : disabled (deployment type requires a user context)", script);

        var repairAt = script.IndexOf("-AutoInstall $false -ErrorAction Stop", StringComparison.Ordinal);
        var addAt = script.IndexOf("Add-CMScriptDeploymentType", StringComparison.Ordinal);
        Assert.True(repairAt > 0 && addAt > repairAt);
    }

    [Fact]
    public void TheRepairOnlyRunsWhenTheFlagIsActuallySet()
    {
        // An operator who already cleared it is not written to again.
        Assert.Contains("if ($app.AutoInstall -ne $false) {", Build("User", OnExistingBehavior.Overwrite));
    }

    [Fact]
    public void ARetainedSameVersionApplicationIsRepairedToo()
    {
        // A Skip run keeps the deployment types but must still clear a flag an
        // earlier run set unconditionally.
        var script = Build("User");

        var repairAt = script.IndexOf("-AutoInstall $false -ErrorAction Stop", StringComparison.Ordinal);
        var exitAt = script.IndexOf("exit 0", StringComparison.Ordinal);
        Assert.True(repairAt > 0 && exitAt > repairAt);
    }

    [Fact]
    public void AMachineInstallNeverRepairsAnExistingApplication()
    {
        foreach (var behavior in new[]
                 { OnExistingBehavior.Skip, OnExistingBehavior.Overwrite, OnExistingBehavior.Fail })
        {
            Assert.DoesNotContain("-AutoInstall $false", Build("Machine", behavior));
        }
    }
}
