using System.Diagnostics;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// An installer that launches its application on success keeps a
/// Start-Process -Wait open for as long as that application runs, which under
/// a system-context deployment is forever. The install wrapper for such a
/// definition waits for the installer process alone and then stops the
/// launched instances.
/// </summary>
public sealed class PostInstallKillProcessesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-kill-" + Guid.NewGuid().ToString("N"));

    public PostInstallKillProcessesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void WithProcesses_TheInstallerIsWaitedOnAloneAndTheNamedProcessesAreStopped()
    {
        var script = WrapperGeneratorService.BuildExeInstallScript("picpick_inst.exe", "/S", null, ["picpick"]);

        Assert.Equal(
            "$exePath = Join-Path $PSScriptRoot 'picpick_inst.exe'\r\n" +
            "$startInfo = New-Object System.Diagnostics.ProcessStartInfo\r\n" +
            "$startInfo.FileName = $exePath\r\n" +
            "$startInfo.Arguments = '/S'\r\n" +
            "$startInfo.UseShellExecute = $false\r\n" +
            "$proc = [System.Diagnostics.Process]::Start($startInfo)\r\n" +
            "$proc.WaitForExit()\r\n" +
            "Start-Sleep -Seconds 3\r\n" +
            "foreach ($pn in @('picpick')) { Get-Process -Name $pn -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }\r\n" +
            "$proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline\r\n" +
            "exit $proc.ExitCode",
            script);
    }

    [Fact]
    public void WithoutProcesses_TheInstallBodyIsUnchanged()
    {
        var expected =
            "$exePath = Join-Path $PSScriptRoot 'setup.exe'\r\n" +
            "$proc = Start-Process -FilePath $exePath -ArgumentList '/S' -Wait -PassThru -NoNewWindow\r\n" +
            "$proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline\r\n" +
            "exit $proc.ExitCode";

        Assert.Equal(expected, WrapperGeneratorService.BuildExeInstallScript("setup.exe", "/S"));
        Assert.Equal(expected, WrapperGeneratorService.BuildExeInstallScript("setup.exe", "/S", null, []));
        Assert.Equal(expected, WrapperGeneratorService.BuildExeInstallScript("setup.exe", "/S", null, ["  "]));
    }

    [Fact]
    public void ANameWithAnApostrophe_IsEscapedInTheLiteral()
    {
        var script = WrapperGeneratorService.BuildExeInstallScript("setup.exe", "/S", null, ["it's", "Other"]);

        Assert.Contains("foreach ($pn in @('it''s', 'Other'))", script);
    }

    [Fact]
    public void WithAPostInstallScript_TheStopPrecedesTheExitCodeGuard()
    {
        var script = WrapperGeneratorService.BuildExeInstallScript("setup.exe", "/S", "Set-Content x y", ["App"]);

        Assert.DoesNotContain("-Wait", script);
        var stop = script.IndexOf("Stop-Process", StringComparison.Ordinal);
        var guard = script.IndexOf("if ($proc.ExitCode -ne 0) { exit $proc.ExitCode }", StringComparison.Ordinal);
        var config = script.IndexOf("Set-Content x y", StringComparison.Ordinal);
        Assert.True(stop >= 0 && stop < guard && guard < config, script);
        Assert.EndsWith("exit 0", script);
    }

    [Theory]
    [InlineData("picpick.json", "picpick")]
    [InlineData("hashtools.json", "HashTools")]
    [InlineData("windowsadmincenter.json", "WindowsAdminCenterLauncher")]
    public void TaggedDefinitions_StopTheApplicationTheirInstallerLaunches(string fileName, string process)
    {
        var def = DefinitionFixture.Load(fileName);

        Assert.Equal([process], def.PostInstallKillProcesses!);

        new WrapperGeneratorService().GenerateExeWrappers(_dir, "setup.exe", def.InstallArgs, def.UninstallArgs,
            def.InstallerFramework, def.Application, def.UninstallCommand, null, null, null, def.PostInstallKillProcesses);
        var install = File.ReadAllText(Path.Combine(_dir, "install.ps1"));

        Assert.DoesNotContain("-Wait", install);
        Assert.Contains("$proc.WaitForExit()", install);
        Assert.Contains($"foreach ($pn in @('{process}'))", install);
    }

    [Fact]
    public void UntaggedDefinitions_DeclareNoProcessesToStop()
    {
        var tagged = new[] { "picpick.json", "hashtools.json", "windowsadmincenter.json" };
        var others = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Definitions"), "*.json")
            .Select(Path.GetFileName)
            .Where(n => !tagged.Contains(n))
            .Where(n => DefinitionFixture.Load(n!).PostInstallKillProcesses is { Length: > 0 })
            .ToList();

        Assert.Empty(others);
    }

    /// <summary>Puts a pause right after the line that starts the installer: a slow host, which looks at the process late.</summary>
    private static string Wait(string script) => System.Text.RegularExpressions.Regex.Replace(script, @"(?m)^(\$proc = .*)\r$", "$1\r\nStart-Sleep -Milliseconds 1500");

    /// <summary>
    /// The cause of the old wrapper: a process from Start-Process -PassThru without -Wait holds no handle, so when it ends before
    /// the script reads the handle, Windows PowerShell reports a null ExitCode and the wrapper exits 0. The test pins that
    /// behavior of the host with a slow reader (the installer ends at once, the script looks 1.5 seconds later).
    /// </summary>
    [Fact]
    public void StartProcessWithoutTheWaitLosesTheExitCodeOfAProcessThatEndedBeforeTheHandleWasRead()
    {
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), Path.Combine(_dir, "setup.exe"));
        File.WriteAllText(Path.Combine(_dir, "probe.ps1"),
            "$exePath = Join-Path $PSScriptRoot 'setup.exe'\r\n" +
            "$proc = Start-Process -FilePath $exePath -ArgumentList '/c exit 5' -PassThru -NoNewWindow\r\n" +
            "Start-Sleep -Milliseconds 1500\r\n" +
            "$null = $proc.Handle\r\n" +
            "$proc.WaitForExit()\r\n" +
            "$proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline\r\n" +
            "exit $proc.ExitCode");

        var (code, file) = WrapperRun.Ps1(_dir, "probe.ps1");

        Assert.Equal(0, code);
        Assert.True(string.IsNullOrEmpty(file), file);
    }

    /// <summary>
    /// The wrapper starts the installer so that the process object holds its handle from the start. An installer that ends at once,
    /// before the script looks at it (here: 1.5 seconds later), still gives its own exit code.
    /// </summary>
    [Fact]
    public void AnInstallerThatEndsBeforeTheWrapperLooksAtItKeepsItsExitCode()
    {
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), Path.Combine(_dir, "setup.exe"));
        var script = Wait(WrapperGeneratorService.BuildExeInstallScript("setup.exe", "/c exit 5", null, ["packr-none"]));
        File.WriteAllText(Path.Combine(_dir, "install.ps1"), script);

        Assert.Equal((5, "5"), WrapperRun.Ps1(_dir, "install.ps1"));
    }

    [Fact]
    public void AnInstallerWithoutArgumentsIsStartedWithoutAnArgumentsLine()
    {
        var script = WrapperGeneratorService.BuildExeInstallScript("setup.exe", "", null, ["App"]);

        Assert.DoesNotContain("$startInfo.Arguments", script);
        Assert.Contains("[System.Diagnostics.Process]::Start($startInfo)", script);
    }

    [Fact]
    public void AnArgumentWithAnApostropheIsEscapedInTheArgumentsLine()
    {
        var script = WrapperGeneratorService.BuildExeInstallScript("setup.exe", "/D=C:\\Bob's", null, ["App"]);

        Assert.Contains("$startInfo.Arguments = '/D=C:\\Bob''s'", script);
    }

    /// <summary>
    /// Runs the generated wrapper in Windows PowerShell. The stand-in installer
    /// starts a long-lived child and exits 5: the wrapper has to return well
    /// before the child would end, stop it, and still report the installer's
    /// own exit code without -Wait.
    /// </summary>
    [Fact]
    public void TheWrapperReturnsTheInstallerExitCodeAndStopsTheLaunchedChild()
    {
        var child = "packr-child-" + Guid.NewGuid().ToString("N")[..8];
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        File.Copy(Path.Combine(system, "cmd.exe"), Path.Combine(_dir, "setup.exe"));
        File.Copy(Path.Combine(system, "PING.EXE"), Path.Combine(_dir, child + ".exe"));

        var installArgs = $"/c start \"\" /b \"{Path.Combine(_dir, child + ".exe")}\" -n 60 127.0.0.1 >nul 2>nul & exit 5";
        new WrapperGeneratorService().GenerateExeWrappers(_dir, "setup.exe", installArgs, "", "Unknown", "Probe",
            "$proc = [pscustomobject]@{ ExitCode = 0 }", null, null, null, [child]);

        try
        {
            var watch = Stopwatch.StartNew();
            var (exitCode, file) = WrapperRun.Ps1(_dir, "install.ps1", 45);
            watch.Stop();

            Assert.Equal(5, exitCode);
            Assert.Equal("5", file);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(40), $"took {watch.Elapsed}");
            // TerminateProcess completes asynchronously; the image can linger briefly.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Process.GetProcessesByName(child).Length > 0 && DateTime.UtcNow < deadline)
                Thread.Sleep(100);
            Assert.Empty(Process.GetProcessesByName(child));
        }
        finally
        {
            foreach (var p in Process.GetProcessesByName(child))
            {
                try { p.Kill(); p.WaitForExit(5000); } catch { }
                p.Dispose();
            }
        }
    }
}
