using System.Diagnostics;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// TeraCopy's setup EXE registers an MSI: its only Add/Remove Programs entry
/// is an msiexec command, which the generic uninstaller lookup filters out.
/// Postman's setup returns while its updater is still writing the install.
/// </summary>
public sealed class MsiBackedUninstallAndInstallWaitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-msiun-" + Guid.NewGuid().ToString("N"));

    public MsiBackedUninstallAndInstallWaitTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private (string Install, string Uninstall) Generate(string definition)
    {
        var def = DefinitionFixture.Load(definition);
        new WrapperGeneratorService().GenerateExeWrappers(_dir, def.InstallerFileName!, def.InstallArgs, def.UninstallArgs,
            def.InstallerFramework, def.Application, def.UninstallCommand,
            StagingService.ResolvePostInstallScript(def, null));
        return (File.ReadAllText(Path.Combine(_dir, "install.ps1")), File.ReadAllText(Path.Combine(_dir, "uninstall.ps1")));
    }

    [Fact]
    public void TeraCopy_UninstallsThroughMsiexecWithTheProductCodeOfItsOwnEntry()
    {
        var (_, uninstall) = Generate("teracopy.json");

        Assert.Contains("-notlike 'TeraCopy*'", uninstall);
        Assert.Contains("Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/x', $code, '/qn', '/norestart')", uninstall);
        Assert.DoesNotContain("teracopy-setup.exe", uninstall);
        Assert.DoesNotContain("$arpPaths", uninstall);
    }

    [Fact]
    public void TeraCopy_NamesTheAdvancedInstallerFrameworkItsSwitchesBelongTo()
    {
        var definition = DefinitionFixture.Load("teracopy.json");

        Assert.Equal("AdvancedInstaller", definition.InstallerFramework);
        Assert.StartsWith("/exenoui", definition.InstallArgs);
        Assert.Contains("/exenoui", SilentSwitchDatabase.Lookup(definition.InstallerFramework!).Install);
    }

    [Fact]
    public void TeraCopy_WithNoEntry_TheUninstallScriptExitsOne()
    {
        var (_, uninstall) = Generate("teracopy.json");
        // The lookup must find nothing on the machine that runs the tests.
        var script = uninstall.Replace("'TeraCopy*'", "'packr-absent-product-*'");
        var path = Path.Combine(_dir, "probe-uninstall.ps1");
        File.WriteAllText(path, script);

        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path })
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        Assert.True(proc.WaitForExit(60_000));

        Assert.Equal(1, proc.ExitCode);
    }

    [Fact]
    public void Postman_InstallWaitsForTheApplicationFileBeforeItReportsSuccess()
    {
        var (install, _) = Generate("postman.json");

        var guard = install.IndexOf("if ($proc.ExitCode -ne 0) { exit $proc.ExitCode }", StringComparison.Ordinal);
        var wait = install.IndexOf("while (-not (Test-Path -LiteralPath $installedExe))", StringComparison.Ordinal);
        var stop = install.IndexOf("Stop-Process -Name Postman", StringComparison.Ordinal);

        Assert.True(guard >= 0 && guard < wait && wait < stop, install);
        Assert.Contains("Join-Path $env:LOCALAPPDATA 'Postman\\Postman.exe'", install);
        Assert.Contains("exit 4", install);
        Assert.EndsWith("exit 0", install.TrimEnd());
    }
}
