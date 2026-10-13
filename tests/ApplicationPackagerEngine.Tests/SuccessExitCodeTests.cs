using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Wsus;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// An installer that returns its own code after a complete install reports a failure to
/// the deployment agent unless the install wrapper reports that code as 0. The detection
/// rule still decides whether the install succeeded. WSUS runs the installer itself, so
/// a wrapper that maps a code is a step WSUS does not carry.
/// </summary>
public sealed class SuccessExitCodeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apkg-success-code-" + Guid.NewGuid().ToString("N"));

    public SuccessExitCodeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Map = "if ($proc.ExitCode -eq 111111) { '0' | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline; exit 0 }";

    private (string Install, string Uninstall) Generate(int[]? success, int[]? reboot = null, string? postInstallScript = null)
    {
        var dir = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        new WrapperGeneratorService().GenerateExeWrappers(dir, "setup.exe", "/S", "/S", "Unknown", "Probe App",
            "$proc = Start-Process -FilePath 'x.exe' -Wait -PassThru -NoNewWindow", postInstallScript, null, null, null, reboot, success);
        return (File.ReadAllText(Path.Combine(dir, "install.ps1")), File.ReadAllText(Path.Combine(dir, "uninstall.ps1")));
    }

    [Fact]
    public void TheInstallWrapperReportsTheCodeAsZeroBeforeItWritesTheRawCodeAndTheUninstallWrapperDoesNot()
    {
        var (install, uninstall) = Generate([111111]);

        var map = install.IndexOf(Map, StringComparison.Ordinal);
        Assert.True(map >= 0 && map < install.IndexOf("$proc.ExitCode | Out-File", StringComparison.Ordinal), install);
        Assert.True(install.IndexOf("if (-not $proc)", StringComparison.Ordinal) < map, install);
        Assert.DoesNotContain("111111", uninstall);
    }

    [Fact]
    public void WithoutCodesTheWrapperIsUnchangedAndZeroIsNeverMapped()
    {
        Assert.Equal(Generate(null).Install, Generate([]).Install);
        Assert.Equal(Generate(null).Install, Generate([0]).Install);
        Assert.Equal("x", WrapperGeneratorService.ApplySuccessExitCodes("x", [0]));
    }

    [Fact]
    public void APostInstallScriptRunsAfterAMappedCode()
    {
        var (install, _) = Generate([111111], postInstallScript: "Set-Content x y");

        Assert.Contains("if ($proc.ExitCode -ne 0 -and $proc.ExitCode -ne 111111) { exit $proc.ExitCode }", install);
        Assert.DoesNotContain(Map, install);
    }

    [Fact]
    public void WsusReportsAMappedExitCodeAsAStepItDoesNotCarry()
    {
        var (install, _) = Generate([111111]);
        Assert.Contains(WsusInstallScriptScan.Scan(install), extra => extra.StartsWith("installer exit code mapped to success", StringComparison.Ordinal));

        var plain = "$exePath = Join-Path $PSScriptRoot 'setup.exe'\r\nif (-not (Test-Path -LiteralPath $exePath)) { exit 0 }\r\n" +
                    "$proc = Start-Process -FilePath $exePath -ArgumentList @('/S') -Wait -PassThru -NoNewWindow\r\nexit $proc.ExitCode";
        Assert.Empty(WsusInstallScriptScan.Scan(plain));

        var mapped = "$exePath = Join-Path $PSScriptRoot 'setup.exe'\r\n$proc = Start-Process -FilePath $exePath -ArgumentList @('/S') -Wait -PassThru -NoNewWindow\r\n" +
                     "if ($proc.ExitCode -eq 111111) { exit 0 }\r\nexit $proc.ExitCode";
        Assert.Equal(["installer exit code mapped to success ($proc.ExitCode -eq 111111)"], WsusInstallScriptScan.Scan(mapped));

        var listed = "$proc = Start-Process setup.exe -Wait -PassThru\r\nif ($proc.ExitCode -in 5, 6) {\r\n    exit 0\r\n}\r\nexit $proc.ExitCode";
        Assert.Contains(WsusInstallScriptScan.Scan(listed), extra => extra.StartsWith("installer exit code mapped to success", StringComparison.Ordinal));

        // A reboot code reported as 3010 is not a code reported as success.
        Assert.Empty(WsusInstallScriptScan.Scan(Generate(null, reboot: [1641]).Install));
    }

    [Fact]
    public void CCleanerAcceptsTheCodeItReturnsWithoutADesktopSession()
    {
        var def = DefinitionFixture.Load("ccleaner.json");
        Assert.Equal([111111], def.SuccessExitCodes!);
    }

    /// <summary>Runs install.ps1 in Windows PowerShell with cmd.exe standing in for the installer.</summary>
    [Theory]
    [InlineData(111111, 0)]
    [InlineData(7, 7)]
    [InlineData(0, 0)]
    public void TheInstallWrapperReportsTheMappedCode(int installerCode, int expected)
    {
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), Path.Combine(_dir, "setup.exe"));
        // The stand-in runs for about a second: Start-Process -NoNewWindow fails when the process ends before the cmdlet opens it.
        var command = $"ping -n 2 127.0.0.1 >nul & exit {installerCode}";
        new WrapperGeneratorService().GenerateExeWrappers(_dir, "setup.exe", $"/c {command}", "", "Unknown", "Probe",
            "$proc = Start-Process -FilePath 'x.exe' -Wait -PassThru -NoNewWindow", null, null, null, null, null, [111111]);

        var (exitCode, file) = WrapperRun.Ps1(_dir, "install.ps1");
        Assert.True(expected == exitCode && expected.ToString() == file,
            $"exit code {exitCode}, exitcode.txt '{file}', expected {expected}. Output: {WrapperRun.LastOutput}");
    }
}
