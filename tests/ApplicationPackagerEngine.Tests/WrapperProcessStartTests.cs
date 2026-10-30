using System.Diagnostics;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A wrapper whose installer or uninstaller never starts reports a non-zero
/// exit code, and an empty argument string starts the target with no
/// arguments. Each case runs the generated script under Windows PowerShell 5.1.
/// findstr.exe stands in for a staged installer: started with no arguments it
/// exits 2.
/// </summary>
public sealed class WrapperProcessStartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-wrapstart-" + Guid.NewGuid().ToString("N")[..8]);

    public WrapperProcessStartTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        TestCleanup.DeleteDirectory(_root);
    }

    private string NewDir()
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void StageFindstrAs(string dir, string name) =>
        File.Copy(Path.Combine(Environment.SystemDirectory, "findstr.exe"), Path.Combine(dir, name));

    private static int Run(string script)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        psi.Environment.Remove("PSModulePath");
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(120_000));
        return proc.ExitCode;
    }

    [Fact]
    public void Uninstall_WithEmptyUninstallArguments_NeverStartsTheStagedInstaller()
    {
        var dir = NewDir();
        StageFindstrAs(dir, "setup.exe");
        new WrapperGeneratorService().GenerateExeWrappers(dir, "setup.exe", "", "", installerFramework: null,
            applicationName: "AppPackager Synthetic Product Never Installed " + Guid.NewGuid().ToString("N"));
        var uninstall = Path.Combine(dir, "uninstall.ps1");
        Assert.DoesNotContain("setup.exe", File.ReadAllText(uninstall));
        Assert.Equal(1, Run(uninstall));
    }

    [Fact]
    public void ExeInstall_WithEmptyInstallArguments_StartsTheInstallerWithoutAnArgumentList()
    {
        var dir = NewDir();
        StageFindstrAs(dir, "setup.exe");
        new WrapperGeneratorService().GenerateExeWrappers(dir, "setup.exe", "", "/S");
        var install = Path.Combine(dir, "install.ps1");
        Assert.DoesNotContain("-ArgumentList ''", File.ReadAllText(install));
        Assert.Equal(2, Run(install));
    }

    [Fact]
    public void ArpFallback_WithAMissingInstaller_ExitsNonZero()
    {
        var dir = NewDir();
        new WrapperGeneratorService().GenerateExeWrappers(dir, "setup.exe", "/S", "/uninstall /S", installerFramework: null,
            applicationName: "AppPackager Synthetic Product Never Installed " + Guid.NewGuid().ToString("N"));
        Assert.NotEqual(0, Run(Path.Combine(dir, "uninstall.ps1")));
    }

    [Fact]
    public void ExeInstall_WithAMissingInstaller_ExitsNonZero()
    {
        var dir = NewDir();
        new WrapperGeneratorService().GenerateExeWrappers(dir, "setup.exe", "/S", "/S",
            postInstallKillProcesses: ["appkg-none"], rebootExitCodes: [1641]);
        Assert.NotEqual(0, Run(Path.Combine(dir, "install.ps1")));
    }

    [Fact]
    public void ExeUninstallCommand_ThatCannotStart_ExitsNonZero()
    {
        var dir = NewDir();
        new WrapperGeneratorService().GenerateExeWrappers(dir, "setup.exe", "/S", "/S",
            uninstallCommand: @"$proc = Start-Process -FilePath 'C:\AppPackager-missing\uninstall.exe' -ArgumentList '/S' -Wait -PassThru -NoNewWindow");
        Assert.NotEqual(0, Run(Path.Combine(dir, "uninstall.ps1")));
    }

    [Fact]
    public void MsiUninstallCommand_ThatCannotStart_ExitsNonZero()
    {
        var dir = NewDir();
        new WrapperGeneratorService().GenerateMsiWrappers(dir, "setup.msi",
            uninstallCommand: @"$proc = Start-Process -FilePath 'C:\AppPackager-missing\uninstall.exe' -Wait -PassThru -NoNewWindow");
        Assert.NotEqual(0, Run(Path.Combine(dir, "uninstall.ps1")));
    }

    [Fact]
    public void ARunningInstaller_StillReportsItsOwnExitCode()
    {
        var dir = NewDir();
        StageFindstrAs(dir, "setup.exe");
        new WrapperGeneratorService().GenerateExeWrappers(dir, "setup.exe", "/?", "/S");
        Assert.Equal(0, Run(Path.Combine(dir, "install.ps1")));
    }

    [Fact]
    public void TheGuard_IsOneLineBeforeEachExitCodeWrite()
    {
        Assert.Equal(
            "$x = 1\r\n    if (-not $proc) { exit 1 }\r\n    $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline\r\nexit $proc.ExitCode",
            WrapperGeneratorService.GuardNullProcess(
                "$x = 1\r\n    $proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline\r\nexit $proc.ExitCode"));
    }
}
