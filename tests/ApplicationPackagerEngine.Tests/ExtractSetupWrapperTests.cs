using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public sealed class ExtractSetupWrapperTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "extract-setup-tests-" + Guid.NewGuid().ToString("N"));

    public ExtractSetupWrapperTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private const string SqlInstallArgs =
        "/Q /ACTION=Install /IACCEPTSQLSERVERLICENSETERMS /SUPPRESSPRIVACYSTATEMENTNOTICE /FEATURES=SQLENGINE " +
        "/INSTANCENAME=SQLEXPRESS /SQLSYSADMINACCOUNTS=\"BUILTIN\\Administrators\" /SQLSVCSTARTUPTYPE=Automatic " +
        "/BROWSERSVCSTARTUPTYPE=Disabled /UPDATEENABLED=False";

    private const string SqlUninstallArgs =
        "/Q /ACTION=Uninstall /FEATURES=SQLENGINE /INSTANCENAME=SQLEXPRESS";

    private static ExtractSetupConfig Sql() => new()
    {
        ExtractArgs = "/Q /X:{extractdir}",
        ExtractDirName = "SQLEXPR_Media",
        InnerSetupPath = "setup.exe"
    };

    private (string Install, string Uninstall) Generate(
        ExtractSetupConfig? config = null,
        string payload = "SQLEXPR_x64_ENU.exe",
        string installArgs = SqlInstallArgs,
        string uninstallArgs = SqlUninstallArgs)
    {
        new WrapperGeneratorService().GenerateExtractSetupWrappers(
            _dir, payload, config ?? Sql(), installArgs, uninstallArgs);
        return (File.ReadAllText(Path.Combine(_dir, "install.ps1")),
                File.ReadAllText(Path.Combine(_dir, "uninstall.ps1")));
    }

    [Fact]
    public void BothWrappers_ExtractToASpacelessDriveRootPath()
    {
        var (install, uninstall) = Generate();

        const string root = "$extractRoot = Join-Path $env:SystemDrive 'SQLEXPR_Media'";
        Assert.Contains(root, install);
        Assert.Contains(root, uninstall);
    }

    [Fact]
    public void ExtractArgs_PassTheRuntimeExtractionRootAsAnExpression()
    {
        var (install, _) = Generate();

        Assert.Contains(
            "$box = Start-Process -FilePath $mediaPath -ArgumentList @('/Q', (\"/X:\" + $extractRoot)) -Wait -PassThru -NoNewWindow",
            install);
        Assert.DoesNotContain("{extractdir}", install);
    }

    [Fact]
    public void StaleExtractionTree_IsRemovedBeforeExtracting()
    {
        var (install, _) = Generate();

        Assert.Contains(
            "if (Test-Path -LiteralPath $extractRoot) { Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue }",
            install);
    }

    [Fact]
    public void FailedExtraction_SurfacesTheBoxExitCode()
    {
        var (install, _) = Generate();

        Assert.Contains("if ($box.ExitCode -ne 0) { Exit-Wrapper $box.ExitCode }", install);
    }

    [Fact]
    public void MissingPayloadAndMissingInnerSetup_HaveDistinctExitCodes()
    {
        var (install, _) = Generate();

        Assert.Contains("Write-Error \"Payload not found: $mediaPath\"; Exit-Wrapper 1", install);
        Assert.Contains("Write-Error \"Inner setup not found: $setup\"; Exit-Wrapper 2", install);
    }

    [Fact]
    public void InnerSetup_IsResolvedRelativeToTheExtractionRoot()
    {
        var (install, _) = Generate();

        Assert.Contains("$setup = Join-Path $extractRoot 'setup.exe'", install);
    }

    [Fact]
    public void NestedInnerSetupPath_IsPreserved()
    {
        var config = Sql();
        config.InnerSetupPath = "media\\setup.exe";

        var (install, _) = Generate(config);

        Assert.Contains("$setup = Join-Path $extractRoot 'media\\setup.exe'", install);
    }

    [Fact]
    public void Install_RunsInnerSetupWithTheDefinitionsInstallArgs()
    {
        var (install, _) = Generate();

        Assert.Contains(
            "$proc = Start-Process -FilePath $setup -ArgumentList @('/Q', '/ACTION=Install', " +
            "'/IACCEPTSQLSERVERLICENSETERMS', '/SUPPRESSPRIVACYSTATEMENTNOTICE', '/FEATURES=SQLENGINE', " +
            "'/INSTANCENAME=SQLEXPRESS', '/SQLSYSADMINACCOUNTS=\"BUILTIN\\Administrators\"', " +
            "'/SQLSVCSTARTUPTYPE=Automatic', '/BROWSERSVCSTARTUPTYPE=Disabled', '/UPDATEENABLED=False') " +
            "-Wait -PassThru -NoNewWindow",
            install);
    }

    [Fact]
    public void Uninstall_RunsTheSameExtractFlowWithTheUninstallAction()
    {
        var (_, uninstall) = Generate();

        Assert.Contains("$box = Start-Process -FilePath $mediaPath", uninstall);
        Assert.Contains(
            "$proc = Start-Process -FilePath $setup -ArgumentList @('/Q', '/ACTION=Uninstall', " +
            "'/FEATURES=SQLENGINE', '/INSTANCENAME=SQLEXPRESS') -Wait -PassThru -NoNewWindow",
            uninstall);
    }

    [Fact]
    public void BothWrappers_RemoveTheExtractionTreeAndReportSetupsExitCode()
    {
        var (install, uninstall) = Generate();

        var cleanup = string.Join("\r\n",
            "$code = if ($proc) { $proc.ExitCode } else { 1 }",
            "Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue",
            "Exit-Wrapper $code");

        Assert.Contains(cleanup, install);
        Assert.Contains(cleanup, uninstall);
    }

    [Fact]
    public void QuotedArgumentValue_StaysOneArgumentWithItsQuotesIntact()
    {
        var (install, _) = Generate(installArgs: "/SQLSYSADMINACCOUNTS=\"CONTOSO\\SQL Admins\"");

        Assert.Contains("@('/SQLSYSADMINACCOUNTS=\"CONTOSO\\SQL Admins\"')", install);
    }

    [Fact]
    public void ApostropheInPayloadName_IsEscapedInTheLiteral()
    {
        var (install, _) = Generate(payload: "jan's-media.exe");

        Assert.Contains("$mediaPath = Join-Path $PSScriptRoot 'jan''s-media.exe'", install);
    }

    [Fact]
    public void AllFourWrapperFilesAreWritten()
    {
        Generate();

        foreach (var name in new[] { "install.ps1", "uninstall.ps1", "install.bat", "uninstall.bat" })
            Assert.True(File.Exists(Path.Combine(_dir, name)), name);
    }

    [Fact]
    public void ValidConfig_Passes()
    {
        WrapperGeneratorService.ValidateExtractSetup(Sql());
    }

    [Fact]
    public void ExtractDirNameWithASpace_IsRefused()
    {
        var config = Sql();
        config.ExtractDirName = "SQLEXPR Media";

        Assert.Contains("must not contain whitespace",
            Assert.Throws<InvalidOperationException>(() => WrapperGeneratorService.ValidateExtractSetup(config)).Message);
    }

    [Fact]
    public void ExtractDirNameThatIsAPath_IsRefused()
    {
        var config = Sql();
        config.ExtractDirName = @"C:\SQLEXPR_Media";

        Assert.Contains("single folder name",
            Assert.Throws<InvalidOperationException>(() => WrapperGeneratorService.ValidateExtractSetup(config)).Message);
    }

    [Fact]
    public void EmptyExtractDirName_IsRefused()
    {
        var config = Sql();
        config.ExtractDirName = "";

        Assert.Contains("ExtractDirName is required",
            Assert.Throws<InvalidOperationException>(() => WrapperGeneratorService.ValidateExtractSetup(config)).Message);
    }

    [Fact]
    public void ExtractArgsWithoutThePlaceholder_IsRefused()
    {
        var config = Sql();
        config.ExtractArgs = "/Q /X:C:\\Fixed";

        Assert.Contains("{extractdir}",
            Assert.Throws<InvalidOperationException>(() => WrapperGeneratorService.ValidateExtractSetup(config)).Message);
    }

    [Fact]
    public void EmptyInnerSetupPath_IsRefused()
    {
        var config = Sql();
        config.InnerSetupPath = "";

        Assert.Contains("InnerSetupPath is required",
            Assert.Throws<InvalidOperationException>(() => WrapperGeneratorService.ValidateExtractSetup(config)).Message);
    }

    [Fact]
    public void Tokenizer_SplitsOnWhitespaceOutsideQuotes()
    {
        Assert.Equal(["/Q", "/ACTION=Install"], WrapperGeneratorService.Tokenize("/Q  /ACTION=Install"));
    }

    [Fact]
    public void Tokenizer_KeepsAQuotedValueWithItsSpacesAsOneToken()
    {
        Assert.Equal(
            ["/A=\"one two\"", "/B"],
            WrapperGeneratorService.Tokenize("/A=\"one two\" /B"));
    }

    [Fact]
    public void Tokenizer_OnEmptyInput_ReturnsNoTokens()
    {
        Assert.Empty(WrapperGeneratorService.Tokenize("   "));
    }

    [Fact]
    public void SqlDefinition_BindsAValidExtractSetupConfig()
    {
        var def = DefinitionFixture.Load("sqlserver2022express.json");

        var config = Assert.IsType<ExtractSetupConfig>(def.ExtractSetup);
        WrapperGeneratorService.ValidateExtractSetup(config);

        Assert.Equal("SQLEXPR_Media", config.ExtractDirName);
        Assert.Equal("setup.exe", config.InnerSetupPath);
        Assert.Equal("/Q /X:{extractdir}", config.ExtractArgs);
    }

    [Fact]
    public void SqlDefinition_RestoresTheInstanceAndSysadminChoicesWithTheReferenceDefaults()
    {
        var settings = DefinitionFixture.Load("sqlserver2022express.json").InstallerSettings;

        Assert.NotNull(settings);
        var instance = Assert.Single(settings, p => p.Key == "InstanceName");
        Assert.Equal("SQLEXPRESS", instance.DefaultValue);
        Assert.Equal("/INSTANCENAME={value}", instance.CommandFragment);

        var sysadmins = Assert.Single(settings, p => p.Key == "SysAdminAccounts");
        Assert.Equal(@"BUILTIN\Administrators", sysadmins.DefaultValue);
        Assert.Equal("/SQLSYSADMINACCOUNTS=\"{value}\"", sysadmins.CommandFragment);
    }

    [Fact]
    public void SqlDefinition_DefaultSettingValuesResolveIntoTheInstallArgs()
    {
        var def = DefinitionFixture.Load("sqlserver2022express.json");

        // InstallArgs carries only the switches no parameter owns; the resolver
        // supplies the rest, so the switch must not be duplicated in the base.
        Assert.DoesNotContain("/INSTANCENAME=", def.InstallArgs);
        Assert.DoesNotContain("/SQLSYSADMINACCOUNTS=", def.InstallArgs);

        var resolved = StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, null);
        Assert.Contains("/INSTANCENAME=SQLEXPRESS", resolved);
        Assert.Contains("/SQLSYSADMINACCOUNTS=\"BUILTIN\\Administrators\"", resolved);

        var uninstall = StagingService.ApplySettingTokens(def.UninstallArgs, def.InstallerSettings, null);
        Assert.Contains("/INSTANCENAME=SQLEXPRESS", uninstall);
    }

    [Fact]
    public void SqlDefinition_WrappersGenerateFromItsOwnArgs()
    {
        var def = DefinitionFixture.Load("sqlserver2022express.json");

        var (install, uninstall) = Generate(
            def.ExtractSetup!, def.InstallerFileName!, def.InstallArgs, def.UninstallArgs);

        Assert.Contains("$mediaPath = Join-Path $PSScriptRoot 'SQLEXPR_x64_ENU.exe'", install);
        Assert.Contains("'/ACTION=Install'", install);
        Assert.Contains("'/ACTION=Uninstall'", uninstall);
        Assert.Contains("Join-Path $env:SystemDrive 'SQLEXPR_Media'", uninstall);
    }
}
