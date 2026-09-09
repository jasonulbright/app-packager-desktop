using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The stage-time install mode swap for definitions that declare both modes:
/// the staged installer's compiled script supplies the branch, and switch,
/// uninstaller, detection and context move together.
/// </summary>
public class InstallModeTransformTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static AppDefinition Def(string framework, string installArgs, DetectionConfig detection) => new()
    {
        Application = "Test App (x64)",
        CMName = "Test App",
        InstallerType = "EXE",
        InstallerFramework = framework,
        InstallArgs = installArgs,
        SupportsInstallModes = ["CurrentUser", "AllUsers"],
        Detection = detection
    };

    private static StageDetection RegistryDetection(string key, bool is64, string hive = "LocalMachine") => new()
    {
        Type = "RegistryKeyValue",
        RegistryKeyRelative = key,
        ValueName = "DisplayVersion",
        ExpectedValue = "1.2.3",
        Operator = "GreaterEquals",
        PropertyType = "Version",
        Is64Bit = is64,
        Hive = hive
    };

    [Fact]
    public void NsisUserDefault_AllUsersMovesSwitchKeyHiveAndContext()
    {
        var def = Def("NSIS", "/S", new DetectionConfig { Type = "RegistryKeyValue", RegistryHive = "HKCU" });
        var detection = RegistryDetection(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", true, "CurrentUser");

        var result = StagingService.ApplyInstallMode(def, Fixture("nsis-u-multimode.bin"), "AllUsers", "/S", detection);

        Assert.Equal("/S /allusers", result.InstallArgs);
        Assert.Equal("Machine", result.InstallContext);
        Assert.Equal(@"""%ProgramW6432%\TestApp\Uninstall Test App.exe"" /S /allusers", result.UninstallCommandLine);
        Assert.Contains("[Environment]::ExpandEnvironmentVariables('%ProgramW6432%\\TestApp\\Uninstall Test App.exe')", result.UninstallExpression);
        Assert.Equal("LocalMachine", result.Detection.Hive);
        Assert.True(result.Detection.Is64Bit);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", result.Detection.RegistryKeyRelative);
        Assert.Equal("1.2.3", result.Detection.ExpectedValue);
        // The original detection object is untouched.
        Assert.Equal("CurrentUser", detection.Hive);
    }

    [Fact]
    public void ExistingModeSwitch_IsReplacedAndOtherSwitchesStay()
    {
        var def = Def("NSIS", "/S /allusers /NOSHORTCUTS", new DetectionConfig { Type = "RegistryKeyValue" });
        var detection = RegistryDetection(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", true);

        var result = StagingService.ApplyInstallMode(def, Fixture("nsis-u-multimode.bin"), "CurrentUser", "/S /allusers /NOSHORTCUTS", detection);

        // The default branch carries no extra switch: only the foreign one is dropped.
        Assert.Equal("/S /NOSHORTCUTS", result.InstallArgs);
        Assert.Equal("User", result.InstallContext);
        Assert.Equal("CurrentUser", result.Detection.Hive);
    }

    [Fact]
    public void InnoAdminDefault_CurrentUserTakesTheUserProgramsFolder()
    {
        var def = Def("InnoSetup", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-", new DetectionConfig { Type = "RegistryKeyValue" });
        var detection = RegistryDetection(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Tree64_is1", true);

        var result = StagingService.ApplyInstallMode(def, Fixture("inno-admin-modes-x64.bin"), "CurrentUser",
            "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-", detection);

        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CURRENTUSER", result.InstallArgs);
        Assert.Equal("User", result.InstallContext);
        Assert.Equal(@"""%LOCALAPPDATA%\Programs\Tree\unins000.exe"" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART", result.UninstallCommandLine);
        Assert.Equal("CurrentUser", result.Detection.Hive);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Tree64_is1", result.Detection.RegistryKeyRelative);
    }

    [Fact]
    public void DefinitionExpression_KeepsItsTrailingWaitOnTheBranch()
    {
        var def = Def("InnoSetup", "/VERYSILENT /SP-", new DetectionConfig { Type = "RegistryKeyValue" });
        var detection = RegistryDetection(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Tree64_is1", true);
        const string expression = "$proc = Start-Process -FilePath \"C:\\Program Files\\Tree\\unins000.exe\" -ArgumentList \"/VERYSILENT\" -Wait -PassThru -NoNewWindow; while (Get-Process -Name 'Au_' -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 1 }";

        var result = StagingService.ApplyInstallMode(def, Fixture("inno-admin-modes-x64.bin"), "CurrentUser", "/VERYSILENT /SP-", detection, null, expression);

        Assert.Equal(
            "$uninstaller = [Environment]::ExpandEnvironmentVariables('%LOCALAPPDATA%\\Programs\\Tree\\unins000.exe')\r\n" +
            "if (-not (Test-Path -LiteralPath $uninstaller)) { exit 0 }\r\n" +
            "$proc = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru -NoNewWindow" +
            "; while (Get-Process -Name 'Au_' -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 1 }",
            result.UninstallExpression);
    }

    [Fact]
    public void DefinitionExpression_KeepsItsLeadingStatementsAndEnvForm()
    {
        var def = Def("NSIS", "/S", new DetectionConfig { Type = "RegistryKeyValue", RegistryHive = "HKCU" });
        var detection = RegistryDetection(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", true, "CurrentUser");
        const string expression = "Get-Process TestApp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep 2; " +
            "$proc = Start-Process -FilePath \"$env:LOCALAPPDATA\\Programs\\TestApp\\Uninstall Test App.exe\" -ArgumentList \"/S\" -Wait -PassThru -NoNewWindow";

        var result = StagingService.ApplyInstallMode(def, Fixture("nsis-u-multimode.bin"), "AllUsers", "/S", detection, null, expression);

        Assert.StartsWith("Get-Process TestApp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep 2;\r\n$uninstaller = ", result.UninstallExpression);
        Assert.Contains("ExpandEnvironmentVariables('%ProgramW6432%\\TestApp\\Uninstall Test App.exe')", result.UninstallExpression);
        Assert.EndsWith("-ArgumentList @('/S', '/allusers') -Wait -PassThru -NoNewWindow", result.UninstallExpression);
    }

    [Fact]
    public void DefinitionExpression_WithItsOwnLookup_YieldsToTheBranchCommand()
    {
        var def = Def("NSIS", "/S", new DetectionConfig { Type = "RegistryKeyValue", RegistryHive = "HKCU" });
        var detection = RegistryDetection(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", true, "CurrentUser");
        const string expression = "$cmd = (Get-ItemProperty -LiteralPath 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\TestApp').UninstallString; $proc = Start-Process -FilePath $cmd -ArgumentList '/S' -Wait -PassThru -NoNewWindow";
        var notes = new List<string>();

        var result = StagingService.ApplyInstallMode(def, Fixture("nsis-u-multimode.bin"), "AllUsers", "/S", detection, notes.Add, expression);

        Assert.StartsWith("$uninstaller = [Environment]::ExpandEnvironmentVariables('%ProgramW6432%\\TestApp\\Uninstall Test App.exe')", result.UninstallExpression);
        Assert.DoesNotContain("Get-ItemProperty", result.UninstallExpression);
        Assert.Contains(notes, n => n.Contains("does not name the default uninstaller"));
    }

    [Fact]
    public void FileDetection_MovesBetweenTheBranchFolders()
    {
        var def = Def("InnoSetup", "/VERYSILENT /SP-", new DetectionConfig { Type = "File" });
        var detection = new StageDetection
        {
            Type = "File",
            FilePath = @"C:\Program Files\Tree\bin",
            FileName = "tree.exe",
            PropertyType = "Version",
            Operator = "GreaterEquals",
            ExpectedValue = "1.2.3"
        };

        var result = StagingService.ApplyInstallMode(def, Fixture("inno-admin-modes-x64.bin"), "AllUsers", "/VERYSILENT /SP-", detection);

        Assert.Equal(@"%ProgramFiles%\Tree\bin", result.Detection.FilePath);
        Assert.Equal("tree.exe", result.Detection.FileName);
        Assert.Equal(@"C:\Program Files\Tree\bin", detection.FilePath);
    }

    [Fact]
    public void FileDetectionOutsideTheDefaultFolder_IsLeftAlone()
    {
        var def = Def("NSIS", "/S", new DetectionConfig { Type = "File" });
        var detection = new StageDetection { Type = "File", FilePath = @"C:\Tools\Tree", FileName = "tree.exe" };
        var notes = new List<string>();

        var result = StagingService.ApplyInstallMode(def, Fixture("nsis-u-multimode.bin"), "AllUsers", "/S", detection, notes.Add);

        Assert.Equal(@"C:\Tools\Tree", result.Detection.FilePath);
        Assert.Contains(notes, n => n.Contains("left unchanged"));
    }

    [Fact]
    public void PerUserBranch_FileDetectionBecomesHkcuVersionDetection()
    {
        var def = Def("InnoSetup", "/VERYSILENT /SP-", new DetectionConfig { Type = "File" });
        var detection = new StageDetection
        {
            Type = "File",
            FilePath = @"C:\Program Files\Tree\bin",
            FileName = "tree.exe",
            PropertyType = "Version",
            Operator = "GreaterEquals",
            ExpectedValue = "1.2.3",
            Is64Bit = true
        };

        var result = StagingService.ApplyInstallMode(
            def, Fixture("inno-admin-modes-x64.bin"), "CurrentUser", "/VERYSILENT /SP-", detection, null, null, "1.2.3");

        Assert.Equal("RegistryKeyValue", result.Detection.Type);
        Assert.Equal("CurrentUser", result.Detection.Hive);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Tree64_is1", result.Detection.RegistryKeyRelative);
        Assert.Equal("DisplayVersion", result.Detection.ValueName);
        Assert.Equal("GreaterEquals", result.Detection.Operator);
        Assert.Equal("1.2.3", result.Detection.ExpectedValue);
        Assert.False(result.Detection.Is64Bit);
        Assert.Equal("File", detection.Type);
    }

    [Fact]
    public void PerUserBranch_WithoutAVersion_RefusesToInventUserDetection()
    {
        var def = Def("InnoSetup", "/VERYSILENT /SP-", new DetectionConfig { Type = "File" });
        var detection = new StageDetection { Type = "File", FilePath = @"C:\Program Files\Tree\bin", FileName = "tree.exe" };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            StagingService.ApplyInstallMode(def, Fixture("inno-admin-modes-x64.bin"), "CurrentUser", "/VERYSILENT /SP-", detection));
        Assert.Contains("Cannot create user detection", ex.Message);
    }

    [Fact]
    public void ModeTheScriptDoesNotOffer_IsRefused()
    {
        var def = Def("NSIS", "/S", new DetectionConfig { Type = "RegistryKeyValue" });
        var detection = RegistryDetection(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", true);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            StagingService.ApplyInstallMode(def, Fixture("nsis-u-single.bin"), "AllUsers", "/S", detection));
        Assert.Contains("offers no AllUsers", ex.Message);
    }

    [Theory]
    [InlineData("rstudio.json")]
    [InlineData("gimp.json")]
    [InlineData("stellarium.json")]
    [InlineData("thonny.json")]
    [InlineData("bleachbit.json")]
    [InlineData("joplin.json")]
    [InlineData("simplenote.json")]
    [InlineData("freecad.json")]
    [InlineData("kdiff3.json")]
    [InlineData("bitwarden.json")]
    [InlineData("keystoreexplorer.json")]
    [InlineData("typora.json")]
    public void TaggedDefinitions_DeclareBothModesAndForceNoFolder(string definition)
    {
        var def = DefinitionFixture.Load(definition);

        Assert.True(def.OffersInstallModes);
        // A /D= or /DIR= override pins the folder, which a mode swap cannot honor.
        Assert.DoesNotContain("/D=", def.InstallArgs);
        Assert.DoesNotContain("/DIR=", def.InstallArgs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefinitionWithoutTheDeclaration_IsRefused()
    {
        var def = Def("NSIS", "/S", new DetectionConfig { Type = "RegistryKeyValue" });
        def.SupportsInstallModes = null;

        Assert.Throws<InvalidOperationException>(() =>
            StagingService.ApplyInstallMode(def, Fixture("nsis-u-multimode.bin"), "AllUsers", "/S",
                RegistryDetection(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", true)));
        Assert.False(def.OffersInstallModes);
        def.SupportsInstallModes = ["AllUsers"];
        Assert.False(def.OffersInstallModes);
        def.SupportsInstallModes = ["CurrentUser", "AllUsers"];
        Assert.True(def.OffersInstallModes);
    }
}
