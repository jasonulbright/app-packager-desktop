using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The test runner labels an install exit code and decides whether the later phases run. A label that
/// starts with PASS must let detection run: exit 1641 (reboot started by the installer) is such a label.
/// </summary>
public sealed class DefinitionTestRunnerInstallGateTests
{
    [Theory]
    [InlineData(0, "PASS")]
    [InlineData(3010, "PASS (reboot required)")]
    [InlineData(1641, "PASS (hard reboot initiated)")]
    public void AnInstallExitThatMeansSuccessLabelsPassAndRunsTheNextPhase(int exit, string label)
    {
        var (result, proceed) = DefinitionTestRunner.ClassifyInstallExit(exit);

        Assert.Equal(label, result);
        Assert.True(proceed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1603)]
    [InlineData(1618)]
    public void AnyOtherInstallExitFailsAndStopsThePhases(int exit)
    {
        var (result, proceed) = DefinitionTestRunner.ClassifyInstallExit(exit);

        Assert.Equal($"FAIL (exit {exit})", result);
        Assert.False(proceed);
    }
}
