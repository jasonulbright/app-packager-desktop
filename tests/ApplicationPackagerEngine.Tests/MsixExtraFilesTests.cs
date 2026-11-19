using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A definition can carry extra files for an MSIX: a ZIP of dependency packages and a license file. The stage downloads
/// them, checks each against a SHA-256, extracts the dependency packages of the named architecture folders, and the
/// install wrapper hands the packages of the device's architecture and the license to Add-AppxProvisionedPackage.
/// The App Installer (WinGet) and PowerShell 7 (MSIX) definitions use it.
/// </summary>
public sealed class MsixExtraFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "msix-extra-tests-" + Guid.NewGuid().ToString("N"));

    public MsixExtraFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => TestCleanup.DeleteDirectory(_dir);

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private string MakeZip(string name, params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        return path;
    }

    private static AppDefinition Definition(params ExtraFileConfig[] files) => new()
    {
        Application = "Contoso",
        InstallerType = "MSIX",
        ExtraFiles = files
    };

    private static ExtraFileConfig Archive(string hash = "", bool require = false, params string[] folders) => new()
    {
        Kind = ExtraFileService.DependencyArchive,
        DownloadUrl = "https://example.test/v{version}/deps.zip",
        FileName = "deps.zip",
        Sha256 = hash,
        RequireHash = require,
        ArchiveFolders = folders.Length == 0 ? ["x64"] : folders
    };

    private static ExtraFileConfig LicenseFile(string hash = "", bool require = false) => new()
    {
        Kind = ExtraFileService.License,
        DownloadUrl = "https://example.test/v{version}/abc_License1.xml",
        FileName = "License1.xml",
        Sha256 = hash,
        RequireHash = require
    };

    // ---- the model ----------------------------------------------------------------------------

    [Fact]
    public void ADefinitionReadsItsExtraFilesAndStageNotes()
    {
        var def = System.Text.Json.JsonSerializer.Deserialize<AppDefinition>("""
            {"InstallerType":"MSIX","ExtraFiles":[{"Kind":"License","FileName":"License1.xml","DownloadUrl":"u","RequireHash":true},
            {"Kind":"DependencyArchive","FileName":"d.zip","DownloadUrl":"u","ArchiveFolders":["x64","arm64"]}],"StageNotes":["one"]}
            """)!;

        Assert.Equal(2, def.ExtraFiles!.Length);
        Assert.True(def.ExtraFiles[0].RequireHash);
        Assert.Equal(["x64", "arm64"], def.ExtraFiles[1].ArchiveFolders);
        Assert.Equal(["one"], def.StageNotes);
    }

    [Fact]
    public void AnImportedFeedNeverCarriesExtraFilesBecauseTheyDownloadAndFeedScripts()
    {
        Assert.Contains("ExtraFiles", ApplicationPackagerEngine.Services.VendorImport.DefinitionCode.CodeProperties);
        Assert.Empty(ApplicationPackagerEngine.Services.VendorImport.DefinitionCode.Unclassified());
    }

    // ---- validation ---------------------------------------------------------------------------

    [Fact]
    public void OnlyAnMsixDefinitionTakesExtraFiles()
    {
        var def = Definition(LicenseFile());
        def.InstallerType = "MSI";
        var ex = Assert.Throws<InvalidOperationException>(() => ExtraFileService.Validate(def));
        Assert.Contains("only an MSIX definition", ex.Message);
    }

    [Theory]
    [InlineData("Other")]
    [InlineData("")]
    public void AnUnknownKindIsRefused(string kind)
    {
        var file = LicenseFile();
        file.Kind = kind;
        Assert.Throws<InvalidOperationException>(() => ExtraFileService.Validate(Definition(file)));
    }

    [Theory]
    [InlineData("..\\License1.xml")]
    [InlineData("a\\b.xml")]
    [InlineData("")]
    [InlineData("Licençe.xml")]
    public void AnExtraFileNameMustBeAPlainAsciiFileName(string name)
    {
        var file = LicenseFile();
        file.FileName = name;
        Assert.Throws<InvalidOperationException>(() => ExtraFileService.Validate(Definition(file)));
    }

    [Fact]
    public void ADependencyArchiveNeedsAnArchitectureFolderOfLettersAndDigits()
    {
        var none = Archive();
        none.ArchiveFolders = [];
        Assert.Throws<InvalidOperationException>(() => ExtraFileService.Validate(Definition(none)));

        var bad = Archive();
        bad.ArchiveFolders = ["..\\x64"];
        Assert.Throws<InvalidOperationException>(() => ExtraFileService.Validate(Definition(bad)));
    }

    [Fact]
    public void TwoFilesOfOneKindOrOneNameAreRefused()
    {
        Assert.Throws<InvalidOperationException>(() => ExtraFileService.Validate(Definition(LicenseFile(), LicenseFile())));
        var other = Archive();
        other.FileName = "License1.xml";
        Assert.Throws<InvalidOperationException>(() => ExtraFileService.Validate(Definition(LicenseFile(), other)));
    }

    [Fact]
    public void AFileWithoutAnAddressIsRefused()
    {
        var file = LicenseFile();
        file.DownloadUrl = "";
        Assert.Throws<InvalidOperationException>(() => ExtraFileService.Validate(Definition(file)));
    }

    [Fact]
    public void DescribeListsTheArchitectureFoldersAndTheLicenseName()
    {
        var extras = ExtraFileService.Describe(Definition(Archive(folders: ["x64", "arm64"]), LicenseFile()));

        Assert.Equal(["x64", "arm64"], extras.DependencyFolders);
        Assert.Equal("License1.xml", extras.LicenseFileName);
        Assert.False(extras.IsEmpty);
        Assert.True(ExtraFileService.Describe(new AppDefinition()).IsEmpty);
    }

    // ---- address and SHA-256 --------------------------------------------------------------------

    private const string Feed = """
        {"tag_name":"v1.29.380","assets":[
          {"name":"Other.zip","digest":"sha256:2222222222222222222222222222222222222222222222222222222222222222","browser_download_url":"https://example.test/Other.zip"},
          {"name":"e53e159d00e04f729cc2180cffd1c02e_License1.xml","uploader":{"login":"x"},"digest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","browser_download_url":"https://example.test/v1.29.380/e53e159d00e04f729cc2180cffd1c02e_License1.xml"}]}
        """;

    [Fact]
    public void TheUrlTemplateTakesTheVersionAndAQueryTakesTheAddressFromTheFeed()
    {
        Assert.Equal("https://example.test/v1.2.3/deps.zip", ExtraFileService.ResolveUrl(Archive(), null, "1.2.3"));

        var licence = LicenseFile();
        licence.DownloadUrlQuery = "\"browser_download_url\":\\s*\"(?<url>https://example.test/v{version}/[0-9a-f]+_License1\\.xml)\"";
        Assert.Equal("https://example.test/v1.29.380/e53e159d00e04f729cc2180cffd1c02e_License1.xml", ExtraFileService.ResolveUrl(licence, Feed, "1.29.380"));
        Assert.Null(ExtraFileService.ResolveUrl(licence, Feed, "9.9.9"));
    }

    [Fact]
    public void ThePinWinsOverTheFeedHashAndAFeedWithoutTheFileGivesNone()
    {
        var file = LicenseFile();
        file.HashQuery = "\"name\":\\s*\"[0-9a-f]+_License1\\.xml\"(?:[^{}]|\\{(?:[^{}]|\\{[^{}]*\\})*\\})*?\"digest\":\\s*\"sha256:(?<hash>[0-9a-fA-F]{64})\"";

        Assert.Equal(new string('1', 64), ExtraFileService.ResolveHash(file, Feed, "1.29.380"));
        file.Sha256 = new string('A', 64);
        Assert.Equal(new string('A', 64), ExtraFileService.ResolveHash(file, Feed, "1.29.380"));

        file.Sha256 = null;
        Assert.Null(ExtraFileService.ResolveHash(file, "{}", "1.29.380"));
        Assert.Null(ExtraFileService.ResolveHash(LicenseFile(), Feed, "1.29.380"));
    }

    // ---- the dependency archive ---------------------------------------------------------------

    [Fact]
    public void OnlyThePackagesOfTheNamedFoldersAreExtracted()
    {
        var zip = MakeZip("deps.zip",
            ("x64/Microsoft.VCLibs.140.00_14.0.33519.0_x64.appx", "a"),
            ("x64/readme.txt", "no"),
            ("arm64/Microsoft.VCLibs.140.00_14.0.33519.0_arm64.appx", "b"),
            ("x86/Microsoft.VCLibs.140.00_14.0.33519.0_x86.appx", "c"));
        var target = Path.Combine(_dir, "out");

        var written = ExtraFileService.ExtractDependencies(zip, ["x64", "arm64"], target);

        Assert.Equal(2, written.Count);
        Assert.True(File.Exists(Path.Combine(target, "x64", "Microsoft.VCLibs.140.00_14.0.33519.0_x64.appx")));
        Assert.True(File.Exists(Path.Combine(target, "arm64", "Microsoft.VCLibs.140.00_14.0.33519.0_arm64.appx")));
        Assert.False(Directory.Exists(Path.Combine(target, "x86")));
        Assert.False(File.Exists(Path.Combine(target, "x64", "readme.txt")));
    }

    [Fact]
    public void AFolderWithoutAPackageRefusesTheStage()
    {
        var zip = MakeZip("deps.zip", ("x64/a.appx", "a"));
        var ex = Assert.Throws<InvalidOperationException>(() => ExtraFileService.ExtractDependencies(zip, ["x64", "arm64"], Path.Combine(_dir, "out")));
        Assert.Contains("arm64", ex.Message);
    }

    [Fact]
    public void AnEntryThatWalksOutOfTheDestinationIsRefused()
    {
        var zip = MakeZip("evil.zip", ("x64/..\\..\\escape.appx", "a"));
        // The name is taken as one file name inside the folder: it holds a separator, so it is skipped and the folder is empty.
        Assert.Throws<InvalidOperationException>(() => ExtraFileService.ExtractDependencies(zip, ["x64"], Path.Combine(_dir, "out")));
        Assert.False(File.Exists(Path.Combine(_dir, "escape.appx")));
    }

    // ---- download, check, place -----------------------------------------------------------------

    private static Func<string, string, CancellationToken, Task> Serve(Dictionary<string, byte[]> bodies, List<string> asked) =>
        (url, path, _) =>
        {
            asked.Add(url);
            File.WriteAllBytes(path, bodies[url]);
            return Task.CompletedTask;
        };

    [Fact]
    public async Task TheStageDownloadsChecksAndPlacesTheLicenseAndExtractsTheDependencies()
    {
        var zipBytes = File.ReadAllBytes(MakeZip("src.zip", ("x64/dep_x64.appx", "a"), ("arm64/dep_arm64.appx", "b")));
        var license = Encoding.UTF8.GetBytes("<License/>");
        var def = Definition(Archive(Sha(zipBytes), true, "x64", "arm64"), LicenseFile(Sha(license), true));
        var asked = new List<string>();
        var recorded = new List<(string, string)>();
        var content = Path.Combine(_dir, "content");
        Directory.CreateDirectory(content);

        await ExtraFileService.StageAsync(def, content, null, "1.2.3",
            Serve(new() { ["https://example.test/v1.2.3/deps.zip"] = zipBytes, ["https://example.test/v1.2.3/abc_License1.xml"] = license }, asked),
            null, (p, h) => recorded.Add((p, h)), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(content, "Dependencies", "x64", "dep_x64.appx")));
        Assert.True(File.Exists(Path.Combine(content, "Dependencies", "arm64", "dep_arm64.appx")));
        Assert.False(File.Exists(Path.Combine(content, "deps.zip")), "the archive is not part of the content");
        Assert.True(File.Exists(Path.Combine(content, "License1.xml")));
        Assert.Equal([(Path.Combine(content, "License1.xml"), Sha(license))], recorded);
        Assert.Equal(2, asked.Count);
    }

    [Fact]
    public async Task AFileThatDoesNotMatchItsSha256RefusesTheStageAndLeavesNothing()
    {
        var license = Encoding.UTF8.GetBytes("<License/>");
        var def = Definition(LicenseFile(new string('0', 64), true));
        var content = Path.Combine(_dir, "content");
        Directory.CreateDirectory(content);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ExtraFileService.StageAsync(def, content, null, "1.2.3",
            Serve(new() { ["https://example.test/v1.2.3/abc_License1.xml"] = license }, []), null, null, CancellationToken.None));

        Assert.Contains("does not match", ex.Message);
        Assert.False(File.Exists(Path.Combine(content, "License1.xml")));
    }

    [Fact]
    public async Task AFileThatRequiresAHashAndHasNoneRefusesTheStageBeforeTheDownload()
    {
        var def = Definition(LicenseFile("", true));
        var asked = new List<string>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ExtraFileService.StageAsync(def, _dir, null, "1.2.3",
            Serve([], asked), null, null, CancellationToken.None));

        Assert.Contains("no SHA256", ex.Message);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task AFileWithoutARequiredHashIsUsedAndTheLogSaysItWasNotVerified()
    {
        var license = Encoding.UTF8.GetBytes("<License/>");
        var def = Definition(LicenseFile("", false));
        var log = new List<string>();

        await ExtraFileService.StageAsync(def, _dir, null, "1.2.3",
            Serve(new() { ["https://example.test/v1.2.3/abc_License1.xml"] = license }, []), log.Add, null, CancellationToken.None);

        Assert.Contains(log, l => l.Contains("NOT verified"));
        Assert.True(File.Exists(Path.Combine(_dir, "License1.xml")));
    }

    [Fact]
    public async Task AnAddressThatTheFeedDoesNotListRefusesTheStage()
    {
        var file = LicenseFile(new string('1', 64), true);
        file.DownloadUrlQuery = "no-such-asset(?<url>x)";

        await Assert.ThrowsAsync<InvalidOperationException>(() => ExtraFileService.StageAsync(Definition(file), _dir, Feed, "1.29.380",
            Serve([], []), null, null, CancellationToken.None));
    }

    // ---- the install wrapper --------------------------------------------------------------------

    private string _lastInstall = "";

    private string Install(MsixExtras? extras, bool provisioned = true)
    {
        var folder = Path.Combine(_dir, "w" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        new WrapperGeneratorService().GenerateMsixWrappers(folder, "app.msixbundle", "AABB", provisioned, null, null, extras);
        _lastInstall = Path.Combine(folder, "install.ps1");
        return File.ReadAllText(_lastInstall);
    }

    private static bool Parses(string path)
    {
        var script = "$t=$null;$e=$null;[void][System.Management.Automation.Language.Parser]::ParseFile('" + path.Replace("'", "''") + "',[ref]$t,[ref]$e); if ($e.Count -eq 0) { 'OK' } else { $e[0].Message }";
        var info = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim() == "OK";
    }

    [Fact]
    public void WithDependenciesAndALicenseTheOneCallTakesTheArchitecturePackagesAndTheLicenseAndNoSkipLicense()
    {
        var install = Install(new MsixExtras(["x64", "arm64"], "License1.xml"));

        Assert.Contains("Add-AppxProvisionedPackage -Online -PackagePath $msixPath -DependencyPackagePath $dependencies -LicensePath $license -ErrorAction Stop", install);
        Assert.DoesNotContain("-SkipLicense", install);
        Assert.Contains("'AMD64' { 'x64' }", install);
        Assert.Contains("'ARM64' { 'arm64' }", install);
        Assert.Contains("Join-Path $PSScriptRoot 'License1.xml'", install);
        Assert.Contains("Join-Path 'Dependencies' $dependencyFolder", install);
        Assert.True(Parses(_lastInstall), "install.ps1 does not parse");
    }

    [Fact]
    public void ADeviceWithoutDependencyPackagesForItsArchitectureStopsWithExitCodeOne()
    {
        var install = Install(new MsixExtras(["x64"], "License1.xml"));

        Assert.Contains("No dependency packages for the $processor processor architecture", install);
        Assert.Contains("No dependency packages for $dependencyFolder in the content folder", install);
        Assert.Contains("License file not found", install);
        Assert.DoesNotContain("'ARM64'", install);
    }

    [Fact]
    public void ALicenseWithoutDependenciesAndDependenciesWithoutALicenseBuildTheirOwnCall()
    {
        var licenseOnly = Install(new MsixExtras([], "License1.xml"));
        Assert.Contains("Add-AppxProvisionedPackage -Online -PackagePath $msixPath -LicensePath $license -ErrorAction Stop", licenseOnly);
        Assert.DoesNotContain("DependencyPackagePath", licenseOnly);

        var depsOnly = Install(new MsixExtras(["x64"], null));
        Assert.Contains("-DependencyPackagePath $dependencies -SkipLicense -ErrorAction Stop", depsOnly);
        Assert.DoesNotContain("-LicensePath", depsOnly);
    }

    [Fact]
    public void WithoutExtrasTheWrapperIsTheOldOneWithSkipLicense()
    {
        var install = Install(null);

        Assert.Contains("Add-AppxProvisionedPackage -Online -PackagePath $msixPath -SkipLicense -ErrorAction Stop", install);
        Assert.DoesNotContain("Dependencies", install);
    }

    [Fact]
    public void AnUnprovisionedPackageIgnoresTheExtras()
    {
        var install = Install(new MsixExtras(["x64"], "License1.xml"), provisioned: false);

        Assert.Contains("Add-AppxPackage -Path $msixPath", install);
        Assert.DoesNotContain("-LicensePath", install);
    }

    // ---- the shipped definitions ---------------------------------------------------------------

    private const string WingetRelease = """
        {"tag_name":"v1.29.380","prerelease":false,"assets":[
          {"name":"DesktopAppInstaller_Dependencies.json","digest":"sha256:3333333333333333333333333333333333333333333333333333333333333333","browser_download_url":"https://github.com/microsoft/winget-cli/releases/download/v1.29.380/DesktopAppInstaller_Dependencies.json"},
          {"name":"DesktopAppInstaller_Dependencies.zip","uploader":{"login":"x"},"digest":"sha256:4444444444444444444444444444444444444444444444444444444444444444","browser_download_url":"https://github.com/microsoft/winget-cli/releases/download/v1.29.380/DesktopAppInstaller_Dependencies.zip"},
          {"name":"e53e159d00e04f729cc2180cffd1c02e_License1.xml","uploader":{"login":"x"},"digest":"sha256:5555555555555555555555555555555555555555555555555555555555555555","browser_download_url":"https://github.com/microsoft/winget-cli/releases/download/v1.29.380/e53e159d00e04f729cc2180cffd1c02e_License1.xml"},
          {"name":"Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle","uploader":{"login":"x"},"digest":"sha256:6666666666666666666666666666666666666666666666666666666666666666","browser_download_url":"https://github.com/microsoft/winget-cli/releases/download/v1.29.380/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle"}]}
        """;

    private const string PowerShellReleases = """
        [
          {"tag_name":"v7.7.0-rc.1","prerelease":true,"assets":[{"name":"PowerShellPreview-7.7.0-rc.1.msixbundle","digest":"sha256:9999999999999999999999999999999999999999999999999999999999999999"}]},
          {"tag_name":"v7.7.0","prerelease":false,"assets":[
            {"name":"PowerShell-7.7.0-win-x64.zip","digest":"sha256:2222222222222222222222222222222222222222222222222222222222222222"},
            {"name":"PowerShell-7.7.0.msixbundle","uploader":{"login":"x"},"digest":"sha256:7777777777777777777777777777777777777777777777777777777777777777","browser_download_url":"https://github.com/PowerShell/PowerShell/releases/download/v7.7.0/PowerShell-7.7.0.msixbundle"}]},
          {"tag_name":"v7.6.10","prerelease":false,"assets":[
            {"name":"PowerShell-7.6.10.msixbundle","digest":"sha256:8888888888888888888888888888888888888888888888888888888888888888"},
            {"name":"PowerShell-LTS-7.6.10.msixbundle","digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}
        ]
        """;

    [Fact]
    public void AppInstallerTakesTheTagTheBundleDigestTheDependencyArchiveAndTheLicenseOfTheRelease()
    {
        var def = DefinitionFixture.Load("appinstaller.json");
        var config = def.VersionScraper;

        Assert.Equal("1.29.380", HttpRegexScraper.ExtractVersion(WingetRelease, config.Pattern, config.SelectHighestVersion));
        Assert.Equal(new string('6', 64), StagingService.ExtractFeedHash(WingetRelease, config, "1.29.380"), ignoreCase: true);
        Assert.True(def.RequirePayloadHash);
        Assert.True(def.RequireValidSignature);
        Assert.Equal("MSIX", def.InstallerType);
        Assert.Equal("External", def.IconSource);

        var archive = def.ExtraFiles!.Single(f => f.Kind == ExtraFileService.DependencyArchive);
        var license = def.ExtraFiles!.Single(f => f.Kind == ExtraFileService.License);
        Assert.Equal(new string('4', 64), ExtraFileService.ResolveHash(archive, WingetRelease, "1.29.380"), ignoreCase: true);
        Assert.Equal(new string('5', 64), ExtraFileService.ResolveHash(license, WingetRelease, "1.29.380"), ignoreCase: true);
        Assert.Equal("https://github.com/microsoft/winget-cli/releases/download/v1.29.380/e53e159d00e04f729cc2180cffd1c02e_License1.xml",
            ExtraFileService.ResolveUrl(license, WingetRelease, "1.29.380"));
        Assert.Equal("https://github.com/microsoft/winget-cli/releases/download/v1.29.380/DesktopAppInstaller_Dependencies.zip",
            ExtraFileService.ResolveUrl(archive, WingetRelease, "1.29.380"));
        Assert.True(archive.RequireHash && license.RequireHash);
        Assert.Equal(["x64", "arm64"], archive.ArchiveFolders);
        Assert.Equal("License1.xml", license.FileName);
        ExtraFileService.Validate(def);
    }

    [Fact]
    public void PowerShellMsixTakesTheNewestStableBundleAndNeverTheLtsOrThePreviewBundle()
    {
        var def = DefinitionFixture.Load("powershell7msix.json");
        var config = def.VersionScraper;

        Assert.Equal("7.7.0", HttpRegexScraper.ExtractVersion(PowerShellReleases, config.Pattern, config.SelectHighestVersion));
        Assert.Equal(new string('7', 64), StagingService.ExtractFeedHash(PowerShellReleases, config, "7.7.0"), ignoreCase: true);
        Assert.True(def.RequirePayloadHash);
        Assert.True(def.RequireValidSignature);
        Assert.Equal("MSIX", def.InstallerType);
        Assert.Equal("External", def.IconSource);
        Assert.Null(def.ExtraFiles);
        Assert.Contains("v{version}/PowerShell-{version}.msixbundle", def.DownloadUrl);
        Assert.Equal("PowerShell-{version}.msixbundle", def.InstallerFileName);
    }

    [Fact]
    public void ThePowerShellMsixStageNotesNameTheLimitsOfTheMsixPackage()
    {
        var notes = string.Join("\n", DefinitionFixture.Load("powershell7msix.json").StageNotes!);

        Assert.Contains("single-user installation", notes);
        Assert.Contains("does not support PowerShell remoting", notes);
        Assert.Contains("Register-PSSessionConfiguration", notes);
        Assert.Contains("Set-ExecutionPolicy -Scope LocalMachine", notes);
        Assert.Contains("Program Files\\WindowsApps", notes);
    }

    [Fact]
    public void TheAppInstallerStageNotesNameWhatTheProvisioningDoesNotDo()
    {
        var notes = string.Join("\n", DefinitionFixture.Load("appinstaller.json").StageNotes!);

        Assert.Contains("next sign-in", notes);
        Assert.Contains("logged-off profile", notes);
        Assert.Contains("feature upgrade", notes);
    }

    [Theory]
    [InlineData("appinstaller.json")]
    [InlineData("powershell7msix.json")]
    public void BothDefinitionsDetectByTheProvisionedPackageInThe64BitHost(string file)
    {
        var def = DefinitionFixture.Load(file);

        Assert.Equal("Script", def.Detection.Type);
        Assert.True(def.Detection.Is64Bit);
        Assert.Equal("No (InstallerTypeUnsupported|CustomInstall)", def.WsusSupport);
        Assert.Matches("^[0-9a-f]{16}$", def.Id);
    }
}
