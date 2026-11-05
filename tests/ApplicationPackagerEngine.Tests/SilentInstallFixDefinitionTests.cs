using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Install and uninstall switches proven on a clean machine by the PowerShell app
/// (commit 7fdb162): the install scope, the real uninstall entry and the exit
/// code a complete install returns.
/// </summary>
public sealed class SilentInstallFixDefinitionTests
{
    [Fact]
    public void VimInstallsAndUninstallsForAllUsers()
    {
        var def = DefinitionFixture.Load("vim.json");
        Assert.Equal("/S /allusers", def.InstallArgs);
        Assert.Equal("/S /allusers", def.UninstallArgs);
    }

    [Fact]
    public void VimUninstallsThroughTheUninstallStringOfItsEntry()
    {
        var command = DefinitionFixture.Load("vim.json").UninstallCommand!;
        Assert.Contains(@"CurrentVersion\Uninstall\Vim'", command);
        Assert.Contains(@"WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Vim'", command);
        Assert.Contains(".UninstallString", command);
        Assert.Contains("ArgumentList @('/S', '/allusers')", command);
        Assert.DoesNotContain("uninstall-gui.exe", command);
        Assert.DoesNotContain("Get-ChildItem", command);
    }

    [Fact]
    public void PgAdminInstallsForAllUsers()
    {
        var def = DefinitionFixture.Load("pgadmin4.json");
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /ALLUSERS", def.InstallArgs);
    }

    [Fact]
    public void AimpRunsAutomaticallyAndSilentlyOnInstallAndUninstall()
    {
        var def = DefinitionFixture.Load("aimp.json");
        Assert.Equal("/AUTO /SILENT", def.InstallArgs);
        Assert.Equal("/AUTO /SILENT", def.UninstallArgs);
    }

    [Fact]
    public void AnacondaUninstallsInPlaceAndThenDeletesTheLeftoverFolder()
    {
        var command = DefinitionFixture.Load("anaconda.json").UninstallCommand!;
        Assert.Contains("_?={0}", command);
        Assert.Contains("@('/S', ('_?={0}' -f $installDir))", command);
        Assert.Contains("if ($proc.ExitCode -eq 0) { Remove-Item -LiteralPath $installDir -Recurse -Force", command);
        Assert.Contains("if (-not (Test-Path -LiteralPath $uninstaller)) { exit 0 }", command);
        Assert.Contains(@"$installDir = 'C:\ProgramData\Anaconda3'", command);
    }

    [Fact]
    public void TheSessionManagerPluginUninstallsFromTheBundleCacheCopy()
    {
        var command = DefinitionFixture.Load("awsssmplugin.json").UninstallCommand!;
        Assert.Contains("$_.BundleCachePath", command);
        Assert.Contains("-like 'Session Manager Plugin*'", command);
        Assert.Contains("$exe = $hit.BundleCachePath", command);
        Assert.Contains("@('/uninstall', '/quiet', '/norestart')", command);
        Assert.DoesNotContain("QuietUninstallString", command);
    }

    [Fact]
    public void AndroidStudioCountsExitCode1223AsSuccess()
    {
        var codes = DefinitionFixture.Load("androidstudio.json").SuccessExitCodes!;
        Assert.Equal([0, 1223], codes);
        var script = WrapperGeneratorService.ApplySuccessExitCodes(
            WrapperGeneratorService.BuildExeInstallScript("setup.exe", "/S"), codes);
        Assert.Contains("if ($proc.ExitCode -eq 1223) { '0' | Out-File", script);
    }

    // The live feed names a release installer android-studio-rabbit2-windows.exe;
    // only releases before 2025.3 carried the version in the file name.
    private const string Feed = """
        {"content":{"item":[
        {"download":[{"link":"https://edgedl.me.gvt1.com/android/studio/install/2026.2.3.2/android-studio-rabbit3-canary2-windows.exe"}],"build":"AI-262.1.1.1.1.1","name":"Canary","channel":"Canary","version":"2026.2.3.2"},
        {"download":[{"link":"https://edgedl.me.gvt1.com/android/studio/install/2026.2.2.8/android-studio-rabbit2-windows.exe","checksum":"x"},{"link":"https://edgedl.me.gvt1.com/android/studio/ide-zips/2026.2.2.8/android-studio-rabbit2-windows.zip"}],"build":"AI-262.10968.63.2622.16730706","platformVersion":"2026.2.3","name":"Android Studio Rabbit 2 | 2026.2.2","channel":"Release","version":"2026.2.2.8"},
        {"download":[{"link":"https://edgedl.me.gvt1.com/android/studio/install/2025.3.1.5/android-studio-2025.3.1.5-windows.exe"}],"build":"AI-253.1.1.1.1","platformVersion":"2025.3.1","name":"Old","channel":"Release","version":"2025.3.1.5"}
        ]}}
        """;

    [Fact]
    public void AndroidStudioScrapesTheNewestReleaseWhateverItsInstallerIsCalled()
    {
        var config = DefinitionFixture.Load("androidstudio.json").VersionScraper;
        Assert.True(config.SelectHighestVersion);
        Assert.Equal("2026.2.2.8", HttpRegexScraper.ExtractVersion(Feed, config.Pattern, config.SelectHighestVersion));
    }

    [Fact]
    public void AndroidStudioTakesTheInstallerLinkOfThatRelease()
    {
        var config = DefinitionFixture.Load("androidstudio.json").VersionScraper;
        var match = System.Text.RegularExpressions.Regex.Match(Feed,
            config.DownloadUrlQuery!.Replace("{version}", System.Text.RegularExpressions.Regex.Escape("2026.2.2.8")));
        Assert.True(match.Success);
        Assert.Equal("https://edgedl.me.gvt1.com/android/studio/install/2026.2.2.8/android-studio-rabbit2-windows.exe", match.Groups[1].Value);
    }
}
