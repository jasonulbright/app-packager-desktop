using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public sealed class BootstrapperLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "layout-wrapper-tests-" + Guid.NewGuid().ToString("N"));

    public BootstrapperLayoutTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static BootstrapperLayoutConfig Adk() => new()
    {
        BootstrapperUrl = "https://go.microsoft.com/fwlink/?linkid=2289980",
        BootstrapperFileName = "adksetup.exe",
        VersionSourceMsi = "Installers\\Windows Deployment Tools-x86*.msi",
        InstallPath = @"C:\Program Files (x86)\Windows Kits\10",
        Features = ["OptionId.DeploymentTools", "OptionId.UserStateMigrationTool"]
    };

    private (string Install, string Uninstall) Generate(BootstrapperLayoutConfig layout)
    {
        new WrapperGeneratorService().GenerateBootstrapperLayoutWrappers(_dir, layout);
        return (File.ReadAllText(Path.Combine(_dir, "install.ps1")),
                File.ReadAllText(Path.Combine(_dir, "uninstall.ps1")));
    }

    // ── Wrapper text ────────────────────────────────────────────

    [Fact]
    public void Install_RunsTheLayoutCopyWithQuietInstallPathAndFeatures()
    {
        var (install, _) = Generate(Adk());

        Assert.Contains("$setupPath = Join-Path $PSScriptRoot 'adksetup.exe'", install);
        Assert.Contains(
            "$proc = Start-Process -FilePath $setupPath -ArgumentList @('/quiet', '/installpath', " +
            "'C:\\Program Files (x86)\\Windows Kits\\10', '/features', 'OptionId.DeploymentTools', " +
            "'OptionId.UserStateMigrationTool') -Wait -PassThru -NoNewWindow",
            install);
        Assert.Contains("Exit-Wrapper $proc.ExitCode", install);
    }

    [Fact]
    public void Install_MissingBootstrapper_ExitsOne()
    {
        var (install, _) = Generate(Adk());

        Assert.Contains("if (-not (Test-Path -LiteralPath $setupPath)) { Write-Error \"Bootstrapper not found: $setupPath\"; Exit-Wrapper 1 }", install);
    }

    [Fact]
    public void Uninstall_RunsQuietUninstallAndTreatsMissingContentAsSuccess()
    {
        var (_, uninstall) = Generate(Adk());

        Assert.Contains("if (-not (Test-Path -LiteralPath $setupPath)) { Exit-Wrapper 0 }", uninstall);
        Assert.Contains(
            "$proc = Start-Process -FilePath $setupPath -ArgumentList @('/quiet', '/uninstall') -Wait -PassThru -NoNewWindow",
            uninstall);
        Assert.DoesNotContain("/installpath", uninstall);
        Assert.DoesNotContain("/features", uninstall);
    }

    [Fact]
    public void SingleFeature_EmitsOneFeatureArgument()
    {
        var layout = Adk();
        layout.BootstrapperFileName = "adkwinpesetup.exe";
        layout.Features = ["OptionId.WindowsPreinstallationEnvironment"];

        var (install, _) = Generate(layout);

        Assert.Contains("'/features', 'OptionId.WindowsPreinstallationEnvironment') -Wait", install);
    }

    [Fact]
    public void NoFeaturesOrInstallPath_OmitsBothSwitches()
    {
        var layout = Adk();
        layout.InstallPath = "";
        layout.Features = [];

        var (install, _) = Generate(layout);

        Assert.Contains("-ArgumentList @('/quiet') -Wait", install);
    }

    [Fact]
    public void ApostropheInInstallPath_IsEscapedInTheLiteral()
    {
        var layout = Adk();
        layout.InstallPath = @"C:\jan's kits";

        var (install, _) = Generate(layout);

        Assert.Contains("'C:\\jan''s kits'", install);
    }

    [Fact]
    public void AllFourWrapperFilesAreWritten()
    {
        Generate(Adk());

        foreach (var name in new[] { "install.ps1", "uninstall.ps1", "install.bat", "uninstall.bat" })
            Assert.True(File.Exists(Path.Combine(_dir, name)), name);
    }

    [Fact]
    public void ManifestInstallArgs_MatchWhatTheWrapperRuns()
    {
        Assert.Equal(
            "/quiet /installpath \"C:\\Program Files (x86)\\Windows Kits\\10\" /features OptionId.DeploymentTools OptionId.UserStateMigrationTool",
            StagingService.BuildLayoutInstallArgs(Adk()));
    }

    // ── Path-length guard ───────────────────────────────────────

    [Fact]
    public void LayoutPathWithinLimit_IsAccepted()
    {
        StagingService.AssertLayoutPathLength(@"C:\temp\packr-layout\10.1.26100.4188", 120);
    }

    [Fact]
    public void LayoutPathOverLimit_IsRefusedWithTheLengthAndLimitNamed()
    {
        var longPath = @"C:\temp\" + new string('a', 200);

        var ex = Assert.Throws<InvalidOperationException>(
            () => StagingService.AssertLayoutPathLength(longPath, 120));

        Assert.Contains("208 chars", ex.Message);
        Assert.Contains("limit 120", ex.Message);
    }

    [Fact]
    public void LayoutPathExactlyAtLimit_IsAccepted()
    {
        var path = @"C:\" + new string('a', 117);
        Assert.Equal(120, path.Length);

        StagingService.AssertLayoutPathLength(path, 120);
    }

    // ── Config validation ───────────────────────────────────────

    [Fact]
    public void ValidConfig_Passes()
    {
        StagingService.ValidateBootstrapperLayout(Adk());
    }

    [Fact]
    public void MissingBootstrapperFileName_IsRefused()
    {
        var layout = Adk();
        layout.BootstrapperFileName = "";

        Assert.Contains("BootstrapperFileName",
            Assert.Throws<InvalidOperationException>(() => StagingService.ValidateBootstrapperLayout(layout)).Message);
    }

    [Fact]
    public void MissingVersionSourceMsi_IsRefused()
    {
        var layout = Adk();
        layout.VersionSourceMsi = "";

        Assert.Contains("VersionSourceMsi",
            Assert.Throws<InvalidOperationException>(() => StagingService.ValidateBootstrapperLayout(layout)).Message);
    }

    [Fact]
    public void NonPositiveMaxLayoutPathLength_IsRefused()
    {
        var layout = Adk();
        layout.MaxLayoutPathLength = 0;

        Assert.Contains("MaxLayoutPathLength",
            Assert.Throws<InvalidOperationException>(() => StagingService.ValidateBootstrapperLayout(layout)).Message);
    }

    // ── Version-source MSI resolution ───────────────────────────

    private string MakeLayout(params string[] installerNames)
    {
        var installers = Path.Combine(_dir, "Installers");
        Directory.CreateDirectory(installers);
        foreach (var name in installerNames)
            File.WriteAllText(Path.Combine(installers, name), "");
        return _dir;
    }

    [Fact]
    public void ExactRelativePath_ResolvesTheAnchor()
    {
        var layout = MakeLayout("Windows Deployment Tools-x86_en-us.msi");

        var resolved = StagingService.ResolveVersionSourceMsi(
            layout, "Installers\\Windows Deployment Tools-x86_en-us.msi");

        Assert.Equal("Windows Deployment Tools-x86_en-us.msi", Path.GetFileName(resolved));
    }

    [Fact]
    public void ForwardSlashesInTheRelativePath_AreAccepted()
    {
        var layout = MakeLayout("Windows Deployment Tools-x86_en-us.msi");

        var resolved = StagingService.ResolveVersionSourceMsi(
            layout, "Installers/Windows Deployment Tools-x86_en-us.msi");

        Assert.True(File.Exists(resolved));
    }

    [Fact]
    public void Pattern_ResolvesASingleMatch()
    {
        var layout = MakeLayout(
            "Windows Deployment Tools-x86_en-us.msi",
            "Windows PE wims (DesktopEditions)-x86_en-us.msi");

        var resolved = StagingService.ResolveVersionSourceMsi(
            layout, "Installers\\Windows Deployment Tools-x86*.msi");

        Assert.Equal("Windows Deployment Tools-x86_en-us.msi", Path.GetFileName(resolved));
    }

    [Fact]
    public void PatternMatchingSeveralFiles_IsRefused()
    {
        var layout = MakeLayout(
            "Windows Deployment Tools-x86_en-us.msi",
            "Windows Deployment Tools-x86_fr-fr.msi");

        var ex = Assert.Throws<InvalidOperationException>(
            () => StagingService.ResolveVersionSourceMsi(layout, "Installers\\Windows Deployment Tools-x86*.msi"));

        Assert.Contains("matched 2 files", ex.Message);
    }

    [Fact]
    public void PatternMatchingNothing_IsRefused()
    {
        var layout = MakeLayout("Windows Deployment Tools-x86_en-us.msi");

        Assert.Contains("matched no file",
            Assert.Throws<InvalidOperationException>(
                () => StagingService.ResolveVersionSourceMsi(layout, "Installers\\Nothing-x86*.msi")).Message);
    }

    [Fact]
    public void MissingExactAnchor_IsRefused()
    {
        var layout = MakeLayout("Windows Deployment Tools-x86_en-us.msi");

        Assert.Contains("Detection anchor missing",
            Assert.Throws<InvalidOperationException>(
                () => StagingService.ResolveVersionSourceMsi(layout, "Installers\\Absent.msi")).Message);
    }

    // ── Shipped definitions ─────────────────────────────────────

    [Theory]
    [InlineData("windowsadk.json", "adksetup.exe", "OptionId.DeploymentTools")]
    [InlineData("windowspeaddon.json", "adkwinpesetup.exe", "OptionId.WindowsPreinstallationEnvironment")]
    public void ShippedDefinition_BindsAValidLayoutConfig(string fileName, string bootstrapper, string feature)
    {
        var def = DefinitionFixture.Load(fileName);

        var layout = Assert.IsType<BootstrapperLayoutConfig>(def.BootstrapperLayout);
        StagingService.ValidateBootstrapperLayout(layout);

        Assert.Equal(bootstrapper, layout.BootstrapperFileName);
        Assert.Contains(feature, layout.Features);
        Assert.Equal("/layout", layout.LayoutSwitch);
        Assert.Equal(120, layout.MaxLayoutPathLength);
        Assert.StartsWith("Installers\\", layout.VersionSourceMsi);
        Assert.Equal(@"C:\Program Files (x86)\Windows Kits\10", layout.InstallPath);
    }

    [Theory]
    [InlineData("windowsadk.json")]
    [InlineData("windowspeaddon.json")]
    public void ShippedDefinition_DeclaresTheBootstrapperUrlItDownloads(string fileName)
    {
        var layout = DefinitionFixture.Load(fileName).BootstrapperLayout!;

        Assert.StartsWith("https://", layout.BootstrapperUrl);
    }

    [Fact]
    public void AdkDefinition_ManifestArgsMatchTheReferencePackager()
    {
        var layout = DefinitionFixture.Load("windowsadk.json").BootstrapperLayout!;

        Assert.Equal(
            "/quiet /installpath \"C:\\Program Files (x86)\\Windows Kits\\10\" /features OptionId.DeploymentTools OptionId.UserStateMigrationTool",
            StagingService.BuildLayoutInstallArgs(layout));
    }
}
