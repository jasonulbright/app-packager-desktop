using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The test runner labels an install exit code and decides whether the later phases run. A label that
/// starts with PASS must let detection run: exit 1641 (reboot started by the installer) is such a label.
/// </summary>
public sealed class DefinitionTestRunnerInstallGateTests
{
    [Fact]
    public async Task RunAsyncWithHardRebootInitiatedStillEvaluatesDetection()
    {
        var stagedContentPath = Path.Combine(Path.GetTempPath(), $"test-runner-1641-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagedContentPath);
        try
        {
            File.WriteAllText(Path.Combine(stagedContentPath, "install.bat"),
                "@echo off\r\necho installed>installed.marker\r\nexit /b 1641\r\n");
            var manifest = new StageManifest
            {
                AppName = "Installer test",
                InstallContext = "User",
                Detection = new StageDetection
                {
                    Type = "File",
                    FilePath = stagedContentPath,
                    FileName = "installed.marker",
                    PropertyType = "Existence"
                }
            };
            var definition = new AppDefinition { Id = "installer-test", CanUninstall = false };

            var result = await new DefinitionTestRunner().RunAsync(stagedContentPath, manifest, definition);

            Assert.Equal(1641, result.InstallExitCode);
            Assert.Equal("PASS (hard reboot initiated)", result.InstallResult);
            Assert.Equal("PASS", result.DetectResult);
            Assert.Equal("PASS", result.OverallResult);
        }
        finally
        {
            Directory.Delete(stagedContentPath, recursive: true);
        }
    }

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
