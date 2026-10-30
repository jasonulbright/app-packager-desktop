using System.Diagnostics;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The EXE uninstall wrapper has to run the product's own uninstaller. A body
/// that runs the first non-msiexec Add/Remove Programs entry it meets removes,
/// on a machine with two Inno Setup products, whichever one enumerates first.
/// </summary>
public sealed class ArpLookupUninstallTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-arp-" + Guid.NewGuid().ToString("N"));

    public ArpLookupUninstallTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Generate(string framework, string application = "ShareX (x64)",
        string? arpKeyName = null, string? installFolder = null, string uninstallArgs = "/VERYSILENT /NORESTART")
    {
        var dir = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        new WrapperGeneratorService().GenerateExeWrappers(
            dir, "ShareX-21.0.0-setup-x64.exe", "/SP- /VERYSILENT", uninstallArgs,
            framework, application, null, null, arpKeyName, installFolder);
        return File.ReadAllText(Path.Combine(dir, "uninstall.ps1"));
    }

    [Fact]
    public void Inno_MatchesByKeyThenFolderThenDisplayName()
    {
        var script = Generate("InnoSetup", arpKeyName: "ShareX_is1", installFolder: @"C:\Program Files\ShareX");

        Assert.Contains("$keyName = 'ShareX_is1'", script);
        Assert.Contains(@"$installFolder = 'C:\Program Files\ShareX'", script);
        Assert.Contains("$displayName = '*ShareX*'", script);
        Assert.Contains("$_.PSChildName -eq $keyName", script);
        Assert.Contains(".StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)", script);
        Assert.Contains("$_.DisplayName -like $displayName", script);
        Assert.Contains("$_.UninstallString -like '*unins*'", script);
    }

    [Fact]
    public void Inno_NeverRunsAnUnmatchedEntryAndNeverRerunsSetup()
    {
        var script = Generate("InnoSetup");

        Assert.DoesNotContain("Join-Path $PSScriptRoot 'ShareX-21.0.0-setup-x64.exe'", script);
        Assert.EndsWith("exit 1", script.TrimEnd());
        // Every Start-Process in the body is gated on a resolved match.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, "Start-Process"));
        Assert.Contains("if ($match) {", script);
    }

    [Fact]
    public void Nsis_ExitsOneWithoutAMatch()
    {
        var script = Generate("NSIS", application: "Notepad++ (x64)", installFolder: @"C:\Program Files\Notepad++");

        Assert.Contains("$displayName = '*Notepad++*'", script);
        Assert.DoesNotContain("'*unins*'", script);
        Assert.EndsWith("exit 1", script.TrimEnd());
    }

    [Theory]
    [InlineData("InnoSetup")]
    [InlineData("NSIS")]
    [InlineData("WixBurn")]
    [InlineData("BitRock")]
    [InlineData("Squirrel")]
    [InlineData("Unknown")]
    public void EveryEngine_SearchesMachineAndUserEntriesInBothViews(string framework)
    {
        var script = Generate(framework);

        Assert.Contains(@"'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'", script);
        Assert.Contains(@"'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'", script);
        Assert.Contains(@"'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'", script);
        Assert.Contains(@"'HKCU:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'", script);
    }

    [Fact]
    public void OtherEngines_ExitNonZeroWithNoMatch_AndNeverStartTheStagedInstaller()
    {
        var script = Generate("WixBurn", application: "Contoso Bundle", uninstallArgs: "/uninstall /quiet");

        Assert.DoesNotContain("ShareX-21.0.0-setup-x64.exe", script);
        Assert.Contains(WrapperGeneratorService.NoArpEntryExit, script);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, "Start-Process"));
    }

    [Fact]
    public void DisplayNameDropsTheArchitectureTagAndEscapesWildcards()
    {
        Assert.Equal("*Foo [[]Beta[]]*", WrapperGeneratorService.EscapeLikePattern("Foo [Beta]"));
        Assert.Equal("*O''Reilly [*]*", WrapperGeneratorService.EscapeLikePattern("O'Reilly *"));
        Assert.Equal("", WrapperGeneratorService.EscapeLikePattern("  "));

        var script = Generate("NSIS", application: "Git for Windows (x64)");
        Assert.Contains("$displayName = '*Git for Windows*'", script);
    }

    [Fact]
    public void Hints_ComeFromTheDetectionRule()
    {
        var registry = new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Apache NetBeans_is1"
        };
        Assert.Equal(("Apache NetBeans_is1", null), WrapperGeneratorService.ResolveUninstallHints(registry));

        var file = new StageDetection { Type = "File", FilePath = @"C:\Program Files\ShareX", FileName = "ShareX.exe" };
        Assert.Equal((null, @"C:\Program Files\ShareX"), WrapperGeneratorService.ResolveUninstallHints(file));

        var compound = new StageDetection { Type = "Compound", Clauses = [file, registry] };
        Assert.Equal(("Apache NetBeans_is1", @"C:\Program Files\ShareX"), WrapperGeneratorService.ResolveUninstallHints(compound));
    }

    [Fact]
    public void Hints_SkipUnresolvedTokensButKeepGuidKeys()
    {
        var token = new StageDetection
        {
            RegistryKeyRelative = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ProductCode}",
            FilePath = "{setting:InstallScope:Root}"
        };
        Assert.Equal((null, null), WrapperGeneratorService.ResolveUninstallHints(token));

        var guid = new StageDetection
        {
            RegistryKeyRelative = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{9A1B2C3D-0000-4000-8000-000000000001}"
        };
        Assert.Equal(("{9A1B2C3D-0000-4000-8000-000000000001}", null), WrapperGeneratorService.ResolveUninstallHints(guid));

        Assert.Equal((null, null), WrapperGeneratorService.ResolveUninstallHints(null));
        Assert.Equal((null, null), WrapperGeneratorService.ResolveUninstallHints(new StageDetection { Type = "Script" }));
    }

    /// <summary>
    /// Runs the generated body in Windows PowerShell with hints no product can
    /// carry, so the lookup walks every step against the live registry and
    /// reaches the no-match exit without starting anything. Exit 1 reports that nothing was removed.
    /// </summary>
    [Fact]
    public void Inno_BodyParsesAndExitsOneWhenNothingMatches()
    {
        var dir = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var probe = "Packr-" + Guid.NewGuid().ToString("N");
        new WrapperGeneratorService().GenerateExeWrappers(
            dir, "setup.exe", "/VERYSILENT", "/VERYSILENT /NORESTART", "InnoSetup", probe + " (x64)",
            null, null, probe + "_is1", @"C:\Program Files\" + probe);

        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{Path.Combine(dir, "uninstall.ps1")}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 1, $"exit {process.ExitCode}: {stderr}");
        Assert.Equal("", stderr.Trim());
    }
}
