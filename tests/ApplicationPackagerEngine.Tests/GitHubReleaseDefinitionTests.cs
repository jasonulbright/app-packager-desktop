using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// GitHub Desktop resolves its version from the download redirect and
/// installs per user through Squirrel. Signing Suite and AppPackager Suite
/// read the four-part version from their release asset names.
/// </summary>
public sealed class GitHubReleaseDefinitionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-ghrel-" + Guid.NewGuid().ToString("N"));

    public GitHubReleaseDefinitionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string RedirectPattern() =>
        DefinitionFixture.Load("githubdesktop.json").VersionScraper.Pattern["REDIR:".Length..];

    [Theory]
    [InlineData("https://desktop.githubusercontent.com/releases/3.6.6-8b85519e/GitHubDesktopSetup-x64.exe", "3.6.6")]
    [InlineData("https://desktop.githubusercontent.com/releases/3.7.10-0a1b2c3d/GitHubDesktopSetup-x64.exe", "3.7.10")]
    public void GitHubDesktopReadsTheVersionFromTheRedirectTarget(string url, string expected)
    {
        Assert.Equal(expected, HttpRegexScraper.ExtractVersion(url, RedirectPattern(), false));
        var query = DefinitionFixture.Load("githubdesktop.json").VersionScraper.DownloadUrlQuery!.Replace("{version}", Regex.Escape(expected));
        Assert.Equal(url, Regex.Match(url, query, RegexOptions.Multiline).Groups[1].Value);
    }

    [Theory]
    [InlineData("https://central.github.com/deployments/desktop/desktop/latest/win32")]
    [InlineData("https://desktop.githubusercontent.com/releases/3.6.6-8b85519e/GitHubDesktopSetup-x64.msi")]
    [InlineData("https://desktop.githubusercontent.com/releases/3.6.6-8b85519e/GitHubDesktopSetup-arm64.exe")]
    [InlineData("")]
    public void GitHubDesktopRefusesARedirectThatIsNotTheVersionedX64Installer(string url)
    {
        Assert.Null(HttpRegexScraper.ExtractVersion(url, RedirectPattern(), false));
    }

    [Fact]
    public void GitHubDesktopInstallsPerUserAndDetectsItsSquirrelKey()
    {
        var def = DefinitionFixture.Load("githubdesktop.json");

        Assert.Equal("User", def.InstallContext);
        Assert.Equal("-s", def.InstallArgs);
        Assert.Equal("GitHubDesktopSetup-x64.exe", def.InstallerFileName);
        Assert.Equal("GitHub Desktop", def.CMName);
        var staged = StagingService.BuildDetection(def, null, "3.6.6");
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\GitHubDesktop", staged.RegistryKeyRelative);
        Assert.Equal("CurrentUser", staged.Hive);
        Assert.False(staged.Is64Bit);
        Assert.Equal("3.6.6", staged.ExpectedValue);
    }

    [Fact]
    public void GitHubDesktopWaitsForTheInstalledAppThenStopsIt()
    {
        var def = DefinitionFixture.Load("githubdesktop.json");

        new WrapperGeneratorService().GenerateExeWrappers(_dir, def.InstallerFileName!, def.InstallArgs, def.UninstallArgs,
            def.InstallerFramework, def.Application, def.UninstallCommand, StagingService.ResolvePostInstallScript(def, null));
        var install = File.ReadAllText(Path.Combine(_dir, "install.ps1"));
        var uninstall = File.ReadAllText(Path.Combine(_dir, "uninstall.ps1"));

        Assert.Contains("$installedExe = Join-Path $env:LOCALAPPDATA 'GitHubDesktop\\GitHubDesktop.exe'", install);
        Assert.Contains("$deadline = (Get-Date).AddMinutes(5)", install);
        Assert.Contains("        exit 4", install);
        Assert.Contains("Stop-Process -Name GitHubDesktop -Force", install);
        Assert.Contains("$updateExe = Join-Path $env:LOCALAPPDATA 'GitHubDesktop\\Update.exe'", uninstall);
        Assert.Contains("if (-not (Test-Path -LiteralPath $updateExe)) { exit 0 }", uninstall);
        Assert.Contains("@('--uninstall', '-s')", uninstall);
        Assert.True(uninstall.IndexOf("Stop-Process", StringComparison.Ordinal) < uninstall.IndexOf("--uninstall", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("signingsuite.json", "SigningSuiteSetup-2026.09.15.0005.exe", "2026.09.15.0005")]
    [InlineData("apppackagersuite.json", "SuiteSetup-2026.09.21.0032.exe", "2026.09.21.0032")]
    public void TheSuitesReadTheVersionFromTheSetupAssetName(string file, string asset, string expected)
    {
        var pattern = DefinitionFixture.Load(file).VersionScraper.Pattern;
        Assert.Equal(expected, HttpRegexScraper.ExtractVersion($"\"name\": \"{asset}\"", pattern, false));
    }

    [Theory]
    [InlineData("signingsuite.json", "SigningSuite-2026.09.15.0005.zip")]
    [InlineData("signingsuite.json", "checksums.txt")]
    [InlineData("signingsuite.json", "SigningSuiteSetup.exe")]
    [InlineData("apppackagersuite.json", "AppPackagerSuite-2026.09.21.0032.zip")]
    [InlineData("apppackagersuite.json", "SuiteSetup.exe")]
    [InlineData("apppackagersuite.json", "SigningSuiteSetup-2026.09.15.0005.exe")]
    public void TheSuitesIgnoreEveryOtherAsset(string file, string asset)
    {
        Assert.Null(HttpRegexScraper.ExtractVersion($"\"name\": \"{asset}\"", DefinitionFixture.Load(file).VersionScraper.Pattern, false));
    }

    [Fact]
    public void SigningSuiteDetectsItsFixedInnoKeyInTheNativeView()
    {
        var def = DefinitionFixture.Load("signingsuite.json");
        var staged = StagingService.BuildDetection(def, null, "2026.09.27.0007");

        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{6B7F2C1E-4D8A-4F3B-9E21-5A0C7D3B8E64}_is1", staged.RegistryKeyRelative);
        Assert.True(staged.Is64Bit);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-", def.InstallArgs);
        Assert.Contains("if (-not (Test-Path -LiteralPath $key)) { exit 0 }", def.UninstallCommand);
    }

    [Fact]
    public void AppPackagerSuiteInstallsPerUserAndWaitsForTheUninstallerCopy()
    {
        var def = DefinitionFixture.Load("apppackagersuite.json");
        var staged = StagingService.BuildDetection(def, null, "2026.10.02.0045");

        Assert.Equal("User", def.InstallContext);
        Assert.Equal("/S", def.InstallArgs);
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AppPackagerSuite", staged.RegistryKeyRelative);
        Assert.Equal("CurrentUser", staged.Hive);
        Assert.False(staged.Is64Bit);
        Assert.Contains("while (Get-Process -Name 'Au_' -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 1 }", def.UninstallCommand);
    }
}
