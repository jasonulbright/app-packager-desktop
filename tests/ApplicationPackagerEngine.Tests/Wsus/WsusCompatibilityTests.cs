using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Wsus;
using static ApplicationPackagerEngine.Tests.Wsus.WsusTestSupport;

namespace ApplicationPackagerEngine.Tests.Wsus;

public class WsusCompatibilityTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly string _content;

    public WsusCompatibilityTests() => _content = NewContentFolder(_temp);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void CarriesEveryRecordedStageFileWithTheInstallerFirstNeverAGeneratedFile()
    {
        string[] recorded = ["app-icon.ico", "Data1.cab", "install.bat", "install.ps1", @"scripts\detect.ps1", "setup.ini", "setup.exe", "uninstall.ps1", "tool.png", "Patches/fix.msp"];
        var manifest = Manifest(m =>
        {
            m.Icon = "tool.png";
            m.FileHashes = recorded.Select(r => new StageFileHash { RelativePath = r, Sha256 = new string('0', 64), Size = 1 }).ToArray();
        });
        Assert.Equal(["setup.exe", "Data1.cab", "setup.ini", @"Patches\fix.msp"], WsusPayload.Files(manifest));
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData(@"Data\x.cab")]
    public void ResolvesAPathInsideTheContentFolder(string path)
    {
        Assert.Equal(Path.GetFullPath(Path.Combine(_content, path)), WsusPayload.ResolvePath(_content, path));
    }

    [Theory]
    [InlineData(@"..\outside.txt")]
    [InlineData(@"Data\..\..\outside.txt")]
    [InlineData("..")]
    [InlineData("C:outside.txt")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\Windows\win.ini")]
    [InlineData(@"\\server\share\setup.exe")]
    [InlineData("setup.exe:hidden")]
    [InlineData("")]
    public void RefusesAPathOutsideTheContentFolder(string path)
    {
        Assert.Contains("content folder", Assert.Throws<WsusPayloadException>(() => WsusPayload.ResolvePath(_content, path)).Message);
    }

    [Fact]
    public void BlocksAManifestThatRecordsAFileOutsideTheContentFolder()
    {
        var outside = Path.Combine(_temp.Path, "outside.txt");
        File.WriteAllText(outside, "marker");
        var manifest = Manifest(m => m.FileHashes =
        [
            new StageFileHash { RelativePath = "setup.exe", Sha256 = Sha256(Path.Combine(_content, "setup.exe")), Size = 9 },
            new StageFileHash { RelativePath = @"..\outside.txt", Sha256 = Sha256(outside), Size = 6 }
        ]);
        var finding = Assert.Single(WsusCompatibility.Evaluate(manifest, _content), f => f.Code == WsusRefusalCodes.PayloadOutsideStage);
        Assert.Equal(WsusSeverity.Blocking, finding.Severity);
        Assert.Contains(@"..\outside.txt", finding.Message);
        Assert.Contains("outside the content folder", Assert.Throws<WsusPayloadException>(() =>
            WsusPayload.AssertIntegrity(manifest, _content, ["setup.exe", @"..\outside.txt"])).Message);
    }

    [Fact]
    public void ReportsAnInstallerOutsideTheContentFolderOnce()
    {
        var findings = WsusCompatibility.Evaluate(Manifest(m => m.InstallerFile = @"..\setup.exe"), _content);
        Assert.Single(findings, f => f.Code == WsusRefusalCodes.PayloadOutsideStage);
        Assert.DoesNotContain(findings, f => f.Code is WsusRefusalCodes.InstallerMissing or WsusRefusalCodes.InstallerInSubfolder);
    }

    public static TheoryData<string, string> AcceptedScripts => new()
    {
        { "the standard wrapper", StandardInstallScript },
        { "an MSI wrapper that closes the running copy",
            "Get-Process tool -ErrorAction SilentlyContinue | Stop-Process -Force\r\n$msi = Join-Path $PSScriptRoot 'product.msi'\r\n$p = Start-Process msiexec.exe -ArgumentList @('/i', \"`\"$msi`\"\", '/qn') -Wait -PassThru\r\nexit $p.ExitCode" },
        { "a direct installer call", "& (Join-Path $PSScriptRoot 'setup.exe') /S\r\nexit $LASTEXITCODE" },
        { "a wrapper written with aliases",
            "gps tool -ErrorAction SilentlyContinue | kill -Force\r\nsleep 2\r\n$p = saps (Join-Path $PSScriptRoot 'setup.exe') -ArgumentList '/S' -Wait -PassThru\r\nexit $p.ExitCode" },
        { "a wrapper that waits on its process object",
            "$p = Start-Process (Join-Path $PSScriptRoot 'setup.exe') -ArgumentList '/S' -PassThru\r\n$p.WaitForExit()\r\nif ([string]$p.ExitCode.ToString().Trim() -eq '0') { exit 0 }\r\nexit $p.ExitCode" },
        { "a wrapper that finds its MSI and reports errors",
            "$msi = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.msi' | Select-Object -First 1\r\nif (-not $msi) { Write-Error 'No MSI in the content folder.'; exit 1 }\r\n$p = Start-Process msiexec.exe -ArgumentList @('/i', $msi.FullName, '/qn') -Wait -PassThru\r\nexit $p.ExitCode" },
        { "the desktop EXE wrapper",
            GuardedExe(WrapperGeneratorService.BuildExeInstallScript("setup.exe", "/S")) },
        { "the desktop EXE wrapper that stops launched processes",
            GuardedExe(WrapperGeneratorService.BuildExeInstallScript("setup.exe", "/S", postInstallKillProcesses: ["tool", "tray"])) },
        { "the desktop EXE wrapper with a reboot exit code",
            WrapperGeneratorService.ApplyRebootExitCodes(GuardedExe(WrapperGeneratorService.BuildExeInstallScript("setup.exe", "/S")), [1641]) }
    };

    private static string GuardedExe(string script) => WrapperGeneratorService.GuardNullProcess(script);

    [Theory]
    [MemberData(nameof(AcceptedScripts))]
    public void AcceptsAPlainInstallerLaunch(string name, string script)
    {
        var path = Path.Combine(_temp.Path, $"install-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, script);
        Assert.Empty(WsusCompatibility.InstallScriptExtras(path));
        Assert.NotEmpty(name);
    }

    [Fact]
    public void AcceptsTheDesktopMsiWrapper()
    {
        var folder = Path.Combine(_temp.Path, "msi-wrapper");
        Directory.CreateDirectory(folder);
        new WrapperGeneratorService().GenerateMsiWrappers(folder, "product.msi", installArgs: "/qn /norestart ALLUSERS=1");
        Assert.Empty(WsusCompatibility.InstallScriptExtras(Path.Combine(folder, "install.ps1")));
    }

    public static TheoryData<string, string, string> NamedExtras => new()
    {
        { "a machine environment variable", "[Environment]::SetEnvironmentVariable",
            "$p = Start-Process setup.exe -ArgumentList '/S' -Wait -PassThru\r\n[Environment]::SetEnvironmentVariable('DBEAVER_AI_DISABLED', 'true', 'Machine')\r\nexit 0" },
        { "a second process", "2 process launches",
            "Start-Process setup.exe -ArgumentList '/x' -Wait\r\nStart-Process (Join-Path $env:TEMP 'x\\setup.exe') -Wait" },
        { "a second launch next to the start object of the wrapper", "2 process launches",
            "$startInfo = New-Object System.Diagnostics.ProcessStartInfo\r\n$startInfo.FileName = $exePath\r\n$proc = [System.Diagnostics.Process]::Start($startInfo)\r\nStart-Process (Join-Path $env:TEMP 'x\\setup.exe') -Wait\r\nexit 0" },
        { "a second start through the type", "2 process launches",
            "$startInfo = New-Object System.Diagnostics.ProcessStartInfo\r\n$startInfo.FileName = $exePath\r\n$proc = [System.Diagnostics.Process]::Start($startInfo)\r\n$other = [System.Diagnostics.Process]::Start($startInfo)\r\nexit 0" },
        { "another object than the start object of the wrapper", "New-Object",
            "Start-Process setup.exe -Wait\r\n$client = New-Object System.Net.WebClient" },
        { "a setting of another object than the start object", "$other.FileName assignment",
            "$other = New-Object System.Diagnostics.ProcessStartInfo\r\n$other.FileName = $exePath\r\nStart-Process setup.exe -Wait" },
        { "another setting of the start object", "$startInfo.WorkingDirectory assignment",
            "$startInfo = New-Object System.Diagnostics.ProcessStartInfo\r\n$startInfo.WorkingDirectory = 'C:\\Windows'\r\n$proc = [System.Diagnostics.Process]::Start($startInfo)\r\nexit 0" },
        { "another method of the process type", "[System.Diagnostics.Process]::GetProcessById",
            "Start-Process setup.exe -Wait\r\n$null = [System.Diagnostics.Process]::GetProcessById(4)" },
        { "a registry write", "Set-ItemProperty",
            "Start-Process setup.exe -Wait\r\nSet-ItemProperty -Path 'HKLM:\\SOFTWARE\\Contoso' -Name AutoUpdate -Value 0" },
        { "a file copy", "Copy-Item",
            "Start-Process setup.exe -Wait\r\nCopy-Item (Join-Path $PSScriptRoot 'settings.xml') 'C:\\ProgramData\\Contoso'" },
        { "a script that does not parse", "install.ps1 does not parse", "Start-Process setup.exe -Wait {" },
        { "a file deleted through its own method", ".Delete()",
            "$p = Start-Process setup.exe -ArgumentList '/S' -Wait -PassThru\r\n(Get-Item 'C:\\ProgramData\\Contoso\\settings.xml').Delete()\r\nexit $p.ExitCode" },
        { "a file changed through a property", "$f.IsReadOnly assignment",
            "Start-Process setup.exe -Wait\r\n$f = Get-Item 'C:\\ProgramData\\Contoso\\settings.xml'\r\n$f.IsReadOnly = $true" },
        { "an environment variable for the installer", "$env:CONTOSO_SILENT assignment",
            "$env:CONTOSO_SILENT = '1'\r\nStart-Process setup.exe -Wait" },
        { "a desktop post-install configuration", "post-install configuration",
            WrapperGeneratorService.BuildExeInstallScript("setup.exe", "/S", "Write-Host 'configured'") },
        { "a desktop post-install registry write", "Set-ItemProperty",
            WrapperGeneratorService.BuildExeInstallScript("setup.exe", "/S", "Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Contoso' -Name X -Value 1") }
    };

    [Theory]
    [MemberData(nameof(NamedExtras))]
    public void NamesEachStepBesidesTheInstallerLaunch(string name, string expected, string script)
    {
        var path = Path.Combine(_temp.Path, $"install-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, script);
        Assert.Contains(expected, WsusCompatibility.InstallScriptExtras(path));
        Assert.NotEmpty(name);
    }

    [Fact]
    public void ReportsNoBlockingFindingForAPlainSilentExe()
    {
        var findings = WsusCompatibility.Evaluate(Manifest(), _content);
        Assert.DoesNotContain(findings, f => f.Severity == WsusSeverity.Blocking);
        Assert.Single(findings, f => f.Code == WsusRefusalCodes.UninstallNotPublished);
    }

    public static TheoryData<string, Action<StageManifest>> BlockingCases => new()
    {
        { WsusRefusalCodes.MultipleDeploymentTypes, m => m.DeploymentTypes = [new StageDeploymentType { NameSuffix = "x64" }] },
        { WsusRefusalCodes.InstallerTypeUnsupported, m => m.InstallerType = "MSIX" },
        { WsusRefusalCodes.PerUserInstall, m => m.InstallationBehaviorType = "InstallForUser" },
        { WsusRefusalCodes.PerUserInstall, m => m.InstallContext = "User" },
        { WsusRefusalCodes.InteractiveInstall, m => m.RequireUserInteraction = true },
        { WsusRefusalCodes.CustomInstall, m => m.SetupFile = "Deploy-Application.exe" },
        { WsusRefusalCodes.CustomInstall, m => m.CustomAssets = [new StageCustomAsset { Category = "InstallBefore", RelativePath = "install-before.ps1" }] },
        { WsusRefusalCodes.CustomInstall, m => m.CommandOverrides = new StageCommandOverrides { Install = "setup.exe /quiet", Uninstall = "" } },
        { WsusRefusalCodes.InstallerMissing, m => m.InstallerFile = "absent.exe" },
        { WsusRefusalCodes.InstallerMissing, m => m.InstallerFile = "" },
        { WsusRefusalCodes.DetectionNotMappable, m => m.Detection = new StageDetection { Type = "Script", ScriptText = "exit 0" } }
    };

    [Theory]
    [MemberData(nameof(BlockingCases))]
    public void Blocks(string code, Action<StageManifest> change)
    {
        Assert.Single(WsusCompatibility.Evaluate(Manifest(change), _content), f => f.Severity == WsusSeverity.Blocking && f.Code == code);
    }

    [Fact]
    public void BlocksAnInstallerInASubfolder()
    {
        Directory.CreateDirectory(Path.Combine(_content, "bin"));
        File.WriteAllText(Path.Combine(_content, "bin", "setup.exe"), "x");
        Assert.Single(WsusCompatibility.Evaluate(Manifest(m => m.InstallerFile = @"bin\setup.exe"), _content),
            f => f.Code == WsusRefusalCodes.InstallerInSubfolder);
    }

    [Fact]
    public void DoesNotBlockAnUninstallOnlyCommandOverrideOrTheDefaultInstallEntry()
    {
        var manifest = Manifest(m => { m.SetupFile = "install.bat"; m.CommandOverrides = new StageCommandOverrides { Install = "", Uninstall = "uninstall.exe /S" }; m.UninstallCommand = "$proc = Start-Process uninstall.exe -Wait -PassThru"; });
        Assert.DoesNotContain(WsusCompatibility.Evaluate(manifest, _content), f => f.Severity == WsusSeverity.Blocking);
    }

    [Fact]
    public void AsksForReviewOfRequirementRulesAndRunningProcesses()
    {
        var manifest = Manifest(m => { m.Requirements = [new StageRequirement { RuleId = "cpu-arch" }]; m.RunningProcess = ["tool"]; });
        var codes = WsusCompatibility.Evaluate(manifest, _content).Where(f => f.Severity == WsusSeverity.Review).Select(f => f.Code).ToList();
        Assert.Contains(WsusRefusalCodes.RequirementsNotTranslated, codes);
        Assert.Contains(WsusRefusalCodes.RunningProcessNotClosed, codes);
    }

    [Fact]
    public void RecordsThatRuntimeMinutesAreNotSentToWsus()
    {
        Assert.DoesNotContain(WsusCompatibility.Evaluate(Manifest(), _content), f => f.Code == WsusRefusalCodes.TimingNotApplied);
        var info = Assert.Single(WsusCompatibility.Evaluate(Manifest(m => m.Timing = new StageTiming { EstimatedMinutes = 10, MaximumMinutes = 30 }), _content),
            f => f.Code == WsusRefusalCodes.TimingNotApplied);
        Assert.Equal(WsusSeverity.Info, info.Severity);
    }

    [Fact]
    public void BlocksAnInstallScriptThatDoesMoreThanRunTheInstaller()
    {
        var folder = NewContentFolder(_temp, "custom");
        File.WriteAllText(Path.Combine(folder, "install.ps1"), StandardInstallScript + "\r\n[Environment]::SetEnvironmentVariable('DBEAVER_AI_DISABLED', 'true', 'Machine')");
        var finding = Assert.Single(WsusCompatibility.Evaluate(Manifest(), folder), f => f.Code == WsusRefusalCodes.CustomInstall);
        Assert.Equal(WsusSeverity.Blocking, finding.Severity);
        Assert.StartsWith("install.ps1 does more than run the installer: [Environment]::SetEnvironmentVariable", finding.Message);
    }

    [Fact]
    public void BlocksRecordedPayloadFilesInASubfolderOrMissingFromTheStage()
    {
        var folder = NewContentFolder(_temp, "nested");
        Directory.CreateDirectory(Path.Combine(folder, "Data"));
        File.WriteAllText(Path.Combine(folder, @"Data\x.cab"), "cab");
        var manifest = Manifest(m => m.FileHashes = new[] { "setup.exe", @"Data\x.cab", "gone.cab" }
            .Select(r => new StageFileHash { RelativePath = r, Sha256 = new string('0', 64), Size = 1 }).ToArray());
        var findings = WsusCompatibility.Evaluate(manifest, folder);
        Assert.Contains(@"Data\x.cab", Assert.Single(findings, f => f.Code == WsusRefusalCodes.PayloadInSubfolder).Message);
        Assert.Contains("gone.cab", Assert.Single(findings, f => f.Code == WsusRefusalCodes.PayloadMissing).Message);
    }

    [Fact]
    public void BlocksADetectionWhoseExpectedValueIsNotANumericVersion()
    {
        var manifest = Manifest(m => m.Detection = Registry(@"SOFTWARE\Contoso", "2026.08.2-200", op: "IsEquals"));
        var finding = Assert.Single(WsusCompatibility.Evaluate(manifest, _content), f => f.Code == WsusRefusalCodes.DetectionNotMappable);
        Assert.Equal(WsusSeverity.Blocking, finding.Severity);
        Assert.Contains("2026.08.2-200", finding.Message);
    }
}
