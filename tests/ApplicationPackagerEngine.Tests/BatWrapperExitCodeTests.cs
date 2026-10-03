using System.Diagnostics;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The deployment agent runs install.bat and uninstall.bat and sees only
/// their exit code. These tests run the generated .bat under cmd.exe with a
/// stand-in script and pin the code that comes back.
/// </summary>
public sealed class BatWrapperExitCodeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-bat-" + Guid.NewGuid().ToString("N"));

    public BatWrapperExitCodeTests()
    {
        Directory.CreateDirectory(_dir);
        new WrapperGeneratorService().GenerateExeWrappers(_dir, "setup.exe", "/s", "/x /s", "Unknown", "Probe App");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private int RunBat(string batName, string ps1Name, string ps1Body)
    {
        File.WriteAllText(Path.Combine(_dir, ps1Name), ps1Body);
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _dir
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(Path.Combine(_dir, batName));
        using var proc = Process.Start(psi)!;
        Assert.True(proc.WaitForExit(60_000), "the .bat wrapper did not return");
        return proc.ExitCode;
    }

    private const string WriteAndExit = "'{0}' | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline; exit {0}";

    [Theory]
    [InlineData("install.bat", "install.ps1", 0)]
    [InlineData("install.bat", "install.ps1", 7)]
    [InlineData("install.bat", "install.ps1", 1603)]
    [InlineData("install.bat", "install.ps1", 3010)]
    [InlineData("uninstall.bat", "uninstall.ps1", 0)]
    [InlineData("uninstall.bat", "uninstall.ps1", 1605)]
    [InlineData("uninstall.bat", "uninstall.ps1", 3010)]
    public void TheBat_ReturnsTheCodeWrittenToExitCodeFile(string bat, string ps1, int code)
    {
        Assert.Equal(code, RunBat(bat, ps1, string.Format(WriteAndExit, code)));
        Assert.False(File.Exists(Path.Combine(_dir, "exitcode.txt")));
    }

    [Fact]
    public void WithoutTheFile_TheBatReturnsThePowerShellExitCode()
    {
        Assert.Equal(5, RunBat("install.bat", "install.ps1", "exit 5"));
    }

    [Fact]
    public void WithAnEmptyFile_TheBatReturnsThePowerShellExitCode()
    {
        Assert.Equal(6, RunBat("install.bat", "install.ps1",
            "$null | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline; exit 6"));
        Assert.False(File.Exists(Path.Combine(_dir, "exitcode.txt")));
    }

    [Fact]
    public void TheRebootRemap_Reaches3010ThroughTheBat()
    {
        var remapped = WrapperGeneratorService.ApplyRebootExitCodes(
            "$proc = [pscustomobject]@{ ExitCode = 2 }\r\n"
            + "$proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline\r\n"
            + "exit $proc.ExitCode", [2]);

        Assert.Equal(3010, RunBat("install.bat", "install.ps1", remapped));
    }

    [Fact]
    public void AScriptThatThrows_DoesNotReturnSuccess()
    {
        Assert.NotEqual(0, RunBat("install.bat", "install.ps1", "throw 'installer failed to start'"));
    }
}
