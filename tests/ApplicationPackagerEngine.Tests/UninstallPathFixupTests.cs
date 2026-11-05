using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Uninstall commands that name the product's own uninstaller. A wrong file
/// name leaves the product installed; re-running a staged setup with an
/// uninstall switch is not an uninstall for these products.
/// </summary>
public sealed class UninstallPathFixupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-uninst-" + Guid.NewGuid().ToString("N"));

    public UninstallPathFixupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string UninstallScript(string fileName, string installerFile)
    {
        var def = DefinitionFixture.Load(fileName);
        new WrapperGeneratorService().GenerateExeWrappers(_dir, installerFile, def.InstallArgs, def.UninstallArgs,
            def.InstallerFramework, def.Application, def.UninstallCommand);
        return File.ReadAllText(Path.Combine(_dir, "uninstall.ps1"));
    }

    [Fact]
    public void PSPad_RunsTheUninstallerUnderUninst()
    {
        var command = DefinitionFixture.Load("pspad.json").UninstallCommand!;

        Assert.Contains(@"-FilePath ""C:\Program Files\PSPad\Uninst\unins000.exe""", command);
        Assert.DoesNotContain(@"PSPad\unins000.exe", command);
        Assert.StartsWith("Get-Process PSPad", command);
    }

    [Fact]
    public void Rainmeter_RunsUninstExe()
    {
        var command = DefinitionFixture.Load("rainmeter.json").UninstallCommand!;

        Assert.Contains(@"-FilePath ""C:\Program Files\Rainmeter\uninst.exe""", command);
        Assert.DoesNotContain("uninstall.exe", command);
        Assert.StartsWith("Get-Process Rainmeter", command);
    }

    /// <summary>
    /// Squirrel removes the ARP entry and reports success while the app runs,
    /// leaving the app folder and the process in place, so the process stops
    /// first. Update.exe in the profile is the uninstaller, not the staged setup.
    /// </summary>
    [Fact]
    public void Postman_StopsTheAppAndRunsSquirrelUpdateExe()
    {
        var script = UninstallScript("postman.json", "Postman-win64-Setup.exe");

        Assert.StartsWith("Get-Process Postman -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2;", script);
        Assert.Contains(@"$updateExe = Join-Path $env:LOCALAPPDATA 'Postman\Update.exe'", script);
        Assert.Contains("if (-not (Test-Path -LiteralPath $updateExe)) { exit 0 }", script);
        Assert.Contains("-FilePath $updateExe -ArgumentList @('--uninstall', '-s')", script);
        Assert.DoesNotContain("Postman-win64-Setup.exe", script);
        Assert.DoesNotContain("$arpPaths", script);
    }

    /// <summary>The installer's own /uninstall switch exits 1 and removes nothing.</summary>
    [Fact]
    public void CutePdfWriter_RunsItsDedicatedUninstaller()
    {
        var def = DefinitionFixture.Load("cutepdfwriter.json");
        var script = UninstallScript("cutepdfwriter.json", "CuteWriter-4.0.exe");

        Assert.Equal("/uninstall /s", def.UninstallArgs);
        Assert.Contains(@"$uninstaller = Join-Path ${env:ProgramFiles(x86)} 'CutePDF Writer\unInstcpw64.exe'", script);
        Assert.Contains("if (-not (Test-Path -LiteralPath $uninstaller)) { Remove-Item -LiteralPath 'HKLM:\\SOFTWARE\\App Packager\\CutePDF Writer' -Recurse -Force -ErrorAction SilentlyContinue; exit 0 }", script);
        Assert.Contains("if ($proc.ExitCode -eq 0) { Remove-Item -LiteralPath 'HKLM:\\SOFTWARE\\App Packager\\CutePDF Writer'", script);
        Assert.Contains("-FilePath $uninstaller -ArgumentList @('/uninstall', '/s')", script);
        Assert.DoesNotContain("CuteWriter", script);
    }
}
