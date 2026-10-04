using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// VS Code ships two installers that land in different places. The system
/// installer writes under Program Files and the user installer under the
/// profile, so one definition cannot describe both.
/// </summary>
public class VsCodeDefinitionTests
{
    [Fact]
    public void TheSystemDefinitionInstallsAndDetectsUnderProgramFiles()
    {
        var def = DefinitionFixture.Load("vscode.json");

        Assert.Equal("Machine", def.InstallContext);
        Assert.Equal("https://update.code.visualstudio.com/{version}/win32-x64/stable", def.DownloadUrl);
        Assert.Equal("VSCodeSetup-x64-{version}.exe", def.InstallerFileName);
        Assert.Equal("/VERYSILENT /NORESTART /MERGETASKS=!runcode", def.InstallArgs);
        Assert.Equal(@"""%ProgramFiles%\Microsoft VS Code\unins000.exe"" /VERYSILENT /NORESTART", def.UninstallCommand);
        Assert.Equal("File", def.Detection.Type);
        Assert.Equal(@"C:\Program Files\Microsoft VS Code", def.Detection.FilePath);
        Assert.Equal("Code.exe", def.Detection.FileName);
    }

    [Fact]
    public void TheUserDefinitionDetectsThroughTheUserUninstallKey()
    {
        var def = DefinitionFixture.Load("vscode-user.json");

        Assert.Equal("User", def.InstallContext);
        Assert.Equal("https://update.code.visualstudio.com/{version}/win32-x64-user/stable", def.DownloadUrl);
        Assert.Equal("VSCodeUserSetup-x64-{version}.exe", def.InstallerFileName);
        Assert.Equal("RegistryKeyValue", def.Detection.Type);
        Assert.Equal("HKCU", def.Detection.RegistryHive);
        Assert.Equal(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{771FD6B0-FA20-440A-A002-3B3BAC16DC50}_is1",
            def.Detection.RegistryKeyTemplate);
        Assert.Equal("DisplayVersion", def.Detection.ValueName);
        Assert.False(def.Detection.Is64Bit);
    }

    /// <summary>The profile path is a run-time value, not a packaging-time one.</summary>
    [Fact]
    public void TheUserUninstallerIsFoundThroughAnEnvironmentToken()
    {
        var def = DefinitionFixture.Load("vscode-user.json");

        Assert.Equal(@"""%LOCALAPPDATA%\Programs\Microsoft VS Code\unins000.exe"" /VERYSILENT /NORESTART",
            def.UninstallCommand);
        Assert.Contains("ExpandEnvironmentVariables",
            ApplicationPackagerEngine.Services.WrapperGeneratorService.NormalizeUninstallCommand(def.UninstallCommand));
    }

    [Fact]
    public void TheTwoDefinitionsDoNotShareAnIdentity()
    {
        var system = DefinitionFixture.Load("vscode.json");
        var user = DefinitionFixture.Load("vscode-user.json");

        Assert.NotEqual(system.Id, user.Id);
        Assert.NotEqual(system.CMName, user.CMName);
        Assert.NotEqual(system.Application, user.Application);
    }

    [Theory]
    [InlineData("vscode.json", "Machine", "InstallForSystem")]
    [InlineData("vscode-user.json", "User", "InstallForUser")]
    public void PackagedInstallScopeAndDetectionFollowTheSelectedDefinition(
        string definitionFile, string installContext, string installBehavior)
    {
        var definition = DefinitionFixture.Load(definitionFile);
        const string version = "1.2.3";
        var detection = StagingService.BuildDetection(definition, null, version, version);
        var manifest = new StageManifest
        {
            AppName = definition.CMName!,
            DisplayName = definition.Application,
            Publisher = definition.Vendor,
            SoftwareVersion = version,
            InstallerFile = definition.InstallerFileName!.Replace("{version}", version, StringComparison.OrdinalIgnoreCase),
            InstallerType = definition.InstallerType,
            Architecture = definition.Architecture,
            InstallContext = definition.InstallContext,
            InstallArgs = definition.InstallArgs,
            UninstallArgs = definition.UninstallArgs,
            UninstallCommand = definition.UninstallCommand,
            Detection = detection
        };

        var script = MecmTarget.BuildPackageScript(manifest, manifest.AppName,
            @"\\server\share\Applications\Microsoft\Visual Studio Code\1.2.3", "P01", "", 15, 30,
            ConditionTemplateService.Defaults());

        Assert.Equal(installContext, manifest.InstallContext);
        Assert.Equal("1.2.3", detection.ExpectedValue);
        Assert.Contains($"-InstallationBehaviorType {installBehavior}", script);
        if (installContext == "User")
        {
            Assert.Equal("CurrentUser", detection.Hive);
            Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{771FD6B0-FA20-440A-A002-3B3BAC16DC50}_is1",
                detection.RegistryKeyRelative);
            Assert.Contains("-Hive CurrentUser", script);
            Assert.DoesNotContain("-LogonRequirementType", script);
            Assert.StartsWith(@"""%LOCALAPPDATA%\Programs\Microsoft VS Code\unins000.exe""", manifest.UninstallCommand);
        }
        else
        {
            Assert.Equal(@"C:\Program Files\Microsoft VS Code", detection.FilePath);
            Assert.Contains("New-CMDetectionClauseFile", script);
            Assert.Contains("-LogonRequirementType WhetherOrNotUserLoggedOn", script);
        }
    }
}
