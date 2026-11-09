using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A deployment-type step that the site refuses must not leave a half-built application behind. An application that the run
/// created is removed; an application that was already there stays, and only the staging deployment types of the run go.
/// </summary>
public sealed class ConfigMgrFailedDeploymentTypeTests
{
    private static StageManifest Manifest() => new()
    {
        AppName = "App", Publisher = "Vendor", SoftwareVersion = "1.0",
        Detection = new StageDetection { Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\Vendor\App", ValueName = "DisplayVersion", ExpectedValue = "1.0" }
    };

    private static string Build(OnExistingBehavior onExisting) =>
        MecmTarget.BuildPackageScript(Manifest(), "App", @"\\server\share\Apps\Vendor\App\1.0", "P01", "comment", 15, 30,
            null, null, null, null, null, onExisting);

    [Theory]
    [InlineData(OnExistingBehavior.Skip)]
    [InlineData(OnExistingBehavior.Overwrite)]
    public void TheDeploymentTypeStepsRunInATryBlockThatEndsInTheCleanup(OnExistingBehavior onExisting)
    {
        var script = Build(onExisting);

        var tryAt = script.LastIndexOf("try {", script.IndexOf("Adding deployment type", StringComparison.Ordinal), StringComparison.Ordinal);
        var catchAt = script.IndexOf("} catch {", script.IndexOf("Renamed deployment type", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(tryAt > script.IndexOf("Creating application", StringComparison.Ordinal), "The try block starts after the application was created.");
        Assert.True(catchAt > tryAt, "The catch block follows the rename of the deployment types.");

        var cleanup = script[catchAt..script.IndexOf("Updated application version", StringComparison.Ordinal)];
        Assert.Contains("if ($createdApp)", cleanup);
        Assert.Contains("Remove-CMApplication -Name 'App' -Force -ErrorAction Stop", cleanup);
        Assert.Contains("(staging)", cleanup);
        Assert.Contains("    throw", cleanup);
        Assert.Contains("$createdApp = $true", script);
    }

    [Fact]
    public void OnlyTheApplicationThatTheRunCreatedIsRemoved()
    {
        var script = Build(OnExistingBehavior.Overwrite);

        // The flag is set in the branch that creates the application and nowhere else.
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(script, @"\$createdApp = \$true").Count);
        var createAt = script.IndexOf("Creating application", StringComparison.Ordinal);
        var flagAt = script.IndexOf("$createdApp = $true", StringComparison.Ordinal);
        Assert.InRange(flagAt - createAt, 1, 400);
    }
}
