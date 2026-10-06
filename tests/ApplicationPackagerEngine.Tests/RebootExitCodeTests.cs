using System.Diagnostics;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// An installer that signals "reboot required" with its own code reports a
/// failure to the deployment agent unless the wrapper translates it to 3010.
/// The .bat wrapper returns the value in exitcode.txt, so the translated code
/// has to be the one written there.
/// </summary>
public sealed class RebootExitCodeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-reboot-" + Guid.NewGuid().ToString("N"));

    public RebootExitCodeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private const string Remap2 = "if ($proc.ExitCode -eq 2) { '3010' | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline; exit 3010 }";

    private (string Install, string Uninstall) Generate(int[]? codes, string? uninstallCommand = null,
        string framework = "Unknown", string? postInstallScript = null, string[]? kill = null)
    {
        var dir = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        new WrapperGeneratorService().GenerateExeWrappers(dir, "setup.exe", "/s", "/x /s", framework, "Probe App",
            uninstallCommand, postInstallScript, null, null, kill, codes);
        return (File.ReadAllText(Path.Combine(dir, "install.ps1")), File.ReadAllText(Path.Combine(dir, "uninstall.ps1")));
    }

    [Fact]
    public void InstallAndUninstall_ReportTheRebootCodeAs3010BeforeWritingTheRawCode()
    {
        var (install, uninstall) = Generate([2], "$proc = Start-Process -FilePath 'x.exe' -Wait -PassThru -NoNewWindow");

        foreach (var script in new[] { install, uninstall })
        {
            var remap = script.IndexOf(Remap2, StringComparison.Ordinal);
            var raw = script.IndexOf("$proc.ExitCode | Out-File", StringComparison.Ordinal);
            Assert.True(remap >= 0 && remap < raw, script);
        }
    }

    [Fact]
    public void TheArpLookupUninstall_RemapsAtEveryExitCodeWrite()
    {
        var (_, uninstall) = Generate([2]);

        var writes = System.Text.RegularExpressions.Regex.Matches(uninstall, @"\$proc\.ExitCode \| Out-File").Count;
        var remaps = System.Text.RegularExpressions.Regex.Matches(uninstall, System.Text.RegularExpressions.Regex.Escape(Remap2)).Count;
        Assert.Equal(2, writes);
        Assert.Equal(writes, remaps);
    }

    [Fact]
    public void WithoutCodes_TheWrappersAreUnchanged()
    {
        var (install, uninstall) = Generate(null, "$proc = Start-Process -FilePath 'x.exe' -Wait -PassThru -NoNewWindow");
        var (installEmpty, uninstallEmpty) = Generate([], "$proc = Start-Process -FilePath 'x.exe' -Wait -PassThru -NoNewWindow");

        Assert.DoesNotContain("3010", install + uninstall);
        Assert.Equal(install, installEmpty);
        Assert.Equal(uninstall, uninstallEmpty);
        Assert.Equal(WrapperGeneratorService.GuardNullProcess(WrapperGeneratorService.BuildExeInstallScript("setup.exe", "/s")), install);
    }

    [Fact]
    public void ZeroAnd3010_AreNeverRemapped()
    {
        Assert.Equal("x", WrapperGeneratorService.ApplyRebootExitCodes("x", [0, 3010]));
    }

    [Fact]
    public void WithAPostInstallScript_TheRemapPrecedesTheSuccessGuard()
    {
        var (install, _) = Generate([2], postInstallScript: "Set-Content x y");

        Assert.True(install.IndexOf(Remap2, StringComparison.Ordinal)
            < install.IndexOf("if ($proc.ExitCode -ne 0)", StringComparison.Ordinal), install);
    }

    [Fact]
    public void WithProcessesToStop_TheRemapFollowsTheStop()
    {
        var (install, _) = Generate([2], kill: ["App"]);

        Assert.True(install.IndexOf("Stop-Process", StringComparison.Ordinal)
            < install.IndexOf(Remap2, StringComparison.Ordinal), install);
    }

    [Fact]
    public void DellCommandUpdate_RemapsTwoAndUninstallsWithThePassthroughSwitches()
    {
        var def = DefinitionFixture.Load("dellcommandupdate.json");

        Assert.Equal([2], def.RebootExitCodes!);
        Assert.Equal("/s", def.InstallArgs);
        Assert.Equal("/passthrough /x /s /v\"/qn\"", def.UninstallArgs);
        Assert.Equal(
            "$exePath = Join-Path $PSScriptRoot 'Dell-Command-Update.exe'; $proc = Start-Process -FilePath $exePath -ArgumentList @('/passthrough', '/x', '/s', '/v\"/qn\"') -Wait -PassThru -NoNewWindow",
            def.UninstallCommand);

        new WrapperGeneratorService().GenerateExeWrappers(_dir, def.InstallerFileName!, def.InstallArgs, def.UninstallArgs,
            def.InstallerFramework, def.Application, def.UninstallCommand, null, null, null,
            def.PostInstallKillProcesses, def.RebootExitCodes);
        var install = File.ReadAllText(Path.Combine(_dir, "install.ps1"));
        var uninstall = File.ReadAllText(Path.Combine(_dir, "uninstall.ps1"));

        Assert.Contains(Remap2, install);
        Assert.Contains(Remap2, uninstall);
        Assert.DoesNotContain("$arpPaths", uninstall);
    }

    [Fact]
    public void OnlyDellCommandUpdate_DeclaresRebootCodes()
    {
        var declared = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Definitions"), "*.json")
            .Select(Path.GetFileName)
            .Where(n => DefinitionFixture.Load(n!).RebootExitCodes is { Length: > 0 })
            .ToList();

        Assert.Equal(["dellcommandupdate.json"], declared);
    }

    /// <summary>
    /// Runs install.ps1 and uninstall.ps1 in Windows PowerShell with cmd.exe
    /// standing in for the installer. Both the script's exit code and the
    /// value left in exitcode.txt carry the remapped code.
    /// </summary>
    [Theory]
    [InlineData(2, 3010)]
    [InlineData(7, 7)]
    [InlineData(0, 0)]
    public void TheWrappersReportTheRemappedCode(int installerCode, int expected)
    {
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            Path.Combine(_dir, "setup.exe"));
        // The stand-in runs for about a second before it exits. Windows PowerShell's
        // Start-Process -NoNewWindow fails with "the process has exited" when the process
        // ends before the cmdlet opens it, and the wrapper then exits 1.
        var command = $"ping -n 2 127.0.0.1 >nul & exit {installerCode}";
        new WrapperGeneratorService().GenerateExeWrappers(_dir, "setup.exe", $"/c {command}", "", "Unknown", "Probe",
            $"$exePath = Join-Path $PSScriptRoot 'setup.exe'; $proc = Start-Process -FilePath $exePath -ArgumentList @('/c', '{command}') -Wait -PassThru -NoNewWindow",
            null, null, null, null, [2]);

        foreach (var name in new[] { "install.ps1", "uninstall.ps1" })
        {
            var (exitCode, file) = WrapperRun.Ps1(_dir, name);
            Assert.True(expected == exitCode && expected.ToString() == file,
                $"{name}: exit code {exitCode}, exitcode.txt '{file}', expected {expected}. Output: {WrapperRun.LastOutput}");
        }
    }
}

/// <summary>Runs a generated wrapper script the way the .bat wrapper launches it.</summary>
internal static class WrapperRun
{
    public static (int ExitCode, string? ExitCodeFile) Ps1(string dir, string name, int timeoutSeconds = 60)
    {
        var exitFile = Path.Combine(dir, "exitcode.txt");
        File.Delete(exitFile);
        using var process = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{Path.Combine(dir, name)}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        // Both streams are read while the script runs: a full pipe would block it.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(timeoutSeconds)), $"{name} did not return");
        LastOutput = $"{stdout.Result}{stderr.Result}".Trim();
        return (process.ExitCode, File.Exists(exitFile) ? File.ReadAllText(exitFile) : null);
    }

    /// <summary>What the last script run on this thread wrote, for a failed assertion's message.</summary>
    [ThreadStatic]
    public static string? LastOutput;
}
