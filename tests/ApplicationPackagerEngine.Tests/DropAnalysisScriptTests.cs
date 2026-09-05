using System.Text;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Drop analysis over NSIS and Inno Setup fixtures: identity, key, hive, view,
/// switches and install modes come from the compiled script, and a chosen
/// mode swaps the whole branch at stage time.
/// </summary>
public sealed class DropAnalysisScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"packr-dropscript-{Guid.NewGuid():N}");

    public DropAnalysisScriptTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static DroppedInstallerAnalysisService Service() => new(new FakeMsiInspector { Throws = true });

    /// <summary>Copies a fixture under a real installer name so the analysis sees the drop as the operator would.</summary>
    private string Fixture(string name, string asFileName)
    {
        var target = Path.Combine(_root, asFileName);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), target, true);
        return target;
    }

    [Fact]
    public async Task NsisDrop_TakesIdentityKeyAndSwitchesFromTheScript()
    {
        var path = Fixture("nsis-u-none.bin", "TestApp-Setup-1.2.3.exe");
        var analysis = await Service().AnalyzeAsync(path);

        Assert.Equal("NSIS", analysis.Engine);
        Assert.Equal("Test App", analysis.AppName.Value);
        Assert.Equal(AnalysisConfidence.Confirmed, analysis.AppName.Confidence);
        Assert.Equal("nsis-compiled-script", analysis.AppName.Source);
        Assert.Equal("1.2.3", analysis.SoftwareVersion.Value);
        Assert.Equal("Test Co", analysis.Publisher.Value);
        Assert.False(analysis.RequiresConfirmation);
        Assert.Equal("/S", analysis.InstallArgs.Value);
        Assert.Equal(@"""%LOCALAPPDATA%\TestApp\Uninstall.exe"" /S", analysis.UninstallCommand.Value);
        Assert.Equal(AnalysisConfidence.Confirmed, analysis.UninstallCommand.Confidence);
        Assert.Equal(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", analysis.UninstallRegistryKey.Value);
        Assert.Equal(AnalysisConfidence.Confirmed, analysis.UninstallRegistryKey.Confidence);
        Assert.Equal("User", analysis.InstallContext);
        Assert.Equal("CurrentUser", analysis.Detection.Hive);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", analysis.Detection.RegistryKeyRelative);
        Assert.Equal(["CurrentUser"], analysis.InstallModes);
        Assert.False(analysis.HasInstallModeChoice);
        Assert.Contains("compiled NSIS script", analysis.DetectionHint);
    }

    [Fact]
    public async Task NsisDrop_ScriptViewBeatsThePeArchitecture()
    {
        // A 32-bit key path: HKLM without SetRegView 64 lands under WOW6432Node.
        var path = Fixture("nsis-u-pf-hklm-wow.bin", "TestApp-Setup.exe");
        var analysis = await Service().AnalyzeAsync(path);

        Assert.Equal(@"HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", analysis.UninstallRegistryKey.Value);
        Assert.False(analysis.Detection.Is64Bit);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", analysis.Detection.RegistryKeyRelative);
        Assert.Equal("Machine", analysis.InstallContext);
        Assert.Equal("LocalMachine", analysis.Detection.Hive);
    }

    [Fact]
    public async Task NsisDrop_WithAModeSwitch_OffersBothModesAndStagesTheChosenBranch()
    {
        var path = Fixture("nsis-u-multimode.bin", "TestApp-Setup.exe");
        var service = Service();
        var analysis = await service.AnalyzeAsync(path);

        Assert.True(analysis.HasInstallModeChoice);
        Assert.Equal(["CurrentUser", "AllUsers"], analysis.InstallModes);
        Assert.Equal("CurrentUser", analysis.DefaultInstallMode);

        var user = service.BuildStageManifest(analysis);
        Assert.Equal("User", user.InstallContext);
        Assert.Equal("/S", user.InstallArgs);
        Assert.Equal("CurrentUser", user.Detection.Hive);

        var machine = service.BuildStageManifest(analysis, new DroppedInstallerOverrides { InstallMode = "AllUsers" });
        Assert.Equal("Machine", machine.InstallContext);
        Assert.Equal("/S /allusers", machine.InstallArgs);
        Assert.Equal(@"""%ProgramW6432%\TestApp\Uninstall Test App.exe"" /S /allusers", machine.UninstallCommand);
        Assert.Equal("LocalMachine", machine.Detection.Hive);
        Assert.True(machine.Detection.Is64Bit);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", machine.Detection.RegistryKeyRelative);

        // The same mode as the default changes nothing; an unknown one is refused.
        var same = service.BuildStageManifest(analysis, new DroppedInstallerOverrides { InstallMode = "CurrentUser" });
        Assert.Equal("/S", same.InstallArgs);
        var single = await service.AnalyzeAsync(Fixture("nsis-u-single.bin", "Single-Setup.exe"));
        Assert.Throws<InvalidOperationException>(() =>
            service.BuildStageManifest(single, new DroppedInstallerOverrides { InstallMode = "AllUsers" }));
    }

    [Fact]
    public async Task InnoDrop_TakesIdentityKeyAndSwitchesFromTheHeader()
    {
        var path = Fixture("inno-admin-670.bin", "TestApp-1.2.3-setup.exe");
        var analysis = await Service().AnalyzeAsync(path);

        Assert.Equal("InnoSetup", analysis.Engine);
        Assert.Equal("Test App", analysis.AppName.Value);
        Assert.Equal("inno-setup-header", analysis.AppName.Source);
        Assert.Equal("1.2.3", analysis.SoftwareVersion.Value);
        Assert.False(analysis.RequiresConfirmation);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-", analysis.InstallArgs.Value);
        Assert.Equal(@"""%ProgramFiles%\Test App\unins000.exe"" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART", analysis.UninstallCommand.Value);
        Assert.Equal(@"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Test App_is1", analysis.UninstallRegistryKey.Value);
        Assert.True(analysis.Detection.Is64Bit);
        Assert.Equal("Machine", analysis.InstallContext);
        Assert.Equal(["AllUsers"], analysis.InstallModes);
    }

    [Fact]
    public async Task InnoDrop_WithCommandLineOverride_StagesThePerUserBranchOnRequest()
    {
        var path = Fixture("inno-admin-modes.bin", "WinSCP-setup.exe");
        var service = Service();
        var analysis = await service.AnalyzeAsync(path);

        Assert.Equal(["CurrentUser", "AllUsers"], analysis.InstallModes);
        Assert.Equal("AllUsers", analysis.DefaultInstallMode);
        Assert.False(analysis.Detection.Is64Bit);

        var perUser = service.BuildStageManifest(analysis, new DroppedInstallerOverrides { InstallMode = "CurrentUser" });
        Assert.Equal("User", perUser.InstallContext);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CURRENTUSER", perUser.InstallArgs);
        Assert.Equal(@"""%LOCALAPPDATA%\Programs\WinSCP\unins000.exe"" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART", perUser.UninstallCommand);
        Assert.Equal("CurrentUser", perUser.Detection.Hive);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\winscp3_is1", perUser.Detection.RegistryKeyRelative);

        var definition = service.BuildDefinition(analysis, new DroppedInstallerOverrides { InstallMode = "CurrentUser" });
        Assert.Equal("User", definition.InstallContext);
        Assert.Equal("HKCU", definition.Detection.RegistryHive);
        Assert.Contains("[Environment]::ExpandEnvironmentVariables('%LOCALAPPDATA%\\Programs\\WinSCP\\unins000.exe')", definition.UninstallCommand);
        Assert.Contains("$proc = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')", definition.UninstallCommand);
    }

    [Fact]
    public async Task UnreadableScript_FallsBackToTheEngineConvention()
    {
        var path = Fixture("nsis-mk-bzip2-solid.bin", "Probe-Setup.exe");
        var analysis = await Service().AnalyzeAsync(path);

        Assert.Equal("NSIS", analysis.Engine);
        Assert.Empty(analysis.InstallModes);
        Assert.NotEqual("nsis-compiled-script", analysis.AppName.Source);
        Assert.Equal(AnalysisConfidence.Predicted, analysis.UninstallRegistryKey.Confidence);
    }

    [Fact]
    public void UninstallExpression_ExpandsTheExecutableAndQuotesEachArgument()
    {
        var expression = WrapperGeneratorService.UninstallExpressionFromCommandLine(
            @"""%LOCALAPPDATA%\Programs\Test App\Uninstall Test App.exe"" /S /allusers");

        Assert.Contains("$uninstaller = [Environment]::ExpandEnvironmentVariables('%LOCALAPPDATA%\\Programs\\Test App\\Uninstall Test App.exe')", expression);
        Assert.Contains("if (-not (Test-Path -LiteralPath $uninstaller)) { exit 0 }", expression);
        Assert.Contains("-ArgumentList @('/S', '/allusers') -Wait -PassThru -NoNewWindow", expression);

        Assert.Equal("", WrapperGeneratorService.UninstallExpressionFromCommandLine(@"""uninstall.exe"" /S"));
        Assert.Equal("", WrapperGeneratorService.UninstallExpressionFromCommandLine(""));
        Assert.Contains("-FilePath $uninstaller -Wait", WrapperGeneratorService.UninstallExpressionFromCommandLine(@"C:\App\unins000.exe"));
    }
}
