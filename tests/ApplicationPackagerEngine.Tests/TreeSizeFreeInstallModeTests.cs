using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// TreeSize Free's Inno Setup script offers both install modes. The setup
/// names its uninstaller by install order, so the definition's own uninstall
/// reads the ARP UninstallString, per-user entries first.
/// </summary>
public class TreeSizeFreeInstallModeTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void TheDefinitionDeclaresBothModesAndTheFullSilentSwitchSet()
    {
        var def = DefinitionFixture.Load("treesizefree.json");

        Assert.True(def.OffersInstallModes);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /ALLUSERS /MERGETASKS=!desktopicon", def.InstallArgs);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", def.UninstallArgs);
    }

    [Fact]
    public void TheUninstallReadsTheUserUninstallTreeFirst()
    {
        var command = DefinitionFixture.Load("treesizefree.json").UninstallCommand!;

        var hkcu = command.IndexOf(@"'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'", StringComparison.Ordinal);
        var hklm = command.IndexOf(@"'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'", StringComparison.Ordinal);
        var wow = command.IndexOf(@"'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'", StringComparison.Ordinal);
        Assert.True(hkcu >= 0 && hkcu < hklm && hklm < wow, command);
        Assert.Contains("$_.DisplayName -like 'TreeSize Free*'", command);
        Assert.Contains("if (-not $cmd) { exit 0 }", command);
        Assert.Contains("-ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')", command);
    }

    /// <summary>
    /// A per-user stage runs the uninstaller the branch names, not a lookup
    /// that reads the machine uninstall tree only.
    /// </summary>
    [Fact]
    public void ACurrentUserStageTakesTheBranchUninstaller()
    {
        var def = DefinitionFixture.Load("treesizefree.json");
        var detection = StagingService.BuildDetection(def, null, "9.6.1");
        var notes = new List<string>();

        var result = StagingService.ApplyInstallMode(def, Fixture("inno-admin-modes-x64.bin"), "CurrentUser",
            def.InstallArgs, detection, notes.Add, def.UninstallCommand, "9.6.1");

        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /MERGETASKS=!desktopicon /CURRENTUSER", result.InstallArgs);
        Assert.Equal("User", result.InstallContext);
        Assert.StartsWith("$uninstaller = [Environment]::ExpandEnvironmentVariables('%LOCALAPPDATA%\\Programs\\", result.UninstallExpression);
        Assert.DoesNotContain("$arpPaths", result.UninstallExpression);
        Assert.Equal("RegistryKeyValue", result.Detection.Type);
        Assert.Equal("CurrentUser", result.Detection.Hive);
        Assert.Contains(notes, n => n.Contains("does not name the default uninstaller"));
    }

    [Fact]
    public void AnAllUsersStageKeepsAMachineContext()
    {
        var def = DefinitionFixture.Load("treesizefree.json");
        var detection = StagingService.BuildDetection(def, null, "9.6.1");

        var result = StagingService.ApplyInstallMode(def, Fixture("inno-admin-modes-x64.bin"), "AllUsers",
            def.InstallArgs, detection, null, def.UninstallCommand, "9.6.1");

        Assert.Equal("Machine", result.InstallContext);
        Assert.Equal("File", result.Detection.Type);
    }
}
