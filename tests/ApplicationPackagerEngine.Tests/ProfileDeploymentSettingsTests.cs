using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The install step fields of a profile (return codes, reboot policy, working directory) are no longer stored and ignored:
/// they reach the Intune return code table, the post-execution behavior, the WSUS exit code lists and the ConfigMgr script.
/// </summary>
public sealed class ProfileDeploymentSettingsTests
{
    [Fact]
    public void ABareNumberIsASuccessAndATypeIsCaseInsensitive()
    {
        var codes = ProfileDeploymentSettings.ParseReturnCodes("0, 5, 3011=softreboot;1641=HardReboot\n99=FastRetry");

        Assert.Equal(
            [("0", "Success"), ("5", "Success"), ("3011", "SoftReboot"), ("1641", "HardReboot"), ("99", "FastRetry")],
            codes.Select(c => (c.Code.ToString(), c.Type)));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12=Maybe")]
    [InlineData("12=Success,12=Failed")]
    [InlineData("=Success")]
    public void AMistakeInTheListIsRefusedWithTheEntryNamed(string text)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ProfileDeploymentSettings.ParseReturnCodes(text));
        Assert.Contains("Install.ReturnCodes", ex.Message);
    }

    [Fact]
    public void AnEmptyListChangesNothing()
    {
        Assert.Empty(ProfileDeploymentSettings.ParseReturnCodes("  "));
        var table = ProfileDeploymentSettings.IntuneReturnCodes([]);
        Assert.Equal(ProfileDeploymentSettings.IntuneDefaults, table);
    }

    [Fact]
    public void TheIntuneTableKeepsTheDefaultsReplacesAMatchingCodeAndAddsTheRest()
    {
        var codes = ProfileDeploymentSettings.ParseReturnCodes("1618=Failed,3011=SoftReboot,8=FastRetry");

        var table = ProfileDeploymentSettings.IntuneReturnCodes(codes).ToDictionary(t => t.Code, t => t.Graph);

        Assert.Equal("failed", table[1618]);
        Assert.Equal("softReboot", table[3011]);
        Assert.Equal("retry", table[8]);
        Assert.Equal("success", table[1707]);
        Assert.Equal("hardReboot", table[1641]);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Inherit", null)]
    [InlineData("No reboot", "NoAction")]
    [InlineData("Soft reboot", "BasedOnExitCode")]
    [InlineData("Hard reboot", "ProgramReboot")]
    [InlineData("Force restart", "ForceReboot")]
    [InlineData("ForceReboot", "ForceReboot")]
    public void ARebootPolicyMapsToAPostExecutionBehavior(string? policy, string? expected) =>
        Assert.Equal(expected, ProfileDeploymentSettings.PostExecutionBehaviorFor(policy));

    [Fact]
    public void AnUnknownRebootPolicyIsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ProfileDeploymentSettings.PostExecutionBehaviorFor("Sometimes"));
        Assert.Contains("Install.RebootPolicy", ex.Message);
    }

    [Fact]
    public void AWorkingDirectoryWithALineBreakIsRefused()
    {
        Assert.Null(ProfileDeploymentSettings.WorkingDirectoryFor("  ", "Install"));
        Assert.Equal(@"C:\Temp", ProfileDeploymentSettings.WorkingDirectoryFor(@" C:\Temp ", "Install"));
        Assert.Throws<InvalidOperationException>(() => ProfileDeploymentSettings.WorkingDirectoryFor("C:\\a\r\nb", "Install"));
    }

    [Fact]
    public void ApplyJoinsTheCodesToTheWsusListsAndSetsTheBehavior()
    {
        var manifest = new StageManifest
        {
            InstallReturnCodes = JsonValue.Create("0,5,3011=SoftReboot,1641=HardReboot,7=Failed"),
            InstallRebootPolicy = JsonValue.Create("Force restart"),
            SuccessExitCodes = [9],
            RebootExitCodes = [3010]
        };

        ProfileDeploymentSettings.Apply(manifest);

        Assert.Equal("ForceReboot", manifest.PostExecutionBehavior);
        Assert.Equal([9, 5], manifest.SuccessExitCodes);
        Assert.Equal([3010, 3011, 1641], manifest.RebootExitCodes);
    }

    [Fact]
    public void ApplyWithNoProfileFieldsLeavesTheManifestAsItWas()
    {
        var manifest = new StageManifest { PostExecutionBehavior = "NoAction" };

        ProfileDeploymentSettings.Apply(manifest);

        Assert.Equal("NoAction", manifest.PostExecutionBehavior);
        Assert.Null(manifest.SuccessExitCodes);
        Assert.Null(manifest.RebootExitCodes);
    }

    [Fact]
    public void TheIntuneArrayOfAManifestCarriesTheProfileCodes()
    {
        var manifest = new StageManifest { InstallReturnCodes = JsonValue.Create("3011=SoftReboot") };

        var array = ProfileDeploymentSettings.IntuneReturnCodeArray(manifest);

        Assert.Contains(array, n => n!["returnCode"]!.GetValue<int>() == 3011 && n["type"]!.GetValue<string>() == "softReboot");
        Assert.Equal(6, array.Count);
    }

    [Fact]
    public void TheWorkingDirectoriesReachTheConfigMgrDeploymentType()
    {
        var manifest = new StageManifest
        {
            AppName = "Widget",
            InstallerType = "EXE",
            InstallWorkingDirectory = JsonValue.Create(@"C:\Temp\O'Neil"),
            UninstallWorkingDirectory = JsonValue.Create(@"%ProgramData%\Widget"),
            Detection = new StageDetection { Type = "File", FilePath = @"%SystemRoot%\System32", FileName = "w.dll", ExpectedValue = "1.0" }
        };

        var script = MecmTarget.BuildPackageScript(manifest, "Widget", @"\\s\c", "MCM", "", 15, 30);

        Assert.Contains(@" -InstallWorkingDirectory 'C:\Temp\O''Neil' -UninstallWorkingDirectory '%ProgramData%\Widget' -UserInteractionMode Hidden", script);

        manifest.InstallWorkingDirectory = null;
        manifest.UninstallWorkingDirectory = null;
        var plain = MecmTarget.BuildPackageScript(manifest, "Widget", @"\\s\c", "MCM", "", 15, 30);
        Assert.DoesNotContain("WorkingDirectory", plain);
    }
}
