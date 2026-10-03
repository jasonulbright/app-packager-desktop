using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The Brave GitHub release carries only the Omaha wrappers, which refuse a
/// system-level install. The raw installer is published unversioned, so its
/// own FileVersion, which reads &lt;Chromium major&gt;.&lt;Brave version&gt;,
/// is the packaged version.
/// </summary>
public sealed class BraveDefinitionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-brave-" + Guid.NewGuid().ToString("N"));

    public BraveDefinitionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string? Transform(string raw, string transform) =>
        typeof(ScraperRegistry).GetMethod("ApplyTransform",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [raw, transform]) as string;

    [Fact]
    public void TheVersionComesFromTheDownloadedInstaller()
    {
        var def = DefinitionFixture.Load("brave.json");

        Assert.Equal("FileVersion", def.VersionScraper.Type);
        Assert.Equal("FileVersion", def.VersionScraper.Pattern);
        Assert.Equal("https://referrals.brave.com/latest/brave_installer-x64.exe", def.VersionScraper.Url);
        Assert.Equal(def.VersionScraper.Url, def.DownloadUrl);
        Assert.DoesNotContain("{", def.DownloadUrl);
        Assert.Equal("brave_installer-x64-{version}.exe", def.InstallerFileName);
    }

    [Theory]
    [InlineData("140.1.79.123", "1.79.123")]
    [InlineData("1.79.123", "1.79.123")]
    public void TheTransformDropsTheChromiumMajor(string raw, string expected)
    {
        Assert.Equal(expected, Transform(raw, DefinitionFixture.Load("brave.json").VersionScraper.VersionTransform!));
    }

    [Fact]
    public void DetectionComparesBraveExeAgainstTheFullFileVersion()
    {
        var staged = StagingService.BuildDetection(DefinitionFixture.Load("brave.json"), null, "1.79.123", "140.1.79.123");

        Assert.Equal("File", staged.Type);
        Assert.Equal(@"C:\Program Files\BraveSoftware\Brave-Browser\Application", staged.FilePath);
        Assert.Equal("brave.exe", staged.FileName);
        Assert.Equal("140.1.79.123", staged.ExpectedValue);
    }

    [Fact]
    public void InstallIsSystemLevelAndDoesNotLaunchTheBrowser()
    {
        var def = DefinitionFixture.Load("brave.json");

        Assert.Equal("--install --silent --system-level --do-not-launch-chrome", def.InstallArgs);
        Assert.Equal("--uninstall --system-level --force-uninstall", def.UninstallArgs);
    }

    [Fact]
    public void UninstallRunsTheInstalledSetupAndMapsItsSuccessAndRebootCodes()
    {
        var def = DefinitionFixture.Load("brave.json");

        new WrapperGeneratorService().GenerateExeWrappers(_dir, "brave_installer-x64-1.79.123.exe", def.InstallArgs,
            def.UninstallArgs, def.InstallerFramework, def.Application, def.UninstallCommand);
        var uninstall = File.ReadAllText(Path.Combine(_dir, "uninstall.ps1"));

        Assert.Contains(@"Get-ChildItem -Path 'C:\Program Files\BraveSoftware\Brave-Browser\Application\*\Installer\setup.exe'", uninstall);
        Assert.Contains("Sort-Object -Property FullName -Descending | Select-Object -First 1", uninstall);
        Assert.Contains("if (-not $setup) { exit 0 }", uninstall);
        Assert.Contains("-ArgumentList @('--uninstall', '--system-level', '--force-uninstall')", uninstall);
        Assert.Contains("if ($proc.ExitCode -eq 19) { exit 0 }", uninstall);
        Assert.Contains("if ($proc.ExitCode -eq 20) { exit 3010 }", uninstall);
        Assert.DoesNotContain("brave_installer-x64-1.79.123.exe", uninstall);
        Assert.True(uninstall.IndexOf("exit 3010", StringComparison.Ordinal)
            < uninstall.IndexOf("$proc.ExitCode | Out-File", StringComparison.Ordinal));
    }
}
