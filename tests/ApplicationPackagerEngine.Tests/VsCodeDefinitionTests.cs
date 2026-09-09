using ApplicationPackagerEngine.Models;

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
}
